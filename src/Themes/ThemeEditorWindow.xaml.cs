using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EazyLoop;

public partial class ThemeEditorWindow : Window
{
    private sealed class PaletteRow(string label, Color initial) : INotifyPropertyChanged
    {
        private Color color = initial;
        public string Label { get; } = label;
        public string Hex => ThemePalette.Hex(color);
        public SolidColorBrush Brush => new(color);
        public Color Color
        {
            get => color;
            set
            {
                if (value == color) return;
                color = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hex)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Brush)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private const string ColorDragFormat = "EazyLoop.ThemeColor";
    private static readonly string[] Roles = ["Фон окна", "Панели", "Поля и кнопки", "Текст", "Акцент", "Курсор воспроизведения"];
    private readonly UiTheme? existing;
    private readonly PaletteRow[] rows;
    private ThemePalette previewed;
    private Button? pressed;
    private PaletteRow? dragging;
    private Border? highlighted;
    private Point pressPoint;
    private bool ready;

    internal ThemeEditorWindow(UiTheme? existing)
    {
        this.existing = existing;
        previewed = (existing ?? ThemeManager.Current).Palette;
        rows = previewed.Values.Select((value, index) =>
        {
            ThemePalette.TryColor(value, out var color);
            return new PaletteRow(Locale.T(Roles[index]), color);
        }).ToArray();
        InitializeComponent();
        PaletteRows.ItemsSource = rows;
        ThemeName.Text = existing?.Name ?? "";
        ready = true;
        UpdatePreview();
        Loaded += (_, _) => { ThemeName.Focus(); ThemeName.SelectAll(); };
        Closed += (_, _) =>
        {
            if (previewed != ThemeManager.Current.Palette) ThemeManager.Preview(ThemeManager.Current.Palette);
        };
    }

    private void NameChanged(object sender, TextChangedEventArgs e)
    {
        if (ready) UpdatePreview();
    }

    private void ColorClicked(object sender, RoutedEventArgs e)
    {
        pressed = null;
        if (sender is not Button { DataContext: PaletteRow row }) return;
        Color original = row.Color;
        ThemePalette originalPreview = previewed;
        var picker = new ThemeColorWindow(row.Label, original, color => SetColor(row, color)) { Owner = this };
        if (picker.ShowDialog() == true) return;
        row.Color = original;
        if (previewed != originalPreview)
        {
            ThemeManager.Preview(originalPreview);
            previewed = originalPreview;
        }
        UpdatePreview();
    }

    private void SetColor(PaletteRow row, Color color)
    {
        if (row.Color == color) return;
        row.Color = color;
        UpdatePreview();
    }

    private void ColorPressed(object sender, MouseButtonEventArgs e)
    {
        pressed = (Button)sender;
        pressPoint = e.GetPosition(this);
    }

    private void ColorMoved(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { pressed = null; return; }
        if (sender != pressed || pressed is not { DataContext: PaletteRow row } source) return;
        Vector move = e.GetPosition(this) - pressPoint;
        if (Math.Abs(move.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(move.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        pressed = null;
        source.ReleaseMouseCapture();
        e.Handled = true;
        dragging = row;
        try { DragDrop.DoDragDrop(source, new DataObject(ColorDragFormat, row.Hex), DragDropEffects.Copy | DragDropEffects.Move); }
        finally
        {
            dragging = null;
            Highlight(null);
        }
    }

    private void ColorDragOver(object sender, DragEventArgs e)
    {
        var target = (Border)sender;
        e.Effects = DragEffect(target, e);
        Highlight(e.Effects == DragDropEffects.None ? null : target);
        e.Handled = true;
    }

    private void ColorDragLeave(object sender, DragEventArgs e)
    {
        if (sender == highlighted) Highlight(null);
    }

    private void ColorDropped(object sender, DragEventArgs e)
    {
        var target = (Border)sender;
        Highlight(null);
        e.Effects = DragEffect(target, e);
        e.Handled = true;
        if (e.Effects == DragDropEffects.None) return;
        TransferColor(dragging!, (PaletteRow)target.DataContext, e.Effects == DragDropEffects.Move);
    }

    private void TransferColor(PaletteRow source, PaletteRow target, bool swap)
    {
        var old = target.Color;
        target.Color = source.Color;
        if (swap) source.Color = old;
        UpdatePreview();
    }

    private DragDropEffects DragEffect(Border target, DragEventArgs e)
    {
        if (dragging == null || target.DataContext is not PaletteRow row || row == dragging ||
            !e.Data.GetDataPresent(ColorDragFormat, autoConvert: false))
            return DragDropEffects.None;
        var effect = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0 ? DragDropEffects.Move : DragDropEffects.Copy;
        return effect & e.AllowedEffects;
    }

    private void Highlight(Border? target)
    {
        if (highlighted == target) return;
        highlighted?.SetResourceReference(Border.BorderBrushProperty, "Line");
        highlighted = target;
        highlighted?.SetResourceReference(Border.BorderBrushProperty, "Violet");
    }

    private void UpdatePreview()
    {
        var palette = new ThemePalette(rows[0].Hex, rows[1].Hex, rows[2].Hex, rows[3].Hex, rows[4].Hex, rows[5].Hex);
        if (!palette.IsReadable())
        {
            Validation.Text = Locale.T("Подберите более контрастные цвета текста и фона.");
            SaveButton.IsEnabled = false;
            return;
        }
        if (palette != previewed)
        {
            ThemeManager.Preview(palette);
            previewed = palette;
        }
        Validation.Text = "";
        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(ThemeName.Text);
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (!SaveButton.IsEnabled) return;
        try
        {
            ThemeManager.SaveCustom(new UiTheme(existing?.Id ?? Guid.NewGuid().ToString("N"), ThemeName.Text.Trim(), previewed));
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Validation.Text = Locale.T("Не удалось сохранить тему.");
        }
    }
}
