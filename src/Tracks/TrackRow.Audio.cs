using System.Windows;
using System.Windows.Input;

namespace EazyLoop;

public partial class TrackRow
{
    private AudioEditorWindow? audioEditor;
    internal event Action? SaveAudioProjectRequested;

    private void OpenAudioEditor()
    {
        if (IsMidi || active || Data.Length == 0) return;
        if (audioEditor != null)
        {
            audioEditor.Activate();
            return;
        }
        audioEditor = new AudioEditorWindow(Data.Samples, Data.Length, Data.Audio?.Edit, TrackName.Text, grid,
            ApplyAudioEdit, () => SaveAudioProjectRequested?.Invoke(),
            () => ToggleTransportRequested?.Invoke(), frame =>
                SeekRequested?.Invoke(LoopEngine.Mod((long)frame + Data.Shift, grid.Frames)))
        {
            Owner = Window.GetWindow(this)
        };
        audioEditor.Closed += (_, _) => audioEditor = null;
        audioEditor.Show();
    }

    private void ApplyAudioEdit(AudioEdit? edit)
    {
        engine.ReplaceAudio(Data, Data.Samples, Data.Length, edit);
        RefreshWave();
        Edited?.Invoke();
    }

    private void EditAudioMenuClicked(object sender, RoutedEventArgs e) => OpenAudioEditor();

    private void WaveKeys(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.IsRepeat) return;
        OpenAudioEditor();
        e.Handled = true;
    }
}
