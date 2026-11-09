using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using NAudio.Vst3;

namespace EazyLoop;

public partial class FxWindow : Window
{
    internal sealed record CopiedEffect(FxSlotSettings Settings, bool Connected);
    private static CopiedEffect? clipboard;
    private readonly FxRack rack;
    private readonly Action edited;
    private readonly EditHistory<FxRack.RackState> history;
    private readonly Dictionary<FxSlot, Window> editors = [];
    private bool ready;

    internal FxWindow(FxRack rack, string title, Action edited, EditHistory<FxRack.RackState> history,
        Action<TrackFxSettings> applyPreset)
    {
        this.rack = rack;
        this.edited = edited;
        this.history = history;
        this.applyPreset = applyPreset;
        InitializeComponent();
        ChainScroll.ContextMenu = EffectMenu("Вставить эффект", PasteEffect,
            () => clipboard != null && rack.Slots.Length < FxCatalog.MaxSlots);
        history.WatchDrag(Graph);
        PreviewKeyDown += (_, e) => HistoryKeys.Handle(e, forward =>
        {
            if (history.Step(forward)) edited();
        });
        TrackTitle.Text = title;
        AllEffectsToggle.IsChecked = !rack.Bypassed;
        EqToggle.IsChecked = rack.EqEnabled;
        Graph.BandEdited += ChangeBand;
        for (int i = 0; i < rack.Bands.Length; i++)
        {
            var label = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11 };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Soft");
            BandLabels.Children.Add(label);
        }
        ready = true;
        ShowEq();
        ShowChain();
        RefreshFxPresets();
    }

    internal void Rename(string title) => TrackTitle.Text = title;

    internal void RefreshRack()
    {
        ready = false;
        AllEffectsToggle.IsChecked = !rack.Bypassed;
        EqToggle.IsChecked = rack.EqEnabled;
        ready = true;
        PresetChoice.SelectedIndex = 0;
        ShowEq();
        ShowChain();
    }

    internal void CloseSlotEditor(FxSlot slot)
    {
        if (editors.TryGetValue(slot, out var editor)) editor.Close();
    }

    private void RackEdited()
    {
        PresetChoice.SelectedIndex = 0;
        history.Commit();
        edited();
    }

    private void AllEffectsToggled(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        rack.SetBypassed(AllEffectsToggle.IsChecked != true);
        ShowEq();
        RackEdited();
    }

    private void EqToggled(object sender, RoutedEventArgs e)
    {
        if (ready) SetEq(rack.Bands);
    }

    private void ResetEq(object sender, RoutedEventArgs e) => SetEq(FxCatalog.Flat);

    private void ChangeBand(int index, EqBand band)
    {
        if (rack.Bands[index] == band) return;
        var bands = (EqBand[])rack.Bands.Clone();
        bands[index] = band;
        SetEq(bands);
    }

    private void SetEq(EqBand[] bands)
    {
        rack.SetEq(EqToggle.IsChecked == true, bands);
        ShowEq();
        RackEdited();
    }

    private void ShowEq()
    {
        EqToggle.Content = rack.EqEnabled ? Locale.T("Вкл") : Locale.T("Обход");
        Graph.Update(rack.Bands, rack.EqEnabled && !rack.Bypassed);
        for (int i = 0; i < rack.Bands.Length; i++)
        {
            var band = rack.Bands[i];
            ((TextBlock)BandLabels.Children[i]).Text = Locale.F($"{i + 1}   {band.Hz:0} Гц   {band.Db:+0.#;-0.#;0} дБ");
        }
    }

    private void AddBuiltInClicked(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = BuiltInButton, Placement = PlacementMode.Bottom,
            Style = (Style)FindResource("FxMenu") };
        foreach (var entry in FxCatalog.BuiltIn)
        {
            var item = new MenuItem { Header = Locale.T(entry.Value.Name), Style = (Style)FindResource("FxMenuItem") };
            item.Click += (_, _) => AddSlot(new FxSlot(FxCatalog.Default(entry.Key)));
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void AddVstClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            string? path = ChooseModule(this);
            if (path == null) return;
            Connect(path, null);
        }
        catch (Exception ex) { Report(ex); }
    }

    internal static string? ChooseModule(Window owner)
    {
        var entries = Vst3PluginScanner.EnumerateInstalled().OrderBy(module => module.Name).ToArray();
        return Pick(owner, Locale.T("Подключить VST3"), entries, nameof(Vst3ModuleInfo.Name),
            () => PickFile(owner), () => PickFolder(owner))?.Path;
    }

    private static Vst3ModuleInfo? PickFile(Window owner)
    {
        var dialog = new OpenFileDialog { Filter = Locale.T("VST3-эффект (*.vst3)|*.vst3"), CheckFileExists = true, Title = Locale.T("Файл VST3") };
        return dialog.ShowDialog(owner) == true ? new(dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName)) : null;
    }

    private static Vst3ModuleInfo? PickFolder(Window owner)
    {
        var dialog = new OpenFolderDialog { Title = Locale.T("Папка пакета .vst3") };
        if (dialog.ShowDialog(owner) != true) return null;
        if (!dialog.FolderName.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(owner, Locale.T("Выберите папку, имя которой заканчивается на .vst3."), Locale.T("Подключить VST3"));
            return null;
        }
        return new(dialog.FolderName, Path.GetFileNameWithoutExtension(dialog.FolderName));
    }

    internal static T? Pick<T>(Window owner, string title, T[] entries, string display,
        Func<T?>? browseFile = null, Func<T?>? browseFolder = null)
        where T : class
    {
        var dialog = new Window
        {
            Title = title, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Width = 430, Height = 178, ResizeMode = ResizeMode.NoResize
        };
        dialog.SetResourceReference(BackgroundProperty, "Background");
        dialog.SetResourceReference(ForegroundProperty, "Ink");
        var layout = new StackPanel { Margin = new Thickness(16) };
        dialog.Content = layout;
        var choices = new ComboBox
        {
            ItemsSource = entries, DisplayMemberPath = display, SelectedIndex = entries.Length == 0 ? -1 : 0,
            IsTextSearchEnabled = true, Height = 34, IsEnabled = entries.Length > 0
        };
        layout.Children.Add(choices);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        layout.Children.Add(actions);
        T? selected = null;
        void Choose(T? value)
        {
            if (value == null) return;
            selected = value;
            dialog.DialogResult = true;
        }
        Button PickButton(string text, Action action)
        {
            var button = new Button { Style = (Style)owner.FindResource("Compact"), Content = text, Margin = new Thickness(4, 0, 0, 0) };
            button.Click += (_, _) => action();
            return button;
        }
        var connect = PickButton(Locale.T("Подключить"), () => Choose(choices.SelectedItem as T));
        connect.IsDefault = true;
        connect.IsEnabled = entries.Length > 0;
        actions.Children.Add(connect);
        if (browseFile != null) actions.Children.Add(PickButton(Locale.T("Файл…"), () => Choose(browseFile())));
        if (browseFolder != null) actions.Children.Add(PickButton(Locale.T("Пакет…"), () => Choose(browseFolder())));
        return dialog.ShowDialog() == true ? selected : null;
    }

    private void Connect(string path, FxSlot? saved)
    {
        var slot = LoadSlot(path, saved, this);
        if (slot == null) return;
        if (saved == null) AddSlot(slot);
        else
        {
            ShowChain();
            RackEdited();
        }
    }

    private static FxSlot? LoadSlot(string path, FxSlot? saved, Window? owner = null)
    {
        Vst3Module? module = Vst3Module.Load(path);
        FxSlot? slot = saved;
        try
        {
            var effects = module.GetClasses().Where(info => info.IsAudioModule && !info.IsInstrument).ToArray();
            if (effects.Length == 0) throw new NotSupportedException(Locale.T("В этом VST3 нет аудиоэффектов."));
            Vst3ClassInfo? info = saved?.Settings.Plugin is { } previous
                ? effects.FirstOrDefault(info => string.Equals(info.ClassId, previous.ClassId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(Locale.T("В файле нет сохранённого класса плагина."))
                : effects.Length == 1 ? effects[0] : Pick(owner!, Locale.T("Выберите эффект"), effects, nameof(Vst3ClassInfo.Name));
            if (info == null) return null;
            slot ??= new FxSlot(new FxSlotSettings(FxKind.Vst3, true, [], new VstReference(path, info.ClassId, info.Name)));
            slot.Connect(module, info);
            module = null; // The slot owns the loaded module from this point.
            if (saved != null)
                slot.Update(slot.Settings with { Plugin = slot.Settings.Plugin! with { Path = path } });
            return slot;
        }
        catch
        {
            if (saved == null) slot?.Dispose();
            throw;
        }
        finally { module?.Dispose(); }
    }

    private void ConnectSaved(FxSlot slot)
    {
        try
        {
            var reference = slot.Settings.Plugin!;
            if (File.Exists(reference.Path) || Directory.Exists(reference.Path)) Connect(reference.Path, slot);
            else if (ChooseModule(this) is { } path) Connect(path, slot);
        }
        catch (Exception ex) { Report(ex); }
    }

    private void AddSlot(FxSlot slot)
    {
        if (rack.Slots.Length >= FxCatalog.MaxSlots) { slot.Dispose(); return; }
        rack.SetSlots([.. rack.Slots, slot]);
        ShowChain();
        RackEdited();
    }

    private ContextMenu EffectMenu(string key, Action invoke, Func<bool>? available = null)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("FxMenu") };
        var item = new MenuItem { Style = (Style)FindResource("FxMenuItem") };
        item.SetResourceReference(HeaderedItemsControl.HeaderProperty, "loc:" + key);
        item.Click += (_, _) => invoke();
        if (available != null) menu.Opened += (_, _) => item.IsEnabled = available();
        menu.Items.Add(item);
        return menu;
    }

    private void CopyEffect(FxSlot slot)
    {
        try
        {
            if (slot.SavePluginState()) RackEdited();
            clipboard = CopySettings(slot);
        }
        catch (Exception ex) { Report(ex); }
    }

    private void PasteEffect()
    {
        if (clipboard is not { } copied || rack.Slots.Length >= FxCatalog.MaxSlots) return;
        AddSlot(CreateCopy(copied, out var connectionError));
        if (connectionError != null) Report(connectionError);
    }

    internal static CopiedEffect CopySettings(FxSlot slot) => new(CloneSettings(slot.Settings), slot.Plugin != null);

    internal static FxSlot CreateCopy(CopiedEffect copied, out Exception? connectionError, Guid? id = null)
    {
        var slot = new FxSlot(CloneSettings(copied.Settings), id);
        connectionError = null;
        if (copied.Connected && slot.Settings.Plugin is { } plugin &&
            (File.Exists(plugin.Path) || Directory.Exists(plugin.Path)))
        {
            try { LoadSlot(plugin.Path, slot); }
            catch (Exception ex) { connectionError = ex; }
        }
        return slot;
    }

    private static FxSlotSettings CloneSettings(FxSlotSettings settings) => settings with
    {
        Values = [.. settings.Values],
        Plugin = settings.Plugin is { } plugin ? plugin with { State = plugin.State?.ToArray() } : null
    };

    private void ShowChain()
    {
        var slots = rack.Slots;
        BuiltInButton.IsEnabled = VstButton.IsEnabled = slots.Length < FxCatalog.MaxSlots;
        Chain.Children.Clear();
        if (slots.Length == 0)
        {
            var hint = new TextBlock { Text = Locale.T("Добавьте эффект или установленный VST3."), Margin = new Thickness(4, 11, 0, 0) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Soft");
            Chain.Children.Add(hint);
            return;
        }
        for (int i = 0; i < slots.Length; i++) Chain.Children.Add(SlotCard(slots[i], i, slots.Length));
    }

    private Border SlotCard(FxSlot slot, int index, int total)
    {
        var stack = new StackPanel();
        var heading = new DockPanel();
        stack.Children.Add(heading);
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(controls, Dock.Right);
        heading.Children.Add(controls);
        var bypass = new ToggleButton
        {
            Style = (Style)FindResource("PillToggle"), Padding = new Thickness(6, 0, 6, 0), Height = 27,
            IsChecked = slot.Settings.Enabled, Content = slot.Settings.Enabled ? Locale.T("Вкл") : Locale.T("Обход"), ToolTip = Locale.T("Включить или обойти эффект")
        };
        bypass.Click += (_, _) =>
        {
            rack.SetSlotEnabled(slot, bypass.IsChecked == true);
            bypass.Content = slot.Settings.Enabled ? Locale.T("Вкл") : Locale.T("Обход");
            RackEdited();
        };
        controls.Children.Add(bypass);
        controls.Children.Add(ActionButton("↑", Locale.T("Переместить выше"), () => Move(index, index - 1), index > 0));
        controls.Children.Add(ActionButton("↓", Locale.T("Переместить ниже"), () => Move(index, index + 1), index < total - 1));
        controls.Children.Add(ActionButton("×", Locale.T("Удалить эффект"), () => Remove(slot)));
        heading.Children.Add(new TextBlock { Text = $"{index + 1:00}   {slot.Name}", FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) });

        if (slot.Settings.Kind == FxKind.Vst3)
        {
            bool connected = slot.Plugin != null;
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            stack.Children.Add(actions);
            actions.Children.Add(ActionButton(connected ? Locale.T("Редактор…") : Locale.T("Подключить…"), Locale.T("Настроить VST3"), () =>
            {
                if (connected) OpenEditor(slot);
                else ConnectSaved(slot);
            }));
            var status = new TextBlock { Text = slot.Error ?? (connected ? Locale.T("VST3 · подключён") : Locale.T("VST3 · сохранён в проекте")),
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 290, FontSize = 11 };
            status.SetResourceReference(TextBlock.ForegroundProperty, slot.Error == null ? "Muted" : "Rose");
            actions.Children.Add(status);
        }
        else
        {
            var parameters = FxCatalog.BuiltIn[slot.Settings.Kind].Parameters;
            bool tune = slot.Settings.Kind == FxKind.PitchCorrection;
            if (tune) stack.Children.Add(PitchScaleLine((int)slot.Settings.Values[0], value => SetParameter(0, value)));
            if (slot.Settings.Kind == FxKind.NoiseReduction)
            {
                var hint = new TextBlock
                {
                    Text = slot.Error ?? Locale.T("RNNoise обучен на речи. Уменьшите силу, если гитара теряет тихие ноты."),
                    FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 1)
                };
                hint.SetResourceReference(TextBlock.ForegroundProperty, slot.Error == null ? "Muted" : "Rose");
                stack.Children.Add(hint);
            }
            for (int i = tune ? 1 : 0; i < parameters.Length; i++)
            {
                int parameter = i;
                var meta = parameters[i];
                stack.Children.Add(ParameterLine(Locale.T(meta.Name), meta.Min, meta.Max, slot.Settings.Values[i],
                    value => $"{value:0.##} {Locale.T(meta.Unit)}", value => SetParameter(parameter, value), history.WatchDrag));
            }
        }
        var card = new Border
        {
            Child = stack, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 6),
            ContextMenu = EffectMenu("Копировать эффект", () => CopyEffect(slot))
        };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        return card;

        void SetParameter(int parameter, double value)
        {
            var settings = slot.Settings;
            var values = (float[])settings.Values.Clone();
            values[parameter] = (float)value;
            slot.Update(settings with { Values = values });
            RackEdited();
        }
    }

    private static DockPanel PitchScaleLine(int selection, Action<int> changed)
    {
        var options = Enumerable.Range(0, DiatonicScale.Count).Select(DiatonicScale.FromIndex)
            .Select(key => key.DisplayName).Prepend(Locale.T("Хроматика · ближайшая нота")).ToArray();
        string title = Locale.T("Тональность");
        var choice = new ComboBox
        {
            ItemsSource = options, SelectedIndex = selection, Height = 28,
            ToolTip = Locale.T("Голос и одиночные ноты. Выберите тональность или коррекцию к любой ближайшей ноте.")
        };
        System.Windows.Automation.AutomationProperties.SetName(choice, title);
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedIndex >= 0) changed(choice.SelectedIndex);
        };
        var line = new DockPanel { Margin = new Thickness(0, 6, 0, 3) };
        var label = new TextBlock { Text = title, Width = 94, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel.SetDock(label, Dock.Left);
        line.Children.Add(label);
        line.Children.Add(choice);
        return line;
    }

    internal static DockPanel ParameterLine(string title, double min, double max, double initial,
        Func<double, string> format, Action<double> changed, Action<UIElement>? watchDrag = null)
    {
        var line = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var label = new TextBlock { Text = title, Width = 94, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = title };
        var value = new TextBlock { Text = format(initial), Width = 75, FontSize = 11, TextAlignment = TextAlignment.Right };
        DockPanel.SetDock(label, Dock.Left);
        DockPanel.SetDock(value, Dock.Right);
        line.Children.Add(label);
        line.Children.Add(value);
        var slider = new Slider { Minimum = min, Maximum = max, Value = initial, SmallChange = (max - min) / 100 };
        watchDrag?.Invoke(slider);
        System.Windows.Automation.AutomationProperties.SetName(slider, title);
        slider.ValueChanged += (_, e) =>
        {
            changed(e.NewValue);
            value.Text = format(e.NewValue);
        };
        line.Children.Add(slider);
        return line;
    }

    private Button ActionButton(string text, string tip, Action action, bool enabled = true)
    {
        var button = new Button { Style = (Style)FindResource("Compact"), Content = text, ToolTip = tip,
            Margin = new Thickness(4, 0, 0, 0), IsEnabled = enabled };
        button.Click += (_, _) => action();
        return button;
    }

    private void Move(int from, int to)
    {
        var slots = (FxSlot[])rack.Slots.Clone();
        (slots[from], slots[to]) = (slots[to], slots[from]);
        rack.SetSlots(slots);
        ShowChain();
        RackEdited();
    }

    private void Remove(FxSlot slot)
    {
        CloseSlotEditor(slot);
        rack.SetSlots(rack.Slots.Where(item => item != slot).ToArray());
        slot.Dispose();
        ShowChain();
        RackEdited();
    }

    private void OpenEditor(FxSlot slot)
    {
        if (editors.TryGetValue(slot, out var existing)) { existing.Activate(); return; }
        var plugin = slot.Plugin;
        if (plugin == null) return;
        try
        {
            var editor = CreatePluginEditor(this, plugin, slot.Name);
            editors.Add(slot, editor);
            editor.Closed += (_, _) =>
            {
                editors.Remove(slot);
                try { if (slot.SavePluginState()) RackEdited(); }
                catch (Exception ex) { Report(ex); }
            };
            editor.Show();
        }
        catch (Exception ex) { editors.Remove(slot); Report(ex); }
    }

    internal static Window CreatePluginEditor(Window owner, Vst3Plugin plugin, string title)
    {
        var editor = new Window
        {
            Title = title, Owner = owner, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 430, Height = 440, MinWidth = 190, MinHeight = 90
        };
        editor.SetResourceReference(BackgroundProperty, "Background");
        editor.SetResourceReference(ForegroundProperty, "Ink");
        var view = plugin.CreateView();
        if (view == null) { editor.Content = ParameterEditor(plugin); return editor; }
        editor.ResizeMode = ResizeMode.NoResize;
        editor.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(editor).Handle;
            double dpi = VisualTreeHelper.GetDpi(editor).DpiScaleX;
            void SizeTo(Vst3ViewSize size)
            {
                editor.Width = Math.Clamp(size.Width / dpi + 16, 190, 2200);
                editor.Height = Math.Clamp(size.Height / dpi + 40, 90, 1500);
            }
            view.Resized += (_, size) => SizeTo(size);
            try { view.AttachTo(hwnd, (float)dpi); SizeTo(view.GetSize()); }
            catch (Exception) { editor.Content = ParameterEditor(plugin); }
        };
        editor.Closing += (_, _) => view.Dispose();
        return editor;
    }

    private static ScrollViewer ParameterEditor(Vst3Plugin plugin)
    {
        var stack = new StackPanel { Margin = new Thickness(14) };
        foreach (var parameter in plugin.Parameters)
        {
            if (parameter.IsHidden || parameter.IsReadOnly) continue;
            stack.Children.Add(ParameterLine(parameter.Title, 0, 1, parameter.NormalizedValue,
                _ => parameter.DisplayValue, value => parameter.NormalizedValue = value));
        }
        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        foreach (var editor in editors.Values.ToArray()) editor.Close();
        base.OnClosing(e);
    }

    private void Report(Exception ex) => MessageBox.Show(this, ex.Message, Locale.T("Эффекты дорожки"),
        MessageBoxButton.OK, MessageBoxImage.Warning);
}
