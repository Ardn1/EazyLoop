namespace EazyLoop;

internal sealed class InputPitch
{
    private const int Rate = LoopEngine.Rate;
    private const int HopLength = Rate / 16;

    private sealed record Reading(double Hertz, long At);
    private readonly PitchDetector detector = new(Rate, 27, 2100, Rate / 25);
    private readonly float[] ring, window;
    private int cursor, filled, sinceAnalysis;
    private int analyzing;
    private long windowAt;
    private Reading? reading;

    public InputPitch()
    {
        ring = new float[detector.WindowLength];
        window = new float[detector.WindowLength];
    }

    public double Frequency => Volatile.Read(ref reading) is { } value && Environment.TickCount64 - value.At <= 240
        ? value.Hertz : 0;

    // Called on the capture thread. Keep the DSP on a worker and drop an update if the worker is busy.
    public void Push(ReadOnlySpan<float> samples)
    {
        while (!samples.IsEmpty)
        {
            int count = Math.Min(samples.Length, ring.Length - cursor);
            samples[..count].CopyTo(ring.AsSpan(cursor));
            cursor += count;
            if (cursor == ring.Length) cursor = 0;
            filled = Math.Min(ring.Length, filled + count);
            sinceAnalysis = Math.Min(HopLength, sinceAnalysis + count);
            samples = samples[count..];
        }
        if (filled < ring.Length || sinceAnalysis < HopLength ||
            Interlocked.CompareExchange(ref analyzing, 1, 0) != 0) return;

        ring.AsSpan(cursor).CopyTo(window);
        ring.AsSpan(0, cursor).CopyTo(window.AsSpan(ring.Length - cursor));
        sinceAnalysis = 0;
        windowAt = Environment.TickCount64;
        ThreadPool.UnsafeQueueUserWorkItem(static pitch => pitch.Analyze(), this, preferLocal: false);
    }

    private void Analyze()
    {
        try
        {
            double hz = detector.Detect(window);
            if (hz <= 0) return;
            var previous = Volatile.Read(ref reading);
            if (previous != null && windowAt - previous.At <= 150)
            {
                double change = Math.Log2(hz / previous.Hertz);
                if (Math.Abs(change) < .025) hz = previous.Hertz * Math.Pow(2, change * .55);
            }
            Volatile.Write(ref reading, new Reading(hz, windowAt));
        }
        finally { Volatile.Write(ref analyzing, 0); }
    }
}
