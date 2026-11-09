namespace EazyLoop;

internal sealed class SimpleSynth(SynthPatch patch, GlissandoCurve? curve = null)
{
    private const float Rate = LoopEngine.Rate;
    private struct Voice
    {
        public int Key;
        public Guid NoteId;
        public PitchContour? Path;
        public double Phase;
        public double Step;
        public int Position, LastFrame, Segment;
        public float Envelope;
        public float Strength;
        public bool Down;
        public bool Releasing;
        public bool Decaying;
        public float Filter;
        public float ReleaseStep;
    }

    private readonly Voice[] voices = new Voice[24];
    private SynthPatch patch = patch;
    private GlissandoCurve? curve = curve;
    private bool sustain;
    private float modulation;
    private double bend = 1;
    private double lfo;

    public SynthPatch Patch { get => Volatile.Read(ref patch); set => Volatile.Write(ref patch, value); }
    public GlissandoCurve? Curve { get => Volatile.Read(ref curve); set => Volatile.Write(ref curve, value); }
    public bool HasVoices => Array.Exists(voices, static voice => voice.Strength > 0);

    public void On(int key, int velocity, int duration = 1, int elapsed = 0, Guid noteId = default, PitchContour? path = null)
    {
        int target = 0;
        for (int i = 0; i < voices.Length; i++)
        {
            if (voices[i].Strength == 0) { target = i; break; }
            if (voices[i].Envelope < voices[target].Envelope) target = i;
        }
        voices[target] = new Voice
        {
            Key = key, NoteId = noteId, Step = 440 * Math.Pow(2, (key - 69) / 12.0) / Rate, Down = true,
            Position = elapsed, LastFrame = Math.Max(1, duration - 1),
            Path = path,
            Strength = (velocity / 127f) * 0.17f
        };
    }

    public void Off(int key, Guid noteId = default)
    {
        for (int i = 0; i < voices.Length; i++)
        {
            ref var voice = ref voices[i];
            if (!voice.Down || voice.Key != key || voice.NoteId != noteId) continue;
            voice.Down = false;
            if (!sustain) Release(ref voice);
        }
    }

    public void Control(MidiLane lane, int value)
    {
        if (lane == MidiLane.Modulation) modulation = value / 127f;
        if (lane == MidiLane.Pitch) bend = Math.Pow(2, (value - 8192) * 2.0 / (8192 * 12));
        if (lane != MidiLane.Sustain) return;
        sustain = value >= 64;
        if (!sustain)
            for (int i = 0; i < voices.Length; i++)
                if (voices[i].Strength > 0 && !voices[i].Down) Release(ref voices[i]);
    }

    public void Reset()
    {
        // A short fade suppresses clicks if a loop wraps mid-note.
        for (int i = 0; i < voices.Length; i++)
        {
            ref var voice = ref voices[i];
            voice.Down = false;
            if (voice.Strength == 0) continue;
            voice.Releasing = true;
            voice.ReleaseStep = voice.Envelope / 96;
        }
        sustain = false;
        modulation = 0;
        bend = 1;
    }

    private void Release(ref Voice voice)
    {
        if (voice.Releasing) return;
        voice.Releasing = true;
        voice.ReleaseStep = voice.Envelope * 1000 / (Patch.Release * Rate);
    }

    public void MixInto(Span<float> stereo)
    {
        var shape = Patch;
        WaveShape nextWave = shape.NextWave;
        var trajectory = Curve?.Contour;
        float attack = 1000 / (shape.Attack * Rate);
        float hold = shape.Sustain / 100f;
        float decay = (1 - hold) * 1000 / (shape.Decay * Rate);
        float lowpass = LowpassCoefficient(shape.Tone);
        for (int j = 0; j < stereo.Length; j += 2)
        {
            double pitch = bend * (1 + 0.012 * modulation * Math.Sin(lfo));
            float mixed = 0;
            for (int i = 0; i < voices.Length; i++)
            {
                ref var voice = ref voices[i];
                if (voice.Strength == 0) continue;
                if (voice.Releasing)
                {
                    voice.Envelope -= voice.ReleaseStep;
                    if (voice.Envelope <= 0) { voice = default; continue; }
                }
                else if (voice.Decaying) voice.Envelope = Math.Max(hold, voice.Envelope - decay);
                else if ((voice.Envelope += attack) >= 1)
                {
                    voice.Envelope = 1;
                    voice.Decaying = true;
                }
                double step = voice.Step * pitch;
                float sample;
                var path = voice.Path ?? trajectory;
                if (path == null)
                {
                    step = Math.Min(0.45, step);
                    sample = Oscillator(shape.Wave, nextWave, shape.Blend, voice.Phase, step);
                }
                else
                {
                    double position = path.Start + (double)voice.Position * (path.End - path.Start) / voice.LastFrame;
                    double cents = path.CentsAt(position, ref voice.Segment);
                    if (voice.Path != null) cents -= 6900;
                    if (voice.Down && voice.Position < voice.LastFrame) voice.Position++;
                    step *= Math.Pow(2, cents / 1200);
                    // Keep the commanded pitch. Fade out frequencies near the sampling limit.
                    sample = step < .5 ? Oscillator(shape.Wave, nextWave, shape.Blend, voice.Phase, step) *
                        (float)Math.Min(1, (.5 - step) / .05) : 0;
                }
                voice.Filter += lowpass * (sample - voice.Filter);
                mixed += voice.Filter * voice.Envelope * voice.Strength;
                voice.Phase += step;
                if (voice.Phase >= 1) voice.Phase -= Math.Floor(voice.Phase);
            }
            stereo[j] += mixed;
            stereo[j + 1] += mixed;
            lfo += 2 * Math.PI * 5.2 / Rate;
            if (lfo > 2 * Math.PI) lfo -= 2 * Math.PI;
        }
    }

    internal static float LowpassCoefficient(float tone)
    {
        float cutoff = 350 * MathF.Pow(34, tone / 100f);
        return 1 - MathF.Exp(-2 * MathF.PI * cutoff / Rate);
    }

    internal static float Oscillator(WaveShape wave, double phase, double step) => (float)(wave switch
    {
        WaveShape.Sine => Math.Sin(2 * Math.PI * phase),
        WaveShape.Triangle => 1 - 4 * Math.Abs(phase - 0.5),
        WaveShape.Saw => 2 * phase - 1 - Bleed(phase, step),
        WaveShape.Square => (phase < 0.5 ? 1 : -1) + Bleed(phase, step) - Bleed((phase + 0.5) % 1, step),
        _ => 0
    });

    internal static float Oscillator(WaveShape wave, WaveShape next, float blend, double phase, double step)
    {
        float sample = Oscillator(wave, phase, step);
        return blend == 0 ? sample : sample + (Oscillator(next, phase, step) - sample) * blend;
    }

    private static double Bleed(double phase, double step)
    {
        if (phase < step) { double x = phase / step - 1; return -x * x; }
        if (phase > 1 - step) { double x = (phase - 1) / step + 1; return x * x; }
        return 0;
    }
}
