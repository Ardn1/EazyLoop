using System.Text.Json.Serialization;
using NAudio.Vst3;

namespace EazyLoop;

internal enum WaveShape { Sine, Triangle, Saw, Square }
internal enum MidiLane { Velocity = -1, Modulation = 1, Volume = 7, Pan = 10, Expression = 11, Sustain = 64, Pitch = 128 }

internal sealed record SynthPatch(WaveShape Wave, float Attack, float Decay, float Sustain, float Release, float Tone,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float Blend = 0)
{
    public static readonly SynthPatch Default = new(WaveShape.Triangle, 8, 120, 70, 180, 75);
    public static ReadOnlySpan<WaveShape> Waves => [WaveShape.Sine, WaveShape.Triangle, WaveShape.Square, WaveShape.Saw];
    [JsonIgnore] public float WavePosition => (Waves.IndexOf(Wave) + Blend) * 100;
    [JsonIgnore] public WaveShape NextWave => Waves[Math.Min(Waves.IndexOf(Wave) + 1, Waves.Length - 1)];

    public SynthPatch WithWavePosition(float position)
    {
        float scaled = Math.Clamp(position, 0, 300) / 100;
        int index = (int)scaled;
        return this with { Wave = Waves[index], Blend = scaled - index };
    }

    public bool IsValid() => Enum.IsDefined(Wave) && Attack is >= 1 and <= 1500 && Decay is >= 5 and <= 2000 &&
        Sustain is >= 0 and <= 100 && Release is >= 5 and <= 3000 && Tone is >= 0 and <= 100 &&
        Blend is >= 0 and < 1 && (Blend == 0 || Wave != WaveShape.Saw);
}

internal sealed record MidiNote(Guid Id, int Pitch, int Start, int Length, int Velocity);
internal sealed record MidiPoint(MidiLane Lane, int Tick, int Value);
internal sealed record GlissandoSettings(SynthPatch Voice, GlissandoCurve? Curve = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GlissandoTimeline? Timeline = null)
{
    public static readonly GlissandoSettings Default = new(SynthPatch.Default, Timeline: GlissandoTimeline.Empty);
    public bool IsValid() => Voice?.IsValid() == true &&
        (Timeline != null ? Curve == null && Timeline.IsValid() : Curve?.IsValid() == true);
}

internal sealed record MidiTrackSettings(SynthPatch? Synth, VstReference? Plugin, MidiNote[] Notes, MidiPoint[] Points,
    DrumKit? Drums = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    GlissandoSettings? Glissando = null)
{
    public const int Ppq = 960;
    public const int MaxTicks = 7 * 16 * Ppq;
    public const int MaxNotes = 4096;
    public const int MaxPoints = 4096;
    public static int Ticks(LoopGrid grid) => grid.Beats * grid.Bars * Ppq;
    public static int Maximum(MidiLane lane) => lane == MidiLane.Pitch ? 16383 : 127;
    public static int Default(MidiLane lane) => lane switch
    {
        MidiLane.Velocity => 96, MidiLane.Volume or MidiLane.Expression => 127,
        MidiLane.Pan => 64, MidiLane.Pitch => 8192, _ => 0
    };

    public bool IsValid()
    {
        int instruments = (Synth != null ? 1 : 0) + (Plugin != null ? 1 : 0) + (Drums != null ? 1 : 0) +
            (Glissando != null ? 1 : 0);
        if (instruments != 1 || (Synth != null && !Synth.IsValid()) ||
            (Plugin != null && !FxCatalog.IsValidPlugin(Plugin)) ||
            (Drums != null && !Drums.IsValid()) ||
            (Glissando != null && !Glissando.IsValid()) ||
            Notes == null || Notes.Length > MaxNotes || (Glissando?.Timeline != null && Notes.Length != 0) ||
            Points == null || Points.Length > MaxPoints) return false;
        var ids = new HashSet<Guid>();
        var hits = new HashSet<(int, int)>();
        foreach (var n in Notes)
            if (n == null || n.Id == Guid.Empty || !ids.Add(n.Id) || n.Pitch is < 0 or > 127 ||
                n.Start < 0 || n.Start >= MaxTicks || n.Length <= 0 || n.Length > MaxTicks - n.Start ||
                n.Velocity is < 1 or > 127 || (Drums != null && (!DrumKit.Keys.Contains(n.Pitch) ||
                    !hits.Add((n.Pitch, n.Start))))) return false;
        var locations = new HashSet<(MidiLane, int)>();
        foreach (var p in Points)
            if (p == null || !Enum.IsDefined(p.Lane) || p.Lane == MidiLane.Velocity ||
                p.Tick < 0 || p.Tick >= MaxTicks || p.Value < 0 || p.Value > Maximum(p.Lane) ||
                !locations.Add((p.Lane, p.Tick))) return false;
        return true;
    }
}

internal sealed class MidiTrack : IDisposable
{
    private enum Message { Control, Off, On }
    private readonly record struct Event(int Tick, Message Kind, int Key, int Value, Guid NoteId = default, int EndTick = 0,
        PitchContour? Contour = null);
    private sealed record Sequence(MidiNote[] Notes, MidiPoint[] Points, Event[] Events, GlissandoTimeline? Timeline);
    private sealed record PreviewRequest(int[] Notes, int Velocity);
    private sealed record SynthPreviewNote(int Key, int Velocity, bool FullEnvelope = false);

    private readonly object nativeGate = new();
    private readonly SimpleSynth? synth;
    private readonly SimpleSynth? previewSynth;
    private readonly SimpleDrums? drums;
    private Vst3Module? module;
    private Vst3Plugin? plugin;
    private float[] silence = [];
    private VstReference? reference;
    private Sequence sequence;
    private Sequence? current;
    private int eventIndex;
    private int expectedPhase = -1;
    private long epoch = -1;
    private bool wasPlaying;
    private readonly int[] held = new int[128];
    private readonly int[] auditions = new int[12];
    private int auditionCount;
    private PreviewRequest? pendingPreview;
    private int previewRemaining;
    private int auditionOff;
    private SynthPreviewNote? pendingSynthNote;
    private int synthPreviewKey, synthPreviewHold;
    private float volume = 1, expression = 1, pan;
    private string? error;

    public MidiTrack(MidiTrackSettings settings)
    {
        if ((settings.Synth ?? settings.Glissando?.Voice) is { } patch)
        {
            synth = new SimpleSynth(patch, settings.Glissando?.Curve);
            previewSynth = new SimpleSynth(patch, settings.Glissando?.Curve);
        }
        if (settings.Drums != null) drums = new SimpleDrums(settings.Drums);
        reference = settings.Plugin;
        sequence = Compile(settings.Notes, settings.Points, settings.Glissando?.Timeline);
        SequenceHistory = new(() => (Notes, Points), value => ChangeSequence(value.Notes, value.Points),
            (a, b) => a.Notes.AsSpan().SequenceEqual(b.Notes) && a.Points.AsSpan().SequenceEqual(b.Points));
        if (Timeline != null)
            TimelineHistory = new(() => Timeline!, ChangeTimeline, (a, b) => ReferenceEquals(a, b));
    }

    internal EditHistory<(MidiNote[] Notes, MidiPoint[] Points)> SequenceHistory { get; }
    internal EditHistory<GlissandoTimeline>? TimelineHistory { get; }
    public Vst3Plugin? Plugin => Volatile.Read(ref plugin);
    public string? Error => Volatile.Read(ref error);
    public SynthPatch? Patch => synth?.Patch;
    public GlissandoCurve? Curve => synth?.Curve;
    public GlissandoTimeline? Timeline => Volatile.Read(ref sequence).Timeline;
    public DrumKit? Kit => drums?.Kit;
    public VstReference? Reference => reference;
    public MidiNote[] Notes => Volatile.Read(ref sequence).Notes;
    public MidiPoint[] Points => Volatile.Read(ref sequence).Points;
    public bool HasPreview => Volatile.Read(ref pendingPreview) != null || Volatile.Read(ref previewRemaining) > 0 ||
        Volatile.Read(ref pendingSynthNote) != null || previewSynth?.HasVoices == true;
    public bool CanPlay => synth != null || drums != null || (Plugin != null && Error == null);

    public MidiTrackSettings Snapshot()
    {
        var value = Volatile.Read(ref sequence);
        var curve = Curve;
        var patch = Patch;
        var glissando = curve == null && value.Timeline == null ? null : new GlissandoSettings(patch!, curve, value.Timeline);
        return new(glissando == null ? patch : null, reference, value.Notes, value.Points, Kit, glissando);
    }

    public void ChangePatch(SynthPatch patch) { if (synth != null) synth.Patch = patch; }
    public void ChangeCurve(GlissandoCurve curve) { if (synth?.Curve != null) synth.Curve = curve; }
    public void ChangeKit(DrumKit kit) { if (drums != null) drums.Kit = kit; }
    public void ChangeSequence(MidiNote[] notes, MidiPoint[] points) => Volatile.Write(ref sequence, Compile(notes, points, Timeline));
    public void ChangeTimeline(GlissandoTimeline timeline)
    {
        var value = Volatile.Read(ref sequence);
        Volatile.Write(ref sequence, Compile(value.Notes, value.Points, timeline));
    }

    private static Sequence Compile(MidiNote[] notes, MidiPoint[] points, GlissandoTimeline? timeline)
    {
        int phrases = timeline?.Contours.Count(contour => contour.Start < contour.End) ?? 0;
        var events = new Event[(notes.Length + phrases) * 2 + points.Length];
        int index = 0;
        foreach (var note in notes)
        {
            int end = note.Start + note.Length;
            events[index++] = new Event(note.Start, Message.On, note.Pitch, note.Velocity, note.Id, end);
            events[index++] = new Event(end, Message.Off, note.Pitch, 0, note.Id);
        }
        if (timeline != null)
            foreach (var contour in timeline.Contours)
            {
                if (contour.Start == contour.End) continue;
                events[index++] = PhraseOn(contour);
                events[index++] = new Event(contour.End, Message.Off, 69, 0, Contour: contour);
            }
        foreach (var point in points) events[index++] = new Event(point.Tick, Message.Control, (int)point.Lane, point.Value);
        Array.Sort(events, static (a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Kind.CompareTo(b.Kind));
        return new Sequence(notes, points, events, timeline);
    }

    private static Event PhraseOn(PitchContour contour) =>
        new(contour.Start, Message.On, 69, 100, EndTick: contour.End, Contour: contour);

    public void Connect(Vst3Module source, Vst3ClassInfo type, string path)
    {
        var loaded = source.CreatePlugin(type, LoopEngine.Rate, FxRack.BlockFrames);
        try
        {
            if (!loaded.IsInstrument || loaded.OutputChannelCount is not (1 or 2))
                throw new NotSupportedException(Locale.T("Выберите VST3-инструмент с моно- или стереовыходом."));
            if (reference?.State is { Length: > 0 } state) loaded.LoadState(state);
            var input = new float[loaded.InputChannelCount * FxRack.BlockFrames];
            lock (nativeGate)
            {
                silence = input;
                module = source;
                reference = new VstReference(path, type.ClassId, type.Name, reference?.State);
                expectedPhase = -1;
                Volatile.Write(ref error, null);
                Volatile.Write(ref plugin, loaded);
            }
        }
        catch { loaded.Dispose(); throw; }
    }

    public bool SavePluginState()
    {
        lock (nativeGate)
        {
            if (plugin == null || reference == null) return false;
            byte[] state = plugin.SaveState();
            if (state.Length > FxCatalog.MaxStateBytes) throw new InvalidOperationException(Locale.T("Состояние инструмента превышает 1 МБ."));
            if (reference.State != null && state.AsSpan().SequenceEqual(reference.State)) return false;
            reference = reference with { State = state };
            return true;
        }
    }

    public void Preview(int[] notes, int velocity = 96)
    {
        if (CanPlay) Volatile.Write(ref pendingPreview, new PreviewRequest(notes, velocity));
    }

    public void PreviewNote(int key, int velocity)
    {
        if (previewSynth != null) Volatile.Write(ref pendingSynthNote, new SynthPreviewNote(key, velocity));
        else Preview([key], velocity);
    }

    public void PreviewEnvelope()
    {
        if (previewSynth != null) Volatile.Write(ref pendingSynthNote, new SynthPreviewNote(69, 100, FullEnvelope: true));
    }

    public void Render(Span<float> output, LoopGrid grid, int phase, long currentEpoch, bool playing)
    {
        output.Clear();
        if (!CanPlay || !Monitor.TryEnter(nativeGate)) return;
        try
        {
            if (synth == null && drums == null && plugin == null) return;
            if (playing) RenderSequence(output, grid, phase, currentEpoch);
            else RenderPreview(output, grid);
            MixSynthPreview(output);
            wasPlaying = playing;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref error, ex.Message);
            Volatile.Write(ref previewRemaining, 0);
            output.Clear();
        }
        finally { Monitor.Exit(nativeGate); }
    }

    private void RenderSequence(Span<float> output, LoopGrid grid, int phase, long currentEpoch)
    {
        var value = Volatile.Read(ref sequence);
        int written = 0, remaining = output.Length / 2, loop = grid.Frames;
        if (!wasPlaying || epoch != currentEpoch || expectedPhase != phase || current != value)
        {
            if (!wasPlaying || epoch != currentEpoch) drums?.Reset();
            current = value;
            epoch = currentEpoch;
            Seek(value, grid, phase);
        }
        var preview = Interlocked.Exchange(ref pendingPreview, null);
        if (drums != null && preview != null)
            foreach (int key in preview.Notes) drums.Hit(key, preview.Velocity);
        Volatile.Write(ref previewRemaining, 0);

        while (remaining > 0)
        {
            if (phase == loop)
            {
                phase = 0;
                Seek(value, grid, 0);
            }
            int boundary = loop;
            while (eventIndex < value.Events.Length)
            {
                Event e = value.Events[eventIndex];
                int frame = Frame(e.Tick, grid);
                if (frame > phase)
                {
                    boundary = Math.Min(loop, frame);
                    break;
                }
                Apply(e, grid, phase);
                eventIndex++;
            }
            int frames = Math.Min(remaining, boundary - phase);
            Process(output.Slice(written * 2, frames * 2), grid, phase, true);
            phase += frames;
            remaining -= frames;
            written += frames;
        }
        expectedPhase = phase == loop ? -1 : phase;
    }

    private void Seek(Sequence value, LoopGrid grid, int phase)
    {
        Reset();
        eventIndex = 0;
        while (eventIndex < value.Events.Length && Frame(value.Events[eventIndex].Tick, grid) < phase)
        {
            Event e = value.Events[eventIndex++];
            if (e.Kind == Message.Control) Apply(e, grid, phase);
        }
        if (phase == 0 || drums != null) return;
        if (value.Timeline != null)
            foreach (var contour in value.Timeline.Contours)
            {
                if (Frame(contour.Start, grid) >= phase) break;
                if (Frame(contour.End, grid) <= phase) continue;
                Apply(PhraseOn(contour), grid, phase);
                break;
            }
        foreach (var note in value.Notes)
        {
            int end = note.Start + note.Length;
            int endFrame = Frame(end, grid);
            if (Frame(note.Start, grid) < phase && (endFrame > phase || (endFrame == phase && Curve == null)))
                Apply(new Event(note.Start, Message.On, note.Pitch, note.Velocity, note.Id, end), grid, phase);
        }
    }

    private static int Frame(int tick, LoopGrid grid) =>
        tick >= MidiTrackSettings.Ticks(grid) ? grid.Frames :
            Math.Min(grid.Frames - 1, NoteFrame(tick, grid));

    private static int NoteFrame(int tick, LoopGrid grid) =>
        (int)Math.Round(tick * grid.BeatFrames / MidiTrackSettings.Ppq);

    private void RenderPreview(Span<float> output, LoopGrid grid)
    {
        if (Interlocked.Exchange(ref pendingPreview, null) is { } preview)
        {
            if (wasPlaying) drums?.Reset();
            Reset();
            int duration = Curve == null ? LoopEngine.Rate / 3 : LoopEngine.Rate;
            auditionCount = Math.Min(preview.Notes.Length, auditions.Length);
            for (int i = 0; i < auditionCount; i++)
            {
                auditions[i] = preview.Notes[i];
                Apply(new Event(0, Message.On, auditions[i], preview.Velocity), grid, 0, duration);
            }
            int frames = Curve != null ? duration + (int)(Patch!.Release * LoopEngine.Rate / 1000) + 1 :
                drums == null ? LoopEngine.Rate * 3 / 2 : LoopEngine.Rate * 2;
            Volatile.Write(ref previewRemaining, frames);
            auditionOff = duration;
        }

        int offset = 0, remaining = Math.Min(output.Length / 2, previewRemaining);
        while (remaining > 0)
        {
            int frames = auditionOff > 0 ? Math.Min(auditionOff, remaining) : remaining;
            Process(output.Slice(offset * 2, frames * 2), grid, 0, false);
            offset += frames;
            remaining -= frames;
            Volatile.Write(ref previewRemaining, previewRemaining - frames);
            if (auditionOff > 0 && (auditionOff -= frames) == 0)
                for (int i = 0; i < auditionCount; i++) Apply(new Event(0, Message.Off, auditions[i], 0), grid, 0);
        }
    }

    private void MixSynthPreview(Span<float> output)
    {
        if (synth == null || previewSynth == null) return;
        var note = Interlocked.Exchange(ref pendingSynthNote, null);
        if (note == null && !previewSynth.HasVoices) return;

        var patch = previewSynth.Patch = synth.Patch;
        previewSynth.Curve = synth.Curve;
        if (note != null)
        {
            previewSynth.Reset();
            synthPreviewHold = 0;
            if (note.Velocity > 0)
            {
                synthPreviewKey = note.Key;
                synthPreviewHold = note.FullEnvelope
                    ? (int)Math.Ceiling((patch.Attack + patch.Decay + 260) * LoopEngine.Rate / 1000)
                    : Math.Max(previewSynth.Curve == null ? LoopEngine.Rate / 3 : LoopEngine.Rate,
                        (int)((patch.Attack + 100) * LoopEngine.Rate / 1000));
                previewSynth.On(synthPreviewKey, note.Velocity, synthPreviewHold);
            }
        }

        if (synthPreviewHold > 0)
        {
            int frames = Math.Min(synthPreviewHold, output.Length / 2);
            previewSynth.MixInto(output[..(frames * 2)]);
            if ((synthPreviewHold -= frames) == 0) previewSynth.Off(synthPreviewKey);
            output = output[(frames * 2)..];
        }
        if (!output.IsEmpty) previewSynth.MixInto(output);
    }

    private void Reset()
    {
        Array.Clear(held);
        synth?.Reset();
        plugin?.ResetEventSchedule();
        plugin?.AllNotesOff();
        volume = expression = 1;
        pan = 0;
        Control(MidiLane.Sustain, 0);
        Control(MidiLane.Modulation, 0);
        Control(MidiLane.Pitch, 8192);
    }

    private void Apply(Event e, LoopGrid grid, int phase, int previewDuration = 1)
    {
        if (e.Kind == Message.Control) { Control((MidiLane)e.Key, e.Value); return; }
        if (drums != null)
        {
            if (e.Kind == Message.On) drums.Hit(e.Key, e.Value);
            return;
        }
        if (synth != null && (e.Contour != null || (synth.Curve != null && e.NoteId != Guid.Empty)))
        {
            if (e.Kind == Message.On)
            {
                int start = Frame(e.Tick, grid);
                int end = e.EndTick > MidiTrackSettings.Ticks(grid) ? NoteFrame(e.EndTick, grid) : Frame(e.EndTick, grid);
                synth.On(e.Key, e.Value, end - start, phase - start, e.NoteId, e.Contour);
            }
            else synth.Off(e.Key, e.NoteId);
            return;
        }
        if (e.Kind == Message.On)
        {
            if (held[e.Key]++ > 0) return;
            synth?.On(e.Key, e.Value, previewDuration);
            plugin?.EnqueueNoteOn(e.Key, e.Value / 127f);
        }
        else if (held[e.Key] > 0 && --held[e.Key] == 0)
        {
            synth?.Off(e.Key);
            plugin?.EnqueueNoteOff(e.Key);
        }
    }

    private void Control(MidiLane lane, int value)
    {
        switch (lane)
        {
            case MidiLane.Volume: volume = value / 127f; break;
            case MidiLane.Expression: expression = value / 127f; break;
            case MidiLane.Pan: pan = Math.Clamp((value - 64) / 63f, -1, 1); break;
            case MidiLane.Pitch:
                synth?.Control(lane, value);
                plugin?.EnqueuePitchBend(value / 16383.0);
                break;
            default:
                synth?.Control(lane, value);
                plugin?.EnqueueControlChange((int)lane, value / 127.0);
                break;
        }
    }

    private void Process(Span<float> output, LoopGrid grid, int phase, bool playing)
    {
        int frames = output.Length / 2;
        if (synth != null) synth.MixInto(output);
        else if (drums != null) drums.Render(output);
        else if (plugin is { } instrument)
        {
            double quarters = phase / grid.BeatFrames;
            var context = new Vst3MusicalContext(playing, phase, quarters, Math.Floor(quarters / grid.Beats) * grid.Beats,
                grid.Bpm, grid.Beats, 4);
            instrument.SetMusicalContext(context);
            int channels = instrument.OutputChannelCount;
            instrument.Process(silence.AsSpan(0, frames * instrument.InputChannelCount), output[..(frames * channels)], frames);
            if (channels == 1)
                for (int i = frames - 1; i >= 0; i--) output[2 * i] = output[2 * i + 1] = output[i];
        }

        float level = volume * expression, left = level * (1 - Math.Max(0, pan)), right = level * (1 + Math.Min(0, pan));
        for (int i = 0; i < output.Length; i += 2)
        {
            output[i] = float.IsFinite(output[i]) ? output[i] * left : 0;
            output[i + 1] = float.IsFinite(output[i + 1]) ? output[i + 1] * right : 0;
        }
    }

    public void Dispose()
    {
        lock (nativeGate)
        {
            var loaded = plugin;
            Volatile.Write(ref plugin, null);
            try { loaded?.Dispose(); }
            finally { module?.Dispose(); module = null; }
        }
    }
}
