using Winora;

internal static class FullscreenRoutingChecks
{
    public static void Run(Action<bool, string> check)
    {
        FullscreenRoutingMonitor[] monitors = [Monitor("A", true), Monitor("B"), Monitor("C")];
        var incoming = Enumerable.Range(1, 7).Select(id => Window(id, "A")).ToArray();
        var existing = Enumerable.Range(20, 3).Select(id => Window(id, "B")).ToArray();
        var plan = FullscreenRoutingPolicy.Plan(monitors, [.. incoming, .. existing], ["A"]);
        var onB = 3 + plan.Moves.Count(move => move.DestinationMonitorId == "B");
        var onC = plan.Moves.Count(move => move.DestinationMonitorId == "C");
        check(plan.Moves.Count == 7 && Math.Abs(onB - onC) <= 1 && plan.Moves.Select(move => move.Window.Id).Distinct().Count() == 7,
            "Background tiles are distributed by destination load without duplicate moves");
        check(plan.Moves.All(move => move.DestinationMonitorId != "A" && !move.IsRestore),
            "The fullscreen game monitor cannot be an evacuation destination");
        check(FullscreenRoutingPolicy.Plan([Monitor("A", true)], incoming, ["A"]).Moves.Count == 0,
            "A single-monitor desktop never hides or sends tiles off-screen");
        check(FullscreenRoutingPolicy.Plan([Monitor("A", true), Monitor("B", true), Monitor("C", true)], incoming, ["A"]).Moves.Count == 0,
            "No tiles move when every other monitor is occupied by a fullscreen app");
        check(FullscreenRoutingPolicy.Plan(monitors, incoming, []).Moves.Count == 0,
            "A fullscreen non-game does not trigger game-only evacuation");

        FullscreenRoutingWindow[] excluded =
        [
            Window(1, "A") with { IsGame = true }, Window(2, "A") with { IsForeground = true },
            Window(3, "A") with { IsModelFocused = true }, Window(4, "A") with { IsDragging = true },
            Window(5, "A") with { IsTiling = false }, Window(6, "A") with { IsVisible = false },
            Window(7, "A") with { ProcessStart = 0 }, Window(8, "A")
        ];
        plan = FullscreenRoutingPolicy.Plan(monitors, excluded, ["A"]);
        check(plan.Moves.Count == 1 && plan.Moves[0].Window.Id == excluded[^1].Id,
            "Games, foreground/model-focused apps, drags, floating/hidden windows and uncertain identities stay untouched");

        var coordinator = new FullscreenRoutingCoordinator();
        var original = Window(10, "A");
        var evacuation = coordinator.Plan(monitors, [original], ["A"]).Moves.Single();
        check(coordinator.Leases.Count == 0 && !coordinator.Complete(evacuation, original),
            "Planning or an unconfirmed move cannot create a restoration claim");
        var relocated = AtDestination(original, evacuation);
        check(coordinator.Complete(evacuation, relocated) && coordinator.Leases.Count == 1,
            "A confirmed move records the original monitor and the verified destination");
        var lease = coordinator.Leases.Single();
        FullscreenRoutingMonitor[] available = [Monitor("A"), Monitor("B"), Monitor("C")];
        var restoration = coordinator.Plan(available, [relocated], []).Moves.Single();
        check(restoration.IsRestore && restoration.DestinationMonitorId == "A" && coordinator.Leases.Count == 1,
            "Game-end restoration returns to the original monitor and retains its journal until confirmed");
        check(coordinator.Complete(restoration, AtDestination(relocated, restoration)) && coordinator.Leases.Count == 0,
            "A confirmed restoration clears its journal entry");

        foreach (var changed in new[]
        {
            relocated with { Handle = relocated.Handle + 1 },
            relocated with { ProcessId = relocated.ProcessId + 1 },
            relocated with { ProcessStart = relocated.ProcessStart + 1 }
        })
        {
            plan = FullscreenRoutingPolicy.Plan(available, [changed], [], [lease]);
            check(plan.Moves.Count == 0 && plan.RetainedLeases.Count == 0,
                "Reused handles or changed process identities cannot restore an unrelated window");
        }
        plan = FullscreenRoutingPolicy.Plan(available, [], [], [lease]);
        check(plan.Moves.Count == 0 && plan.RetainedLeases.Count == 0,
            "Closed windows are removed from the restoration journal");
        foreach (var changed in new[]
        {
            relocated with { MonitorId = "C", WorkspaceName = Workspace("C") },
            relocated with { WorkspaceName = "user-workspace" },
            relocated with { IsTiling = false }
        })
        {
            plan = FullscreenRoutingPolicy.Plan(available, [changed], [], [lease]);
            check(plan.Moves.Count == 0 && plan.RetainedLeases.Count == 0,
                "A user's monitor, workspace or floating-state change takes precedence over restoration");
        }
        plan = FullscreenRoutingPolicy.Plan(available, [relocated], [], [lease with { UserMoved = true }]);
        check(plan.Moves.Count == 0 && plan.RetainedLeases.Count == 0,
            "An explicit user-move marker prevents automatic restoration even within the same workspace");
        plan = FullscreenRoutingPolicy.Plan(available, [relocated with { IsForeground = true }], [], [lease]);
        check(plan.Moves.Count == 0 && plan.RetainedLeases.Count == 1,
            "The currently active relocated app is left in place until it can be restored without stealing focus");

        plan = FullscreenRoutingPolicy.Plan(monitors, incoming, ["A"], paused: true);
        check(plan.Moves.Count == 0, "Paused tiling prevents new monitor moves");
        plan = FullscreenRoutingPolicy.Plan(available, [relocated], [], [lease], paused: true);
        check(plan.Moves.Count == 0 && plan.RetainedLeases.Count == 1,
            "Paused tiling defers restoration while retaining the necessary state");
        plan = FullscreenRoutingPolicy.Plan(monitors, [relocated], ["A"], [lease], enabled: false);
        check(plan.Moves.Count == 1 && plan.Moves[0].IsRestore && plan.Moves[0].DestinationMonitorId == "A",
            "Explicitly switching protection off returns background apps while leaving the fullscreen game untouched");
        plan = FullscreenRoutingPolicy.Plan(monitors, [relocated with { IsForeground = true }, Window(50, "A")], ["A"], [lease], enabled: false);
        check(plan.Moves.Count == 0 && plan.RetainedLeases.Count == 1,
            "Switching protection off neither evacuates new tiles nor steals focus from a relocated active app");
        plan = FullscreenRoutingPolicy.Plan([Monitor("A", true), Monitor("B"), Monitor("C")], [relocated], [], [lease]);
        check(plan.Moves.Count == 0 && plan.RetainedLeases.Count == 1,
            "Game-end restoration waits while another fullscreen app occupies the original monitor");

        FullscreenRoutingMonitor[] overlapping = [Monitor("A", true), Monitor("B", true), Monitor("C")];
        plan = FullscreenRoutingPolicy.Plan(overlapping, [relocated], ["A", "B"], [lease]);
        var reroute = plan.Moves.Single();
        check(!reroute.IsRestore && reroute.DestinationMonitorId == "C" && reroute.Lease.OriginalMonitorId == "A",
            "A second fullscreen game reroutes an evacuated tile while preserving its first origin");
        plan = FullscreenRoutingPolicy.Plan([Monitor("B"), Monitor("C")], [relocated], [], [lease]);
        check(plan.Moves.Count == 0 && plan.RetainedLeases.Count == 1,
            "An unplugged origin never causes an off-screen move and remains eligible for later recovery");
        FullscreenRoutingMonitor[] reordered = [new("A", "monitor-3", false), Monitor("B"), new("C", "monitor-1", false)];
        plan = FullscreenRoutingPolicy.Plan(reordered, [relocated], [], [lease]);
        check(plan.Moves.Single().DestinationMonitorId == "A" && plan.Moves.Single().DestinationWorkspaceName == "monitor-3",
            "Restoration follows the original physical monitor after workspace indexes change");
        reordered = [new("A", "monitor-2", false), new("B", "monitor-1", false), Monitor("C")];
        var rebound = relocated with { WorkspaceName = "monitor-1" };
        coordinator = new FullscreenRoutingCoordinator([lease]);
        plan = coordinator.Plan(reordered, [rebound], []);
        check(plan.Moves.Count == 1 && plan.Moves[0].DestinationMonitorId == "A"
            && plan.Moves[0].DestinationWorkspaceName == "monitor-2"
            && coordinator.Leases.Single().DestinationWorkspaceName == "monitor-1",
            "A reindexed destination workspace preserves the route and updates the journal by physical monitor identity");
        plan = FullscreenRoutingPolicy.Plan(reordered, [relocated with { WorkspaceName = "user-workspace" }], [], [lease]);
        check(plan.Moves.Count == 0 && plan.RetainedLeases.Count == 0,
            "Topology rebinding does not override a move to an unowned workspace");

        coordinator = new FullscreenRoutingCoordinator([lease]);
        plan = coordinator.Plan(monitors, [relocated, Window(40, "A")], ["A"]);
        check(plan.Moves.Count == 1 && plan.Moves[0].Window.Id == Window(40, "A").Id && coordinator.Leases.Count == 1,
            "New tiles are evacuated during an existing game without moving already relocated apps repeatedly");
        coordinator.MarkUserMoved(relocated.Id);
        plan = coordinator.Plan(available, [relocated], []);
        check(plan.Moves.Count == 0 && coordinator.Leases.Count == 0,
            "The coordinator forgets an explicitly overridden relocation rather than restoring it later");

        coordinator = new FullscreenRoutingCoordinator([lease]);
        var corrected = relocated with { MonitorId = "C", WorkspaceName = Workspace("C") };
        check(coordinator.ObserveAutomaticPlacement(corrected)
            && coordinator.Leases.Single().OriginalMonitorId == "A"
            && coordinator.Plan(available, [corrected], []).Moves.Single().DestinationMonitorId == "A",
            "A verified layout correction retains the game's original monitor across automatic moves");
        var floated = corrected with { IsTiling = false, IsFloating = true };
        check(coordinator.ObserveAutomaticPlacement(floated)
            && coordinator.Plan(monitors, [floated], ["A"]).RetainedLeases.Single().AutomaticallyFloating,
            "Automatic floating preserves the origin while fullscreen prevents return");
        var floatReturn = coordinator.Plan(available, [floated], []).Moves.Single();
        check(floatReturn.IsRestore && coordinator.Complete(floatReturn, AtDestination(floated, floatReturn))
            && coordinator.Leases.Count == 0,
            "An automatically floated app can return to its origin without being forcibly retiled");
        coordinator = new FullscreenRoutingCoordinator([lease]);
        check(!coordinator.ObserveAutomaticPlacement(corrected with { ProcessStart = corrected.ProcessStart + 1 })
            && coordinator.Leases.Single() == lease,
            "Automatic placement cannot rebind a restoration journal to a replaced window");
    }

    private static FullscreenRoutingMonitor Monitor(string id, bool fullscreen = false) => new(id, Workspace(id), fullscreen);
    private static string Workspace(string monitor) => $"monitor-{monitor[0] - 'A' + 1}";
    private static FullscreenRoutingWindow Window(int id, string monitor) => new(
        Guid.Parse($"00000000-0000-0000-0000-{id:x12}"), 1000 + id, 2000 + id, 3000 + id, monitor, Workspace(monitor));
    private static FullscreenRoutingWindow AtDestination(FullscreenRoutingWindow window, FullscreenRoutingMove move) =>
        window with { MonitorId = move.DestinationMonitorId, WorkspaceName = move.DestinationWorkspaceName };
}
