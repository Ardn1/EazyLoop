namespace EazyLoop;

internal sealed class PitchCorrectionFx
{
    public const int Latency = 1440;
    private const int Rate = LoopEngine.Rate;
    private const int Decimation = 4;
    private const int AnalysisRate = Rate / Decimation;
    private const int AnalysisHop = AnalysisRate * 16 / 1000;
    private const int RingLength = 4096;
    private const int RingMask = RingLength - 1;
    private const double PeriodSmoothing = .002;
    private const double BlendSmoothing = .005;

    private readonly PitchDetector detector = new(AnalysisRate, 45, 1600, AnalysisRate / 60);
    private readonly float[] stereoRing = new float[2 * RingLength];
    private readonly float[] analysisRing, analysisWindow;
    private int cursor, analysisCursor, analysisCount, hop, decimator;
    private float filtered, smoothed;
    private double phase = .5, period = 200, targetPeriod = 200;
    private double ratio = 1, targetRatio = 1, wet, targetWet;

    public PitchCorrectionFx()
    {
        analysisRing = new float[detector.WindowLength];
        analysisWindow = new float[detector.WindowLength];
    }

    public void Reset()
    {
        Array.Clear(stereoRing);
        cursor = analysisCursor = analysisCount = hop = decimator = 0;
        filtered = smoothed = 0;
        phase = .5;
        period = targetPeriod = 200;
        ratio = targetRatio = 1;
        wet = targetWet = 0;
    }

    public void Process(Span<float> stereo, float[] parameters)
    {
        int selection = (int)parameters[0];
        DiatonicScale? scale = selection == 0 ? null : DiatonicScale.FromIndex(selection - 1);
        double strength = parameters[1] / 100.0;
        double speed = 1 - Math.Exp(-1000 / (Rate * (double)parameters[2]));
        if (strength == 0)
        {
            targetRatio = 1;
            targetWet = 0;
        }

        for (int i = 0; i < stereo.Length; i += 2)
        {
            float left = stereo[i], right = stereo[i + 1];
            stereoRing[2 * cursor] = left;
            stereoRing[2 * cursor + 1] = right;
            float mono = (left + right) * .5f;
            filtered += .36f * (mono - filtered);
            smoothed += .36f * (filtered - smoothed);
            if (++decimator == Decimation)
            {
                decimator = 0;
                PushAnalysis(smoothed, scale, strength);
            }

            int dry = 2 * ((cursor - Latency) & RingMask);
            ratio += (targetRatio - ratio) * speed;
            wet += (targetWet - wet) * BlendSmoothing;
            if (wet > .0001)
            {
                period += (targetPeriod - period) * PeriodSmoothing;
                // The two taps are one input period apart. Their crossfade hides the jump
                // when either moving tap wraps while keeping the waveform in phase.
                phase += (1 - ratio) / (2 * period);
                if (phase < 0) phase++;
                else if (phase >= 1) phase--;
                double first = Latency + (2 * phase - 1) * period;
                double second = phase < .5 ? first + period : first - period;
                double weight = 1 - Math.Abs(2 * phase - 1);
                weight *= weight * (3 - 2 * weight);
                stereo[i] = Blend(dry, 0, first, second, weight);
                stereo[i + 1] = Blend(dry, 1, first, second, weight);
            }
            else
            {
                stereo[i] = stereoRing[dry];
                stereo[i + 1] = stereoRing[dry + 1];
            }
            cursor = (cursor + 1) & RingMask;
        }
    }

    private void PushAnalysis(float sample, DiatonicScale? scale, double strength)
    {
        analysisRing[analysisCursor++] = sample;
        if (analysisCursor == analysisRing.Length) analysisCursor = 0;
        if (analysisCount < analysisRing.Length) analysisCount++;
        if (hop < AnalysisHop) hop++;
        if (strength == 0 || analysisCount < analysisRing.Length || hop < AnalysisHop) return;
        hop = 0;
        analysisRing.AsSpan(analysisCursor).CopyTo(analysisWindow);
        analysisRing.AsSpan(0, analysisCursor).CopyTo(analysisWindow.AsSpan(analysisRing.Length - analysisCursor));

        double frequency = detector.Detect(analysisWindow);
        if (frequency <= 0)
        {
            targetRatio = 1;
            targetWet = 0;
            return;
        }

        double note = 69 + 12 * Math.Log2(frequency / 440);
        double correction = (NearestNote(note, scale) - note) * strength;
        targetPeriod = Rate / frequency;
        if (wet <= .0001)
        {
            period = targetPeriod;
            phase = .5;
        }
        targetRatio = Math.Pow(2, correction / 12);
        targetWet = Math.Clamp(Math.Abs(correction) * 50, 0, 1);
    }

    internal static int NearestNote(double pitch, DiatonicScale? scale)
    {
        int lower = (int)Math.Floor(pitch), upper = lower + 1;
        if (scale is { } key)
        {
            while (!key.Contains(lower)) lower--;
            while (!key.Contains(upper)) upper++;
        }
        return pitch - lower <= upper - pitch ? lower : upper;
    }

    private float Blend(int dry, int channel, double first, double second, double weight)
    {
        float original = stereoRing[dry + channel];
        double shifted = Read(first, channel) * weight + Read(second, channel) * (1 - weight);
        return original + (float)(wet * (shifted - original));
    }

    private float Read(double delay, int channel)
    {
        double read = cursor - delay;
        int index = (int)Math.Floor(read);
        float a = stereoRing[2 * (index & RingMask) + channel];
        float b = stereoRing[2 * ((index + 1) & RingMask) + channel];
        return a + (float)(read - index) * (b - a);
    }
}
