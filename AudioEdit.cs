using System.Text.Json.Serialization;

namespace EazyLoop;

internal readonly record struct AudioClip(int Source, int Start, int Frames)
{
    [JsonIgnore] public int End => Start + Frames;
}

internal readonly record struct GainPoint(int Frame, float Gain);

internal sealed record AudioEdit(AudioClip[] Clips, GainPoint[] Points)
{
    public const int MaxClips = 256;
    public const int MaxPoints = 256;
    public const float MaxGain = 2;
    private const int EdgeFade = 48;

    public static AudioEdit Original(int frames) => new([new(0, 0, frames)], []);

    public bool Same(AudioEdit other) =>
        ReferenceEquals(this, other) || (Clips.AsSpan().SequenceEqual(other.Clips) && Points.AsSpan().SequenceEqual(other.Points));

    public bool IsOriginal(int frames) => Points.Length == 0 && Clips.Length == 1 && Clips[0] == new AudioClip(0, 0, frames);

    public int Length(int sourceFrames) => Clips.Aggregate(sourceFrames, (length, clip) => Math.Max(length, clip.End));

    public int Extent(int sourceFrames) => Math.Max(Length(sourceFrames), Points.Length == 0 ? 0 : Points[^1].Frame);

    public bool IsValid(int sourceFrames, int maxFrames)
    {
        if (sourceFrames <= 0 || Clips == null || Clips.Length > MaxClips || Points == null || Points.Length > MaxPoints)
            return false;
        int last = 0;
        foreach (var clip in Clips.OrderBy(clip => clip.Source))
        {
            if (clip.Frames <= 0 || clip.Source < last || clip.Source > sourceFrames - clip.Frames ||
                clip.Start < 0 || clip.Start > maxFrames - clip.Frames)
                return false;
            last = clip.Source + clip.Frames;
        }
        last = -1;
        foreach (var point in Points)
        {
            if (point.Frame <= last || point.Frame > maxFrames || !float.IsFinite(point.Gain) || point.Gain is < 0 or > MaxGain)
                return false;
            last = point.Frame;
        }
        return true;
    }

    public float[] Render(float[] source, int sourceFrames)
    {
        var output = new float[Length(sourceFrames)];
        var starts = Clips.Select(clip => (clip.Source, clip.Start)).ToHashSet();
        var ends = Clips.Select(clip => (clip.Source + clip.Frames, clip.End)).ToHashSet();
        foreach (var clip in Clips)
        {
            // Preserve continuous cuts. Fade exposed or discontinuous edges to avoid clicks.
            int fade = Math.Min(EdgeFade, clip.Frames / 2);
            bool fadeIn = fade > 0 && (clip.Source != 0 || clip.Start != 0) && !ends.Contains((clip.Source, clip.Start));
            bool fadeOut = fade > 0 && (clip.Source + clip.Frames != sourceFrames || clip.End != output.Length) &&
                !starts.Contains((clip.Source + clip.Frames, clip.End));
            for (int i = 0; i < clip.Frames; i++)
            {
                float value = source[clip.Source + i];
                if (fadeIn && i < fade) value *= i / (float)fade;
                if (fadeOut && i >= clip.Frames - fade) value *= (clip.Frames - 1 - i) / (float)fade;
                output[clip.Start + i] += value;
            }
        }

        if (Points.Length == 0) return output;
        int right = 0;
        for (int frame = 0; frame < output.Length; frame++)
        {
            while (right < Points.Length && Points[right].Frame <= frame) right++;
            float gain = right == 0 ? Points[0].Gain : right == Points.Length ? Points[^1].Gain :
                Interpolate(Points[right - 1], Points[right], frame);
            output[frame] *= gain;
        }
        return output;
    }

    private static float Interpolate(GainPoint left, GainPoint right, int frame) =>
        left.Gain + (right.Gain - left.Gain) * ((frame - left.Frame) / (float)(right.Frame - left.Frame));
}

internal sealed record EditedAudio(AudioEdit Edit, float[] Samples)
{
    public static EditedAudio? Build(AudioEdit? edit, float[] source, int length) =>
        edit == null ? null : new(edit, edit.Render(source, length));
}
