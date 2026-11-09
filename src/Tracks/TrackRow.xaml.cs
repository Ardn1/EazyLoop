using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace EazyLoop;

public partial class TrackRow : UserControl
{
    private sealed record InputChoice(string Name, TrackInput? Source, bool Available = true);
    private readonly LoopEngine engine;
    private LoopGrid grid;
    private bool ready;
    private bool active;
    private bool armed;
    private bool collapsed;
    private bool fillingInputs;
    private Point gripStart;
    private string? trackTitle;
    internal Track Data { get; }
    internal int TrackNumber { get; private set; }
    internal TrackInput? Input { get; private set; }
    internal bool InputAvailable { get; private set; } = true;
    internal event Action<TrackRow>? RemoveRequested;
    internal event Action<TrackRow>? ExportRequested;
    internal event Action<TrackRow>? TargetRequested;
    internal event Action<TrackRow>? DragRequested;
    internal event Action? InputListRequested;
    internal event Action? Edited;
    internal event Action? ContentEdited;
    internal event Action<int>? SeekRequested;

    internal TrackRow(LoopEngine engine, Track track, int number, LoopGrid grid, TrackSettings? saved = null)
    {
        this.engine = engine;
        Data = track;
        fxHistory = new(track.Fx.CaptureState, RestoreFx, FxRack.SameState);
        trackTitle = saved?.Name;
        Input = saved?.Input;
        this.grid = grid;
        InitializeComponent();
        SetNumber(number);
        TitleEditor.MaxLength = TrackSettings.MaxNameLength;
        Wave.ShiftChanged += shift => SetShift(shift);
        Wave.Clicked += OpenAudioEditor;
        Wave.ZoomStepped += delta => WaveZoom.Value += delta;
        Wave.GridChanged(grid);
        Volume.Value = Data.Volume * 100.0;
        Pan.Value = Data.Pan * 100.0;
        Mute.IsChecked = Data.Muted;
        Solo.IsChecked = Data.Solo;
        WaveZoom.Value = saved?.Zoom ?? 0;
        ready = true;
        ApplyMix(notify: false);
        ShowMixValue(VolumeValue);
        ShowMixValue(PanValue);
        SetShift(Data.Shift);
        RefreshWave();
        SetCollapsed(saved?.Collapsed == true);
        SetupMidi();
    }

    internal TrackSnapshot Snapshot() => engine.Snapshot(Data, TrackNumber, WaveZoom.Value, trackTitle, Input, collapsed);

    internal void WatchArrangementDrags(EditHistory<ProjectSnapshot> history)
    {
        history.WatchDrag(Volume);
        history.WatchDrag(Pan);
        history.WatchDrag(Wave);
        history.WatchDrag(WaveZoom);
    }

    internal void RestoreArrangement(TrackSnapshot saved, LoopGrid targetGrid, AudioDriver driver, IReadOnlyList<AudioDevice> devices)
    {
        var state = saved.Settings;
        grid = targetGrid;
        ready = false;
        Volume.Value = state.Volume * 100.0;
        Pan.Value = state.Pan * 100.0;
        Mute.IsChecked = state.Muted;
        Solo.IsChecked = state.Solo;
        WaveZoom.Value = state.Zoom;
        ready = true;
        ApplyMix(notify: false);
        ShowMixValue(VolumeValue);
        ShowMixValue(PanValue);
        SetShift(state.Shift, notify: false);
        trackTitle = state.Name;
        if (Input != state.Input)
        {
            Input = state.Input;
            SetInputs(driver, devices);
        }
        if (collapsed != state.Collapsed) SetCollapsed(state.Collapsed);
        if (Data.Samples != saved.Samples || Data.Length != state.Frames || Data.Audio?.Edit != state.AudioEdit)
        {
            engine.ReplaceAudio(Data, saved.Samples, state.Frames, state.AudioEdit);
            RefreshWave();
        }
    }

    internal void SetNumber(int number)
    {
        TrackNumber = number;
        Number.Text = number.ToString("00");
        TrackName.Text = trackTitle ?? Locale.F($"Дорожка {number}");
        TitleHit.ToolTip = Locale.F($"{TrackName.Text} · двойной щелчок для переименования");
        effects?.Rename(TrackName.Text);
        midiEditor?.Rename(TrackName.Text);
        glissandoEditor?.Rename(TrackName.Text);
        drumEditor?.Rename(TrackName.Text);
        audioEditor?.Rename(TrackName.Text);
    }

    internal void RefreshLanguage()
    {
        SetNumber(TrackNumber);
        if (!active) TrackInfo.Text = Description();
        UpdateCollapseHint();
        Wave.InvalidateStrip();
        MidiPreview.InvalidateVisual();
        UpdateMidiLanguage();
    }

    internal void CloseFxWindow()
    {
        audioEditor?.Close();
        midiEditor?.Close();
        glissandoEditor?.Close();
        drumSettings?.Close();
        drumEditor?.Close();
        effects?.Close();
    }
    internal void DisposeEffects()
    {
        CloseFxWindow();
        Data.Fx.Dispose();
        Data.Midi?.Dispose();
    }

    internal void SetInputs(AudioDriver driver, IReadOnlyList<AudioDevice> devices)
    {
        if (IsMidi) return;
        var choices = new List<InputChoice> { new(Locale.T("Общий микрофон"), null) };
        foreach (var device in devices)
            if (device.Id != SoundSession.Default)
                choices.Add(new InputChoice(device.Name, new TrackInput(driver, device.Id, device.Name)));

        var selected = Input == null ? choices[0] : choices.FirstOrDefault(item => item.Source is { } source &&
            source.Driver == Input.Driver && source.Id == Input.Id &&
            (driver == AudioDriver.Wasapi || source.Name == Input.Name));
        if (selected == null)
        {
            selected = new InputChoice(Locale.F($"Недоступен · {Input!.Name}"), Input, false);
            choices.Add(selected);
        }

        fillingInputs = true;
        InputSelector.ItemsSource = choices;
        InputSelector.SelectedItem = selected;
        InputSelector.ToolTip = selected.Name;
        InputAvailable = selected.Available;
        fillingInputs = false;
    }

    internal void SetArmed(bool value)
    {
        if (armed == value) return;
        armed = value;
        if (!active) TrackInfo.Text = Description();
        UpdateTrackAppearance();
    }

    internal void ShowDropCue(bool? before)
    {
        DropMarker.Visibility = before.HasValue ? Visibility.Visible : Visibility.Collapsed;
        DropMarker.VerticalAlignment = before == false ? VerticalAlignment.Bottom : VerticalAlignment.Top;
    }

    internal void GridChanged(LoopGrid value)
    {
        grid = value;
        Wave.GridChanged(value);
        SetShift(Data.Shift, notify: false);
        midiEditor?.ChangeGrid(value);
        glissandoEditor?.ChangeGrid(value);
        drumEditor?.ChangeGrid(value);
        audioEditor?.ChangeGrid(value);
        if (Data.Midi is { } midi) UpdateMidiPreview(midi, -1);
    }

    internal void Display(TransportView transport, bool busy, string? state)
    {
        int phase = transport.Playing && transport.State != Transport.CountIn ? transport.Phase : -1;
        if (Data.Midi is { } midi)
        {
            UpdateMidiPreview(midi, phase);
            midiEditor?.Transport(phase, transport.Playing);
            glissandoEditor?.Transport(phase, transport.Playing);
            drumEditor?.Transport(transport.Phase, transport.Playing);
        }
        else
        {
            Wave.SetPlayhead(phase, busy && state == Locale.T("Запись…"));
            audioEditor?.Transport(phase < 0 ? -1 : LoopEngine.Mod(phase - Data.Shift, grid.Frames), transport.Playing);
        }
        if (active == busy && (state == null || TrackInfo.Text == state)) return;
        active = busy;
        TrackInfo.Text = state ?? Description();
        UpdateTrackAppearance();
        UpdateControls();
    }

    internal void RefreshWave()
    {
        Wave.SetSamples(Data.PlaybackSamples, Data.PlaybackLength);
        audioEditor?.Sync(Data.Samples, Data.Length, Data.Audio?.Edit);
        if (!active) TrackInfo.Text = Description();
        UpdateControls();
    }

    private string Description() => midiInputTarget ? Locale.T("MIDI-вход") : IsDrums ? "Drums" : IsMidi ? "MIDI" : Data.Length == 0
        ? armed ? Locale.T("Выбрана") : Locale.T("Пустая")
        : Locale.F($"{Data.PlaybackLength / (double)LoopEngine.Rate:0.00} с");

    private void UpdateTrackAppearance()
    {
        Card.SetResourceReference(Border.BackgroundProperty, active || armed || midiInputTarget ? "AccentPanel" : "Panel");
        Card.SetResourceReference(Border.BorderBrushProperty, active ? "Rose" : armed || midiInputTarget ? "AccentLine" : "Line");
    }

    private void CollapseClicked(object sender, RoutedEventArgs e)
    {
        SetCollapsed(!collapsed);
        Edited?.Invoke();
    }

    private void SetCollapsed(bool value)
    {
        collapsed = value;
        var details = value ? Visibility.Collapsed : Visibility.Visible;
        Grip.Visibility = FxButton.Visibility = TrackActions.Visibility = TrackDetails.Visibility = details;
        InputRow.Visibility = IsMidi ? Visibility.Collapsed : details;
        MidiOpenButton.Visibility = IsMidi ? details : Visibility.Collapsed;
        DrumSettingsButton.Visibility = IsDrums ? details : Visibility.Collapsed;
        CompactMix.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(TrackInfoPanel, value ? 2 : 1);
        TrackInfoPanel.Margin = new Thickness(0, 0, value ? 0 : 12, 0);
        int padding = value ? 4 : 10;
        Card.Padding = new Thickness(12, padding, 12, padding);
        CollapseArrow.Angle = value ? 180 : 0;
        UpdateCollapseHint();
        if (value) InputSelector.IsDropDownOpen = false;
    }

    private void UpdateCollapseHint()
    {
        string action = collapsed ? Locale.T("Развернуть дорожку") : Locale.T("Свернуть дорожку");
        CollapseButton.ToolTip = action;
        AutomationProperties.SetName(CollapseButton, action);
    }

    private void UpdateControls()
    {
        RemoveButton.IsEnabled = !active;
        InputSelector.IsEnabled = !active;
        TargetButton.IsEnabled = !active && (IsMidi || Data.Length == 0);
        bool editable = !active && Data.Length > 0;
        Offset.IsEnabled = NudgeLeft.IsEnabled = NudgeRight.IsEnabled = Wave.IsEnabled = WaveZoom.IsEnabled = editable;
    }

    private void SliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready) return;
        ShowMixValue(sender == Volume ? VolumeValue : PanValue);
        ApplyMix();
    }
    private void MixChanged(object sender, RoutedEventArgs e) => ApplyMix();

    private void ApplyMix(bool notify = true)
    {
        if (!ready) return;
        engine.Mix(Data, (float)(Volume.Value / 100), (float)(Pan.Value / 100), Data.Shift,
            Mute.IsChecked == true, Solo.IsChecked == true);
        Wave.Opacity = MidiPreview.Opacity = Data.Muted ? 0.54 : 1;
        if (notify) Edited?.Invoke();
    }

    private string FormatMixValue(TextBox field) => field == VolumeValue
        ? $"{Volume.Value:0.#}%"
        : Math.Round(Pan.Value, 1).ToString("+0.#;-0.#;0");

    private void ShowMixValue(TextBox field) => field.Text = FormatMixValue(field);

    private void CommitMixValue(TextBox field)
    {
        if (field.Text == FormatMixValue(field)) return;
        bool volume = field == VolumeValue;
        var slider = volume ? Volume : Pan;
        string text = field.Text.Trim().Replace('−', '-').Replace(',', '.');
        if (volume && text.EndsWith('%')) text = text[..^1].TrimEnd();
        if (double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out double value) && double.IsFinite(value))
            slider.Value = Math.Round(Math.Clamp(value, slider.Minimum, slider.Maximum), 1);
        ShowMixValue(field);
    }

    private void MixValueFocus(object sender, KeyboardFocusChangedEventArgs e) => ((TextBox)sender).SelectAll();
    private void MixValueFocusLost(object sender, KeyboardFocusChangedEventArgs e) => CommitMixValue((TextBox)sender);

    private void MixValueKeys(object sender, KeyEventArgs e)
    {
        var field = (TextBox)sender;
        if (e.Key == Key.Enter) CommitMixValue(field);
        else if (e.Key == Key.Escape) ShowMixValue(field);
        else return;
        field.SelectAll();
        e.Handled = true;
    }

    private void SetShift(int shift, bool notify = true)
    {
        int half = grid.Frames / 2;
        shift = LoopEngine.Mod((long)shift + half, grid.Frames) - half;
        bool changed = shift != Data.Shift;
        engine.Mix(Data, Data.Volume, Data.Pan, shift, Data.Muted, Data.Solo);
        Wave.SetShift(shift);
        Offset.Text = (shift / (LoopEngine.Rate / 1000.0)).ToString("+0;-0;0");
        if (changed && notify) Edited?.Invoke();
    }

    private void WaveZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Wave?.SetZoom(e.NewValue);
        if (ready) Edited?.Invoke();
    }

    private void InputOpened(object sender, EventArgs e)
    {
        InputListRequested?.Invoke();
        if (Data.Length == 0) TargetRequested?.Invoke(this);
    }

    private void InputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || fillingInputs || InputSelector.SelectedItem is not InputChoice choice || !choice.Available) return;
        InputSelector.ToolTip = choice.Name;
        InputAvailable = true;
        if (Input != choice.Source)
        {
            Input = choice.Source;
            Edited?.Invoke();
        }
        if (Data.Length == 0) TargetRequested?.Invoke(this);
    }

    private void TargetClicked(object sender, RoutedEventArgs e)
    {
        if (IsMidi) OpenMidi();
        else TargetRequested?.Invoke(this);
    }

    private void TitleClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        BeginTitleEdit();
        e.Handled = true;
    }

    private void BeginTitleEdit()
    {
        TitleEditor.Text = TrackName.Text;
        TitleHit.Visibility = Visibility.Collapsed;
        TitleEditor.Visibility = Visibility.Visible;
        TitleEditor.Focus();
        TitleEditor.SelectAll();
    }

    private void TitleKeys(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        EndTitleEdit(e.Key == Key.Enter);
        e.Handled = true;
    }

    private void TitleFocusLost(object sender, KeyboardFocusChangedEventArgs e) => EndTitleEdit(save: true);

    private void EndTitleEdit(bool save)
    {
        if (TitleEditor.Visibility != Visibility.Visible) return;
        string text = TitleEditor.Text.Trim();
        TitleEditor.Visibility = Visibility.Collapsed;
        TitleHit.Visibility = Visibility.Visible;
        if (!save || text == TrackName.Text) return;
        trackTitle = text.Length == 0 ? null : text;
        SetNumber(TrackNumber);
        Edited?.Invoke();
    }

    private void GripDown(object sender, MouseButtonEventArgs e)
    {
        gripStart = e.GetPosition(Grip);
        Grip.CaptureMouse();
        e.Handled = true;
    }

    private void GripMove(object sender, MouseEventArgs e)
    {
        if (!Grip.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(Grip) - gripStart;
        if (Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance &&
            Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance) return;
        Grip.ReleaseMouseCapture();
        DragRequested?.Invoke(this);
        e.Handled = true;
    }

    private void GripUp(object sender, MouseButtonEventArgs e)
    {
        if (Grip.IsMouseCaptured) Grip.ReleaseMouseCapture();
    }

    private void CommitShift()
    {
        if (int.TryParse(Offset.Text, out int value))
        {
            int limit = grid.Frames / (LoopEngine.Rate / 1000) / 2;
            SetShift(Math.Clamp(value, -limit, limit) * (LoopEngine.Rate / 1000));
        }
        else SetShift(Data.Shift);
    }

    private void OffsetChanged(object sender, KeyboardFocusChangedEventArgs e) => CommitShift();
    private void OffsetKeys(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitShift();
        e.Handled = true;
    }

    private void NudgeEarlier(object sender, RoutedEventArgs e) => SetShift(Data.Shift - LoopEngine.Rate / 100);
    private void NudgeLater(object sender, RoutedEventArgs e) => SetShift(Data.Shift + LoopEngine.Rate / 100);
    private void ResetPan(object sender, MouseButtonEventArgs e) => Pan.Value = 0;

    private void TrackMenuOpened(object sender, RoutedEventArgs e)
    {
        DrumSettingsMenuItem.Visibility = IsDrums ? Visibility.Visible : Visibility.Collapsed;
        EditAudioMenuItem.IsEnabled = ExportMenuItem.IsEnabled = !active && Data.Length > 0;
        ResetShiftMenuItem.IsEnabled = ExportMenuItem.IsEnabled && Data.Shift != 0;
        MuteMenuItem.Header = Mute.IsChecked == true ? Locale.T("Включить звук") : Locale.T("Заглушить");
        SoloMenuItem.Header = Solo.IsChecked == true ? Locale.T("Снять соло") : Locale.T("Соло");
        CopyAllFxMenuItem.IsEnabled = Data.Fx.Snapshot() != null;
        PasteAllFxMenuItem.IsEnabled = copiedFx != null;
        RemoveMenuItem.IsEnabled = !active;
    }

    private void ExportMenuClicked(object sender, RoutedEventArgs e) => ExportRequested?.Invoke(this);
    private void RenameMenuClicked(object sender, RoutedEventArgs e) => Dispatcher.BeginInvoke(BeginTitleEdit);
    private void MuteMenuClicked(object sender, RoutedEventArgs e) => Mute.IsChecked = Mute.IsChecked != true;
    private void SoloMenuClicked(object sender, RoutedEventArgs e) => Solo.IsChecked = Solo.IsChecked != true;
    private void ResetShiftMenuClicked(object sender, RoutedEventArgs e) => SetShift(0);
    private void RemoveClicked(object sender, RoutedEventArgs e) => RemoveRequested?.Invoke(this);
}
