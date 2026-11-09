using System.Collections.Immutable;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace EazyLoop;

internal sealed class GlissandoRoll : ThemedCanvas
{
    private const double Keys = 62, Ruler = 25, Row = 19;
    private static readonly Typeface Font = new("Consolas");
    private readonly MidiTrack track;
    private LoopGrid grid = new(100, 4, 2);
    private int topPitch = 78, playTick = -1;
    private double firstTick, zoom = 1;
    private (int Phrase, int Point)? selected;
    private int? pencilTick;
    private Vector dragOffset;

    internal bool Snap { get; set; } = true;
    internal bool Pencil { get; set; }
    internal int Step { get; set; } = 240;
    internal int RowsVisible => Math.Max(1, (int)Math.Ceiling((ActualHeight - Ruler) / Row));
    private int Ticks => MidiTrackSettings.Ticks(grid);
    private double PixelsPerTick => Math.Max(1, ActualWidth - Keys) * zoom / Ticks;
    private double LastTick => Math.Min(Ticks, firstTick + Ticks / zoom);
    private GlissandoTimeline Timeline => track.Timeline!;
    internal event Action? Edited;
    internal event Action<int>? ScrollPitch;
    internal event Action<GlissandoPoint?>? SelectionChanged;

    internal GlissandoRoll(MidiTrack track)
    {
        this.track = track;
        ClipToBounds = Focusable = true;
        Cursor = Cursors.Cross;
    }

    internal void SetViewport(LoopGrid value, double scale, double left, int top)
    {
        grid = value;
        zoom = scale;
        firstTick = left;
        topPitch = top;
        InvalidateVisual();
    }

    internal void Playhead(int tick)
    {
        if (tick == playTick) return;
        playTick = tick;
        InvalidateVisual();
    }

    internal void ClearSelection() => Select(null);

    protected override void OnRender(DrawingContext drawing)
    {
        if (ActualWidth <= Keys || ActualHeight <= Ruler) return;
        double width = ActualWidth, height = ActualHeight;
        drawing.DrawRectangle(ThemeManager.Brush("Background"), null, new Rect(RenderSize));
        drawing.DrawText(Label(Locale.T("НОТЫ"), "Muted"), new Point(10, 5));
        for (int pitch = topPitch; pitch >= Math.Max(0, topPitch - RowsVisible + 1); pitch--)
        {
            double y = Y(pitch * 100) - Row / 2;
            bool black = pitch % 12 is 1 or 3 or 6 or 8 or 10;
            drawing.DrawRectangle(ThemeManager.Brush(black ? "Surface" : "Panel"), null, new Rect(Keys, y, width - Keys, Row));
            drawing.DrawRectangle(ThemeManager.Brush(black ? "Background" : "FieldHover"), null,
                new Rect(0, y, Keys - 2, Row - 1));
            if (selected is { } s && Math.Round(Timeline.Phrases[s.Phrase][s.Point].Cents / 100.0) == pitch)
                drawing.DrawRectangle(ThemeManager.Brush("AccentPanel"), null, new Rect(0, y, Keys - 2, Row - 1));
            drawing.DrawText(Label(PianoRollSurface.PitchName(pitch), pitch % 12 == 0 ? "Ink" : "Soft"), new Point(11, y + 2));
            if (!black)
                drawing.DrawLine(ThemeManager.Stroke(pitch % 12 == 0 ? "StrongLine" : "Line"),
                    new Point(Keys, y), new Point(width, y));
        }
        drawing.PushClip(new RectangleGeometry(new Rect(Keys, 0, width - Keys, height)));
        int barTicks = grid.Beats * MidiTrackSettings.Ppq;
        int stride = Step * PixelsPerTick >= 5 ? Step : MidiTrackSettings.Ppq;
        for (int tick = (int)(firstTick / stride) * stride; tick <= LastTick; tick += stride)
        {
            double x = X(tick);
            bool bar = tick % barTicks == 0;
            string color = bar ? "AccentLine" : tick % MidiTrackSettings.Ppq == 0 ? "StrongLine" : "Line";
            drawing.DrawLine(ThemeManager.Stroke(color), new Point(x, bar ? 0 : Ruler), new Point(x, height));
            if (bar) drawing.DrawText(Label((tick / barTicks + 1).ToString("00"), "Ink"), new Point(x + 5, 5));
        }
        if (playTick >= 0)
        {
            double x = X(playTick);
            drawing.DrawLine(ThemeManager.Stroke("Mint"), new Point(x, 0), new Point(x, height));
        }
        drawing.PushClip(new RectangleGeometry(new Rect(Keys, Ruler, width - Keys, height - Ruler)));
        if (Timeline.Count == 0)
        {
            var hint = Label(Locale.T("Поставьте две точки, чтобы начать фразу."), "Soft");
            drawing.DrawText(hint, new Point(Keys + Math.Max(10, (width - Keys - hint.Width) / 2),
                Ruler + (height - Ruler - hint.Height) / 2));
        }
        for (int i = 0; i < Timeline.Contours.Length; i++)
        {
            var contour = Timeline.Contours[i];
            if (contour.End < firstTick || contour.Start > LastTick) continue;
            bool active = selected?.Phrase == i;
            if (contour.Start < contour.End)
            {
                double x = X(Math.Max(firstTick, contour.Start));
                double end = X(Math.Min(LastTick, contour.End));
                drawing.DrawRectangle(ThemeManager.Brush(active ? "AccentHover" : "HoverOverlay"), null,
                    new Rect(x, Ruler, end - x, height - Ruler));
                DrawContour(drawing, contour, firstTick, LastTick, X, Y, ThemeManager.Stroke("Violet", active ? 2.5 : 1.7));
            }
            for (int j = 0; j < contour.Points.Length; j++)
            {
                var point = contour.Points[j];
                if (point.Position < firstTick || point.Position > LastTick) continue;
                bool chosen = selected == (i, j);
                double radius = chosen ? 5.5 : 3.8;
                drawing.DrawEllipse(ThemeManager.Brush(chosen ? "Mint" : "Violet"), ThemeManager.Stroke("Field"),
                    Pixel(point), radius, radius);
            }
        }
        drawing.Pop();
        drawing.Pop();
    }

    internal static void DrawContour(DrawingContext drawing, PitchContour contour, double from, double to,
        Func<double, double> x, Func<double, double> y, Pen pen)
    {
        var trace = new StreamGeometry();
        using (var path = trace.Open())
        {
            double start = Math.Max(from, contour.Start);
            int segment = 0;
            path.BeginFigure(new Point(x(start), y(contour.CentsAt(start, ref segment))), false, false);
            for (int i = 1; i < contour.Points.Length; i++)
            {
                double end = Math.Min(to, contour.Points[i].Position);
                if (end <= start) continue;
                int steps = Math.Max(1, (int)Math.Ceiling((x(end) - x(start)) / 3));
                for (int j = 1; j <= steps; j++)
                {
                    double tick = start + (end - start) * j / steps;
                    path.LineTo(new Point(x(tick), y(contour.CentsAt(tick, ref segment))), true, true);
                }
                start = end;
                if (start >= to) break;
            }
        }
        drawing.DrawGeometry(null, pen, trace);
    }

    private FormattedText Label(string text, string brush) => new(text, Locale.Culture, FlowDirection.LeftToRight,
        Font, 11, ThemeManager.Brush(brush), VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private double X(double tick) => Keys + (tick - firstTick) * PixelsPerTick;
    private double Y(double cents) => Ruler + (topPitch + .5 - cents / 100) * Row;
    private Point Pixel(GlissandoPoint point) => new(X(point.Position), Y(point.Cents));
    private int PitchAt(double y, bool snap) => (int)Math.Round((topPitch + .5 - (y - Ruler) / Row) * (snap ? 1 : 100)) *
        (snap ? 100 : 1);

    private GlissandoPoint Read(Point point)
    {
        double tick = firstTick + (point.X - Keys) / PixelsPerTick;
        int step = Snap ? Step : 1;
        return new(Math.Clamp((int)Math.Round(tick / step) * step, 0, Ticks),
            Math.Clamp(PitchAt(point.Y, Snap), 0, GlissandoTimeline.MaxPitch));
    }

    private (int Phrase, int Point)? Hit(Point mouse)
    {
        (int, int)? result = null;
        double nearest = 81;
        for (int i = 0; i < Timeline.Contours.Length; i++)
        {
            var contour = Timeline.Contours[i];
            for (int j = 0; j < contour.Points.Length; j++)
            {
                var point = contour.Points[j];
                if (point.Position > LastTick) break;
                if (point.Position < firstTick) continue;
                double distance = (Pixel(point) - mouse).LengthSquared;
                if (distance > nearest) continue;
                nearest = distance;
                result = (i, j);
            }
        }
        return result;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Point mouse = e.GetPosition(this);
        if (mouse.Y < Ruler) return;
        if (mouse.X < Keys)
        {
            track.PreviewNote(Math.Clamp(PitchAt(mouse.Y, true) / 100, 0, 127), 96);
            e.Handled = true;
            return;
        }
        Focus();
        if (!CaptureMouse()) return;
        var point = Read(mouse);
        dragOffset = default;
        if (Pencil)
        {
            pencilTick = point.Position;
            Insert(point, PhraseAt(point.Position), pencilTick);
        }
        else if (Hit(mouse) is { } hit)
        {
            dragOffset = mouse - Pixel(Timeline.Phrases[hit.Phrase][hit.Point]);
            Select(hit);
        }
        else Insert(point, PhraseAt(point.Position), null);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured || selected is not { } s) return;
        var point = Read(e.GetPosition(this) - dragOffset);
        if (pencilTick is int previous)
        {
            int minimum = Snap ? Step : Math.Max(1, (int)(9 / PixelsPerTick));
            if (point.Position != previous && Math.Abs(point.Position - previous) < minimum) return;
            Insert(point, s.Phrase, previous);
            pencilTick = Timeline.Phrases[selected!.Value.Phrase][selected.Value.Point].Position;
        }
        else MovePoint(point);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        if (pencilTick is int previous && selected is { } s) Insert(Read(e.GetPosition(this)), s.Phrase, previous);
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        pencilTick = null;
        base.OnLostMouseCapture(e);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured || e.GetPosition(this) is not { X: >= Keys, Y: >= Ruler } mouse || Hit(mouse) is not { } hit) return;
        Select(hit);
        DeletePoint();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (IsMouseCaptured) return;
        ScrollPitch?.Invoke(-Math.Sign(e.Delta) * 3);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsMouseCaptured || selected is not { } s || Keyboard.Modifiers != ModifierKeys.None) return;
        var point = Timeline.Phrases[s.Phrase][s.Point];
        switch (e.Key)
        {
            case Key.Delete: DeletePoint(); break;
            case Key.Left: MovePoint(point with { Position = point.Position - (Snap ? Step : 1) }); break;
            case Key.Right: MovePoint(point with { Position = point.Position + (Snap ? Step : 1) }); break;
            case Key.Up: MovePoint(point with { Cents = point.Cents + (Snap ? 100 : 1) }); break;
            case Key.Down: MovePoint(point with { Cents = point.Cents - (Snap ? 100 : 1) }); break;
            default: return;
        }
        e.Handled = true;
    }

    private int PhraseAt(int tick)
    {
        for (int i = 0; i < Timeline.Contours.Length; i++)
        {
            var contour = Timeline.Contours[i];
            if (tick >= contour.Start && tick <= contour.End) return i;
        }
        if (selected is { } s)
        {
            var (min, max) = PhraseBounds(s.Phrase);
            if (tick >= min && tick <= max) return s.Phrase;
        }
        return -1;
    }

    private (int Min, int Max) PhraseBounds(int i) =>
        (i == 0 ? 0 : Timeline.Contours[i - 1].End + 1,
            i == Timeline.Contours.Length - 1 ? Ticks : Math.Min(Ticks, Timeline.Contours[i + 1].Start - 1));

    private void Insert(GlissandoPoint point, int phraseIndex, int? from)
    {
        var phrases = Timeline.Phrases;
        if (phraseIndex < 0)
        {
            if (Timeline.Count >= GlissandoTimeline.MaxPoints) { Select(null); return; }
            int index = 0;
            while (index < phrases.Length && phrases[index][0].Position < point.Position) index++;
            Change(phrases.Insert(index, [point]), (index, 0));
            return;
        }

        var (min, max) = PhraseBounds(phraseIndex);
        if (Snap && (point.Position < min || point.Position > max))
        {
            min = (min + Step - 1) / Step * Step;
            max = max / Step * Step;
        }
        if (min > max) return;
        point = point with { Position = Math.Clamp(point.Position, min, max) };
        var original = phrases[phraseIndex];
        var points = original;
        if (from is int previous && previous != point.Position)
        {
            int left = Math.Min(previous, point.Position), right = Math.Max(previous, point.Position);
            points = points.RemoveAll(p => p.Position > left && p.Position < right);
        }
        int i = 0;
        while (i < points.Length && points[i].Position < point.Position) i++;
        if (i < points.Length && points[i].Position == point.Position)
        {
            if (points[i] == point && points == original) { Select((phraseIndex, i)); return; }
            points = points.SetItem(i, point);
        }
        else if (Timeline.Count - original.Length + points.Length < GlissandoTimeline.MaxPoints) points = points.Insert(i, point);
        else return;
        Change(phrases.SetItem(phraseIndex, points), (phraseIndex, i));
    }

    private void MovePoint(GlissandoPoint point)
    {
        if (selected is not { } s) return;
        var phrases = Timeline.Phrases;
        var points = phrases[s.Phrase];
        var (min, max) = PhraseBounds(s.Phrase);
        if (s.Point > 0) min = points[s.Point - 1].Position + 1;
        if (s.Point < points.Length - 1) max = Math.Min(max, points[s.Point + 1].Position - 1);
        if (min > max) return;
        int tick = Math.Clamp(point.Position, min, max);
        if (Snap && tick != point.Position)
        {
            min = (min + Step - 1) / Step * Step;
            max = max / Step * Step;
            tick = min <= max ? Math.Clamp(point.Position, min, max) : points[s.Point].Position;
        }
        var next = new GlissandoPoint(tick, Math.Clamp(point.Cents, 0, GlissandoTimeline.MaxPitch));
        if (next != points[s.Point]) Change(phrases.SetItem(s.Phrase, points.SetItem(s.Point, next)), s);
    }

    private void DeletePoint()
    {
        if (selected is not { } s) return;
        var phrases = Timeline.Phrases;
        var points = phrases[s.Phrase].RemoveAt(s.Point);
        if (points.IsEmpty) Change(phrases.RemoveAt(s.Phrase), null);
        else Change(phrases.SetItem(s.Phrase, points), (s.Phrase, Math.Min(s.Point, points.Length - 1)));
    }

    private void Select((int Phrase, int Point)? value)
    {
        selected = value;
        SelectionChanged?.Invoke(value is { } s ? Timeline.Phrases[s.Phrase][s.Point] : null);
        InvalidateVisual();
    }

    private void Change(ImmutableArray<ImmutableArray<GlissandoPoint>> phrases, (int Phrase, int Point)? selection)
    {
        track.ChangeTimeline(new GlissandoTimeline(phrases, Timeline.Smoothing));
        Select(selection);
        Edited?.Invoke();
    }
}
