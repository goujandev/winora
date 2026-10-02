using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Winora;

var failures = 0;
void Check(bool condition, string name)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {name}");
    if (!condition) failures++;
}
Check(!AppBuild.IsDevelopment && AppBuild.Name == "Winora" && AppBuild.InstanceMutex == @"Local\Winora.Settings", "Production identity is unchanged");
Check(Settings.DirectoryPath.EndsWith("\\Winora", StringComparison.OrdinalIgnoreCase), "Production data directory is unchanged");
foreach (var mode in Enum.GetValues<TaskbarMode>())
{
    using var config = JsonDocument.Parse(TaskbarService.BuildConfiguration(mode));
    var root = config.RootElement;
    var expected = mode switch { TaskbarMode.Transparent => "clear", TaskbarMode.Acrylic => "acrylic", _ => "normal" };
    Check(root.GetProperty("desktop_appearance").GetProperty("accent").GetString() == expected, $"{mode} uses the correct native accent");
    Check(new[] { "visible_window_appearance", "maximized_window_appearance", "start_opened_appearance", "search_opened_appearance", "task_view_opened_appearance", "battery_saver_appearance" }
        .All(key => !root.GetProperty(key).GetProperty("enabled").GetBoolean()), $"{mode} is consistent across taskbar states");
}
try { TaskbarService.BuildConfiguration((TaskbarMode)99); Check(false, "Invalid native mode is rejected"); }
catch (ArgumentOutOfRangeException) { Check(true, "Invalid native mode is rejected"); }
var temporary = Path.Combine(Path.GetTempPath(), $"winora-checks-{Guid.NewGuid():N}.json");
try
{
    Check(Settings.Load(temporary) == new UserSettings() && Settings.Load(temporary).StartWinoraWithWindows,
        "Missing preferences enable app startup by default");
    Check(!Settings.Load(temporary).TilingEnabled && Settings.Load(temporary).TilingGap == 8,
        "Automatic tiling is opt-in and uses a restrained default gap");
    File.WriteAllText(temporary, "{broken");
    Check(Settings.Load(temporary) == new UserSettings(), "Corrupt preferences use Windows defaults");
    File.WriteAllText(temporary, "{\"Mode\":\"FutureMode\"}");
    Check(Settings.Load(temporary) == new UserSettings(), "Unknown persisted mode is handled safely");
    var explicitPreferences = new UserSettings(Mode: TaskbarMode.Acrylic, AlwaysShowTrayIcons: true, DarkMode: true, StartWinoraWithWindows: false);
    Settings.Save(explicitPreferences, temporary);
    Check(Settings.Load(temporary) == explicitPreferences, "Explicit app startup opt-out and enabled features survive reload");
    File.WriteAllText(temporary, "{\"Mode\":\"Transparent\",\"StartWithWindows\":true}");
    Check(Settings.Load(temporary) == new UserSettings(Mode: TaskbarMode.Transparent), "Legacy taskbar startup preferences migrate to default-on app startup");
    File.WriteAllText(temporary, "{\"Mode\":\"Transparent\",\"StartWithWindows\":false}");
    Check(Settings.Load(temporary) == new UserSettings(Mode: TaskbarMode.Transparent), "Legacy taskbar-only opt-out does not disable the new app preference");
    Settings.Save(Settings.Load(temporary) with { AlwaysShowTrayIcons = true }, temporary);
    Check(Settings.Load(temporary) == new UserSettings(Mode: TaskbarMode.Transparent, AlwaysShowTrayIcons: true), "Tray preference persists without changing taskbar finish or app startup");
    File.WriteAllText(temporary, "{\"Mode\":\"Acrylic\",\"StartWithWindows\":true,\"AlwaysShowTrayIcons\":true}");
    Check(Settings.Load(temporary) == new UserSettings(Mode: TaskbarMode.Acrylic, AlwaysShowTrayIcons: true), "Existing preferences preserve the finish and tray automation while enabling app startup");
    Settings.Save(Settings.Load(temporary) with { DarkMode = true }, temporary);
    Check(Settings.Load(temporary) == new UserSettings(Mode: TaskbarMode.Acrylic, AlwaysShowTrayIcons: true, DarkMode: true), "Dark app theme persists independently of taskbar, startup and tray preferences");
    File.WriteAllText(temporary, "{\"Mode\":\"Acrylic\",\"StartWithWindows\":true,\"StartWinoraWithWindows\":false,\"AlwaysShowTrayIcons\":true}");
    Check(!Settings.Load(temporary).StartWinoraWithWindows && Settings.Load(temporary).AlwaysShowTrayIcons,
        "A saved app startup opt-out takes precedence over legacy taskbar startup");
    Check(!Settings.Load(temporary).TilingEnabled && Settings.Load(temporary).TilingGap == 8,
        "Existing installations preserve their desktop by leaving the new tiling feature off");
    var tilingPreferences = new UserSettings(Mode: TaskbarMode.Acrylic, AlwaysShowTrayIcons: true,
        DarkMode: true, StartWinoraWithWindows: false, TilingEnabled: true, TilingGap: 20);
    Settings.Save(tilingPreferences, temporary);
    Check(Settings.Load(temporary) == tilingPreferences,
        "Tiling enablement and gap survive reload without changing existing preferences");
    File.WriteAllText(temporary, "{\"Mode\":\"Acrylic\",\"AlwaysShowTrayIcons\":true,\"DarkMode\":true,\"StartWinoraWithWindows\":false,\"TilingEnabled\":true,\"TilingGap\":-100}");
    Check(Settings.Load(temporary) == (tilingPreferences with { TilingGap = 0 }),
        "An invalid negative tiling gap is clamped while every other preference is retained");
    File.WriteAllText(temporary, "{\"Mode\":\"Acrylic\",\"AlwaysShowTrayIcons\":true,\"DarkMode\":true,\"StartWinoraWithWindows\":false,\"TilingEnabled\":true,\"TilingGap\":100}");
    Check(Settings.Load(temporary) == (tilingPreferences with { TilingGap = 32 }),
        "An excessive tiling gap is clamped without discarding existing feature preferences");
    Check(Settings.Load(temporary).MoveTilesForFullscreenGames && Settings.Load(temporary).FullscreenGameExecutables is null,
        "Existing tiling preferences enable fullscreen-game routing without guessing any game applications");
    var gamePath = Path.Combine(Path.GetTempPath(), "winora-game-test.exe");
    Settings.Save(tilingPreferences with { MoveTilesForFullscreenGames = false,
        FullscreenGameExecutables = [gamePath, gamePath.ToUpperInvariant(), "relative.exe", "", Path.ChangeExtension(gamePath, ".txt")] }, temporary);
    var routedPreferences = Settings.Load(temporary);
    Check(!routedPreferences.MoveTilesForFullscreenGames && routedPreferences.FullscreenGameExecutables?.Length == 1
        && string.Equals(routedPreferences.FullscreenGameExecutables[0], gamePath, StringComparison.OrdinalIgnoreCase)
        && (routedPreferences with { MoveTilesForFullscreenGames = true, FullscreenGameExecutables = null }) == tilingPreferences,
        "Configured games survive reload with absolute executable paths, case-insensitive deduplication and other preferences intact");
    Settings.Save(routedPreferences with { FullscreenGameExecutables = [] }, temporary);
    Check(Settings.Load(temporary).FullscreenGameExecutables is null,
        "Removing the last configured game returns to automatic exclusive-game detection");
    var customPath = Path.Combine(Path.GetTempPath(), "custom-window-app.exe");
    Settings.Save(tilingPreferences with { TiledAppExecutables = [customPath, customPath.ToUpperInvariant(), "relative.exe"] }, temporary);
    var appPreferences = Settings.Load(temporary);
    Check(appPreferences.TiledAppExecutables is { Length: 1 }
        && string.Equals(appPreferences.TiledAppExecutables[0], customPath, StringComparison.OrdinalIgnoreCase)
        && (appPreferences with { TiledAppExecutables = null }) == tilingPreferences,
        "Custom-window app choices persist independently with absolute, deduplicated executable paths");
}
finally { if (File.Exists(temporary)) File.Delete(temporary); }

await ImmediateChangeChecks.Run(Check);
await StartupChecks.Run(Check);
TilingChecks.Run(Check);
TilingLayoutChecks.Run(Check);
FullscreenRoutingChecks.Run(Check);

// Opt-in integration check changes the taskbar temporarily and restores it in finally.
if (args.Length == 2 && args[0] == "--engine")
{
    var output = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(output);
    var service = new TaskbarService();
    if (service.IsRunning) throw new InvalidOperationException("Exit Winora’s current taskbar engine before running the isolated integration check.");
    try
    {
        TaskbarCapture.Save(Path.Combine(output, "taskbar-default.png"));
        await service.ApplyAsync(TaskbarMode.Transparent, progress: new Progress<string>(Console.WriteLine));
        Check(service.IsRunning, "Transparent mode starts the native engine");
        TaskbarCapture.Save(Path.Combine(output, "taskbar-transparent.png"));
        using var engine = Process.GetProcessesByName("TranslucentTB").Single(p => string.Equals(p.MainModule?.FileName, TaskbarService.EngineExecutable, StringComparison.OrdinalIgnoreCase));
        var cpuBefore = engine.TotalProcessorTime;
        var stopwatch = Stopwatch.StartNew();
        await Task.Delay(10000);
        engine.Refresh();
        var measurements = new
        {
            EngineVersion = TaskbarService.EngineVersion,
            WorkingSetBytes = engine.WorkingSet64, PrivateBytes = engine.PrivateMemorySize64,
            IdleCpuPercentOneCore = (engine.TotalProcessorTime - cpuBefore).TotalSeconds / stopwatch.Elapsed.TotalSeconds * 100,
            MeasurementSeconds = stopwatch.Elapsed.TotalSeconds,
            OSVersion = Environment.OSVersion.Version.ToString()
        };
        File.WriteAllText(Path.Combine(output, "engine-measurements.json"), JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
        await service.ApplyAsync(TaskbarMode.Acrylic);
        Check(service.IsRunning, "Acrylic mode keeps the engine active");
        TaskbarCapture.Save(Path.Combine(output, "taskbar-acrylic.png"));
        Check(File.ReadAllText(Path.Combine(TaskbarService.EngineDirectory, "settings.json")).Contains("acrylic"), "Acrylic configuration reaches the native engine");
    }
    finally { await service.ApplyAsync(TaskbarMode.Default); }
    Check(!service.IsRunning, "Default mode gracefully stops the engine and requests restore");
    await Task.Delay(700);
    TaskbarCapture.Save(Path.Combine(output, "taskbar-restored.png"));
}
if (args.Length == 1 && args[0] == "--tray-live") TrayLiveCheck.Run();
return failures == 0 ? 0 : 1;
