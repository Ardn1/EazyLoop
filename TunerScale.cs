using System.Windows;
using System.Windows.Media;

namespace EazyLoop;

public sealed class TunerScale : ThemedCanvas
{
    internal const int Tolerance = 3;
    private double? cents;
    public bool Compact { get; set; }

    internal static bool IsTuned(double deviation) => Math.Abs(Math.Round(deviation)) <= Tolerance;

    internal void Update(double? deviation)
    {
        if (cents == deviation) return;
        cents = deviation;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        if (Compact)
        {
            DrawCompact(drawing);
            return;
        }
        const int steps = 10;
        const double inset = 6;
        double width = ActualWidth - inset * 2, height = ActualHeight;
        if (width <= 0 || height <= 8) return;

        double center = ActualWidth / 2, middle = height / 2, spacing = width / (2 * steps);
        double barWidth = Math.Clamp(spacing * .38, 2, 5);
        var inactive = ThemeManager.Brush("StrongLine");
        var centerMark = ThemeManager.Brush("Muted");
        var color = ThemeManager.Brush(cents is double offset && IsTuned(offset) ? "Mint" : "Warning");
        double marker = cents.GetValueOrDefault() / 5;
        drawing.DrawRoundedRectangle(ThemeManager.Brush("MintShade"), null,
            new Rect(center - spacing * .65, 0, spacing * 1.3, height), 4, 4);
        drawing.DrawLine(ThemeManager.Stroke("Line"), new Point(inset, middle), new Point(inset + width, middle));
        for (int step = -steps; step <= steps; step++)
        {
            double barHeight = step == 0 ? height - 4 : step % 2 == 0 ? height * .58 : height * .37;
            bool lit = cents.HasValue && step >= Math.Min(0, marker) && step <= Math.Max(0, marker);
            var rect = new Rect(center + step * spacing - barWidth / 2, middle - barHeight / 2, barWidth, barHeight);
            drawing.DrawRoundedRectangle(lit ? color : step == 0 ? centerMark : inactive, null, rect, 2, 2);
        }

        if (cents.HasValue)
        {
            double x = center + marker * spacing;
            drawing.DrawRoundedRectangle(color, ThemeManager.Stroke("Surface", 1.2),
                new Rect(x - 3.5, 1, 7, height - 2), 3.5, 3.5);
        }
    }

    private void DrawCompact(DrawingContext drawing)
    {
        const double inset = 3.5;
        double width = ActualWidth - inset * 2, middle = ActualHeight / 2;
        if (width <= 0 || middle < 4) return;
        double center = ActualWidth / 2;
        drawing.DrawRoundedRectangle(ThemeManager.Brush("StrongLine"), null, new Rect(inset, middle - 1.5, width, 3), 1.5, 1.5);
        drawing.DrawLine(ThemeManager.Stroke("Muted"), new Point(center, middle - 3.5), new Point(center, middle + 3.5));
        if (cents is not double deviation) return;
        double x = center + Math.Clamp(deviation, -50, 50) * width / 100;
        var color = ThemeManager.Brush(IsTuned(deviation) ? "Mint" : "Warning");
        drawing.DrawRectangle(color, null, new Rect(Math.Min(center, x), middle - 1.5, Math.Abs(x - center), 3));
        drawing.DrawEllipse(color, null, new Point(x, middle), 3, 3);
    }
}
