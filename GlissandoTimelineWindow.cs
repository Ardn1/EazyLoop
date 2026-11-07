using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace EazyLoop;

internal sealed class GlissandoTimelineWindow : Window
{
    private readonly GlissandoRoll roll;
    private readonly ScrollBar pitch = new() { Orientation = Orientation.Vertical, SmallChange = 1, LargeChange = 12, Width = 15 };
    private readonly ScrollBar time = new() { Orientation = Orientation.Horizontal, SmallChange = 240, LargeChange = 960, Height = 15,
        Margin = new Thickness(62, 0, 15, 0) };
    private readonly Slider zoom = new() { Minimum = 1, Maximum = 12, Value = 1, SmallChange = 1, LargeChange = 2, Width = 104 };
    private readonly TextBlock title = new() { FontSize = 21, FontWeight = FontWeights.SemiBold };
    private readonly Button play;
    private LoopGrid grid;
    private bool updatingViewport;
    private Window? toneWindow;

    internal GlissandoTimelineWindow(MidiTrack track, string name, LoopGrid grid, Action edited, Action togglePlay)
    {
        this.grid = grid;
        Width = 1090;
        Height = 685;
        MinWidth = 760;
        MinHeight = 480;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "Background");
        SetResourceReference(ForegroundProperty, "Ink");
        Rename(name);
        var history = track.TimelineHistory!;
        Slider? smoothing = null;
        bool syncing = false;
        string hint = Locale.T("Щелчок добавляет точку. Перетаскивание и стрелки двигают её. Правый щелчок удаляет.");
        string newPhraseHint = Locale.T("Поставьте первую точку новой фразы в свободном месте.");
        var selection = Caption(hint, new Thickness(0, 0, 10, 0));
        selection.ToolTip = hint;

        var layout = new DockPanel { Margin = new Thickness(16) };
        Content = layout;
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        play = Button(Locale.T("▶ Играть"), Locale.T("Играть / остановить"), togglePlay);
        actions.Children.Add(play);
        actions.Children.Add(Button(Locale.T("Тембр…"), Locale.T("Форма волны"), () =>
        {
            if (toneWindow != null) { toneWindow.Activate(); return; }
            toneWindow = new SynthWindow(track, edited) { Owner = this };
            toneWindow.Closed += (_, _) => toneWindow = null;
            toneWindow.Show();
        }));
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(title);

        roll = new GlissandoRoll(track);
        AutomationProperties.SetName(roll, Locale.T("Редактор глиссандо"));
        history.WatchDrag(roll);
        roll.Edited += () => { history.Commit(); edited(); };
        roll.ScrollPitch += delta => pitch.Value += delta;
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(toolbar, Dock.Top);
        layout.Children.Add(toolbar);
        var undo = HistoryButton(1, Locale.T("Отменить · Ctrl+Z"), () => StepHistory(false));
        var redo = HistoryButton(-1, Locale.T("Повторить · Ctrl+Y или Ctrl+Shift+Z"), () => StepHistory(true));
        toolbar.Children.Add(undo);
        toolbar.Children.Add(redo);
        toolbar.Children.Add(Button(Locale.T("Новая фраза"), newPhraseHint,
            () => { roll.ClearSelection(); selection.Text = newPhraseHint; roll.Focus(); }));
        var pencil = Toggle(Locale.T("Карандаш"), Locale.T("Рисуйте кривую, удерживая левую кнопку мыши."), false);
        var snap = Toggle(Locale.T("Привязка"), Locale.T("Привязать точку к ритмической сетке и ближайшей ноте."), true);
        pencil.Click += (_, _) => roll.Pencil = pencil.IsChecked == true;
        snap.Click += (_, _) => roll.Snap = snap.IsChecked == true;
        toolbar.Children.Add(pencil);
        toolbar.Children.Add(snap);
        var divisions = new ComboBox { Width = 67, Height = 29, ItemsSource = new[] { "1/4", "1/8", "1/16", "1/32" },
            SelectedIndex = 2, Margin = new Thickness(0, 0, 10, 3) };
        AutomationProperties.SetName(divisions, Locale.T("Сетка"));
        divisions.SelectionChanged += (_, _) => { roll.Step = MidiTrackSettings.Ppq >> divisions.SelectedIndex; roll.InvalidateVisual(); };
        toolbar.Children.Add(divisions);
        AutomationProperties.SetName(zoom, Locale.T("Масштаб по времени"));
        toolbar.Children.Add(Caption(Locale.T("Масштаб по времени"), new Thickness(0, 0, 5, 3)));
        toolbar.Children.Add(zoom);

        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        var smooth = FxWindow.ParameterLine(Locale.T("Сглаживание"), 0, 100, track.Timeline!.Smoothing * 100,
            value => $"{value:0} %", value =>
            {
                if (syncing) return;
                var timeline = track.Timeline!;
                track.ChangeTimeline(new GlissandoTimeline(timeline.Phrases, value / 100));
                roll.InvalidateVisual();
                history.Commit();
                edited();
            }, control => { smoothing = (Slider)control; history.WatchDrag(control); });
        smooth.Width = 252;
        smooth.ToolTip = Locale.T("Меняет переходы между точками и сохраняет высоту в самих точках.");
        DockPanel.SetDock(smooth, Dock.Right);
        footer.Children.Add(smooth);
        roll.SelectionChanged += point =>
        {
            if (point is not GlissandoPoint p) { selection.Text = hint; return; }
            int beat = p.Position / MidiTrackSettings.Ppq;
            int note = (int)Math.Round(p.Cents / 100.0);
            int offset = p.Cents - note * 100;
            string cents = offset == 0 ? "" : $" {offset:+0;−0;0} ¢";
            selection.Text = $"{PianoRollSurface.PitchName(note)}{cents}   ·   {beat / this.grid.Beats + 1}.{beat % this.grid.Beats + 1}.{p.Position % MidiTrackSettings.Ppq:000}";
        };
        footer.Children.Add(selection);

        DockPanel.SetDock(time, Dock.Bottom);
        pitch.Style = time.Style = (Style)FindResource("PianoScroll");
        layout.Children.Add(time);
        DockPanel.SetDock(pitch, Dock.Right);
        layout.Children.Add(pitch);
        var frame = new Border { Child = roll, BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        layout.Children.Add(frame);
        AutomationProperties.SetName(pitch, Locale.T("Прокрутка по нотам"));
        AutomationProperties.SetName(time, Locale.T("Прокрутка по времени"));
        pitch.ValueChanged += (_, _) => UpdateViewport();
        time.ValueChanged += (_, _) => UpdateViewport();
        zoom.ValueChanged += (_, _) => UpdateViewport();
        roll.SizeChanged += (_, _) => UpdateViewport();
        double center = track.Timeline.Count == 0 ? 66 : track.Timeline.Contours[0].Points[0].Cents / 100.0;
        pitch.Maximum = 127;
        pitch.Value = Math.Clamp(127 - center - 12, 0, 127);
        UpdateViewport();
        history.Changed += UpdateHistory;
        Closed += (_, _) => history.Changed -= UpdateHistory;
        PreviewKeyDown += (_, e) => HistoryKeys.Handle(e, StepHistory);
        UpdateHistory();

        void UpdateHistory()
        {
            undo.IsEnabled = history.CanUndo;
            redo.IsEnabled = history.CanRedo;
        }

        void StepHistory(bool forward)
        {
            if (!history.Step(forward)) return;
            roll.ClearSelection();
            syncing = true;
            try { smoothing!.Value = track.Timeline!.Smoothing * 100; }
            finally { syncing = false; }
            edited();
        }
    }

    internal void Rename(string name)
    {
        title.Text = name;
        Title = $"{name} · {Locale.T("Редактор глиссандо")}";
    }

    internal void ChangeGrid(LoopGrid value)
    {
        grid = value;
        UpdateViewport();
    }

    internal void Transport(int phase, bool playing)
    {
        play.Content = playing ? Locale.T("■ Стоп") : Locale.T("▶ Играть");
        roll.Playhead(phase < 0 ? -1 : (int)Math.Round(phase * MidiTrackSettings.Ppq / grid.BeatFrames));
    }

    private void UpdateViewport()
    {
        if (updatingViewport) return;
        updatingViewport = true;
        try
        {
            int ticks = MidiTrackSettings.Ticks(grid);
            pitch.Maximum = Math.Max(0, 128 - roll.RowsVisible);
            pitch.ViewportSize = roll.RowsVisible;
            time.Maximum = ticks - ticks / zoom.Value;
            time.ViewportSize = ticks / zoom.Value;
            time.Visibility = zoom.Value > 1 ? Visibility.Visible : Visibility.Collapsed;
            roll.SetViewport(grid, zoom.Value, time.Value, 127 - (int)pitch.Value);
        }
        finally { updatingViewport = false; }
    }

    private static TextBlock Caption(string text, Thickness margin)
    {
        var label = new TextBlock { Text = text, FontSize = 11, Margin = margin, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Soft");
        return label;
    }

    private static Button Button(object content, string hint, Action action)
    {
        var button = new Button { Content = content, ToolTip = hint, Height = 29, Margin = new Thickness(0, 0, 6, 3),
            Style = (Style)Application.Current.FindResource("Compact") };
        AutomationProperties.SetName(button, content as string ?? hint);
        button.Click += (_, _) => action();
        return button;
    }

    private static Button HistoryButton(int direction, string hint, Action action)
    {
        var button = Button(direction, hint, action);
        button.Style = (Style)Application.Current.FindResource("HistoryButton");
        return button;
    }

    private static ToggleButton Toggle(string text, string hint, bool active) => new()
    {
        Content = text, ToolTip = hint, IsChecked = active, Height = 29, Padding = new Thickness(8, 0, 8, 0),
        Margin = new Thickness(0, 0, 6, 3), Style = (Style)Application.Current.FindResource("PillToggle")
    };
}
