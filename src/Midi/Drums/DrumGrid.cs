using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace EazyLoop;

public sealed class DrumGrid : ThemedCanvas
{
    private const double LabelWidth = 144, Ruler = 27;
    private static readonly Brush Dark = Ink("#221E31");
    private static Brush Surface => ThemeManager.Brush("Surface");
    private static Brush InkText => ThemeManager.Brush("Ink");
    private static Brush Muted => ThemeManager.Brush("Soft");
    private static Brush BandA => ThemeManager.Brush("Panel");
    private static Brush BandB => ThemeManager.Brush("Surface");
    private static readonly Brush[] Pads = DrumKit.Colors.Select(Ink).ToArray();
    private static Pen Beat => ThemeManager.Stroke("StrongLine");
    private static Pen Bar => ThemeManager.Stroke("AccentLine");
    private static Pen Cell => ThemeManager.Stroke("Line");
    private static Pen Head => ThemeManager.Stroke("Mint", 1.5);
    private static Pen SelectedPen => ThemeManager.Stroke("Ink", 2);
    private static Pen PasteHead => ThemeManager.Stroke("Violet");
    private static readonly Typeface Ui = new("Segoe UI");
    private static MidiNote[] copiedNotes = [];
    private MidiTrack? track;
    private LoopGrid grid = new(100, 4, 2);
    private double scale = 1, offset, playTick = -1;
    private int step = 240;
    private readonly HashSet<Guid> selection = [];
    private Point anchor;
    private MidiNote? pressed;
    private bool moved, moveAllowed;
    private Rect marquee = Rect.Empty;
    private MidiNote[]? movePreview;
    private int? pasteTick;
    private DrawingGroup? content;
    private double RowHeight => Math.Clamp((ActualHeight - Ruler - 8) / (track?.Kit?.Count ?? 8), 28, 58);
    private double VisibleTicks => MidiTrackSettings.Ticks(grid) / scale;
    private double Factor => Math.Max(1, ActualWidth - LabelWidth) / VisibleTicks;
    private double X(double tick) => LabelWidth + (tick - offset) * Factor;
    private int LoopTicks => MidiTrackSettings.Ticks(grid);
    private double GridBottom => Math.Min(ActualHeight, Ruler + (track?.Kit?.Count ?? 0) * RowHeight);
    private static int Pad(MidiNote note) => Array.IndexOf(DrumKit.Keys, note.Pitch);
    private int PadAt(double y) => (int)((y - Ruler) / RowHeight);
    private double TimeAt(double x) => offset + (x - LabelWidth) / Factor;
    private int TickAt(double x) => Math.Clamp((int)(TimeAt(x) / step) * step, 0, LoopTicks - 1);
    private bool OnRuler(Point point) => point.X >= LabelWidth && point.X <= ActualWidth && point.Y >= 0 && point.Y < Ruler;

    public DrumGrid() => Focusable = true;

    internal event Action? Edited;
    internal event Action? SelectionChanged;
    internal event Action<int>? SettingsRequested;
    internal event Action<int>? SeekRequested;

    internal void Setup(MidiTrack midi)
    {
        track = midi;
        Refresh();
    }

    internal void SetViewport(LoopGrid value, int division, double zoom, double left)
    {
        CancelGesture();
        grid = value; step = division; scale = zoom; offset = left;
        if (pasteTick >= LoopTicks) pasteTick = null;
        Refresh();
    }

    internal bool HasSelection => selection.Count != 0;
    internal MidiNote? Selected => selection.Count == 1 ? track?.Notes.FirstOrDefault(n => selection.Contains(n.Id)) : null;

    internal void SetVelocity(int velocity)
    {
        if (track == null || Selected is not { } note || note.Velocity == velocity) return;
        var updated = note with { Velocity = Math.Clamp(velocity, 1, 127) };
        Change(track.Notes.Select(n => n.Id == updated.Id ? updated : n).ToArray());
    }

    internal void SyncSelection()
    {
        CancelGesture();
        var visible = track!.Notes.Where(IsVisibleHit);
        selection.IntersectWith(visible.Select(n => n.Id));
        NotifySelection();
    }

    internal void CopyNotes()
    {
        if (track == null || !HasSelection) return;
        var notes = track.Notes.Where(n => selection.Contains(n.Id)).ToArray();
        int first = notes.Min(n => n.Start);
        copiedNotes = notes.Select(n => n with { Start = n.Start - first }).ToArray();
    }

    internal string? PasteNotes()
    {
        if (track?.Kit is not { } kit || copiedNotes.Length == 0) return Locale.T("Сначала выделите и скопируйте ноты.");
        if (pasteTick is not int start) return Locale.T("Правый щелчок по линейке задаёт место вставки.");
        if (start + copiedNotes.Max(n => n.Start + n.Length) > LoopTicks)
            return Locale.T("Фрагмент не помещается до конца лупа.");
        if (copiedNotes.Any(n => Pad(n) >= kit.Count))
            return Locale.T("В наборе не хватает полос для вставки.");
        var notes = track.Notes;
        if (notes.Length + copiedNotes.Length > MidiTrackSettings.MaxNotes)
            return Locale.T("В дорожке достигнут предел количества нот.");
        var pasted = copiedNotes.Select(n => n with { Id = Guid.NewGuid(), Start = start + n.Start }).ToArray();
        if (Overlaps(pasted, notes)) return Locale.T("В месте вставки уже есть удары.");

        Select(pasted);
        Change([.. notes, .. pasted]);
        return null;
    }

    internal void RemoveSelected()
    {
        if (track == null || !HasSelection) return;
        var remaining = track.Notes.Where(n => !selection.Contains(n.Id)).ToArray();
        selection.Clear();
        Change(remaining);
    }

    private void Refresh() { content = null; InvalidateVisual(); }

    protected override void ThemeChanged() => Refresh();

    internal void Playhead(double tick)
    {
        if (tick == playTick) return;
        playTick = tick;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        if (track?.Kit == null || ActualWidth <= LabelWidth || ActualHeight <= Ruler) return;
        if (content == null)
        {
            var group = new DrawingGroup();
            using (var context = group.Open()) DrawGrid(context, track.Kit);
            group.Freeze();
            content = group;
        }
        drawing.DrawDrawing(content);
        drawing.PushClip(new RectangleGeometry(new Rect(LabelWidth, 0, ActualWidth - LabelWidth, GridBottom)));
        if (pasteTick is int tick)
        {
            double x = X(tick);
            drawing.DrawLine(PasteHead, new Point(x, 0), new Point(x, GridBottom));
            drawing.DrawRectangle(PasteHead.Brush, null, new Rect(x, 0, 5, 8));
        }
        if (!marquee.IsEmpty)
            drawing.DrawRectangle(ThemeManager.Brush("MintShade"), Head, marquee);
        if (movePreview != null)
        {
            drawing.PushOpacity(.8);
            var outline = moveAllowed ? Head : ThemeManager.Stroke("Rose", 2);
            foreach (var note in movePreview) DrawHit(drawing, note, Pad(note), outline);
            drawing.Pop();
        }
        if (playTick >= 0)
        {
            double x = X(playTick);
            if (x >= LabelWidth && x <= ActualWidth)
                drawing.DrawLine(Head, new Point(x, 1), new Point(x, GridBottom));
        }
        drawing.Pop();
    }

    private void DrawGrid(DrawingContext context, DrumKit kit)
    {
        double width = ActualWidth, height = ActualHeight;
        context.DrawRoundedRectangle(Surface, null, new Rect(0, 0, width, height), 8, 8);
        context.DrawText(Text(Locale.T("УДАРНЫЕ"), Muted, 10), new Point(12, 6));
        for (int i = 0; i < kit.Count; i++)
        {
            double y = Ruler + i * RowHeight;
            context.DrawRectangle(i % 2 == 0 ? BandA : BandB, null, new Rect(LabelWidth, y, width - LabelWidth, RowHeight - 1));
            context.DrawRoundedRectangle(Pads[i], null, new Rect(6, y + 4, LabelWidth - 12, RowHeight - 8), 5, 5);
            var name = Text(kit.PadName(i), Dark, 12);
            name.MaxTextWidth = LabelWidth - 23;
            name.Trimming = TextTrimming.CharacterEllipsis;
            context.DrawText(name, new Point(14, y + (RowHeight - name.Height) / 2));
        }
        double bottom = Ruler + kit.Count * RowHeight;
        context.PushClip(new RectangleGeometry(new Rect(LabelWidth, 0, width - LabelWidth, Math.Min(bottom, height))));
        int loop = MidiTrackSettings.Ticks(grid), beat = MidiTrackSettings.Ppq, bar = grid.Beats * beat;
        int stride = step * Factor >= 4 ? step : beat;
        int first = Math.Max(0, (int)(offset / stride) * stride);
        int last = Math.Min(loop, (int)Math.Ceiling(offset + VisibleTicks));
        for (int i = first; i <= last; i += stride)
        {
            double x = X(i);
            bool major = i % bar == 0;
            context.DrawLine(major ? Bar : i % beat == 0 ? Beat : Cell, new Point(x, major ? 0 : Ruler), new Point(x, bottom));
            if (major) context.DrawText(Text((i / bar + 1).ToString("00"), InkText, 11), new Point(x + 6, 6));
        }
        foreach (var note in track!.Notes)
        {
            int pad = Pad(note);
            if (pad < 0 || pad >= kit.Count || note.Start >= loop) continue;
            var bounds = HitBounds(note, pad);
            if (bounds.Right < LabelWidth || bounds.Left > width) continue;
            DrawHit(context, note, pad, selection.Contains(note.Id) ? SelectedPen : null);
        }
        context.Pop();
    }

    private Rect HitBounds(MidiNote note, int pad)
    {
        double length = Math.Max(3, Math.Min(step, LoopTicks - note.Start) * Factor - 4);
        return new Rect(X(note.Start) + 2, Ruler + pad * RowHeight + 7, length, RowHeight - 14);
    }

    private void DrawHit(DrawingContext context, MidiNote note, int pad, Pen? outline)
    {
        var bounds = HitBounds(note, pad);
        context.PushOpacity(.45 + .55 * note.Velocity / 127.0);
        context.DrawRoundedRectangle(Pads[pad], null, bounds, 4, 4);
        context.Pop();
        if (outline != null) context.DrawRoundedRectangle(null, outline, bounds, 4, 4);
        if (bounds.Width < 26) return;
        var label = Text(note.Velocity.ToString(), Dark, 10);
        context.DrawText(label, new Point(bounds.X + (bounds.Width - label.Width) / 2,
            bounds.Y + (bounds.Height - label.Height) / 2));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (BeginGesture(e.GetPosition(this)) && !CaptureMouse()) ResetGesture();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        CancelGesture();
        RightClick(e.GetPosition(this));
        e.Handled = true;
    }

    private bool BeginGesture(Point point)
    {
        if (track?.Kit == null) return false;
        if (point.Y < Ruler)
        {
            if (OnRuler(point))
            {
                int frame = (int)Math.Round(TimeAt(point.X) * grid.BeatFrames / MidiTrackSettings.Ppq);
                SeekRequested?.Invoke(Math.Clamp(frame, 0, grid.Frames - 1));
            }
            return false;
        }
        if (point.Y >= GridBottom)
        {
            if (point.X >= LabelWidth) ClearSelection();
            return false;
        }
        if (point.X < LabelWidth)
        {
            track.Preview([DrumKit.Keys[PadAt(point.Y)]]);
            return false;
        }
        anchor = point;
        pressed = HitAt(point);
        return true;
    }

    private void RightClick(Point point)
    {
        if (track?.Kit == null) return;
        if (point.Y < Ruler)
        {
            if (OnRuler(point))
            {
                pasteTick = TickAt(point.X);
                InvalidateVisual();
            }
            return;
        }
        if (point.Y >= GridBottom)
        {
            if (point.X >= LabelWidth) ClearSelection();
            return;
        }
        if (point.X < LabelWidth)
        {
            SettingsRequested?.Invoke(PadAt(point.Y));
            return;
        }
        var hit = HitAt(point);
        if (hit == null) ClearSelection();
        else if (!selection.Contains(hit.Id))
        {
            Select([hit]);
            NotifySelection();
        }
    }

    private MidiNote? HitAt(Point point)
    {
        int pad = PadAt(point.Y), key = DrumKit.Keys[pad];
        return track!.Notes.LastOrDefault(n => n.Pitch == key && n.Start < LoopTicks && HitBounds(n, pad).Contains(point));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured) UpdateGesture(e.GetPosition(this));
    }

    protected override void OnQueryCursor(QueryCursorEventArgs e)
    {
        base.OnQueryCursor(e);
        if (IsMouseCaptured || !OnRuler(e.GetPosition(this))) return;
        e.Cursor = Cursors.Hand;
        e.Handled = true;
    }

    private void UpdateGesture(Point point)
    {
        if (!moved && Math.Abs(point.X - anchor.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - anchor.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (!moved && pressed is { } hit && !selection.Contains(hit.Id))
        {
            Select([hit]);
            NotifySelection();
        }
        moved = true;
        if (pressed == null)
        {
            marquee = new Rect(anchor, new Point(Math.Clamp(point.X, LabelWidth, ActualWidth),
                Math.Clamp(point.Y, Ruler, GridBottom)));
        }
        else
        {
            var notes = track!.Notes;
            var group = notes.Where(n => selection.Contains(n.Id)).ToArray();
            int dx = (int)Math.Round((point.X - anchor.X) / (Factor * step), MidpointRounding.AwayFromZero);
            int dy = (int)Math.Round((point.Y - anchor.Y) / RowHeight, MidpointRounding.AwayFromZero);
            dx = Math.Clamp(dx, -group.Min(n => n.Start) / step,
                Math.Max(0, LoopTicks - group.Max(n => n.Start + n.Length)) / step);
            dy = Math.Clamp(dy, -group.Min(Pad), track.Kit!.Count - 1 - group.Max(Pad));
            movePreview = group.Select(n => n with { Start = n.Start + dx * step, Pitch = DrumKit.Keys[Pad(n) + dy] }).ToArray();
            moveAllowed = !Overlaps(movePreview, notes.Where(n => !selection.Contains(n.Id)));
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        try { EndGesture(e.GetPosition(this)); }
        finally { ReleaseMouseCapture(); }
        e.Handled = true;
    }

    private void EndGesture(Point point)
    {
        UpdateGesture(point);
        if (!moved)
        {
            if (pressed == null && HasSelection) ClearSelection();
            else if (pressed == null || !selection.Contains(pressed.Id)) ToggleHit();
        }
        else if (pressed == null)
        {
            var notes = track!.Notes.Where(n => IsVisibleHit(n) && marquee.IntersectsWith(HitBounds(n, Pad(n))));
            Select(notes);
            NotifySelection();
        }
        else if (moveAllowed && movePreview is { } preview)
        {
            var movedById = preview.ToDictionary(n => n.Id);
            var notes = track!.Notes;
            var updated = notes.Select(n => movedById.GetValueOrDefault(n.Id, n)).ToArray();
            if (!updated.SequenceEqual(notes)) Change(updated);
        }
    }

    private void ToggleHit()
    {
        int key = DrumKit.Keys[PadAt(anchor.Y)], tick = TickAt(anchor.X);
        var notes = track!.Notes;
        var hit = pressed ?? notes.FirstOrDefault(n => n.Pitch == key && n.Start == tick);
        if (hit != null)
        {
            selection.Remove(hit.Id);
            Change(notes.Where(n => n.Id != hit.Id).ToArray());
        }
        else if (notes.Length < MidiTrackSettings.MaxNotes)
        {
            var note = new MidiNote(Guid.NewGuid(), key, tick, Math.Min(step, LoopTicks - tick), 100);
            Change([.. notes, note]);
            track.Preview([key]);
        }
    }

    private static bool Overlaps(IEnumerable<MidiNote> placed, IEnumerable<MidiNote> existing)
    {
        var occupied = existing.Select(n => (n.Pitch, n.Start)).ToHashSet();
        return placed.Any(n => occupied.Contains((n.Pitch, n.Start)));
    }

    internal bool CancelGesture()
    {
        if (!IsMouseCaptured) return false;
        ReleaseMouseCapture();
        return true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        ResetGesture();
        base.OnLostMouseCapture(e);
    }

    private void ResetGesture()
    {
        pressed = null;
        moved = false;
        movePreview = null;
        marquee = Rect.Empty;
        InvalidateVisual();
    }

    private void Select(IEnumerable<MidiNote> notes)
    {
        selection.Clear();
        selection.UnionWith(notes.Select(n => n.Id));
    }

    private bool IsVisibleHit(MidiNote note)
    {
        int pad = Pad(note);
        return pad >= 0 && pad < track!.Kit!.Count && note.Start < LoopTicks;
    }

    private void ClearSelection()
    {
        if (!HasSelection) return;
        selection.Clear();
        NotifySelection();
    }

    private void Change(MidiNote[] notes)
    {
        track!.ChangeSequence(notes, track.Points);
        Edited?.Invoke();
        NotifySelection();
    }

    private void NotifySelection()
    {
        SelectionChanged?.Invoke();
        Refresh();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        CancelGesture();
        Refresh();
    }

    private FormattedText Text(string text, Brush brush, double size) => new(text, CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight, Ui, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Brush Ink(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
