using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace EazyLoop;

public sealed partial class PianoRollSurface : ThemedCanvas
{
    private const double Keys = 68, Ruler = 26, Row = 19, LaneHeight = 104, VelocityBarWidth = 4;
    private static readonly Brush KeyWhite = Ink("#D7D8E4"), KeyBlack = Ink("#38394A"), KeyEdge = Ink("#A8ABBF");
    private static readonly Brush KeyLabel = Ink("#241D36");
    private static Brush Dark => ThemeManager.Brush("Background");
    private static Brush LightRow => ThemeManager.Brush("Panel");
    private static Brush DarkRow => ThemeManager.Brush("Surface");
    private static Brush InkText => ThemeManager.Brush("Soft");
    private static Brush Note => ThemeManager.Brush("Violet");
    private static Brush SelectedNote => ThemeManager.Brush("Mint");
    private static Brush LaneFill => ThemeManager.Brush("Field");
    private static Brush MarqueeFill => ThemeManager.Brush("MintShade");
    private static Brush HoverRow => ThemeManager.Brush("AccentHover");
    private static Brush HoverKey => ThemeManager.Brush("AccentKey");
    private static Brush HoverLabel => ThemeManager.Brush("AccentPanel");
    private static Brush HoverInk => ThemeManager.Brush("Ink");
    private static Pen HoverOutline => ThemeManager.Stroke("Ink");
    private static Pen Thin => ThemeManager.Stroke("Line");
    private static Pen Bar => ThemeManager.Stroke("AccentLine");
    private static Pen Octave => ThemeManager.Stroke("StrongLine");
    private static Pen Head => ThemeManager.Stroke("Mint");
    private static Pen PasteHead => ThemeManager.Stroke("Violet");
    private static readonly Typeface Mono = new("Consolas");
    private static MidiNote[] copiedNotes = [];
    private enum Drag { None, Move, Resize, Marquee, Velocity, StaffVelocity, Control }
    private Drag drag;
    private Point anchor;
    private Point? velocityStroke;
    private bool dragMoved, extendSelection;
    private Rect marquee = Rect.Empty;
    private MidiNote? noteToPreview;
    private int? mouseKey;
    private MidiNote[] originals = [];
    private int controlTick;
    private int gestureTick;
    private int? pasteTick;
    private int topPitch = 78;
    private int playTick = -1;
    private int? laneValue;
    private DrawingGroup? content;
    private Point? pointer;
    private int? hoverPitch;
    private MidiNote? hoverNote;
    private readonly HashSet<Guid> selection = [];
    private MidiTrack? track;
    private LoopGrid grid = new(100, 4, 2);
    private double zoom = 1;
    private double leftTick;
    private MidiLane lane = MidiLane.Velocity;
    internal int Step { get; set; } = 240;
    internal bool Snap { get; set; } = true;
    internal int[] Intervals { get; set; } = [0];
    internal DiatonicScale? Scale { get; set; }
    internal int ScaleVoices { get; set; }
    internal event Action? Changed;
    internal event Action? GestureStarted;
    internal event Action? GestureEnded;
    internal event Action? SelectionChanged;
    internal event Action<int>? PitchScroll;

    public PianoRollSurface() { Focusable = true; ClipToBounds = true; }

    internal void Setup(MidiTrack value) { track = value; InvalidateRoll(); }

    internal void SequenceRestored(MidiNote[] previous)
    {
        var ids = track!.Notes.Select(note => note.Id).ToHashSet();
        selection.IntersectWith(ids);
        if (selection.Count == 0)
        {
            ids.ExceptWith(previous.Select(note => note.Id));
            selection.UnionWith(ids);
        }
        laneValue = null;
        NotifySelection();
    }

    private double PitchSpacing => staffMode ? StaffStep : Row;
    internal int RowsVisible => Math.Max(1, (int)Math.Floor((ActualHeight - Ruler - LaneHeight) / PitchSpacing));
    internal double MaxOffset => Math.Max(0, MidiTrackSettings.Ticks(grid) - VisibleTicks);
    private double VisibleTicks => MidiTrackSettings.Ticks(grid) / zoom;
    private double LaneTop => Math.Max(Ruler + Row, ActualHeight - LaneHeight);
    private double PixelsPerTick => Math.Max(1, ActualWidth - Keys) / VisibleTicks;

    internal void SetViewport(LoopGrid value, double scale, double offset, int pitch, MidiLane channel)
    {
        grid = value; zoom = Math.Max(1, scale); leftTick = Math.Clamp(offset, 0, MaxOffset);
        if (pasteTick is int tick && tick >= MidiTrackSettings.Ticks(grid)) pasteTick = null;
        topPitch = Math.Clamp(pitch, Math.Min(PitchUnits - 1, RowsVisible - 1), PitchUnits - 1);
        if (lane != channel)
        {
            EndVelocityStroke();
            laneValue = null;
        }
        lane = channel;
        InvalidateRoll();
    }

    internal void Playhead(int tick)
    {
        if (playTick == tick) return;
        playTick = tick;
        InvalidateVisual();
    }

    internal MidiNote? Selected => track?.Notes.FirstOrDefault(note => selection.Contains(note.Id));

    internal void UpdateSelected(int start, int length, int velocity, int? pitch = null)
    {
        if (track == null || Selected is not { } selected) return;
        int end = MidiTrackSettings.Ticks(grid);
        start = Math.Clamp(start, 0, end - 1);
        length = Math.Clamp(length, 1, end - start);
        var changed = selected with { Start = start, Length = length, Velocity = Math.Clamp(velocity, 1, 127),
            Pitch = pitch ?? selected.Pitch };
        if (changed != selected) ApplyNotes(track.Notes.Select(n => n.Id == changed.Id ? changed : n).ToArray());
    }

    internal void RemoveSelected()
    {
        if (track == null || selection.Count == 0) return;
        var notes = track.Notes.Where(n => !selection.Contains(n.Id)).ToArray();
        selection.Clear();
        ApplyNotes(notes);
    }

    internal void CopyNotes()
    {
        if (track == null || selection.Count == 0) return;
        var notes = track.Notes.Where(note => selection.Contains(note.Id)).ToArray();
        if (notes.Length == 0) return;
        int first = notes.Min(note => note.Start);
        copiedNotes = notes.Select(note => note with { Start = note.Start - first }).ToArray();
    }

    internal string? PasteNotes()
    {
        if (track == null || copiedNotes.Length == 0) return Locale.T("Сначала выделите и скопируйте ноты.");
        if (pasteTick is not int start) return Locale.T("Выберите позицию вставки щелчком по линейке тактов.");
        if (start + copiedNotes.Max(note => note.Start + note.Length) > MidiTrackSettings.Ticks(grid))
            return Locale.T("Фрагмент не помещается до конца лупа.");
        if (track.Notes.Length + copiedNotes.Length > MidiTrackSettings.MaxNotes)
            return Locale.T("В дорожке достигнут предел количества нот.");

        var pasted = copiedNotes.Select(note => note with { Id = Guid.NewGuid(), Start = start + note.Start }).ToArray();
        selection.Clear();
        foreach (var note in pasted) selection.Add(note.Id);
        ApplyNotes([.. track.Notes, .. pasted]);
        return null;
    }

    private double X(double tick) => Keys + (tick - leftTick) * PixelsPerTick;
    private double Y(int pitch) => Ruler + (topPitch - pitch) * Row;
    private double Tick(double x) => leftTick + (x - Keys) / PixelsPerTick;
    private int Pitch(double y) => staffMode ? StaffPitch(y) : Math.Clamp(topPitch - (int)Math.Floor((y - Ruler) / Row), 0, 127);
    private int Quantize(double tick) => Snap ? (int)Math.Round(tick / Step) * Step : (int)Math.Round(tick);
    private int InsertTime(double tick) => Snap ? (int)Math.Floor(tick / Step) * Step : (int)Math.Round(tick);

    protected override void OnRender(DrawingContext drawing)
    {
        if (content == null)
        {
            var rendered = new DrawingGroup();
            using (var plot = rendered.Open()) DrawRoll(plot);
            rendered.Freeze();
            content = rendered;
        }
        drawing.DrawDrawing(content);
        DrawHover(drawing);
        if (pasteTick is int tick)
        {
            double at = X(tick);
            if (at >= Keys && at <= ActualWidth)
            {
                drawing.DrawLine(PasteHead, new Point(at, 0), new Point(at, LaneTop));
                drawing.DrawRoundedRectangle(Note, null, new Rect(at, 0, 5, 8), 1, 1);
            }
        }
        if (!marquee.IsEmpty) drawing.DrawRectangle(MarqueeFill, Head, marquee);
        double x = X(playTick);
        if (playTick >= 0 && x >= Keys && x <= ActualWidth)
            drawing.DrawLine(Head, new Point(x, 0), new Point(x, ActualHeight));
    }

    private void UpdateHover(Point? position)
    {
        pointer = position;
        MidiNote? note = null;
        int? pitch = null;
        if (track != null && position is { } point && point.X >= 0 && point.X < ActualWidth &&
            point.Y >= Ruler && point.Y < LaneTop)
        {
            if (point.X >= Keys) note = Hit(point).Note;
            pitch = note?.Pitch ?? Pitch(point.Y);
        }
        if (pitch == hoverPitch && note == hoverNote) return;
        hoverPitch = pitch;
        hoverNote = note;
        InvalidateVisual();
    }

    private void DrawHover(DrawingContext drawing)
    {
        if (hoverPitch is not int pitch || ActualWidth <= Keys) return;
        double y = staffMode ? StaffY(StaffIndex(pitch)) - StaffStep / 2 : Y(pitch);
        double top = Math.Max(y, Ruler), bottom = Math.Min(y + PitchSpacing, LaneTop);
        if (bottom <= top) return;

        drawing.DrawRectangle(HoverRow, null, new Rect(Keys, top, ActualWidth - Keys, bottom - top));
        drawing.DrawRectangle(HoverKey, null, new Rect(0, top, Keys - 2, bottom - top));
        if (bottom - top >= 13)
        {
            var label = Text(PitchName(pitch), HoverInk, 11);
            var box = new Rect(Keys - 44, top + 1, 34, bottom - top - 2);
            drawing.DrawRoundedRectangle(HoverLabel, null, box, 3, 3);
            drawing.DrawText(label, new Point(box.X + (box.Width - label.Width) / 2, top + (bottom - top - label.Height) / 2));
        }

        if (hoverNote is not { } note) return;
        drawing.PushClip(new RectangleGeometry(new Rect(Keys, Ruler, ActualWidth - Keys, LaneTop - Ruler)));
        if (staffMode) DrawStaffHover(drawing, note);
        else drawing.DrawRoundedRectangle(null, HoverOutline, NoteRect(note), 3, 3);
        drawing.Pop();
    }

    private void DrawRoll(DrawingContext drawing)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= Keys || height <= Ruler + LaneHeight || track == null) return;
        var notes = track.Notes;
        drawing.DrawRectangle(Dark, null, new Rect(0, 0, width, height));
        drawing.DrawRectangle(LaneFill, null, new Rect(0, LaneTop, width, height - LaneTop));
        drawing.PushClip(new RectangleGeometry(new Rect(0, Ruler, width, LaneTop - Ruler)));
        if (staffMode) DrawStaff(drawing, width);
        else for (int pitch = topPitch; pitch >= Math.Max(0, topPitch - RowsVisible); pitch--)
        {
            double y = Y(pitch);
            bool black = pitch % 12 is 1 or 3 or 6 or 8 or 10;
            drawing.DrawRectangle(black ? DarkRow : LightRow, null, new Rect(Keys, y, width - Keys, Row));
            drawing.DrawRectangle(KeyWhite, null, new Rect(0, y, Keys - 2, Row - 1));
            if (black) drawing.DrawRoundedRectangle(KeyBlack, null, new Rect(0, y, Keys * .69, Row - 1), 2, 2);
            if (pitch % 12 == 0)
            {
                drawing.DrawText(Text(PitchName(pitch), KeyLabel, 11), new Point(Keys - 31, y + 2));
                drawing.DrawLine(Octave, new Point(Keys, y), new Point(width, y));
            }
            else if (!black) drawing.DrawLine(Thin, new Point(Keys, y), new Point(width, y));
            DrawScaleMarker(drawing, pitch, y + Row / 2);
        }
        drawing.Pop();
        drawing.DrawRectangle(Dark, null, new Rect(0, 0, Keys, Ruler));
        drawing.DrawText(Text(Locale.T("НОТЫ"), InkText, 10), new Point(11, 6));
        drawing.DrawLine(new Pen(KeyEdge, 1), new Point(Keys - 1, Ruler), new Point(Keys - 1, LaneTop));
        drawing.DrawLine(Octave, new Point(0, LaneTop), new Point(width, LaneTop));

        drawing.PushClip(new RectangleGeometry(new Rect(Keys, 0, width - Keys, height)));
        int barTicks = grid.Beats * MidiTrackSettings.Ppq;
        int stride = Step * PixelsPerTick >= 5 ? Step : MidiTrackSettings.Ppq;
        int firstTick = (int)(leftTick / stride) * stride;
        int lastTick = Math.Min(MidiTrackSettings.Ticks(grid), (int)Math.Ceiling(leftTick + VisibleTicks));
        for (int tick = firstTick; tick <= lastTick; tick += stride)
        {
            double x = X(tick);
            bool bar = tick % barTicks == 0;
            var pen = bar ? Bar : tick % MidiTrackSettings.Ppq == 0 ? Octave : Thin;
            drawing.DrawLine(pen, new Point(x, bar ? 0 : Ruler), new Point(x, height));
            if (bar) drawing.DrawText(Text((tick / barTicks + 1).ToString("00"), InkText, 11), new Point(x + 5, 6));
        }

        drawing.PushClip(new RectangleGeometry(new Rect(Keys, Ruler, width - Keys, LaneTop - Ruler)));
        foreach (var note in notes)
        {
            int pitch = staffMode ? StaffIndex(note.Pitch) : note.Pitch;
            if (note.Start >= lastTick || note.Start + note.Length <= leftTick ||
                pitch > topPitch || pitch < topPitch - RowsVisible) continue;
            bool selected = selection.Contains(note.Id);
            Brush fill = selected ? SelectedNote : Note;
            if (staffMode) { DrawStaffNote(drawing, note, fill); continue; }
            Rect box = NoteRect(note);
            drawing.DrawRoundedRectangle(fill, null, box, 3, 3);
            if (box.Width > 34)
                drawing.DrawText(Text(PitchName(note.Pitch), ThemeManager.Brush(selected ? "OnMint" : "OnAccent"), 10),
                    new Point(box.X + 4, box.Y + 1));
        }
        drawing.Pop();
        DrawLane(drawing, notes, lastTick);
        drawing.Pop();
        bool pitchLane = lane == MidiLane.Pitch;
        bool semitones = pitchLane && track.Patch != null;
        drawing.DrawText(Text(semitones ? "+2 st" : pitchLane ? "+8191" : "127", InkText, 10), new Point(10, LaneTop + 7));
        drawing.DrawText(Text(semitones ? "-2 st" : pitchLane ? "-8192" : "0", InkText, 10), new Point(10, height - 20));
        if (laneValue is int value)
        {
            string label = semitones ? $"{(value - 8192) * 2.0 / 8192:+0.00;-0.00;0} st" :
                pitchLane ? (value - 8192).ToString("+0;-0;0") : value.ToString();
            drawing.DrawText(Text(label, SelectedNote, 11), new Point(10, LaneTop + 43));
        }
    }

    private void DrawLane(DrawingContext drawing, MidiNote[] notes, int lastTick)
    {
        if (lane == MidiLane.Velocity)
        {
            foreach (var note in notes)
            {
                if (note.Start < leftTick || note.Start >= lastTick) continue;
                double y = ValueY(note.Velocity);
                drawing.DrawRoundedRectangle(selection.Contains(note.Id) ? SelectedNote : Note, null,
                    new Rect(X(note.Start), y, VelocityBarWidth, Math.Max(1, ActualHeight - y - 8)), 1, 1);
            }
            return;
        }

        var points = track!.Points.Where(p => p.Lane == lane).OrderBy(p => p.Tick);
        int previous = MidiTrackSettings.Default(lane);
        double x = Keys, right = Math.Min(ActualWidth, X(lastTick));
        foreach (var point in points)
        {
            if (point.Tick < leftTick) { previous = point.Value; continue; }
            if (point.Tick > lastTick) break;
            double at = X(point.Tick), y = ValueY(point.Value), from = ValueY(previous);
            drawing.DrawLine(Head, new Point(x, from), new Point(at, from));
            drawing.DrawLine(Head, new Point(at, from), new Point(at, y));
            drawing.DrawEllipse(SelectedNote, null, new Point(at, y), 4, 4);
            x = at;
            previous = point.Value;
        }
        drawing.DrawLine(Head, new Point(x, ValueY(previous)), new Point(right, ValueY(previous)));
    }

    private Rect NoteRect(MidiNote note) => new(X(note.Start) + 1, Y(note.Pitch) + 2,
        Math.Max(2, note.Length * PixelsPerTick - 2), Row - 4);

    private double ValueY(int value) => ActualHeight - 8 - value * (LaneHeight - 17) / MidiTrackSettings.Maximum(lane);

    private int ValueAt(double y)
    {
        double ratio = Math.Clamp((ActualHeight - 8 - y) / (LaneHeight - 17), 0, 1);
        return lane == MidiLane.Sustain ? ratio < .5 ? 0 : 127 :
            (int)Math.Round(ratio * MidiTrackSettings.Maximum(lane));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (velocityStroke != null)
        {
            e.Handled = true;
            return;
        }
        Focus();
        Point point = e.GetPosition(this);
        if (point.X < Keys)
        {
            if (track != null && point.Y >= Ruler && point.Y < LaneTop && CaptureMouse())
                PlayMouseKey(point);
            e.Handled = true;
            return;
        }
        GestureStarted?.Invoke();
        BeginGesture(point, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        e.Handled = true;
        if (drag == Drag.None || !CaptureMouse()) GestureEnded?.Invoke();
    }

    private void BeginGesture(Point p, bool addSelection)
    {
        if (track == null) return;
        drag = Drag.None;
        dragMoved = false;
        noteToPreview = null;
        if (p.Y < Ruler)
        {
            pasteTick = Math.Clamp(InsertTime(Tick(p.X)), 0, MidiTrackSettings.Ticks(grid) - 1);
            InvalidateVisual();
            return;
        }
        anchor = p;
        if (p.Y >= LaneTop)
        {
            if (lane == MidiLane.Velocity)
            {
                var target = track.Notes.OrderBy(n => Math.Abs(X(n.Start) - p.X)).FirstOrDefault();
                if (target == null || Math.Abs(X(target.Start) - p.X) > 24) return;
                originals = track.Notes.Where(n => n.Start == target.Start &&
                    (selection.Contains(target.Id) ? selection.Contains(n.Id) : true)).ToArray();
                if (originals.Length == 0) return;
                drag = Drag.Velocity;
                EditDrag(p);
            }
            else
            {
                controlTick = Math.Clamp(Quantize(Tick(p.X)), 0, MidiTrackSettings.Ticks(grid) - 1);
                drag = Drag.Control;
                EditDrag(p);
            }
        }
        else
        {
            var (existing, gesture) = Hit(p);
            if (existing != null)
            {
                if (addSelection)
                {
                    if (!selection.Add(existing.Id)) selection.Remove(existing.Id);
                    NotifySelection();
                    return;
                }
                if (selection.Contains(existing.Id)) noteToPreview = existing;
                else
                {
                    selection.Clear();
                    selection.Add(existing.Id);
                }
                originals = track.Notes.Where(n => selection.Contains(n.Id)).ToArray();
                drag = gesture;
                gestureTick = drag == Drag.Resize ? existing.Start + existing.Length : existing.Start;
                NotifySelection();
            }
            else
            {
                extendSelection = addSelection;
                drag = Drag.Marquee;
            }
        }
    }

    private void InsertNotes(Point point)
    {
        if (track == null) return;
        int end = MidiTrackSettings.Ticks(grid);
        int tick = Math.Clamp(InsertTime(Tick(point.X)), 0, end - 1);
        int root = Pitch(point.Y);
        var chord = Scale is { } scale ? scale.BuildChord(root, ScaleVoices)
            : Intervals.Where(i => root + i <= 127).Select(i => root + i).ToArray();
        if (chord.Length == 0 || track.Notes.Length + chord.Length > MidiTrackSettings.MaxNotes) return;
        int length = Math.Min(Step, end - tick);
        var inserted = chord.Select(key => new MidiNote(Guid.NewGuid(), key, tick, length, 96)).ToArray();
        selection.Clear();
        foreach (var note in inserted) selection.Add(note.Id);
        ApplyNotes([.. track.Notes, .. inserted]);
        track.Preview(chord);
    }

    private void DrawScaleMarker(DrawingContext drawing, int pitch, double y)
    {
        if (Scale is { } scale && scale.Contains(pitch))
            drawing.DrawEllipse(staffMode ? SelectedNote : Note, null, new Point(Keys - 8, y), 2.5, 2.5);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (track == null) return;
        Point p = e.GetPosition(this);
        if (p.X < Keys) return;
        if (IsVelocityLane(p))
        {
            if (!IsMouseCaptured)
            {
                Focus();
                GestureStarted?.Invoke();
                PaintVelocity(p);
            }
        }
        else if (p.Y >= LaneTop && lane != MidiLane.Velocity)
        {
            var point = track.Points.Where(x => x.Lane == lane).OrderBy(x => Math.Abs(X(x.Tick) - p.X)).FirstOrDefault();
            if (point != null && Math.Abs(X(point.Tick) - p.X) < 14)
                ApplyPoints(track.Points.Where(x => x != point).ToArray());
        }
        else if (p.Y >= Ruler && p.Y < LaneTop && Hit(p).Note is { } note)
        {
            selection.Remove(note.Id);
            ApplyNotes(track.Notes.Where(n => n.Id != note.Id).ToArray());
        }
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (velocityStroke == null) return;
        Point point = e.GetPosition(this);
        if (point != velocityStroke.Value) PaintVelocity(point);
        EndVelocityStroke();
        e.Handled = true;
    }

    private bool IsVelocityLane(Point point) =>
        lane == MidiLane.Velocity && point.X >= Keys && point.X < ActualWidth &&
        point.Y >= LaneTop && point.Y < ActualHeight;

    private void PaintVelocity(Point point)
    {
        Point from = velocityStroke ?? point;
        velocityStroke = point;
        if (track == null || lane != MidiLane.Velocity) return;

        double dx = point.X - from.X, dy = point.Y - from.Y;
        double start = 0, end = 1;
        if (dy == 0)
        {
            if (from.Y < LaneTop || from.Y >= ActualHeight) return;
        }
        else
        {
            double top = (LaneTop - from.Y) / dy, bottom = (ActualHeight - from.Y) / dy;
            start = Math.Max(0, Math.Min(top, bottom));
            end = Math.Min(1, Math.Max(top, bottom));
            if (start > end) return;
        }
        double firstX = from.X + dx * start, lastX = from.X + dx * end;
        double left = Math.Min(firstX, lastX), right = Math.Max(firstX, lastX);
        var notes = track.Notes;
        MidiNote[]? edited = null;
        for (int i = 0; i < notes.Length; i++)
        {
            var note = notes[i];
            if (note.Start < leftTick) continue;
            double x = X(note.Start);
            if (x >= ActualWidth || x > right || x + VelocityBarWidth < left) continue;
            // Follow the stroke between mouse events so a fast sweep still hits every bar.
            double progress = dx == 0 ? end : Math.Clamp((x + VelocityBarWidth / 2 - from.X) / dx, start, end);
            int velocity = Math.Clamp(ValueAt(from.Y + dy * progress), 1, 127);
            if (note.Velocity == velocity) continue;
            edited ??= (MidiNote[])notes.Clone();
            edited[i] = note with { Velocity = velocity };
        }
        if (edited == null) return;
        laneValue = Math.Clamp(ValueAt(from.Y + dy * end), 1, 127);
        ApplyNotes(edited);
    }

    private void EndVelocityStroke()
    {
        if (velocityStroke == null) return;
        velocityStroke = null;
        GestureEnded?.Invoke();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        UpdateHover(e.GetPosition(this));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (velocityStroke != null && e.RightButton == MouseButtonState.Pressed)
            PaintVelocity(e.GetPosition(this));
        EndVelocityStroke();
        UpdateHover(null);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        EndVelocityStroke();
        base.OnLostKeyboardFocus(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point point = e.GetPosition(this);
        if (velocityStroke != null)
        {
            if (e.RightButton == MouseButtonState.Pressed) PaintVelocity(point);
            else EndVelocityStroke();
        }
        else if (mouseKey != null && IsMouseCaptured) PlayMouseKey(point);
        else if (drag != Drag.None && IsMouseCaptured) EditDrag(point);
        UpdateHover(point);
    }

    private void PlayMouseKey(Point point)
    {
        if (point.X < 0 || point.X >= Keys || point.Y < Ruler || point.Y >= LaneTop) return;
        int key = Pitch(point.Y);
        if (mouseKey == key) return;
        ReleaseMouseKey();
        mouseKey = key;
        track!.PlayLiveNote(key, MidiTrackSettings.Default(MidiLane.Velocity));
    }

    private void ReleaseMouseKey()
    {
        if (mouseKey is not int key) return;
        track?.PlayLiveNote(key, 0);
        mouseKey = null;
    }

    protected override void OnQueryCursor(QueryCursorEventArgs e)
    {
        base.OnQueryCursor(e);
        if (!staffMode || track == null) return;
        var gesture = drag;
        if (!IsMouseCaptured)
        {
            var point = e.GetPosition(this);
            if (point.X < Keys || point.Y < Ruler || point.Y >= LaneTop) return;
            gesture = HitStaff(track.Notes, point).Gesture;
        }
        if (gesture is not (Drag.Resize or Drag.StaffVelocity)) return;
        e.Cursor = gesture == Drag.Resize ? Cursors.SizeWE : Cursors.SizeNS;
        e.Handled = true;
    }

    private void EditDrag(Point point)
    {
        if (track == null) return;
        if (!dragMoved && drag is Drag.Move or Drag.Resize or Drag.Marquee or Drag.StaffVelocity)
        {
            bool movedX = Math.Abs(point.X - anchor.X) >= SystemParameters.MinimumHorizontalDragDistance;
            bool movedY = Math.Abs(point.Y - anchor.Y) >= SystemParameters.MinimumVerticalDragDistance;
            bool pastThreshold = drag switch
            {
                Drag.Resize when staffMode => movedX,
                Drag.StaffVelocity => movedY,
                _ => movedX || movedY
            };
            if (!pastThreshold) return;
            dragMoved = true;
        }
        if (drag == Drag.Marquee)
        {
            marquee = new Rect(anchor, new Point(Math.Clamp(point.X, Keys, ActualWidth),
                Math.Clamp(point.Y, Ruler, LaneTop)));
            InvalidateVisual();
            return;
        }
        if (drag == Drag.Control)
        {
            int tick = controlTick;
            var next = new MidiPoint(lane, tick, ValueAt(point.Y));
            laneValue = next.Value;
            var existing = track.Points.FirstOrDefault(p => p.Lane == lane && p.Tick == tick);
            if (existing == next || (existing == null && track.Points.Length >= MidiTrackSettings.MaxPoints)) return;
            ApplyPoints([.. track.Points.Where(p => p != existing), next]);
            return;
        }
        int ticks = MidiTrackSettings.Ticks(grid);
        double timeDelta = (point.X - anchor.X) / PixelsPerTick;
        int dt = drag == Drag.Resize && staffMode ? Quantize(timeDelta) : Quantize(gestureTick + timeDelta) - gestureTick;
        int dy = (int)Math.Round((anchor.Y - point.Y) / PitchSpacing);
        int velocityDelta = (int)Math.Round(anchor.Y - point.Y);
        if (drag == Drag.Velocity) laneValue = Math.Clamp(ValueAt(point.Y), 1, 127);
        if (drag == Drag.Move)
        {
            dt = Math.Clamp(dt, -originals.Min(n => n.Start), Math.Max(-originals.Min(n => n.Start), ticks - originals.Max(n => n.Start + n.Length)));
            dy = staffMode ? Math.Clamp(dy, -originals.Min(n => StaffIndex(n.Pitch)), 74 - originals.Max(n => StaffIndex(n.Pitch)))
                : Math.Clamp(dy, -originals.Min(n => n.Pitch), 127 - originals.Max(n => n.Pitch));
        }
        var replacements = originals.ToDictionary(n => n.Id, n => drag switch
        {
            Drag.Move => n with { Start = n.Start + dt, Pitch = MovePitch(n.Pitch, dy) },
            Drag.Resize => n with { Length = Math.Clamp(n.Length + dt, 1, Math.Max(1, ticks - n.Start)) },
            Drag.StaffVelocity => n with { Velocity = Math.Clamp(n.Velocity + velocityDelta, 1, 127) },
            _ => n with { Velocity = Math.Clamp(ValueAt(point.Y), 1, 127) }
        });
        if (drag == Drag.StaffVelocity && lane == MidiLane.Velocity)
            laneValue = replacements[originals[0].Id].Velocity;
        var edited = track.Notes.Select(n => replacements.GetValueOrDefault(n.Id, n)).ToArray();
        if (!edited.SequenceEqual(track.Notes)) ApplyNotes(edited);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        if (mouseKey == null) EndGesture(e.GetPosition(this));
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void EndGesture(Point point)
    {
        if (track == null) return;
        if (drag is Drag.Move or Drag.Resize or Drag.Marquee or Drag.StaffVelocity) EditDrag(point);
        if (drag == Drag.Marquee)
        {
            if (!dragMoved)
            {
                InsertNotes(anchor);
                return;
            }

            if (!extendSelection) selection.Clear();
            foreach (var note in track.Notes)
            {
                if (staffMode && (note.Start < leftTick || note.Start >= leftTick + VisibleTicks)) continue;
                Rect bounds = staffMode ? new Rect(HeadAt(note) - new Vector(HeadSize, HeadSize),
                    new Size(2 * HeadSize, 2 * HeadSize)) : NoteRect(note);
                if (marquee.IntersectsWith(bounds)) selection.Add(note.Id);
            }
            NotifySelection();
        }
        else if (!dragMoved && noteToPreview is { } note && selection.Contains(note.Id))
            track.PreviewNote(note.Pitch, note.Velocity);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        bool keyGesture = mouseKey != null;
        ReleaseMouseKey();
        drag = Drag.None;
        originals = [];
        noteToPreview = null;
        if (!marquee.IsEmpty)
        {
            marquee = Rect.Empty;
            InvalidateVisual();
        }
        if (!keyGesture) GestureEnded?.Invoke();
        UpdateHover(IsMouseOver ? e.GetPosition(this) : null);
        base.OnLostMouseCapture(e);
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        PitchScroll?.Invoke(-e.Delta * 3 / 120);
        e.Handled = true;
    }

    private (MidiNote? Note, Drag Gesture) Hit(Point point)
    {
        if (track == null) return default;
        if (staffMode) return HitStaff(track.Notes, point);
        int pitch = Pitch(point.Y);
        var notes = track.Notes;
        for (int i = notes.Length - 1; i >= 0; i--)
        {
            var note = notes[i];
            if (note.Pitch != pitch) continue;
            var box = NoteRect(note);
            if (box.Contains(point))
                return (note, box.Right - point.X <= Math.Min(8, box.Width / 3) ? Drag.Resize : Drag.Move);
        }
        return default;
    }
    private void ApplyNotes(MidiNote[] notes) { track!.ChangeSequence(notes, track.Points); Changed?.Invoke(); NotifySelection(); }
    private void ApplyPoints(MidiPoint[] points) { track!.ChangeSequence(track.Notes, points); Changed?.Invoke(); InvalidateRoll(); }
    private void NotifySelection() { SelectionChanged?.Invoke(); InvalidateRoll(); }
    private void InvalidateRoll()
    {
        content = null;
        UpdateHover(pointer);
        InvalidateVisual();
    }

    protected override void ThemeChanged() => InvalidateRoll();

    internal static string PitchName(int pitch) => $"{PitchNames[pitch % 12]}{pitch / 12 - 1}";
    private static readonly string[] PitchNames = ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"];
    private FormattedText Text(string label, Brush brush, int size) => new(label, CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight, Mono, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private static Brush Ink(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
