using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace EazyLoop;

public partial class MainWindow
{
    private void ThemeMenuOpened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.Items.Clear();
        AddAction(Locale.T("Создать свою…"), () => EditTheme(null));
        if (ThemeManager.Current.IsCustom)
            AddAction(Locale.T("Редактировать тему…"), () => EditTheme(ThemeManager.Current));
        menu.Items.Add(new Separator());

        foreach (var theme in ThemeManager.All)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock { Text = theme == ThemeManager.Current ? "✓" : "", Width = 17 });
            foreach (string value in new[] { theme.Palette.Background, theme.Palette.Field, theme.Palette.Accent })
            {
                ThemePalette.TryColor(value, out var color);
                var swatch = new Border { Background = new SolidColorBrush(color), Width = 6, Height = 14,
                    CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 2, 0) };
                swatch.SetResourceReference(Border.BorderBrushProperty, "StrongLine");
                header.Children.Add(swatch);
            }
            header.Children.Add(new TextBlock { Text = theme.DisplayName, Margin = new Thickness(7, 0, 0, 0) });
            var item = new MenuItem { Header = header, Style = (Style)FindResource("HeaderPickerItem") };
            AutomationProperties.SetName(item, theme.DisplayName);
            item.Click += (_, _) => ApplyTheme(theme);
            menu.Items.Add(item);
        }
        void AddAction(string title, Action run)
        {
            var item = new MenuItem { Header = title, Style = (Style)FindResource("HeaderPickerItem") };
            item.Click += (_, _) => Dispatcher.BeginInvoke(run);
            menu.Items.Add(item);
        }
    }

    private void ApplyTheme(UiTheme theme)
    {
        try
        {
            ThemeManager.Select(theme);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, Locale.T("Не удалось сохранить тему."), Locale.T("Тема оформления"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void EditTheme(UiTheme? theme)
    {
        var editor = new ThemeEditorWindow(theme) { Owner = this };
        editor.ShowDialog();
    }
}
