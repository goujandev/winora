using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Winora;

public sealed record TilingStatus(bool Running, bool Paused, string Message, bool Responsive = true);

public sealed class TilingService
{
    public static string DirectoryPath => Path.Combine(Settings.DirectoryPath, "tiling");
    public static string ConfigurationPath => Path.Combine(DirectoryPath, "config.yaml");
    public static string EngineDirectory => Path.Combine(DirectoryPath, "engine", TilingProvisioner.EngineVersion);
    public static string EngineExecutable => Path.Combine(EngineDirectory, "glazewm.exe");
    private static string SnapshotPath => Path.Combine(DirectoryPath, "window-positions.json");
    private static string ManagedPath => Path.Combine(DirectoryPath, "managed-windows.json");
    private static readonly SemaphoreSlim Changes = new(1, 1);
    private const int IpcPort = 6123;

    public bool IsRunning
    {
        get
        {
            if (!AppBuild.AllowTilingEffects) return false;
            using var process = FindOwnedProcess();
            return process is not null;
        }
    }

    public async Task EnableAsync(int gap, IProgress<string>? progress = null)
    {
        _ = TilingConfiguration.Build(gap);
        await Changes.WaitAsync();
        var starting = false;
        try
        {
            if (!AppBuild.AllowTilingEffects)
            { await WriteConfigurationAsync(gap, 1); return; }
            ThrowIfForeignManagerRunning();
            await TilingProvisioner.EnsureAsync(progress);
            using var existing = FindOwnedProcess();
            if (existing is not null)
            { await ChangeConfigurationAsync(gap); return; }
            ThrowIfForeignManagerRunning();
            if (ListenerOwner() is not null)
                throw new InvalidOperationException("Another application is using the tiling engine’s local connection. Close it before enabling tiling.");
            await WriteConfigurationAsync(gap, MonitorCount());
            SaveWindowPositions();
            if (File.Exists(ManagedPath)) File.Delete(ManagedPath);
            progress?.Report("Starting tiling…");
            var appHost = Path.Combine(AppContext.BaseDirectory, AppBuild.IsDevelopment ? "Winora.Dev.exe" : "Winora.exe");
            var start = new ProcessStartInfo(appHost)
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
            if (AppBuild.IsDevelopment) start.ArgumentList.Add("--test-tiling");
            start.ArgumentList.Add("--launch-tiling");
            starting = true;
            using (var launcher = Process.Start(start) ?? throw new InvalidOperationException("Windows couldn’t start tiling."))
            {
                using var launchTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await launcher.WaitForExitAsync(launchTimeout.Token); }
                catch (OperationCanceledException) { throw new TimeoutException("Windows took too long to start tiling."); }
                if (launcher.ExitCode != 0) throw new InvalidOperationException("The tiling engine couldn’t start. Check Winora’s error log and try again.");
            }
            Exception? lastError = null;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(150);
                try
                {
                    using var metadata = await SendAsync("query app-metadata");
                    var version = metadata.RootElement.GetProperty("data").GetProperty("version").GetString();
                    if (version != TilingProvisioner.EngineVersion)
                        throw new InvalidOperationException("The running tiling engine has an unexpected version.");
                    using var engine = FindOwnedProcess() ?? throw new InvalidOperationException("The tiling engine stopped during startup.");
                    var managed = await QueryManagedHandlesAsync();
                    SaveManagedHandles(engine, managed);
                    return;
                }
                catch (Exception error) when (error is InvalidOperationException or WebSocketException or OperationCanceledException or JsonException)
                { lastError = error; }
            }
            throw new InvalidOperationException("The tiling engine didn’t become ready. Try again, or use Exit from its tray icon.", lastError);
        }
        catch (Exception startupError) when (starting)
        {
            try { await DisableCoreAsync(); }
            catch (Exception cleanupError)
            {
                throw new InvalidOperationException("Tiling couldn’t start or restore Windows. Use Exit from the GlazeWM tray icon, then retry.",
                    new AggregateException(startupError, cleanupError));
            }
            throw;
        }
        finally { Changes.Release(); }
    }

    // A short-lived child handles the upstream signed shell-launch requirement.
    // Our explicit config stays isolated; GlazeWM owns its shared diagnostic log.
    internal static int LaunchEngine()
    {
        if (!AppBuild.AllowTilingEffects) throw new InvalidOperationException("Real tiling is disabled in this development preview.");
        ThrowIfForeignManagerRunning();
        using var existing = FindOwnedProcess();
        if (existing is not null) return 0;
        if (!File.Exists(ConfigurationPath) || !File.Exists(EngineExecutable))
            throw new InvalidOperationException("The tiling engine hasn’t been prepared.");
        // The signed upstream UIAccess executable must be shell launched, as
        // documented by GlazeWM. No runas verb, installer, or admin request.
        var start = new ProcessStartInfo(EngineExecutable)
        { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = EngineDirectory };
        start.ArgumentList.Add("start");
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(ConfigurationPath);
        start.ArgumentList.Add("--quiet");
        using var engine = Process.Start(start) ?? throw new InvalidOperationException("Windows couldn’t start the tiling engine.");
        return 0;
    }

    public async Task SetGapAsync(int gap)
    {
        _ = TilingConfiguration.Build(gap);
        await Changes.WaitAsync();
        try
        {
            if (!AppBuild.AllowTilingEffects) { await WriteConfigurationAsync(gap, 1); return; }
            ThrowIfForeignManagerRunning();
            if (IsRunning) await ChangeConfigurationAsync(gap);
            else await WriteConfigurationAsync(gap, MonitorCount());
        }
        finally { Changes.Release(); }
    }

    private static async Task ChangeConfigurationAsync(int gap)
    {
        var previous = File.Exists(ConfigurationPath) ? await File.ReadAllTextAsync(ConfigurationPath) : null;
        using var state = await SendAsync("query paused");
        var wasPaused = state.RootElement.GetProperty("data").GetBoolean();
        if (wasPaused) { using var resume = await SendAsync("command wm-toggle-pause"); }
        try
        {
            await WriteConfigurationAsync(gap, MonitorCount());
            using var response = await SendAsync("command wm-reload-config");
        }
        catch
        {
            if (previous is not null)
            {
                await File.WriteAllTextAsync(ConfigurationPath, previous);
                try { using var restored = await SendAsync("command wm-reload-config"); }
                catch (Exception) { /* Preserve the initial, actionable failure. */ }
            }
            throw;
        }
        finally
        {
            if (wasPaused) { using var pause = await SendAsync("command wm-toggle-pause"); }
        }
    }

    public async Task TogglePauseAsync()
    {
        if (!AppBuild.AllowTilingEffects) return;
        await Changes.WaitAsync();
        try { using var response = await SendAsync("command wm-toggle-pause"); }
        finally { Changes.Release(); }
    }

    public async Task RetileAsync()
    {
        if (!AppBuild.AllowTilingEffects) return;
        await Changes.WaitAsync();
        try
        {
            using var state = await SendAsync("query paused");
            if (state.RootElement.GetProperty("data").GetBoolean())
                throw new InvalidOperationException("Resume tiling before rearranging windows.");
            using var response = await SendAsync("command wm-redraw");
        }
        finally { Changes.Release(); }
    }

    public async Task<TilingStatus> GetStatusAsync()
    {
        if (!AppBuild.AllowTilingEffects) return new(false, false, "Development preview");
        if (!IsRunning) return new(false, false, "Tiling stopped");
        try
        {
            using var response = await SendAsync("query paused");
            var paused = response.RootElement.GetProperty("data").GetBoolean();
            return new(true, paused, paused ? "Paused" : "Tiling active");
        }
        catch (Exception error) when (error is InvalidOperationException or WebSocketException or OperationCanceledException or JsonException or Win32Exception)
        { return new(true, false, "Tiling isn’t responding. Use its tray icon to exit, then retry.", Responsive: false); }
    }

    public async Task DisableAsync()
    {
        if (!AppBuild.AllowTilingEffects) return;
        await Changes.WaitAsync();
        try { await DisableCoreAsync(); }
        finally { Changes.Release(); }
    }

    private static async Task DisableCoreAsync()
    {
        using var process = FindOwnedProcess();
        if (process is null) { await StopOwnedWatchersAsync(); return; }
        var managed = ReadManagedHandles(process);
        try
        {
            using var state = await SendAsync("query paused");
            if (state.RootElement.GetProperty("data").GetBoolean())
            { using var resume = await SendAsync("command wm-toggle-pause"); }
            managed.UnionWith(await QueryManagedHandlesAsync());
            try { SaveManagedHandles(process, managed); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Trace.WriteLine(error); }
            using var response = await SendAsync("command wm-exit");
        }
        catch (Exception error) when (error is InvalidOperationException or WebSocketException or OperationCanceledException or JsonException or Win32Exception)
        { /* The fallback below acts only on our positively identified process. */ }
        if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(5)))
        {
            if (!IsOwnedPath(process.MainModule?.FileName))
                throw new InvalidOperationException("Winora couldn’t verify the tiling process to stop it safely.");
            process.Kill();
            if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(3)))
                throw new InvalidOperationException("The tiling engine is still closing. Use Exit from its tray icon, then retry.");
        }
        await StopOwnedWatchersAsync();
        RestoreWindowPositions(managed);
        if (File.Exists(SnapshotPath)) File.Delete(SnapshotPath);
        if (File.Exists(ManagedPath)) File.Delete(ManagedPath);
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan wait)
    {
        if (process.HasExited) return true;
        using var timeout = new CancellationTokenSource(wait);
        try { await process.WaitForExitAsync(timeout.Token); return true; }
        catch (OperationCanceledException) { return false; }
    }

    private static async Task StopOwnedWatchersAsync()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var watcher in Process.GetProcessesByName("glazewm-watcher"))
        {
            using (watcher)
            {
                try
                {
                    var path = watcher.MainModule?.FileName;
                    if (watcher.SessionId != current.SessionId || path is null ||
                        !string.Equals(Path.GetFileName(path), "glazewm-watcher.exe", StringComparison.OrdinalIgnoreCase) ||
                        !IsOwnedPath(Path.Combine(Path.GetDirectoryName(path)!, "glazewm.exe"))) continue;
                    if (!await WaitForExitAsync(watcher, TimeSpan.FromSeconds(3)))
                    {
                        watcher.Kill();
                        if (!await WaitForExitAsync(watcher, TimeSpan.FromSeconds(3)))
                            throw new InvalidOperationException("The tiling recovery helper is still closing. Try again.");
                    }
                }
                catch (Exception error) when ((error is Win32Exception or InvalidOperationException) && watcher.HasExited) { }
            }
        }
    }

    private static async Task<HashSet<long>> QueryManagedHandlesAsync()
    {
        using var response = await SendAsync("query windows");
        return response.RootElement.GetProperty("data").GetProperty("windows").EnumerateArray()
            .Where(window => window.TryGetProperty("handle", out _)).Select(window => window.GetProperty("handle").GetInt64()).ToHashSet();
    }

    private sealed record ManagedWindows(int ProcessId, long ProcessStart, HashSet<long> Handles);

    private static void SaveManagedHandles(Process process, HashSet<long> handles)
    {
        var state = new ManagedWindows(process.Id, process.StartTime.ToUniversalTime().Ticks, handles);
        File.WriteAllText(ManagedPath, JsonSerializer.Serialize(state));
    }

    private static HashSet<long> ReadManagedHandles(Process process)
    {
        try
        {
            if (!File.Exists(ManagedPath)) return [];
            var state = JsonSerializer.Deserialize<ManagedWindows>(File.ReadAllText(ManagedPath));
            return state is not null && state.Handles is not null && state.ProcessId == process.Id && state.ProcessStart == process.StartTime.ToUniversalTime().Ticks
                ? state.Handles : [];
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    private static async Task WriteConfigurationAsync(int gap, int monitors)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = ConfigurationPath + ".tmp";
        await File.WriteAllTextAsync(temporary, TilingConfiguration.Build(gap, monitors));
        File.Move(temporary, ConfigurationPath, overwrite: true);
    }

    private static async Task<JsonDocument> SendAsync(string command)
    {
        using var process = FindOwnedProcess() ?? throw new InvalidOperationException("The tiling engine isn’t running.");
        if (ListenerOwner() != process.Id)
            throw new InvalidOperationException("Winora couldn’t verify the tiling engine’s local connection.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{IpcPort}"), timeout.Token);
        // Recheck after connecting: never command a different manager through
        // the upstream engine’s shared local IPC port.
        if (ListenerOwner() != process.Id || process.HasExited)
            throw new InvalidOperationException("The tiling engine’s connection changed. Try again.");
        await socket.SendAsync(Encoding.UTF8.GetBytes(command).AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        ValueWebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
            if (received.MessageType != WebSocketMessageType.Text)
                throw new InvalidOperationException("The tiling engine returned an unexpected response.");
            data.Write(buffer, 0, received.Count);
            if (data.Length > 4 * 1024 * 1024) throw new InvalidOperationException("The tiling engine’s response was too large.");
        } while (!received.EndOfMessage);
        var response = JsonDocument.Parse(data.ToArray());
        if (!response.RootElement.TryGetProperty("success", out var success) || !success.GetBoolean())
        {
            var error = response.RootElement.TryGetProperty("error", out var detail) ? detail.GetString() : null;
            response.Dispose();
            throw new InvalidOperationException(error ?? "The tiling command failed.");
        }
        return response;
    }

    private static Process? FindOwnedProcess()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName("glazewm"))
        {
            try
            {
                if (process.SessionId == current.SessionId && IsOwnedPath(process.MainModule?.FileName)) return process;
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
            process.Dispose();
        }
        return null;
    }

    private static void ThrowIfForeignManagerRunning()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var name in new[] { "glazewm", "komorebi" })
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try { if (name == "glazewm" && process.SessionId == current.SessionId && IsOwnedPath(process.MainModule?.FileName)) continue; }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
                throw new InvalidOperationException("Another window manager is running. Exit GlazeWM or Komorebi before enabling Winora’s tiling.");
            }
        }
    }

    private static bool IsOwnedPath(string? path)
    {
        if (path is null || !string.Equals(Path.GetFileName(path), "glazewm.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var version = Path.GetDirectoryName(Path.GetFullPath(path));
        return string.Equals(Path.GetDirectoryName(version), Path.Combine(DirectoryPath, "engine"), StringComparison.OrdinalIgnoreCase);
    }

    private static int? ListenerOwner()
    {
        var length = 0;
        var result = GetExtendedTcpTable(0, ref length, false, 2 /* AF_INET */, 3 /* OWNER_PID_LISTENER */, 0);
        if (result != 122 /* INSUFFICIENT_BUFFER */ && result != 0) throw new Win32Exception((int)result);
        var table = Marshal.AllocHGlobal(length);
        try
        {
            result = GetExtendedTcpTable(table, ref length, false, 2, 3, 0);
            if (result != 0) throw new Win32Exception((int)result);
            var count = Marshal.ReadInt32(table);
            for (var index = 0; index < count; index++)
            {
                var row = table + 4 + index * 24;
                var port = (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, 8));
                if (port == IpcPort) return Marshal.ReadInt32(row, 20);
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    private static int MonitorCount()
    {
        var count = 0;
        MonitorCallback callback = (_, _, _, _) => { count++; return true; };
        _ = EnumDisplayMonitors(0, 0, callback, 0);
        return Math.Clamp(count, 1, 64);
    }

    private sealed record WindowPosition(long Handle, int ProcessId, long ProcessStart, uint Flags, uint ShowCommand,
        int MinX, int MinY, int MaxX, int MaxY, int Left, int Top, int Right, int Bottom);

    private static void SaveWindowPositions()
    {
        var positions = new List<WindowPosition>();
        WindowCallback callback = (window, parameter) =>
        {
            if (!IsWindowVisible(window) || GetWindow(window, 4 /* GW_OWNER */) != 0) return true;
            _ = GetWindowThreadProcessId(window, out var id);
            if (id == 0) return true;
            var placement = new WindowPlacement { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
            if (!GetWindowPlacement(window, ref placement)) return true;
            try
            {
                using var process = Process.GetProcessById((int)id);
                positions.Add(new(window, (int)id, process.StartTime.ToUniversalTime().Ticks, placement.Flags, placement.ShowCommand,
                    placement.MinPosition.X, placement.MinPosition.Y, placement.MaxPosition.X, placement.MaxPosition.Y,
                    placement.NormalPosition.Left, placement.NormalPosition.Top, placement.NormalPosition.Right, placement.NormalPosition.Bottom));
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException) { }
            return true;
        };
        _ = EnumWindows(callback, 0);
        File.WriteAllText(SnapshotPath, JsonSerializer.Serialize(positions));
    }

    private static void RestoreWindowPositions(HashSet<long> managed)
    {
        if (!File.Exists(SnapshotPath)) return;
        var positions = JsonSerializer.Deserialize<List<WindowPosition>>(File.ReadAllText(SnapshotPath)) ?? [];
        foreach (var position in positions)
        {
            if (!managed.Contains(position.Handle) || !IsWindow((nint)position.Handle)) continue;
            _ = GetWindowThreadProcessId((nint)position.Handle, out var id);
            if (id != position.ProcessId) continue;
            try
            {
                using var process = Process.GetProcessById(position.ProcessId);
                if (process.StartTime.ToUniversalTime().Ticks != position.ProcessStart) continue;
                var placement = new WindowPlacement
                {
                    Length = (uint)Marshal.SizeOf<WindowPlacement>(), Flags = position.Flags, ShowCommand = position.ShowCommand,
                    MinPosition = new Point { X = position.MinX, Y = position.MinY },
                    MaxPosition = new Point { X = position.MaxX, Y = position.MaxY },
                    NormalPosition = new Rect { Left = position.Left, Top = position.Top, Right = position.Right, Bottom = position.Bottom }
                };
                _ = SetWindowPlacement((nint)position.Handle, in placement);
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException) { }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPlacement
    { public uint Length, Flags, ShowCommand; public Point MinPosition, MaxPosition; public Rect NormalPosition; }
    private delegate bool WindowCallback(nint window, nint parameter);
    private delegate bool MonitorCallback(nint monitor, nint dc, nint rect, nint parameter);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(nint table, ref int size, bool sorted, int family, int tableClass, uint reserved);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(nint window, ref WindowPlacement placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(nint window, in WindowPlacement placement);
}
