using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EazyLoop;

internal static class FxPresetStore
{
    public const string Extension = ".eazyfx";
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EazyLoop", "Presets", "FX");
    private const int Version = 1;
    private const int MaxBytes = 16 * 1024 * 1024;

    private sealed record PresetFile(int Version, TrackFxSettings Settings);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<FxKind>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static TrackFxSettings Load(string path)
    {
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        CheckSize(input);
        try
        {
            if (JsonSerializer.Deserialize<PresetFile>(input, Json) is { Version: Version, Settings: { } settings } &&
                FxCatalog.IsValid(settings)) return settings;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(Locale.T("Не удалось прочитать файл пресета FX."), ex);
        }
        throw new InvalidDataException(Locale.T("Неподдерживаемая версия или неверные параметры пресета FX."));
    }

    public static void Save(string path, TrackFxSettings settings)
    {
        if (!string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase))
            throw new IOException(Locale.F($"Используйте расширение {Extension} для пресета FX."));
        if (!FxCatalog.IsValid(settings))
            throw new InvalidDataException(Locale.T("Неподдерживаемая версия или неверные параметры пресета FX."));

        AtomicFile.Replace(path, temporary =>
        {
            using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(output, new PresetFile(Version, settings), Json);
            CheckSize(output);
            output.Flush(true);
        });
    }

    private static void CheckSize(Stream stream)
    {
        if (stream.Length > MaxBytes) throw new InvalidDataException(Locale.T("Файл пресета FX слишком большой."));
    }
}
