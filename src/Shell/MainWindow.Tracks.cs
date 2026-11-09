using System.Windows;

namespace EazyLoop;

public partial class MainWindow
{
    private IReadOnlyList<AudioDevice> inputDevices = [];
    private TrackRow? armedRow;
    private bool missingTrackInput;
    private TrackRow? draggingRow;
    private TrackRow? dropCueRow;
    private long lastDragScroll;

    private TrackRow? NextTrack() => armedRow is { IsMidi: false, Data.Length: 0 } ? armedRow
        : rows.FirstOrDefault(row => !row.IsMidi && row.Data.Length == 0);

    private void SelectRecordingTrack(TrackRow row)
    {
        if (row.IsMidi || row.Data.Length != 0) return;
        armedRow = row;
        UpdateRecordingTarget(retry: true);
    }

    private void UpdateRecordingTarget(bool retry = false)
    {
        var target = recordingRow ?? NextTrack();
        foreach (var row in rows) row.SetArmed(row == target && row.Data.Length == 0);
        if (recordingRow != null) return;

        missingTrackInput = target?.InputAvailable == false;
        sound?.SelectInput(missingTrackInput ? null : target?.Input?.Id ?? options.Input, retry);
    }

    private void BeginTrackDrag(TrackRow row)
    {
        draggingRow = row;
        lastDragScroll = 0;
        try
        {
            DragDrop.DoDragDrop(row, new DataObject("EazyLoop.Track", row.TrackNumber), DragDropEffects.Move);
        }
        finally
        {
            draggingRow = null;
            ClearDropCue();
        }
    }

    private void TrackDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (draggingRow == null) return;

        double y = e.GetPosition(TrackScroll).Y;
        long now = Environment.TickCount64;
        if (TrackScroll.ScrollableHeight > 0 && now - lastDragScroll >= 45 &&
            (y < 25 || y > TrackScroll.ActualHeight - 25))
        {
            TrackScroll.ScrollToVerticalOffset(TrackScroll.VerticalOffset + (y < 25 ? -16 : 16));
            lastDragScroll = now;
        }

        int insertion = DropIndex(e.GetPosition(TrackList).Y);
        ClearDropCue();
        dropCueRow = rows[Math.Min(insertion, rows.Count - 1)];
        dropCueRow.ShowDropCue(insertion < rows.Count);
        e.Effects = DragDropEffects.Move;
    }

    private void TrackDragLeave(object sender, DragEventArgs e) => ClearDropCue();

    private void TrackDrop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (draggingRow == null) return;
        MoveTrack(draggingRow, DropIndex(e.GetPosition(TrackList).Y));
        e.Effects = DragDropEffects.Move;
        ClearDropCue();
    }

    private int DropIndex(double y)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            double middle = row.TranslatePoint(new Point(0, row.ActualHeight / 2), TrackList).Y;
            if (y < middle) return i;
        }
        return rows.Count;
    }

    private void ClearDropCue()
    {
        dropCueRow?.ShowDropCue(null);
        dropCueRow = null;
    }

    private void MoveTrack(TrackRow row, int insertion)
    {
        int oldIndex = rows.IndexOf(row);
        if (oldIndex < 0) return;
        int newIndex = insertion > oldIndex ? insertion - 1 : insertion;
        if (oldIndex == newIndex) return;
        PlaceTrack(row, newIndex);
        UpdateTrackCount();
        UpdateRecordingTarget();
        ProjectEdited();
    }

    private void PlaceTrack(TrackRow row, int newIndex)
    {
        int oldIndex = rows.IndexOf(row);
        if (oldIndex == newIndex) return;
        rows.RemoveAt(oldIndex);
        rows.Insert(newIndex, row);
        TrackList.Children.RemoveAt(oldIndex);
        TrackList.Children.Insert(newIndex, row);
    }
}
