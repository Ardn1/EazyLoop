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
    private static readonly Pen SelectedPen = new(Dark, 1.5);
    private static Pen Beat => ThemeManager.Stroke("StrongLine");
    private static Pen Bar => ThemeManager.Stroke("AccentLine");
    private static Pen Cell => ThemeManager.Stroke("Line");
    private static Pen Head => ThemeManager.Stroke("Mint", 1.5);
    private static readonly Typeface Ui = new("Segoe UI");
    private MidiTrack? track;
    private LoopGrid grid = new(100, 4, 2);
    private double scale = 1, offset;
    private int step = 240, playTick = -1;
    private Guid? selected;
    private DrawingGroup? content;
    private double RowHeight => Math.Clamp((ActualHeight - Ruler - 8) / (track?.Kit?.Count ?? 8), 28, 58);
    private double VisibleTicks => MidiTrackSettings.Ticks(grid) / scale;
    private double Factor => Math.Max(1, ActualWidth - LabelWidth) / VisibleTicks;
    private double X(int tick) => LabelWidth + (tick - offset) * Factor;

    internal event Action? Edited;
    internal event Action? SelectionChanged;
    internal event Action<int>? SettingsRequested;

    internal void Setup(MidiTrack midi)
    {
        track = midi;
        Refresh();
    }

    internal void SetViewport(LoopGrid value, int division, double zoom, double left)
    {
        grid = value; step = division; scale = zoom; offset = left;
        Refresh();
    }

    internal MidiNote? Selected => track?.Notes.FirstOrDefault(n => n.Id == selected);

    internal void SetVelocity(int velocity)
    {
        if (track == null || Selected is not { } note || note.Velocity == velocity) return;
        var updated = note with { Velocity = Math.Clamp(velocity, 1, 127) };
        Change(track.Notes.Select(n => n.Id == updated.Id ? updated : n).ToArray());
    }

    internal void Refresh() { content = null; InvalidateVisual(); }

    protected override void ThemeChanged() => Refresh();

    internal void Playhead(int tick)
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
        if (playTick >= 0)
        {
            double x = X(playTick);
            if (x >= LabelWidth && x <= ActualWidth)
                drawing.DrawLine(Head, new Point(x, 1), new Point(x, Ruler + track.Kit.Count * RowHeight));
        }
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
            int pad = Array.IndexOf(DrumKit.Keys, note.Pitch);
            if (pad < 0 || pad >= kit.Count || note.Start < offset - step || note.Start >= last) continue;
            double x = X(note.Start), y = Ruler + pad * RowHeight;
            double length = Math.Clamp(Math.Min(step, loop - note.Start) * Factor - 4, 3, width);
            var hit = new Rect(x + 2, y + 7, length, RowHeight - 14);
            context.PushOpacity(.45 + .55 * note.Velocity / 127.0);
            context.DrawRoundedRectangle(Pads[pad], note.Id == selected ? SelectedPen : null, hit, 4, 4);
            context.Pop();
            if (hit.Width >= 26)
            {
                var label = Text(note.Velocity.ToString(), Dark, 10);
                context.DrawText(label, new Point(hit.X + (hit.Width - label.Width) / 2, hit.Y + (hit.Height - label.Height) / 2));
            }
        }
        context.Pop();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Activate(e.GetPosition(this), false);
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        Activate(e.GetPosition(this), true);
        e.Handled = true;
    }

    private void Activate(Point at, bool edit)
    {
        if (track?.Kit is not { } kit || at.Y < Ruler || at.Y >= Ruler + kit.Count * RowHeight) return;
        int pad = (int)((at.Y - Ruler) / RowHeight), key = DrumKit.Keys[pad];
        if (at.X < LabelWidth)
        {
            if (edit) SettingsRequested?.Invoke(pad);
            else track.Preview([key]);
            return;
        }
        int loop = MidiTrackSettings.Ticks(grid);
        double time = offset + (at.X - LabelWidth) / Factor;
        int tick = Math.Clamp((int)(time / step) * step, 0, loop - 1);
        var notes = track.Notes;
        var hit = notes.FirstOrDefault(n => n.Pitch == key && n.Start == tick);
        if (edit)
        {
            hit ??= notes.FirstOrDefault(n => n.Pitch == key && n.Start >= tick && n.Start < tick + step);
            selected = hit?.Id;
            SelectionChanged?.Invoke();
            Refresh();
            return;
        }
        if (hit != null)
        {
            selected = null;
            Change(notes.Where(n => n.Id != hit.Id).ToArray());
        }
        else if (notes.Length < MidiTrackSettings.MaxNotes)
        {
            var note = new MidiNote(Guid.NewGuid(), key, tick, Math.Min(step, loop - tick), 100);
            selected = note.Id;
            Change([.. notes, note]);
            track.Preview([key]);
        }
    }

    private void Change(MidiNote[] notes)
    {
        track!.ChangeSequence(notes, track.Points);
        Edited?.Invoke();
        SelectionChanged?.Invoke();
        Refresh();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
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
