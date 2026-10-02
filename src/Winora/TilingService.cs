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
            { await ChangeConfigurationAsync(gap); await RefreshFullscreenRoutingCoreAsync(); return; }
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
                var ready = false;
                try
                {
                    using var metadata = await SendAsync("query app-metadata");
                    var version = metadata.RootElement.GetProperty("data").GetProperty("version").GetString();
                    if (version != TilingProvisioner.EngineVersion)
                        throw new InvalidOperationException("The running tiling engine has an unexpected version.");
                    using var engine = FindOwnedProcess() ?? throw new InvalidOperationException("The tiling engine stopped during startup.");
                    var managed = await QueryManagedHandlesAsync();
                    SaveManagedHandles(engine, managed);
                    ready = true;
                }
                catch (Exception error) when (error is InvalidOperationException or WebSocketException or OperationCanceledException or JsonException)
                { lastError = error; }
                if (ready) { await RefreshFullscreenRoutingCoreAsync(); return; }
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
            await File.WriteAllTextAsync(TilingLayoutController.RefreshPath, Guid.NewGuid().ToString("D"));
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
            var message = paused ? "Paused" : "Tiling active";
            var responsive = true;
            var routing = FullscreenRoutingWorker.ReadStatus();
            using var worker = FindRoutingWorker(routing);
            if (routing?.Error == true) { message += $" · {routing.Message}"; responsive = false; }
            else if (worker is null || routing is { Stopped: true })
            { message += " · Layout checks stopped; retry to restart them"; responsive = false; }
            else if (!paused && !string.IsNullOrWhiteSpace(routing?.Message)) message += $" · {routing.Message}";
            return new(true, paused, message, Responsive: responsive);
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
        // Return routed apps while the manager still owns its tiling tree.
        // The ordinary pre-tiling placement restore below follows engine exit.
        await StopFullscreenRoutingCoreAsync(throwOnRestoreFailure: false);
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
        if (File.Exists(FullscreenRoutingWorker.JournalPath)) File.Delete(FullscreenRoutingWorker.JournalPath);
    }

    public async Task RefreshFullscreenRoutingAsync()
    {
        if (!AppBuild.AllowTilingEffects) return;
        await Changes.WaitAsync();
        try
        {
            // Register game exclusions in the engine before polling can see
            // their fullscreen surface. Its manage event precedes our worker.
            if (IsRunning) await ChangeConfigurationAsync(Settings.Load().TilingGap);
            await RefreshFullscreenRoutingCoreAsync();
        }
        finally { Changes.Release(); }
    }

    public Task RefreshApplicationRulesAsync() => RefreshFullscreenRoutingAsync();

    private static async Task RefreshFullscreenRoutingCoreAsync()
    {
        if (!AppBuild.AllowTilingEffects) return;
        using var engine = FindOwnedProcess();
        if (engine is null)
        { await StopFullscreenRoutingCoreAsync(throwOnRestoreFailure: true); return; }
        var previous = FullscreenRoutingWorker.ReadStatus();
        using (var existing = FindRoutingWorker(previous))
            if (existing is not null && previous is { Ready: true, Stopped: false, Error: false } &&
                previous.EngineProcessId == engine.Id && previous.EngineProcessStart == engine.StartTime.ToUniversalTime().Ticks) return;
        await StopFullscreenRoutingCoreAsync(throwOnRestoreFailure: false);
        if (File.Exists(FullscreenRoutingWorker.StopPath)) File.Delete(FullscreenRoutingWorker.StopPath);
        var appHost = Path.Combine(AppContext.BaseDirectory, AppBuild.IsDevelopment ? "Winora.Dev.exe" : "Winora.exe");
        var start = new ProcessStartInfo(appHost)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
        if (AppBuild.IsDevelopment) start.ArgumentList.Add("--test-tiling");
        start.ArgumentList.Add("--route-fullscreen");
        using var worker = Process.Start(start) ?? throw new InvalidOperationException("Windows couldn’t start fullscreen routing.");
        var started = worker.StartTime.ToUniversalTime().Ticks;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = FullscreenRoutingWorker.ReadStatus();
            if (state is not null && state.ProcessId == worker.Id && state.ProcessStart == started)
            {
                if (state.Error || state.Stopped) throw new InvalidOperationException(state.Message.Length > 0 ? state.Message : "Fullscreen routing stopped during startup.");
                using var verified = FindRoutingWorker(state);
                if (verified is not null && state.Ready) return;
            }
            if (worker.HasExited) throw new InvalidOperationException("Fullscreen routing couldn’t start. Check Winora’s error log and retry.");
            await Task.Delay(100);
        }
        // This Process was created directly above; no other instance is killed.
        if (!worker.HasExited) worker.Kill();
        throw new TimeoutException("Fullscreen routing took too long to start. Try again.");
    }

    private static async Task StopFullscreenRoutingCoreAsync(bool throwOnRestoreFailure)
    {
        var status = FullscreenRoutingWorker.ReadStatus();
        using var worker = FindRoutingWorker(status);
        if (worker is null)
        {
            using var engine = FindOwnedProcess();
            if (throwOnRestoreFailure && status is { Error: true } && engine is not null &&
                status.EngineProcessId == engine.Id && status.EngineProcessStart == engine.StartTime.ToUniversalTime().Ticks &&
                FullscreenRoutingWorker.HasPendingJournal(engine))
                throw new InvalidOperationException(status.Message);
            return;
        }
        FullscreenRoutingWorker.WriteStop(status!);
        if (!await WaitForExitAsync(worker, TimeSpan.FromSeconds(12)))
        {
            using var verified = FindRoutingWorker(status);
            if (verified is null || verified.Id != worker.Id)
                throw new InvalidOperationException("Winora couldn’t identify its fullscreen helper to stop it safely.");
            worker.Kill();
            if (!await WaitForExitAsync(worker, TimeSpan.FromSeconds(3)))
                throw new InvalidOperationException("Fullscreen routing is still closing. Try again.");
            if (throwOnRestoreFailure)
                throw new InvalidOperationException("Fullscreen routing stopped, but some apps couldn’t be returned. Retry, or turn tiling off to restore their original positions.");
        }
        else if (throwOnRestoreFailure && FullscreenRoutingWorker.ReadStatus() is { Error: true } finished)
            throw new InvalidOperationException(finished.Message);
    }

    private static Process? FindRoutingWorker(FullscreenRoutingStatus? status)
    {
        if (status is null || status.ProcessId <= 0 || status.ProcessStart <= 0 || string.IsNullOrWhiteSpace(status.Executable)) return null;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(status.ProcessId);
            using var current = Process.GetCurrentProcess();
            var expected = Path.Combine(AppContext.BaseDirectory, AppBuild.IsDevelopment ? "Winora.Dev.exe" : "Winora.exe");
            if (process.SessionId == current.SessionId && process.StartTime.ToUniversalTime().Ticks == status.ProcessStart &&
                string.Equals(Path.GetFullPath(status.Executable), expected, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(process.MainModule?.FileName, status.Executable, StringComparison.OrdinalIgnoreCase)) return process;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or IOException) { }
        process?.Dispose();
        return null;
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
        var settings = Settings.Load();
        await File.WriteAllTextAsync(temporary, TilingConfiguration.Build(gap, monitors,
            settings.FullscreenGameExecutables, settings.TiledAppExecutables));
        File.Move(temporary, ConfigurationPath, overwrite: true);
    }

    internal static Task<JsonDocument> SendAsync(string command) => SendCoreAsync(command);
    internal static Task<JsonDocument> SendBoundAsync(string command, EngineSession session) => session.SendAsync(command);

    // A retained kernel handle binds the worker to one verified process object,
    // including across PID reuse, without rescanning every process for each query.
    internal sealed class EngineSession : IDisposable
    {
        private readonly nint handle;
        private readonly SemaphoreSlim messages = new(1, 1);
        private readonly byte[] receiveBuffer = new byte[8192];
        private ClientWebSocket? socket;
        internal int ProcessId { get; }
        internal bool IsAlive => WaitForSingleObject(handle, 0) == 258 /* WAIT_TIMEOUT */;

        internal EngineSession(Process process)
        {
            ProcessId = process.Id;
            handle = OpenProcess(0x00101000 /* SYNCHRONIZE | QUERY_LIMITED_INFORMATION */, false, (uint)ProcessId);
            if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var path = new StringBuilder(1024);
                var length = path.Capacity;
                using var current = Process.GetCurrentProcess();
                if (!QueryFullProcessImageName(handle, 0, path, ref length) || !IsOwnedPath(path.ToString()) ||
                    !ProcessIdToSessionId((uint)ProcessId, out var session) || session != current.SessionId ||
                    !GetProcessTimes(handle, out var created, out _, out _, out _) ||
                    DateTime.FromFileTimeUtc(created).Ticks != process.StartTime.ToUniversalTime().Ticks || !IsAlive)
                    throw new InvalidOperationException("Winora couldn’t bind fullscreen routing to its own tiling engine.");
            }
            catch { _ = CloseHandle(handle); throw; }
        }

        internal async Task<JsonDocument> SendAsync(string command)
        {
            await messages.WaitAsync();
            try
            {
                if (!IsAlive) throw new InvalidOperationException("The tiling engine stopped.");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (socket is null)
                {
                    if (ListenerOwner() != ProcessId)
                        throw new InvalidOperationException("Winora couldn’t verify the tiling engine’s local connection.");
                    socket = new ClientWebSocket();
                    socket.Options.Proxy = null;
                    await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{IpcPort}"), timeout.Token);
                    if (ListenerOwner() != ProcessId || !IsAlive)
                        throw new InvalidOperationException("The tiling engine’s connection changed. Try again.");
                }
                // This connected socket is pinned to the verified server. Its
                // peer cannot turn into a new listener; the retained kernel
                // handle also prevents accepting a recycled process ID.
                var response = await SendReceiveAsync(socket, command, receiveBuffer, timeout.Token);
                if (!IsAlive) { response.Dispose(); throw new InvalidOperationException("The tiling engine stopped."); }
                return response;
            }
            catch { socket?.Dispose(); socket = null; throw; }
            finally { messages.Release(); }
        }

        public void Dispose()
        {
            socket?.Dispose();
            messages.Dispose();
            _ = CloseHandle(handle);
        }
    }

    private static async Task<JsonDocument> SendCoreAsync(string command)
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
        return await SendReceiveAsync(socket, command, new byte[8192], timeout.Token);
    }

    private static async Task<JsonDocument> SendReceiveAsync(ClientWebSocket socket, string command, byte[] buffer, CancellationToken cancellation)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(command).AsMemory(), WebSocketMessageType.Text, true, cancellation);
        using var data = new MemoryStream();
        ValueWebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(buffer.AsMemory(), cancellation);
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

    internal static Process? FindOwnedProcess()
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
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
