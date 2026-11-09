using System.Collections.Concurrent;

namespace EazyLoop;

internal sealed partial class MidiTrack
{
    private readonly ConcurrentQueue<(int Key, int Velocity)> liveNotes = new();
    private readonly int[] liveHeld = new int[128];
    private readonly SimpleSynth? liveSynth;
    private int pluginLiveKeys;
    private int liveTail;

    internal bool HasLiveNotes => !liveNotes.IsEmpty || liveTail > 0 || pluginLiveKeys > 0 || liveSynth?.HasVoices == true;

    internal void PlayLiveNote(int key, int velocity)
    {
        if (CanPlay) liveNotes.Enqueue((key, velocity));
    }

    private void ApplyLiveNotes()
    {
        if (liveSynth != null)
        {
            liveSynth.Patch = synth!.Patch;
            liveSynth.Curve = synth.Curve;
        }
        if (liveNotes.IsEmpty) return;

        Span<bool> started = stackalloc bool[128];
        started.Clear();
        while (liveNotes.TryPeek(out var note))
        {
            // Give even a tap received within a single block time to sound.
            if (note.Velocity == 0 && started[note.Key]) break;
            liveNotes.TryDequeue(out _);
            if (drums != null)
            {
                if (note.Velocity == 0) continue;
                drums.Hit(note.Key, note.Velocity);
                liveTail = LoopEngine.Rate * 2;
                continue;
            }

            int key = note.Key;
            if (note.Velocity > 0)
            {
                if (liveHeld[key]++ != 0) continue;
                started[key] = true;
                if (liveSynth != null) liveSynth.On(key, note.Velocity, LoopEngine.Rate);
                else
                {
                    pluginLiveKeys++;
                    if (held[key] == 0) plugin?.EnqueueNoteOn(key, note.Velocity / 127f);
                }
            }
            else if (liveHeld[key] > 0 && --liveHeld[key] == 0)
            {
                if (liveSynth != null) liveSynth.Off(key);
                else
                {
                    pluginLiveKeys--;
                    if (held[key] == 0) plugin?.EnqueueNoteOff(key);
                    liveTail = LoopEngine.Rate * 3 / 2;
                }
            }
        }
    }
}
