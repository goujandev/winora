using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Winora;

[JsonConverter(typeof(JsonStringEnumConverter<TaskbarMode>))]
public enum TaskbarMode { Default, Transparent, Acrylic }

public sealed record UserSettings(TaskbarMode Mode = TaskbarMode.Default, bool StartWithWindows = false, bool AlwaysShowTrayIcons = false, bool DarkMode = false);

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
            return settings is not null && Enum.IsDefined(settings.Mode) ? settings : new();
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
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
        File.Move(temporary, path, overwrite: true);
    }
}
