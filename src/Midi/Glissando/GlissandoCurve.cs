using System.Collections.Immutable;

namespace EazyLoop;

internal readonly record struct GlissandoPoint(int Position, int Cents);

internal sealed class GlissandoCurve
{
    public const int End = 1000;
    public const int PitchLimit = 2400;
    public const int MaxPoints = 128;
    public static readonly GlissandoCurve Default = new([new(0, -1200), new(420, 0), new(End, 700)], 0.65);
    public static readonly ImmutableArray<GlissandoPoint> Flat = [new(0, 0), new(End, 0)];

    public ImmutableArray<GlissandoPoint> Points { get; }
    public double Smoothing { get; }

    internal PitchContour? Contour { get; }

    public GlissandoCurve(ImmutableArray<GlissandoPoint> points, double smoothing)
    {
        Points = points;
        Smoothing = smoothing;
        if (IsValid()) Contour = new PitchContour(points, smoothing);
    }

    public bool IsValid()
    {
        if (!(Smoothing is >= 0 and <= 1) || Points.IsDefaultOrEmpty ||
            Points.Length is < 2 or > MaxPoints || Points[0].Position != 0 || Points[^1].Position != End)
            return false;
        int previous = -1;
        foreach (var point in Points)
        {
            if (point.Position <= previous || point.Cents is < -PitchLimit or > PitchLimit) return false;
            previous = point.Position;
        }
        return true;
    }

    public double CentsAt(double position, ref int segment) => Contour!.CentsAt(position, ref segment);
}

internal sealed class PitchContour
{
    public ImmutableArray<GlissandoPoint> Points { get; }
    public int Start => Points[0].Position;
    public int End => Points[^1].Position;
    private readonly double smoothing;
    private readonly double[] slopes;

    public PitchContour(ImmutableArray<GlissandoPoint> points, double smoothing)
    {
        Points = points;
        this.smoothing = smoothing;
        slopes = new double[points.Length];
        if (points.Length < 2 || smoothing == 0) return;
        double previous = Slope(points[0], points[1]);
        slopes[0] = previous;
        for (int i = 1; i < points.Length - 1; i++)
        {
            double next = Slope(points[i], points[i + 1]);
            // The harmonic mean keeps each segment monotone and places extrema at the drawn points.
            if (previous * next > 0) slopes[i] = 2 * previous * next / (previous + next);
            previous = next;
        }
        slopes[^1] = previous;
    }

    public double CentsAt(double position, ref int segment)
    {
        if (position <= Start) return Points[0].Cents;
        if (position >= End) return Points[^1].Cents;
        segment = Math.Min(segment, Points.Length - 2);
        while (segment > 0 && position < Points[segment].Position) segment--;
        while (segment < Points.Length - 2 && position > Points[segment + 1].Position) segment++;
        var a = Points[segment];
        var b = Points[segment + 1];
        int width = b.Position - a.Position;
        double t = (position - a.Position) / width;
        double linear = a.Cents + t * (b.Cents - a.Cents);
        if (smoothing == 0) return linear;

        double slope = Slope(a, b);
        double correction = width * t * (1 - t) *
            ((1 - t) * (slopes[segment] - slope) - t * (slopes[segment + 1] - slope));
        return linear + smoothing * correction;
    }

    private static double Slope(GlissandoPoint a, GlissandoPoint b) =>
        (b.Cents - a.Cents) / (double)(b.Position - a.Position);
}
