using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WpfTrack = System.Windows.Controls.Primitives.Track;

namespace EazyLoop;

public partial class App : Application
{
    private void RangeTrackDown(object sender, MouseButtonEventArgs e)
    {
        var range = (RangeBase)sender;
        var track = FindTrack(range);
        if (!range.IsEnabled || range.Minimum == range.Maximum || track?.Thumb is not { } thumb ||
            e.OriginalSource is Visual source && (source == thumb || thumb.IsAncestorOf(source))) return;
        range.Focus();
        if (!range.CaptureMouse()) return;
        SetRangePosition(range, track, e.GetPosition(track));
        e.Handled = true;
    }

    private void RangeTrackMove(object sender, MouseEventArgs e)
    {
        var range = (RangeBase)sender;
        if (!range.IsMouseCaptured) return;
        if (e.LeftButton != MouseButtonState.Pressed) range.ReleaseMouseCapture();
        else if (FindTrack(range) is { } track) SetRangePosition(range, track, e.GetPosition(track));
        e.Handled = true;
    }

    private void RangeTrackUp(object sender, MouseButtonEventArgs e)
    {
        var range = (RangeBase)sender;
        if (!range.IsMouseCaptured) return;
        if (FindTrack(range) is { } track) SetRangePosition(range, track, e.GetPosition(track));
        range.ReleaseMouseCapture();
        e.Handled = true;
    }

    private static WpfTrack? FindTrack(RangeBase range) => range.Template?.FindName("PART_Track", range) as WpfTrack;

    private static void SetRangePosition(RangeBase range, WpfTrack track, Point point)
    {
        bool horizontal = track.Orientation == Orientation.Horizontal;
        double length = horizontal ? track.ActualWidth : track.ActualHeight;
        var thumb = track.Thumb;
        double thumbLength = horizontal ? thumb.ActualWidth : thumb.ActualHeight;
        if (length <= thumbLength) return;
        double coordinate = horizontal ? point.X : point.Y;
        double fraction = Math.Clamp((coordinate - thumbLength / 2) / (length - thumbLength), 0, 1);
        if (horizontal == track.IsDirectionReversed) fraction = 1 - fraction;
        double value = range.Minimum + fraction * (range.Maximum - range.Minimum);
        if (range is Slider { IsSnapToTickEnabled: true } slider) value = Snap(slider, value);
        range.SetCurrentValue(RangeBase.ValueProperty, value);
    }

    private static double Snap(Slider slider, double value)
    {
        double lower = slider.Minimum, upper = slider.Maximum;
        if (slider.Ticks is { Count: > 0 } ticks)
        {
            foreach (double tick in ticks)
            {
                if (tick <= value && tick > lower) lower = tick;
                if (tick >= value && tick < upper) upper = tick;
            }
        }
        else if (slider.TickFrequency > 0)
        {
            double frequency = slider.TickFrequency;
            lower += Math.Floor((value - lower) / frequency) * frequency;
            upper = Math.Min(upper, lower + frequency);
        }
        else return value;
        return value - lower < upper - value ? lower : upper;
    }
}
