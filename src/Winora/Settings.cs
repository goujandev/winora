using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Winora;

[JsonConverter(typeof(JsonStringEnumConverter<TaskbarMode>))]
public enum TaskbarMode { Default, Transparent, Acrylic }

public sealed record UserSettings(TaskbarMode Mode = TaskbarMode.Default, bool AlwaysShowTrayIcons = false,
    bool DarkMode = false, bool StartWinoraWithWindows = true, bool TilingEnabled = false, int TilingGap = 8,
    bool MoveTilesForFullscreenGames = true, string[]? FullscreenGameExecutables = null,
    string[]? TiledAppExecutables = null);

public static class Settings
{
    public static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppBuild.DataFolder);
    public static string FilePath => Path.Combine(DirectoryPath, "preferences.json");

    public static UserSettings Load(string? path = null)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path ?? FilePath));
            return settings is not null && Enum.IsDefined(settings.Mode) ? Normalize(settings) : new();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public static void Save(UserSettings settings, string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Normalize(settings)));
        File.Move(temporary, path, overwrite: true);
    }

    private static UserSettings Normalize(UserSettings settings) => settings with
    {
        TilingGap = Math.Clamp(settings.TilingGap, TilingConfiguration.MinimumGap, TilingConfiguration.MaximumGap),
        FullscreenGameExecutables = NormalizeExecutables(settings.FullscreenGameExecutables),
        TiledAppExecutables = NormalizeExecutables(settings.TiledAppExecutables)
    };

    private static string[]? NormalizeExecutables(string[]? games)
    {
        if (games is null) return null;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in games)
        {
            if (string.IsNullOrWhiteSpace(game) || !Path.IsPathFullyQualified(game)
                || !string.Equals(Path.GetExtension(game), ".exe", StringComparison.OrdinalIgnoreCase)) continue;
            try { paths.Add(Path.GetFullPath(game)); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException) { }
        }
        return paths.Count == 0 ? null : paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
