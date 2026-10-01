using System.IO;
using Winora;

internal static class StartupChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"winora-startup-checks-{Guid.NewGuid():N}");
        var current = Path.Combine(temporary, "current");
        Directory.CreateDirectory(current);
        try
        {
            check(StartupService.GetLauncherPath(current) == Path.Combine(current, "Winora.exe"),
                "An unpackaged current directory uses its own executable");
            File.WriteAllText(Path.Combine(temporary, "Winora.exe"), "installer launcher fixture");
            check(StartupService.GetLauncherPath(current) == Path.Combine(current, "Winora.exe"),
                "A parent executable alone does not identify an installed application");
            File.WriteAllText(Path.Combine(temporary, "Update.exe"), "installer marker fixture");
            var launcher = StartupService.GetLauncherPath(current);
            check(launcher == Path.Combine(temporary, "Winora.exe"),
                "Installed startup uses the stable Velopack launcher outside current");
            var spacedPath = Path.Combine(temporary, "application folder", "Winora.exe");
            var command = StartupService.BuildCommand(spacedPath);
            check(command == $"\"{spacedPath}\" --startup",
                "Startup quotes executable paths and requests headless feature restoration");
            var enabled = StartupService.BuildRegistrationChanges(true, command);
            check(enabled.Count == 3 && enabled[StartupService.StartupName] == command
                && enabled["Winora.Taskbar"] is null && enabled["Winora.TrayIcons"] is null,
                "Enabling app startup creates one entry and removes both legacy feature entries");
            var disabled = StartupService.BuildRegistrationChanges(false, null);
            check(disabled.Count == 3 && disabled.Values.All(value => value is null),
                "Disabling app startup removes all Winora sign-in entries");
        }
        finally { Directory.Delete(temporary, recursive: true); }

        var calls = new List<string>();
        Task ApplyTaskbar(TaskbarMode mode) { calls.Add($"taskbar:{mode}"); return Task.CompletedTask; }
        Task EnableTray() { calls.Add("tray"); return Task.CompletedTask; }
        var enabledFeatures = new UserSettings(Mode: TaskbarMode.Acrylic, AlwaysShowTrayIcons: true);
        var failures = await FeatureStartup.RestoreAsync(enabledFeatures with { StartWinoraWithWindows = false }, ApplyTaskbar, EnableTray);
        check(calls.Count == 0 && failures.Count == 0,
            "Global startup off prevents restoration without changing enabled feature preferences");

        failures = await FeatureStartup.RestoreAsync(enabledFeatures, ApplyTaskbar, EnableTray);
        check(calls.SequenceEqual(new[] { "taskbar:Acrylic", "tray" }) && failures.Count == 0,
            "One startup restores every enabled feature using its saved configuration");

        calls.Clear();
        failures = await FeatureStartup.RestoreAsync(new UserSettings(), ApplyTaskbar, EnableTray);
        check(calls.Count == 0 && failures.Count == 0,
            "Startup launches no feature engines when Windows defaults are selected");
        failures = await FeatureStartup.RestoreAsync(new UserSettings(AlwaysShowTrayIcons: true), ApplyTaskbar, EnableTray);
        check(calls.SequenceEqual(new[] { "tray" }) && failures.Count == 0,
            "Tray automation restores independently of the selected taskbar finish");

        calls.Clear();
        var taskbarError = new IOException("Taskbar fixture failure");
        failures = await FeatureStartup.RestoreAsync(enabledFeatures,
            _ => Task.FromException(taskbarError), EnableTray);
        check(calls.SequenceEqual(new[] { "tray" }) && failures.Count == 1 && failures[0].InnerException == taskbarError,
            "A failed taskbar restoration still restores tray automation and preserves the error");

        var trayError = new IOException("Tray fixture failure");
        failures = await FeatureStartup.RestoreAsync(enabledFeatures,
            _ => Task.FromException(taskbarError), () => Task.FromException(trayError));
        check(failures.Count == 2 && failures[0].InnerException == taskbarError && failures[1].InnerException == trayError,
            "Independent startup failures are both reported for logging");
    }
}
