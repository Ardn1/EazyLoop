using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace EazyLoop;

internal sealed record ThemePalette(string Background, string Panel, string Field, string Text, string Accent, string Playhead)
{
    [JsonIgnore] public string[] Values => [Background, Panel, Field, Text, Accent, Playhead];

    public static bool TryColor(string? value, out Color color)
    {
        color = default;
        string hex = value?.Trim() ?? "";
        if (hex.StartsWith('#')) hex = hex[1..];
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
            return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    public static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public bool IsReadable()
    {
        if (!TryColor(Text, out var ink) || !TryColor(Accent, out _) || !TryColor(Playhead, out _)) return false;
        return new[] { Background, Panel, Field }.All(value =>
            TryColor(value, out var surface) && ThemeManager.Contrast(surface, ink) >= 4.5);
    }
}

internal sealed record UiTheme(string Id, string Name, ThemePalette Palette)
{
    [JsonIgnore] public bool IsCustom => Guid.TryParseExact(Id, "N", out _);
    [JsonIgnore] public string DisplayName => IsCustom ? Name : Locale.T(Name);
}

internal static class ThemeManager
{
    private sealed record Settings(string Selected, UiTheme[] Custom);

    private static readonly UiTheme[] BuiltIn =
    [
        new("classic", "Классическая", new("#101119", "#191B27", "#202231", "#F4F3FA", "#BAAAFF", "#86E7CC")),
        new("graphite", "Графит", new("#111315", "#1C2025", "#272C32", "#F0F3F6", "#CBD5E3", "#8EE6BA")),
        new("cobalt", "Кобальт", new("#0B1323", "#12223C", "#1B3050", "#EAF3FF", "#80B8FF", "#8BE6F0")),
        new("forest", "Хвоя", new("#0D1711", "#17271C", "#223729", "#EFF9E9", "#B7DD88", "#8CE3BF")),
        new("copper", "Медь", new("#1B1310", "#2C1F19", "#3B2A21", "#FFF1E7", "#F2AC7D", "#A4DEB5")),
        new("plum", "Слива", new("#190F1D", "#2B1B33", "#392543", "#FFF1FF", "#E9AAEC", "#A0E8CD")),
        new("lagoon", "Лагуна", new("#081919", "#10292C", "#19383C", "#EAF9F7", "#7FDDCA", "#F1CB88")),
        new("paper", "Бумага", new("#EBE7DF", "#FCFAF3", "#F1EDE4", "#282520", "#5942A5", "#00766A")),
        new("pastel-blue", "Голубая пастель", new("#DDEBF7", "#F2F8FF", "#D4E5F5", "#23384E", "#3C6190", "#276B71")),
        new("beach", "Пляжная", new("#F1E9D9", "#FFFCF5", "#DCEEF3", "#323D43", "#3B6580", "#286D6D"))
    ];

    private static readonly string PathToSettings = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EazyLoop", "themes.json");
    private static UiTheme[] custom = [];
    private static ResourceDictionary? applied;
    private static readonly Dictionary<(string, double, bool), Pen> Pens = [];
    private static int revision;

    public static UiTheme Current { get; private set; } = BuiltIn[0];
    public static IEnumerable<UiTheme> All => BuiltIn.Concat(custom);

    public static void Initialize()
    {
        if (applied != null) return;
        try
        {
            if (new FileInfo(PathToSettings) is { Exists: true, Length: <= 512_000 } &&
                JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathToSettings)) is { } saved)
            {
                custom = (saved.Custom ?? []).Where(theme => theme is { IsCustom: true } &&
                    !string.IsNullOrWhiteSpace(theme.Name) && theme.Name.Length <= 48 &&
                    theme.Palette != null && theme.Palette.IsReadable())
                    .DistinctBy(theme => theme.Id).ToArray();
                Current = All.FirstOrDefault(theme => theme.Id == saved.Selected) ?? BuiltIn[0];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        Preview(Current.Palette);
    }

    public static void Select(UiTheme theme)
    {
        if (theme == Current) return;
        Save(new Settings(theme.Id, custom));
        Current = theme;
        Preview(theme.Palette);
    }

    public static void SaveCustom(UiTheme theme)
    {
        var updated = custom.Select(item => item.Id == theme.Id ? theme : item).ToArray();
        if (!updated.Any(item => item.Id == theme.Id)) updated = [.. updated, theme];
        Save(new Settings(theme.Id, updated));
        custom = updated;
        Current = theme;
        Preview(theme.Palette);
    }

    public static SolidColorBrush Brush(string key) => (SolidColorBrush)applied![key];

    public static Pen Stroke(string key, double width = 1, bool dotted = false)
    {
        var style = (key, width, dotted);
        if (Pens.TryGetValue(style, out var pen)) return pen;
        pen = new Pen(Brush(key), width);
        if (dotted) pen.DashStyle = DashStyles.Dot;
        pen.Freeze();
        Pens.Add(style, pen);
        return pen;
    }

    public static void Preview(ThemePalette palette)
    {
        var values = palette.Values.Select(value => { ThemePalette.TryColor(value, out var color); return color; }).ToArray();
        var bg = values[0];
        var panel = values[1];
        var field = values[2];
        var ink = values[3];
        var accent = values[4];
        var playhead = values[5];
        bool light = Luminance(panel) > .42;
        var rose = light ? Color.FromRgb(161, 38, 66) : Color.FromRgb(255, 158, 172);
        var dictionary = new ResourceDictionary { ["ThemeRevision"] = ++revision };
        Add("Background", bg);
        Add("Panel", panel);
        Add("Field", field);
        Add("Ink", ink);
        Add("Violet", accent);
        Add("Mint", playhead);
        Add("Rose", rose);
        Add("Soft", Blend(panel, ink, .76));
        Add("Muted", Blend(panel, ink, .64));
        Add("Line", Blend(field, ink, light ? .23 : .12));
        Add("StrongLine", Blend(field, ink, .32));
        Add("Surface", Blend(bg, panel, .44));
        Add("FieldHover", Blend(field, ink, .07));
        Add("AccentPanel", Blend(panel, accent, .17));
        Add("AccentLine", Blend(field, accent, .50));
        Add("AccentHover", WithOpacity(accent, 46));
        Add("AccentKey", WithOpacity(accent, 92));
        Add("OnAccent", ContrastInk(accent));
        Add("OnMint", ContrastInk(playhead));
        Add("MintShade", WithOpacity(playhead, 38));
        Add("RecordPanel", Blend(panel, rose, .13));
        Add("RecordLine", Blend(field, rose, .40));
        Add("Warning", light ? Color.FromRgb(133, 66, 19) : Color.FromRgb(255, 209, 142));
        Add("HoverOverlay", WithOpacity(ink, 25));
        dictionary["TransportTintColor"] = Blend(panel, accent, .09);
        dictionary["PanelColor"] = panel;

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        int index = applied == null ? -1 : dictionaries.IndexOf(applied);
        applied = dictionary;
        Pens.Clear();
        if (index < 0) dictionaries.Add(dictionary);
        else dictionaries[index] = dictionary;

        void Add(string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            dictionary[key] = brush;
        }
    }

    public static Color ContrastInk(Color color) => Luminance(color) > .179 ? Colors.Black : Colors.White;

    public static double Contrast(Color a, Color b)
    {
        double first = Luminance(a), second = Luminance(b);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte component)
        {
            double value = component / 255.0;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }

    private static Color Blend(Color first, Color second, double amount) => Color.FromRgb(
        (byte)Math.Round(first.R + (second.R - first.R) * amount),
        (byte)Math.Round(first.G + (second.G - first.G) * amount),
        (byte)Math.Round(first.B + (second.B - first.B) * amount));

    private static Color WithOpacity(Color color, byte opacity) => Color.FromArgb(opacity, color.R, color.G, color.B);

    private static void Save(Settings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathToSettings)!);
        string pending = PathToSettings + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(pending, JsonSerializer.Serialize(settings));
            File.Move(pending, PathToSettings, overwrite: true);
        }
        finally
        {
            try { File.Delete(pending); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
