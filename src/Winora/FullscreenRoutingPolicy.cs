namespace Winora;

public sealed record FullscreenRoutingMonitor(string Id, string WorkspaceName, bool IsFullscreen);

public sealed record FullscreenRoutingWindow(Guid Id, long Handle, int ProcessId, long ProcessStart,
    string MonitorId, string WorkspaceName, bool IsTiling = true, bool IsVisible = true,
    bool IsForeground = false, bool IsGame = false, bool IsModelFocused = false, bool IsDragging = false,
    bool IsFloating = false, string MonitorDevice = "");

public sealed record FullscreenRoutingLease(Guid Id, long Handle, int ProcessId, long ProcessStart,
    string OriginalMonitorId, string OriginalWorkspaceName, string DestinationMonitorId,
    string DestinationWorkspaceName, bool UserMoved = false, bool AutomaticallyFloating = false);

public sealed record FullscreenRoutingMove(FullscreenRoutingWindow Window, string DestinationMonitorId,
    string DestinationWorkspaceName, bool IsRestore, FullscreenRoutingLease Lease);

public sealed record FullscreenRoutingPlan(IReadOnlyList<FullscreenRoutingMove> Moves,
    IReadOnlyList<FullscreenRoutingLease> RetainedLeases);

/// <summary>Plans monitor moves without accessing Windows or commanding the tiling engine.</summary>
public static class FullscreenRoutingPolicy
{
    public static FullscreenRoutingPlan Plan(IReadOnlyCollection<FullscreenRoutingMonitor> monitors,
        IReadOnlyCollection<FullscreenRoutingWindow> windows, IReadOnlyCollection<string> gameMonitorIds,
        IReadOnlyCollection<FullscreenRoutingLease>? leases = null, bool enabled = true, bool paused = false)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(gameMonitorIds);
        var monitorById = monitors.ToDictionary(monitor => monitor.Id, StringComparer.OrdinalIgnoreCase);
        var windowById = windows.ToDictionary(window => window.Id);
        var protectedMonitors = gameMonitorIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retained = new List<FullscreenRoutingLease>();
        foreach (var lease in leases ?? [])
        {
            if (lease.UserMoved || !windowById.TryGetValue(lease.Id, out var window) || !SameIdentity(window, lease)
                || !(window.IsTiling || lease.AutomaticallyFloating && window.IsFloating)
                || !SameMonitor(window.MonitorId, lease.DestinationMonitorId)) continue;
            if (window.WorkspaceName == lease.DestinationWorkspaceName) retained.Add(lease);
            else if (monitorById.TryGetValue(lease.DestinationMonitorId, out var destination)
                && window.WorkspaceName == destination.WorkspaceName)
            {
                // Winora owns one displayed workspace per physical monitor. Its
                // index can change after hotplug without being a user override.
                retained.Add(lease with { DestinationWorkspaceName = destination.WorkspaceName });
            }
        }
        if (paused) return new([], retained);

        var moves = new List<FullscreenRoutingMove>();
        var planned = new HashSet<Guid>();
        var byWindow = retained.ToDictionary(lease => lease.Id);
        var loads = monitors.ToDictionary(monitor => monitor.Id,
            monitor => windows.Count(window => window.IsTiling && window.IsVisible && SameMonitor(window.MonitorId, monitor.Id)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var lease in retained.OrderBy(lease => lease.Id))
        {
            var window = windowById[lease.Id];
            if (!CanMove(window, lease.AutomaticallyFloating) || !monitorById.TryGetValue(lease.OriginalMonitorId, out var original)
                || (enabled && (original.IsFullscreen || protectedMonitors.Contains(original.Id)))) continue;
            // Workspace names follow monitor order; monitor identity survives
            // reconnection/reordering, so use its currently bound workspace.
            var move = new FullscreenRoutingMove(window, original.Id, original.WorkspaceName, true, lease);
            moves.Add(move);
            planned.Add(window.Id);
            AdjustLoads(loads, window.MonitorId, original.Id);
        }

        if (!enabled) return new(moves, retained);
        var targets = monitors.Where(monitor => !monitor.IsFullscreen && !protectedMonitors.Contains(monitor.Id))
            .OrderBy(monitor => monitor.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        if (targets.Length == 0) return new(moves, retained);

        foreach (var window in windows.OrderBy(window => window.Id))
        {
            if (planned.Contains(window.Id) || !protectedMonitors.Contains(window.MonitorId)
                || !monitorById.ContainsKey(window.MonitorId) || !CanMove(window)) continue;
            var target = targets.OrderBy(monitor => loads[monitor.Id])
                .ThenBy(monitor => monitor.Id, StringComparer.OrdinalIgnoreCase).First();
            var lease = byWindow.TryGetValue(window.Id, out var previous)
                ? previous with { DestinationMonitorId = target.Id, DestinationWorkspaceName = target.WorkspaceName }
                : new FullscreenRoutingLease(window.Id, window.Handle, window.ProcessId, window.ProcessStart,
                    window.MonitorId, window.WorkspaceName, target.Id, target.WorkspaceName);
            moves.Add(new(window, target.Id, target.WorkspaceName, false, lease));
            AdjustLoads(loads, window.MonitorId, target.Id);
        }
        return new(moves, retained);
    }

    internal static bool SameIdentity(FullscreenRoutingWindow window, FullscreenRoutingLease lease) =>
        window.Id == lease.Id && window.Handle == lease.Handle && window.ProcessId == lease.ProcessId
        && window.ProcessStart == lease.ProcessStart && window.Handle != 0 && window.ProcessId > 0 && window.ProcessStart > 0;

    internal static bool SameMonitor(string left, string right) => StringComparer.OrdinalIgnoreCase.Equals(left, right);

    internal static FullscreenRoutingLease? ObserveAutomaticPlacement(FullscreenRoutingLease lease, FullscreenRoutingWindow window)
        => SameIdentity(window, lease) && (window.IsTiling || window.IsFloating)
            ? lease with { DestinationMonitorId = window.MonitorId, DestinationWorkspaceName = window.WorkspaceName,
                AutomaticallyFloating = window.IsFloating } : null;

    internal static bool CanRestoreState(FullscreenRoutingWindow window, FullscreenRoutingLease lease)
        => window.IsTiling || lease.AutomaticallyFloating && window.IsFloating;

    private static bool CanMove(FullscreenRoutingWindow window, bool allowFloating = false)
        => (window.IsTiling || allowFloating && window.IsFloating) && window.IsVisible
        && !window.IsForeground && !window.IsGame && !window.IsModelFocused && !window.IsDragging
        && window.Handle != 0 && window.ProcessId > 0 && window.ProcessStart > 0;

    private static void AdjustLoads(Dictionary<string, int> loads, string source, string destination)
    {
        if (loads.ContainsKey(source)) loads[source] = Math.Max(0, loads[source] - 1);
        loads[destination]++;
    }
}

/// <summary>Tracks only moves confirmed by a fresh engine snapshot.</summary>
public sealed class FullscreenRoutingCoordinator
{
    private readonly Dictionary<Guid, FullscreenRoutingLease> leases;

    public FullscreenRoutingCoordinator(IEnumerable<FullscreenRoutingLease>? restoredLeases = null) =>
        leases = (restoredLeases ?? []).ToDictionary(lease => lease.Id);

    public IReadOnlyList<FullscreenRoutingLease> Leases => leases.Values.OrderBy(lease => lease.Id).ToArray();

    public FullscreenRoutingPlan Plan(IReadOnlyCollection<FullscreenRoutingMonitor> monitors,
        IReadOnlyCollection<FullscreenRoutingWindow> windows, IReadOnlyCollection<string> gameMonitorIds,
        bool enabled = true, bool paused = false)
    {
        var plan = FullscreenRoutingPolicy.Plan(monitors, windows, gameMonitorIds, Leases, enabled, paused);
        var retained = plan.RetainedLeases.Select(lease => lease.Id).ToHashSet();
        foreach (var id in leases.Keys.Where(id => !retained.Contains(id)).ToArray()) leases.Remove(id);
        foreach (var lease in plan.RetainedLeases) leases[lease.Id] = lease;
        return plan;
    }

    public bool Complete(FullscreenRoutingMove move, FullscreenRoutingWindow observedWindow)
    {
        if (!FullscreenRoutingPolicy.SameIdentity(observedWindow, move.Lease)
            || !FullscreenRoutingPolicy.CanRestoreState(observedWindow, move.Lease)
            || !FullscreenRoutingPolicy.SameMonitor(observedWindow.MonitorId, move.DestinationMonitorId)
            || observedWindow.WorkspaceName != move.DestinationWorkspaceName) return false;
        if (move.IsRestore) leases.Remove(move.Window.Id);
        else leases[move.Window.Id] = move.Lease;
        return true;
    }

    public void MarkUserMoved(Guid windowId)
    {
        if (leases.TryGetValue(windowId, out var lease)) leases[windowId] = lease with { UserMoved = true };
    }

    public bool ObserveAutomaticPlacement(FullscreenRoutingWindow observedWindow)
    {
        if (!leases.TryGetValue(observedWindow.Id, out var lease)
            || FullscreenRoutingPolicy.ObserveAutomaticPlacement(lease, observedWindow) is not { } updated) return false;
        leases[observedWindow.Id] = updated;
        return true;
    }
}
