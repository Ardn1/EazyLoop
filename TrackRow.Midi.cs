using System.Windows;
using System.Windows.Input;

namespace EazyLoop;

public partial class TrackRow
{
    private PianoRollWindow? midiEditor;
    private GlissandoTimelineWindow? glissandoEditor;
    private DrumWindow? drumEditor;
    private DrumSettingsWindow? drumSettings;
    internal bool IsMidi => Data.Midi != null;
    private bool IsDrums => Data.Midi?.Kit != null;
    internal event Action? ToggleTransportRequested;

    private void SetupMidi()
    {
        if (Data.Midi is not { } midi) return;
        WaveRow.Visibility = ShiftPanel.Visibility = Visibility.Collapsed;
        MidiPreview.Visibility = Visibility.Visible;
        UpdateMidiPreview(midi, -1);
        UpdateMidiLanguage();
    }

    private void UpdateMidiLanguage()
    {
        if (!IsMidi) return;
        if (IsDrums) MidiOpenButton.Content = Locale.T("Удары…");
        bool glissando = Data.Midi?.Timeline != null;
        if (glissando) MidiOpenButton.Content = Locale.T("Глиссандо…");
        string tooltip = IsDrums ? Locale.T("Открыть редактор ударных") :
            glissando ? Locale.T("Открыть редактор глиссандо") : Locale.T("Открыть редактор MIDI");
        TargetButton.ToolTip = MidiPreview.ToolTip = tooltip;
        System.Windows.Automation.AutomationProperties.SetName(TargetButton, tooltip);
    }

    private void MidiClicked(object sender, RoutedEventArgs e) => OpenMidi();

    private void MidiPreviewClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenMidi();
    }

    internal void OpenMidi()
    {
        if (Data.Midi is not { } midi) return;
        if (midi.Timeline != null)
        {
            if (glissandoEditor != null) { glissandoEditor.Activate(); return; }
            glissandoEditor = new GlissandoTimelineWindow(midi, TrackName.Text, grid, MidiEdited,
                () => ToggleTransportRequested?.Invoke()) { Owner = Window.GetWindow(this) };
            glissandoEditor.Closed += (_, _) => glissandoEditor = null;
            glissandoEditor.Show();
            return;
        }
        if (midi.Kit != null)
        {
            if (drumEditor != null) { drumEditor.Activate(); return; }
            drumEditor = new DrumWindow(midi, TrackName.Text, grid, MidiEdited,
                () => ToggleTransportRequested?.Invoke(), OpenDrumSettings) { Owner = Window.GetWindow(this) };
            drumEditor.Closed += (_, _) => drumEditor = null;
            drumEditor.Show();
            return;
        }
        if (midiEditor != null) { midiEditor.Activate(); return; }
        midiEditor = new PianoRollWindow(midi, TrackName.Text, grid, MidiEdited,
            () => ToggleTransportRequested?.Invoke()) { Owner = Window.GetWindow(this) };
        midiEditor.Closed += (_, _) => midiEditor = null;
        midiEditor.Show();
    }

    private void MidiEdited()
    {
        if (Data.Midi is { } midi) UpdateMidiPreview(midi, -1);
        ContentEdited?.Invoke();
    }

    private void UpdateMidiPreview(MidiTrack midi, int phase) =>
        MidiPreview.Update(midi.Notes, grid, phase, midi.Kit?.Count ?? 0, midi.Timeline);

    private void DrumSettingsClicked(object sender, RoutedEventArgs e) => OpenDrumSettings(-1);

    private void OpenDrumSettings(int pad)
    {
        if (Data.Midi is not { Kit: not null } midi) return;
        if (drumSettings != null) { drumSettings.SelectPad(pad); return; }
        drumSettings = new DrumSettingsWindow(midi, pad, () =>
        {
            drumEditor?.KitChanged();
            MidiEdited();
        }) { Owner = (Window?)drumEditor ?? Window.GetWindow(this) };
        drumSettings.Closed += (_, _) => drumSettings = null;
        drumSettings.Show();
    }
}
