using System.Collections.Immutable;

namespace EazyLoop;

internal sealed class GlissandoTimeline
{
    internal const int MaxPoints = 4096;
    internal const int MaxPitch = 12700;
    public static readonly GlissandoTimeline Empty = new([], .65);

    public ImmutableArray<ImmutableArray<GlissandoPoint>> Phrases { get; }
    public double Smoothing { get; }
    internal PitchContour[] Contours { get; }
    internal int Count { get; }

    public GlissandoTimeline(ImmutableArray<ImmutableArray<GlissandoPoint>> phrases, double smoothing)
    {
        Phrases = phrases;
        Smoothing = smoothing;
        if (!IsValid()) { Contours = []; return; }
        Contours = phrases.Select(points => new PitchContour(points, smoothing)).ToArray();
        Count = phrases.Sum(phrase => phrase.Length);
    }

    public bool IsValid()
    {
        if (Phrases.IsDefault || Phrases.Length > MaxPoints || !(Smoothing is >= 0 and <= 1)) return false;
        int previous = -1, count = 0;
        foreach (var phrase in Phrases)
        {
            if (phrase.IsDefaultOrEmpty || phrase.Length > MaxPoints - count) return false;
            count += phrase.Length;
            foreach (var point in phrase)
            {
                if (point.Position <= previous || point.Position > MidiTrackSettings.MaxTicks ||
                    point.Cents is < 0 or > MaxPitch) return false;
                previous = point.Position;
            }
        }
        return true;
    }
}
