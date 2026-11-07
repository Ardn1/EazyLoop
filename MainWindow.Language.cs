using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace EazyLoop;

public partial class MainWindow
{
    private void UpdateLanguageIcon() => LanguageButton.Content = LanguageIcon(Locale.Current);

    private void HeaderPickerClicked(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var menu = button.ContextMenu;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void LanguageMenuOpened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.Items.Clear();
        string[] order = ["ru", "en", "zh-Hans", "hi", "es"];
        foreach (var language in Locale.Languages.OrderBy(item =>
        {
            int index = Array.IndexOf(order, item.Code);
            return index < 0 ? order.Length : index;
        }).ThenBy(item => item.Name))
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock { Text = language == Locale.Current ? "✓" : "", Width = 17 });
            header.Children.Add(new Border { Width = 28, Child = LanguageIcon(language) });
            header.Children.Add(new TextBlock { Text = language.Name, Margin = new Thickness(7, 0, 0, 0) });
            var item = new MenuItem { Header = header, Style = (Style)FindResource("HeaderPickerItem") };
            AutomationProperties.SetName(item, language.Name);
            item.Click += (_, _) => ChangeLanguage(language);
            menu.Items.Add(item);
        }
    }

    private static FrameworkElement LanguageIcon(Locale.Language language) =>
        Flag(language.Flag) is { } flag ? flag : new TextBlock
        {
            Text = language.Code, FontSize = 9, MaxWidth = 29, TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center
        };

    private void ChangeLanguage(Locale.Language language)
    {
        try
        {
            if (language != Locale.Current)
            {
                Locale.Select(language);
                UpdateLanguageIcon();
                UpdateGridLength();
                UpdateProjectHeader();
                RefreshLocalizedInputs();
                foreach (var row in rows) row.RefreshLanguage();
                Refresh();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, Locale.F($"Не удалось сохранить язык. {ex.Message}"),
                Locale.T("Язык интерфейса"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshLocalizedInputs()
    {
        inputDevices = inputDevices.Select(device => device.Id == SoundSession.Default
            ? new AudioDevice(device.Id, Locale.T("По умолчанию")) : device).ToArray();
        populating = true;
        try
        {
            Microphone.ItemsSource = inputDevices;
            Microphone.SelectedValue = options.Input;
            foreach (var row in rows) row.SetInputs(options.Driver, inputDevices);
        }
        finally { populating = false; }
    }

    private static Image? Flag(string code)
    {
        var artwork = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, 22, 14), 1, 1) };
        using (var paint = artwork.Open())
        {
            switch (code)
            {
                case "RU":
                    Block(Brushes.White, 0, 0, 22, 14);
                    Block(Brushes.RoyalBlue, 0, 4.67, 22, 4.67);
                    Block(Brushes.Crimson, 0, 9.33, 22, 4.67);
                    break;
                case "GB":
                    Block(Brushes.MidnightBlue, 0, 0, 22, 14);
                    paint.DrawLine(new Pen(Brushes.White, 3), new Point(0, 0), new Point(22, 14));
                    paint.DrawLine(new Pen(Brushes.White, 3), new Point(0, 14), new Point(22, 0));
                    paint.DrawLine(new Pen(Brushes.Crimson, 1), new Point(0, 0), new Point(22, 14));
                    paint.DrawLine(new Pen(Brushes.Crimson, 1), new Point(0, 14), new Point(22, 0));
                    Block(Brushes.White, 0, 4.5, 22, 5);
                    Block(Brushes.White, 8.5, 0, 5, 14);
                    Block(Brushes.Crimson, 0, 5.7, 22, 2.6);
                    Block(Brushes.Crimson, 9.7, 0, 2.6, 14);
                    break;
                case "CN":
                    Block(Brushes.Red, 0, 0, 22, 14);
                    Star(4.6, 4.6, 2.6);
                    Star(8.4, 2.3, .85);
                    Star(10, 4.1, .85);
                    Star(10, 6.3, .85);
                    Star(8.4, 8, .85);
                    break;
                case "IN":
                    Block(Brushes.White, 0, 0, 22, 14);
                    Block(Brushes.DarkOrange, 0, 0, 22, 4.67);
                    Block(Brushes.ForestGreen, 0, 9.33, 22, 4.67);
                    paint.DrawEllipse(Brushes.White, new Pen(Brushes.Navy, .65), new Point(11, 7), 1.85, 1.85);
                    paint.DrawEllipse(Brushes.Navy, null, new Point(11, 7), .7, .7);
                    break;
                case "ES":
                    Block(Brushes.Crimson, 0, 0, 22, 14);
                    Block(Brushes.Gold, 0, 3.5, 22, 7);
                    Block(Brushes.Firebrick, 5, 5.5, 2.7, 3);
                    break;
                default: return null;
            }

            void Block(Brush fill, double x, double y, double width, double height) =>
                paint.DrawRectangle(fill, null, new Rect(x, y, width, height));

            void Star(double x, double y, double radius)
            {
                var geometry = new StreamGeometry();
                using (var path = geometry.Open())
                    for (int i = 0; i < 10; i++)
                    {
                        double angle = i * Math.PI / 5 - Math.PI / 2;
                        double length = i % 2 == 0 ? radius : radius * .42;
                        var point = new Point(x + length * Math.Cos(angle), y + length * Math.Sin(angle));
                        if (i == 0) path.BeginFigure(point, true, true);
                        else path.LineTo(point, true, false);
                    }
                paint.DrawGeometry(Brushes.Gold, null, geometry);
            }
        }
        artwork.Freeze();
        return new Image { Source = new DrawingImage(artwork), Width = 22, Height = 14 };
    }
}
