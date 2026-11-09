using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace EazyLoop;

public partial class FxWindow
{
    private sealed record PresetEntry(string Name, string? Path);
    private readonly Action<TrackFxSettings> applyPreset;
    private string? presetName;
    private bool refreshingPresets;
    private static string PresetFilter =>
        Locale.F($"Пресеты FX (*{FxPresetStore.Extension})|*{FxPresetStore.Extension}");

    private void FxPresetDropDownOpened(object sender, EventArgs e) => RefreshFxPresets();

    private void RefreshFxPresets(string? selectedPath = null)
    {
        selectedPath ??= (PresetChoice.SelectedItem as PresetEntry)?.Path;
        var entries = new List<PresetEntry> { new(Locale.T("Пресеты FX"), null) };
        try
        {
            if (Directory.Exists(FxPresetStore.Folder))
                entries.AddRange(Directory.GetFiles(FxPresetStore.Folder, "*" + FxPresetStore.Extension)
                    .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.CurrentCultureIgnoreCase)
                    .Select(FileEntry));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var selected = entries.FirstOrDefault(entry => string.Equals(entry.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
        if (selected == null && selectedPath != null && File.Exists(selectedPath))
        {
            selected = FileEntry(selectedPath);
            entries.Add(selected);
        }
        refreshingPresets = true;
        try
        {
            PresetChoice.ItemsSource = entries;
            PresetChoice.SelectedItem = selected ?? entries[0];
        }
        finally { refreshingPresets = false; }
    }

    private static PresetEntry FileEntry(string path) => new(Path.GetFileNameWithoutExtension(path), path);

    private void FxPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ready && !refreshingPresets && PresetChoice.SelectedItem is PresetEntry { Path: { } path })
            LoadFxPreset(path);
    }

    private void SaveFxPresetClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(FxPresetStore.Folder);
            var dialog = new SaveFileDialog
            {
                Title = Locale.T("Сохранить пресет FX"),
                InitialDirectory = FxPresetStore.Folder,
                FileName = (presetName ?? Locale.T("Пресет FX")) + FxPresetStore.Extension,
                Filter = PresetFilter,
                DefaultExt = FxPresetStore.Extension,
                AddExtension = true,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) == true) SaveFxPreset(dialog.FileName);
        }
        catch (Exception ex) { Report(ex); }
    }

    private void SaveFxPreset(string path)
    {
        if (rack.SavePluginStates()) RackEdited();
        var settings = rack.Snapshot() ?? new TrackFxSettings(rack.EqEnabled, rack.Bands, []);
        FxPresetStore.Save(path, settings);
        presetName = Path.GetFileNameWithoutExtension(path);
        RefreshFxPresets(path);
    }

    private void LoadFxPresetClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Locale.T("Загрузить пресет FX"),
            InitialDirectory = FxPresetStore.Folder,
            Filter = PresetFilter,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) LoadFxPreset(dialog.FileName);
    }

    private void LoadFxPreset(string path)
    {
        try
        {
            var settings = FxPresetStore.Load(path);
            applyPreset(settings);
            presetName = Path.GetFileNameWithoutExtension(path);
            RefreshFxPresets(path);
        }
        catch (Exception ex)
        {
            PresetChoice.SelectedIndex = 0;
            Report(ex);
        }
    }
}
