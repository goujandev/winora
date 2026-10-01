using System.IO;
using Microsoft.Win32;

namespace Winora;

/// <summary>One sign-in registration restores every enabled Winora feature.</summary>
public static class StartupService
{
    public const string StartupName = "Winora";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly string[] LegacyStartupNames = ["Winora.Taskbar", "Winora.TrayIcons"];

    public static void Synchronize(bool enabled)
    {
        if (AppBuild.IsDevelopment) return;
        var command = enabled ? BuildCommand(GetLauncherPath(AppContext.BaseDirectory)) : null;
        // Create the key only when enabling. Turning off never stops an active feature.
        using var key = enabled
            ? Registry.CurrentUser.CreateSubKey(RunKey)
            : Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;
        foreach (var change in BuildRegistrationChanges(enabled, command))
        {
            if (change.Value is null) key.DeleteValue(change.Key, throwOnMissingValue: false);
            else key.SetValue(change.Key, change.Value, RegistryValueKind.String);
        }
    }

    public static void Remove() => Synchronize(false);

    public static string GetLauncherPath(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var contentDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var executable = Path.Combine(contentDirectory, "Winora.exe");
        var root = Path.GetDirectoryName(contentDirectory);
        // Velopack replaces `current` during updates. Its root execution stub stays stable.
        // Require the installer marker as well as the stub, so an arbitrary directory named
        // current in a source checkout is not mistaken for an installed application.
        if (string.Equals(Path.GetFileName(contentDirectory), "current", StringComparison.OrdinalIgnoreCase)
            && root is not null && File.Exists(Path.Combine(root, "Update.exe"))
            && File.Exists(Path.Combine(root, "Winora.exe")))
            return Path.Combine(root, "Winora.exe");
        return executable;
    }

    public static string BuildCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (executablePath.Contains('"')) throw new ArgumentException("An executable path cannot contain quotes.", nameof(executablePath));
        return $"\"{Path.GetFullPath(executablePath)}\" --startup";
    }

    // Pure registration plan also makes it possible to verify migration without changing HKCU.
    public static IReadOnlyDictionary<string, string?> BuildRegistrationChanges(bool enabled, string? command)
    {
        if (enabled) ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var changes = new Dictionary<string, string?> { [StartupName] = enabled ? command : null };
        foreach (var legacyName in LegacyStartupNames) changes[legacyName] = null;
        return changes;
    }
}
