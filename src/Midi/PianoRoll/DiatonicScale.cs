namespace EazyLoop;

internal readonly record struct DiatonicScale(int Tonic, bool Minor)
{
    public const int Count = 24;
    private static readonly string[] MajorKeys = ["C", "D♭", "D", "E♭", "E", "F", "F♯", "G", "A♭", "A", "B♭", "B"];
    private static readonly string[] MinorKeys = ["C", "C♯", "D", "E♭", "E", "F", "F♯", "G", "G♯", "A", "B♭", "B"];
    public static DiatonicScale FromIndex(int index) => new(index % 12, index >= 12);
    public string DisplayName => Minor ? Locale.F($"{MinorKeys[Tonic]} минор") : Locale.F($"{MajorKeys[Tonic]} мажор");

    private ReadOnlySpan<int> Steps => Minor ? [0, 2, 3, 5, 7, 8, 10] : [0, 2, 4, 5, 7, 9, 11];

    public bool Contains(int pitch) => Steps.Contains((pitch + 12 - Tonic) % 12);

    public int[] BuildChord(int root, int voices)
    {
        var steps = Steps;
        int degree = steps.IndexOf((root + 12 - Tonic) % 12);
        if (degree < 0) return [];

        var chord = new int[voices];
        int tonicBelow = root - steps[degree];
        for (int i = 0; i < chord.Length; i++)
        {
            int step = degree + 2 * i;
            chord[i] = tonicBelow + steps[step % steps.Length] + 12 * (step / steps.Length);
        }
        return chord[^1] <= 127 ? chord : [];
    }
}
