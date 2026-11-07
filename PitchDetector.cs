using System.Numerics;

namespace EazyLoop;

internal sealed class PitchDetector
{
    private readonly int rate, minHertz, maxHertz, maxLag, compareLength;
    private readonly float[] differences, normalized;
    public int WindowLength => compareLength + maxLag + 1;

    public PitchDetector(int rate, int minHertz, int maxHertz, int compareLength)
    {
        this.rate = rate;
        this.minHertz = minHertz;
        this.maxHertz = maxHertz;
        this.compareLength = compareLength;
        maxLag = (rate + minHertz - 1) / minHertz + 1;
        differences = new float[maxLag + 1];
        normalized = new float[maxLag + 1];
    }

    public double Detect(float[] window)
    {
        double sum = 0, squareSum = 0;
        foreach (float sample in window)
        {
            sum += sample;
            squareSum += sample * sample;
        }
        double variance = (squareSum - sum * sum / window.Length) / window.Length;
        if (!(variance > 0.000001)) return 0;

        // YIN's cumulative mean normalized difference (de Cheveigné and Kawahara, JASA 2002).
        float cumulative = 0;
        for (int lag = 1; lag <= maxLag; lag++)
        {
            var squared = Vector<float>.Zero;
            int i = 0;
            for (; i <= compareLength - Vector<float>.Count; i += Vector<float>.Count)
            {
                var delta = new Vector<float>(window, i) - new Vector<float>(window, i + lag);
                squared += delta * delta;
            }
            float distance = Vector.Sum(squared);
            for (; i < compareLength; i++)
            {
                float delta = window[i] - window[i + lag];
                distance += delta * delta;
            }
            differences[lag] = distance;
            cumulative += distance;
            normalized[lag] = cumulative > 0 ? distance * lag / cumulative : 1;
        }

        int best = 0;
        double score = 1;
        for (int lag = 4; lag < maxLag; lag++)
        {
            if (!IsTrough(lag)) continue;
            score = Score(lag);
            if (score >= .16) continue;
            best = lag;
            break;
        }
        if (best == 0) return 0;

        // A weak fundamental can leave a shallow dip at half the true period.
        // Prefer a multiple only when the match improves substantially.
        int candidate = best;
        for (int multiple = 2; multiple <= 3 && score > .028; multiple++)
        {
            int center = candidate * multiple;
            int radius = Math.Max(3, (int)(center * .015));
            if (center - radius >= maxLag) break;
            int better = best;
            double betterScore = score;
            for (int lag = Math.Max(candidate + 1, center - radius); lag <= Math.Min(maxLag - 1, center + radius); lag++)
            {
                if (!IsTrough(lag)) continue;
                double match = Score(lag);
                if (match >= betterScore) continue;
                betterScore = match;
                better = lag;
            }
            if (betterScore < score * .6 && score - betterScore > .028)
            {
                best = better;
                score = betterScore;
            }
        }

        // Refine at the bottom of the local trough before converting lag to hertz.
        while (best > 1 && differences[best - 1] < differences[best]) best--;
        while (best < maxLag - 1 && differences[best + 1] < differences[best]) best++;
        double frequency = rate / (best + Refine(differences, best));
        return frequency >= minHertz && frequency <= maxHertz ? frequency : 0;
    }

    private bool IsTrough(int lag) => normalized[lag] <= normalized[lag - 1] && normalized[lag] <= normalized[lag + 1];

    private double Score(int lag) => Math.Max(0, normalized[lag] -
        (normalized[lag - 1] - normalized[lag + 1]) * Refine(normalized, lag) / 4);

    private static double Refine(float[] values, int lag)
    {
        double left = values[lag - 1], mid = values[lag], right = values[lag + 1];
        double curvature = left + right - 2 * mid;
        return curvature > 0 ? Math.Clamp(.5 * (left - right) / curvature, -.5, .5) : 0;
    }
}
