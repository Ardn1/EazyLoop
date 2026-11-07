using System.Text.Json.Serialization;

namespace EazyLoop;

internal sealed class Track
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public float[] Samples = [];
    public int Length;
    public EditedAudio? Audio;
    public float[] PlaybackSamples => Audio?.Samples ?? Samples;
    public int PlaybackLength => Audio?.Samples.Length ?? Length;
    public float Volume = 0.85f;
    public float Pan;
    public int Shift;
    public bool Muted;
    public bool Solo;
    public FxRack Fx { get; init; } = new();
    public MidiTrack? Midi { get; init; }
}

internal readonly record struct LoopGrid(int Bpm, int Beats, int Bars)
{
    [JsonIgnore]
    public double BeatFrames => 60.0 * LoopEngine.Rate / Bpm;
    [JsonIgnore]
    public int BarFrames => (int)Math.Round(BeatFrames * Beats);
    [JsonIgnore]
    public int Frames => (int)Math.Round(BeatFrames * Beats * Bars);
}

internal enum Transport { Stopped, Playing, CountIn, Recording, Finishing }

internal readonly record struct TransportView(Transport State, int Phase, int Beat, int LoopFrames, float Peak, bool Playing);

internal sealed class LoopEngine
{
    public const int Rate = 48000;
    public const int MaxTracks = 16;
    private const int FadeFrames = 96;

    private sealed class Take(Track track, float[] samples, long start, long writeOrigin, long scheduledEnd)
    {
        public Track Track { get; } = track;
        public float[] Samples { get; } = samples;
        public long Start { get; } = start;
        public long WriteOrigin { get; } = writeOrigin;
        public long End => ManualEnd ?? scheduledEnd;
        public long? ManualEnd;
        public long? InputAnchor;
        public long OutputAnchor;
        public long CapturedThrough = start;
        public int Length;
    }

    private readonly object gate = new();
    private readonly record struct RenderPart(float[] Samples, int Length, float Volume, float Pan, int Shift,
        int First, int Phase, FxRack Fx, MidiTrack? Midi);
    private readonly RenderPart[] mixParts = new RenderPart[MaxTracks];
    private readonly float[] fxBuffer = new float[FxRack.BlockFrames * 2];
    private readonly List<Track> tracks = [];
    private readonly Queue<Track> changed = [];
    private readonly float[] accent = Click(1720, 0.38f);
    private readonly float[] tick = Click(1120, 0.26f);
    private LoopGrid grid = new(100, 4, 2);
    private long rendered;
    private long origin;
    private long playbackStart;
    private int cuePhase;
    private long countInStart;
    private long renderEpoch;
    private bool playing;
    private bool metronome;
    private bool countIn;
    private float clickVolume = 1;
    private Take? take;
    private float peak;
    private float heldPeak;

    public void Configure(LoopGrid value)
    {
        lock (gate)
        {
            cuePhase = Math.Clamp((int)Math.Round(cuePhase * value.BeatFrames / grid.BeatFrames), 0, value.Frames - 1);
            grid = value;
        }
    }
    public void Metronome(bool enabled, bool precount, float volume)
    {
        lock (gate)
        {
            metronome = enabled;
            countIn = precount;
            clickVolume = volume;
        }
    }

    public Track Add(Track? data = null)
    {
        lock (gate)
        {
            if (tracks.Count >= MaxTracks) throw new InvalidOperationException(Locale.T("Достигнут лимит дорожек."));
            var track = data ?? new Track();
            tracks.Add(track);
            return track;
        }
    }

    public void Remove(Track track)
    {
        lock (gate) { if (take?.Track != track) tracks.Remove(track); }
    }

    public TrackSnapshot Snapshot(Track track, int number, double zoom, string? name, TrackInput? input, bool collapsed)
    {
        lock (gate)
        {
            return new TrackSnapshot(new TrackSettings(track.Id, number, track.Length, track.Volume,
                track.Pan, track.Shift, track.Muted, zoom, name, input, track.Fx.Snapshot(), collapsed,
                track.Midi?.Snapshot(), track.Solo, track.Audio?.Edit), track.Samples);
        }
    }

    public void ReplaceAudio(Track track, float[] samples, int length, AudioEdit? edit)
    {
        var edited = EditedAudio.Build(edit, samples, length);
        lock (gate)
        {
            track.Samples = samples;
            track.Length = length;
            track.Audio = edited;
        }
    }

    public void Mix(Track track, float volume, float pan, int shift, bool mute, bool solo)
    {
        lock (gate)
        {
            track.Volume = volume;
            track.Pan = pan;
            track.Shift = shift;
            track.Muted = mute;
            track.Solo = solo;
        }
    }

    public void Play()
    {
        lock (gate)
        {
            if (playing) return;
            StartAt(rendered, rendered);
        }
    }

    public void Seek(int phase, long frame)
    {
        lock (gate)
        {
            cuePhase = Math.Clamp(phase, 0, grid.Frames - 1);
            if (!playing) return;
            EndRecord(frame);
            // Switch at the next unqueued frame. The finishing take keeps its original write origin.
            StartAt(rendered, rendered);
        }
    }

    private void StartAt(long frame, long clickStart)
    {
        playbackStart = frame;
        origin = frame - cuePhase;
        cuePhase = 0;
        countInStart = clickStart;
        renderEpoch++;
        playing = true;
    }

    public void Record(Track track, long frame, int correctionFrames)
    {
        int length;
        lock (gate) length = grid.Frames;
        var data = new float[length];

        lock (gate)
        {
            if (take != null || track.Midi != null || !tracks.Contains(track)) return;
            bool overdub = playing;
            long start = Math.Max(frame, playbackStart);
            if (!playing)
            {
                long clickStart = countIn ? rendered : Math.Max(rendered, frame);
                start = countIn ? clickStart + grid.BarFrames : frame;
                StartAt(start, clickStart);
            }
            int phase = Mod(start - origin, length);
            long end = start + length - phase;
            // A mid-loop overdub starts immediately and gets one full pass after the first wrap.
            if (overdub && phase != 0) end += length;
            take = new Take(track, data, start, origin + correctionFrames, end);
        }
    }

    public bool EndRecord(long frame)
    {
        lock (gate)
        {
            if (take is not { ManualEnd: null } active) return false;
            if (frame <= active.Start && active.Length == 0)
            {
                if (active.Start == playbackStart && frame < playbackStart) playing = false;
                take = null;
                return true;
            }
            long end = Math.Min(frame, active.End);
            if (active.CapturedThrough >= end) CompleteTake();
            else active.ManualEnd = end;
            return false;
        }
    }

    public void Stop(long frame)
    {
        lock (gate)
        {
            EndRecord(frame);
            playing = false;
            cuePhase = 0;
        }
    }

    public void AbortAudio()
    {
        lock (gate)
        {
            CompleteTake();
            playing = false;
            cuePhase = 0;
            peak = heldPeak = 0;
        }
    }

    public void RestartClock()
    {
        lock (gate)
        {
            rendered = origin = playbackStart = countInStart = 0;
            cuePhase = 0;
            peak = heldPeak = 0;
        }
    }

    public Track? Changed()
    {
        lock (gate) return changed.TryDequeue(out Track? track) ? track : null;
    }

    public TransportView View(long frame)
    {
        lock (gate)
        {
            int phase = playing ? Mod(Math.Max(frame, playbackStart) - origin, grid.Frames) : cuePhase;
            int beat = Math.Min(grid.Beats * grid.Bars - 1, (int)(phase / grid.BeatFrames));
            var state = playing ? Transport.Playing : Transport.Stopped;
            if (take is { } pending && frame - pending.End >= Rate / 2) CompleteTake();
            if (take is { } active)
            {
                if (active.ManualEnd.HasValue || frame >= active.End) state = Transport.Finishing;
                else if (frame < active.Start && active.Start == playbackStart && countInStart < playbackStart)
                {
                    state = Transport.CountIn;
                    beat = Math.Clamp((int)((frame - countInStart) / grid.BeatFrames), 0, grid.Beats - 1);
                }
                else state = Transport.Recording;
            }
            heldPeak = Math.Max(peak, heldPeak * 0.82f);
            peak = 0;
            return new TransportView(state, phase, beat, grid.Frames, heldPeak, playing);
        }
    }

    public void Capture(ReadOnlySpan<float> input, long outputFrame, long inputFrame)
    {
        float inputPeak = 0;
        foreach (float sample in input) inputPeak = Math.Max(inputPeak, Math.Abs(sample));
        lock (gate)
        {
            peak = Math.Max(peak, inputPeak);
            if (take is not { } active) return;

            if (active.InputAnchor is not long inputAnchor)
            {
                if (outputFrame + input.Length <= active.Start) return;
                active.InputAnchor = inputAnchor = inputFrame;
                active.OutputAnchor = outputFrame;
            }

            // Keep each take contiguous on the capture device's sample clock.
            long start = active.OutputAnchor + inputFrame - inputAnchor;
            long first = Math.Max(start, active.Start);
            long last = Math.Min(start + input.Length, active.End);
            if (last > first)
            {
                int loop = active.Samples.Length;
                first = Math.Max(first, last - loop);
                int destination = Mod(first - active.WriteOrigin, loop);
                var source = input.Slice((int)(first - start), (int)(last - first));
                int untilWrap = Math.Min(source.Length, loop - destination);
                source[..untilWrap].CopyTo(active.Samples.AsSpan(destination));
                source[untilWrap..].CopyTo(active.Samples);
                active.Length = Math.Max(active.Length, destination + untilWrap);
                active.CapturedThrough = Math.Max(active.CapturedThrough, last);
            }
            if (start + input.Length >= active.End) CompleteTake();
        }
    }

    private void CompleteTake()
    {
        if (take is not { } active) return;
        if (active.Length > 0)
        {
            active.Track.Samples = active.Samples;
            active.Track.Length = active.Length;
            active.Track.Audio = null;
            changed.Enqueue(active.Track);
        }
        take = null;
    }

    public void Render(Span<float> stereo)
    {
        stereo.Clear();
        int frames = stereo.Length / 2;
        long start, loopStart, audioStart, clickStart, epoch;
        LoopGrid current;
        bool clickEnabled, running;
        float clickGain;
        int partCount = 0;
        lock (gate)
        {
            start = rendered;
            rendered += frames;
            running = playing;
            current = grid;
            loopStart = origin;
            audioStart = playbackStart;
            clickStart = countInStart;
            epoch = renderEpoch;
            clickEnabled = metronome;
            clickGain = running ? clickVolume : 0;
            int first = running ? (int)Math.Clamp(audioStart - start, 0, frames) : 0;
            int phase = Mod(start + first - loopStart, current.Frames);

            if (first < frames)
            {
                bool soloActive = tracks.Any(static track => track.Solo);
                foreach (var track in tracks)
                {
                    if (track.Muted || track.Volume == 0 || (soloActive && !track.Solo)) continue;
                    if (track.Midi is { } midi)
                    {
                        if (midi.CanPlay && (running || midi.HasPreview))
                            mixParts[partCount++] = new([], 0, track.Volume, track.Pan, 0, first, phase, track.Fx, midi);
                        continue;
                    }
                    if (running && track.Length > 0)
                        mixParts[partCount++] = new(track.PlaybackSamples, Math.Min(track.PlaybackLength, current.Frames),
                            track.Volume, track.Pan, track.Shift, first, phase, track.Fx, null);
                }
            }
        }

        // Third-party processing runs without the transport/capture lock.
        for (int i = 0; i < partCount; i++)
        {
            var part = mixParts[i];
            mixParts[i] = default;
            MixTrack(stereo, part, current, epoch, running);
        }

        if (clickGain > 0 && (clickEnabled || (start < audioStart && clickStart < audioStart)))
        {
            for (int i = 0; i < frames; i++)
            {
                long absolute = start + i;
                if (!clickEnabled && absolute >= audioStart) break;
                // An immediate start may be inside the queue that has already been filled with silence.
                long firstAccent = absolute - clickStart;
                int local = clickStart > audioStart && loopStart == audioStart && firstAccent >= 0 && firstAccent < accent.Length
                    ? (int)firstAccent
                    : absolute < audioStart ? (int)firstAccent : Mod(absolute - loopStart, current.Frames);
                if (local < 0) continue;
                int beat = (int)((local + 0.5) / current.BeatFrames);
                int sample = local - (int)Math.Round(beat * current.BeatFrames);
                float[] click = beat % current.Beats == 0 ? accent : tick;
                if ((uint)sample >= (uint)click.Length) continue;
                float value = click[sample] * clickGain;
                stereo[i * 2] += value;
                stereo[i * 2 + 1] += value;
            }
        }
        for (int i = 0; i < stereo.Length; i++) stereo[i] = Math.Clamp(stereo[i], -1f, 1f);
    }

    private void MixTrack(Span<float> output, RenderPart part, LoopGrid grid, long epoch, bool running)
    {
        int loopFrames = grid.Frames;
        float left = part.Volume * MathF.Sqrt((1 - part.Pan) / 2);
        float right = part.Volume * MathF.Sqrt((1 + part.Pan) / 2);
        int fade = Math.Max(1, Math.Min(FadeFrames, part.Length / 2));
        int total = output.Length / 2;
        var fxPlan = part.Fx.CurrentPlan;
        bool fx = fxPlan.HasEffects;
        if (!fx && part.Midi == null)
        {
            int source = Mod(part.Phase - part.Shift, loopFrames);
            for (int i = part.First; i < total; i++)
            {
                float value = Sample(part, source, fade);
                output[2 * i] += value * left;
                output[2 * i + 1] += value * right;
                if (++source == loopFrames) source = 0;
            }
            return;
        }

        // The loop is already in memory, so reported insert latency can be fed ahead.
        int latency = fxPlan.Latency();
        int ahead = Mod(part.Phase - part.Shift + (long)latency, loopFrames);
        if (running && latency > 0 && part.Fx.NeedsReset(epoch, fxPlan) &&
            fxPlan.Slots.Any(static slot => slot.HasBuiltInLatency && slot.Settings.Enabled))
        {
            // Fill the insert delays before the first block after a start, seek or chain change.
            int remaining = Math.Min(latency, FxCatalog.MaxSlots * Math.Max(PitchCorrectionFx.Latency, NoiseReductionFx.Latency));
            int previous = Mod(ahead - remaining, loopFrames);
            while (remaining > 0)
            {
                int count = Math.Min(remaining, FxRack.BlockFrames);
                var block = fxBuffer.AsSpan(0, count * 2);
                FillSource(block, ref previous);
                part.Fx.Process(block, epoch, fxPlan);
                remaining -= count;
            }
        }
        for (int start = part.First; start < total; start += FxRack.BlockFrames)
        {
            int count = Math.Min(total - start, FxRack.BlockFrames);
            var block = fxBuffer.AsSpan(0, count * 2);
            FillSource(block, ref ahead);
            if (fx) part.Fx.Process(block, epoch, fxPlan);
            for (int i = 0; i < count; i++)
            {
                int index = 2 * (start + i);
                output[index] += block[2 * i] * left;
                output[index + 1] += block[2 * i + 1] * right;
            }
        }

        void FillSource(Span<float> block, ref int source)
        {
            if (part.Midi is { } midi)
            {
                midi.Render(block, grid, source, epoch, running);
                source = Mod(source + block.Length / 2, loopFrames);
                return;
            }
            for (int i = 0; i < block.Length; i += 2)
            {
                block[i] = block[i + 1] = Sample(part, source, fade);
                if (++source == loopFrames) source = 0;
            }
        }
    }

    private static float Sample(in RenderPart part, int source, int fade)
    {
        if (source >= part.Length) return 0;
        float envelope = Math.Min(fade, Math.Min(source, part.Length - 1 - source)) / (float)fade;
        return part.Samples[source] * envelope;
    }

    internal static int Mod(long frame, int modulus)
    {
        long remainder = frame % modulus;
        return (int)(remainder < 0 ? remainder + modulus : remainder);
    }

    private static float[] Click(double frequency, float volume)
    {
        var data = new float[Rate * 24 / 1000];
        for (int i = 0; i < data.Length; i++)
            data[i] = volume * (float)(Math.Sin(2 * Math.PI * frequency * i / Rate)
                * Math.Exp(-7.0 * i / data.Length) * Math.Min(1, i / 24.0));
        return data;
    }
}
