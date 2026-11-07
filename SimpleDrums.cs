namespace EazyLoop;

internal enum DrumSound
{
    DeepKick, PunchKick, TightSnare, BrushSnare, ClosedHat, OpenHat, Clap, LowTom, HighTom, Rim, Ride,
    AcousticKick, FrameDrum, Shaker, Tambourine, Timpani, ConcertSnare
}

internal sealed record DrumPad(DrumSound Sound, float Tune, float Decay, float Tone, float Level, float Pan)
{
    public bool IsValid() => Enum.IsDefined(Sound) && Tune is >= -24 and <= 24 && Decay is >= 20 and <= 1800 &&
        Tone is >= 0 and <= 100 && Level is >= 0 and <= 100 && Pan is >= -100 and <= 100;
}

internal sealed record DrumKit(int Count, DrumPad[] Pads)
{
    public static readonly int[] Keys = [36, 38, 42, 46, 39, 45, 50, 37];
    public static readonly string[] Colors = ["#F1B4C7", "#F4C6A4", "#F1DB9E", "#BDE0AE", "#A6E5D9", "#ACCEF6", "#C6BBF9", "#E3B6ED"];

    public static readonly (DrumSound Sound, string Name, int Decay, int Tone, int Level)[] Sounds =
    [
        (DrumSound.DeepKick, "Глубокая бочка", 430, 24, 83),
        (DrumSound.PunchKick, "Плотная бочка", 240, 60, 85),
        (DrumSound.TightSnare, "Плотный малый", 165, 65, 68),
        (DrumSound.BrushSnare, "Мягкий малый", 260, 36, 64),
        (DrumSound.ClosedHat, "Закрытый хэт", 65, 56, 42),
        (DrumSound.OpenHat, "Открытый хэт", 360, 60, 39),
        (DrumSound.Clap, "Хлопок", 200, 55, 60),
        (DrumSound.LowTom, "Низкий том", 340, 42, 70),
        (DrumSound.HighTom, "Высокий том", 255, 51, 64),
        (DrumSound.Rim, "Удар по ободу", 95, 56, 56),
        (DrumSound.Ride, "Тарелка", 580, 67, 42),
        (DrumSound.AcousticKick, "Акустическая бочка", 380, 42, 78),
        (DrumSound.FrameDrum, "Рамочный барабан", 380, 39, 68),
        (DrumSound.Shaker, "Шейкер", 145, 53, 45),
        (DrumSound.Tambourine, "Тамбурин", 450, 57, 52),
        (DrumSound.Timpani, "Литавра", 910, 40, 74),
        (DrumSound.ConcertSnare, "Концертный малый", 295, 47, 69)
    ];

    public static DrumKit Default => new(8,
        [Preset(DrumSound.DeepKick), Preset(DrumSound.TightSnare), Preset(DrumSound.ClosedHat),
         Preset(DrumSound.OpenHat), Preset(DrumSound.Clap), Preset(DrumSound.LowTom),
         Preset(DrumSound.HighTom), Preset(DrumSound.Rim)]);

    public static DrumPad Preset(DrumSound sound)
    {
        var preset = Sounds[(int)sound];
        return new DrumPad(sound, 0, preset.Decay, preset.Tone, preset.Level, 0);
    }

    public bool IsValid() => Count is 1 or 3 or 5 or 8 && Pads is { Length: 8 } && Pads.All(p => p?.IsValid() == true);
    public string PadName(int index) => Locale.T(Sounds[(int)Pads[index].Sound].Name);
}

internal sealed class SimpleDrums(DrumKit kit)
{
    private const int Rate = LoopEngine.Rate;
    private struct Voice
    {
        public DrumSound Sound;
        public int Remaining;
        public int Age;
        public double Phase;
        public double Step;
        public double Sweep;
        public float Envelope;
        public float Falloff;
        public float Filter;
        public float Tone;
        public float FilterRate;
        public float Left;
        public float Right;
        public uint Noise;
    }

    private readonly Voice[] voices = new Voice[DrumKit.Keys.Length];
    private DrumKit kit = kit;
    private uint hitSeed = 0xa2719b3f;
    public DrumKit Kit { get => Volatile.Read(ref kit); set => Volatile.Write(ref kit, value); }
    public void Reset() => Array.Clear(voices);

    public void Hit(int key, int velocity)
    {
        int slot = Array.IndexOf(DrumKit.Keys, key);
        var snapshot = Kit;
        if (slot < 0 || slot >= snapshot.Count) return;
        var pad = snapshot.Pads[slot];
        if (pad.Sound == DrumSound.ClosedHat)
        {
            for (int i = 0; i < voices.Length; i++)
                if (voices[i].Sound == DrumSound.OpenHat)
                {
                    voices[i].Falloff = Math.Min(.97f, voices[i].Falloff);
                    voices[i].Remaining = Math.Min(160, voices[i].Remaining);
                }
        }

        double frequency = pad.Sound switch
        {
            DrumSound.DeepKick => 48, DrumSound.PunchKick => 65, DrumSound.TightSnare => 183,
            DrumSound.BrushSnare => 145, DrumSound.LowTom => 93, DrumSound.HighTom => 146,
            DrumSound.Rim => 535, DrumSound.AcousticKick => 61, DrumSound.FrameDrum => 111,
            DrumSound.Shaker => 74, DrumSound.Tambourine => 1180, DrumSound.Timpani => 93,
            DrumSound.ConcertSnare => 203, _ => 315
        };
        double sweep = pad.Sound switch
        {
            DrumSound.DeepKick => 4.5, DrumSound.PunchKick => 3.8,
            DrumSound.LowTom or DrumSound.HighTom => .75, DrumSound.AcousticKick => .35,
            DrumSound.FrameDrum => .15, DrumSound.Timpani => .07, _ => 0
        };
        float tone = pad.Tone / 100;
        float level = pad.Level / 100 * (velocity / 127f) * .65f;
        float pan = pad.Pan / 100;
        int samples = (int)(pad.Decay * Rate / 1000);
        hitSeed = unchecked(hitSeed * 1664525u + 1013904223u);
        voices[slot] = new Voice
        {
            Sound = pad.Sound, Remaining = samples, Step = frequency * Math.Pow(2, pad.Tune / 12) / Rate,
            Sweep = sweep, Envelope = 1, Falloff = MathF.Exp(-6.9f / samples), Tone = tone,
            FilterRate = .04f + tone * .35f, Left = level * (1 - Math.Max(0, pan)),
            Right = level * (1 + Math.Min(0, pan)), Noise = hitSeed | 1
        };
    }

    public void Render(Span<float> output)
    {
        output.Clear();
        int count = Kit.Count;
        for (int lane = 0; lane < count; lane++)
        {
            ref var voice = ref voices[lane];
            int frames = Math.Min(output.Length / 2, voice.Remaining);
            for (int i = 0; i < frames; i++)
            {
                float value = Sample(ref voice);
                output[2 * i] += value * voice.Left;
                output[2 * i + 1] += value * voice.Right;
            }
            voice.Remaining -= frames;
        }
        // Hidden pads resume their pattern at the next hit, with no leftover tail.
        for (int lane = count; lane < voices.Length; lane++) voices[lane].Remaining = 0;
    }

    private static float Sample(ref Voice voice)
    {
        uint noise = voice.Noise;
        noise ^= noise << 13; noise ^= noise >> 17; noise ^= noise << 5;
        voice.Noise = noise;
        float white = (noise >> 8) * (2f / 16777216) - 1;
        voice.Filter += voice.FilterRate * (white - voice.Filter);
        float high = white - voice.Filter;
        double angle = 2 * Math.PI * voice.Phase;
        float body = (float)Math.Sin(angle);
        float transient = Math.Max(0, 1 - voice.Age / 155f);
        double baseAngle = 2 * Math.PI * voice.Step * voice.Age;
        float sample = voice.Sound switch
        {
            DrumSound.DeepKick or DrumSound.PunchKick => body * .86f + high * transient * voice.Tone * .40f,
            DrumSound.TightSnare => .73f * high + .27f * body,
            DrumSound.BrushSnare => .66f * voice.Filter + .23f * high + .16f * body,
            DrumSound.ClosedHat or DrumSound.OpenHat => high * (.67f + .25f * (float)Math.Sin(angle * 13)),
            DrumSound.Ride => .40f * high + .30f * (float)Math.Sin(angle * 11) + .20f * (float)Math.Sin(angle * 17),
            DrumSound.Clap => high * (voice.Age < 1700 ? .35f + .58f * (1 - voice.Age % 550 / 550f) : .42f),
            DrumSound.Rim => .62f * body + .27f * (float)Math.Sin(angle * 3) + .22f * high * transient,
            DrumSound.AcousticKick => .73f * body + .15f * (float)Math.Sin(baseAngle * 1.68) +
                transient * (.25f * voice.Filter + .08f * high),
            DrumSound.FrameDrum => .64f * body + .21f * (float)Math.Sin(baseAngle * 1.59) +
                .12f * (float)Math.Sin(baseAngle * 2.14) + .18f * high * transient,
            DrumSound.Shaker => high * Math.Min(1, voice.Age / 290f) * (.56f + .30f * body),
            DrumSound.Tambourine => .54f * high + .16f * body + .16f * (float)Math.Sin(baseAngle * 1.71) +
                .12f * (float)Math.Sin(baseAngle * 2.63),
            DrumSound.Timpani => .61f * body + .22f * (float)Math.Sin(baseAngle * 1.50) +
                .11f * (float)Math.Sin(baseAngle * 2.00) + .10f * (float)Math.Sin(baseAngle * 2.44) +
                .17f * voice.Filter * transient,
            DrumSound.ConcertSnare => .43f * high + .28f * voice.Filter + .17f * body +
                .10f * (float)Math.Sin(baseAngle * 1.47),
            _ => .82f * body + .12f * (float)Math.Sin(angle * 2) + .14f * high * transient
        };
        voice.Phase += voice.Step * (1 + voice.Sweep);
        if (voice.Phase >= 1) voice.Phase -= Math.Floor(voice.Phase);
        voice.Sweep *= .9987;
        voice.Age++;
        float value = sample * voice.Envelope;
        voice.Envelope *= voice.Falloff;
        return value;
    }
}
