using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace EazyLoop;

internal sealed class GlissandoWindow : Window
{
    private sealed record Shape(string Name, WaveShape Wave);

    public GlissandoWindow(MidiTrack track, Action edited)
    {
        Title = "Glissando Synth";
        Width = 940;
        Height = 520;
        MinWidth = 800;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Background");
        SetResourceReference(ForegroundProperty, "Ink");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        var history = new EditHistory<(SynthPatch Patch, GlissandoCurve Curve)>(
            () => (track.Patch!, track.Curve!), value =>
            {
                track.ChangePatch(value.Patch);
                track.ChangeCurve(value.Curve);
            }, (a, b) => a.Patch == b.Patch && a.Curve.Smoothing == b.Curve.Smoothing &&
                a.Curve.Points.AsSpan().SequenceEqual(b.Curve.Points.AsSpan()));
        var layout = new DockPanel { Margin = new Thickness(18) };
        Content = layout;
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock { Text = Title, FontSize = 22, FontWeight = FontWeights.SemiBold });
        header.Children.Add(Caption(Locale.T("Кривая высоты растягивается на длительность каждой MIDI-ноты.")));
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);

        var sidebar = new StackPanel { Width = 248, Margin = new Thickness(16, 0, 0, 0) };
        DockPanel.SetDock(sidebar, Dock.Right);
        layout.Children.Add(sidebar);
        var graph = new DockPanel();
        layout.Children.Add(graph);
        var plot = new GlissandoPlot();
        AutomationProperties.SetName(plot, Locale.T("Кривая высоты ноты"));
        history.WatchDrag(plot);
        plot.Changed += SaveCurve;
        var selection = Caption("");
        plot.SelectionChanged += point => selection.Text = point is GlissandoPoint p
            ? Locale.F($"{p.Position / 10.0:0.#}% ноты · {p.Cents / 100.0:+0.##;-0.##;0} пт")
            : Locale.T("Высота, полутоны ↑");
        plot.Update(track.Curve!);
        var curveControls = new StackPanel { Margin = new Thickness(0, 4, 0, 10) };
        var voiceControls = new StackPanel();
        var shapes = new Shape[] { new(Locale.T("Синус"), WaveShape.Sine), new(Locale.T("Треугольник"), WaveShape.Triangle),
            new(Locale.T("Пила"), WaveShape.Saw), new(Locale.T("Квадрат"), WaveShape.Square) };

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(toolbar, Dock.Top);
        graph.Children.Add(toolbar);
        var undo = HistoryButton(1, Locale.T("Отменить · Ctrl+Z"), () => Step(false));
        var redo = HistoryButton(-1, Locale.T("Повторить · Ctrl+Y или Ctrl+Shift+Z"), () => Step(true));
        toolbar.Children.Add(undo);
        toolbar.Children.Add(redo);
        history.Changed += UpdateHistory;
        UpdateHistory();
        PreviewKeyDown += (_, e) => HistoryKeys.Handle(e, Step);

        var pencil = Toggle(Locale.T("Карандаш"), Locale.T("Рисуйте кривую, удерживая левую кнопку мыши."), false);
        pencil.Click += (_, _) => plot.Pencil = pencil.IsChecked == true;
        toolbar.Children.Add(pencil);
        var snap = Toggle(Locale.T("По полутонам"), Locale.T("Привязать высоту точки к ближайшему полутону."), true);
        snap.Click += (_, _) => plot.Snap = snap.IsChecked == true;
        toolbar.Children.Add(snap);
        var clear = Button(Locale.T("Выровнять"), Locale.T("Заменить кривую прямой на исходной высоте ноты."),
            () => ChangeCurve(new GlissandoCurve(GlissandoCurve.Flat, track.Curve!.Smoothing)));
        toolbar.Children.Add(clear);

        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        footer.Children.Add(Caption(Locale.T("Щелчок добавляет точку. Перетаскивание и стрелки двигают её. Правый щелчок удаляет.")));
        footer.Children.Add(selection);
        DockPanel.SetDock(footer, Dock.Bottom);
        graph.Children.Add(footer);
        graph.Children.Add(plot);

        sidebar.Children.Add(new TextBlock { Text = Locale.T("Кривая высоты ноты"), FontWeight = FontWeights.SemiBold });
        sidebar.Children.Add(curveControls);
        var preview = Button(Locale.T("▶ Прослушать A4"), Locale.T("Сыграть ля первой октавы с этой кривой."),
            () => track.PreviewNote(69, 100));
        preview.HorizontalAlignment = HorizontalAlignment.Left;
        sidebar.Children.Add(preview);
        sidebar.Children.Add(new TextBlock { Text = Locale.T("Тембр"), FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 18, 0, 7) });
        sidebar.Children.Add(voiceControls);
        BuildControls();

        void BuildControls()
        {
            curveControls.Children.Clear();
            var curve = track.Curve!;
            var smoothing = FxWindow.ParameterLine(Locale.T("Сглаживание"), 0, 100, curve.Smoothing * 100,
                value => $"{value:0} %", value => ChangeCurve(new GlissandoCurve(track.Curve!.Points, value / 100)),
                history.WatchDrag);
            smoothing.ToolTip = Locale.T("Меняет переходы между точками и сохраняет высоту в самих точках.");
            curveControls.Children.Add(smoothing);

            voiceControls.Children.Clear();
            var patch = track.Patch!;
            var wave = new ComboBox { ItemsSource = shapes, DisplayMemberPath = nameof(Shape.Name), Height = 32,
                SelectedItem = shapes.First(shape => shape.Wave == patch.Wave), Margin = new Thickness(0, 0, 0, 6) };
            AutomationProperties.SetName(wave, Locale.T("Форма волны"));
            wave.SelectionChanged += (_, _) =>
            {
                if (wave.SelectedItem is Shape shape) ChangePatch(track.Patch! with { Wave = shape.Wave, Blend = 0 });
            };
            voiceControls.Children.Add(wave);
            Add("Атака", 1, 1500, patch.Attack, Locale.T("мс"), (p, x) => p with { Attack = x });
            Add("Спад", 5, 2000, patch.Decay, Locale.T("мс"), (p, x) => p with { Decay = x });
            Add("Поддержка", 0, 100, patch.Sustain, "%", (p, x) => p with { Sustain = x });
            Add("Отпускание", 5, 3000, patch.Release, Locale.T("мс"), (p, x) => p with { Release = x });
            Add("Яркость", 0, 100, patch.Tone, "%", (p, x) => p with { Tone = x });
        }

        void Add(string name, double min, double max, double value, string unit, Func<SynthPatch, float, SynthPatch> set) =>
            voiceControls.Children.Add(FxWindow.ParameterLine(Locale.T(name), min, max, value, x => $"{x:0} {unit}",
                x => ChangePatch(set(track.Patch!, (float)x)), history.WatchDrag));

        void ChangeCurve(GlissandoCurve curve)
        {
            plot.Update(curve);
            SaveCurve(curve);
        }

        void SaveCurve(GlissandoCurve curve)
        {
            track.ChangeCurve(curve);
            history.Commit();
            edited();
        }

        void ChangePatch(SynthPatch patch)
        {
            track.ChangePatch(patch);
            history.Commit();
            edited();
        }

        void Step(bool forward)
        {
            if (!history.Step(forward)) return;
            plot.Update(track.Curve!);
            BuildControls();
            edited();
        }

        void UpdateHistory()
        {
            undo.IsEnabled = history.CanUndo;
            redo.IsEnabled = history.CanRedo;
        }
    }

    private static TextBlock Caption(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Soft");
        return label;
    }

    private static Button HistoryButton(int direction, string hint, Action action)
    {
        var button = Button(direction, hint, action);
        button.Style = (Style)Application.Current.FindResource("HistoryButton");
        return button;
    }

    private static Button Button(object content, string hint, Action action)
    {
        var button = new Button { Content = content, ToolTip = hint, Margin = new Thickness(0, 0, 6, 0),
            Style = (Style)Application.Current.FindResource("Compact") };
        AutomationProperties.SetName(button, content as string ?? hint);
        button.Click += (_, _) => action();
        return button;
    }

    private static ToggleButton Toggle(string text, string hint, bool active) => new()
    {
        Content = text, ToolTip = hint, IsChecked = active, Padding = new Thickness(8, 0, 8, 0),
        Margin = new Thickness(0, 0, 6, 0), Style = (Style)Application.Current.FindResource("PillToggle")
    };
}
