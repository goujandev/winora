using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Winora;

public sealed class TaskbarService
{
    public const string EngineVersion = "2026.2";
    public static string EngineDirectory => Path.Combine(Settings.DirectoryPath, "engine", EngineVersion);
    public static string EngineExecutable => Path.Combine(EngineDirectory, "TranslucentTB.exe");
    private const string StartupName = "Winora.Taskbar";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsRunning { get { using var process = FindOwnedProcess(); return process is not null; } }
    public string DescribeStatus() => IsRunning ? "Your taskbar appearance is active." : "Ready. Choose an appearance and apply it.";

    public async Task ApplyAsync(TaskbarMode mode, bool startWithWindows = false, IProgress<string>? progress = null)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        ThrowIfForeignEngineRunning();
        if (mode == TaskbarMode.Default) { await StopAsync(); SetStartup(false); return; }
        progress?.Report("Preparing the taskbar engine…");
        await EngineProvisioner.EnsureAsync(progress);
        using (var existing = FindOwnedProcess())
        {
            if (existing is not null && !string.Equals(existing.MainModule?.FileName, EngineExecutable, StringComparison.OrdinalIgnoreCase))
                await StopAsync();
        }
        var configPath = Path.Combine(EngineDirectory, "settings.json");
        var previous = File.Exists(configPath) ? await File.ReadAllTextAsync(configPath) : null;
        try
        {
            // The engine watches this file and reloads it without restarting Explorer.
            var temporary = configPath + ".tmp";
            await File.WriteAllTextAsync(temporary, BuildConfiguration(mode));
            File.Move(temporary, configPath, overwrite: true);
            if (!IsRunning)
            {
                progress?.Report("Starting taskbar effects…");
                using var process = Process.Start(new ProcessStartInfo(EngineExecutable)
                {
                    WorkingDirectory = EngineDirectory, UseShellExecute = false, CreateNoWindow = true
                }) ?? throw new InvalidOperationException("Windows couldn’t start the taskbar engine.");
                await Task.Delay(1800);
                if (process.HasExited) throw new InvalidOperationException($"The taskbar engine exited (code {process.ExitCode}). Check its log in {EngineDirectory}.");
            }
            else await Task.Delay(700);
            SetStartup(startWithWindows);
        }
        catch
        {
            if (previous is not null) await File.WriteAllTextAsync(configPath, previous);
            throw;
        }
    }

    public static string BuildConfiguration(TaskbarMode mode)
    {
        var accent = mode switch { TaskbarMode.Transparent => "clear", TaskbarMode.Acrylic => "acrylic", TaskbarMode.Default => "normal", _ => throw new ArgumentOutOfRangeException(nameof(mode)) };
        return JsonSerializer.Serialize(new
        {
            desktop_appearance = new { accent, color = mode == TaskbarMode.Acrylic ? "#20273770" : "#00000000", show_line = mode == TaskbarMode.Default },
            visible_window_appearance = new { enabled = false },
            maximized_window_appearance = new { enabled = false },
            start_opened_appearance = new { enabled = false },
            search_opened_appearance = new { enabled = false },
            task_view_opened_appearance = new { enabled = false },
            battery_saver_appearance = new { enabled = false },
            // Preserve the upstream tray icon as an independent exit/recovery route.
            hide_tray = false, disable_saving = true, verbosity = "warn", use_xaml_context_menu = false
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    public Task PrepareForUpdateAsync() => StopAsync();
    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(StartupName, $"\"{EngineExecutable}\"");
        else key.DeleteValue(StartupName, throwOnMissingValue: false);
    }

    public static async Task StopAsync()
    {
        using var process = FindOwnedProcess();
        if (process is null) return;
        var posted = false;
        bool Visit(nint window, nint parameter)
        {
            _ = GetWindowThreadProcessId(window, out var processId);
            var name = new StringBuilder(128);
            _ = GetClassName(window, name, name.Capacity);
            if (processId == process.Id && name.ToString() == "TrayWindow")
            { posted = PostMessage(window, 0x0010 /* WM_CLOSE */, 0, 0); return false; }
            return true;
        }
        _ = EnumWindows(Visit, 0);
        if (!posted)
        {
            var window = FindWindowEx(new nint(-3) /* HWND_MESSAGE */, 0, "TrayWindow", "TranslucentTB");
            if (window != 0) Visit(window, 0);
        }
        if (!posted) throw new InvalidOperationException("Couldn’t request a graceful taskbar restore. Use Exit on the TranslucentTB tray icon.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { throw new InvalidOperationException("The taskbar engine hasn’t finished restoring Windows. Close its dialogs and try again."); }
    }

    private static Process? FindOwnedProcess()
    {
        foreach (var process in Process.GetProcessesByName("TranslucentTB"))
        {
            try { if (IsOwnedPath(process.MainModule?.FileName)) return process; }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
            process.Dispose();
        }
        return null;
    }
    private static void ThrowIfForeignEngineRunning()
    {
        foreach (var process in Process.GetProcessesByName("TranslucentTB"))
        {
            using (process)
            {
                try { if (IsOwnedPath(process.MainModule?.FileName)) continue; }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
                throw new InvalidOperationException("Another copy of TranslucentTB is running. Exit it before applying Winora’s taskbar effects.");
            }
        }
    }
    private static bool IsOwnedPath(string? path)
    {
        if (path is null || !string.Equals(Path.GetFileName(path), "TranslucentTB.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var versionDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
        var engineRoot = Path.Combine(Settings.DirectoryPath, "engine");
        return string.Equals(Path.GetDirectoryName(versionDirectory), engineRoot, StringComparison.OrdinalIgnoreCase);
    }
    private delegate bool WindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint childAfter, string className, string windowName);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
