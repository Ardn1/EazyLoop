namespace EazyLoop;

public partial class MainWindow
{
    private readonly EditHistory<ProjectSnapshot> history;

    private static bool SameArrangement(ProjectSnapshot a, ProjectSnapshot b)
    {
        if (a.Settings != b.Settings || a.Tracks.Length != b.Tracks.Length) return false;
        for (int i = 0; i < a.Tracks.Length; i++)
        {
            var left = a.Tracks[i];
            var right = b.Tracks[i];
            if (left.Samples != right.Samples ||
                (left.Settings with { Fx = null, Midi = null }) != (right.Settings with { Fx = null, Midi = null }))
                return false;
        }
        return true;
    }

    private void StepHistory(bool forward)
    {
        if (transport != Transport.Stopped || recordingRow != null) return;
        CollectTakes();
        if (history.Step(forward)) ProjectContentEdited();
    }

    private void RestoreArrangement(ProjectSnapshot state)
    {
        ready = false;
        try
        {
            foreach (var row in rows.ToArray())
                if (!state.Tracks.Any(track => track.Settings.Id == row.Data.Id))
                    DetachTrack(row);

            for (int i = 0; i < state.Tracks.Length; i++)
            {
                var saved = state.Tracks[i];
                var row = rows.Find(item => item.Data.Id == saved.Settings.Id);
                if (row == null) row = AddTrack(false, saved)!;
                row.RestoreArrangement(saved, state.Settings.Grid, options.Driver, inputDevices);
                PlaceTrack(row, i);
            }

            var settings = state.Settings;
            Tempo.Text = settings.Grid.Bpm.ToString();
            Meter.SelectedValue = settings.Grid.Beats;
            Bars.SelectedItem = settings.Grid.Bars;
            MetronomeButton.IsChecked = settings.Metronome;
            CountInButton.IsChecked = settings.CountIn;
            ClickVolume.Value = settings.ClickVolume * 100.0;
        }
        finally { ready = true; }

        CommitGrid();
        UpdateClick(edited: false);
        UpdateTrackCount();
        UpdateRecordingTarget();
    }
}
