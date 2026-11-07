using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace EazyLoop;

public sealed class MidiPreviewView : ThemedCanvas
{
    private static Brush Surface => ThemeManager.Brush("Surface");
    private static Brush Accent => ThemeManager.Brush("Violet");
    private static Brush Muted => ThemeManager.Brush("Soft");
    private static Pen GridLine => ThemeManager.Stroke("Line");
    private static Pen Head => ThemeManager.Stroke("Mint");
    private static readonly Typeface Font = new("Segoe UI");
    private MidiNote[] notes = [];
    private GlissandoTimeline? timeline;
    private LoopGrid grid = new(100, 4, 2);
    private int phase = -1;
    private int low = 48, range = 24;
    private int drumCount;

    internal void Update(MidiNote[] value, LoopGrid settings, int position, int visibleDrums = 0, GlissandoTimeline? glissando = null)
    {
        if (notes == value && timeline == glissando && grid == settings && phase == position && drumCount == visibleDrums) return;
        if (notes != value || timeline != glissando)
        {
            notes = value;
            timeline = glissando;
            if (timeline is { Count: > 0 })
            {
                var points = timeline.Phrases.SelectMany(phrase => phrase);
                low = points.Min(point => point.Cents) / 100 - 3;
                range = Math.Max(8, points.Max(point => point.Cents) / 100 - low + 4);
            }
            else
            {
                low = notes.Length == 0 ? 48 : notes.Min(note => note.Pitch) - 3;
                range = notes.Length == 0 ? 24 : Math.Max(8, notes.Max(note => note.Pitch) - low + 4);
            }
        }
        grid = settings;
        phase = position;
        drumCount = visibleDrums;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 2) return;
        context.DrawRoundedRectangle(Surface, null, new Rect(0, 0, w, h), 7, 7);
        for (int i = 1; i < grid.Bars; i++)
        {
            double x = w * i / grid.Bars;
            context.DrawLine(GridLine, new Point(x, 5), new Point(x, h - 5));
        }
        if (timeline is { Count: > 0 })
        {
            int ticks = MidiTrackSettings.Ticks(grid);
            var trace = ThemeManager.Stroke("Violet", 1.5);
            foreach (var contour in timeline.Contours)
            {
                if (contour.Start > ticks) break;
                GlissandoRoll.DrawContour(context, contour, 0, ticks, X, Y, trace);
                foreach (var point in contour.Points)
                {
                    if (point.Position > ticks) break;
                    context.DrawEllipse(Accent, null, new Point(Math.Clamp(X(point.Position), 2, Math.Max(2, w - 2)),
                        Y(point.Cents)), 2, 2);
                }
            }
            double X(double tick) => w * tick / ticks;
            double Y(double cents) => h - 5 - (h - 10) * (cents / 100 - low) / range;
        }
        else if (notes.Length == 0)
        {
            var text = new FormattedText(timeline != null ? Locale.T("Открыть редактор глиссандо") :
                drumCount > 0 ? Locale.T("Открыть редактор ударных") : Locale.T("Открыть редактор MIDI"),
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Font, 12, Muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            context.DrawText(text, new Point((w - text.Width) / 2, (h - text.Height) / 2));
        }
        else
        {
            int ticks = MidiTrackSettings.Ticks(grid);
            foreach (var note in notes)
            {
                if (note.Start >= ticks || (drumCount > 0 && Array.IndexOf(DrumKit.Keys, note.Pitch) >= drumCount)) continue;
                double x = w * note.Start / ticks;
                double end = w * Math.Min(ticks, note.Start + note.Length) / ticks;
                double y = h - 5 - (h - 10) * (note.Pitch - low) / range;
                context.DrawRoundedRectangle(Accent, null, new Rect(x, y, Math.Min(w - x, Math.Max(2, end - x - 1)), 3), 1, 1);
            }
        }
        if (phase >= 0)
        {
            double x = w * phase / grid.Frames;
            context.DrawLine(Head, new Point(x, 2), new Point(x, h - 2));
        }
    }

}
