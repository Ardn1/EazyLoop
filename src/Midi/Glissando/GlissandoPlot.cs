using System.Collections.Immutable;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace EazyLoop;

internal sealed class GlissandoPlot : ThemedCanvas
{
    private static readonly Typeface Font = new("Segoe UI");
    private static Brush panel => ThemeManager.Brush("Panel");
    private static Brush muted => ThemeManager.Brush("Muted");
    private static Brush violet => ThemeManager.Brush("Violet");
    private static Brush mint => ThemeManager.Brush("Mint");
    private static Pen grid => ThemeManager.Stroke("Line");
    private static Pen zero => ThemeManager.Stroke("Muted", 1, dotted: true);
    private static Pen trace => ThemeManager.Stroke("Violet", 2.2);
    private GlissandoCurve curve = GlissandoCurve.Default;
    private int selected = -1;
    private int? strokePosition;

    public bool Pencil { get; set; }
    public bool Snap { get; set; } = true;
    public event Action<GlissandoCurve>? Changed;
    public event Action<GlissandoPoint?>? SelectionChanged;

    public GlissandoPlot()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
    }

    private Rect Graph => new(42, 14, Math.Max(1, ActualWidth - 65), Math.Max(1, ActualHeight - 43));

    public void Update(GlissandoCurve next)
    {
        curve = next;
        if (selected >= curve.Points.Length) selected = -1;
        Select(selected);
    }

    protected override void OnRender(DrawingContext context)
    {
        var area = Graph;
        context.DrawRoundedRectangle(panel, grid, new Rect(.5, .5, Math.Max(1, ActualWidth - 1),
            Math.Max(1, ActualHeight - 1)), 8, 8);
        for (int semitones = -24; semitones <= 24; semitones += 3)
        {
            double y = Pixel(0, semitones * 100).Y;
            context.DrawLine(semitones == 0 ? zero : grid, new Point(area.Left, y), new Point(area.Right, y));
            if (semitones % 6 == 0)
            {
                var label = Label(semitones.ToString("+0;−0;0", Locale.Culture));
                context.DrawText(label, new Point(area.Left - label.Width - 7, y - label.Height / 2));
            }
        }
        for (int i = 0; i <= 4; i++)
        {
            double x = area.Left + area.Width * i / 4;
            context.DrawLine(grid, new Point(x, area.Top), new Point(x, area.Bottom));
            var label = Label($"{i * 25}%");
            context.DrawText(label, new Point(x - label.Width / 2, area.Bottom + 8));
        }

        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            var points = curve.Points;
            int segment = 0;
            path.BeginFigure(Pixel(points[0].Position, points[0].Cents), false, false);
            for (int i = 1; i < points.Length; i++)
            {
                int start = points[i - 1].Position, width = points[i].Position - start;
                int steps = Math.Max(1, (int)Math.Ceiling(area.Width * width / GlissandoCurve.End / 3));
                for (int j = 1; j <= steps; j++)
                {
                    double position = start + (double)width * j / steps;
                    path.LineTo(Pixel(position, curve.CentsAt(position, ref segment)), true, true);
                }
            }
        }
        context.DrawGeometry(null, trace, geometry);
        for (int i = 0; i < curve.Points.Length; i++)
        {
            var point = curve.Points[i];
            double radius = i == selected ? 5.5 : 3.5;
            context.DrawEllipse(i == selected ? mint : violet, grid, Pixel(point.Position, point.Cents), radius, radius);
        }
    }

    private FormattedText Label(string text) => new(text, Locale.Culture, FlowDirection.LeftToRight, Font,
        10, muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private Point Pixel(double position, double cents)
    {
        var area = Graph;
        return new Point(area.Left + area.Width * position / GlissandoCurve.End,
            area.Top + area.Height * (GlissandoCurve.PitchLimit - cents) / (2 * GlissandoCurve.PitchLimit));
    }

    private GlissandoPoint Read(Point mouse)
    {
        var area = Graph;
        double time = Math.Clamp((mouse.X - area.Left) / area.Width, 0, 1);
        double height = Math.Clamp((mouse.Y - area.Top) / area.Height, 0, 1);
        int step = Snap ? 100 : 1;
        return new((int)Math.Round(time * GlissandoCurve.End),
            (int)Math.Round((1 - 2 * height) * GlissandoCurve.PitchLimit / step) * step);
    }

    private int Hit(Point mouse)
    {
        double nearest = 9 * 9;
        int hit = -1;
        for (int i = 0; i < curve.Points.Length; i++)
        {
            var point = curve.Points[i];
            double distance = (Pixel(point.Position, point.Cents) - mouse).LengthSquared;
            if (distance > nearest) continue;
            nearest = distance;
            hit = i;
        }
        return hit;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Point mouse = e.GetPosition(this);
        int hit = Hit(mouse);
        if (hit < 0 && !Graph.Contains(mouse)) return;
        Focus();
        if (!CaptureMouse()) return;
        var point = Read(mouse);
        if (Pencil)
        {
            strokePosition = point.Position;
            DrawPoint(point, true);
        }
        else if (hit >= 0) Select(hit);
        else
        {
            int i = 0;
            while (curve.Points[i].Position < point.Position) i++;
            if (curve.Points[i].Position == point.Position) Select(i);
            else if (curve.Points.Length < GlissandoCurve.MaxPoints) Change(curve.Points.Insert(i, point), i);
            else Select(-1);
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured) return;
        var point = Read(e.GetPosition(this));
        if (strokePosition != null) DrawPoint(point, false);
        else MovePoint(point);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        if (strokePosition != null) DrawPoint(Read(e.GetPosition(this)), true);
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        strokePosition = null;
        base.OnLostMouseCapture(e);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured) return;
        int hit = Hit(e.GetPosition(this));
        if (hit < 0) return;
        Focus();
        Select(hit);
        DeletePoint();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (selected < 0 || IsMouseCaptured || Keyboard.Modifiers != ModifierKeys.None) return;
        var point = curve.Points[selected];
        int step = Snap ? 100 : 1;
        switch (e.Key)
        {
            case Key.Left: MovePoint(point with { Position = point.Position - 1 }); break;
            case Key.Right: MovePoint(point with { Position = point.Position + 1 }); break;
            case Key.Up: MovePoint(point with { Cents = point.Cents + step }); break;
            case Key.Down: MovePoint(point with { Cents = point.Cents - step }); break;
            case Key.Delete: DeletePoint(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void MovePoint(GlissandoPoint point)
    {
        if (selected < 0) return;
        var points = curve.Points;
        int position = selected == 0 || selected == points.Length - 1 ? points[selected].Position :
            Math.Clamp(point.Position, points[selected - 1].Position + 1, points[selected + 1].Position - 1);
        var next = new GlissandoPoint(position, Math.Clamp(point.Cents, -GlissandoCurve.PitchLimit, GlissandoCurve.PitchLimit));
        if (next != points[selected]) Change(points.SetItem(selected, next), selected);
    }

    private void DrawPoint(GlissandoPoint point, bool force)
    {
        int previous = strokePosition!.Value;
        if (!force && Math.Abs(point.Position - previous) < 18) return;
        int left = Math.Min(previous, point.Position), right = Math.Max(previous, point.Position);
        var points = curve.Points.RemoveAll(p => p.Position > left && p.Position < right);
        int i = 0;
        while (points[i].Position < point.Position) i++;
        if (points[i].Position == point.Position)
        {
            if (points[i] == point) { Select(i); strokePosition = point.Position; return; }
            points = points.SetItem(i, point);
        }
        else if (points.Length < GlissandoCurve.MaxPoints) points = points.Insert(i, point);
        else return;
        strokePosition = point.Position;
        Change(points, i);
    }

    private void DeletePoint()
    {
        if (selected > 0 && selected < curve.Points.Length - 1) Change(curve.Points.RemoveAt(selected), -1);
    }

    private void Select(int index)
    {
        selected = index;
        SelectionChanged?.Invoke(index < 0 ? null : curve.Points[index]);
        InvalidateVisual();
    }

    private void Change(ImmutableArray<GlissandoPoint> points, int selection)
    {
        selected = selection;
        var next = new GlissandoCurve(points, curve.Smoothing);
        Update(next);
        Changed?.Invoke(next);
    }
}
