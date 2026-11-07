using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace EazyLoop;

public sealed class EqPlot : ThemedCanvas
{
    private static Brush Surface => ThemeManager.Brush("Surface");
    private static Brush Text => ThemeManager.Brush("Soft");
    private static Pen GridLine => ThemeManager.Stroke("Line");
    private static Pen ZeroLine => ThemeManager.Stroke("StrongLine");
    private static Brush CurveColor => ThemeManager.Brush("Violet");
    private static readonly Brush MarkerText = Ink("#221E31");
    private static readonly Brush[] Colors = [Ink("#81E1CB"), Ink("#BAAAFF"), Ink("#EEA5CD"), Ink("#FFCB89")];
    private static readonly Typeface Font = new("Consolas");
    private static readonly int[] Markers = [30, 100, 300, 1000, 3000, 10000, 18000];
    private static readonly int[] GainMarkers = [-15, -7, 0, 7, 15];
    private EqBand[] bands = FxCatalog.Flat;
    private bool enabled = true;
    private int active = -1;
    internal event Action<int, EqBand>? BandEdited;

    private Rect Plot => new(35, 15, Math.Max(1, ActualWidth - 49), Math.Max(1, ActualHeight - 36));

    internal void Update(EqBand[] value, bool on)
    {
        bands = value;
        enabled = on;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        context.DrawRoundedRectangle(Surface, null, new Rect(RenderSize), 9, 9);
        Rect rect = Plot;
        double middle = rect.Top + rect.Height / 2;
        foreach (int hz in Markers)
        {
            double x = X(hz, rect);
            context.DrawLine(GridLine, new Point(x, rect.Top), new Point(x, rect.Bottom));
            string label = hz >= 1000 ? $"{hz / 1000}k" : hz.ToString(CultureInfo.InvariantCulture);
            var text = Label(label, Text);
            context.DrawText(text, new Point(x - text.Width / 2, rect.Bottom + 4));
        }
        foreach (int db in GainMarkers)
        {
            double y = Y(db, rect);
            context.DrawLine(db == 0 ? ZeroLine : GridLine, new Point(rect.Left, y), new Point(rect.Right, y));
            context.DrawText(Label(db.ToString("+0;-0;0"), Text), new Point(8, y - 6));
        }

        var filters = bands.Select((band, index) => EqFilter.Create(index, band)).ToArray();
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            for (int i = 0; i <= 170; i++)
            {
                double x = rect.Left + rect.Width * i / 170;
                double hz = Hertz(x, rect);
                double db = 0;
                if (enabled)
                    foreach (var filter in filters) db += filter.ResponseDb(hz);
                var point = new Point(x, Math.Clamp(middle - db * rect.Height / 30, rect.Top, rect.Bottom));
                if (i == 0) path.BeginFigure(point, false, false);
                else path.LineTo(point, true, false);
            }
        }
        context.DrawGeometry(null, new Pen(enabled ? CurveColor : Text, 2), geometry);
        for (int i = 0; i < bands.Length; i++)
        {
            var point = Center(i, rect);
            context.DrawEllipse(Colors[i], ThemeManager.Stroke("StrongLine", 1.5), point, 9, 9);
            context.DrawText(Label((i + 1).ToString(), MarkerText), new Point(point.X - 3, point.Y - 6));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Rect rect = Plot;
        Point point = e.GetPosition(this);
        int hit = -1;
        double distance = 17;
        for (int i = 0; i < bands.Length; i++)
        {
            double candidate = (point - Center(i, rect)).Length;
            if (candidate < distance) { hit = i; distance = candidate; }
        }
        if (hit < 0) return;
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            BandEdited?.Invoke(hit, FxCatalog.Flat[hit]);
            return;
        }
        if (CaptureMouse()) active = hit;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (active < 0) return;
        var p = e.GetPosition(this);
        Rect rect = Plot;
        var limits = FxCatalog.BandRange[active];
        float hz = (float)Math.Clamp(Math.Round(Hertz(p.X, rect)), limits.Min, limits.Max);
        float db = (float)Math.Clamp(Math.Round((15 - (p.Y - rect.Top) * 30 / rect.Height) * 2) / 2, -15, 15);
        BandEdited?.Invoke(active, new EqBand(hz, db));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (active >= 0) ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        active = -1;
        base.OnLostMouseCapture(e);
    }

    private Point Center(int index, Rect rect) => new(X(bands[index].Hz, rect), Y(bands[index].Db, rect));
    private static double X(double hz, Rect rect) => rect.Left + Math.Log(hz / 25) / Math.Log(720) * rect.Width;
    private static double Hertz(double x, Rect rect) => 25 * Math.Pow(720, Math.Clamp((x - rect.Left) / rect.Width, 0, 1));
    private static double Y(double db, Rect rect) => rect.Top + (15 - db) / 30 * rect.Height;
    private FormattedText Label(string text, Brush color) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        Font, 10, color, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Brush Ink(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
