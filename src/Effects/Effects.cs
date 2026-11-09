using System.Text.Json.Serialization;

namespace EazyLoop;

internal enum FxKind { Gate, Compressor, Drive, Chorus, Tremolo, Delay, Reverb, Limiter, Vst3, PitchCorrection, NoiseReduction }

internal sealed record EqBand(float Hz, float Db);
internal sealed record VstReference(string Path, string ClassId, string Name, byte[]? State = null);
internal sealed record FxSlotSettings(FxKind Kind, bool Enabled, float[] Values, VstReference? Plugin = null);
internal sealed record TrackFxSettings(bool EqEnabled, EqBand[] Bands, FxSlotSettings[] Slots,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Bypassed = false);

internal sealed record FxParameter(string Name, float Min, float Max, float Default, string Unit);

internal static class FxCatalog
{
    public const int MaxSlots = 8;
    public const int MaxStateBytes = 1024 * 1024;
    public static readonly EqBand[] Flat = [new(100, 0), new(350, 0), new(1800, 0), new(8000, 0)];
    public static readonly (int Min, int Max)[] BandRange = [(25, 400), (100, 1600), (450, 8000), (1800, 18000)];

    public static readonly IReadOnlyDictionary<FxKind, (string Name, FxParameter[] Parameters)> BuiltIn =
        new Dictionary<FxKind, (string, FxParameter[])>
        {
            [FxKind.PitchCorrection] = ("Автотюн", [new("Тональность", 0, DiatonicScale.Count, 0, ""),
                new("Сила коррекции", 0, 100, 100, "%"), new("Скорость коррекции", 5, 150, 18, "мс")]),
            [FxKind.NoiseReduction] = ("ИИ-шумодав", [new("Сила обработки", 0, 100, 40, "%")]),
            [FxKind.Gate] = ("Гейт", [new("Порог", -80, -15, -46, "дБ"), new("Спад", 20, 450, 110, "мс")]),
            [FxKind.Compressor] = ("Компрессор", [new("Порог", -55, 0, -20, "дБ"), new("Степень", 1, 12, 3, ":1"), new("Выход", 0, 18, 3, "дБ")]),
            [FxKind.Drive] = ("Драйв", [new("Усиление", 0, 36, 15, "дБ"), new("Доля", 0, 100, 55, "%")]),
            [FxKind.Chorus] = ("Хорус", [new("Скорость", 0.1f, 5, 0.8f, "Гц"), new("Глубина", 1, 11, 4, "мс"), new("Доля", 0, 100, 32, "%")]),
            [FxKind.Tremolo] = ("Тремоло", [new("Скорость", 0.1f, 16, 4, "Гц"), new("Глубина", 0, 100, 48, "%")]),
            [FxKind.Delay] = ("Дилей", [new("Время", 25, 1500, 310, "мс"), new("Повторы", 0, 85, 32, "%"), new("Доля", 0, 100, 24, "%")]),
            [FxKind.Reverb] = ("Реверберация", [new("Размер", 0, 100, 48, "%"), new("Доля", 0, 100, 25, "%")]),
            [FxKind.Limiter] = ("Лимитер", [new("Потолок", -24, 0, -2, "дБ")])
        };

    public static FxSlotSettings Default(FxKind kind) =>
        new(kind, true, BuiltIn[kind].Parameters.Select(parameter => parameter.Default).ToArray());

    public static bool IsValid(TrackFxSettings fx)
    {
        if (fx.Bands == null || fx.Bands.Length != 4 || fx.Slots == null || fx.Slots.Length > MaxSlots) return false;
        for (int i = 0; i < fx.Bands.Length; i++)
        {
            var band = fx.Bands[i];
            var limits = BandRange[i];
            if (band == null || !float.IsFinite(band.Hz) || band.Hz < limits.Min || band.Hz > limits.Max ||
                !float.IsFinite(band.Db) || band.Db is < -15 or > 15) return false;
        }
        foreach (var slot in fx.Slots)
        {
            if (slot == null || slot.Values == null) return false;
            if (slot.Kind == FxKind.Vst3)
            {
                var vst = slot.Plugin;
                if (vst == null || slot.Values.Length != 0 || !IsValidPlugin(vst)) return false;
                continue;
            }
            if (!BuiltIn.TryGetValue(slot.Kind, out var info) || slot.Values.Length != info.Parameters.Length) return false;
            if (slot.Kind == FxKind.PitchCorrection && slot.Values[0] != MathF.Truncate(slot.Values[0])) return false;
            for (int i = 0; i < slot.Values.Length; i++)
            {
                float value = slot.Values[i];
                if (!float.IsFinite(value) || value < info.Parameters[i].Min || value > info.Parameters[i].Max) return false;
            }
        }
        return true;
    }

    public static bool IsValidPlugin(VstReference vst) =>
        !string.IsNullOrWhiteSpace(vst.Name) && !string.IsNullOrWhiteSpace(vst.Path) &&
        vst.ClassId?.Length == 32 && vst.ClassId.All(Uri.IsHexDigit) &&
        vst.Path.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase) && !(vst.State?.Length > MaxStateBytes);
}

internal readonly record struct EqFilter(double B0, double B1, double B2, double A1, double A2)
{
    public static EqFilter Create(int band, EqBand value)
    {
        if (value.Db == 0) return new(1, 0, 0, 0, 0);
        // RBJ Audio EQ Cookbook: unity-Q peaks and shelves with slope S=1.
        double w = 2 * Math.PI * value.Hz / LoopEngine.Rate;
        double c = Math.Cos(w), s = Math.Sin(w), a = Math.Pow(10, value.Db / 40.0);
        double alpha = s / 2, k = s * Math.Sqrt(2 * a);
        double plus = a + 1, minus = a - 1;
        if (band == 0)
            return Normalize(a * (plus - minus * c + k), 2 * a * (minus - plus * c),
                a * (plus - minus * c - k), plus + minus * c + k, -2 * (minus + plus * c), plus + minus * c - k);
        if (band == 3)
            return Normalize(a * (plus + minus * c + k), -2 * a * (minus + plus * c),
                a * (plus + minus * c - k), plus - minus * c + k, 2 * (minus - plus * c), plus - minus * c - k);
        return Normalize(1 + alpha * a, -2 * c, 1 - alpha * a, 1 + alpha / a, -2 * c, 1 - alpha / a);
    }

    private static EqFilter Normalize(double b0, double b1, double b2, double a0, double a1, double a2) =>
        new(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);

    public double ResponseDb(double hz)
    {
        double w = 2 * Math.PI * hz / LoopEngine.Rate;
        double c = Math.Cos(w), s = Math.Sin(w), c2 = Math.Cos(2 * w), s2 = Math.Sin(2 * w);
        double nr = B0 + B1 * c + B2 * c2, ni = B1 * s + B2 * s2;
        double dr = 1 + A1 * c + A2 * c2, di = A1 * s + A2 * s2;
        return 10 * Math.Log10((nr * nr + ni * ni) / (dr * dr + di * di));
    }

    public float Tick(float value, ref double z1, ref double z2)
    {
        double output = B0 * value + z1;
        z1 = B1 * value - A1 * output + z2;
        z2 = B2 * value - A2 * output;
        return (float)output;
    }
}

internal sealed class BuiltInFx
{
    private const float Rate = LoopEngine.Rate;
    private readonly FxKind kind;
    private readonly float[] delay;
    private readonly float[][] room;
    private readonly PitchCorrectionFx? pitch;
    private readonly NoiseReductionFx? noiseReduction;
    private readonly int[] positions = new int[4];
    private int position;
    private double phase;
    private float envelope;
    private float gain = 1;

    public int Latency => pitch != null ? PitchCorrectionFx.Latency : noiseReduction != null ? NoiseReductionFx.Latency : 0;

    public BuiltInFx(FxKind kind)
    {
        this.kind = kind;
        delay = kind switch { FxKind.Delay => new float[144_008], FxKind.Chorus => new float[3072], _ => [] };
        room = kind == FxKind.Reverb ? [new float[1429], new float[1777], new float[2087], new float[2591]] : [];
        if (kind == FxKind.PitchCorrection) pitch = new PitchCorrectionFx();
        if (kind == FxKind.NoiseReduction) noiseReduction = new NoiseReductionFx();
    }

    public void Reset()
    {
        Array.Clear(delay);
        foreach (var line in room) Array.Clear(line);
        Array.Clear(positions);
        position = 0;
        phase = envelope = 0;
        gain = 1;
        pitch?.Reset();
        noiseReduction?.Reset();
    }

    public void Process(Span<float> samples, float[] p)
    {
        switch (kind)
        {
            case FxKind.PitchCorrection: pitch!.Process(samples, p); break;
            case FxKind.NoiseReduction: noiseReduction!.Process(samples, p[0]); break;
            case FxKind.Gate:
            case FxKind.Compressor:
            case FxKind.Limiter: Dynamics(samples, p); break;
            case FxKind.Drive:
                float drive = Linear(p[0]);
                float mix = p[1] * 0.01f;
                for (int i = 0; i < samples.Length; i++)
                {
                    float wet = samples[i] * drive;
                    samples[i] += mix * (wet / (1 + Math.Abs(wet)) - samples[i]);
                }
                break;
            case FxKind.Delay:
            case FxKind.Chorus: Delay(samples, p); break;
            case FxKind.Tremolo:
                for (int i = 0; i < samples.Length; i += 2)
                {
                    float amp = 1 - p[1] * 0.005f * (1 - (float)Math.Cos(phase));
                    samples[i] *= amp;
                    samples[i + 1] *= amp;
                    Step(p[0]);
                }
                break;
            case FxKind.Reverb: Reverb(samples, p); break;
        }
    }

    private void Dynamics(Span<float> samples, float[] p)
    {
        float threshold = Linear(p[0]);
        float makeup = kind == FxKind.Compressor ? Linear(p[2]) : 1;
        float release = Smooth(kind == FxKind.Gate ? p[1] : 90);
        float attack = Smooth(3);
        for (int i = 0; i < samples.Length; i += 2)
        {
            float peak = Math.Max(Math.Abs(samples[i]), Math.Abs(samples[i + 1]));
            envelope = peak + (envelope - peak) * (peak > envelope ? attack : release);
            float target = kind switch
            {
                FxKind.Gate => envelope >= threshold ? 1 : 0,
                FxKind.Limiter => peak > threshold ? threshold / peak : 1,
                _ => envelope > threshold ? MathF.Pow(threshold / envelope, 1 - 1 / p[1]) : 1
            };
            float speed = kind == FxKind.Gate ? (target > gain ? attack : release)
                : target < gain ? (kind == FxKind.Limiter ? 0 : attack) : release;
            gain = target + (gain - target) * speed;
            samples[i] *= gain * makeup;
            samples[i + 1] *= gain * makeup;
        }
    }

    private void Delay(Span<float> samples, float[] p)
    {
        bool echo = kind == FxKind.Delay;
        int size = delay.Length / 2;
        float mix = p[2] * 0.01f, feedback = echo ? p[1] * 0.01f : 0;
        for (int i = 0; i < samples.Length; i += 2)
        {
            for (int c = 0; c < 2; c++)
            {
                double time = echo ? p[0] : 13 + p[1] * Math.Sin(phase + c * Math.PI / 2);
                double read = position - time * (Rate / 1000);
                if (read < 0) read += size;
                int a = (int)read, b = (a + 1) % size;
                float wet = delay[2 * a + c] + (float)(read - a) * (delay[2 * b + c] - delay[2 * a + c]);
                float dry = samples[i + c];
                delay[2 * position + c] = dry + wet * feedback;
                samples[i + c] = dry + mix * (wet - dry);
            }
            if (++position == size) position = 0;
            if (!echo) Step(p[0]);
        }
    }

    private void Reverb(Span<float> samples, float[] p)
    {
        float feedback = 0.30f + p[0] * 0.0063f, mix = p[1] * 0.01f;
        for (int i = 0; i < samples.Length; i += 2)
        {
            float a = room[0][positions[0]], b = room[1][positions[1]];
            float c = room[2][positions[2]], d = room[3][positions[3]];
            float left = samples[i], right = samples[i + 1];
            Write(0, left * 0.28f + feedback * (a + b + c + d) / 2);
            Write(1, right * 0.28f + feedback * (a - b + c - d) / 2);
            Write(2, left * 0.28f + feedback * (a + b - c - d) / 2);
            Write(3, right * 0.28f + feedback * (a - b - c + d) / 2);
            samples[i] = left + mix * ((a + c) - left);
            samples[i + 1] = right + mix * ((b + d) - right);
        }
    }

    private void Write(int line, float value)
    {
        room[line][positions[line]] = value;
        if (++positions[line] == room[line].Length) positions[line] = 0;
    }

    private void Step(float hz)
    {
        phase += 2 * Math.PI * hz / Rate;
        if (phase >= 2 * Math.PI) phase -= 2 * Math.PI;
    }

    private static float Linear(float db) => MathF.Pow(10, db / 20);
    private static float Smooth(float ms) => MathF.Exp(-1000 / (Rate * ms));
}
