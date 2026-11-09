using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EazyLoop;

internal readonly record struct HsvColor(double Hue, double Saturation, double Value)
{
    public static HsvColor FromRgb(Color color, HsvColor previous = default)
    {
        int max = Math.Max(color.R, Math.Max(color.G, color.B));
        int delta = max - Math.Min(color.R, Math.Min(color.G, color.B));
        if (max == 0) return previous with { Value = 0 };
        if (delta == 0) return new HsvColor(previous.Hue, 0, max / 255.0);

        double hue = 60 * (max == color.R ? (color.G - color.B) / (double)delta :
            max == color.G ? 2 + (color.B - color.R) / (double)delta : 4 + (color.R - color.G) / (double)delta);
        return new HsvColor(NormalizeHue(hue), delta / (double)max, max / 255.0);
    }

    public Color ToRgb()
    {
        double sector = NormalizeHue(Hue) / 60;
        double chroma = Value * Saturation;
        double x = chroma * (1 - Math.Abs(sector % 2 - 1));
        double offset = Value - chroma;
        var (r, g, b) = (int)sector switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x)
        };
        return Color.FromRgb(Channel(r), Channel(g), Channel(b));

        byte Channel(double amount) => (byte)Math.Round(255 * (amount + offset));
    }

    public static double NormalizeHue(double hue) => (hue % 360 + 360) % 360;
}

public sealed class ThemeColorWheel : ThemedCanvas
{
    private static readonly Lazy<BitmapSource> Wheel = new(BuildWheel);
    private static readonly Pen LightRing = Outline(Brushes.White);
    private static readonly Pen DarkRing = Outline(Brushes.Black);
    private HsvColor hsv;
    private Color color;
    private double Radius => Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 8);
    private Point Center => new(ActualWidth / 2, ActualHeight / 2);

    internal event Action<HsvColor>? Changed;

    public ThemeColorWheel()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
    }

    internal void Update(HsvColor value, Color rgb)
    {
        if (hsv == value && color == rgb) return;
        hsv = value;
        color = rgb;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        double radius = Radius;
        if (radius <= 0) return;
        Point center = Center;
        drawing.DrawImage(Wheel.Value, new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2));
        if (IsKeyboardFocused)
            drawing.DrawEllipse(null, ThemeManager.Stroke("Violet", 1.5), center, radius + 3, radius + 3);
        double angle = hsv.Hue * Math.PI / 180;
        double distance = hsv.Saturation * radius;
        var marker = center + new Vector(Math.Cos(angle) * distance, Math.Sin(angle) * distance);
        drawing.DrawEllipse(new SolidColorBrush(color), LightRing, marker, 6, 6);
        drawing.DrawEllipse(null, DarkRing, marker, 7, 7);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Point position = e.GetPosition(this);
        if ((position - Center).Length > Radius + 6) return;
        Focus();
        if (CaptureMouse()) Pick(position);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) Pick(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        Pick(e.GetPosition(this));
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
        if (Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift)) return;
        int step = Keyboard.Modifiers == ModifierKeys.Shift ? 10 : 1;
        HsvColor? next = e.Key switch
        {
            Key.Left => hsv with { Hue = HsvColor.NormalizeHue(hsv.Hue - step) },
            Key.Right => hsv with { Hue = HsvColor.NormalizeHue(hsv.Hue + step) },
            Key.Up => hsv with { Saturation = Math.Min(1, hsv.Saturation + step / 100.0) },
            Key.Down => hsv with { Saturation = Math.Max(0, hsv.Saturation - step / 100.0) },
            _ => null
        };
        if (next == null) return;
        Changed?.Invoke(next.Value);
        e.Handled = true;
    }

    private void Pick(Point point)
    {
        Vector offset = point - Center;
        if (Radius <= 0) return;
        double hue = offset.Length < .5 ? hsv.Hue : HsvColor.NormalizeHue(Math.Atan2(offset.Y, offset.X) * 180 / Math.PI);
        Changed?.Invoke(hsv with { Hue = hue, Saturation = Math.Min(1, offset.Length / Radius) });
    }

    private static BitmapSource BuildWheel()
    {
        const int size = 480, stride = size * 4;
        const double radius = size / 2.0 - .5;
        var pixels = new byte[stride * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double dx = x - radius, dy = y - radius;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                double coverage = Math.Clamp(radius + .5 - distance, 0, 1);
                if (coverage == 0) continue;
                Color rgb = new HsvColor(Math.Atan2(dy, dx) * 180 / Math.PI, Math.Min(1, distance / radius), 1).ToRgb();
                int i = y * stride + x * 4;
                pixels[i] = rgb.B;
                pixels[i + 1] = rgb.G;
                pixels[i + 2] = rgb.R;
                pixels[i + 3] = (byte)Math.Round(coverage * 255);
            }
        var source = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    private static Pen Outline(Brush brush)
    {
        var pen = new Pen(brush, 1.1);
        pen.Freeze();
        return pen;
    }
}
