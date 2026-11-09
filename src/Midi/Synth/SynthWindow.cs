using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace EazyLoop;

internal sealed class SynthWindow : Window
{
    private sealed record Preset(string Name, SynthPatch? Patch, string? FilePath = null)
    {
        public string Label => FilePath == null ? Locale.T(Name) : Locale.F($"{Name} · файл");
    }

    private static string PresetFilter =>
        Locale.F($"Тембры SimpleSynth (*{SynthPresetStore.Extension})|*{SynthPresetStore.Extension}");
    private static readonly Preset[] Presets =
    [
        new("Свой тембр", null), new("Мягкий треугольник", SynthPatch.Default),
        new("Саб-бас", new(WaveShape.Sine, 4, 110, 86, 95, 40)),
        new("Щипок", new(WaveShape.Triangle, 2, 230, 12, 190, 85)),
        new("Лид", new(WaveShape.Saw, 6, 130, 76, 155, 71)),
        new("Тёплый пэд", new(WaveShape.Saw, 480, 630, 74, 1150, 27)),
        new("Квадрат 8-bit", new(WaveShape.Square, 1, 65, 83, 48, 100)),
        new("Мягкий орган", new(WaveShape.Square, 12, 160, 100, 110, 32))
    ];

    public SynthWindow(MidiTrack track, Action edited)
    {
        string instrument = track.Timeline != null ? "Glissando Synth" : "SimpleSynth";
        Title = track.Timeline != null ? $"{instrument} · {Locale.T("Тембр")}" : Locale.T("SimpleSynth · тембр");
        Width = 876;
        MaxHeight = SystemParameters.WorkArea.Height;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Background");
        SetResourceReference(ForegroundProperty, "Ink");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        var layout = new StackPanel { Margin = new Thickness(18) };
        Content = new ScrollViewer { Content = layout, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        layout.Children.Add(new TextBlock { Text = instrument, FontSize = 22, FontWeight = FontWeights.SemiBold });
        layout.Children.Add(AxisLabel(Locale.T("Редактор тембра")));
        var history = new EditHistory<SynthPatch>(() => track.Patch!, track.ChangePatch, (a, b) => a == b);

        var toolbar = new DockPanel { Margin = new Thickness(0, 14, 0, 12) };
        layout.Children.Add(toolbar);
        var audition = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(audition, Dock.Right);
        toolbar.Children.Add(audition);
        var undo = HistoryButton(1, Locale.T("Отменить · Ctrl+Z"));
        var redo = HistoryButton(-1, Locale.T("Повторить · Ctrl+Y или Ctrl+Shift+Z"));
        audition.Children.Add(undo);
        audition.Children.Add(redo);
        var notes = new ToggleButton
        {
            Content = Locale.T("Ноты…"), Height = 33, Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 0, 6, 0), Style = (Style)FindResource("PillToggle")
        };
        audition.Children.Add(notes);
        AddNotePad(audition, notes, track);
        string playText = Locale.T("▶ Сыграть A4");
        var play = PresetButton(playText, Locale.T("Прослушать огибающую и тембр на ноте A4."));
        var playLabel = new TextBlock { Text = playText };
        playLabel.SetResourceReference(TextBlock.ForegroundProperty, "OnAccent");
        play.Content = playLabel;
        AutomationProperties.SetName(play, playText);
        play.Height = 33;
        play.SetResourceReference(BackgroundProperty, "Violet");
        play.SetResourceReference(BorderBrushProperty, "Violet");
        play.Click += (_, _) => track.PreviewEnvelope();
        audition.Children.Add(play);

        var presetActions = new StackPanel { Orientation = Orientation.Horizontal };
        toolbar.Children.Add(presetActions);
        presetActions.Children.Add(AxisLabel(Locale.T("Пресет")));
        var presets = new ComboBox { DisplayMemberPath = nameof(Preset.Label), Height = 33, Width = 215,
            Margin = new Thickness(8, 0, 7, 0) };
        AutomationProperties.SetName(presets, Locale.T("Пресет тембра"));
        presetActions.Children.Add(presets);
        string nameToSave = Presets[0].Label;
        var save = PresetButton(Locale.T("Сохранить…"), Locale.T("Назвать и сохранить текущий тембр в файл"));
        var load = PresetButton(Locale.T("Загрузить…"), Locale.F($"Открыть файл тембра. Стандартная папка: {SynthPresetStore.Folder}"));
        load.Margin = new Thickness(6, 0, 0, 0);
        save.Click += (_, _) => PresetFileAction(Locale.T("Сохранение тембра"), SavePreset);
        load.Click += (_, _) => PresetFileAction(Locale.T("Загрузка тембра"), LoadPreset);
        presetActions.Children.Add(save);
        presetActions.Children.Add(load);

        var panels = TwoColumns();
        layout.Children.Add(panels);
        var tonePanel = new StackPanel();
        var toneCard = Card(tonePanel);
        panels.Children.Add(toneCard);
        var envPanel = new StackPanel();
        var envCard = Card(envPanel);
        Grid.SetColumn(envCard, 2);
        panels.Children.Add(envCard);
        var brightness = AxisLabel("");
        tonePanel.Children.Add(PanelHeader(Locale.T("Тембр"), brightness));
        var tone = new SynthTonePad { Height = 226,
            ToolTip = Locale.T("По горизонтали смешиваются формы волны. По вертикали меняется яркость.") };
        AutomationProperties.SetName(tone, Locale.T("Площадка тембра"));
        history.WatchDrag(tone);
        tonePanel.Children.Add(tone);
        var waveform = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
        var preview = new SynthWavePreview { Width = 94, Height = 36, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(preview, Locale.T("Превью звуковой волны"));
        waveform.Children.Add(preview);
        var waveLabel = AxisLabel("");
        waveLabel.TextWrapping = TextWrapping.Wrap;
        waveform.Children.Add(waveLabel);
        tonePanel.Children.Add(waveform);

        envPanel.Children.Add(PanelHeader(Locale.T("Огибающая"), AxisLabel("ADSR")));
        var envelope = new SynthEnvelopePlot { Height = 202, ToolTip = Locale.T(
            "Тяните A и R по горизонтали. D/S меняет спад по горизонтали и поддержку по вертикали.") };
        AutomationProperties.SetName(envelope, Locale.T("Огибающая ADSR"));
        history.WatchDrag(envelope);
        envPanel.Children.Add(envelope);
        var stages = new UniformGrid { Columns = 4, Margin = new Thickness(1, 7, 0, 0) };
        var attack = Stage("Атака");
        var decay = Stage("Спад");
        var sustain = Stage("Поддержка");
        var release = Stage("Отпускание");
        envPanel.Children.Add(stages);

        var exact = new Expander { Header = Locale.T("Точные настройки"), Margin = new Thickness(0, 11, 0, 0) };
        exact.SetResourceReference(ForegroundProperty, "Soft");
        var parameters = TwoColumns();
        parameters.Margin = new Thickness(6, 4, 6, 0);
        exact.Content = parameters;
        layout.Children.Add(exact);
        var left = new StackPanel();
        var right = new StackPanel();
        Grid.SetColumn(right, 2);
        parameters.Children.Add(left);
        parameters.Children.Add(right);
        var sliders = new List<(Slider Slider, Func<SynthPatch, double> Read)>();
        bool syncing = false;
        string ms = Locale.T("мс");
        Add(left, "Форма волны", 0, 300, p => p.WavePosition, x => $"{x / 3:0.#} %",
            (p, x) => p.WithWavePosition(x));
        Add(left, "Яркость", 0, 100, p => p.Tone, x => $"{x:0.#} %", (p, x) => p with { Tone = x });
        Add(left, "Атака", 1, 1500, p => p.Attack, Time, (p, x) => p with { Attack = x });
        Add(right, "Спад", 5, 2000, p => p.Decay, Time, (p, x) => p with { Decay = x });
        Add(right, "Поддержка", 0, 100, p => p.Sustain, x => $"{x:0.#} %", (p, x) => p with { Sustain = x });
        Add(right, "Отпускание", 5, 3000, p => p.Release, Time, (p, x) => p with { Release = x });
        tone.Changed += Change;
        envelope.Changed += Change;
        undo.Click += (_, _) => StepHistory(false);
        redo.Click += (_, _) => StepHistory(true);
        history.Changed += () => { undo.IsEnabled = history.CanUndo; redo.IsEnabled = history.CanRedo; };
        PreviewKeyDown += (_, e) => HistoryKeys.Handle(e, StepHistory);
        presets.SelectionChanged += (_, _) =>
        {
            if (presets.SelectedItem is not Preset { Patch: { } patch } preset) return;
            nameToSave = preset.FilePath == null ? preset.Label : preset.Name;
            if (patch == track.Patch) return;
            track.ChangePatch(patch);
            Sync();
            history.Commit();
            edited();
        };
        presets.DropDownOpened += (_, _) => RefreshPresets();
        RefreshPresets();
        Sync();

        string Time(double value) => $"{value:0.#} {ms}";

        void StepHistory(bool forward)
        {
            if (!history.Step(forward)) return;
            var choices = (IEnumerable<Preset>)presets.ItemsSource;
            presets.SelectedItem = choices.FirstOrDefault(preset => preset.Patch == track.Patch) ?? Presets[0];
            Sync();
            edited();
        }

        TextBlock Stage(string key)
        {
            var stage = new StackPanel { Margin = new Thickness(0, 0, 3, 0) };
            var label = AxisLabel(Locale.T(key));
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.ToolTip = label.Text;
            stage.Children.Add(label);
            var value = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 13 };
            stage.Children.Add(value);
            stages.Children.Add(stage);
            return value;
        }

        void Add(Panel parent, string name, double min, double max, Func<SynthPatch, double> read,
            Func<double, string> format, Func<SynthPatch, float, SynthPatch> set)
        {
            parent.Children.Add(FxWindow.ParameterLine(Locale.T(name), min, max, read(track.Patch!), format,
                value => { if (!syncing) Change(set(track.Patch!, (float)value)); },
                control =>
                {
                    var slider = (Slider)control;
                    slider.SmallChange = 1;
                    sliders.Add((slider, read));
                    history.WatchDrag(slider);
                }));
        }

        void Sync()
        {
            var patch = track.Patch!;
            tone.Update(patch);
            preview.Update(patch);
            envelope.Update(patch);
            brightness.Text = Locale.F($"Яркость {patch.Tone:0.#}%");
            waveLabel.Text = SynthTonePad.WaveLabel(patch);
            attack.Text = Time(patch.Attack);
            decay.Text = Time(patch.Decay);
            sustain.Text = $"{patch.Sustain:0.#} %";
            release.Text = Time(patch.Release);
            syncing = true;
            try
            {
                foreach (var (slider, read) in sliders) slider.Value = read(patch);
            }
            finally { syncing = false; }
        }

        void RefreshPresets(Preset? selected = null)
        {
            var current = presets.SelectedItem as Preset;
            var choices = new List<Preset>(Presets);
            try
            {
                if (Directory.Exists(SynthPresetStore.Folder))
                    foreach (string path in Directory.GetFiles(SynthPresetStore.Folder, "*" + SynthPresetStore.Extension)
                        .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.CurrentCultureIgnoreCase))
                    {
                        if (string.Equals(path, selected?.FilePath, StringComparison.OrdinalIgnoreCase))
                            choices.Add(selected!);
                        else
                        {
                            try { choices.Add(ReadPreset(path)); }
                            catch (Exception ex) when (IsPresetFileError(ex)) { }
                        }
                    }
            }
            catch (Exception ex) when (IsPresetFileError(ex)) { }

            if (selected == null && current?.FilePath is { } currentPath &&
                !string.Equals(Path.GetDirectoryName(currentPath), SynthPresetStore.Folder, StringComparison.OrdinalIgnoreCase))
                choices.Add(current);
            if (selected != null && !choices.Contains(selected)) choices.Add(selected);

            selected ??= current == null ? choices.FirstOrDefault(preset => preset.Patch == track.Patch) :
                choices.FirstOrDefault(preset => preset == current);
            if (presets.ItemsSource is not IEnumerable<Preset> previous || !choices.SequenceEqual(previous))
                presets.ItemsSource = choices;
            presets.SelectedItem = selected ?? Presets[0];
        }

        void SavePreset()
        {
            Directory.CreateDirectory(SynthPresetStore.Folder);
            var dialog = new SaveFileDialog
            {
                Title = Locale.T("Сохранить тембр SimpleSynth под именем"),
                InitialDirectory = SynthPresetStore.Folder,
                FileName = nameToSave + SynthPresetStore.Extension,
                Filter = PresetFilter,
                DefaultExt = SynthPresetStore.Extension,
                AddExtension = true,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != true) return;
            var patch = track.Patch!;
            SynthPresetStore.Save(dialog.FileName, patch);
            RefreshPresets(new Preset(Path.GetFileNameWithoutExtension(dialog.FileName), patch, dialog.FileName));
        }

        void LoadPreset()
        {
            var dialog = new OpenFileDialog
            {
                Title = Locale.T("Загрузить тембр SimpleSynth"),
                InitialDirectory = SynthPresetStore.Folder,
                Filter = PresetFilter,
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) == true) RefreshPresets(ReadPreset(dialog.FileName));
        }

        void Change(SynthPatch next)
        {
            if (next == track.Patch) return;
            track.ChangePatch(next);
            presets.SelectedIndex = 0;
            Sync();
            history.Commit();
            edited();
        }
    }

    private static Preset ReadPreset(string path) =>
        new(Path.GetFileNameWithoutExtension(path), SynthPresetStore.Load(path), path);

    private static bool IsPresetFileError(Exception error) =>
        error is IOException or UnauthorizedAccessException or InvalidDataException;

    private void PresetFileAction(string title, Action action)
    {
        try { action(); }
        catch (Exception ex) when (IsPresetFileError(ex))
        {
            MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static Button PresetButton(string text, string hint) => new()
    {
        Content = text, ToolTip = hint, Style = (Style)Application.Current.FindResource("Compact")
    };

    private static Button HistoryButton(int direction, string hint)
    {
        var button = new Button { Content = direction, ToolTip = hint, IsEnabled = false, Height = 33,
            Margin = new Thickness(0, 0, 6, 0), Style = (Style)Application.Current.FindResource("HistoryButton") };
        AutomationProperties.SetName(button, hint);
        return button;
    }

    private static Grid TwoColumns()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.12, GridUnitType.Star) });
        return grid;
    }

    private static Border Card(UIElement content)
    {
        var card = new Border { Child = content, Padding = new Thickness(11), CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        return card;
    }

    private static DockPanel PanelHeader(string title, TextBlock value)
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
        DockPanel.SetDock(value, Dock.Right);
        header.Children.Add(value);
        header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        return header;
    }

    private static void AddNotePad(Panel toolbar, ToggleButton anchor, MidiTrack track)
    {
        string hint = Locale.T("Нажмите, чтобы сыграть");
        var label = AxisLabel(hint);
        label.Margin = new Thickness(0, 0, 0, 5);
        var pad = new SynthPad(track.PreviewNote, text => label.Text = text ?? hint)
            { Width = 264, Height = 228, Cursor = Cursors.Cross };
        AutomationProperties.SetName(pad, Locale.T("Прослушивание тембра. Нота по вертикали, громкость по горизонтали."));
        var body = new StackPanel();
        body.Children.Add(label);
        body.Children.Add(pad);
        var axes = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
        var right = AxisLabel(Locale.T("Громкость →"));
        DockPanel.SetDock(right, Dock.Right);
        axes.Children.Add(right);
        axes.Children.Add(AxisLabel(Locale.T("↑ Нота")));
        body.Children.Add(axes);
        var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, VerticalOffset = 4,
            AllowsTransparency = true, StaysOpen = false, Child = Card(body) };
        popup.SetBinding(Popup.IsOpenProperty, new Binding(nameof(ToggleButton.IsChecked))
            { Source = anchor, Mode = BindingMode.TwoWay });
        toolbar.Children.Add(popup);
    }

    private static TextBlock AxisLabel(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 11 };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Soft");
        return label;
    }
}
