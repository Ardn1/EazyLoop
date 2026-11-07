using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace EazyLoop;

public partial class MainWindow : Window
{
    private sealed record Choice(int Value, string Name);
    private static readonly Geometry PlayShape = Geometry.Parse("M 0,0 L 10,5.5 L 0,11 Z");
    private static readonly Geometry StopShape = Geometry.Parse("M 0,0 H 12 V 12 H 0 Z");
    private static readonly Geometry RecordShape = new EllipseGeometry(new Point(7, 7), 7, 7);
    private static readonly Geometry CancelShape = Geometry.Parse("M 0,2 L 2,0 L 7,5 L 12,0 L 14,2 L 9,7 L 14,12 L 12,14 L 7,9 L 2,14 L 0,12 L 5,7 Z");
    private readonly LoopEngine engine = new();
    private readonly List<TrackRow> rows = [];
    private readonly DispatcherTimer timer;
    private SoundSession? sound;
    private LoopGrid grid = new(100, 4, 2);
    private AudioOptions options = new(AudioDriver.Wasapi, 20, SoundSession.Default, SoundSession.Default);
    private TrackRow? recordingRow;
    private string? inputError;
    private string? outputError;
    private bool ready;
    private bool populating;
    private bool settingsActive;
    private Transport transport = Transport.Stopped;

    public MainWindow()
    {
        Locale.Initialize();
        ThemeManager.Initialize();
        InitializeComponent();
        Ruler.SeekRequested += SeekPlayback;
        UpdateLanguageIcon();
        Meter.ItemsSource = new[] { 2, 3, 4, 5, 6, 7 }.Select(n => new Choice(n, $"{n}/4")).ToArray();
        Meter.DisplayMemberPath = nameof(Choice.Name);
        Meter.SelectedValuePath = nameof(Choice.Value);
        Meter.SelectedValue = 4;
        Bars.ItemsSource = new[] { 1, 2, 3, 4, 6, 8, 12, 16 };
        Bars.SelectedItem = 2;
        AddTrack(false);
        ready = true;
        CommitGrid();
        revision = savedRevision = 0;
        history = new(Snapshot, RestoreArrangement, SameArrangement, capacity: 24);
        history.WatchDrag(ClickVolume);
        rows[0].WatchArrangementDrags(history);
        timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        timer.Tick += (_, _) =>
        {
            Refresh();
            Autosave();
        };
    }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sound != null) return;
        PopulateMicrophones();
        ReopenSound();
        ComponentDispatcher.ThreadFilterMessage += TransportKeyMessage;
        timer.Start();
        Focus();
    }

    private void ReopenSound()
    {
        repeatRecording = false;
        sound?.Dispose();
        engine.AbortAudio();
        inputError = outputError = null;
        sound = new SoundSession(engine, options);
        UpdateRecordingTarget();
        Refresh();
    }

    private List<AudioDevice> Enumerate(AudioDriver driver, bool input)
    {
        try { return SoundSession.Devices(driver, input); }
        catch (Exception ex)
        {
            AudioHint.ToolTip = ex.Message;
            return [new(SoundSession.Default, Locale.T("По умолчанию"))];
        }
    }

    private void PopulateMicrophones()
    {
        populating = true;
        inputDevices = Enumerate(options.Driver, true);
        if (!inputDevices.Any(device => device.Id == options.Input))
            options = options with { Input = SoundSession.Default };
        Microphone.ItemsSource = inputDevices;
        Microphone.SelectedValue = options.Input;
        foreach (var row in rows) row.SetInputs(options.Driver, inputDevices);
        populating = false;
    }

    private void RefreshMicrophones(object sender, EventArgs e) => RefreshMicrophones();

    private void RefreshMicrophones()
    {
        PopulateMicrophones();
        UpdateRecordingTarget(retry: true);
    }

    private void MicrophoneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || populating || transport != Transport.Stopped || Microphone.SelectedValue is not string id) return;
        options = options with { Input = id };
        repeatRecording = false;
        UpdateRecordingTarget(retry: true);
    }

    private void CommitGrid()
    {
        if (!ready || transport != Transport.Stopped) return;
        int bpm = int.TryParse(Tempo.Text, out int number) ? Math.Clamp(number, 40, 240) : grid.Bpm;
        Tempo.Text = bpm.ToString();
        var next = new LoopGrid(bpm, (int)(Meter.SelectedValue ?? 4), (int)(Bars.SelectedItem ?? 2));
        bool changed = grid != next;
        grid = next;
        engine.Configure(grid);
        Ruler.GridChanged(grid);
        foreach (var row in rows) row.GridChanged(grid);
        UpdateRuler(engine.View(sound?.Position() ?? 0));
        UpdateGridLength();
        if (changed) ProjectEdited();
    }

    private void UpdateGridLength()
    {
        string bars = grid.Bars switch { 1 => Locale.T("такт"), 2 or 3 or 4 => Locale.T("такта"), _ => Locale.T("тактов") };
        LengthLabel.Text = Locale.F($"{grid.Bars} {bars} · {(grid.Frames / (double)LoopEngine.Rate).ToString("0.00", Locale.Culture)} с");
    }

    private void GridChanged(object sender, KeyboardFocusChangedEventArgs e) => CommitGrid();
    private void GridSelectionChanged(object sender, SelectionChangedEventArgs e) => CommitGrid();

    private void TempoKeys(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitGrid();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void ClickToggleChanged(object sender, RoutedEventArgs e) => UpdateClick();
    private void ClickVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateClick();

    private void UpdateClick(bool edited = true)
    {
        if (edited && !ready) return;
        ClickVolumeValue.Text = $"{ClickVolume.Value:0}%";
        engine.Metronome(MetronomeButton.IsChecked == true, CountInButton.IsChecked == true, (float)(ClickVolume.Value / 100));
        if (edited) ProjectEdited();
    }
    private void PlayClicked(object sender, RoutedEventArgs e) => TogglePlay();
    private void RecordClicked(object sender, RoutedEventArgs e) => ToggleRecord();
    private void AddClicked(object sender, RoutedEventArgs e) => AddTrack(true);

    private TrackRow? AddTrack(bool scroll, TrackSnapshot? saved = null, MidiTrack? instrument = null)
    {
        if (rows.Count >= LoopEngine.MaxTracks) return null;
        var data = saved?.Settings;
        var track = data == null ? new Track() : new Track
        {
            Id = data.Id, Length = data.Frames, Samples = saved!.Samples,
            Audio = EditedAudio.Build(data.AudioEdit, saved.Samples, data.Frames),
            Volume = data.Volume, Pan = data.Pan, Shift = data.Shift, Muted = data.Muted, Solo = data.Solo, Fx = new FxRack(data.Fx),
            Midi = instrument ?? (data.Midi != null ? new MidiTrack(data.Midi) : null)
        };
        var row = new TrackRow(engine, engine.Add(track), rows.Count + 1, grid, data);
        row.RemoveRequested += RemoveTrack;
        row.ExportRequested += ExportTrack;
        row.SaveAudioProjectRequested += () => SaveProjectClicked(this, new RoutedEventArgs());
        row.AudioSeekRequested += phase => { if (!fileBusy && !settingsActive) SeekPlayback(phase); };
        row.ToggleTransportRequested += TogglePlay;
        row.TargetRequested += SelectRecordingTrack;
        row.InputListRequested += RefreshMicrophones;
        row.DragRequested += BeginTrackDrag;
        row.Edited += ProjectEdited;
        row.ContentEdited += ProjectContentEdited;
        if (history != null) row.WatchArrangementDrags(history);
        row.SetInputs(options.Driver, inputDevices);
        row.UpdateFxButton();
        rows.Add(row);
        TrackList.Children.Add(row);
        UpdateTrackCount();
        if (scroll)
        {
            if (!row.IsMidi) armedRow = row;
            row.BringIntoView();
        }
        UpdateRecordingTarget();
        ProjectEdited();
        return row;
    }

    private void RemoveTrack(TrackRow row)
    {
        if (row == recordingRow) return;
        row.CloseFxWindow();
        history.Commit();
        DetachTrack(row);
        if (rows.Count == 0) AddTrack(false);
        UpdateTrackCount();
        UpdateRecordingTarget();
        ProjectEdited();
    }

    private void DetachTrack(TrackRow row)
    {
        if (row == armedRow) armedRow = null;
        engine.Remove(row.Data);
        row.DisposeEffects();
        rows.Remove(row);
        TrackList.Children.Remove(row);
    }

    private void UpdateTrackCount()
    {
        for (int i = 0; i < rows.Count; i++) rows[i].SetNumber(i + 1);
        TrackCount.Text = rows.Count.ToString("00");
        AddButton.IsEnabled = AddSimpleButton.IsEnabled = AddInstrumentButton.IsEnabled =
            rows.Count < LoopEngine.MaxTracks;
    }

    private void TogglePlay()
    {
        repeatRecording = false;
        if (sound?.CanPlay != true) return;
        if (transport == Transport.Stopped)
        {
            CommitGrid();
            engine.Play();
        }
        else engine.Stop(sound.Position());
        Refresh();
    }

    private void SeekPlayback(int phase)
    {
        repeatRecording = false;
        engine.Seek(phase, sound?.Position() ?? 0);
        Refresh();
    }

    private void UpdateRuler(TransportView view)
    {
        int position = (int)(view.Phase / grid.BeatFrames);
        PositionLabel.Text = $"{position / grid.Beats + 1:00} / {position % grid.Beats + 1:00}";
        Ruler.SetPlayhead(view.Phase, view.State == Transport.Recording, !view.Playing || view.State == Transport.CountIn);
    }

    private void ToggleRecord()
    {
        if (sound == null || RecordBlockReason() != null) return;
        long frame = sound.Position();
        if (recordingRow != null)
        {
            repeatRecording = !engine.EndRecord(frame);
        }
        else
        {
            CommitGrid();
            recordingRow = NextTrack() ?? AddTrack(true);
            if (recordingRow == null) return;
            engine.Record(recordingRow.Data, frame, options.RecordingCorrectionFrames);
            repeatRecording = true;
        }
        Refresh();
    }

    private void Refresh()
    {
        if (sound == null)
        {
            UpdateRuler(engine.View(0));
            return;
        }
        while (sound.TryOutputIssue(out var issue))
        {
            repeatRecording = false;
            outputError = issue;
            engine.AbortAudio();
        }
        inputError = sound.InputError;
        if (inputError != null && recordingRow != null)
        {
            repeatRecording = false;
            engine.AbortAudio();
        }

        var snapshot = engine.View(sound.Position());
        CollectTakes();
        transport = snapshot.State;
        bool busy = transport is Transport.CountIn or Transport.Recording or Transport.Finishing;
        bool idle = transport == Transport.Stopped;
        if (!busy && recordingRow != null)
        {
            recordingRow = null;
            UpdateRecordingTarget();
        }
        bool hasPhase = snapshot.Playing && transport != Transport.CountIn;

        string status = transport switch
        {
            Transport.CountIn => Locale.F($"Отсчёт · {snapshot.Beat + 1} из {grid.Beats}"),
            Transport.Recording => Locale.T("Запись с микрофона"),
            Transport.Finishing => Locale.T("Завершение записи…"),
            Transport.Playing => Locale.T("Воспроизведение"),
            _ when missingTrackInput => Locale.T("Вход выбранной дорожки недоступен"),
            _ when sound.CanRecord => Locale.T("Готов к записи"),
            _ when inputError != null || outputError != null => Locale.T("Проверьте аудиоустройства"),
            _ => Locale.T("Подключение звука…")
        };

        string? recordBlock = RecordBlockReason();
        UpdateTransportButtons(status, idle, busy, recordBlock);
        PlayButton.IsEnabled = sound.CanPlay;
        TransportKeysHint.Text = Locale.T(repeatRecording && (recordBlock == null || transport == Transport.Finishing)
            ? RecordingKeys : PlaybackKeys);
        Tempo.IsEnabled = Meter.IsEnabled = Bars.IsEnabled = Microphone.IsEnabled = SettingsButton.IsEnabled = idle;
        NewProjectItem.IsEnabled = OpenProjectItem.IsEnabled = idle;
        Level.Width = Math.Clamp(75 * Math.Sqrt(snapshot.Peak), 0, 75);
        Level.Background = (Brush)FindResource(snapshot.Peak >= 0.98f ? "Rose" : "Mint");
        UpdateTuner(sound.InputFrequency, !missingTrackInput && inputError == null);
        int phase = hasPhase ? snapshot.Phase : -1;
        UpdateRuler(snapshot);
        foreach (var row in rows)
        {
            bool active = row == recordingRow;
            row.Display(phase, snapshot.Playing, active, active ? transport switch
            {
                Transport.Recording => Locale.T("Запись…"),
                Transport.Finishing => Locale.T("Завершение…"),
                Transport.CountIn => Locale.T("Отсчёт…"),
                _ => null
            } : null);
        }

        AudioHint.Text = outputError != null ? Locale.T("Ошибка вывода · проверьте настройки")
            : missingTrackInput ? Locale.T("Выберите другой вход дорожки")
            : inputError != null ? Locale.T("Нет входа · выберите микрофон")
            : sound.CanPlay && !sound.CanRecord ? Locale.T("Подключение микрофона…")
            : sound.CanPlay ? Locale.F($"{(options.Driver == AudioDriver.Wasapi ? "WASAPI" : "MME")}   ·   48 кГц")
            : Locale.T("Подключение звука…");
        AudioHint.ToolTip = outputError ?? inputError ?? (missingTrackInput ? NextTrack()?.Input?.Name : null);
    }

    private void UpdateTransportButtons(string status, bool idle, bool busy, string? recordBlock)
    {
        TransportStatus.ToolTip = $"{Locale.T("ТРАНСПОРТ")}\n{status}";
        StateDot.Fill = (Brush)FindResource(busy ? "Rose" : idle ? "Muted" : "Mint");
        string play = Locale.T(idle ? "Играть" : "Стоп");
        PlayIcon.Data = idle ? PlayShape : StopShape;
        PlayButton.ToolTip = play;
        AutomationProperties.SetName(PlayButton, play);

        bool countIn = transport == Transport.CountIn;
        string record = Locale.T(countIn ? "Отмена" : busy ? "Закончить" : "Записать");
        RecordIcon.Data = countIn ? CancelShape : busy ? StopShape : RecordShape;
        RecordButton.IsEnabled = recordBlock == null;
        string recordTip = $"{record} · {RecordShortcuts}";
        RecordButton.ToolTip = recordBlock != null ? $"{Locale.T("Запись недоступна")}\n{recordBlock}"
            : busy ? $"{recordTip}\n{status}" : recordTip;
        AutomationProperties.SetHelpText(RecordButton, recordBlock ?? recordTip);
        AutomationProperties.SetName(RecordButton, record);
    }

    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        settingsActive = true;
        DriverList.SelectedIndex = (int)options.Driver;
        FillSettings(options.Driver);
        CompensationAuto.IsChecked = options.RecordingCompensationMs == null;
        if (options.RecordingCompensationMs is double manual)
            CompensationValue.Text = manual.ToString("0.###", Locale.Culture);
        UpdateRecordingCompensation();
        SettingsOverlay.Visibility = Visibility.Visible;
        DriverList.Focus();
    }

    private void DraftDriverChanged(object sender, SelectionChangedEventArgs e)
    {
        if (settingsActive && DriverList.SelectedIndex >= 0)
            FillSettings((AudioDriver)DriverList.SelectedIndex);
    }

    private void FillSettings(AudioDriver driver)
    {
        var outputs = Enumerate(driver, false);
        OutputList.ItemsSource = outputs;
        OutputList.SelectedValue = driver == options.Driver && outputs.Any(device => device.Id == options.Output)
            ? options.Output : SoundSession.Default;

        int[] buffers = driver == AudioDriver.Wasapi ? [20, 35, 50, 80] : [45, 75, 105, 150];
        BufferList.ItemsSource = buffers.Select(ms => new Choice(ms, Locale.F($"{ms} мс"))).ToArray();
        BufferList.SelectedValue = driver == options.Driver ? options.Buffer : buffers[0];
        UpdateRecordingCompensation();
    }

    private void RecordingCompensationChanged(object sender, RoutedEventArgs e) => UpdateRecordingCompensation();

    private void UpdateRecordingCompensation()
    {
        if (!settingsActive || DriverList.SelectedIndex < 0 || BufferList.SelectedValue is not int buffer) return;
        bool automatic = CompensationAuto.IsChecked == true;
        CompensationValue.IsReadOnly = automatic;
        if (!automatic)
        {
            CompensationHint.Text = Locale.T("Увеличьте число, чтобы сдвинуть запись раньше.");
            return;
        }
        var driver = (AudioDriver)DriverList.SelectedIndex;
        CompensationValue.Text = AudioOptions.AutomaticCompensationMs(driver, buffer).ToString(Locale.Culture);
        CompensationHint.Text = driver == AudioDriver.Wasapi
            ? Locale.T("WASAPI выравнивает по меткам устройств. Здесь показана дополнительная поправка.")
            : Locale.T("MME оценивает задержку по размеру входного блока.");
    }

    private void CloseSettings(object sender, RoutedEventArgs e) => HideSettings();

    private void HideSettings()
    {
        settingsActive = false;
        SettingsOverlay.Visibility = Visibility.Collapsed;
        SettingsButton.Focus();
    }

    private void ApplySettings(object sender, RoutedEventArgs e)
    {
        var driver = (AudioDriver)DriverList.SelectedIndex;
        double? compensation = null;
        if (CompensationAuto.IsChecked != true)
        {
            string value = CompensationValue.Text;
            if ((!double.TryParse(value, NumberStyles.Float, Locale.Culture, out double ms) &&
                 !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out ms)) ||
                !double.IsFinite(ms) || ms is < -500 or > 500)
            {
                CompensationHint.Text = Locale.T("Введите число от −500 до 500 мс.");
                CompensationValue.Focus();
                return;
            }
            compensation = ms;
        }
        var next = new AudioOptions(driver, (int)(BufferList.SelectedValue ?? 20),
            driver == options.Driver ? options.Input : SoundSession.Default,
            (string)(OutputList.SelectedValue ?? SoundSession.Default), compensation);
        bool reopen = next.Driver != options.Driver || next.Buffer != options.Buffer || next.Output != options.Output;
        options = next;
        HideSettings();
        if (reopen)
        {
            PopulateMicrophones();
            ReopenSound();
        }
    }

    private void WindowKeys(object sender, KeyEventArgs e)
    {
        if (fileBusy) return;
        if (settingsActive)
        {
            if (e.Key == Key.Escape && !DriverList.IsDropDownOpen && !OutputList.IsDropDownOpen && !BufferList.IsDropDownOpen)
            {
                HideSettings();
                e.Handled = true;
            }
            return;
        }

        if (HistoryKeys.Handle(e, StepHistory) || e.IsRepeat) return;
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.N when transport == Transport.Stopped: NewProjectClicked(this, e); break;
                case Key.O when transport == Transport.Stopped: OpenProjectClicked(this, e); break;
                case Key.S: SaveProjectClicked(this, e); break;
                case Key.T: AddTrack(true); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        if (e.OriginalSource is TextBox or ComboBox or ComboBoxItem || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.R) ToggleRecord();
        else if (e.Key == Key.M) MetronomeButton.IsChecked = MetronomeButton.IsChecked != true;
        else return;
        e.Handled = true;
    }
}
