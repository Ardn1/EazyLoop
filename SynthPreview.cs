using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace EazyLoop;

internal sealed class SynthWavePreview : ThemedCanvas
{
    private static Brush surface => ThemeManager.Brush("Panel");
    private static Pen grid => ThemeManager.Stroke("Line");
    private static Pen trace => ThemeManager.Stroke("Violet", 1.6);
    private SynthPatch patch = SynthPatch.Default;

    internal void Update(SynthPatch next)
    {
        bool redraw = next.Wave != patch.Wave || next.Blend != patch.Blend || next.Tone != patch.Tone;
        patch = next;
        if (!redraw) return;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 20 || height <= 20) return;
        context.DrawRoundedRectangle(surface, grid, new Rect(.5, .5, width - 1, height - 1), 8, 8);

        const double inset = 10;
        double span = width - 2 * inset, middle = height / 2;
        context.DrawLine(grid, new Point(inset, middle), new Point(width - inset, middle));
        for (int i = 1; i < 4; i++)
        {
            double x = inset + span * i / 4;
            context.DrawLine(grid, new Point(x, inset), new Point(x, height - inset));
        }

        const double step = 440.0 / LoopEngine.Rate;
        int frames = (int)Math.Round(2 / step);
        float filtered = 0, coefficient = SimpleSynth.LowpassCoefficient(patch.Tone);
        WaveShape next = patch.NextWave;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            // Warm up the filter before drawing two periods of a steady A4.
            for (int i = -frames; i <= frames; i++)
            {
                double phase = i * step;
                float sample = SimpleSynth.Oscillator(patch.Wave, next, patch.Blend, phase - Math.Floor(phase), step);
                filtered += coefficient * (sample - filtered);
                if (i < 0) continue;
                var point = new Point(inset + span * i / frames, middle - filtered * (middle - inset));
                if (i == 0) path.BeginFigure(point, false, false);
                else path.LineTo(point, true, false);
            }
        }
        context.DrawGeometry(null, trace, geometry);
    }
}

internal sealed class SynthPad(Action<int, int> play, Action<string?> showPosition) : ThemedCanvas
{
    private const int TopNote = 72;
    private const int Rows = 25;
    private static readonly Typeface Font = new("Segoe UI");
    private static Brush surface => ThemeManager.Brush("Panel");
    private static Brush label => ThemeManager.Brush("Muted");
    private static Brush accent => ThemeManager.Brush("Violet");
    private static Pen grid => ThemeManager.Stroke("Line");
    private static Pen crosshair => ThemeManager.Stroke("Violet", 1, dotted: true);
    private Point? hover;

    protected override void OnRender(DrawingContext context)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2 || h <= 2) return;
        var bounds = new Rect(.5, .5, w - 1, h - 1);
        var outline = new RectangleGeometry(bounds, 8, 8);
        context.DrawGeometry(surface, null, outline);
        context.PushClip(outline);
        for (int row = 1; row < Rows; row++)
        {
            double y = h * row / Rows;
            context.DrawLine(grid, new Point(0, y), new Point(w, y));
        }
        for (int col = 1; col < 4; col++)
        {
            double x = w * col / 4;
            context.DrawLine(grid, new Point(x, 0), new Point(x, h));
        }
        for (int row = 0; row < Rows; row += 12)
        {
            double y = h * (row + .5) / Rows;
            var note = new FormattedText(PianoRollSurface.PitchName(TopNote - row), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Font, 9, label, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            context.DrawText(note, new Point(7, y - note.Height / 2));
        }
        if (hover is Point marker)
        {
            context.DrawLine(crosshair, new Point(marker.X, 0), new Point(marker.X, h));
            context.DrawLine(crosshair, new Point(0, marker.Y), new Point(w, marker.Y));
            context.DrawEllipse(accent, null, marker, 3, 3);
        }
        context.Pop();
        context.DrawGeometry(null, grid, outline);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        UpdatePosition(e.GetPosition(this), false);
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        hover = null;
        showPosition(null);
        InvalidateVisual();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        UpdatePosition(e.GetPosition(this), true);
        e.Handled = true;
    }

    private void UpdatePosition(Point point, bool hit)
    {
        double x = Math.Clamp(point.X, 0, ActualWidth);
        double y = Math.Clamp(point.Y, 0, ActualHeight);
        int row = Math.Min(Rows - 1, (int)(Rows * y / ActualHeight));
        int key = TopNote - row;
        int velocity = (int)Math.Round(127 * x / ActualWidth);
        hover = new Point(x, ActualHeight * (row + .5) / Rows);
        if (hit) play(key, velocity);
        showPosition($"{PianoRollSurface.PitchName(key)} · {velocity * 100.0 / 127:0} %");
        InvalidateVisual();
    }
}
