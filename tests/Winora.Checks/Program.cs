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
    Check(Settings.Load(temporary) == new UserSettings(), "Missing preferences use Windows defaults");
    File.WriteAllText(temporary, "{broken");
    Check(Settings.Load(temporary) == new UserSettings(), "Corrupt preferences use Windows defaults");
    File.WriteAllText(temporary, "{\"Mode\":\"FutureMode\"}");
    Check(Settings.Load(temporary) == new UserSettings(), "Unknown persisted mode is handled safely");
    Settings.Save(new UserSettings(TaskbarMode.Acrylic, true), temporary);
    Check(Settings.Load(temporary) == new UserSettings(TaskbarMode.Acrylic, true), "Appearance and startup preference survive reload");
    File.WriteAllText(temporary, "{\"Mode\":\"Transparent\",\"StartWithWindows\":true}");
    Check(Settings.Load(temporary) == new UserSettings(TaskbarMode.Transparent, true, false), "Existing preferences migrate with tray automation off");
    Settings.Save(Settings.Load(temporary) with { AlwaysShowTrayIcons = true }, temporary);
    Check(Settings.Load(temporary) == new UserSettings(TaskbarMode.Transparent, true, true), "Tray preference persists without changing taskbar finish or startup");
}
finally { if (File.Exists(temporary)) File.Delete(temporary); }

await ImmediateChangeChecks.Run(Check);

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
