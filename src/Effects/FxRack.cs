using NAudio.Vst3;

namespace EazyLoop;

internal sealed class FxRack : IDisposable
{
    public const int BlockFrames = 512;
    internal sealed record SlotState(Guid Id, FxSlotSettings Settings, bool Connected);
    internal sealed record RackState(bool Bypassed, bool EqEnabled, EqBand[] Bands, SlotState[] Slots);
    internal sealed record Plan(EqFilter[]? Filters, FxSlot[] Slots, bool Bypassed = false, long ResetVersion = 0)
    {
        public bool HasEffects => !Bypassed && (Filters != null || Slots.Any(slot => slot.IsActive));

        public int Latency()
        {
            if (Bypassed) return 0;
            int samples = 0;
            foreach (var slot in Slots) samples += slot.Latency;
            return samples;
        }
    }

    private readonly double[] z1 = new double[8];
    private readonly double[] z2 = new double[8];
    private Plan plan = new(null, []);
    private long epoch = -1;
    private long appliedResetVersion;
    public bool EqEnabled { get; private set; } = true;
    public EqBand[] Bands { get; private set; } = FxCatalog.Flat;
    public Plan CurrentPlan => Volatile.Read(ref plan);
    public FxSlot[] Slots => CurrentPlan.Slots;
    public bool Bypassed => CurrentPlan.Bypassed;

    public FxRack(TrackFxSettings? settings = null)
    {
        if (settings == null) return;
        SetEq(settings.EqEnabled, settings.Bands);
        SetSlots(settings.Slots.Select(slot => new FxSlot(slot)).ToArray());
        SetBypassed(settings.Bypassed);
    }

    public TrackFxSettings? Snapshot()
    {
        var current = CurrentPlan;
        return !current.Bypassed && EqEnabled && Bands.SequenceEqual(FxCatalog.Flat) && current.Slots.Length == 0 ? null :
            new TrackFxSettings(EqEnabled, Bands, current.Slots.Select(slot => slot.Settings).ToArray(), current.Bypassed);
    }

    internal RackState CaptureState() => new(Bypassed, EqEnabled, Bands,
        Slots.Select(slot => new SlotState(slot.Id, slot.Settings, slot.Plugin != null)).ToArray());

    internal static bool SameState(RackState a, RackState b) =>
        a.Bypassed == b.Bypassed && a.EqEnabled == b.EqEnabled &&
        a.Bands.SequenceEqual(b.Bands) && a.Slots.Length == b.Slots.Length &&
        a.Slots.Zip(b.Slots).All(pair => pair.First.Id == pair.Second.Id &&
            pair.First.Settings.Enabled == pair.Second.Settings.Enabled &&
            pair.First.Settings.Values.SequenceEqual(pair.Second.Settings.Values));

    internal bool SavePluginStates()
    {
        bool changed = false;
        foreach (var slot in Slots) changed |= slot.SavePluginState();
        return changed;
    }

    internal void Replace(bool bypassed, bool eqEnabled, EqBand[] bands, FxSlot[] slots)
    {
        EqEnabled = eqEnabled;
        Bands = bands;
        var current = CurrentPlan;
        Volatile.Write(ref plan, new Plan(Filters(eqEnabled, bands), slots, bypassed, current.ResetVersion + 1));
    }

    public void SetBypassed(bool bypassed)
    {
        var current = CurrentPlan;
        if (current.Bypassed == bypassed) return;
        Volatile.Write(ref plan, current with { Bypassed = bypassed, ResetVersion = current.ResetVersion + 1 });
    }

    public void SetEq(bool enabled, EqBand[] bands)
    {
        EqEnabled = enabled;
        Bands = bands;
        Volatile.Write(ref plan, plan with { Filters = Filters(enabled, bands) });
    }

    private static EqFilter[]? Filters(bool enabled, EqBand[] bands) =>
        enabled && bands.Any(band => band.Db != 0) ? bands.Select((band, i) => EqFilter.Create(i, band)).ToArray() : null;

    public void SetSlots(FxSlot[] slots)
    {
        var current = CurrentPlan;
        bool reset = current.Slots.Any(static slot => slot.HasBuiltInLatency) ||
            slots.Any(static slot => slot.HasBuiltInLatency);
        Volatile.Write(ref plan, current with { Slots = slots, ResetVersion = current.ResetVersion + (reset ? 1 : 0) });
    }

    public void SetSlotEnabled(FxSlot slot, bool enabled)
    {
        var settings = slot.Settings;
        if (settings.Enabled == enabled) return;
        slot.Update(settings with { Enabled = enabled });
        if (!slot.HasBuiltInLatency) return;
        var current = CurrentPlan;
        Volatile.Write(ref plan, current with { ResetVersion = current.ResetVersion + 1 });
    }

    internal bool NeedsReset(long currentEpoch, Plan current) =>
        epoch != currentEpoch || appliedResetVersion != current.ResetVersion;

    public void Process(Span<float> stereo, long currentEpoch, Plan current)
    {
        if (current.Bypassed) return;
        if (NeedsReset(currentEpoch, current))
        {
            epoch = currentEpoch;
            appliedResetVersion = current.ResetVersion;
            Array.Clear(z1);
            Array.Clear(z2);
            foreach (var slot in current.Slots) slot.Reset();
        }
        if (current.Filters is { } filters)
        {
            for (int band = 0; band < filters.Length; band++)
            {
                var filter = filters[band];
                int l = band * 2, r = l + 1;
                for (int i = 0; i < stereo.Length; i += 2)
                {
                    stereo[i] = filter.Tick(stereo[i], ref z1[l], ref z2[l]);
                    stereo[i + 1] = filter.Tick(stereo[i + 1], ref z1[r], ref z2[r]);
                }
            }
        }
        foreach (var slot in current.Slots) slot.Process(stereo);
    }

    public void Dispose()
    {
        var old = Slots;
        SetSlots([]);
        foreach (var slot in old) slot.Dispose();
    }
}

internal sealed class FxSlot : IDisposable
{
    private FxSlotSettings settings;
    private readonly BuiltInFx? builtIn;
    private readonly object nativeGate = new();
    private readonly float[]? nativeOutput;
    private Vst3Module? module;
    private Vst3Plugin? plugin;
    private string? error;

    public FxSlotSettings Settings => Volatile.Read(ref settings);
    internal Guid Id { get; }
    public Vst3Plugin? Plugin => Volatile.Read(ref plugin);
    public string? Error => Volatile.Read(ref error);
    public string Name => Settings.Plugin?.Name ?? Locale.T(FxCatalog.BuiltIn[Settings.Kind].Name);
    public bool IsActive => Settings.Enabled && (builtIn != null || (Plugin != null && Error == null));
    public bool HasBuiltInLatency => builtIn?.Latency > 0;
    public int Latency => Settings.Enabled && Error == null
        ? builtIn?.Latency ?? (int)Math.Min(Plugin?.LatencySamples ?? 0, LoopEngine.Rate * 2u) : 0;

    public FxSlot(FxSlotSettings settings, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        this.settings = settings;
        if (settings.Kind == FxKind.Vst3) nativeOutput = new float[FxRack.BlockFrames * 2];
        else if (settings.Kind == FxKind.NoiseReduction && !NoiseReductionFx.IsAvailable)
            error = Locale.T("Модуль RNNoise недоступен.");
        else builtIn = new BuiltInFx(settings.Kind);
    }

    public void Update(FxSlotSettings value) => Volatile.Write(ref settings, value);

    public void Connect(Vst3Module source, Vst3ClassInfo info)
    {
        var instance = source.CreatePlugin(info, LoopEngine.Rate, FxRack.BlockFrames);
        try
        {
            if (instance.IsInstrument || instance.InputChannelCount != 2 || instance.OutputChannelCount != 2)
                throw new NotSupportedException(Locale.T("Нужен VST3-эффект со стереовходом и стереовыходом."));
            if (instance.LatencySamples > LoopEngine.Rate * 2)
                throw new NotSupportedException(Locale.T("Задержка эффекта превышает две секунды."));
            if (Settings.Plugin?.State is { Length: > 0 } state) instance.LoadState(state);
        }
        catch { instance.Dispose(); throw; }
        module = source;
        Volatile.Write(ref error, null);
        Volatile.Write(ref plugin, instance);
    }

    public bool SavePluginState()
    {
        lock (nativeGate)
        {
            if (plugin == null || Settings.Plugin is not { } reference) return false;
            byte[] state = plugin.SaveState();
            if (state.Length > FxCatalog.MaxStateBytes)
                throw new InvalidOperationException(Locale.T("Состояние плагина превышает 1 МБ."));
            if (reference.State != null && state.AsSpan().SequenceEqual(reference.State)) return false;
            Update(Settings with { Plugin = reference with { State = state } });
            return true;
        }
    }

    public void Reset() => builtIn?.Reset();

    public void Process(Span<float> stereo)
    {
        var value = Settings;
        if (!value.Enabled) return;
        if (builtIn != null)
        {
            builtIn.Process(stereo, value.Values);
            return;
        }
        if (Plugin == null || Error != null || !Monitor.TryEnter(nativeGate)) return;
        try
        {
            if (plugin == null) return;
            var output = nativeOutput.AsSpan(0, stereo.Length);
            plugin.Process(stereo, output, stereo.Length / 2);
            foreach (float sample in output)
                if (!float.IsFinite(sample))
                    throw new InvalidOperationException(Locale.T("Плагин вернул некорректный аудиосигнал."));
            output.CopyTo(stereo);
        }
        catch (Exception ex) { Volatile.Write(ref error, ex.Message); }
        finally { Monitor.Exit(nativeGate); }
    }

    public void Dispose()
    {
        lock (nativeGate)
        {
            var instance = plugin;
            Volatile.Write(ref plugin, null);
            try { instance?.Dispose(); }
            finally { module?.Dispose(); module = null; }
        }
    }
}
