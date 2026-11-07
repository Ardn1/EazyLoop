using System.Windows;
using NAudio.Vst3;

namespace EazyLoop;

public partial class MainWindow
{
    private void AddSimpleClicked(object sender, RoutedEventArgs e)
    {
        var menu = AddSimpleButton.ContextMenu;
        menu.PlacementTarget = AddSimpleButton;
        menu.IsOpen = true;
    }

    private void AddSynthClicked(object sender, RoutedEventArgs e) =>
        AddMidiTrack(new MidiTrack(new MidiTrackSettings(SynthPatch.Default, null, [], [])), "SimpleSynth");

    private void AddGlissandoClicked(object sender, RoutedEventArgs e) =>
        AddMidiTrack(new MidiTrack(new MidiTrackSettings(null, null, [], [], Glissando: GlissandoSettings.Default)),
            "Glissando Synth");

    private void AddDrumsClicked(object sender, RoutedEventArgs e) =>
        AddMidiTrack(new MidiTrack(new MidiTrackSettings(null, null, [], [], DrumKit.Default)), "SimpleDrums");

    private void AddInstrumentClicked(object sender, RoutedEventArgs e)
    {
        Vst3Module? module = null;
        MidiTrack? instrument = null;
        try
        {
            string? path = FxWindow.ChooseModule(this);
            if (path == null) return;
            module = Vst3Module.Load(path);
            var types = module.GetClasses().Where(info => info.IsInstrument).ToArray();
            if (types.Length == 0) throw new NotSupportedException(Locale.T("В выбранном пакете нет VST3-инструмента."));
            var type = types.Length == 1 ? types[0] : FxWindow.Pick(this, Locale.T("Выберите VST3-инструмент"), types, nameof(Vst3ClassInfo.Name));
            if (type == null) return;
            var reference = new VstReference(path, type.ClassId, type.Name);
            instrument = new MidiTrack(new MidiTrackSettings(null, reference, [], []));
            instrument.Connect(module, type, path);
            module = null;
            AddMidiTrack(instrument, type.Name);
            instrument = null;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Locale.T("MIDI-инструмент"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { instrument?.Dispose(); module?.Dispose(); }
    }

    private void AddMidiTrack(MidiTrack instrument, string name)
    {
        var saved = new TrackSnapshot(new TrackSettings(Guid.NewGuid(), rows.Count + 1, 0, .85f, 0, 0, false, 0,
            Name: name[..Math.Min(name.Length, TrackSettings.MaxNameLength)], Midi: instrument.Snapshot()), []);
        var row = AddTrack(true, saved, instrument);
        if (row == null) instrument.Dispose();
        else row.OpenMidi();
    }
}
