using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EazyLoop;

internal sealed class DrumSettingsWindow : Window
{
    private sealed record CountChoice(int Value, string Name);
    private sealed record PadChoice(int Index, string Name);
    private sealed record Preset(DrumSound Sound, string Name);
    private sealed record KitChoice(string Name, DrumPad[]? Pads);
    private readonly KitChoice[] kitChoices =
        [new(Locale.T("Свой набор"), null), .. DrumKitPreset.BuiltIn.Select(kit => new KitChoice(Locale.T(kit.Name), kit.Pads))];
    private readonly MidiTrack track;
    private readonly Action edited;
    private readonly EditHistory<DrumKit> history;
    private readonly ComboBox pads;
    private readonly ComboBox kitPicker;
    private readonly StackPanel parameters;
    private bool updating;

    public DrumSettingsWindow(MidiTrack track, int pad, Action edited)
    {
        this.track = track;
        this.edited = edited;
        history = new(() => track.Kit!, track.ChangeKit, (a, b) => a.Count == b.Count && a.Pads.SequenceEqual(b.Pads));
        Title = Locale.T("SimpleDrums · настройки набора");
        Width = 426;
        Height = 450;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Background");
        SetResourceReference(ForegroundProperty, "Ink");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        var root = new StackPanel { Margin = new Thickness(18) };
        Content = root;
        root.Children.Add(new TextBlock { Text = "SimpleDrums", FontSize = 22, FontWeight = FontWeights.SemiBold });
        kitPicker = new ComboBox { ItemsSource = kitChoices, DisplayMemberPath = nameof(KitChoice.Name), Height = 31,
            ToolTip = Locale.T("Меняет звуки всех полос набора. Число полос и удары сохраняются.") };
        System.Windows.Automation.AutomationProperties.SetName(kitPicker, Locale.T("Пресет набора"));
        var kitField = Field(Locale.T("ПРЕСЕТ НАБОРА"), kitPicker);
        kitField.Margin = new Thickness(0, 11, 0, 0);
        root.Children.Add(kitField);
        var counts = new CountChoice[] { new(1, Locale.T("1 · бочка")), new(3, Locale.T("3 · основа")), new(5, Locale.T("5 · грув")), new(8, Locale.T("8 · полный набор")) };
        pads = new ComboBox { Height = 31, DisplayMemberPath = nameof(PadChoice.Name) };
        var count = new ComboBox { ItemsSource = counts, DisplayMemberPath = nameof(CountChoice.Name), Height = 31,
            SelectedItem = counts.First(choice => choice.Value == track.Kit!.Count) };
        System.Windows.Automation.AutomationProperties.SetName(count, Locale.T("Число ударных"));
        System.Windows.Automation.AutomationProperties.SetName(pads, Locale.T("Полоса ударных"));
        count.SelectionChanged += (_, _) =>
        {
            if (updating || count.SelectedItem is not CountChoice choice || choice.Value == track.Kit!.Count) return;
            int active = Math.Clamp(pads.SelectedIndex, 0, choice.Value - 1);
            track.ChangeKit(track.Kit with { Count = choice.Value });
            BuildPads(active);
            KitEdited();
        };
        var two = new Grid { Margin = new Thickness(0, 11, 0, 11) };
        two.ColumnDefinitions.Add(new ColumnDefinition());
        two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9) });
        two.ColumnDefinitions.Add(new ColumnDefinition());
        var number = Field(Locale.T("ЧИСЛО УДАРНЫХ"), count);
        two.Children.Add(number);
        pads.SelectionChanged += (_, _) => { if (!updating) BuildPad(); };
        var which = Field(Locale.T("ПОЛОСА"), pads);
        Grid.SetColumn(which, 2);
        two.Children.Add(which);
        root.Children.Add(two);
        parameters = new StackPanel();
        root.Children.Add(parameters);
        BuildPads(Math.Clamp(pad, 0, track.Kit!.Count - 1));
        ShowKitPreset();
        kitPicker.SelectionChanged += (_, _) =>
        {
            var kit = track.Kit!;
            if (kitPicker.SelectedItem is not KitChoice { Pads: { } sounds } || kit.Pads.SequenceEqual(sounds)) return;
            track.ChangeKit(kit with { Pads = sounds.ToArray() });
            BuildPads(Math.Clamp(pads.SelectedIndex, 0, kit.Count - 1));
            KitEdited();
        };
        PreviewKeyDown += (_, e) => HistoryKeys.Handle(e, forward =>
        {
            if (!history.Step(forward)) return;
            var kit = track.Kit!;
            count.SelectedItem = counts.First(choice => choice.Value == kit.Count);
            BuildPads(Math.Clamp(pads.SelectedIndex, 0, kit.Count - 1));
            ShowKitPreset();
            edited();
        });
    }

    private void KitEdited()
    {
        history.Commit();
        edited();
    }

    public void SelectPad(int pad)
    {
        if (pad >= 0 && pad < track.Kit!.Count) pads.SelectedIndex = pad;
        Activate();
    }

    private void BuildPads(int selected)
    {
        updating = true;
        var kit = track.Kit!;
        pads.ItemsSource = Enumerable.Range(0, kit.Count).Select(i => new PadChoice(i, kit.PadName(i))).ToArray();
        pads.SelectedIndex = selected;
        updating = false;
        BuildPad();
    }

    private void BuildPad()
    {
        if (pads.SelectedItem is not PadChoice pad) return;
        parameters.Children.Clear();
        var value = track.Kit!.Pads[pad.Index];
        var sounds = DrumKit.Sounds.Select(item => new Preset(item.Sound, Locale.T(item.Name))).ToArray();
        var presets = new ComboBox { ItemsSource = sounds, DisplayMemberPath = nameof(Preset.Name), Height = 31,
            SelectedItem = sounds.First(item => item.Sound == value.Sound) };
        System.Windows.Automation.AutomationProperties.SetName(presets, Locale.T("Пресет звука"));
        presets.SelectionChanged += (_, _) =>
        {
            if (presets.SelectedItem is not Preset next || next.Sound == track.Kit!.Pads[pad.Index].Sound) return;
            Change(DrumKit.Preset(next.Sound));
            BuildPads(pad.Index);
            track.Preview([DrumKit.Keys[pad.Index]]);
        };
        parameters.Children.Add(Field(Locale.T("ПРЕСЕТ ЗВУКА"), presets));
        Add(Locale.T("Высота"), -24, 24, value.Tune, Locale.T("пт"), (p, x) => p with { Tune = x });
        Add(Locale.T("Затухание"), 20, 1800, value.Decay, Locale.T("мс"), (p, x) => p with { Decay = x });
        Add(Locale.T("Яркость"), 0, 100, value.Tone, "%", (p, x) => p with { Tone = x });
        Add(Locale.T("Громкость"), 0, 100, value.Level, "%", (p, x) => p with { Level = x });
        Add(Locale.T("Панорама"), -100, 100, value.Pan, "", (p, x) => p with { Pan = x });
        var button = new Button { Content = Locale.T("▶ Прослушать"), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)Application.Current.FindResource("Compact") };
        button.Click += (_, _) => track.Preview([DrumKit.Keys[pad.Index]]);
        parameters.Children.Add(button);

        void Add(string name, double min, double max, double current, string unit, Func<DrumPad, float, DrumPad> update) =>
            parameters.Children.Add(FxWindow.ParameterLine(name, min, max, current, x => $"{x:0} {unit}".TrimEnd(),
                x => Change(update(track.Kit!.Pads[pad.Index], (float)x)), history.WatchDrag));

        void Change(DrumPad next)
        {
            var kit = track.Kit!;
            if (kit.Pads[pad.Index] == next) return;
            var set = (DrumPad[])kit.Pads.Clone();
            set[pad.Index] = next;
            track.ChangeKit(kit with { Pads = set });
            ShowKitPreset();
            KitEdited();
        }
    }

    private void ShowKitPreset()
    {
        var sounds = track.Kit!.Pads;
        kitPicker.SelectedItem = kitChoices.FirstOrDefault(choice => choice.Pads?.SequenceEqual(sounds) == true) ?? kitChoices[0];
    }

    private static StackPanel Field(string title, FrameworkElement control)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 0, 0, 4),
            Style = (Style)Application.Current.FindResource("Label") });
        panel.Children.Add(control);
        return panel;
    }
}
