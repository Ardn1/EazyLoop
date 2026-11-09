using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace EazyLoop;

public sealed class LoopStrip : ThemedCanvas
{
    private static Brush Surface => ThemeManager.Brush("Surface");
    private static Brush BeatMark => ThemeManager.Brush("Line");
    private static Brush BarMark => ThemeManager.Brush("AccentLine");
    private static Brush Copy => ThemeManager.Brush("Soft");
    private static Brush Violet => ThemeManager.Brush("Violet");
    private static Brush Shade => ThemeManager.Brush("AccentPanel");
    private static Brush Playhead => ThemeManager.Brush("Mint");
    private static Brush Rose => ThemeManager.Brush("Rose");
    private static readonly Typeface Mono = new("Consolas");
    private static readonly Typeface Ui = new("Segoe UI");
    private DrawingGroup? image;
    private (float Min, float Max)[] peaks = [];
    private LoopGrid grid = new(100, 4, 2);
    private int length;
    private int shift;
    private double gain = 1;
    private int phase = -1;
    private bool recording;
    private bool parked;
    private bool dragging;
    private bool shifting;
    private int lastSeek = -1;
    private double anchor;
    private int previousShift;
    private const int Bucket = 128;

    public bool IsRuler { get; set; }
    internal event Action? Clicked;
    internal event Action<int>? ShiftChanged;
    internal event Action<double>? ZoomStepped;
    internal event Action<int>? SeekRequested;

    internal void GridChanged(LoopGrid value)
    {
        if (grid == value) return;
        grid = value;
        InvalidateStrip();
    }

    internal void SetSamples(float[] samples, int count)
    {
        length = count;
        peaks = new (float, float)[(length + Bucket - 1) / Bucket];
        for (int i = 0; i < length; i++)
        {
            ref var peak = ref peaks[i / Bucket];
            float sample = Math.Clamp(samples[i], -1f, 1f);
            peak.Min = Math.Min(peak.Min, sample);
            peak.Max = Math.Max(peak.Max, sample);
        }
        Cursor = count == 0 ? Cursors.Arrow : Cursors.Hand;
        InvalidateStrip();
    }

    internal void SetShift(int value)
    {
        if (shift == value) return;
        shift = value;
        InvalidateStrip();
    }

    internal void SetZoom(double stops)
    {
        double value = Math.Pow(2, stops);
        if (gain == value) return;
        gain = value;
        InvalidateStrip();
    }

    internal void SetPlayhead(int value, bool active, bool parked = false)
    {
        if (phase == value && recording == active && this.parked == parked) return;
        phase = value;
        recording = active;
        this.parked = parked;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateStrip();
    }

    internal void InvalidateStrip()
    {
        image = null;
        InvalidateVisual();
    }

    protected override void ThemeChanged() => InvalidateStrip();

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 3 || height < 3) return;
        image ??= DrawBackground(width, height);
        context.DrawDrawing(image);
        if (phase < 0) return;
        double x = Math.Clamp(phase * width / grid.Frames, 1, width - 1);
        Brush color = recording ? Rose : parked ? Violet : Playhead;
        if (IsRuler)
        {
            if (parked) context.DrawRectangle(color, null, new Rect(x - .5, 4, 1, height - 7));
            else context.DrawRoundedRectangle(color, null, new Rect(0, height - 4, x, 2), 1, 1);
            context.DrawEllipse(color, null, new Point(x, height - 3), 3, 3);
        }
        else context.DrawRectangle(color, null, new Rect(x, 1, 1, height - 2));
    }

    private DrawingGroup DrawBackground(double width, double height)
    {
        var result = new DrawingGroup();
        using (DrawingContext drawing = result.Open())
        {
            drawing.DrawRoundedRectangle(Surface, null, new Rect(0, 0, width, height), 7, 7);
            int beats = grid.Beats * grid.Bars;
            for (int i = 1; i < beats; i++)
            {
                bool bar = i % grid.Beats == 0;
                if (!bar && width / beats < 10) continue;
                double x = Math.Round(i * width / beats);
                drawing.DrawRectangle(bar ? BarMark : BeatMark, null, new Rect(x, 5, 1, height - 10));
            }

            if (IsRuler)
            {
                double barWidth = width / grid.Bars;
                for (int i = 0; i < grid.Bars; i++)
                {
                    var label = Text((i + 1).ToString("00"), 11, Mono, Copy);
                    drawing.DrawText(label, new Point(i * barWidth + 7, 7));
                }
                drawing.DrawRoundedRectangle(Shade, null, new Rect(0, height - 4, width, 2), 1, 1);
            }
            else if (length == 0)
            {
                for (double x = 8; x < width - 4; x += 10)
                    drawing.DrawRectangle(BeatMark, null, new Rect(x, height / 2, 4, 1));
                var hint = Text(Locale.T("Здесь появится волна"), 12, Ui, Copy);
                double xText = (width - hint.Width) / 2;
                drawing.DrawRoundedRectangle(Surface, null, new Rect(xText - 7, 3, hint.Width + 14, height - 6), 4, 4);
                drawing.DrawText(hint, new Point(xText, (height - hint.Height) / 2));
            }
            else
            {
                double center = height / 2;
                double scale = center - 5;
                int loop = grid.Frames;
                int visibleSamples = Math.Min(length, loop);
                int slots = Math.Max(1, (int)(width / 3));
                for (int i = 0; i < slots; i++)
                {
                    int first = (int)(i * (long)loop / slots);
                    int next = (int)((i + 1L) * loop / slots);
                    int source = LoopEngine.Mod(first - shift, loop);
                    int end = source + next - first;
                    var peak = FindPeak(source, Math.Min(end, loop), visibleSamples);
                    if (end > loop)
                    {
                        var wrapped = FindPeak(0, end - loop, visibleSamples);
                        peak = (Math.Min(peak.Min, wrapped.Min), Math.Max(peak.Max, wrapped.Max), peak.Visible || wrapped.Visible);
                    }
                    if (!peak.Visible) continue;
                    double top = Math.Max(0.6, Math.Min(1, peak.Max * gain) * scale);
                    double bottom = Math.Max(0.6, Math.Min(1, -peak.Min * gain) * scale);
                    drawing.DrawRoundedRectangle(Violet, null,
                        new Rect(i * width / slots + 0.5, center - top, 1.65, top + bottom), 0.7, 0.7);
                }
            }
        }
        result.Freeze();
        return result;
    }

    private (float Min, float Max, bool Visible) FindPeak(int start, int end, int count)
    {
        end = Math.Min(end, count);
        if (start >= end) return (0, 0, false);
        float min = 0, max = 0;
        int last = (end - 1) / Bucket;
        for (int i = start / Bucket; i <= last; i++)
        {
            min = Math.Min(min, peaks[i].Min);
            max = Math.Max(max, peaks[i].Max);
        }
        return (min, max, true);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsEnabled) return;
        if (IsRuler)
        {
            if (ActualWidth <= 0) return;
            lastSeek = -1;
            dragging = CaptureMouse();
            SeekAt(e.GetPosition(this).X);
            e.Handled = true;
            return;
        }
        if (length == 0) return;
        Focus();
        shifting = false;
        anchor = e.GetPosition(this).X;
        previousShift = shift;
        dragging = CaptureMouse();
        e.Handled = true;
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (IsRuler || length == 0 || !IsEnabled || (Keyboard.Modifiers & ModifierKeys.Alt) == 0) return;
        ZoomStepped?.Invoke(e.Delta / 480.0);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging && ActualWidth > 0)
        {
            double x = e.GetPosition(this).X;
            if (IsRuler) SeekAt(x);
            else
            {
                if (!shifting && Math.Abs(x - anchor) < SystemParameters.MinimumHorizontalDragDistance) return;
                shifting = true;
                double delta = (x - anchor) * grid.Frames / ActualWidth;
                ShiftChanged?.Invoke(previousShift + (int)Math.Round(delta));
            }
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!dragging) return;
        Point point = e.GetPosition(this);
        bool open = !IsRuler && !shifting && new Rect(RenderSize).Contains(point);
        if (IsRuler && ActualWidth > 0) SeekAt(point.X);
        ReleaseMouseCapture();
        e.Handled = true;
        if (open) Clicked?.Invoke();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (!IsRuler || !IsEnabled) return;
        if (IsMouseCaptured) ReleaseMouseCapture();
        SeekRequested?.Invoke(0);
        e.Handled = true;
    }

    private void SeekAt(double x)
    {
        double fraction = Math.Clamp(x / ActualWidth, 0, 1);
        int position = Math.Min(grid.Frames - 1, (int)(fraction * grid.Frames));
        if (position == lastSeek) return;
        lastSeek = position;
        SeekRequested?.Invoke(position);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        dragging = false;
        base.OnLostMouseCapture(e);
    }

    private FormattedText Text(string text, int size, Typeface font, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, font, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

}
