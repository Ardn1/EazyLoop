using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace EazyLoop;

public sealed partial class PianoRollSurface
{
    private const double StaffStep = 15, HeadSize = 7, EndSize = 4.25, StaffInset = 13;
    private static readonly int[] StaffOffsets = [0, 0, 1, 1, 2, 3, 3, 4, 4, 5, 5, 6];
    private static readonly int[] Naturals = [0, 2, 4, 5, 7, 9, 11];
    private static readonly int[] StaffLines = [25, 27, 29, 31, 33, 37, 39, 41, 43, 45];
    private static Pen StaffLine => ThemeManager.Stroke("StrongLine");
    private static Pen LedgerLine => ThemeManager.Stroke("Muted");
    private static Brush StaffPaper => ThemeManager.Brush("Panel");
    private static readonly Typeface Symbols = new("Segoe UI Symbol");
    private bool staffMode;
    internal bool IsStaff => staffMode;
    internal bool AddSharp { get; set; }
    internal int PitchUnits => staffMode ? 75 : 128;

    internal void SetStaffMode(bool enabled)
    {
        staffMode = enabled;
        InvalidateRoll();
    }

    private static int StaffIndex(int pitch) => pitch / 12 * 7 + StaffOffsets[pitch % 12];
    private static bool Black(int pitch) => pitch % 12 is 1 or 3 or 6 or 8 or 10;
    private static int NoteAtStaff(int index, bool sharp)
    {
        index = Math.Clamp(index, 0, 74);
        int position = index % 7;
        return Math.Min(127, index / 7 * 12 + Naturals[position] + (sharp && position is not (2 or 6) ? 1 : 0));
    }
    private double StaffY(int line) => Ruler + (topPitch - line + .5) * StaffStep;
    private int StaffPitch(double y) => NoteAtStaff(topPitch - (int)Math.Floor((y - Ruler) / StaffStep), AddSharp);
    private Point HeadAt(MidiNote note) => new(Math.Min(X(note.Start) + StaffInset, Math.Max(Keys + StaffInset, ActualWidth - 18)),
        StaffY(StaffIndex(note.Pitch)));

    private Point LengthHandleAt(Point head) => new(Math.Max(Keys + 7, head.X - 12), Math.Min(LaneTop - 4, head.Y + 8));
    private static Point VelocityHandleAt(Point head) => new(head.X + 12, Math.Max(Ruler + 5, head.Y - 9));

    private (double From, double To, bool ShowEnd) DurationSpan(MidiNote note, Point head)
    {
        bool showStart = note.Start >= leftTick;
        double from = showStart ? head.X : Keys;
        double end = X(note.Start + note.Length);
        if (end > ActualWidth + .01) return (from, ActualWidth, false);

        double minimum = showStart ? from + HeadSize + EndSize + 2 : from;
        double to = Math.Min(Math.Max(minimum, end + StaffInset), ActualWidth - EndSize - 1);
        return (from, Math.Max(from, to), true);
    }

    private (MidiNote? Note, Drag Gesture) HitStaff(MidiNote[] notes, Point point)
    {
        (MidiNote? Note, Drag Gesture) hit = default;
        MidiNote? stripHit = null;
        double nearest = double.PositiveInfinity;
        double rightTick = leftTick + VisibleTicks;
        foreach (var note in notes)
        {
            if (note.Start + note.Length <= leftTick || note.Start >= rightTick) continue;
            var head = HeadAt(note);
            if (head.Y + HeadSize < Ruler || head.Y - HeadSize >= LaneTop) continue;
            var (from, to, showEnd) = DurationSpan(note, head);
            if (showEnd) Test(note, Drag.Resize, new Point(to, head.Y), 6);
            if (point.X >= from && point.X <= to && Math.Abs(point.Y - head.Y) <= 4) stripHit = note;
            if (note.Start >= leftTick)
            {
                Test(note, Drag.Move, head, 9);
                Test(note, Drag.Resize, LengthHandleAt(head), 8);
                Test(note, Drag.StaffVelocity, VelocityHandleAt(head), 7);
            }
        }
        return hit.Note == null && stripHit != null ? (stripHit, Drag.Move) : hit;

        void Test(MidiNote note, Drag gesture, Point center, double radius)
        {
            double distance = (point - center).LengthSquared;
            if (distance > radius * radius || distance > nearest) return;
            if (distance == nearest && hit.Gesture == Drag.Move && gesture != Drag.Move) return;
            nearest = distance;
            hit = (note, gesture);
        }
    }

    private int MovePitch(int pitch, int delta) => staffMode
        ? NoteAtStaff(StaffIndex(pitch) + delta, Black(pitch))
        : Math.Clamp(pitch + delta, 0, 127);

    private void DrawStaff(DrawingContext drawing, double width)
    {
        drawing.DrawRectangle(StaffPaper, null, new Rect(0, Ruler, width, LaneTop - Ruler));
        drawing.DrawRectangle(LightRow, null, new Rect(0, Ruler, Keys - 1, LaneTop - Ruler));
        foreach (int line in StaffLines)
        {
            double y = StaffY(line);
            if (y < Ruler || y >= LaneTop) continue;
            drawing.DrawLine(StaffLine, new Point(13, y), new Point(width, y));
        }
        DrawClef("𝄞", 41, Locale.T("Скрип."));
        DrawClef("𝄢", 31, Locale.T("Бас"));
        if (Scale.HasValue)
            for (int line = topPitch; line >= Math.Max(0, topPitch - RowsVisible); line--)
                DrawScaleMarker(drawing, NoteAtStaff(line, AddSharp), StaffY(line));
        for (int c = 0; c <= 70; c += 7)
        {
            double y = StaffY(c);
            if (y < Ruler + 8 || y > LaneTop - 8) continue;
            drawing.DrawText(Text($"C{c / 7 - 1}", InkText, 10), new Point(Keys - 24, y - 6));
        }

        void DrawClef(string clef, int line, string label)
        {
            double y = StaffY(line);
            if (y < Ruler - 12 || y > LaneTop + 12) return;
            var symbol = new FormattedText(clef, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Symbols,
                35, SelectedNote, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawing.DrawText(symbol, new Point(10, y - symbol.Height / 2));
            drawing.DrawText(Text(label, InkText, 9), new Point(10, y + 21));
        }
    }

    private void DrawStaffNote(DrawingContext drawing, MidiNote note, Brush fill)
    {
        var center = HeadAt(note);
        bool showStart = note.Start >= leftTick;
        if (showStart)
        {
            int line = StaffIndex(note.Pitch);
            if (line == 35) Ledger(35);
            else if (line < 25)
                for (int ledger = 23; ledger >= line; ledger -= 2) Ledger(ledger);
            else if (line > 45)
                for (int ledger = 47; ledger <= line; ledger += 2) Ledger(ledger);

            if (Black(note.Pitch))
                drawing.DrawText(Text("♯", InkText, 13), new Point(center.X - 20, center.Y - 9));
        }

        var (from, to, showEnd) = DurationSpan(note, center);
        var handlePen = new Pen(fill, 1.25);
        drawing.PushOpacity(.65);
        drawing.DrawRoundedRectangle(fill, null, new Rect(from, center.Y - 1.6, to - from, 3.2), 1.6, 1.6);
        drawing.Pop();
        if (showEnd) drawing.DrawEllipse(StaffPaper, handlePen, new Point(to, center.Y), EndSize, EndSize);
        if (!showStart) return;
        drawing.DrawEllipse(fill, null, center, HeadSize, HeadSize);

        var velocity = VelocityHandleAt(center);
        drawing.DrawEllipse(StaffPaper, handlePen, velocity, 4, 4);
        double level = 1 + 2 * note.Velocity / 127.0;
        drawing.DrawEllipse(fill, null, velocity, level, level);

        var length = LengthHandleAt(center);
        var left = length - new Vector(6, 0);
        var right = length + new Vector(6, 0);
        drawing.DrawLine(handlePen, left, right);
        drawing.DrawLine(handlePen, left, left + new Vector(3, -3));
        drawing.DrawLine(handlePen, left, left + new Vector(3, 3));
        drawing.DrawLine(handlePen, right, right + new Vector(-3, -3));
        drawing.DrawLine(handlePen, right, right + new Vector(-3, 3));
        drawing.DrawText(Text(DurationLabel(note.Length), fill, 10), new Point(center.X + 20, center.Y - 14));

        void Ledger(int at)
        {
            double y = StaffY(at);
            drawing.DrawLine(LedgerLine, new Point(center.X - 12, y), new Point(center.X + 12, y));
        }
    }

    private void DrawStaffHover(DrawingContext drawing, MidiNote note)
    {
        var head = HeadAt(note);
        var (from, to, showEnd) = DurationSpan(note, head);
        if (note.Start >= leftTick)
        {
            drawing.DrawEllipse(null, HoverOutline, head, HeadSize + 1, HeadSize + 1);
            from += HeadSize + 1;
        }
        if (showEnd) drawing.DrawEllipse(null, HoverOutline, new Point(to, head.Y), EndSize + 1, EndSize + 1);
        double lineEnd = showEnd ? to - EndSize - 1 : to;
        if (lineEnd > from) drawing.DrawLine(HoverOutline, new Point(from, head.Y), new Point(lineEnd, head.Y));
    }

    private static string DurationLabel(int ticks) => ticks switch
    {
        3840 => "1/1", 1920 => "1/2", 1440 => "3/8", 960 => "1/4", 720 => "3/16",
        480 => "1/8", 360 => "3/32", 320 => "1/8T", 240 => "1/16", 160 => "1/16T", 120 => "1/32",
        _ => Locale.F($"{ticks / (double)MidiTrackSettings.Ppq:0.##} дол.")
    };

    internal static bool TryPitch(string text, out int pitch)
    {
        if (int.TryParse(text, out pitch)) return pitch is >= 0 and <= 127;
        text = text.Trim().ToUpperInvariant();
        if (text.Length < 2) return false;
        int letter = "CDEFGAB".IndexOf(text[0]);
        if (letter < 0) return false;
        int adjust = text[1] switch { '#' or '♯' => 1, 'B' or '♭' => -1, _ => 0 };
        int start = adjust == 0 ? 1 : 2;
        if (!int.TryParse(text.AsSpan(start), out int octave) || octave is < -2 or > 10) return false;
        pitch = (octave + 1) * 12 + Naturals[letter] + adjust;
        return pitch is >= 0 and <= 127;
    }
}
