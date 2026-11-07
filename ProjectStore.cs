using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using NAudio.Wave;

namespace EazyLoop;

internal sealed record TrackSettings(Guid Id, int Number, int Frames, float Volume, float Pan,
    int Shift, bool Muted, double Zoom, string? Name = null, TrackInput? Input = null, TrackFxSettings? Fx = null,
    bool Collapsed = false, MidiTrackSettings? Midi = null, bool Solo = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AudioEdit? AudioEdit = null)
{
    public const int MaxNameLength = 64;
}

internal sealed record TrackSnapshot(TrackSettings Settings, float[] Samples);

internal sealed record ProjectSettings(LoopGrid Grid, bool Metronome, bool CountIn, float ClickVolume);

internal sealed record ProjectSnapshot(ProjectSettings Settings, TrackSnapshot[] Tracks);

internal sealed class ProjectStore : IDisposable
{
    public const string Extension = ".eazyloop";
    public const string LegacyExtension = ".loopdeck";

    private sealed record ProjectFile(int Version, ProjectSettings Settings, TrackSettings[] Tracks,
        Guid[]? PendingDeletes);

    private const int Version = 10;
    private const int MaxManifestBytes = 16 * 1024 * 1024;
    private const int ChunkSize = 64 * 1024;
    private static readonly int MaxFrames = new LoopGrid(40, 7, 16).Frames;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly FileStream projectLock;
    private readonly string audioFolder;
    private Dictionary<Guid, (int Frames, float[] Samples)> audioOnDisk = [];
    private HashSet<Guid> pendingDeletes = [];
    public string FilePath { get; }
    public string Name => Path.GetFileNameWithoutExtension(FilePath);
    public static string DefaultFolder
    {
        get
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrEmpty(documents)) throw new IOException(Locale.T("Не удалось найти папку «Документы»."));
            return Path.Combine(documents, "EazyLoop");
        }
    }

    private ProjectStore(string path)
    {
        FilePath = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(FilePath)!;
        audioFolder = Path.Combine(folder, "Audio");
        CheckAudioFolder();
        projectLock = new FileStream(Path.Combine(folder, ".loopdeck-lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    public static string NewDestination(string folder, string name)
    {
        name = NormalizeName(name);
        string directory = Path.Combine(folder, name);
        if (Directory.Exists(directory) || File.Exists(directory))
            throw new IOException(Locale.F($"Папка «{name}» уже существует. Введите другое имя."));
        return directory + Extension;
    }

    private static string NormalizeName(string name)
    {
        string extension = Path.GetExtension(name);
        if (extension.Equals(Extension, StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(LegacyExtension, StringComparison.OrdinalIgnoreCase))
            name = name[..^extension.Length];
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.EndsWith('.') || name.EndsWith(' '))
            throw new IOException(Locale.T("Введите имя проекта без недопустимых символов, точки или пробела на конце."));
        return name;
    }

    public static (ProjectStore Store, string? Warning) Create(string destination, ProjectSnapshot snapshot)
    {
        string name = NormalizeName(Path.GetFileName(destination));
        string folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, name);
        if (Directory.Exists(folder) || File.Exists(folder))
            throw new IOException(Locale.F($"Папка «{name}» уже существует. Выберите другое имя или место."));
        Directory.CreateDirectory(folder);

        var store = new ProjectStore(Path.Combine(folder, name + Extension));
        try
        {
            if (File.Exists(store.FilePath)) throw new IOException(Locale.T("Проект уже существует."));
            return (store, store.Save(snapshot));
        }
        catch { store.Dispose(); throw; }
    }

    public static (ProjectStore Store, ProjectSnapshot Snapshot, string? Warning) Open(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(Locale.T("Файл проекта не найден."), path);
        var store = new ProjectStore(path);
        try
        {
            using var stream = File.OpenRead(store.FilePath);
            if (stream.Length > MaxManifestBytes) throw new InvalidDataException(Locale.T("Файл проекта слишком большой."));
            var project = JsonSerializer.Deserialize<ProjectFile>(stream, Json);
            Validate(project);
            var tracks = project!.Tracks.Select(track =>
                new TrackSnapshot(track, track.Frames == 0 ? [] : store.ReadAudio(track))).ToArray();
            store.audioOnDisk = tracks.Where(track => track.Settings.Frames > 0)
                .ToDictionary(track => track.Settings.Id, track => (track.Settings.Frames, track.Samples));
            store.pendingDeletes = (project.PendingDeletes ?? []).ToHashSet();
            return (store, new ProjectSnapshot(project.Settings, tracks), store.CleanDeletedAudio());
        }
        catch { store.Dispose(); throw; }
    }

    public string? Save(ProjectSnapshot snapshot)
    {
        CheckAudioFolder();
        Directory.CreateDirectory(audioFolder);
        var tracks = snapshot.Tracks.Select(track => track.Settings).ToArray();
        var nextAudio = snapshot.Tracks.Where(track => track.Settings.Frames > 0)
            .ToDictionary(track => track.Settings.Id, track => (track.Settings.Frames, track.Samples));
        var deleted = pendingDeletes.Concat(audioOnDisk.Keys).Except(nextAudio.Keys).ToHashSet();
        int version = tracks.Any(track => track.AudioEdit != null) ? Version :
            tracks.Any(track => track.Midi?.Glissando?.Timeline != null) ? 9 :
            tracks.Any(track => (track.Midi?.Synth ?? track.Midi?.Glissando?.Voice) is { Blend: > 0 }) ? 8 :
            tracks.Any(track => track.Fx?.Slots.Any(slot => slot.Kind == FxKind.PitchCorrection) == true) ? 7 :
            tracks.Any(track => track.Midi?.Glissando != null) ? 6 :
            tracks.Any(track => track.Midi?.Drums?.Pads.Any(pad => pad.Sound > DrumSound.Ride) == true) ? 5 :
            tracks.Any(track => track.Midi?.Drums != null) ? 4 :
            tracks.Any(track => track.Midi != null) ? 3 : tracks.Any(track => track.Fx != null) ? 2 : 1;
        var project = new ProjectFile(version,
            snapshot.Settings, tracks, deleted.ToArray());
        Validate(project);

        foreach (var track in snapshot.Tracks)
        {
            var info = track.Settings;
            if (info.Frames == 0) continue;
            string path = WavePath(info.Id);
            var audio = nextAudio[info.Id];
            if (audioOnDisk.GetValueOrDefault(info.Id) == audio && File.Exists(path)) continue;
            WriteAudio(path, track);
            audioOnDisk[info.Id] = audio;
        }

        AtomicFile.Replace(FilePath, path =>
        {
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(output, project, Json);
            if (output.Length > MaxManifestBytes) throw new InvalidDataException(Locale.T("Настройки плагинов превысили лимит проекта в 16 МБ."));
            output.Flush(true);
        });

        audioOnDisk = nextAudio;
        pendingDeletes = deleted;
        return CleanDeletedAudio();
    }

    private string? CleanDeletedAudio()
    {
        if (!Directory.Exists(audioFolder))
        {
            pendingDeletes.Clear();
            return null;
        }
        string? warning = null;
        foreach (Guid id in pendingDeletes.ToArray())
        {
            try
            {
                File.Delete(WavePath(id));
                pendingDeletes.Remove(id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warning ??= Locale.F($"Проект сохранён. Не удалось удалить WAV: {ex.Message}");
            }
        }
        return warning;
    }

    private static void Validate(ProjectFile? file)
    {
        if (file == null || file.Version is < 1 or > Version || file.Settings == null || file.Tracks == null)
            throw new InvalidDataException(Locale.T("Неизвестный формат проекта EazyLoop."));

        var settings = file.Settings;
        var grid = settings.Grid;
        if (grid.Bpm is < 40 or > 240 || grid.Beats is < 2 or > 7 ||
            grid.Bars is not (1 or 2 or 3 or 4 or 6 or 8 or 12 or 16) ||
            !float.IsFinite(settings.ClickVolume) || settings.ClickVolume is < 0 or > 1 ||
            file.Tracks.Length > LoopEngine.MaxTracks)
            throw new InvalidDataException(Locale.T("Некорректные настройки проекта."));

        var ids = new HashSet<Guid>();
        var numbers = new HashSet<int>();
        foreach (var track in file.Tracks)
        {
            if (track?.Fx is { } fx && !FxCatalog.IsValid(fx))
                throw new InvalidDataException(Locale.T("Некорректные настройки эффектов."));
            if (track?.Midi is { } midi && (track.Frames != 0 || track.Input != null || !midi.IsValid()))
                throw new InvalidDataException(Locale.T("Некорректная MIDI-дорожка."));
            if (track == null || track.Id == Guid.Empty || !ids.Add(track.Id) ||
                track.Number <= 0 || track.Number >= int.MaxValue - 1 || !numbers.Add(track.Number) ||
                track.Frames < 0 || track.Frames > MaxFrames ||
                (track.AudioEdit is { } edit && (file.Version < 10 || !edit.IsValid(track.Frames, MaxFrames))) ||
                track.Shift < -grid.Frames / 2 || track.Shift > grid.Frames / 2 ||
                !float.IsFinite(track.Volume) || track.Volume is < 0 or > 1 ||
                !float.IsFinite(track.Pan) || track.Pan is < -1 or > 1 ||
                !double.IsFinite(track.Zoom) || track.Zoom is < -2 or > 4 ||
                track.Name is { Length: > TrackSettings.MaxNameLength } ||
                (track.Input is { } input && (!Enum.IsDefined(input.Driver) ||
                    string.IsNullOrWhiteSpace(input.Id) || input.Id.Length > 512 ||
                    string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 512)))
                throw new InvalidDataException(Locale.T("Некорректные данные дорожки."));
        }
        if (file.PendingDeletes?.Any(id => id == Guid.Empty || ids.Contains(id)) == true)
            throw new InvalidDataException(Locale.T("Некорректный список удалённых дорожек."));
    }

    private void CheckAudioFolder()
    {
        if (Directory.Exists(audioFolder) && (File.GetAttributes(audioFolder) & FileAttributes.ReparsePoint) != 0)
            throw new IOException(Locale.T("Папка Audio должна находиться внутри проекта."));
    }

    private string WavePath(Guid id) => Path.Combine(audioFolder, $"track-{id:N}.wav");

    public bool IsManagedWave(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string name = Path.GetFileNameWithoutExtension(fullPath);
        return string.Equals(Path.GetDirectoryName(fullPath), audioFolder, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetExtension(fullPath), ".wav", StringComparison.OrdinalIgnoreCase) &&
            name.StartsWith("track-", StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(name.AsSpan(6), "N", out _);
    }

    public static void ExportWave(string path, TrackSnapshot track)
    {
        if (track.Settings.Frames <= 0) throw new InvalidOperationException(Locale.T("На дорожке ещё нет записи."));
        if (!string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
            throw new IOException(Locale.T("Для экспорта выберите расширение .wav."));
        if (track.Settings.AudioEdit is { } edit)
        {
            var samples = edit.Render(track.Samples, track.Settings.Frames);
            track = new TrackSnapshot(track.Settings with { Frames = samples.Length, AudioEdit = null }, samples);
        }
        WriteAudio(path, track);
    }

    private float[] ReadAudio(TrackSettings track)
    {
        using var reader = new WaveFileReader(WavePath(track.Id));
        var format = reader.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.IeeeFloat || format.SampleRate != LoopEngine.Rate ||
            format.BitsPerSample != 32 || format.Channels != 1 || reader.Length != track.Frames * sizeof(float))
            throw new InvalidDataException(Locale.F($"Неверный WAV для дорожки {track.Number}."));

        var samples = new float[track.Frames];
        var bytes = MemoryMarshal.AsBytes(samples.AsSpan());
        for (int offset = 0; offset < bytes.Length; offset += ChunkSize)
            reader.ReadExactly(bytes.Slice(offset, Math.Min(ChunkSize, bytes.Length - offset)));
        if (Array.Exists(samples, sample => !float.IsFinite(sample)))
            throw new InvalidDataException(Locale.F($"Повреждены аудиоданные дорожки {track.Number}."));
        return samples;
    }

    private static void WriteAudio(string target, TrackSnapshot track)
    {
        AtomicFile.Replace(target, path =>
        {
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            using (var writer = new WaveFileWriter(output,
                WaveFormat.CreateIeeeFloatWaveFormat(LoopEngine.Rate, 1)))
            {
                var source = MemoryMarshal.AsBytes(track.Samples.AsSpan(0, track.Settings.Frames));
                var buffer = new byte[Math.Min(ChunkSize, source.Length)];
                for (int offset = 0; offset < source.Length; offset += buffer.Length)
                {
                    int count = Math.Min(buffer.Length, source.Length - offset);
                    source.Slice(offset, count).CopyTo(buffer);
                    writer.Write(buffer, 0, count);
                }
            }
            output.Flush(true);
        });
    }

    public void Dispose() => projectLock.Dispose();
}
