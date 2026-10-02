using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Winora;

// Runs in the existing headless tiling helper, not in the settings window.
// The engine tree and the real HWND frames are reconciled independently: an
// app can accept management yet change its own size after the initial redraw.
internal sealed record TilingAutomaticChange(Guid Id, long Handle, int ProcessId, long ProcessStart, bool UserOverride = false,
    string MonitorId = "", string Workspace = "", bool Floating = false);

internal sealed class TilingLayoutController(Func<bool> engineAlive, ITilingNativeWindows? desktop = null,
    ITilingInputMonitor? inputMonitor = null, Func<TilingAutomaticChange, Task>? automaticChange = null,
    string? refreshPath = null) : IDisposable
{
    internal static string RefreshPath => Path.Combine(TilingService.DirectoryPath, "layout-refresh.json");
    private readonly ITilingNativeWindows native = desktop ?? new TilingNativeWindows();
    private readonly ITilingInputMonitor input = inputMonitor ?? new TilingInputMonitor();
    private readonly Dictionary<Guid, long> arrival = [];
    private readonly HashSet<Guid> pending = [];
    private readonly Dictionary<Guid, string> memberships = [];
    private readonly HashSet<Guid> defaultLayouts = [];
    private readonly Dictionary<Guid, Repair> repairs = [];
    private readonly HashSet<Guid> constrained = [];
    private readonly Dictionary<Guid, AutomaticFloat> automaticFloats = [];
    private long nextArrival;
    private bool initialized;
    private string refresh = "";

    private sealed record WindowState(Guid Id, long Handle, string State, Guid WorkspaceId,
        string Workspace, string Device, bool Focused, bool Dragging, TilingNativeWindow? Native,
        bool MaximizedState, bool PreviouslyFloating, bool ModelVisible, TilingRect ModelBounds, string MonitorIdentity);
    private sealed record WorkspaceState(Guid MonitorId, Guid Id, string Name, string Device,
        TilingNativeMonitor Monitor, double ScaleFactor, IReadOnlyList<WindowState> Windows, TilingLayoutNode? Template, string EngineFingerprint);
    private sealed record State(bool Paused, IReadOnlyList<WorkspaceState> Workspaces)
    {
        internal IEnumerable<WindowState> Windows => Workspaces.SelectMany(workspace => workspace.Windows);
    }
    private sealed record Repair(TilingRect Bounds, long LastAttempt, int Attempts);
    private sealed record AutomaticFloat(long Handle, int ProcessId, long ProcessStart, string Device, int MinWidth, int MinHeight,
        int? MaxWidth, int? MaxHeight, bool RefusedGeometry);

    public void Dispose() => input.Dispose();

    internal void ObserveRoutingMove(FullscreenRoutingWindow window)
    {
        if (automaticFloats.TryGetValue(window.Id, out var fallback) && fallback.Handle == window.Handle
            && fallback.ProcessId == window.ProcessId && fallback.ProcessStart == window.ProcessStart)
            automaticFloats[window.Id] = fallback with { Device = window.MonitorDevice };
    }

    internal async Task<string> ReconcileAsync(Func<string, Task<JsonDocument>> send,
        IReadOnlyList<NativeFullscreenWindow> fullscreen, UserSettings settings)
    {
        if (!engineAlive()) return "";
        if (native.IsAnyMoveSizeActive())
        {
            // Explicit manipulation of a floated app overrides our automatic
            // fallback. Leave user-floated windows outside recovery entirely.
            foreach (var (id, fallback) in automaticFloats.Where(pair => pair.Value.Handle == native.ForegroundHandle).ToArray())
            {
                automaticFloats.Remove(id); constrained.Remove(id);
                if (automaticChange is not null)
                    await automaticChange(new(id, fallback.Handle, fallback.ProcessId, fallback.ProcessStart, UserOverride: true));
            }
            return "";
        }
        var state = await ReadAsync(send);
        var liveIds = state.Windows.Select(window => window.Id).ToHashSet();
        foreach (var missing in arrival.Keys.Where(id => !liveIds.Contains(id)).ToArray())
        { arrival.Remove(missing); pending.Remove(missing); repairs.Remove(missing); constrained.Remove(missing); automaticFloats.Remove(missing); }
        constrained.RemoveWhere(id => !state.Windows.Any(window => window.Id == id && window.State == "floating"));
        foreach (var id in automaticFloats.Keys.Where(id => !state.Windows.Any(window => window.Id == id && window.State == "floating")).ToArray())
            automaticFloats.Remove(id);
        foreach (var window in state.Windows)
        {
            if (arrival.ContainsKey(window.Id)) continue;
            arrival.Add(window.Id, ++nextArrival);
            if (initialized && window.State == "tiling") pending.Add(window.Id);
        }
        initialized = true;
        if (state.Paused || state.Windows.Any(window => window.Dragging || window.Native?.IsMoveSizeActive == true)) return "";
        var restoredMaximized = false;
        foreach (var window in state.Windows.Where(window => window.State == "fullscreen" && window.MaximizedState
            && window.Native is { IsMaximized: true, IsVisible: true, IsMinimized: false, IsCloaked: false }
            && !fullscreen.Any(full => full.Handle == window.Handle)))
        {
            if (!engineAlive() || native.IsAnyMoveSizeActive()) break;
            if (window.Focused && native.ForegroundHandle != window.Handle) continue;
            if (!native.TryRead(window.Handle, out var current, queryConstraints: false) || !SameIdentity(window, current)
                || !current.IsMaximized || current.IsMoveSizeActive) continue;
            // GlazeWM represents ordinary maximise as fullscreen(maximized:true).
            // Its redraw restores the native window directly into its tile.
            var command = window.PreviouslyFloating ? "set-floating --centered=false" : "set-tiling";
            using var restored = await send($"command --id {window.Id:D} {command}");
            restoredMaximized = true;
        }
        if (restoredMaximized) state = await ReadAsync(send);
        var occupied = fullscreen.Select(window => window.MonitorDevice).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var force = ReadRefresh();
        if (force) defaultLayouts.UnionWith(state.Workspaces.Select(workspace => workspace.Id));

        foreach (var (id, fallback) in automaticFloats.ToArray())
        {
            var window = state.Windows.FirstOrDefault(window => window.Id == id);
            if (window?.Native is not { IsVisible: true, IsMinimized: false, IsCloaked: false } current) continue;
            if (!string.Equals(window.Device, fallback.Device, StringComparison.OrdinalIgnoreCase))
            { automaticFloats.Remove(id); constrained.Remove(id); continue; }
            var changedLimits = current.MinWidth != fallback.MinWidth || current.MinHeight != fallback.MinHeight
                || current.MaxWidth != fallback.MaxWidth || current.MaxHeight != fallback.MaxHeight;
            if (fallback.RefusedGeometry && !changedLimits) continue;
            if (window.Focused && native.ForegroundHandle != window.Handle) continue;
            var target = SelectDestination(state, window, occupied, settings.TilingGap, excludeCurrent: false);
            if (target is null) continue;
            if (target.Id != window.WorkspaceId && !await MoveAsync(send, window, target, state)) continue;
            using (var tiled = await send($"command --id {window.Id:D} set-tiling")) { }
            state = await ReadAsync(send);
            var recovered = state.Windows.FirstOrDefault(candidate => candidate.Id == id);
            if (recovered is not { State: "tiling", Native: { } observed } || !SameIdentity(window, observed)) continue;
            automaticFloats.Remove(id); constrained.Remove(id);
            await NotifyAutomaticChangeAsync(recovered);
        }

        // Only new windows are balanced. Existing/manual monitor placement and
        // fullscreen restoration must not trigger another redistribution.
        foreach (var id in pending.OrderBy(id => arrival.GetValueOrDefault(id)).Take(4).ToArray())
        {
            var window = state.Windows.FirstOrDefault(window => window.Id == id);
            if (window is null || !Eligible(window) || fullscreen.Any(full => full.Handle == window.Handle))
            { pending.Remove(id); continue; }
            if (window.Focused && native.ForegroundHandle != window.Handle) continue;
            var target = SelectDestination(state, window, occupied, settings.TilingGap, excludeCurrent: false);
            if (target is null) continue;
            if (target.Id == window.WorkspaceId) { pending.Remove(id); continue; }
            if (!await MoveAsync(send, window, target, state)) continue;
            pending.Remove(id);
            state = await ReadAsync(send);
        }

        var limited = 0;
        var deferred = false;
        foreach (var workspaceId in state.Workspaces.Select(workspace => workspace.Id).ToArray())
        {
            if (!engineAlive() || native.IsAnyMoveSizeActive()) break;
            var workspace = state.Workspaces.FirstOrDefault(workspace => workspace.Id == workspaceId);
            if (workspace is null || occupied.Contains(workspace.Device)) continue;
            var windows = OrderedTiles(workspace).ToArray();
            if (windows.Length == 0) { memberships.Remove(workspaceId); defaultLayouts.Remove(workspaceId); continue; }
            var gap = ScaledGap(settings.TilingGap, workspace.ScaleFactor);
            var area = Inset(workspace.Monitor.WorkRect, gap);
            if (area.Width <= 0 || area.Height <= 0) continue;
            // Geometry changes reflow the user's tree; only membership changes
            // and an explicit Rearrange choose a new default arrangement.
            var key = string.Join(',', windows.Select(window => window.Id));
            if (memberships.GetValueOrDefault(workspaceId) != key) defaultLayouts.Add(workspaceId);
            var preserve = !defaultLayouts.Contains(workspaceId);
            var limits = LayoutWindows(windows);
            var plan = preserve && workspace.Template is { } template
                ? TilingLayoutPolicy.Reflow(area, template, limits, gap)
                : TilingLayoutPolicy.Build(area, limits, gap);
            if (preserve && !plan.FitsAll) plan = TilingLayoutPolicy.Build(area, limits, gap);

            var partial = workspace.Windows.Any(window => window.State == "tiling" && window.ModelVisible && !Eligible(window));
            if (partial)
            {
                // Retain the unknown window's allocation in the engine tree.
                // Healthy neighbours can still be repaired inside their own
                // model rectangles without overlapping or moving that window.
                var cells = windows.Where(window => ValidModelBounds(window.ModelBounds, workspace.Monitor.WorkRect))
                    .Select(window => new TilingLayoutLeaf(window.Id, window.ModelBounds)).ToArray();
                var freshPartial = await ReadAsync(send);
                var currentPartial = freshPartial.Workspaces.FirstOrDefault(candidate => candidate.Id == workspace.Id);
                if (!freshPartial.Paused && currentPartial?.EngineFingerprint == workspace.EngineFingerprint)
                    limited += await RepairFramesAsync(send, currentPartial, cells);
                deferred |= cells.Length < windows.Length;
                state = freshPartial;
                continue;
            }

            // If an app's minimum cannot fit, try another available monitor.
            // Floating is the honest fallback; never make a sub-minimum speck.
            var overflow = plan.OverflowWindowIds.Concat(plan.Cells.Where(cell =>
            {
                var nativeLimits = windows.First(window => window.Id == cell.WindowId).Native!;
                return nativeLimits.MaxWidth is { } maxWidth && cell.Bounds.Width > maxWidth
                    || nativeLimits.MaxHeight is { } maxHeight && cell.Bounds.Height > maxHeight;
            }).Select(cell => cell.WindowId)).Distinct().ToArray();
            if (overflow.Length > 0)
            {
                foreach (var id in overflow)
                {
                    var window = windows.First(window => window.Id == id);
                    if (window.Focused && native.ForegroundHandle != window.Handle) { limited++; continue; }
                    var target = SelectDestination(state, window, occupied, settings.TilingGap, excludeCurrent: true);
                    if (target is not null && await MoveAsync(send, window, target, state)) continue;
                    if (await FloatSafelyAsync(send, window, workspace)) limited++;
                }
                state = await ReadAsync(send);
                memberships.Remove(workspaceId);
                continue;
            }

            var aligned = await TilingTreeReconciler.ReconcileAsync(workspace.Id, plan, send, workspace.EngineFingerprint, native);
            // Tree commands redraw asynchronously. Re-read state/identity after
            // them before considering any native correction.
            var fresh = await ReadAsync(send);
            var current = fresh.Workspaces.FirstOrDefault(candidate => candidate.Id == workspace.Id);
            if (fresh.Paused || current is null || native.IsAnyMoveSizeActive()
                || !OrderedTiles(current).Select(window => window.Id).ToHashSet().SetEquals(windows.Select(window => window.Id)))
            { state = fresh; continue; }
            if (aligned) { memberships[workspaceId] = key; defaultLayouts.Remove(workspaceId); }
            else { deferred = true; state = fresh; continue; }
            limited += await RepairFramesAsync(send, current, plan.Cells);
            state = fresh;
        }
        var count = Math.Max(limited, constrained.Count);
        return count > 0 ? $"{count} app{(count == 1 ? "" : "s")} could not be arranged"
            : deferred ? "Layout waiting for active windows" : "";
    }

    private async Task<int> RepairFramesAsync(Func<string, Task<JsonDocument>> send, WorkspaceState current,
        IReadOnlyList<TilingLayoutLeaf> cells)
    {
        var limited = 0;
        foreach (var cell in cells)
        {
            if (!engineAlive() || native.IsAnyMoveSizeActive()) break;
            var window = current.Windows.FirstOrDefault(window => window.Id == cell.WindowId);
            if (window is null || !Eligible(window) || window.Native is not { } actual) continue;
            if (Near(actual.FrameRect, cell.Bounds)) { repairs.Remove(window.Id); continue; }
            var now = Environment.TickCount64;
            var repair = repairs.GetValueOrDefault(window.Id);
            if (repair is not null && repair.Bounds == cell.Bounds && now - repair.LastAttempt < 220) continue;
            if (repair is { Attempts: >= 8 } && repair.Bounds == cell.Bounds)
            {
                // A restrictive app can repeatedly undo requested geometry.
                // Stop fighting it and expose its native limits instead.
                if (await FloatSafelyAsync(send, window, current, refusedGeometry: true)) limited++;
                continue;
            }
            var accepted = native.TrySetFrameBounds(actual, cell.Bounds);
            repairs[window.Id] = new(cell.Bounds, now, repair?.Bounds == cell.Bounds ? repair.Attempts + 1 : 1);
            if (!accepted) limited++;
        }
        return limited;
    }

    private WorkspaceState? SelectDestination(State state, WindowState window, HashSet<string> occupied,
        int gap, bool excludeCurrent)
    {
        var open = native.GetOpenWindows();
        var candidates = state.Workspaces.Where(workspace => !excludeCurrent || workspace.Id != window.WorkspaceId)
            .Select(workspace =>
            {
                var tiles = OrderedTiles(workspace).Where(candidate => candidate.Id != window.Id).Append(window).ToArray();
                var spacing = ScaledGap(gap, workspace.ScaleFactor);
                var area = Inset(workspace.Monitor.WorkRect, spacing);
                var fits = area.Width > 0 && area.Height > 0 && FitsNativeLimits(TilingLayoutPolicy.Build(area, LayoutWindows(tiles), spacing), tiles);
                return new TilingMonitor(workspace.MonitorId, workspace.Monitor.IsPrimary, workspace.Monitor.DisplayOrder,
                    open.Count(candidate => candidate.Handle != window.Handle && !candidate.IsMinimized
                        && string.Equals(candidate.MonitorDevice, workspace.Device, StringComparison.OrdinalIgnoreCase)),
                    occupied.Contains(workspace.Device), fits);
            }).ToArray();
        var chosen = TilingLayoutPolicy.SelectMonitor(candidates);
        return chosen is null ? null : state.Workspaces.First(workspace => workspace.MonitorId == chosen.Id);
    }

    private async Task<bool> MoveAsync(Func<string, Task<JsonDocument>> send, WindowState window,
        WorkspaceState target, State previous)
    {
        if (!engineAlive() || native.IsAnyMoveSizeActive() || !native.TryRead(window.Handle, out var before)
            || !SameIdentity(window, before)) return false;
        var wasForeground = native.ForegroundHandle == window.Handle;
        if (window.Focused && !wasForeground) return false;
        var preserveFocus = wasForeground && await input.RearmAsync();
        var inputVersion = input.Version;
        if (wasForeground && native.ForegroundHandle != window.Handle) return false;
        using (var response = await send($"command --id {window.Id:D} move --workspace {target.Name}")) { }
        var fresh = await ReadAsync(send);
        var moved = fresh.Windows.FirstOrDefault(candidate => candidate.Id == window.Id);
        if (moved is null || moved.WorkspaceId != target.Id || moved.Native is not { } observed
            || !SameIdentity(window, observed)) return false;
        await NotifyAutomaticChangeAsync(moved);
        // Upstream workspace moves reset focus in the source. Preserve the new
        // app's existing focus only when the user did not interact meanwhile and
        // the observed fallback belongs to that exact source workspace. Ignore
        // the engine's tagged dummy mouse event. Fresh hook installation and
        // the responsive pump are confirmed before every focused move.
        var foreground = native.ForegroundHandle;
        if (preserveFocus && !fresh.Paused && foreground != window.Handle && input.IsReliable && input.Version == inputVersion)
        {
            using var focused = await send("query focused");
            var current = focused.RootElement.GetProperty("data").GetProperty("focused");
            var type = current.GetProperty("type").GetString();
            var id = Guid.Parse(current.GetProperty("id").GetString()!);
            var sourceFallback = type == "window"
                ? previous.Windows.Any(candidate => candidate.Id == id && candidate.WorkspaceId == window.WorkspaceId
                    && candidate.Handle == foreground && fresh.Windows.Any(actual => actual.Id == id
                        && actual.Native is { } observedFallback && SameIdentity(candidate, observedFallback)))
                : type == "workspace" && id == window.WorkspaceId && native.IsDesktop(foreground);
            if (sourceFallback && engineAlive() && !native.IsAnyMoveSizeActive() && input.IsReliable
                && input.Version == inputVersion && native.ForegroundHandle == foreground)
            { using var focus = await send($"command --id {window.Id:D} focus --container-id {window.Id:D}"); }
        }
        return true;
    }

    private async Task<bool> FloatSafelyAsync(Func<string, Task<JsonDocument>> send, WindowState window, WorkspaceState workspace,
        bool refusedGeometry = false)
    {
        if (!engineAlive() || native.IsAnyMoveSizeActive() || !native.TryRead(window.Handle, out var actual)
            || !SameIdentity(window, actual) || actual.IsMoveSizeActive
            || window.Focused && native.ForegroundHandle != window.Handle) return false;
        using (var response = await send($"command --id {window.Id:D} set-floating --centered=false")) { }
        var fresh = await ReadAsync(send);
        var floating = fresh.Windows.FirstOrDefault(candidate => candidate.Id == window.Id);
        if (floating?.State != "floating" || floating.Native is not { } current || !SameIdentity(window, current)) return false;
        var work = workspace.Monitor.WorkRect;
        var width = Math.Max(current.MinWidth, Math.Max(TilingLayoutPolicy.SafetyMinimumWidth, current.FrameRect.Width));
        var height = Math.Max(current.MinHeight, Math.Max(TilingLayoutPolicy.SafetyMinimumHeight, current.FrameRect.Height));
        if (current.MaxWidth is { } maxWidth) width = Math.Min(width, maxWidth);
        if (current.MaxHeight is { } maxHeight) height = Math.Min(height, maxHeight);
        width = Math.Max(current.MinWidth, Math.Min(width, work.Width));
        height = Math.Max(current.MinHeight, Math.Min(height, work.Height));
        var bounds = new TilingRect(Math.Clamp(current.FrameRect.X, work.X, Math.Max(work.X, work.Right - width)),
            Math.Clamp(current.FrameRect.Y, work.Y, Math.Max(work.Y, work.Bottom - height)), width, height);
        var corrected = native.TrySetFrameBounds(current, bounds);
        repairs.Remove(window.Id);
        constrained.Add(window.Id);
        automaticFloats[window.Id] = new(window.Handle, current.ProcessId, current.ProcessStart, workspace.Device, current.MinWidth, current.MinHeight,
            current.MaxWidth, current.MaxHeight, refusedGeometry || !corrected);
        await NotifyAutomaticChangeAsync(floating);
        return true;
    }

    private Task NotifyAutomaticChangeAsync(WindowState window) => automaticChange is not null && window.Native is { } actual
        ? automaticChange(new(window.Id, window.Handle, actual.ProcessId, actual.ProcessStart,
            MonitorId: window.MonitorIdentity, Workspace: window.Workspace, Floating: window.State == "floating")) : Task.CompletedTask;

    private IEnumerable<WindowState> OrderedTiles(WorkspaceState workspace) => workspace.Windows.Where(Eligible)
        .OrderBy(window => arrival.GetValueOrDefault(window.Id, long.MaxValue));
    private static bool Eligible(WindowState window) => window.State == "tiling" && !window.Dragging
        && window.Native is { IsVisible: true, IsMinimized: false, IsCloaked: false, IsMaximized: false,
            IsMoveSizeActive: false, IsApplicationWindow: true };
    private static bool SameIdentity(WindowState window, TilingNativeWindow native) => window.Native is { } previous
        && native.Handle == window.Handle && native.ProcessId == previous.ProcessId && native.ProcessStart == previous.ProcessStart;
    private static IReadOnlyList<TilingLayoutWindow> LayoutWindows(IEnumerable<WindowState> windows) => windows
        .Select(window => new TilingLayoutWindow(window.Id, window.Native!.MinWidth, window.Native.MinHeight)).ToArray();
    private static bool FitsNativeLimits(TilingLayoutPlan plan, WindowState[] windows) => plan.FitsAll && plan.Cells.All(cell =>
    {
        var limits = windows.First(window => window.Id == cell.WindowId).Native!;
        return (limits.MaxWidth is null || cell.Bounds.Width <= limits.MaxWidth)
            && (limits.MaxHeight is null || cell.Bounds.Height <= limits.MaxHeight);
    });
    internal static int ScaledGap(int gap, double monitorScale) => (int)Math.Round(gap * monitorScale);
    private static TilingRect Inset(TilingRect bounds, int gap) => new(bounds.X + gap, bounds.Y + gap,
        bounds.Width - gap * 2, bounds.Height - gap * 2);
    private static bool Near(TilingRect left, TilingRect right) => Math.Abs(left.X - right.X) <= 3
        && Math.Abs(left.Y - right.Y) <= 3 && Math.Abs(left.Width - right.Width) <= 3 && Math.Abs(left.Height - right.Height) <= 3;

    private static bool ValidModelBounds(TilingRect bounds, TilingRect work) => bounds.Width > 0 && bounds.Height > 0
        && bounds.X >= work.X && bounds.Y >= work.Y && (long)bounds.X + bounds.Width <= work.Right
        && (long)bounds.Y + bounds.Height <= work.Bottom;

    private bool ReadRefresh()
    {
        var path = refreshPath ?? RefreshPath;
        var next = File.Exists(path) ? File.ReadAllText(path) : "";
        if (next == refresh) return false;
        refresh = next;
        return true;
    }
    private async Task<State> ReadAsync(Func<string, Task<JsonDocument>> send)
    {
        using var paused = await send("query paused");
        using var response = await send("query monitors");
        var monitors = native.GetMonitors().ToDictionary(monitor => monitor.Device, StringComparer.OrdinalIgnoreCase);
        var workspaces = new List<WorkspaceState>();
        foreach (var monitor in response.RootElement.GetProperty("data").GetProperty("monitors").EnumerateArray())
        {
            var device = monitor.GetProperty("deviceName").GetString() ?? "";
            if (!monitors.TryGetValue(device, out var physical)) continue;
            var monitorId = Guid.Parse(monitor.GetProperty("id").GetString()!);
            var identity = monitor.TryGetProperty("devicePath", out var path) && path.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(path.GetString()) ? path.GetString()! : device;
            var scale = monitor.GetProperty("scaleFactor").GetDouble();
            if (!double.IsFinite(scale) || scale is <= 0 or > 8)
                throw new InvalidOperationException("The tiling monitor scale is invalid.");
            foreach (var workspace in monitor.GetProperty("children").EnumerateArray())
            {
                if (workspace.GetProperty("type").GetString() != "workspace" || !workspace.GetProperty("isDisplayed").GetBoolean()) continue;
                var id = Guid.Parse(workspace.GetProperty("id").GetString()!);
                var name = workspace.GetProperty("name").GetString() ?? "";
                if (!Regex.IsMatch(name, @"\A[A-Za-z0-9_-]+\z")) throw new InvalidOperationException("The tiling workspace name is invalid.");
                var windows = new List<WindowState>();
                void Read(JsonElement node)
                {
                    if (node.GetProperty("type").GetString() == "window")
                    {
                        var handle = node.GetProperty("handle").GetInt64();
                        var windowState = node.GetProperty("state");
                        var state = windowState.GetProperty("type").GetString() ?? "";
                        native.TryRead(handle, out var actual, queryConstraints: state == "tiling" || state == "floating");
                        windows.Add(new(Guid.Parse(node.GetProperty("id").GetString()!), handle, state, id, name, device,
                            node.GetProperty("hasFocus").GetBoolean(), node.TryGetProperty("activeDrag", out var drag) && drag.ValueKind != JsonValueKind.Null,
                            actual, windowState.TryGetProperty("maximized", out var maximized) && maximized.GetBoolean(),
                            node.TryGetProperty("prevState", out var previous) && previous.ValueKind == JsonValueKind.Object
                                && previous.GetProperty("type").GetString() == "floating",
                            node.GetProperty("displayState").GetString() is "shown" or "showing",
                            new(node.GetProperty("x").GetInt32(), node.GetProperty("y").GetInt32(),
                                node.GetProperty("width").GetInt32(), node.GetProperty("height").GetInt32()), identity));
                    }
                    else if (node.TryGetProperty("children", out var children)) foreach (var child in children.EnumerateArray()) Read(child);
                }
                Read(workspace);
                workspaces.Add(new(monitorId, id, name, device, physical, scale, windows,
                    ReadTemplate(workspace, windows.Where(Eligible).Select(window => window.Id).ToHashSet(), physical.WorkRect),
                    TilingTreeReconciler.Fingerprint(workspace)));
            }
        }
        return new(paused.RootElement.GetProperty("data").GetBoolean(), workspaces);
    }

    private static TilingLayoutNode? ReadTemplate(JsonElement element, HashSet<Guid> eligible, TilingRect bounds)
    {
        if (element.GetProperty("type").GetString() == "window")
        {
            var id = Guid.Parse(element.GetProperty("id").GetString()!);
            return eligible.Contains(id) ? new TilingLayoutLeaf(id, bounds) : null;
        }
        if (!element.TryGetProperty("children", out var children)) return null;
        var nodes = new List<(TilingLayoutNode Node, double Weight)>();
        foreach (var child in children.EnumerateArray())
            if (ReadTemplate(child, eligible, bounds) is { } node)
            {
                var weight = child.TryGetProperty("tilingSize", out var size) && size.ValueKind == JsonValueKind.Number
                    ? size.GetDouble() : 1;
                nodes.Add((node, double.IsFinite(weight) && weight > 0 ? weight : .000001));
            }
        var direction = element.TryGetProperty("tilingDirection", out var axis) && axis.GetString() == "vertical"
            ? TilingSplitDirection.Vertical : TilingSplitDirection.Horizontal;
        TilingLayoutNode Group(int start, int count)
        {
            if (count == 1) return nodes[start].Node;
            var firstCount = count / 2;
            var firstWeight = nodes.Skip(start).Take(firstCount).Sum(node => node.Weight);
            var totalWeight = nodes.Skip(start).Take(count).Sum(node => node.Weight);
            return new TilingLayoutSplit(direction, firstWeight / totalWeight,
                Group(start, firstCount), Group(start + firstCount, count - firstCount), bounds);
        }
        return nodes.Count == 0 ? null : Group(0, nodes.Count);
    }
}
