using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Winora;

internal sealed record FullscreenRoutingStatus(int ProcessId, long ProcessStart, string Executable,
    bool Ready, bool Stopped, bool Error, string Message, int EngineProcessId = 0, long EngineProcessStart = 0);
internal sealed record FullscreenRoutingStop(int ProcessId, long ProcessStart);
internal sealed record FullscreenRoutingJournal(int EngineProcessId, long EngineProcessStart,
    IReadOnlyList<FullscreenRoutingLease> Leases);

// This mode deliberately creates no WPF Application or window. It shares the
// existing apphost/runtime and survives closing the production settings window.
internal static class FullscreenRoutingWorker
{
    internal static string StatusPath => Path.Combine(TilingService.DirectoryPath, "fullscreen-routing-status.json");
    internal static string StopPath => Path.Combine(TilingService.DirectoryPath, "fullscreen-routing-stop.json");
    internal static string JournalPath => Path.Combine(TilingService.DirectoryPath, "fullscreen-routing-journal.json");
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    private static TilingService.EngineSession? boundEngine;
    private static string? lastJournal;

    internal static Task<int> RunAsync() => Task.Run(() =>
    {
        if (!AppBuild.AllowTilingEffects) throw new InvalidOperationException("Fullscreen routing is disabled in this development preview.");
        using var instance = new Mutex(false, AppBuild.IsDevelopment ? @"Local\Winora.Dev.FullscreenRouting" : @"Local\Winora.FullscreenRouting");
        try { if (!instance.WaitOne(0)) return 0; }
        catch (AbandonedMutexException) { }
        // A named Mutex belongs to its acquiring thread. Hold it on this one
        // thread while the async polling/IPC loop uses the pool independently.
        try { return RunOwnedAsync().GetAwaiter().GetResult(); }
        finally { instance.ReleaseMutex(); }
    });

    private static async Task<int> RunOwnedAsync()
    {
        using var current = Process.GetCurrentProcess();
        using var engine = TilingService.FindOwnedProcess() ?? throw new InvalidOperationException("The tiling engine isn’t running.");
        using var connection = new TilingService.EngineSession(engine);
        boundEngine = connection;
        var status = new FullscreenRoutingStatus(current.Id, current.StartTime.ToUniversalTime().Ticks,
            current.MainModule?.FileName ?? throw new InvalidOperationException("Winora couldn’t identify its routing helper."),
            Ready: false, Stopped: false, Error: false, Message: "", EngineProcessId: engine.Id,
            EngineProcessStart: engine.StartTime.ToUniversalTime().Ticks);
        var leases = ReadJournal(engine);
        var detector = new FullscreenGameDetector();
        using var layout = new TilingLayoutController(() => EngineMatches(engine));
        var exitCode = 0;
        try
        {
            WriteStatus(status);
            using (var metadata = await SendAsync("query app-metadata"))
                if (metadata.RootElement.GetProperty("data").GetProperty("version").GetString() != TilingProvisioner.EngineVersion)
                    throw new InvalidOperationException("Fullscreen routing found an unexpected tiling engine version.");
            _ = await ReadSnapshotAsync([]);
            var initialSettings = Settings.Load();
            await layout.ReconcileAsync(SendAsync, detector.Detect(initialSettings.FullscreenGameExecutables), initialSettings);
            status = status with { Ready = true };
            WriteStatus(status);
            var signature = "";
            var changedAt = DateTimeOffset.UtcNow;
            var routingWasEnabled = initialSettings.MoveTilesForFullscreenGames;
            while (EngineMatches(engine) && !StopRequested(status))
            {
                var settings = Settings.Load();
                var fullscreen = detector.Detect(settings.FullscreenGameExecutables);
                var nextSignature = string.Join(";", fullscreen.OrderBy(window => window.Handle)
                    .Select(window => $"{window.Handle}:{window.ProcessStart}:{window.MonitorDevice}:{window.IsGame}"));
                if (signature != nextSignature) { signature = nextSignature; changedAt = DateTimeOffset.UtcNow; }
                // Enter/exit transitions settle before routing; short focus/mode
                // changes should not scatter apps and immediately bring them back.
                var message = "";
                if (!settings.MoveTilesForFullscreenGames && leases.Count > 0)
                {
                    var paused = (await ReadSnapshotAsync(fullscreen)).Paused;
                    if (routingWasEnabled || !paused) await RestoreAsync(engine, leases, detector);
                    WriteJournal(engine, leases);
                    if (leases.Count > 0) message = "Some focused apps stayed on their current monitor";
                }
                else if (settings.MoveTilesForFullscreenGames && DateTimeOffset.UtcNow - changedAt >= TimeSpan.FromSeconds(1)
                    && (fullscreen.Any(window => window.IsGame) || leases.Count > 0))
                {
                    var snapshot = await ReadSnapshotAsync(fullscreen);
                    var plan = FullscreenRoutingPolicy.Plan(snapshot.Monitors, snapshot.Windows,
                        snapshot.GameMonitors, leases, enabled: true, paused: snapshot.Paused);
                    leases = plan.RetainedLeases.ToList();
                    await ExecuteMovesAsync(plan.Moves, leases, engine, detector);
                    WriteJournal(engine, leases);
                    var gameSources = snapshot.GameMonitors.Count;
                    var blockedFocus = snapshot.Windows.Any(window => snapshot.GameMonitors.Contains(window.MonitorId) &&
                        window.IsTiling && !window.IsGame && (window.IsForeground || window.IsModelFocused));
                    var freeMonitor = snapshot.Monitors.Any(monitor => !monitor.IsFullscreen && !snapshot.GameMonitors.Contains(monitor.Id));
                    message = snapshot.Paused ? "Fullscreen routing paused" :
                        gameSources > 0 && !freeMonitor ? "Fullscreen game detected; no free monitor" :
                        blockedFocus ? "Some apps stay behind to keep game focus" :
                        gameSources == 0 && leases.Count > 0 ? "Apps waiting to return to their original monitor" :
                        leases.Count > 0 ? $"{leases.Count} app{(leases.Count == 1 ? "" : "s")} moved around fullscreen game" : "";
                }
                routingWasEnabled = settings.MoveTilesForFullscreenGames;
                var layoutMessage = await layout.ReconcileAsync(SendAsync, fullscreen, settings);
                if (layoutMessage.Length > 0) message = message.Length > 0 ? message + " · " + layoutMessage : layoutMessage;
                if (status.Message != message) { status = status with { Message = message }; WriteStatus(status); }
                await Task.Delay(Interval);
            }
        }
        catch (Exception error)
        {
            status = status with { Error = true, Message = $"Tiling checks stopped: {error.Message}" };
            exitCode = 1;
        }
        finally
        {
            var focusedResidual = false;
            try
            {
                if (EngineMatches(engine) && leases.Count > 0)
                {
                    await RestoreAsync(engine, leases, detector);
                    if (leases.Count > 0)
                    {
                        var remaining = await ReadSnapshotAsync(detector.Detect(Settings.Load().FullscreenGameExecutables));
                        focusedResidual = remaining.Windows.Any(window => leases.Any(lease => lease.Id == window.Id) &&
                            (window.IsForeground || window.IsModelFocused));
                    }
                }
            }
            catch (Exception error)
            {
                status = status with { Error = true, Message = $"Fullscreen routing couldn’t restore apps: {error.Message}" };
                exitCode = 1;
            }
            WriteJournal(engine, leases);
            if (!status.Error)
                status = status with { Message = leases.Count == 0 ? "" : focusedResidual ?
                    "Some focused apps stayed on their current monitor" : "Some apps stayed on their current monitor" };
            WriteStatus(status with { Stopped = true });
            boundEngine = null;
        }
        return exitCode;
    }

    private sealed record Snapshot(IReadOnlyList<FullscreenRoutingMonitor> Monitors,
        IReadOnlyList<FullscreenRoutingWindow> Windows, HashSet<string> GameMonitors, bool Paused);

    private static async Task<Snapshot> ReadSnapshotAsync(IReadOnlyList<NativeFullscreenWindow> fullscreen)
    {
        using var pausedResponse = await SendAsync("query paused");
        var paused = pausedResponse.RootElement.GetProperty("data").GetBoolean();
        using var response = await SendAsync("query monitors");
        var monitors = new List<FullscreenRoutingMonitor>();
        var windows = new List<FullscreenRoutingWindow>();
        var games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var foreground = NativeForeground();
        foreach (var monitor in response.RootElement.GetProperty("data").GetProperty("monitors").EnumerateArray())
        {
            var device = monitor.GetProperty("deviceName").GetString() ?? "";
            var id = monitor.TryGetProperty("devicePath", out var path) && path.ValueKind == JsonValueKind.String
                ? path.GetString()! : device;
            if (string.IsNullOrWhiteSpace(id)) id = device;
            var onMonitor = fullscreen.Where(window => string.Equals(window.MonitorDevice, device, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var workspace in monitor.GetProperty("children").EnumerateArray())
            {
                if (workspace.GetProperty("type").GetString() != "workspace" || !workspace.GetProperty("isDisplayed").GetBoolean()) continue;
                var name = workspace.GetProperty("name").GetString() ?? "";
                if (!Regex.IsMatch(name, @"\A[A-Za-z0-9_-]+\z"))
                    throw new InvalidOperationException("The owned tiling configuration has an unexpected workspace name.");
                monitors.Add(new(id, name, onMonitor.Length > 0));
                if (onMonitor.Any(window => window.IsGame)) games.Add(id);
                ReadWindows(workspace, id, name, fullscreen, foreground, windows);
            }
        }
        return new(monitors, windows, games, paused);
    }

    private static void ReadWindows(JsonElement container, string monitor, string workspace,
        IReadOnlyList<NativeFullscreenWindow> fullscreen, long foreground, List<FullscreenRoutingWindow> windows)
    {
        if (container.GetProperty("type").GetString() == "window")
        {
            var handle = container.GetProperty("handle").GetInt64();
            if (!FullscreenGameDetector.TryGetIdentity(handle, out var processId, out var processStart)) return;
            windows.Add(new(Guid.Parse(container.GetProperty("id").GetString()!), handle, processId, processStart, monitor, workspace,
                IsTiling: container.GetProperty("state").GetProperty("type").GetString() == "tiling",
                IsVisible: container.GetProperty("displayState").GetString() is "shown" or "showing",
                IsForeground: handle == foreground, IsGame: fullscreen.Any(window => window.Handle == handle && window.IsGame),
                IsModelFocused: container.GetProperty("hasFocus").GetBoolean(),
                IsDragging: container.TryGetProperty("activeDrag", out var drag) && drag.ValueKind != JsonValueKind.Null));
        }
        else if (container.TryGetProperty("children", out var children))
            foreach (var child in children.EnumerateArray()) ReadWindows(child, monitor, workspace, fullscreen, foreground, windows);
    }

    private static async Task ExecuteMovesAsync(IReadOnlyList<FullscreenRoutingMove> moves,
        List<FullscreenRoutingLease> leases, Process engine, FullscreenGameDetector detector, bool allowOccupiedDestinations = false)
    {
        foreach (var move in moves)
        {
            if (!EngineMatches(engine)) throw new InvalidOperationException("The tiling engine changed while routing apps.");
            // Native focus and the manager’s model can disagree for an ignored
            // game. Recheck both immediately before each specific-window move.
            var fullscreen = detector.Detect(Settings.Load().FullscreenGameExecutables);
            var before = await ReadSnapshotAsync(fullscreen);
            var window = before.Windows.FirstOrDefault(window => window.Id == move.Window.Id);
            if (window is null || window.Handle != move.Window.Handle || window.ProcessId != move.Window.ProcessId ||
                window.ProcessStart != move.Window.ProcessStart || window.WorkspaceName != move.Window.WorkspaceName ||
                !FullscreenRoutingPolicy.SameMonitor(window.MonitorId, move.Window.MonitorId) || window.IsGame ||
                !window.IsTiling || !window.IsVisible || window.IsForeground || window.IsModelFocused || window.IsDragging || before.Paused) continue;
            var destination = before.Monitors.FirstOrDefault(monitor => FullscreenRoutingPolicy.SameMonitor(monitor.Id, move.DestinationMonitorId));
            if (destination is null || destination.WorkspaceName != move.DestinationWorkspaceName ||
                (!allowOccupiedDestinations && destination.IsFullscreen)) continue;
            if (NativeForeground() == window.Handle || NativeForeground() == 0) continue;
            try
            { using var response = await SendAsync($"command --id {window.Id:D} move --workspace {move.DestinationWorkspaceName}"); }
            catch (InvalidOperationException) when (EngineMatches(engine))
            {
                var verify = await ReadSnapshotAsync(detector.Detect(Settings.Load().FullscreenGameExecutables));
                var surviving = verify.Windows.FirstOrDefault(candidate => candidate.Id == window.Id);
                if (surviving is null || surviving.Handle != window.Handle || surviving.ProcessId != window.ProcessId ||
                    surviving.ProcessStart != window.ProcessStart) continue;
                throw;
            }
            var after = await ReadSnapshotAsync(fullscreen);
            var observed = after.Windows.FirstOrDefault(candidate => candidate.Id == window.Id);
            if (observed is null || observed.Handle != window.Handle || observed.ProcessId != window.ProcessId ||
                observed.ProcessStart != window.ProcessStart || observed.MonitorId != move.DestinationMonitorId ||
                observed.WorkspaceName != move.DestinationWorkspaceName || !observed.IsTiling) continue;
            leases.RemoveAll(lease => lease.Id == observed.Id);
            if (!move.IsRestore) leases.Add(move.Lease);
            // Persist every acknowledged movement so a helper restart retains
            // the route origin even if another app opens or closes afterward.
            WriteJournal(engine, leases);
        }
    }

    private static async Task RestoreAsync(Process engine, List<FullscreenRoutingLease> leases,
        FullscreenGameDetector detector)
    {
        var fullscreen = detector.Detect(Settings.Load().FullscreenGameExecutables);
        var snapshot = await ReadSnapshotAsync(fullscreen);
        var paused = snapshot.Paused;
        if (paused) { using var resume = await SendAsync("command wm-toggle-pause"); }
        try
        {
            for (var pass = 0; pass < 3 && leases.Count > 0; pass++)
            {
                snapshot = await ReadSnapshotAsync(detector.Detect(Settings.Load().FullscreenGameExecutables));
                var plan = FullscreenRoutingPolicy.Plan(snapshot.Monitors, snapshot.Windows, [], leases, enabled: false, paused: false);
                leases.Clear();
                leases.AddRange(plan.RetainedLeases);
                var before = leases.Count;
                await ExecuteMovesAsync(plan.Moves, leases, engine, detector, allowOccupiedDestinations: true);
                if (leases.Count >= before) break;
            }
        }
        finally
        {
            if (paused && EngineMatches(engine)) { using var pause = await SendAsync("command wm-toggle-pause"); }
        }
    }

    private static bool EngineMatches(Process expected)
        => boundEngine is { IsAlive: true } session && session.ProcessId == expected.Id;

    private static Task<JsonDocument> SendAsync(string command) => TilingService.SendBoundAsync(command,
        boundEngine ?? throw new InvalidOperationException("Fullscreen routing isn’t connected to its tiling engine."));

    private static List<FullscreenRoutingLease> ReadJournal(Process engine)
    {
        try
        {
            var journal = JsonSerializer.Deserialize<FullscreenRoutingJournal>(File.ReadAllText(JournalPath));
            return journal is not null && journal.EngineProcessId == engine.Id && journal.EngineProcessStart == engine.StartTime.ToUniversalTime().Ticks
                ? journal.Leases?.ToList() ?? [] : [];
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    private static void WriteJournal(Process engine, IReadOnlyList<FullscreenRoutingLease> leases)
    {
        var text = JsonSerializer.Serialize(new FullscreenRoutingJournal(engine.Id, engine.StartTime.ToUniversalTime().Ticks, leases));
        if (text == lastJournal) return;
        WriteAtomicText(JournalPath, text);
        lastJournal = text;
    }

    private static bool StopRequested(FullscreenRoutingStatus status)
    {
        if (!File.Exists(StopPath)) return false;
        try
        {
            var stop = JsonSerializer.Deserialize<FullscreenRoutingStop>(File.ReadAllText(StopPath));
            return stop?.ProcessId == status.ProcessId && stop.ProcessStart == status.ProcessStart;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    internal static FullscreenRoutingStatus? ReadStatus()
    {
        try { return JsonSerializer.Deserialize<FullscreenRoutingStatus>(File.ReadAllText(StatusPath)); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    internal static bool HasPendingJournal(Process engine)
    {
        try
        {
            var journal = JsonSerializer.Deserialize<FullscreenRoutingJournal>(File.ReadAllText(JournalPath));
            return journal is not null && journal.EngineProcessId == engine.Id &&
                journal.EngineProcessStart == engine.StartTime.ToUniversalTime().Ticks && journal.Leases is { Count: > 0 };
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    internal static void WriteStop(FullscreenRoutingStatus status) => WriteAtomic(StopPath, new FullscreenRoutingStop(status.ProcessId, status.ProcessStart));
    private static void WriteStatus(FullscreenRoutingStatus status) => WriteAtomic(StatusPath, status);
    private static void WriteAtomic<T>(string path, T data)
        => WriteAtomicText(path, JsonSerializer.Serialize(data));
    private static void WriteAtomicText(string path, string text)
    {
        Directory.CreateDirectory(TilingService.DirectoryPath);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }

    private static long NativeForeground() => (long)GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
