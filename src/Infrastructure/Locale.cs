using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace EazyLoop;

internal static class Locale
{
    internal sealed record Language(string Code, string Name, string Flag, CultureInfo Culture,
        IReadOnlyDictionary<string, string> Strings);

    private sealed record Pack(string Name, string Flag, Dictionary<string, string> Strings);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly string Preference = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EazyLoop", "language.txt");
    private static readonly Lazy<Dictionary<string, Language>> Catalog = new(LoadLanguages);
    private static Language? active;
    private static ResourceDictionary? localizedResources;

    internal static Language Current => Volatile.Read(ref active) ?? Catalog.Value["ru"];
    internal static IReadOnlyList<Language> Languages => Catalog.Value.Values.ToArray();
    internal static CultureInfo Culture => Current.Culture;

    internal static string T(string source) => Lookup(Current, source);

    internal static string F(FormattableString source)
    {
        var language = Current;
        try { return string.Format(language.Culture, Lookup(language, source.Format), source.GetArguments()); }
        catch (FormatException) { return source.ToString(language.Culture); }
    }

    internal static void Initialize()
    {
        if (localizedResources != null) return;
        string code = "ru";
        try
        {
            if (new FileInfo(Preference) is { Exists: true, Length: <= 64 }) code = File.ReadAllText(Preference).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Apply(Catalog.Value.GetValueOrDefault(code) ?? Catalog.Value["ru"]);
    }

    internal static void Select(Language language)
    {
        if (language == Current) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Preference)!);
        string pending = Preference + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(pending, language.Code);
            File.Move(pending, Preference, overwrite: true);
        }
        finally
        {
            try { File.Delete(pending); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Apply(language);
    }

    private static void Apply(Language language)
    {
        var dictionary = new ResourceDictionary();
        foreach (var key in Catalog.Value["ru"].Strings.Keys)
            dictionary["loc:" + key] = Lookup(language, key);
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        Volatile.Write(ref active, language);
        if (localizedResources == null) dictionaries.Add(dictionary);
        else dictionaries[dictionaries.IndexOf(localizedResources)] = dictionary;
        localizedResources = dictionary;
    }

    private static string Lookup(Language language, string source) =>
        language.Strings.GetValueOrDefault(source) ?? Catalog.Value["ru"].Strings.GetValueOrDefault(source) ?? source;

    private static Dictionary<string, Language> LoadLanguages()
    {
        const string prefix = "EazyLoop.Languages.";
        var result = new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(Locale).Assembly;
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            Add(name[prefix.Length..^5], stream);
        }
        string folder = Path.Combine(AppContext.BaseDirectory, "Languages");
        try
        {
            if (Directory.Exists(folder))
                foreach (string file in Directory.GetFiles(folder, "*.json"))
                    try
                    {
                        using var stream = File.OpenRead(file);
                        Add(Path.GetFileNameWithoutExtension(file), stream);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;

        void Add(string code, Stream stream)
        {
            if (stream.Length > 2 * 1024 * 1024) return;
            try
            {
                if (JsonSerializer.Deserialize<Pack>(stream, Json) is not { Strings: { } texts } pack ||
                    string.IsNullOrWhiteSpace(pack.Name)) return;
                var culture = CultureInfo.GetCultureInfo(code);
                var strings = result.TryGetValue(code, out var original)
                    ? new Dictionary<string, string>(original.Strings) : new Dictionary<string, string>();
                foreach (var entry in texts)
                    if (!string.IsNullOrWhiteSpace(entry.Value)) strings[entry.Key] = entry.Value;
                result[code] = new Language(code, pack.Name, pack.Flag ?? "", culture, strings);
            }
            catch (Exception ex) when (ex is JsonException or CultureNotFoundException) { }
        }
    }
}
