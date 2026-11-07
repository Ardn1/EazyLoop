using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace EazyLoop;

internal sealed class ProjectNameWindow : Window
{
    public string? Destination { get; private set; }

    public ProjectNameWindow(Window owner, string title, string folder)
    {
        Owner = owner;
        Title = title;
        Width = 465;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Background");
        SetResourceReference(ForegroundProperty, "Ink");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        var root = new StackPanel { Margin = new Thickness(18) };
        Content = root;
        root.Children.Add(new TextBlock { Text = Locale.T("Название проекта"), FontSize = 17, FontWeight = FontWeights.SemiBold });
        var name = new TextBox { Height = 33, Margin = new Thickness(0, 7, 0, 6), MaxLength = 100,
            Text = SuggestedName(folder) };
        AutomationProperties.SetName(name, Locale.T("Название проекта"));
        root.Children.Add(name);
        var location = new TextBlock { Text = folder, ToolTip = folder, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
        location.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        root.Children.Add(location);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        error.SetResourceReference(TextBlock.ForegroundProperty, "Rose");
        root.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = Locale.T("Отмена"), IsCancel = true };
        var create = new Button { Content = Locale.T("Создать"), IsDefault = true, Margin = new Thickness(7, 0, 0, 0) };
        create.Click += (_, _) =>
        {
            try
            {
                Destination = ProjectStore.NewDestination(folder, name.Text);
                DialogResult = true;
            }
            catch (IOException ex)
            {
                error.Text = ex.Message;
                error.Visibility = Visibility.Visible;
                name.Focus();
            }
        };
        name.TextChanged += (_, _) => error.Visibility = Visibility.Collapsed;
        actions.Children.Add(cancel);
        actions.Children.Add(create);
        root.Children.Add(actions);
        Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
    }

    private static string SuggestedName(string folder)
    {
        string name = Locale.T("Новый проект");
        string candidate = name;
        for (int i = 2; Directory.Exists(Path.Combine(folder, candidate)) || File.Exists(Path.Combine(folder, candidate)); i++)
            candidate = $"{name} {i}";
        return candidate;
    }
}
