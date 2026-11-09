using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EazyLoop;

internal static class SynthPresetStore
{
    public const string Extension = ".eazysynth";
    private const int Version = 2;
    private const int MaxBytes = 16 * 1024;
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EazyLoop", "Presets", "SimpleSynth");

    private sealed record PresetFile(int Version, SynthPatch Patch);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<WaveShape>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static SynthPatch Load(string path)
    {
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length > MaxBytes) throw new InvalidDataException(Locale.T("Файл тембра слишком большой."));
        try
        {
            if (JsonSerializer.Deserialize<PresetFile>(input, Json) is { Version: 1 or Version, Patch: { } patch } &&
                patch.IsValid()) return patch;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(Locale.T("Не удалось прочитать файл тембра SimpleSynth."), ex);
        }
        throw new InvalidDataException(Locale.T("Файл содержит неподдерживаемую версию или неверные параметры тембра."));
    }

    public static void Save(string path, SynthPatch patch)
    {
        if (!string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase))
            throw new IOException(Locale.F($"Используйте расширение {Extension} для файла тембра."));
        if (!patch.IsValid()) throw new InvalidDataException(Locale.T("Параметры тембра вне допустимого диапазона."));

        AtomicFile.Replace(path, temporary =>
        {
            using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write);
                JsonSerializer.Serialize(output, new PresetFile(patch.Blend == 0 ? 1 : Version, patch), Json);
            output.Flush(true);
        });
    }
}
