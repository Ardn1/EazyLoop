using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;

namespace EazyLoop;

public partial class MainWindow
{
    private const string OpenProjectPattern = "*" + ProjectStore.Extension + ";*" + ProjectStore.LegacyExtension;
    private static readonly ProjectSettings DefaultProject = new(new LoopGrid(100, 4, 2), false, false, 1);
    private ProjectStore? project;
    private long revision;
    private long savedRevision;
    private long autoSaveAt = long.MaxValue;
    private Task<bool>? saving;
    private string? projectWarning;
    private bool fileBusy;
    private bool closeApproved;
    private bool Dirty => revision != savedRevision;

    private void ProjectEdited()
    {
        if (!ready) return;
        history?.Commit();
        ProjectContentEdited();
    }

    private void ProjectContentEdited()
    {
        repeatRecording = false;
        MarkProjectDirty();
    }

    private void MarkProjectDirty()
    {
        if (!ready) return;
        revision++;
        autoSaveAt = Environment.TickCount64 + 350;
        UpdateProjectHeader();
    }

    private void CollectTakes()
    {
        while (engine.Changed() is { } track)
        {
            var row = rows.FirstOrDefault(item => item.Data == track);
            if (row == null) continue;
            row.RefreshWave();
            history?.Commit();
            MarkProjectDirty();
        }
    }

    private ProjectSnapshot Snapshot() => new(
        new ProjectSettings(grid, MetronomeButton.IsChecked == true, CountInButton.IsChecked == true,
            (float)(ClickVolume.Value / 100)),
        rows.Select(row => row.Snapshot()).ToArray());

    private void UpdateProjectHeader()
    {
        ProjectLabel.Text = project?.Name ?? Locale.T("Без проекта");
        ProjectLabel.ToolTip = project?.FilePath;
        ProjectStatus.Text = saving != null ? Locale.T("Сохранение…") :
            projectWarning != null ? Locale.T("Проверьте сохранение") :
            Dirty ? Locale.T("Есть изменения") : project != null ? Locale.T("Сохранено") : "";
        ProjectStatus.ToolTip = projectWarning;
    }

    private void Autosave()
    {
        if (project != null && Dirty && !fileBusy && saving == null && Environment.TickCount64 >= autoSaveAt)
            _ = SaveOnce();
    }

    private Task<bool> SaveOnce() => saving ??= SaveRevisionAsync(project!, Snapshot(), revision);

    private async Task<bool> SaveRevisionAsync(ProjectStore target, ProjectSnapshot snapshot, long currentRevision)
    {
        autoSaveAt = long.MaxValue;
        await Task.Yield();
        UpdateProjectHeader();
        try
        {
            projectWarning = await Task.Run(() => target.Save(snapshot));
            savedRevision = currentRevision;
            return true;
        }
        catch (Exception ex)
        {
            projectWarning = ex.Message;
            return false;
        }
        finally
        {
            saving = null;
            UpdateProjectHeader();
        }
    }

    private async Task<bool> FlushProjectAsync()
    {
        do
        {
            if (!await SaveOnce()) return false;
        }
        while (Dirty);
        return true;
    }

    private async void NewProjectClicked(object sender, RoutedEventArgs e) => await RunFileActionAsync(async () =>
    {
        if (transport != Transport.Stopped || !await CanLeaveAsync()) return;
        string? path = ChooseDestination(Locale.T("Новый проект"));
        if (path == null) return;
        var snapshot = new ProjectSnapshot(DefaultProject, []);
        var (store, warning) = await Task.Run(() => ProjectStore.Create(path, snapshot));
        SwitchProject(store, snapshot, warning);
    });

    private async void OpenProjectClicked(object sender, RoutedEventArgs e) => await RunFileActionAsync(async () =>
    {
        if (transport != Transport.Stopped) return;
        var dialog = new OpenFileDialog
        {
            Title = Locale.T("Открыть проект EazyLoop"),
            Filter = Locale.F($"Проекты EazyLoop ({OpenProjectPattern})|{OpenProjectPattern}"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true ||
            string.Equals(project?.FilePath, Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase) ||
            !await CanLeaveAsync()) return;
        var (store, snapshot, warning) = await Task.Run(() => ProjectStore.Open(dialog.FileName));
        SwitchProject(store, snapshot, warning);
    });

    private async void SaveProjectClicked(object sender, RoutedEventArgs e) => await RunFileActionAsync(async () =>
    {
        foreach (var row in rows) row.CaptureFxState();
        if (project == null)
        {
            await SaveNewProjectAsync();
            return;
        }

        if (!await FlushProjectAsync() || projectWarning != null)
            MessageBox.Show(this, projectWarning ?? Locale.T("Не удалось сохранить проект."), Locale.T("Сохранение проекта"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
    });

    private async Task<bool> SaveNewProjectAsync()
    {
        string? path = ChooseDestination(Locale.T("Сохранить проект"));
        if (path == null) return false;
        CollectTakes();
        var snapshot = Snapshot();
        long currentRevision = revision;
        var (store, warning) = await Task.Run(() => ProjectStore.Create(path, snapshot));
        project = store;
        projectWarning = warning;
        savedRevision = currentRevision;
        UpdateProjectHeader();
        return true;
    }

    private async void ExportTrack(TrackRow row) => await RunFileActionAsync(async () =>
    {
        var snapshot = row.Snapshot();
        if (snapshot.Settings.Frames == 0) return;

        string title = snapshot.Settings.Name ?? Locale.F($"Дорожка {snapshot.Settings.Number}");
        char[] invalid = Path.GetInvalidFileNameChars();
        string name = string.Concat(title.Select(c => invalid.Contains(c) ? '_' : c)).TrimEnd(' ', '.');
        var dialog = new SaveFileDialog
        {
            Title = Locale.T("Сохранить дорожку как WAV"),
            Filter = Locale.T("Аудиофайл WAV (*.wav)|*.wav"),
            DefaultExt = ".wav",
            FileName = $"{snapshot.Settings.Number:00} — {name}.wav",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        if (project?.IsManagedWave(dialog.FileName) == true)
            throw new IOException(Locale.T("Выберите другое имя для экспортируемого WAV. Это имя используется внутри проекта."));
        string path = dialog.FileName;
        await Task.Run(() => ProjectStore.ExportWave(path, snapshot));
    }, Locale.T("Экспорт дорожки"));

    private string? ChooseDestination(string title)
    {
        var dialog = new ProjectNameWindow(this, title, ProjectStore.DefaultFolder);
        return dialog.ShowDialog() == true ? dialog.Destination : null;
    }

    private async Task<bool> CanLeaveAsync()
    {
        foreach (var row in rows) row.CloseFxWindow();
        CollectTakes();
        if (project != null)
        {
            if ((!Dirty && saving == null) || await FlushProjectAsync()) return true;
            return MessageBox.Show(this, Locale.F($"Последние изменения не сохранены. {projectWarning}\n\nПродолжить без них?"),
                Locale.T("Сохранение проекта"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                == MessageBoxResult.Yes;
        }

        if (!Dirty) return true;
        return MessageBox.Show(this, Locale.T("Сохранить текущие дорожки и настройки в проект?"),
            "EazyLoop", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => await SaveNewProjectAsync(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private void SwitchProject(ProjectStore store, ProjectSnapshot snapshot, string? warning)
    {
        repeatRecording = false;
        project?.Dispose();
        project = store;
        projectWarning = warning;
        ready = false;
        foreach (var row in rows)
        {
            engine.Remove(row.Data);
            row.DisposeEffects();
        }
        rows.Clear();
        TrackList.Children.Clear();
        armedRow = null;
        grid = snapshot.Settings.Grid;
        Tempo.Text = grid.Bpm.ToString();
        Meter.SelectedValue = grid.Beats;
        Bars.SelectedItem = grid.Bars;
        MetronomeButton.IsChecked = snapshot.Settings.Metronome;
        CountInButton.IsChecked = snapshot.Settings.CountIn;
        ClickVolume.Value = snapshot.Settings.ClickVolume * 100.0;
        ClickVolumeButton.IsChecked = false;
        foreach (var track in snapshot.Tracks) AddTrack(false, track);
        if (rows.Count == 0) AddTrack(false);
        TrackScroll.ScrollToTop();
        ready = true;
        engine.Seek(0, 0);
        CommitGrid();
        UpdateClick(edited: false);
        UpdateRecordingTarget();
        revision = savedRevision = 0;
        autoSaveAt = long.MaxValue;
        history.Reset();
        UpdateProjectHeader();
        Refresh();
    }

    private async Task RunFileActionAsync(Func<Task> action, string errorTitle = "Проект EazyLoop")
    {
        if (fileBusy) return;
        fileBusy = true;
        Keyboard.ClearFocus();
        Workspace.IsEnabled = false;
        try { await action(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Locale.T(errorTitle), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Workspace.IsEnabled = true;
            fileBusy = false;
        }
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closeApproved)
        {
            ComponentDispatcher.ThreadFilterMessage -= TransportKeyMessage;
            timer.Stop();
            // The driver may still own a blocked callback during process exit.
            if (sound?.StopForExit() != false)
                foreach (var row in rows) row.DisposeEffects();
            project?.Dispose();
            return;
        }

        e.Cancel = true;
        if (fileBusy) return;
        // Leave the original Closing event before issuing the final Close().
        await Task.Yield();
        bool canClose = false;
        await RunFileActionAsync(async () =>
        {
            engine.AbortAudio();
            Refresh();
            canClose = await CanLeaveAsync();
        });
        if (!canClose) return;
        closeApproved = true;
        Close();
    }
}
