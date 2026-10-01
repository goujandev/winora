using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace Winora;

public sealed class TrayIconService
{
    public const string TrayKey = @"Control Panel\NotifyIconSettings";
    private const string PreferenceKey = @"Software\Winora\TrayIcons\Preference";
    private const string StateKey = @"Software\Winora\TrayIcons\State";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupName = "Winora.TrayIcons";
    private static string AgentRoot => Path.Combine(Settings.DirectoryPath, "tray-agent");
    private static string AgentPath => Path.Combine(AgentRoot,
        typeof(TrayIconService).Assembly.GetName().Version!.ToString(3), "Winora.TrayAgent.exe");

    public static bool IsSupported
    {
        get
        {
            if (AppBuild.IsDevelopment) return true;
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return false;
            try { using var key = Registry.CurrentUser.OpenSubKey(TrayKey); return key is not null; }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException) { return false; }
        }
    }

    public static string GetStatus()
    {
        if (AppBuild.IsDevelopment) return "Dev preview only. Windows tray visibility is unchanged.";
        try
        {
            if (!IsSupported) return "Tray automation is unavailable on this Windows configuration.";
            using var state = Registry.CurrentUser.OpenSubKey(StateKey);
            var error = state?.GetValue("LastError") as int? ?? 0;
            if (error != 0) return $"Tray automation needs attention (Windows error {error}).";
            using var process = FindAgent();
            return process is null ? "Tray automation stopped. Turn it off and on to retry." : "";
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { return "Windows prevented tray automation. Turn it off and on to retry."; }
    }

    public async Task EnableAsync()
    {
        if (AppBuild.IsDevelopment) return;
        if (!IsSupported) throw new InvalidOperationException("This Windows configuration does not expose app-tray visibility settings.");
        var source = Path.Combine(AppContext.BaseDirectory, "Winora.TrayAgent.exe");
        if (!File.Exists(source)) throw new FileNotFoundException("The tray helper is missing. Repair or update Winora.");
        using var running = FindAgent();
        if (running is not null && !string.Equals(running.MainModule?.FileName, AgentPath, StringComparison.OrdinalIgnoreCase))
            await DisableAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(AgentPath)!);
        if (!File.Exists(AgentPath)) File.Copy(source, AgentPath);
        else if (!SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(AgentPath))))
        {
            await DisableAsync();
            File.Copy(source, AgentPath, true);
        }
        using (var state = Registry.CurrentUser.CreateSubKey(StateKey)) state.SetValue("ProcessId", 0, RegistryValueKind.DWord);
        using (var preference = Registry.CurrentUser.CreateSubKey(PreferenceKey)) preference.SetValue("Enabled", 1, RegistryValueKind.DWord);
        // A dedicated sign-in entry persists independently of taskbar translucency.
        using (var startup = Registry.CurrentUser.CreateSubKey(RunKey)) startup.SetValue(StartupName, $"\"{AgentPath}\"");
        using var existing = FindAgent();
        if (existing is null)
        {
            using var launched = Process.Start(new ProcessStartInfo(AgentPath) { UseShellExecute = false, CreateNoWindow = true })
                ?? throw new InvalidOperationException("Windows could not start tray automation.");
        }
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(100);
            using var state = Registry.CurrentUser.OpenSubKey(StateKey);
            using var process = FindAgent();
            if (process is not null && state?.GetValue("ProcessId") is int pid && pid == process.Id)
            {
                var error = state.GetValue("LastError") as int? ?? 0;
                if (error != 0) { await DisableAsync(); throw new Win32Exception(error, "Windows could not apply app-tray visibility."); }
                return;
            }
        }
        await DisableAsync();
        throw new InvalidOperationException("Tray automation did not start. Try again or repair Winora.");
    }

    public static async Task DisableAsync()
    {
        if (AppBuild.IsDevelopment) return;
        using (var preference = Registry.CurrentUser.CreateSubKey(PreferenceKey)) preference.SetValue("Enabled", 0, RegistryValueKind.DWord);
        using (var startup = Registry.CurrentUser.OpenSubKey(RunKey, true)) startup?.DeleteValue(StartupName, false);
        using var process = FindAgent();
        if (process is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { throw new InvalidOperationException("Tray automation could not stop. Try again before changing visibility in Windows."); }
    }

    private static Process? FindAgent()
    {
        foreach (var process in Process.GetProcessesByName("Winora.TrayAgent"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path is not null && string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(path)), AgentRoot, StringComparison.OrdinalIgnoreCase))
                    return process;
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
            process.Dispose();
        }
        return null;
    }
}
