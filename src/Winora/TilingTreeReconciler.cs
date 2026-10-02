using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Winora;

/// <summary>
/// Keeps the owned engine's split tree consistent with Winora's layout. All
/// operations target container IDs; none change focus or traverse monitors.
/// Native frame reconciliation remains the caller's responsibility.
/// </summary>
internal static class TilingTreeReconciler
{
    private const int MaximumWindows = 32;
    private const int MaximumCommands = 256;

    internal static async Task<bool> ReconcileAsync(Guid workspaceId, TilingLayoutPlan plan,
        Func<string, Task<JsonDocument>> send, string? expectedFingerprint = null, ITilingNativeWindows? native = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(send);
        if (workspaceId == Guid.Empty || plan.Cells.Count > MaximumWindows) return false;
        var desired = plan.Root is null ? null : Canonical(plan.Root);
        var ids = desired is null ? [] : Leaves(desired).ToArray();
        if (ids.Length != plan.Cells.Count || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length
            || !ids.ToHashSet().SetEquals(plan.Cells.Select(cell => cell.WindowId))) return false;
        // Public IPC cannot size deeper alternating ancestors by split ID.
        // Preserve an already correct manual tree, but never rebuild or change
        // its deep weights through window commands that target a nearer split.
        var deep = desired is DesiredGroup group && group.Children.Any(child => child is DesiredGroup nested
            && nested.Children.Any(grandchild => grandchild is DesiredGroup));

        try
        {
            var context = new Context(workspaceId, ids, send, expectedFingerprint, native);
            var current = await context.ReadAsync();
            if (current is null) return false;
            if (desired is null) return current.Children.Count == 0;
            if (deep) return Matches(desired, current) && WeightsAgree(desired, current);

            if (!Matches(desired, current))
            {
                var axis = desired is DesiredGroup rootGroup ? rootGroup.Direction : current.Direction;
                if (current.Direction != axis)
                {
                    current = await context.ChangeAsync(workspaceId, $"set-tiling-direction {Name(axis)}");
                    if (current is null || current.Direction != axis) return false;
                }
                current = await FlattenAsync(context, current, axis);
                if (current is null) return false;
                current = await OrderAsync(context, current, workspaceId, ids, axis);
                if (current is null) return false;
                if (desired is DesiredGroup root)
                    foreach (var child in root.Children.OfType<DesiredGroup>())
                    {
                        current = await BuildGroupAsync(context, current, child, axis);
                        if (current is null) return false;
                    }
                if (!Matches(desired, current)) return false;
            }

            if (desired is DesiredGroup sizedRoot)
            {
                current = await SizeGroupAsync(context, current, current.Id, sizedRoot);
                if (current is null) return false;
                for (var index = 0; index < sizedRoot.Children.Count; index++)
                    if (sizedRoot.Children[index] is DesiredGroup nested)
                    {
                        var modelGroup = current.Children[index];
                        current = await SizeGroupAsync(context, current, modelGroup.Id, nested);
                        if (current is null) return false;
                    }
            }
            return Matches(desired, current);
        }
        catch (DeferredException) { return false; }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (KeyNotFoundException) { return false; }
        catch (FormatException) { return false; }
    }

    private static async Task<Node?> FlattenAsync(Context context, Node? workspace, TilingSplitDirection axis)
    {
        if (workspace is null) return null;
        while (workspace.Windows().Any(window => window.Parent?.Id != workspace.Id))
        {
            // A first child has no previous tiling sibling to swap with. With
            // a split parent, movement towards the root's start can only lift
            // it to an ancestor; it cannot enter a neighbouring monitor.
            var candidate = workspace.Windows().Where(window => window.Parent?.Id != workspace.Id
                && window.Parent?.Children.FirstOrDefault()?.Id == window.Id)
                .OrderByDescending(window => window.Depth).FirstOrDefault();
            if (candidate?.Parent is not { IsWorkspace: false } parent) return null;
            var oldDepth = candidate.Depth;
            if (parent.Children.Count == 1)
            {
                // move pre-flattens singleton splits, then could move across
                // monitors. Direction toggling flattens only the singleton.
                workspace = await context.ChangeAsync(candidate.Id,
                    $"set-tiling-direction {Name(Opposite(parent.Direction))}", fresh =>
                        fresh.Find(candidate.Id)?.Parent is { IsWorkspace: false } actual
                        && actual.Id == parent.Id && actual.Direction == parent.Direction && actual.Children.Count == 1);
            }
            else workspace = await context.ChangeAsync(candidate.Id, $"move --direction {Start(axis)}", fresh =>
                fresh.Direction == axis && fresh.Find(candidate.Id)?.Parent is { IsWorkspace: false } actual
                && actual.Id == parent.Id && actual.Children.Count > 1 && actual.Children[0].Id == candidate.Id);
            if (workspace is null || workspace.Find(candidate.Id)?.Depth >= oldDepth) return null;
        }
        return workspace;
    }

    private static async Task<Node?> OrderAsync(Context context, Node? workspace, Guid parentId,
        IReadOnlyList<Guid> ids, TilingSplitDirection axis)
    {
        if (workspace is null) return null;
        for (var index = 0; index < ids.Count; index++)
        {
            while (true)
            {
                var parent = workspace.Find(parentId);
                if (parent is null || parent.Direction != axis || parent.Children.Count != ids.Count
                    || parent.Children.Any(child => !child.IsWindow)
                    || !parent.Children.Select(child => child.Id).ToHashSet().SetEquals(ids)) return null;
                var position = parent.Children.FindIndex(child => child.Id == ids[index]);
                if (position == index) break;
                // The requested position is never beyond an already ordered
                // prefix. A previous sibling is required before every move.
                if (position <= index || position == 0) return null;
                workspace = await context.ChangeAsync(ids[index], $"move --direction {Start(axis)}", fresh =>
                    fresh.Find(parentId) is { } actual && actual.Direction == axis && actual.Children.Count == ids.Count
                    && actual.Children.All(child => child.IsWindow)
                    && actual.Children.Select(child => child.Id).ToHashSet().SetEquals(ids)
                    && actual.Children.FindIndex(child => child.Id == ids[index]) == position && position > 0);
                if (workspace is null || workspace.Find(parentId)?.Children
                        .FindIndex(child => child.Id == ids[index]) != position - 1) return null;
            }
        }
        return workspace;
    }

    private static async Task<Node?> BuildGroupAsync(Context context, Node? workspace, DesiredGroup group,
        TilingSplitDirection rootAxis)
    {
        if (workspace is null) return null;
        if (group.Direction == rootAxis || group.Children.Any(child => child is not DesiredLeaf)) return null;
        var ids = Leaves(group).ToArray();
        var anchor = workspace.Find(ids[0]);
        if (anchor?.Parent?.Id != workspace.Id) return null;
        var firstIndex = workspace.Children.FindIndex(child => child.Id == anchor.Id);
        if (firstIndex < 0 || !workspace.Children.Skip(firstIndex).Take(ids.Length)
                .Select(child => child.Id).SequenceEqual(ids)) return null;
        workspace = await context.ChangeAsync(anchor.Id, $"set-tiling-direction {Name(group.Direction)}", fresh =>
            fresh.Direction == rootAxis && fresh.Children.Count > 1 && fresh.Find(anchor.Id)?.Parent?.Id == fresh.Id
            && fresh.Children.Skip(firstIndex).Take(ids.Length).Select(child => child.Id).SequenceEqual(ids));
        if (workspace is null) return null;
        var created = workspace.Find(anchor.Id)?.Parent;
        if (created is null || created.IsWorkspace || created.Parent?.Id != workspace.Id
            || created.Direction != group.Direction || created.Children.Count != 1) return null;
        var groupId = created.Id;
        for (var index = 1; index < ids.Length; index++)
        {
            var window = workspace.Find(ids[index]);
            var position = workspace.Children.FindIndex(child => child.Id == ids[index]);
            // Previous sibling is the specific split we just constructed.
            // This avoids movement at an edge, focus commands, and ambiguous
            // ancestor traversal through some unrelated split.
            if (window?.Parent?.Id != workspace.Id || position <= 0
                || workspace.Children[position - 1].Id != groupId) return null;
            workspace = await context.ChangeAsync(window.Id, $"move --direction {Start(rootAxis)}", fresh =>
                fresh.Direction == rootAxis && fresh.Find(window.Id)?.Parent?.Id == fresh.Id
                && fresh.Children.FindIndex(child => child.Id == window.Id) == position && position > 0
                && fresh.Children[position - 1].Id == groupId
                && fresh.Find(groupId) is { } actual && actual.Direction == group.Direction
                && actual.Windows().Select(leaf => leaf.Id).ToHashSet().SetEquals(ids.Take(index)));
            if (workspace is null) return null;
            var actual = workspace.Find(groupId);
            if (workspace.Find(window.Id)?.Parent?.Id != groupId || actual is null
                || !actual.Windows().Select(leaf => leaf.Id).ToHashSet().SetEquals(ids.Take(index + 1))) return null;
        }
        // The perpendicular split's insertion index follows its internal
        // focus order. Restore identity order using adjacent sibling swaps.
        return await OrderAsync(context, workspace, groupId, ids, group.Direction);
    }

    private static async Task<Node?> SizeGroupAsync(Context context, Node? workspace, Guid groupId, DesiredGroup desired)
    {
        if (workspace is null) return null;
        var lengths = desired.Children.Select(child => desired.Direction == TilingSplitDirection.Horizontal
            ? child.Bounds.Width : child.Bounds.Height).ToArray();
        var total = lengths.Sum(length => (long)length);
        if (total <= 0) return null;
        var weights = lengths.Select(length => (double)length / total).ToArray();
        // Sibling redistribution is proportional to space above the engine's
        // minimum fraction. A short bounded correction loop converges without
        // continually rebuilding a tree that already has the correct shape.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var group = workspace.Find(groupId);
            if (group is null || group.Direction != desired.Direction || group.Children.Count != weights.Length) return null;
            var tolerance = 1.5 / Math.Max(1, total);
            var errors = group.Children.Select((child, index) => child.TilingSize is { } fraction
                ? Math.Abs(fraction - weights[index]) : double.PositiveInfinity).ToArray();
            if (errors.All(error => error <= tolerance)) return workspace;
            var changed = false;
            foreach (var index in Enumerable.Range(0, weights.Length).OrderByDescending(index => errors[index]))
            {
                group = workspace.Find(groupId);
                if (group is null || group.Children.Count != weights.Length) return null;
                var child = group.Children[index];
                if (child.TilingSize is not { } fraction) return null;
                if (Math.Abs(fraction - weights[index]) <= tolerance) continue;
                var target = child.Windows().FirstOrDefault();
                if (target is null) return null;
                var axis = desired.Direction == TilingSplitDirection.Horizontal ? "width" : "height";
                var percent = (weights[index] * 100).ToString("0.######", CultureInfo.InvariantCulture);
                var childIds = group.Children.Select(node => node.Id).ToArray();
                workspace = await context.ChangeAsync(target.Id, $"size --{axis} {percent}%", fresh =>
                    fresh.Find(groupId) is { } actual && actual.Direction == desired.Direction
                    && actual.Children.Select(node => node.Id).SequenceEqual(childIds)
                    && actual.Children[index].Windows().Any(window => window.Id == target.Id));
                if (workspace is null) return null;
                changed = true;
            }
            if (!changed) break;
        }
        // Native reconciliation will supply precise pixel rounding. Refuse
        // a material model mismatch rather than silently reporting success.
        var final = workspace.Find(groupId);
        return final is not null && final.Children.Count == weights.Length
            && final.Children.Select((child, index) => child.TilingSize is { } fraction
                && Math.Abs(fraction - weights[index]) <= 3.0 / Math.Max(1, total)).All(match => match) ? workspace : null;
    }

    private abstract record Desired(TilingRect Bounds);
    private sealed record DesiredLeaf(Guid Id, TilingRect Bounds) : Desired(Bounds);
    private sealed record DesiredGroup(TilingSplitDirection Direction, IReadOnlyList<Desired> Children,
        TilingRect Bounds) : Desired(Bounds);

    private static Desired Canonical(TilingLayoutNode node)
    {
        if (node is TilingLayoutLeaf leaf) return new DesiredLeaf(leaf.WindowId, leaf.Bounds);
        var split = (TilingLayoutSplit)node;
        var children = new[] { Canonical(split.First), Canonical(split.Second) }
            .SelectMany(child => child is DesiredGroup group && group.Direction == split.Direction
                ? group.Children : [child]).ToArray();
        return new DesiredGroup(split.Direction, children, split.Bounds);
    }

    private static IEnumerable<Guid> Leaves(Desired node) => node is DesiredLeaf leaf ? [leaf.Id]
        : ((DesiredGroup)node).Children.SelectMany(Leaves);

    private static bool Matches(Desired desired, Node actual)
    {
        if (desired is DesiredLeaf leaf)
            return actual.IsWindow ? actual.Id == leaf.Id
                : actual.IsWorkspace && actual.Children.Count == 1 && Matches(desired, actual.Children[0]);
        var group = (DesiredGroup)desired;
        return !actual.IsWindow && actual.Direction == group.Direction && actual.Children.Count == group.Children.Count
            && group.Children.Select((child, index) => Matches(child, actual.Children[index])).All(match => match);
    }

    private static bool WeightsAgree(Desired desired, Node actual)
    {
        if (desired is DesiredLeaf) return true;
        var group = (DesiredGroup)desired;
        if (actual.Children.Count != group.Children.Count || actual.Direction != group.Direction) return false;
        var lengths = group.Children.Select(child => group.Direction == TilingSplitDirection.Horizontal
            ? child.Bounds.Width : child.Bounds.Height).ToArray();
        var available = lengths.Sum(length => (long)length);
        if (available <= 0) return false;
        // Native correction can absorb engine pixel rounding, but it must not
        // conceal a materially different split weight at any nesting level.
        var tolerance = 3.0 / available;
        for (var index = 0; index < group.Children.Count; index++)
            if (actual.Children[index].TilingSize is not { } fraction || !double.IsFinite(fraction)
                || Math.Abs(fraction - lengths[index] / (double)available) > tolerance
                || !WeightsAgree(group.Children[index], actual.Children[index])) return false;
        return true;
    }

    private static string Name(TilingSplitDirection direction) => direction == TilingSplitDirection.Horizontal ? "horizontal" : "vertical";
    private static string Start(TilingSplitDirection direction) => direction == TilingSplitDirection.Horizontal ? "left" : "up";
    private static TilingSplitDirection Opposite(TilingSplitDirection direction) => direction == TilingSplitDirection.Horizontal
        ? TilingSplitDirection.Vertical : TilingSplitDirection.Horizontal;

    /// <summary>
    /// Identifies the ordered visible tiling tree, including its raw weights.
    /// Focus and native/model rectangles deliberately do not affect the value.
    /// </summary>
    internal static string Fingerprint(JsonElement workspace)
    {
        var result = new StringBuilder();
        AppendFingerprint(workspace, result);
        return result.ToString();
    }

    private static void AppendFingerprint(JsonElement element, StringBuilder result)
    {
        var type = element.GetProperty("type").GetString();
        if (type == "window" && (element.GetProperty("state").GetProperty("type").GetString() != "tiling"
            || element.GetProperty("displayState").GetString() is not ("shown" or "showing"))) return;
        if (type is not ("window" or "workspace" or "split")) return;
        result.Append(type).Append(':').Append(Guid.Parse(element.GetProperty("id").GetString()!).ToString("D"));
        if (type != "window")
            result.Append(':').Append(element.TryGetProperty("tilingDirection", out var direction)
                && direction.GetString() == "vertical" ? 'v' : 'h');
        result.Append(':');
        if (element.TryGetProperty("tilingSize", out var size) && size.ValueKind == JsonValueKind.Number)
            result.Append(size.GetDouble().ToString("R", CultureInfo.InvariantCulture));
        result.Append('[');
        if (element.TryGetProperty("children", out var children))
            foreach (var child in children.EnumerateArray()) AppendFingerprint(child, result);
        result.Append(']');
    }

    private sealed class Node
    {
        internal required Guid Id { get; init; }
        internal bool IsWindow { get; init; }
        internal bool IsWorkspace { get; init; }
        internal TilingSplitDirection Direction { get; init; }
        internal long Handle { get; init; }
        internal bool Focused { get; init; }
        internal bool Dragging { get; init; }
        internal double? TilingSize { get; init; }
        internal Node? Parent { get; set; }
        internal List<Node> Children { get; } = [];
        internal int Depth => Parent is null ? 0 : Parent.Depth + 1;
        internal IEnumerable<Node> Windows() => IsWindow ? [this] : Children.SelectMany(child => child.Windows());
        internal Node? Find(Guid id) => Id == id ? this : Children.Select(child => child.Find(id)).FirstOrDefault(node => node is not null);
    }

    private sealed class Context(Guid workspaceId, Guid[] expectedIds, Func<string, Task<JsonDocument>> send,
        string? expectedFingerprint, ITilingNativeWindows? native)
    {
        private readonly HashSet<Guid> expected = expectedIds.ToHashSet();
        private readonly Dictionary<Guid, (long Handle, int Process, long Start)> identities = [];
        private string? acceptedFingerprint = expectedFingerprint;
        private int commandCount;

        internal async Task<Node?> ReadAsync(bool acceptOwnedMutation = false)
        {
            using var pause = await send("query paused");
            if (pause.RootElement.GetProperty("data").GetBoolean()) return null;
            using var document = await send("query monitors");
            var response = document.RootElement;
            if (response.TryGetProperty("success", out var success) && !success.GetBoolean()) return null;
            JsonElement? workspaceElement = null;
            foreach (var monitor in response.GetProperty("data").GetProperty("monitors").EnumerateArray())
                foreach (var workspace in monitor.GetProperty("children").EnumerateArray())
                    if (workspace.GetProperty("type").GetString() == "workspace"
                        && Guid.Parse(workspace.GetProperty("id").GetString()!) == workspaceId)
                        workspaceElement = workspace;
            if (workspaceElement is not { } element || !element.GetProperty("isDisplayed").GetBoolean()) return null;
            var fingerprint = Fingerprint(element);
            // A manual direction, order or weight change invalidates the old
            // plan. Only the response to our own mutation advances the anchor.
            if (!acceptOwnedMutation && acceptedFingerprint is not null
                && !string.Equals(acceptedFingerprint, fingerprint, StringComparison.Ordinal)) return null;
            var root = Parse(element, null);
            if (root is null || !root.Windows().Select(window => window.Id).ToHashSet().SetEquals(expected)) return null;
            var foreground = native?.ForegroundHandle ?? GetForegroundWindow().ToInt64();
            foreach (var window in root.Windows())
            {
                if (window.Dragging || window.Handle == 0 || (window.Focused && window.Handle != foreground)
                    || !ReadIdentity(window.Handle, out var process, out var start)) return null;
                var identity = (window.Handle, process, start);
                if (identities.TryGetValue(window.Id, out var initial) && initial != identity) return null;
                identities.TryAdd(window.Id, identity);
            }
            acceptedFingerprint = fingerprint;
            return root;
        }

        private bool ReadIdentity(long handle, out int process, out long start) => native is not null
            ? native.TryGetIdentity(handle, out process, out start)
            : FullscreenGameDetector.TryGetIdentity(handle, out process, out start);

        internal async Task<Node?> ChangeAsync(Guid id, string command, Func<Node, bool>? preflight = null)
        {
            var before = await ReadAsync();
            if (before?.Find(id) is null || (preflight is not null && !preflight(before))
                || ++commandCount > MaximumCommands) throw new DeferredException();
            using var response = await send($"command --id {id:D} {command}");
            if (response.RootElement.TryGetProperty("success", out var success) && !success.GetBoolean()) return null;
            return await ReadAsync(acceptOwnedMutation: true);
        }

        private static Node? Parse(JsonElement element, Node? parent)
        {
            var type = element.GetProperty("type").GetString();
            if (type == "window" && (element.GetProperty("state").GetProperty("type").GetString() != "tiling"
                || element.GetProperty("displayState").GetString() is not ("shown" or "showing"))) return null;
            if (type is not ("window" or "workspace" or "split")) return null;
            var node = new Node
            {
                Id = Guid.Parse(element.GetProperty("id").GetString()!),
                IsWindow = type == "window", IsWorkspace = type == "workspace", Parent = parent,
                Direction = element.TryGetProperty("tilingDirection", out var direction) && direction.GetString() == "vertical"
                    ? TilingSplitDirection.Vertical : TilingSplitDirection.Horizontal,
                Handle = type == "window" ? element.GetProperty("handle").GetInt64() : 0,
                Focused = element.TryGetProperty("hasFocus", out var focus) && focus.GetBoolean(),
                Dragging = element.TryGetProperty("activeDrag", out var drag) && drag.ValueKind != JsonValueKind.Null,
                TilingSize = element.TryGetProperty("tilingSize", out var size) && size.ValueKind == JsonValueKind.Number
                    ? size.GetDouble() : null,
            };
            if (element.TryGetProperty("children", out var children))
                foreach (var child in children.EnumerateArray())
                    if (Parse(child, node) is { } parsed) node.Children.Add(parsed);
            return node;
        }
    }

    private sealed class DeferredException : Exception { }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
