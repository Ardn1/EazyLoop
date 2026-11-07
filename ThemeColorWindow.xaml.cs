using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EazyLoop;

public partial class ThemeColorWindow : Window
{
    private enum Channel { Red, Green, Blue, Hue, Saturation, Value }
    private sealed record ChannelRow(Channel Channel, Slider Slider, TextBox Field);
    private static readonly Brush HueStrip = MakeHueStrip();
    private readonly ChannelRow[] channels;
    private readonly Action<Color> changed;
    private Color rgb;
    private HsvColor hsv;
    private bool syncing;

    internal ThemeColorWindow(string role, Color initial, Action<Color> changed)
    {
        this.changed = changed;
        rgb = initial;
        hsv = HsvColor.FromRgb(initial);
        InitializeComponent();
        RoleTitle.Text = role;
        channels =
        [
            AddChannel(RgbChannels, Channel.Red, "R", 255),
            AddChannel(RgbChannels, Channel.Green, "G", 255),
            AddChannel(RgbChannels, Channel.Blue, "B", 255),
            AddChannel(HsvChannels, Channel.Hue, "H", 360),
            AddChannel(HsvChannels, Channel.Saturation, "S", 100),
            AddChannel(HsvChannels, Channel.Value, "V", 100)
        ];
        Wheel.Changed += value => SetColor(value.ToRgb(), value);
        HexValue.TextChanged += (_, _) =>
        {
            if (syncing) return;
            if (ThemePalette.TryColor(HexValue.Text, out var color)) SetRgb(color, HexValue);
            else Error(Locale.T("Введите цвет в формате #RRGGBB."));
        };
        WatchField(HexValue);
        Refresh();
    }

    private ChannelRow AddChannel(StackPanel parent, Channel channel, string label, int max)
    {
        var line = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
        var field = new TextBox
        {
            Width = 51, Height = 25, MaxLength = 3, FontFamily = new FontFamily("Consolas"), FontSize = 12,
            TextAlignment = TextAlignment.Center, Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(8, 0, 0, 0)
        };
        DockPanel.SetDock(field, Dock.Right);
        AutomationProperties.SetName(field, label);
        line.Children.Add(field);
        line.Children.Add(new TextBlock { Text = label, FontFamily = new FontFamily("Consolas"), Width = 21 });
        var slider = new Slider
        {
            Maximum = max, SmallChange = 1, LargeChange = max == 100 ? 5 : 15, TickFrequency = 1, IsSnapToTickEnabled = true,
            Style = (Style)FindResource("ChannelSlider")
        };
        AutomationProperties.SetName(slider, label);
        line.Children.Add(slider);
        parent.Children.Add(line);

        slider.ValueChanged += (_, e) => { if (!syncing) SetChannel(channel, (int)Math.Round(e.NewValue)); };
        field.TextChanged += (_, _) =>
        {
            if (syncing) return;
            if (int.TryParse(field.Text, NumberStyles.Integer, Locale.Culture, out int value) && value >= 0 && value <= max)
                SetChannel(channel, value, field);
            else Error(Locale.F($"Введите число от 0 до {max}."));
        };
        WatchField(field);
        return new ChannelRow(channel, slider, field);
    }

    private void WatchField(TextBox field)
    {
        field.LostKeyboardFocus += (_, _) => Refresh();
        field.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (ApplyButton.IsEnabled) Refresh();
            e.Handled = true;
        };
    }

    private void SetChannel(Channel channel, int value, TextBox? editing = null)
    {
        switch (channel)
        {
            case Channel.Red: SetRgb(Color.FromRgb((byte)value, rgb.G, rgb.B), editing); return;
            case Channel.Green: SetRgb(Color.FromRgb(rgb.R, (byte)value, rgb.B), editing); return;
            case Channel.Blue: SetRgb(Color.FromRgb(rgb.R, rgb.G, (byte)value), editing); return;
        }
        var next = channel switch
        {
            Channel.Hue => hsv with { Hue = value },
            Channel.Saturation => hsv with { Saturation = value / 100.0 },
            _ => hsv with { Value = value / 100.0 }
        };
        SetColor(next.ToRgb(), next, editing);
    }

    private void SetRgb(Color color, TextBox? editing) => SetColor(color, HsvColor.FromRgb(color, hsv), editing);

    private void SetColor(Color color, HsvColor next, TextBox? editing = null)
    {
        bool changedRgb = rgb != color;
        rgb = color;
        hsv = next;
        Refresh(editing);
        if (changedRgb) changed(color);
    }

    private void Refresh(TextBox? editing = null)
    {
        syncing = true;
        try
        {
            Wheel.Update(hsv, rgb);
            PreviewColor.Background = new SolidColorBrush(rgb);
            if (HexValue != editing) HexValue.Text = ThemePalette.Hex(rgb);
            foreach (var row in channels)
            {
                int value = (int)Math.Round(row.Channel switch
                {
                    Channel.Red => rgb.R,
                    Channel.Green => rgb.G,
                    Channel.Blue => rgb.B,
                    Channel.Hue => hsv.Hue,
                    Channel.Saturation => hsv.Saturation * 100,
                    _ => hsv.Value * 100
                });
                row.Slider.Value = value;
                row.Slider.Background = Strip(row.Channel);
                if (row.Field != editing) row.Field.Text = value.ToString(Locale.Culture);
            }
        }
        finally { syncing = false; }
        Error("");
    }

    private Brush Strip(Channel channel) => channel switch
    {
        Channel.Red => Gradient(Color.FromRgb(0, rgb.G, rgb.B), Color.FromRgb(255, rgb.G, rgb.B)),
        Channel.Green => Gradient(Color.FromRgb(rgb.R, 0, rgb.B), Color.FromRgb(rgb.R, 255, rgb.B)),
        Channel.Blue => Gradient(Color.FromRgb(rgb.R, rgb.G, 0), Color.FromRgb(rgb.R, rgb.G, 255)),
        Channel.Hue => HueStrip,
        Channel.Saturation => Gradient((hsv with { Saturation = 0 }).ToRgb(), (hsv with { Saturation = 1 }).ToRgb()),
        _ => Gradient(Colors.Black, (hsv with { Value = 1 }).ToRgb())
    };

    private static Brush Gradient(Color start, Color end)
    {
        var brush = new LinearGradientBrush(start, end, 0);
        brush.Freeze();
        return brush;
    }

    private static Brush MakeHueStrip()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, .5), EndPoint = new Point(1, .5) };
        for (int i = 0; i <= 6; i++)
            brush.GradientStops.Add(new GradientStop(new HsvColor(i * 60, 1, 1).ToRgb(), i / 6.0));
        brush.Freeze();
        return brush;
    }

    private void Error(string message)
    {
        Validation.Text = message;
        ApplyButton.IsEnabled = message.Length == 0;
    }

    private void ApplyClicked(object sender, RoutedEventArgs e)
    {
        if (ApplyButton.IsEnabled) DialogResult = true;
    }
}
