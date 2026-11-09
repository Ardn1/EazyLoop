using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace EazyLoop;

internal sealed class SynthTonePad : ThemedCanvas
{
    private static readonly Typeface Font = new("Segoe UI");
    private SynthPatch patch = SynthPatch.Default;
    private Rect Plot => new(27, 27, Math.Max(1, ActualWidth - 54), Math.Max(1, ActualHeight - 76));

    internal event Action<SynthPatch>? Changed;

    public SynthTonePad()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
    }

    internal void Update(SynthPatch next)
    {
        bool redraw = next.Wave != patch.Wave || next.Blend != patch.Blend || next.Tone != patch.Tone;
        patch = next;
        if (redraw) InvalidateVisual();
    }

    internal static string WaveLabel(SynthPatch value) => value.Blend == 0 ? ShapeName(value.Wave) :
        Locale.F($"{ShapeName(value.Wave)} → {ShapeName(value.NextWave)} · {value.Blend * 100:0.#}%");

    private static string ShapeName(WaveShape shape) => Locale.T(shape switch
    {
        WaveShape.Sine => "Синус",
        WaveShape.Triangle => "Треугольник",
        WaveShape.Square => "Квадрат",
        _ => "Пила"
    });

    protected override void OnRender(DrawingContext drawing)
    {
        if (ActualWidth < 56 || ActualHeight < 80) return;
        var area = Plot;
        var frame = new Rect(.5, .5, ActualWidth - 1, ActualHeight - 25);
        drawing.DrawRoundedRectangle(ThemeManager.Brush("Field"), ThemeManager.Stroke("Line"), frame, 8, 8);
        var glow = new LinearGradientBrush(ThemeManager.Brush("AccentHover").Color, Colors.Transparent, 90);
        drawing.DrawRoundedRectangle(glow, null, frame, 8, 8);

        for (int i = 0; i <= 4; i++)
        {
            double y = area.Top + i * area.Height / 4;
            drawing.DrawLine(ThemeManager.Stroke("Line"), new Point(area.Left, y), new Point(area.Right, y));
        }
        var waves = SynthPatch.Waves;
        int nearest = (int)Math.Round(patch.WavePosition / 100);
        for (int i = 0; i < waves.Length; i++)
        {
            double x = area.Left + i * area.Width / (waves.Length - 1);
            drawing.DrawLine(ThemeManager.Stroke("Line"), new Point(x, area.Top), new Point(x, area.Bottom));
            var title = Label(ShapeName(waves[i]), i == nearest ? "Violet" : "Soft");
            drawing.DrawText(title, new Point(Math.Clamp(x - title.Width / 2, 0, Math.Max(0, ActualWidth - title.Width)),
                ActualHeight - 20));
        }
        DrawEdge(Locale.T("Ярче"), 5);
        DrawEdge(Locale.T("Мягче"), area.Bottom + 8);

        var point = new Point(area.Left + area.Width * patch.WavePosition / 300,
            area.Bottom - area.Height * patch.Tone / 100);
        var crosshair = ThemeManager.Stroke("Violet", 1, dotted: true);
        drawing.DrawLine(crosshair, new Point(area.Left, point.Y), new Point(area.Right, point.Y));
        drawing.DrawLine(crosshair, new Point(point.X, area.Top), new Point(point.X, area.Bottom));
        drawing.DrawEllipse(ThemeManager.Brush("AccentHover"), ThemeManager.Stroke("Violet", IsKeyboardFocused ? 2 : 1), point, 12, 12);
        drawing.DrawEllipse(ThemeManager.Brush("Violet"), null, point, 5.5, 5.5);

        void DrawEdge(string text, double y)
        {
            var label = Label(text, "Soft");
            drawing.DrawText(label, new Point(ActualWidth - label.Width - 9, y));
        }
    }

    private FormattedText Label(string text, string color) => new(text, Locale.Culture, FlowDirection.LeftToRight,
        Font, 11, ThemeManager.Brush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Point position = e.GetPosition(this);
        if (position.Y >= ActualHeight - 24) return;
        Focus();
        if (!CaptureMouse()) return;
        Pick(position);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) Pick(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnIsKeyboardFocusedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusedChanged(e);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsMouseCaptured || Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift)) return;
        float step = Keyboard.Modifiers == ModifierKeys.Shift ? 10 : 1;
        var next = e.Key switch
        {
            Key.Left => patch.WithWavePosition(patch.WavePosition - step),
            Key.Right => patch.WithWavePosition(patch.WavePosition + step),
            Key.Up => patch with { Tone = Math.Min(100, patch.Tone + step) },
            Key.Down => patch with { Tone = Math.Max(0, patch.Tone - step) },
            _ => null
        };
        if (next == null) return;
        Changed?.Invoke(next);
        e.Handled = true;
    }

    private void Pick(Point position)
    {
        var area = Plot;
        float wave = (float)Math.Round(300 * (position.X - area.Left) / area.Width);
        float tone = (float)Math.Round(100 * Math.Clamp((area.Bottom - position.Y) / area.Height, 0, 1));
        Changed?.Invoke(patch.WithWavePosition(wave) with { Tone = tone });
    }
}

internal sealed class SynthEnvelopePlot : ThemedCanvas
{
    private enum Grip { Attack, Decay, Release }
    private readonly record struct Range(float Min, float Max, double Narrow, double Wide)
    {
        public double Fraction(float time) => Narrow + (Wide - Narrow) * Math.Log(time / Min) / Math.Log(Max / Min);
        public float Time(float original, double delta) => delta == 0 ? original :
            (float)Math.Round(Min * Math.Pow(Max / Min, Math.Clamp((Fraction(original) + delta - Narrow) / (Wide - Narrow), 0, 1)));
    }

    private static readonly Range Attack = new(1, 1500, .11, .23);
    private static readonly Range Decay = new(5, 2000, .17, .25);
    private static readonly Range Release = new(5, 3000, .17, .32);
    private static readonly Typeface Font = new("Segoe UI");
    private static readonly Typeface GripFont = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private SynthPatch patch = SynthPatch.Default;
    private SynthPatch? dragPatch;
    private Point origin;
    private Grip selected;

    internal event Action<SynthPatch>? Changed;

    public SynthEnvelopePlot() => Focusable = true;

    private Rect Plot => new(20, 28, Math.Max(1, ActualWidth - 42), Math.Max(1, ActualHeight - 68));

    internal void Update(SynthPatch next)
    {
        bool redraw = next.Attack != patch.Attack || next.Decay != patch.Decay ||
            next.Sustain != patch.Sustain || next.Release != patch.Release;
        patch = next;
        if (redraw) InvalidateVisual();
    }

    private (Point Peak, Point Hold, Point Off, Point Tail) Points(Rect area)
    {
        double peak = area.Left + Attack.Fraction(patch.Attack) * area.Width;
        double hold = peak + Decay.Fraction(patch.Decay) * area.Width;
        double off = hold + .16 * area.Width;
        double tail = off + Release.Fraction(patch.Release) * area.Width;
        double level = area.Bottom - area.Height * patch.Sustain / 100;
        return (new Point(peak, area.Top), new Point(hold, level), new Point(off, level), new Point(tail, area.Bottom));
    }

    protected override void OnRender(DrawingContext drawing)
    {
        if (ActualWidth < 44 || ActualHeight < 70) return;
        drawing.DrawRoundedRectangle(ThemeManager.Brush("Field"), ThemeManager.Stroke("Line"),
            new Rect(.5, .5, ActualWidth - 1, ActualHeight - 1), 8, 8);
        var area = Plot;
        for (int i = 0; i <= 4; i++)
        {
            double y = area.Top + area.Height * i / 4;
            drawing.DrawLine(ThemeManager.Stroke("Line"), new Point(area.Left, y), new Point(area.Right, y));
        }
        var start = new Point(area.Left, area.Bottom);
        var (peak, hold, off, tail) = Points(area);
        var fill = new StreamGeometry();
        using (var path = fill.Open())
        {
            path.BeginFigure(start, true, false);
            path.PolyLineTo([peak, hold, off, tail], true, true);
        }
        drawing.DrawGeometry(ThemeManager.Brush("MintShade"), ThemeManager.Stroke("Mint", 2), fill);

        var guide = ThemeManager.Stroke("Soft", 1, dotted: true);
        drawing.DrawLine(guide, peak, new Point(peak.X, area.Bottom));
        drawing.DrawLine(guide, off, new Point(off.X, area.Bottom + 8));
        var noteOff = Label(Locale.T("Отпускание клавиши"), "Soft", Font);
        double labelX = Math.Clamp(off.X - noteOff.Width / 2, 7, Math.Max(7, ActualWidth - noteOff.Width - 7));
        drawing.DrawText(noteOff, new Point(labelX, area.Bottom + 17));

        DrawGrip(Grip.Attack, peak, "A");
        DrawGrip(Grip.Decay, hold, "D/S");
        DrawGrip(Grip.Release, tail, "R");

        void DrawGrip(Grip grip, Point center, string text)
        {
            bool active = selected == grip && (IsKeyboardFocused || IsMouseCaptured);
            drawing.DrawEllipse(ThemeManager.Brush(active ? "Mint" : "Panel"), ThemeManager.Stroke("Mint"), center, 15, 15);
            var label = Label(text, active ? "OnMint" : "Mint", GripFont);
            drawing.DrawText(label, new Point(center.X - label.Width / 2, center.Y - label.Height / 2));
        }
    }

    private FormattedText Label(string text, string color, Typeface font) => new(text, Locale.Culture, FlowDirection.LeftToRight,
        font, 11, ThemeManager.Brush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private Grip? Hit(Point point)
    {
        var (peak, hold, _, tail) = Points(Plot);
        if ((point - peak).LengthSquared <= 18 * 18) return Grip.Attack;
        if ((point - hold).LengthSquared <= 18 * 18) return Grip.Decay;
        if ((point - tail).LengthSquared <= 18 * 18) return Grip.Release;
        return null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        origin = e.GetPosition(this);
        if (Hit(origin) is not Grip grip) return;
        Focus();
        if (!CaptureMouse()) return;
        selected = grip;
        dragPatch = patch;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Point point = e.GetPosition(this);
        if (dragPatch is { } original) MoveGrip(original, point - origin);
        else Cursor = Hit(point) switch { Grip.Decay => Cursors.SizeAll, null => Cursors.Arrow, _ => Cursors.SizeWE };
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        dragPatch = null;
        InvalidateVisual();
        base.OnLostMouseCapture(e);
    }

    protected override void OnIsKeyboardFocusedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusedChanged(e);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsMouseCaptured || Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift)) return;
        double step = Keyboard.Modifiers == ModifierKeys.Shift ? 9 : 2;
        switch (e.Key)
        {
            case Key.A: selected = Grip.Attack; break;
            case Key.D: selected = Grip.Decay; break;
            case Key.R: selected = Grip.Release; break;
            case Key.Left: MoveGrip(patch, new Vector(-step, 0)); break;
            case Key.Right: MoveGrip(patch, new Vector(step, 0)); break;
            case Key.Up when selected == Grip.Decay: MoveGrip(patch, new Vector(0, -step)); break;
            case Key.Down when selected == Grip.Decay: MoveGrip(patch, new Vector(0, step)); break;
            default: return;
        }
        InvalidateVisual();
        e.Handled = true;
    }

    private void MoveGrip(SynthPatch original, Vector delta)
    {
        var plot = Plot;
        double horizontal = delta.X / plot.Width;
        var next = selected switch
        {
            Grip.Attack => original with { Attack = Attack.Time(original.Attack, horizontal) },
            Grip.Decay => original with
            {
                Decay = Decay.Time(original.Decay, horizontal),
                Sustain = (float)Math.Clamp(original.Sustain - Math.Round(delta.Y * 100 / plot.Height), 0, 100)
            },
            _ => original with { Release = Release.Time(original.Release, horizontal) }
        };
        Changed?.Invoke(next);
    }
}
