using System.Runtime.InteropServices;

namespace EazyLoop;

internal sealed class NoiseReductionFx
{
    private const int FrameSize = 480;
    // RNNoise overlaps one frame internally. Streaming arbitrary callback sizes buffers one more.
    public const int Latency = 2 * FrameSize;
    private const float PcmScale = 32768f;
    private const string Library = "eazyloop-rnnoise";
    private static readonly int StateBytes = DetectStateSize();
    public static bool IsAvailable => StateBytes > 0;

    private readonly Channel left = new();
    private readonly Channel right = new();

    public void Reset()
    {
        left.Reset();
        right.Reset();
    }

    public void Process(Span<float> stereo, float strength)
    {
        float mix = strength * 0.01f;
        left.Process(stereo, 0, mix);
        right.Process(stereo, 1, mix);
    }

    private static int DetectStateSize()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return 0;
        try { return StateSize(FrameSize); }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            return 0;
        }
    }

    [DllImport(Library, EntryPoint = "eazyloop_rnnoise_state_size", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    private static extern int StateSize(int frameSize);

    [DllImport(Library, EntryPoint = "eazyloop_rnnoise_process", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    private static extern unsafe void ProcessFrame(byte* state, float* output, float* input);

    private sealed class Channel
    {
        private readonly byte[] state = new byte[StateBytes];
        private readonly float[] input = new float[FrameSize];
        private readonly float[] wet = new float[FrameSize];
        private readonly float[] dry = new float[Latency];
        private int position;
        private int dryOffset;

        public void Reset()
        {
            Array.Clear(state);
            Array.Clear(wet);
            Array.Clear(dry);
            position = dryOffset = 0;
        }

        public unsafe void Process(Span<float> stereo, int channel, float mix)
        {
            for (int i = channel; i < stereo.Length; i += 2)
            {
                int index = dryOffset + position;
                float original = dry[index];
                float sample = stereo[i];
                dry[index] = sample;
                input[position] = Math.Clamp(sample, -1f, 1f) * PcmScale;
                stereo[i] = original + mix * (wet[position] / PcmScale - original);
                if (++position != FrameSize) continue;

                fixed (byte* modelState = state)
                fixed (float* wetFrame = wet, inputFrame = input)
                    ProcessFrame(modelState, wetFrame, inputFrame);
                position = 0;
                dryOffset = FrameSize - dryOffset;
            }
        }
    }
}
