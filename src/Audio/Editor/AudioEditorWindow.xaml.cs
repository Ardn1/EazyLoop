using System.Windows;
using System.Windows.Input;

namespace EazyLoop;

public partial class AudioEditorWindow : Window
{
    private readonly EditHistory<AudioEdit> history;
    private readonly Action<AudioEdit?> apply;
    private readonly Action save;
    private readonly Action play;
    private float[] source;
    private int sourceFrames;
    private AudioEdit? applied;
    private LoopGrid grid;
    private int total;
    private int visible;
    private bool refreshingViewport;
    private bool playing;
    private int parked = -1;

    internal AudioEditorWindow(float[] samples, int count, AudioEdit? edit, string name, LoopGrid grid,
        Action<AudioEdit?> apply, Action save, Action play, Action<int> seek)
    {
        source = samples;
        sourceFrames = count;
        applied = edit;
        this.grid = grid;
        this.apply = apply;
        this.save = save;
        this.play = play;
        InitializeComponent();
        Rename(name);
        Timeline.Load(samples, count, edit ?? AudioEdit.Original(count));
        history = new(() => Timeline.State, Restore, (a, b) => a.Same(b));
        history.Changed += UpdateHistory;
        Timeline.Edited += () => { history.Commit(); Apply(); };
        Timeline.SelectionChanged += UpdateSelection;
        Timeline.SeekRequested += frame =>
        {
            if (frame >= this.grid.Frames) return;
            seek(frame);
            if (!playing) Timeline.Playhead(parked = frame);
        };
        Timeline.ZoomRequested += direction => Zoom.Value += direction;
        Timeline.WaveZoomRequested += direction => WaveZoom.Value += direction;
        UpdateViewport();
        UpdateHistory();
        UpdateSelection();
    }

    internal void Rename(string name)
    {
        TrackLabel.Text = name;
        Title = $"{name} · {Locale.T("Редактор аудио")}";
    }

    internal void Sync(float[] samples, int count, AudioEdit? edit)
    {
        if (ReferenceEquals(source, samples) && sourceFrames == count && ReferenceEquals(applied, edit)) return;
        source = samples;
        sourceFrames = count;
        applied = edit;
        Timeline.Load(samples, count, edit ?? AudioEdit.Original(count));
        history.Reset();
        UpdateViewport();
    }

    internal void ChangeGrid(LoopGrid value)
    {
        if (grid == value) return;
        grid = value;
        UpdateViewport();
    }

    internal void Transport(int phase, bool value)
    {
        if (value || playing) parked = -1;
        Timeline.Playhead(value ? phase : parked);
        if (playing == value) return;
        playing = value;
        PlayButton.Content = Locale.T(value ? "■ Стоп" : "▶ Играть");
    }

    private void Apply()
    {
        var edit = Timeline.State;
        applied = edit.IsOriginal(sourceFrames) ? null : edit;
        apply(applied);
        UpdateViewport();
    }

    private void Restore(AudioEdit edit)
    {
        Timeline.SetEdit(edit);
        Apply();
    }

    private void UpdateHistory()
    {
        UndoButton.IsEnabled = history.CanUndo;
        RedoButton.IsEnabled = history.CanRedo;
    }

    private void UpdateSelection()
    {
        SelectionLabel.Text = Timeline.Selection;
        DeleteButton.IsEnabled = SelectionLabel.Text.Length > 0;
    }

    private void UpdateViewport()
    {
        int nextTotal = Math.Max(grid.Frames, Timeline.State.Extent(sourceFrames));
        int nextVisible = Math.Max(1, (int)Math.Ceiling(nextTotal / Math.Pow(2, Zoom.Value)));
        int start = (int)Scroll.Value;
        if (visible != nextVisible || total != nextTotal)
            start += (visible - nextVisible) / 2;
        visible = nextVisible;
        total = nextTotal;
        refreshingViewport = true;
        Scroll.Maximum = total - visible;
        Scroll.SmallChange = visible / 12.0;
        Scroll.LargeChange = visible / 2.0;
        Scroll.Visibility = visible < total ? Visibility.Visible : Visibility.Collapsed;
        Scroll.Value = Math.Clamp(start, 0, total - visible);
        refreshingViewport = false;
        Timeline.SetViewport(grid, total, (int)Scroll.Value, visible);
    }

    private void ZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (history != null) UpdateViewport();
    }

    private void WaveZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Timeline == null) return;
        double scale = Math.Pow(2, e.NewValue);
        Timeline.SetWaveScale(scale);
        WaveZoomValue.Text = $"{scale:0}×";
    }

    private void ScrollChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!refreshingViewport && visible > 0) Timeline.SetViewport(grid, total, (int)e.NewValue, visible);
    }

    private void ToolChanged(object sender, RoutedEventArgs e)
    {
        if (Timeline != null) Timeline.Cutting = CutTool?.IsChecked == true;
    }

    private void SnapChanged(object sender, RoutedEventArgs e)
    {
        if (Timeline != null) Timeline.Snap = Snap.IsChecked == true;
    }

    private void WindowKeys(object sender, KeyEventArgs e)
    {
        if (HistoryKeys.Handle(e, forward => history.Step(forward)) || e.IsRepeat) return;
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) save();
        else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None) Timeline.DeleteSelection();
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (CutTool.IsChecked == true) MoveTool.IsChecked = true;
            else CutTool.IsChecked = true;
        }
        else return;
        e.Handled = true;
    }

    private void SaveClicked(object sender, RoutedEventArgs e) => save();
    private void PlayClicked(object sender, RoutedEventArgs e) => play();
    private void UndoClicked(object sender, RoutedEventArgs e) => history.Step(false);
    private void RedoClicked(object sender, RoutedEventArgs e) => history.Step(true);
    private void DeleteClicked(object sender, RoutedEventArgs e) => Timeline.DeleteSelection();
}
