using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EazyLoop;

public partial class DrumWindow : Window
{
    private sealed record Division(string Name, int Ticks);
    private readonly MidiTrack track;
    private readonly Action edited;
    private readonly Action togglePlay;
    private readonly Action<int> openSettings;
    private LoopGrid grid;
    private bool ready, settingViewport, showingHit;

    internal DrumWindow(MidiTrack track, string title, LoopGrid grid, Action edited, Action play, Action<int> seek,
        Action<int> settings)
    {
        this.track = track;
        this.edited = edited;
        this.grid = grid;
        togglePlay = play;
        openSettings = settings;
        InitializeComponent();
        Rename(title);
        StepChoice.ItemsSource = new Division[] { new("1/4", 960), new("1/8", 480), new("1/16", 240),
            new("1/32", 120), new("1/8 T", 320), new("1/16 T", 160) };
        StepChoice.SelectedIndex = 2;
        Steps.Setup(track);
        Steps.Edited += () => { track.SequenceHistory.Commit(); edited(); };
        track.SequenceHistory.WatchDrag(HitVelocity);
        track.SequenceHistory.Changed += ShowHistory;
        Closed += (_, _) => track.SequenceHistory.Changed -= ShowHistory;
        Steps.SelectionChanged += ShowHit;
        Steps.SettingsRequested += settings;
        Steps.SeekRequested += seek;
        ready = true;
        KitChanged();
        UpdateViewport();
        ShowHistory();
    }

    private void ShowHistory()
    {
        UndoButton.IsEnabled = track.SequenceHistory.CanUndo;
        RedoButton.IsEnabled = track.SequenceHistory.CanRedo;
    }

    private void WindowKeys(object sender, KeyEventArgs e)
    {
        if (HistoryKeys.Handle(e, StepHistory) || e.IsRepeat || e.OriginalSource is ComboBox or ComboBoxItem) return;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.Control && e.Key == Key.C) Steps.CopyNotes();
        else if (modifiers == ModifierKeys.Control && e.Key == Key.V) PasteNotes();
        else if (modifiers == ModifierKeys.None && e.Key == Key.Delete)
        {
            Steps.CancelGesture();
            Steps.RemoveSelected();
        }
        else if (e.Key != Key.Escape || !Steps.CancelGesture()) return;
        e.Handled = true;
    }

    private void UndoClicked(object sender, RoutedEventArgs e) => StepHistory(false);
    private void RedoClicked(object sender, RoutedEventArgs e) => StepHistory(true);

    private void StepHistory(bool forward)
    {
        Steps.CancelGesture();
        if (!track.SequenceHistory.Step(forward)) return;
        Steps.SyncSelection();
        edited();
    }

    internal void Rename(string title) => TrackLabel.Text = title;

    internal void ChangeGrid(LoopGrid value)
    {
        if (grid == value) return;
        grid = value;
        UpdateViewport();
        Steps.SyncSelection();
    }

    internal void KitChanged()
    {
        KitLabel.Text = Locale.F($"SimpleDrums  ·  {track.Kit!.Count} из 8 полос");
        Steps.SyncSelection();
    }

    internal void Transport(int phase, bool playing)
    {
        PlayButton.Content = playing ? Locale.T("■ Стоп") : Locale.T("▶ Играть");
        Steps.Playhead(phase / grid.BeatFrames * MidiTrackSettings.Ppq);
    }

    private void UpdateViewport()
    {
        if (!ready || settingViewport) return;
        settingViewport = true;
        try
        {
            double ticks = MidiTrackSettings.Ticks(grid);
            Scroll.Maximum = Math.Max(0, ticks - ticks / Zoom.Value);
            Scroll.Visibility = Zoom.Value > 1 ? Visibility.Visible : Visibility.Collapsed;
            Steps.SetViewport(grid, ((Division)StepChoice.SelectedItem).Ticks, Zoom.Value, Scroll.Value);
        }
        finally { settingViewport = false; }
    }

    private void ShowHit()
    {
        showingHit = true;
        CopyButton.IsEnabled = Steps.HasSelection;
        var hit = Steps.Selected;
        int index = hit == null ? -1 : Array.IndexOf(DrumKit.Keys, hit.Pitch);
        bool visible = index >= 0 && index < track.Kit!.Count;
        HitControls.IsEnabled = visible;
        HitLabel.Text = visible ? track.Kit!.PadName(index) : Locale.T("Выберите удар");
        HitVelocity.Value = visible ? hit!.Velocity : 100;
        VelocityLabel.Text = visible ? hit!.Velocity.ToString() : "—";
        showingHit = false;
    }

    private void StepChanged(object sender, SelectionChangedEventArgs e) => UpdateViewport();
    private void ViewportChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateViewport();
    private void PlayClicked(object sender, RoutedEventArgs e) => togglePlay();
    private void KitClicked(object sender, RoutedEventArgs e) => openSettings(-1);
    private void CopyClicked(object sender, RoutedEventArgs e) => Steps.CopyNotes();
    private void PasteClicked(object sender, RoutedEventArgs e) => PasteNotes();

    private void PasteNotes()
    {
        Steps.CancelGesture();
        if (Steps.PasteNotes() is { } message)
            MessageBox.Show(this, message, Locale.T("Вставка нот"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void VelocityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready || showingHit) return;
        Steps.SetVelocity((int)Math.Round(HitVelocity.Value));
    }

}
