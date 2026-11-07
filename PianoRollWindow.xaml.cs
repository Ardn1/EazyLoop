using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NAudio.Vst3;

namespace EazyLoop;

public partial class PianoRollWindow : Window
{
    private sealed record GridOption(string Name, int Step);
    private sealed record ChordOption(string Name, int[] Intervals, int ScaleVoices = 0);
    private sealed record ScaleOption(string Name, DiatonicScale Scale);
    private sealed record LaneOption(string Name, MidiLane Lane);

    private readonly MidiTrack track;
    private readonly Action edited;
    private readonly Action togglePlay;
    private LoopGrid grid;
    private Window? instrumentWindow;
    private bool ready;
    private bool viewportUpdating;
    private double pianoScroll = 51, staffScroll = 27;

    internal PianoRollWindow(MidiTrack track, string title, LoopGrid grid, Action edited, Action togglePlay)
    {
        this.track = track;
        this.grid = grid;
        this.edited = edited;
        this.togglePlay = togglePlay;
        InitializeComponent();
        TrackLabel.Text = title;
        GridChoice.ItemsSource = new GridOption[] { new("1/4", 960), new("1/8", 480), new("1/16", 240),
            new("1/32", 120), new("1/8 T", 320), new("1/16 T", 160) };
        ChordChoice.ItemsSource = new ChordOption[]
        {
            new(Locale.T("Нота"), [0]), new("SimpleChord", [], 3), new("SimpleChord4", [], 4),
            new("C", [0, 4, 7]), new("Cm", [0, 3, 7]),
            new("C5", [0, 7]), new("Cdim", [0, 3, 6]), new("Caug", [0, 4, 8]),
            new("Csus2", [0, 2, 7]), new("Csus4", [0, 5, 7]), new("C6", [0, 4, 7, 9]),
            new("C7", [0, 4, 7, 10]), new("Cmaj7", [0, 4, 7, 11]), new("Cm7", [0, 3, 7, 10]),
            new("Cm7♭5", [0, 3, 6, 10]), new("Cdim7", [0, 3, 6, 9]),
            new("C9", [0, 4, 7, 10, 14]), new("C13", [0, 4, 7, 10, 14, 21])
        };
        ScaleChoice.ItemsSource = Enumerable.Range(0, DiatonicScale.Count).Select(DiatonicScale.FromIndex)
            .Select(scale => new ScaleOption(scale.DisplayName, scale)).ToArray();
        LaneChoice.ItemsSource = new LaneOption[]
        {
            new(Locale.T("Сила · Velocity"), MidiLane.Velocity), new(Locale.T("Громкость · CC7"), MidiLane.Volume),
            new(Locale.T("Панорама · CC10"), MidiLane.Pan), new(Locale.T("Модуляция · CC1"), MidiLane.Modulation),
            new(Locale.T("Экспрессия · CC11"), MidiLane.Expression), new(Locale.T("Педаль · CC64"), MidiLane.Sustain),
            new(Locale.T("Высота · Pitch Bend"), MidiLane.Pitch)
        };
        GridChoice.SelectedIndex = 2;
        ChordChoice.SelectedIndex = ScaleChoice.SelectedIndex = LaneChoice.SelectedIndex = 0;
        Roll.Setup(track);
        Roll.Changed += () => { track.SequenceHistory.Commit(); edited(); };
        Roll.GestureStarted += track.SequenceHistory.BeginGesture;
        Roll.GestureEnded += track.SequenceHistory.EndGesture;
        track.SequenceHistory.Changed += ShowHistory;
        Closed += (_, _) => track.SequenceHistory.Changed -= ShowHistory;
        Roll.SelectionChanged += ShowSelection;
        Roll.PitchScroll += delta => PitchScrollBar.Value += delta;
        ready = true;
        UpdateInstrumentLabel();
        UpdateModes();
        ShowSelection();
        ShowHistory();
    }

    private void ShowHistory()
    {
        UndoButton.IsEnabled = track.SequenceHistory.CanUndo;
        RedoButton.IsEnabled = track.SequenceHistory.CanRedo;
    }

    private void UndoClicked(object sender, RoutedEventArgs e) => StepHistory(false);
    private void RedoClicked(object sender, RoutedEventArgs e) => StepHistory(true);

    private void StepHistory(bool forward)
    {
        var previous = track.Notes;
        if (!track.SequenceHistory.Step(forward)) return;
        Roll.SequenceRestored(previous);
        edited();
    }

    internal void Rename(string name) => TrackLabel.Text = name;

    internal void ChangeGrid(LoopGrid value)
    {
        if (grid == value) return;
        grid = value;
        UpdateViewport();
        ShowSelection();
    }

    internal void Transport(int phase, bool playing)
    {
        PlayButton.Content = playing ? Locale.T("■ Стоп") : Locale.T("▶ Играть");
        int tick = phase < 0 ? -1 : (int)Math.Round(phase * MidiTrackSettings.Ppq / grid.BeatFrames);
        Roll.Playhead(tick);
        if (track.Error != null) UpdateInstrumentLabel();
    }

    internal bool CaptureState() => track.SavePluginState();

    private void UpdateInstrumentLabel()
    {
        InstrumentLabel.Text = track.Error ?? (track.Curve != null ? "Glissando Synth" :
            track.Patch != null ? Locale.T("SimpleSynth · встроенный синтезатор")
            : $"{track.Reference?.Name} · VST3" + (track.Plugin == null ? Locale.T(" · ожидает подключения") : ""));
        InstrumentButton.Content = track.Curve != null ? Locale.T("Кривая…") : track.Patch != null ? Locale.T("Тембр…") :
            track.Plugin == null ? Locale.T("Подключить…") : Locale.T("Редактор VST3…");
    }

    private void UpdateModes()
    {
        if (!ready) return;
        Roll.Step = ((GridOption)GridChoice.SelectedItem).Step;
        Roll.Snap = SnapButton.IsChecked == true;
        var chord = (ChordOption)ChordChoice.SelectedItem;
        bool usesScale = chord.ScaleVoices > 0;
        Roll.Intervals = chord.Intervals;
        Roll.ScaleVoices = chord.ScaleVoices;
        Roll.Scale = usesScale ? ((ScaleOption)ScaleChoice.SelectedItem).Scale : null;
        ScaleChoice.Visibility = usesScale ? Visibility.Visible : Visibility.Collapsed;
        bool staff = StaffButton.IsChecked == true;
        if (staff != Roll.IsStaff)
        {
            if (Roll.IsStaff) staffScroll = PitchScrollBar.Value;
            else pianoScroll = PitchScrollBar.Value;
            Roll.SetStaffMode(staff);
            viewportUpdating = true;
            PitchScrollBar.Maximum = Roll.PitchUnits - 1;
            PitchScrollBar.Value = staff ? staffScroll : pianoScroll;
            viewportUpdating = false;
        }
        Roll.AddSharp = staff && SharpButton.IsChecked == true;
        SharpButton.Visibility = staff ? Visibility.Visible : Visibility.Collapsed;
        UpdateViewport();
    }

    private void UpdateViewport()
    {
        if (!ready || viewportUpdating) return;
        viewportUpdating = true;
        try
        {
            double ticks = MidiTrackSettings.Ticks(grid), visible = ticks / Zoom.Value;
            TimeScrollBar.Maximum = Math.Max(0, ticks - visible);
            TimeScrollBar.ViewportSize = visible;
            TimeScrollBar.IsEnabled = Zoom.Value > 1;
            PitchScrollBar.Maximum = Math.Max(0, Roll.PitchUnits - Roll.RowsVisible);
            PitchScrollBar.ViewportSize = Roll.RowsVisible;
            Roll.SetViewport(grid, Zoom.Value, TimeScrollBar.Value, Roll.PitchUnits - 1 - (int)PitchScrollBar.Value,
                ((LaneOption)LaneChoice.SelectedItem).Lane);
        }
        finally { viewportUpdating = false; }
    }

    private void ModesChanged(object sender, RoutedEventArgs e) => UpdateModes();
    private void ModeSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateModes();
    private void LayoutChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateViewport();
    private void RollResized(object sender, SizeChangedEventArgs e) => UpdateViewport();
    private void PlayClicked(object sender, RoutedEventArgs e) => togglePlay();
    private void DeleteClicked(object sender, RoutedEventArgs e) => Roll.RemoveSelected();
    private void CopyClicked(object sender, RoutedEventArgs e) => Roll.CopyNotes();
    private void PasteClicked(object sender, RoutedEventArgs e) => PasteNotes();

    private void PasteNotes()
    {
        if (Roll.PasteNotes() is { } message)
            MessageBox.Show(this, message, Locale.T("Вставка нот"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowSelection()
    {
        var note = Roll.Selected;
        CopyButton.IsEnabled = note != null;
        NoteInspector.IsEnabled = note != null;
        NotePitch.Text = note == null ? "" : PianoRollSurface.PitchName(note.Pitch);
        int bar = grid.Beats * MidiTrackSettings.Ppq;
        NotePosition.Text = note == null ? "" : $"{note.Start / bar + 1}.{note.Start % bar / MidiTrackSettings.Ppq + 1}.{note.Start % MidiTrackSettings.Ppq:000}";
        NoteLength.Text = note == null ? "" : (note.Length / (double)MidiTrackSettings.Ppq).ToString("0.###", CultureInfo.CurrentCulture);
        NoteVelocity.Text = note?.Velocity.ToString() ?? "";
    }

    private void CommitField(object sender)
    {
        if (Roll.Selected is not { } selected) return;
        int start = selected.Start, length = selected.Length, velocity = selected.Velocity, pitch = selected.Pitch;
        if (sender == NotePitch && PianoRollSurface.TryPitch(NotePitch.Text, out int key)) pitch = key;
        else if (sender == NotePosition)
        {
            string[] parts = NotePosition.Text.Split('.');
            if (parts.Length is 2 or 3 && int.TryParse(parts[0], out int bar) && int.TryParse(parts[1], out int beat))
            {
                int tick = parts.Length == 3 && int.TryParse(parts[2], out int tail) ? tail : 0;
                start = (Math.Clamp(bar, 1, grid.Bars) - 1) * grid.Beats * MidiTrackSettings.Ppq +
                    (Math.Clamp(beat, 1, grid.Beats) - 1) * MidiTrackSettings.Ppq + Math.Clamp(tick, 0, MidiTrackSettings.Ppq - 1);
            }
        }
        else if (sender == NoteLength &&
            (double.TryParse(NoteLength.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double beats) ||
             double.TryParse(NoteLength.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out beats)) && double.IsFinite(beats))
            length = (int)Math.Round(Math.Clamp(beats, 0, grid.Beats * grid.Bars) * MidiTrackSettings.Ppq);
        else if (sender == NoteVelocity && int.TryParse(NoteVelocity.Text, out int value)) velocity = value;

        if (start != selected.Start || length != selected.Length || velocity != selected.Velocity || pitch != selected.Pitch)
            Roll.UpdateSelected(start, length, velocity, pitch);
        ShowSelection();
    }

    private void FieldLost(object sender, KeyboardFocusChangedEventArgs e) => CommitField(sender);
    private void FieldKeys(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitField(sender);
        Roll.Focus();
        e.Handled = true;
    }

    private void WindowKeys(object sender, KeyEventArgs e)
    {
        if (HistoryKeys.Handle(e, StepHistory)) return;
        if (e.OriginalSource is TextBox or ComboBox or ComboBoxItem || e.IsRepeat) return;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.Control && e.Key == Key.C) Roll.CopyNotes();
        else if (modifiers == ModifierKeys.Control && e.Key == Key.V) PasteNotes();
        else if (modifiers == ModifierKeys.None && e.Key == Key.Delete) Roll.RemoveSelected();
        else return;
        e.Handled = true;
    }

    private void InstrumentClicked(object sender, RoutedEventArgs e) => OpenInstrument();

    internal void OpenInstrument()
    {
        if (instrumentWindow != null) { instrumentWindow.Activate(); return; }
        try
        {
            if (track.Curve != null)
                instrumentWindow = new GlissandoWindow(track, edited) { Owner = this };
            else if (track.Patch != null)
                instrumentWindow = new SynthWindow(track, edited) { Owner = this };
            else
            {
                if (track.Plugin == null && !Reconnect()) return;
                instrumentWindow = FxWindow.CreatePluginEditor(this, track.Plugin!, track.Reference!.Name);
            }
            instrumentWindow.Closed += (_, _) =>
            {
                instrumentWindow = null;
                try { if (track.SavePluginState()) edited(); }
                catch (Exception ex) { Report(ex); }
            };
            instrumentWindow.Show();
        }
        catch (Exception ex) { instrumentWindow = null; Report(ex); }
    }

    private bool Reconnect()
    {
        var reference = track.Reference!;
        string? path = File.Exists(reference.Path) || Directory.Exists(reference.Path) ? reference.Path : FxWindow.ChooseModule(this);
        if (path == null) return false;
        Vst3Module? module = Vst3Module.Load(path);
        try
        {
            var type = module.GetClasses().FirstOrDefault(info => string.Equals(info.ClassId, reference.ClassId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(Locale.T("Выбранный модуль не содержит сохранённый инструмент."));
            track.Connect(module, type, path);
            module = null;
            UpdateInstrumentLabel();
            edited();
            return true;
        }
        finally { module?.Dispose(); }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        instrumentWindow?.Close();
        base.OnClosing(e);
    }

    private void Report(Exception ex) => MessageBox.Show(this, ex.Message, Locale.T("MIDI-инструмент"), MessageBoxButton.OK, MessageBoxImage.Warning);
}
