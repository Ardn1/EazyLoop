using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EazyLoop;

internal enum AudioDriver { Wasapi, Mme }
internal sealed record AudioDevice(string Id, string Name);
internal sealed record TrackInput(AudioDriver Driver, string Id, string Name);
internal sealed record AudioOptions(AudioDriver Driver, int Buffer, string Input, string Output, double? RecordingCompensationMs = null)
{
    public int RecordingCorrectionFrames => RecordingCompensationMs is double manual
        ? (int)Math.Round((manual - AutomaticCompensationMs(Driver, Buffer)) * LoopEngine.Rate / 1000)
        : 0;

    // WASAPI packet timestamps already locate input on the output clock. MME estimates the
    // first frame of a packet from its length, so the nominal block is the editable baseline.
    public static int AutomaticCompensationMs(AudioDriver driver, int buffer) =>
        driver == AudioDriver.Mme ? Math.Clamp(buffer / 3, 10, 40) : 0;
}

internal sealed class SoundSession : IDisposable
{
    public const string Default = "default";
    private const int Rate = LoopEngine.Rate;
    private const AudioClientStreamFlags SharedFlags = AudioClientStreamFlags.EventCallback
        | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
    private static readonly WaveFormat Stereo = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
    private static readonly WaveFormat Mono = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);

    private sealed class InputRequest(string? id)
    {
        public string? Id { get; } = id;
        public InputPitch Pitch { get; } = new();
        public volatile bool Ready;
        public volatile string? Error;
    }

    private readonly LoopEngine engine;
    private readonly AudioOptions options;
    private readonly ManualResetEvent closing = new(false);
    private readonly ManualResetEvent outputClosing = new(false);
    private readonly ManualResetEvent outputStarted = new(false);
    private readonly AutoResetEvent inputChanged = new(false);
    private readonly Thread render;
    private readonly Thread capture;
    private readonly ConcurrentQueue<string> outputIssues = new();
    private InputRequest requestedInput;
    private long clockOffset = long.MinValue;
    private WaveOut? waveOut;
    private volatile bool outputReady;
    private volatile bool disposed;

    public bool CanPlay => outputReady;
    public bool CanRecord
    {
        get
        {
            var input = Volatile.Read(ref requestedInput);
            return outputReady && input.Ready && input.Error == null;
        }
    }
    public string? InputError => Volatile.Read(ref requestedInput).Error;
    public double InputFrequency => Volatile.Read(ref requestedInput) is { Ready: true, Error: null } input
        ? input.Pitch.Frequency : 0;

    public SoundSession(LoopEngine engine, AudioOptions options)
    {
        this.engine = engine;
        this.options = options;
        requestedInput = new InputRequest(options.Input);
        engine.RestartClock();
        render = new Thread(RunOutput) { IsBackground = true, Name = "EazyLoop Output" };
        capture = new Thread(RunInput) { IsBackground = true, Name = "EazyLoop Input" };
        render.SetApartmentState(ApartmentState.MTA);
        capture.SetApartmentState(ApartmentState.MTA);
        render.Start();
        capture.Start();
    }

    public static List<AudioDevice> Devices(AudioDriver driver, bool input)
    {
        var devices = new List<AudioDevice> { new(Default, Locale.T("По умолчанию")) };
        if (driver == AudioDriver.Wasapi)
        {
            using var enumerator = new MMDeviceEnumerator();
            var endpoints = enumerator.EnumerateAudioEndPoints(input ? DataFlow.Capture : DataFlow.Render,
                DeviceState.Active);
            foreach (var endpoint in endpoints)
            {
                using (endpoint) devices.Add(new(endpoint.ID, endpoint.FriendlyName));
            }
        }
        else
        {
            int count = input ? WaveIn.DeviceCount : WaveOut.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                string name = input ? WaveIn.GetCapabilities(i).ProductName : WaveOut.GetCapabilities(i).ProductName;
                devices.Add(new(i.ToString(), name));
            }
        }
        return devices;
    }

    public bool TryOutputIssue(out string? issue) => outputIssues.TryDequeue(out issue);

    public void SelectInput(string? id, bool retry = false)
    {
        var current = Volatile.Read(ref requestedInput);
        if (current.Id == id && (!retry || current.Error == null)) return;
        Volatile.Write(ref requestedInput, new InputRequest(id));
        inputChanged.Set();
    }

    private bool IsCurrent(InputRequest request) => ReferenceEquals(request, Volatile.Read(ref requestedInput));

    public long Position()
    {
        if (!CanPlay) return 0;
        if (options.Driver == AudioDriver.Wasapi) return FromClock(Now());
        try { return waveOut?.GetPosition() / 4 ?? 0; }
        catch (Exception) { return 0; }
    }

    private static long Now() => (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));

    private long FromClock(long qpc)
    {
        long offset = Volatile.Read(ref clockOffset);
        return offset == long.MinValue ? 0 : Math.Max(0, (qpc - offset) * Rate / 10_000_000);
    }

    private void RunOutput()
    {
        try
        {
            if (options.Driver == AudioDriver.Wasapi)
            {
                IntPtr priority = AvSetMmThreadCharacteristics("Pro Audio", out _);
                try { RenderWasapi(); }
                finally { if (priority != IntPtr.Zero) AvRevertMmThreadCharacteristics(priority); }
            }
            else RenderMme();
        }
        catch (Exception ex) { ReportOutput(ex); }
        finally
        {
            outputReady = false;
            outputStarted.Set();
        }
    }

    private void RunInput()
    {
        if (WaitHandle.WaitAny([closing, outputStarted]) == 0) return;
        IntPtr priority = options.Driver == AudioDriver.Wasapi
            ? AvSetMmThreadCharacteristics("Pro Audio", out _) : IntPtr.Zero;
        WaitHandle[] control = [closing, inputChanged];
        try
        {
            while (!closing.WaitOne(0))
            {
                inputChanged.WaitOne(0);
                var request = Volatile.Read(ref requestedInput);
                if (request.Id != null)
                {
                    try
                    {
                        if (options.Driver == AudioDriver.Wasapi) CaptureWasapi(request);
                        else CaptureMme(request, control);
                    }
                    catch (Exception ex) { request.Error = ex.Message; }
                    finally { request.Ready = false; }
                }

                if (IsCurrent(request) && WaitHandle.WaitAny(control) == 0) break;
            }
        }
        finally { if (priority != IntPtr.Zero) AvRevertMmThreadCharacteristics(priority); }
    }

    private void ReportOutput(Exception ex)
    {
        if (disposed) return;
        outputIssues.Enqueue(ex.Message);
    }

    private static MMDevice GetEndpoint(MMDeviceEnumerator enumerator, string id, DataFlow flow)
    {
        if (id != Default) return enumerator.GetDevice(id);
        return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
    }

    private unsafe void RenderWasapi()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = GetEndpoint(enumerator, options.Output, DataFlow.Render);
        using var signal = new AutoResetEvent(false);
        using var client = endpoint.CreateAudioClient();
        client.Initialize(AudioClientShareMode.Shared, SharedFlags, options.Buffer * 10_000L, 0, Stereo, Guid.Empty);
        client.SetEventHandle(signal.SafeWaitHandle.DangerousGetHandle());

        var output = client.AudioRenderClient;
        var clock = client.AudioClockClient;
        ulong frequency = clock.Frequency;
        int capacity = client.BufferSize;
        // The device can allocate a larger ring than requested. Keep only the requested queue ahead.
        int target = Math.Min(capacity, Math.Max(options.Buffer * Rate / 1000,
            (int)(2 * client.DefaultDevicePeriod * Rate / 10_000_000)));
        Fill(target);
        client.Start();
        try
        {
            if (clock.GetPosition(out ulong initialPosition, out ulong initialQpc))
                UpdateClock(initialPosition, initialQpc);
            outputReady = true;
            outputStarted.Set();
            WaitHandle[] events = [outputClosing, signal];
            while (WaitHandle.WaitAny(events, 1000) != 0)
            {
                if (clock.GetPosition(out ulong position, out ulong qpc)) UpdateClock(position, qpc);
                int frames = target - client.CurrentPadding;
                if (frames > 0) Fill(frames);
            }
        }
        finally { client.Stop(); }

        void UpdateClock(ulong position, ulong qpc) =>
            Volatile.Write(ref clockOffset, (long)qpc - (long)(position * (10_000_000.0 / frequency)));

        void Fill(int frames)
        {
            IntPtr pointer = output.GetBuffer(frames);
            try { engine.Render(new Span<float>((void*)pointer, frames * 2)); }
            finally { output.ReleaseBuffer(frames, AudioClientBufferFlags.None); }
        }
    }

    private unsafe void CaptureWasapi(InputRequest request)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = GetEndpoint(enumerator, request.Id!, DataFlow.Capture);
        using var signal = new AutoResetEvent(false);
        using var client = endpoint.CreateAudioClient();
        client.Initialize(AudioClientShareMode.Shared, SharedFlags, options.Buffer * 10_000L, 0, Mono, Guid.Empty);
        client.SetEventHandle(signal.SafeWaitHandle.DangerousGetHandle());

        var microphone = client.AudioCaptureClient;
        var zeros = new float[client.BufferSize];
        long delivered = 0;
        if (!IsCurrent(request) || closing.WaitOne(0)) return;
        client.Start();
        try
        {
            WaitHandle[] events = [closing, inputChanged, signal];
            while (IsCurrent(request) && WaitHandle.WaitAny(events, 1000) != 0)
            {
                while (IsCurrent(request) && microphone.GetNextPacketSize() > 0)
                {
                    IntPtr pointer = microphone.GetBuffer(out int frames, out var flags, out _, out long qpc);
                    try
                    {
                        if ((flags & AudioClientBufferFlags.TimestampError) != 0 || qpc == 0)
                            qpc = Now() - frames * 10_000_000L / Rate;
                        ReadOnlySpan<float> samples = (flags & AudioClientBufferFlags.Silent) != 0
                            ? zeros.AsSpan(0, frames) : new ReadOnlySpan<float>((void*)pointer, frames);
                        if (IsCurrent(request))
                        {
                            Deliver(request, samples, FromClock(qpc), delivered);
                        }
                        delivered += frames;
                    }
                    finally { microphone.ReleaseBuffer(frames); }
                }
            }
        }
        finally { client.Stop(); }
    }

    private void RenderMme()
    {
        using var player = new WaveOut
        {
            DeviceNumber = Number(options.Output),
            BufferMilliseconds = (options.Buffer + 2) / 3,
            NumberOfBuffers = 3
        };
        player.PlaybackStopped += (_, e) =>
        {
            outputReady = false;
            if (e.Exception is { } error) ReportOutput(error);
        };
        player.Init(new PcmOutput(engine));
        waveOut = player;
        player.Play();
        outputReady = true;
        outputStarted.Set();
        outputClosing.WaitOne();
        player.Stop();
    }

    private void CaptureMme(InputRequest request, WaitHandle[] control)
    {
        int blockMs = AudioOptions.AutomaticCompensationMs(AudioDriver.Mme, options.Buffer);
        using var microphone = new WaveIn
        {
            DeviceNumber = Number(request.Id!),
            WaveFormat = new WaveFormat(Rate, 16, 1),
            BufferMilliseconds = blockMs,
            NumberOfBuffers = 4
        };
        long delivered = 0;
        var samples = new float[Rate * blockMs / 1000 + 1];
        microphone.DataAvailable += (_, e) =>
        {
            if (!IsCurrent(request) || disposed) return;
            var raw = MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, e.BytesRecorded));
            for (int i = 0; i < raw.Length; i++) samples[i] = raw[i] / 32768f;
            if (!IsCurrent(request) || disposed) return;
            Deliver(request, samples.AsSpan(0, raw.Length), Math.Max(0, Position() - raw.Length), delivered);
            delivered += raw.Length;
        };
        microphone.RecordingStopped += (_, e) =>
        {
            request.Ready = false;
            if (e.Exception is { } error) request.Error = error.Message;
        };
        if (!IsCurrent(request) || closing.WaitOne(0)) return;
        microphone.StartRecording();
        while (IsCurrent(request) && request.Error == null && WaitHandle.WaitAny(control, 1000) != 0) { }
        microphone.StopRecording();
    }

    private void Deliver(InputRequest request, ReadOnlySpan<float> samples, long outputFrame, long inputFrame)
    {
        engine.Capture(samples, outputFrame, inputFrame);
        request.Pitch.Push(samples);
        request.Ready = true;
    }

    private static int Number(string id) => id == Default ? -1 : int.Parse(id);

    private sealed class PcmOutput(LoopEngine engine) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = new(Rate, 16, 2);
        private float[] buffer = [];

        public int Read(byte[] output, int offset, int count) => Read(output.AsSpan(offset, count));

        public int Read(Span<byte> output)
        {
            var pcm = MemoryMarshal.Cast<byte, short>(output);
            if (buffer.Length < pcm.Length) buffer = new float[pcm.Length];
            engine.Render(buffer.AsSpan(0, pcm.Length));
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(buffer[i] * 32767f);
            return output.Length;
        }
    }

    public void Dispose() => Stop(Timeout.Infinite);

    public bool StopForExit() => Stop(600);

    private bool Stop(int timeout)
    {
        if (disposed) return false;
        disposed = true;
        closing.Set();
        // Close the capture endpoint before stopping output on shared hardware.
        bool captureStopped = capture.Join(timeout);
        outputClosing.Set();
        bool outputStopped = render.Join(timeout);
        if (!captureStopped || !outputStopped) return false;
        inputChanged.Dispose();
        outputStarted.Dispose();
        outputClosing.Dispose();
        closing.Dispose();
        return true;
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, out uint taskIndex);

    [DllImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
