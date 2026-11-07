using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace EazyLoop;

public sealed class AudioEditSurface : ThemedCanvas
{
    private const double Gutter = 80;
    private const double RulerHeight = 34;
    private const int Bucket = 128;
    private static readonly Typeface Ui = new("Segoe UI");
    private static readonly Typeface Mono = new("Consolas");
    private float[] source = [];
    private int sourceFrames;
    private (float Min, float Max)[] peaks = [];
    private LoopGrid grid = new(100, 4, 2);
    private int timeline;
    private int viewStart;
    private int viewFrames = 1;
    private int phase = -1;
    private DrawingGroup? image;
    private int selectedClip = -1;
    private int selectedPoint = -1;
    private AudioEdit? beforeDrag;
    private Point anchor;
    private GainPoint pointAtAnchor;
    private int knifeFrame = -1;
    private bool cutting;

    internal AudioEdit State { get; private set; } = new([], []);
    private bool snap;
    internal bool Snap
    {
        get => snap;
        set { snap = value; RefreshDrawing(); }
    }
    internal bool Cutting
    {
        get => cutting;
        set
        {
            cutting = value;
            knifeFrame = -1;
            Cursor = value ? Cursors.Cross : Cursors.Arrow;
            InvalidateVisual();
        }
    }
    internal event Action? Edited;
    internal event Action? SelectionChanged;
    internal event Action<int>? SeekRequested;
    internal event Action<int>? ZoomRequested;

    private double WidthOfTime => Math.Max(1, ActualWidth - Gutter);
    private double GainTop => Math.Max(RulerHeight + 70, ActualHeight - 134);
    private double GainCeiling => GainTop + 29;
    private double GainFloor => Math.Max(GainCeiling + 1, ActualHeight - 13);

    internal string Selection => selectedClip >= 0
        ? Locale.F($"{Seconds(State.Clips[selectedClip].Start):0.000} с · длина {Seconds(State.Clips[selectedClip].Frames):0.000} с")
        : selectedPoint >= 0 ? Locale.F($"{Seconds(State.Points[selectedPoint].Frame):0.000} с · {State.Points[selectedPoint].Gain * 100:0}%")
        : "";

    internal void Load(float[] samples, int count, AudioEdit edit)
    {
        if (!ReferenceEquals(source, samples) || sourceFrames != count)
        {
            source = samples;
            sourceFrames = count;
            peaks = new (float, float)[(count + Bucket - 1) / Bucket];
            for (int i = 0; i < count; i++)
            {
                ref var peak = ref peaks[i / Bucket];
                peak.Min = Math.Min(peak.Min, source[i]);
                peak.Max = Math.Max(peak.Max, source[i]);
            }
        }
        SetEdit(edit);
    }

    internal void SetEdit(AudioEdit state)
    {
        FinishDrag(commit: false);
        selectedClip = selectedPoint = -1;
        ShowEdit(state);
    }

    internal void SetViewport(LoopGrid loop, int total, int start, int frames)
    {
        grid = loop;
        timeline = total;
        viewStart = start;
        viewFrames = frames;
        RefreshDrawing();
    }

    internal void Playhead(int frame)
    {
        if (phase == frame) return;
        phase = frame;
        InvalidateVisual();
    }

    internal void DeleteSelection()
    {
        if (beforeDrag != null) return;
        if (selectedClip >= 0)
        {
            var clips = State.Clips.Where((_, i) => i != selectedClip).ToArray();
            SetEdit(State with { Clips = clips });
        }
        else if (selectedPoint >= 0)
        {
            var points = State.Points.Where((_, i) => i != selectedPoint).ToArray();
            if (points.All(point => point.Gain == 1)) points = [];
            SetEdit(State with { Points = points });
        }
        else return;
        Edited?.Invoke();
    }

    private void ShowEdit(AudioEdit state)
    {
        State = state;
        SelectionChanged?.Invoke();
        RefreshDrawing();
    }

    private void RefreshDrawing()
    {
        image = null;
        InvalidateVisual();
    }

    protected override void ThemeChanged() => RefreshDrawing();

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RefreshDrawing();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth <= Gutter || ActualHeight <= RulerHeight + 70) return;
        image ??= DrawTimeline();
        drawing.DrawDrawing(image);
        DrawMarker(phase, "Mint", ActualHeight);
        DrawMarker(knifeFrame, "Rose", GainTop);

        void DrawMarker(int frame, string color, double bottom)
        {
            if (frame < 0 || frame < viewStart || frame > viewStart + viewFrames) return;
            double x = Math.Clamp(X(frame), Gutter, ActualWidth - 1);
            drawing.DrawRectangle(ThemeManager.Brush(color), null, new Rect(x, 0, 1, bottom));
        }
    }

    private DrawingGroup DrawTimeline()
    {
        var result = new DrawingGroup();
        using (var d = result.Open())
        {
            Brush soft = ThemeManager.Brush("Soft");
            Brush accent = ThemeManager.Brush("Violet");
            var thin = new Pen(ThemeManager.Brush("Line"), 1);
            var strong = new Pen(ThemeManager.Brush("AccentLine"), 1);
            var bounds = new Rect(Gutter, 0, WidthOfTime, ActualHeight);
            d.DrawRectangle(ThemeManager.Brush("Surface"), null, new Rect(RenderSize));
            d.DrawRectangle(ThemeManager.Brush("Panel"), null, new Rect(0, 0, ActualWidth, RulerHeight));
            d.DrawRectangle(ThemeManager.Brush("Panel"), null, new Rect(0, GainTop, ActualWidth, ActualHeight - GainTop));
            d.DrawRectangle(ThemeManager.Brush("Panel"), null, new Rect(0, 0, Gutter, ActualHeight));
            Label(d, Locale.T("Фрагменты"), 8, RulerHeight + 12, soft);
            Label(d, Locale.T("Громкость"), 8, GainTop + 6, accent);
            for (int level = 0; level <= 2; level++)
            {
                double y = GainY(level);
                Label(d, $"{level * 100}%", 10, y - 7, soft, Mono);
                d.DrawLine(level == 1 ? strong : thin, new Point(Gutter, y), new Point(ActualWidth, y));
            }
            d.DrawLine(thin, new Point(0, RulerHeight), new Point(ActualWidth, RulerHeight));
            d.DrawLine(strong, new Point(0, GainTop), new Point(ActualWidth, GainTop));
            d.DrawLine(thin, new Point(Gutter, 0), new Point(Gutter, ActualHeight));
            d.PushClip(new RectangleGeometry(bounds));
            double step = grid.BeatFrames;
            if (Snap && step * WidthOfTime / viewFrames >= 52)
            {
                double subdivision = step / 4;
                int tick = (int)Math.Floor(viewStart / subdivision);
                for (; tick * subdivision <= viewStart + viewFrames; tick++)
                {
                    if (tick % 4 == 0) continue;
                    double x = X(tick * subdivision);
                    d.DrawLine(thin, new Point(x, RulerHeight), new Point(x, ActualHeight));
                }
            }
            int beat = (int)Math.Floor(viewStart / step);
            for (; beat * step <= viewStart + viewFrames; beat++)
            {
                double frame = beat * step;
                double x = X(frame);
                bool bar = beat % grid.Beats == 0;
                d.DrawLine(bar ? strong : thin, new Point(x, 0), new Point(x, ActualHeight));
                if (bar || step * WidthOfTime / viewFrames > 55)
                    Label(d, bar ? $"{beat / grid.Beats + 1:00}" : $"{beat % grid.Beats + 1}", x + 5, 4, soft, Mono);
                if (bar) Label(d, $"{Seconds(frame):0.00}", x + 5, 17, soft, Mono);
            }
            for (int i = 0; i < State.Clips.Length; i++)
                if (i != selectedClip) DrawClip(d, State.Clips[i], false);
            if (selectedClip >= 0) DrawClip(d, State.Clips[selectedClip], true);
            DrawGain(d, accent);
            d.Pop();
        }
        result.Freeze();
        return result;
    }

    private void DrawClip(DrawingContext d, AudioClip clip, bool selected)
    {
        double left = X(clip.Start);
        double right = X(clip.End);
        if (right <= Gutter || left >= ActualWidth) return;
        double top = RulerHeight + 12;
        double height = GainTop - top - 13;
        var accent = ThemeManager.Brush(selected ? "Mint" : "Violet");
        d.PushOpacity(selected ? .89 : .68);
        d.DrawRoundedRectangle(ThemeManager.Brush("AccentPanel"), new Pen(accent, selected ? 1.4 : .7),
            new Rect(left, top, Math.Max(1, right - left), height), 5, 5);
        d.Pop();
        var wave = new Pen(accent, 1);
        double middle = top + height * .53;
        for (double x = Math.Max(Gutter, left) + .5; x < Math.Min(ActualWidth, right); x += 2)
        {
            int a = clip.Source + Math.Clamp((int)FrameAt(x) - clip.Start, 0, clip.Frames - 1);
            int b = clip.Source + Math.Clamp((int)Math.Ceiling(FrameAt(x + 2)) - clip.Start, 1, clip.Frames);
            var (min, max) = Peak(a, b);
            double scale = height * .42;
            d.DrawLine(wave, new Point(x, middle - Math.Clamp(max, 0, 1) * scale - .4),
                new Point(x, middle - Math.Clamp(min, -1, 0) * scale + .4));
        }
    }

    private (float Min, float Max) Peak(int start, int end)
    {
        float min = 0, max = 0;
        for (int i = start; i < end;)
        {
            if (i % Bucket == 0 && end - i >= Bucket)
            {
                var peak = peaks[i / Bucket];
                min = Math.Min(min, peak.Min);
                max = Math.Max(max, peak.Max);
                i += Bucket;
            }
            else
            {
                min = Math.Min(min, source[i]);
                max = Math.Max(max, source[i]);
                i++;
            }
        }
        return (min, max);
    }

    private void DrawGain(DrawingContext d, Brush accent)
    {
        var points = State.Points;
        float value = points.Length == 0 ? 1 : points[0].Gain;
        var line = new Pen(accent, 1.6);
        Point last = new(X(0), GainY(value));
        for (int i = 0; i < points.Length; i++)
        {
            var position = PointFor(points[i]);
            d.DrawLine(line, last, position);
            d.DrawEllipse(i == selectedPoint ? ThemeManager.Brush("Mint") : accent,
                new Pen(ThemeManager.Brush("Panel"), 1), position, 4.5, 4.5);
            last = position;
        }
        d.DrawLine(line, last, new Point(X(timeline), last.Y));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var point = e.GetPosition(this);
        if (point.X < Gutter || timeline <= 0) return;
        Focus();
        e.Handled = true;
        if (point.Y < RulerHeight)
        {
            SeekRequested?.Invoke(Frame(point.X, snap: false));
            return;
        }
        if (point.Y >= GainTop)
        {
            StartPoint(point);
            return;
        }

        int clip = HitClip(Frame(point.X, snap: false));
        Select(clip, -1);
        if (clip < 0) return;
        if (Cutting) Split(clip, Frame(point.X, Snap));
        else StartDrag(point);
    }

    private int HitClip(int frame)
    {
        if (selectedClip >= 0 && Contains(State.Clips[selectedClip], frame)) return selectedClip;
        return Array.FindLastIndex(State.Clips, clip => Contains(clip, frame));
    }

    private static bool Contains(AudioClip clip, int frame) => frame >= clip.Start && frame < clip.End;

    private void Split(int index, int frame)
    {
        var clip = State.Clips[index];
        if (frame <= clip.Start || frame >= clip.End || State.Clips.Length >= AudioEdit.MaxClips) return;
        int first = frame - clip.Start;
        var clips = new List<AudioClip>(State.Clips);
        clips[index] = clip with { Frames = first };
        clips.Insert(index + 1, new AudioClip(clip.Source + first, frame, clip.Frames - first));
        selectedClip = index + 1;
        ShowEdit(State with { Clips = clips.ToArray() });
        Edited?.Invoke();
    }

    private void StartPoint(Point mouse)
    {
        int hit = HitPoint(mouse);
        if (hit >= 0)
        {
            Select(-1, hit);
            pointAtAnchor = State.Points[hit];
            StartDrag(mouse);
            return;
        }
        if (State.Points.Length >= AudioEdit.MaxPoints || !StartDrag(mouse)) return;
        var points = State.Points.Length == 0
            ? new List<GainPoint> { new(0, 1), new(timeline, 1) }
            : new List<GainPoint>(State.Points);
        int frame = Frame(mouse.X, Snap);
        int index = points.FindIndex(point => point.Frame >= frame);
        if (index < 0) index = points.Count;
        var created = new GainPoint(frame, Gain(mouse.Y));
        pointAtAnchor = created;
        if (index < points.Count && points[index].Frame == frame) points[index] = created;
        else points.Insert(index, created);
        selectedClip = -1;
        selectedPoint = index;
        ShowEdit(State with { Points = points.ToArray() });
    }

    private int HitPoint(Point mouse) => Array.FindIndex(State.Points, point =>
        (PointFor(point) - mouse).LengthSquared <= 64);

    private bool StartDrag(Point mouse)
    {
        if (!CaptureMouse()) return false;
        anchor = mouse;
        beforeDrag = State;
        return true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var mouse = e.GetPosition(this);
        if (beforeDrag != null) DragTo(mouse);
        else
        {
            knifeFrame = Cutting && mouse.X >= Gutter && mouse.Y >= RulerHeight && mouse.Y < GainTop
                ? Frame(mouse.X, Snap) : -1;
            InvalidateVisual();
        }
    }

    private void DragTo(Point mouse)
    {
        if (selectedClip >= 0)
        {
            var clip = beforeDrag!.Clips[selectedClip];
            double delta = mouse.X - anchor.X;
            int start = Math.Abs(delta) < .5 ? clip.Start : Quantize(clip.Start + delta * viewFrames / WidthOfTime, Snap);
            start = Math.Clamp(start, 0, timeline - clip.Frames);
            if (start == State.Clips[selectedClip].Start) return;
            var clips = (AudioClip[])State.Clips.Clone();
            clips[selectedClip] = clip with { Start = start };
            ShowEdit(State with { Clips = clips });
        }
        else if (selectedPoint >= 0)
        {
            var points = State.Points;
            var current = points[selectedPoint];
            double dx = (mouse.X - anchor.X) * viewFrames / WidthOfTime;
            double dy = (mouse.Y - anchor.Y) * AudioEdit.MaxGain / (GainFloor - GainCeiling);
            int low = selectedPoint > 0 ? points[selectedPoint - 1].Frame + 1 : 0;
            int high = selectedPoint + 1 < points.Length ? points[selectedPoint + 1].Frame - 1 : timeline;
            int frame = Math.Abs(mouse.X - anchor.X) < .5 ? pointAtAnchor.Frame :
                Math.Clamp(Quantize(pointAtAnchor.Frame + dx, Snap), low, high);
            float gain = (float)Math.Clamp(Math.Round((pointAtAnchor.Gain - dy) * 100) / 100, 0, AudioEdit.MaxGain);
            if (frame == current.Frame && gain == current.Gain) return;
            points = (GainPoint[])points.Clone();
            points[selectedPoint] = new(frame, gain);
            ShowEdit(State with { Points = points });
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (beforeDrag == null) return;
        DragTo(e.GetPosition(this));
        FinishDrag(commit: true);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        FinishDrag(commit: false);
    }

    private void FinishDrag(bool commit)
    {
        if (beforeDrag is not { } previous) return;
        beforeDrag = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (commit)
        {
            if (!previous.Same(State)) Edited?.Invoke();
        }
        else
        {
            selectedClip = selectedPoint = -1;
            ShowEdit(previous);
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        int point = HitPoint(e.GetPosition(this));
        if (point < 0) return;
        Select(-1, point);
        DeleteSelection();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        knifeFrame = -1;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (beforeDrag != null || Keyboard.Modifiers != ModifierKeys.Control) return;
        ZoomRequested?.Invoke(Math.Sign(e.Delta));
        e.Handled = true;
    }

    private void Select(int clip, int point)
    {
        selectedClip = clip;
        selectedPoint = point;
        SelectionChanged?.Invoke();
        RefreshDrawing();
    }

    private int Frame(double x, bool snap) => Math.Clamp(Quantize(FrameAt(x), snap), 0, timeline);
    private int Quantize(double frame, bool snap)
    {
        double subdivision = grid.BeatFrames / 4;
        return (int)Math.Round(snap ? Math.Round(frame / subdivision) * subdivision : frame);
    }
    private double FrameAt(double x) => viewStart + (x - Gutter) * viewFrames / WidthOfTime;
    private double X(double frame) => Gutter + (frame - viewStart) * WidthOfTime / viewFrames;
    private static double Seconds(double frame) => frame / LoopEngine.Rate;
    private float Gain(double y) => (float)Math.Clamp(Math.Round((GainFloor - y) / (GainFloor - GainCeiling) * 200) / 100, 0, AudioEdit.MaxGain);
    private double GainY(float gain) => GainFloor - (GainFloor - GainCeiling) * gain / AudioEdit.MaxGain;
    private Point PointFor(GainPoint point) => new(X(point.Frame), GainY(point.Gain));

    private void Label(DrawingContext d, string text, double x, double y, Brush brush, Typeface? font = null) =>
        d.DrawText(new FormattedText(text, Locale.Culture, FlowDirection.LeftToRight, font ?? Ui,
            11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
}
