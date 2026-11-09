using System.Windows;
using System.Windows.Threading;
using NAudio;
using NAudio.Midi;

namespace EazyLoop;

public partial class MainWindow
{
    private sealed record MidiDevice(int Index, string Name);
    private MidiIn? midiInput;
    private MidiDevice? midiDevice;
    private TrackRow? midiTarget;
    private readonly HashSet<int> midiPressed = [];

    private void RefreshMidiInputs(object sender, RoutedEventArgs e) =>
        PopulateMidiInputs(MidiInputList.SelectedItem as MidiDevice);

    private void PopulateMidiInputs(MidiDevice? draft)
    {
        var disabled = new MidiDevice(-1, Locale.T("Без MIDI-входа"));
        var devices = new List<MidiDevice> { disabled };
        try
        {
            for (int i = 0, count = MidiIn.NumberOfDevices; i < count; i++)
                devices.Add(new MidiDevice(i, MidiIn.DeviceInfo(i).ProductName));
            if (midiDevice != null && !devices.Contains(midiDevice)) SetMidiInput(null);
            MidiInputHint.Text = Locale.T("MIDI-клавиатура играет на дорожке, редактор которой вы открыли последним.");
        }
        catch (Exception ex)
        {
            if (midiDevice != null && !devices.Contains(midiDevice)) devices.Add(midiDevice);
            MidiInputHint.Text = ex.Message;
        }
        MidiInputList.ItemsSource = devices;
        MidiInputList.SelectedItem = draft != null && devices.Contains(draft) ? draft : midiDevice ?? disabled;
    }

    private bool ApplyMidiInput()
    {
        var choice = MidiInputList.SelectedItem as MidiDevice;
        var nextDevice = choice is { Index: >= 0 } ? choice : null;
        if (nextDevice == midiDevice) return true;
        MidiIn? next = null;
        try
        {
            if (nextDevice != null)
            {
                if (MidiIn.DeviceInfo(nextDevice.Index).ProductName != nextDevice.Name)
                    throw new InvalidOperationException(Locale.T("Обновите список MIDI-устройств и выберите клавиатуру заново."));
                next = new MidiIn(nextDevice.Index);
                next.MessageReceived += MidiMessageReceived;
                next.Start();
            }
        }
        catch (Exception ex)
        {
            CloseMidiInput(next);
            MidiInputHint.Text = Locale.F($"Не удалось открыть MIDI-вход. {ex.Message}");
            return false;
        }
        SetMidiInput(next);
        midiDevice = nextDevice;
        return true;
    }

    private void SetMidiInput(MidiIn? next)
    {
        var previous = midiInput;
        midiInput = next;
        midiDevice = null;
        ReleaseMidiKeys();
        midiTarget?.SetMidiInputTarget(next != null);
        CloseMidiInput(previous);
    }

    private void CloseMidiInput(MidiIn? input)
    {
        if (input == null) return;
        input.MessageReceived -= MidiMessageReceived;
        try { input.Dispose(); }
        catch (MmException ex) when (ex.Result == MmResult.InvalidHandle) { }
    }

    private void MidiMessageReceived(object? sender, MidiInMessageEventArgs e)
    {
        int raw = e.RawMessage;
        int command = raw & 0xf0;
        int key = (raw >> 8) & 0x7f;
        bool panic = command == 0xb0 && key is 120 or 123;
        if (command is not (0x80 or 0x90) && !panic) return;
        int channel = raw & 0xf;
        int velocity = command == 0x90 ? (raw >> 16) & 0x7f : 0;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!ReferenceEquals(sender, midiInput) || midiTarget?.Data.Midi is not { CanPlay: true } track) return;
            if (panic)
            {
                ReleaseMidiKeys(channel);
                return;
            }
            int id = channel * 128 + key;
            if (velocity == 0 ? midiPressed.Remove(id) : midiPressed.Add(id))
                track.PlayLiveNote(key, velocity);
        }));
    }

    private void SelectMidiTarget(TrackRow? row)
    {
        if (row == midiTarget) return;
        ReleaseMidiKeys();
        midiTarget?.SetMidiInputTarget(false);
        midiTarget = row;
        row?.SetMidiInputTarget(midiInput != null);
    }

    private void ReleaseMidiKeys(int? channel = null)
    {
        foreach (int id in midiPressed.ToArray())
        {
            if (channel.HasValue && id / 128 != channel.Value) continue;
            midiTarget?.Data.Midi?.PlayLiveNote(id % 128, 0);
            midiPressed.Remove(id);
        }
    }
}
