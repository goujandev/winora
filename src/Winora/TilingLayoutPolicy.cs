namespace Winora;

/// <summary>Physical pixels inside the monitor's usable area, after outer gaps.</summary>
public readonly record struct TilingRect(int X, int Y, int Width, int Height)
{
    public int Right => checked(X + Width);
    public int Bottom => checked(Y + Height);
}

public sealed record TilingLayoutWindow(Guid Id, int MinWidth, int MinHeight);

/// <summary>Horizontal divides left/right; vertical divides top/bottom.</summary>
public enum TilingSplitDirection { Horizontal, Vertical }

public abstract record TilingLayoutNode(TilingRect Bounds);
public sealed record TilingLayoutLeaf(Guid WindowId, TilingRect Bounds) : TilingLayoutNode(Bounds);
public sealed record TilingLayoutSplit(TilingSplitDirection Direction, double Ratio,
    TilingLayoutNode First, TilingLayoutNode Second, TilingRect Bounds) : TilingLayoutNode(Bounds);

/// <summary>Overflow windows need another monitor or floating placement, never an undersized tile.</summary>
public sealed record TilingLayoutPlan(TilingLayoutNode? Root,
    IReadOnlyList<TilingLayoutLeaf> Cells, IReadOnlyList<Guid> OverflowWindowIds)
{
    public bool FitsAll => OverflowWindowIds.Count == 0;
}

public sealed record TilingMonitor(Guid Id, bool IsPrimary, int DisplayOrder, int WindowCount,
    bool IsFullscreenOccupied = false, bool CanAcceptWindow = true);

/// <summary>Deterministic layouts; no operating-system calls or persisted state.</summary>
public static class TilingLayoutPolicy
{
    // Some custom frames report no native minimum at all. Keep these useful rather than allowing specks.
    public const int SafetyMinimumWidth = 160;
    public const int SafetyMinimumHeight = 120;

    public static TilingMonitor? SelectMonitor(IEnumerable<TilingMonitor> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        var candidates = monitors.ToArray();
        if (candidates.Any(monitor => monitor.WindowCount < 0 || monitor.DisplayOrder < 0))
            throw new ArgumentOutOfRangeException(nameof(monitors));
        return candidates.Where(monitor => !monitor.IsFullscreenOccupied && monitor.CanAcceptWindow)
            .OrderBy(monitor => monitor.WindowCount)
            .ThenByDescending(monitor => monitor.IsPrimary)
            .ThenBy(monitor => monitor.DisplayOrder)
            .ThenBy(monitor => monitor.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// The caller supplies windows in stable arrival order. Larger app minima reflow adjacent tiles.
    /// If the requested pattern cannot fit, later windows remain explicit overflow for the caller to handle.
    /// </summary>
    public static TilingLayoutPlan Build(TilingRect bounds, IReadOnlyList<TilingLayoutWindow> windows, int gap = 8,
        Func<TilingSplitDirection, IReadOnlyList<Guid>, IReadOnlyList<Guid>, double?>? preferredRatio = null)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new ArgumentOutOfRangeException(nameof(bounds));
        if (gap < 0 || gap > 256) throw new ArgumentOutOfRangeException(nameof(gap));
        if (windows.Any(window => window.MinWidth < 0 || window.MinHeight < 0))
            throw new ArgumentOutOfRangeException(nameof(windows));
        if (windows.Select(window => window.Id).Distinct().Count() != windows.Count)
            throw new ArgumentException("A window cannot appear twice in a layout.", nameof(windows));

        var portrait = bounds.Height > bounds.Width;
        var candidates = windows.Where(window => Math.Max(SafetyMinimumWidth, window.MinWidth) <= bounds.Width
            && Math.Max(SafetyMinimumHeight, window.MinHeight) <= bounds.Height).ToArray();
        var count = candidates.Length;
        while (count > 0)
        {
            var included = candidates.Take(count).ToArray();
            foreach (var recipe in MakeRecipes(included, portrait, bounds))
            {
                var minimum = Minimum(recipe, gap);
                if (minimum.Width > bounds.Width || minimum.Height > bounds.Height) continue;
                var cells = new List<TilingLayoutLeaf>(count);
                var root = Place(recipe, bounds, gap, cells, preferredRatio);
                // Keep identity order independent of the spatial tree traversal.
                var byId = cells.ToDictionary(cell => cell.WindowId);
                return new(root, included.Select(window => byId[window.Id]).ToArray(),
                    windows.Where(window => !byId.ContainsKey(window.Id)).Select(window => window.Id).ToArray());
            }
            count--;
        }
        return new(null, [], windows.Select(window => window.Id).ToArray());
    }

    /// <summary>
    /// Preserve a user's existing split directions, spatial identity order and weights while reflowing new bounds.
    /// An infeasible template explicitly overflows the entire set so the caller can try a balanced layout first.
    /// </summary>
    public static TilingLayoutPlan Reflow(TilingRect bounds, TilingLayoutNode template,
        IReadOnlyList<TilingLayoutWindow> windows, int gap = 8)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(windows);
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new ArgumentOutOfRangeException(nameof(bounds));
        if (gap < 0 || gap > 256) throw new ArgumentOutOfRangeException(nameof(gap));
        if (windows.Any(window => window.MinWidth < 0 || window.MinHeight < 0))
            throw new ArgumentOutOfRangeException(nameof(windows));
        if (windows.Select(window => window.Id).Distinct().Count() != windows.Count)
            throw new ArgumentException("A window cannot appear twice in a layout.", nameof(windows));
        var byId = windows.ToDictionary(window => window.Id);
        var seen = new HashSet<Guid>();
        Recipe ReadTemplate(TilingLayoutNode node)
        {
            if (node is TilingLayoutLeaf leaf)
            {
                if (!byId.TryGetValue(leaf.WindowId, out var window) || !seen.Add(leaf.WindowId))
                    throw new ArgumentException("Template window identities must match the supplied windows.", nameof(template));
                return new Leaf(window);
            }
            if (node is not TilingLayoutSplit split || !Enum.IsDefined(split.Direction)
                || !double.IsFinite(split.Ratio) || split.First is null || split.Second is null)
                throw new ArgumentException("The split template is invalid.", nameof(template));
            return new Split(split.Direction, Math.Clamp(split.Ratio, 0, 1), ReadTemplate(split.First), ReadTemplate(split.Second));
        }
        var recipe = ReadTemplate(template);
        if (seen.Count != windows.Count)
            throw new ArgumentException("Template window identities must match the supplied windows.", nameof(template));
        var minimum = Minimum(recipe, gap);
        if (minimum.Width > bounds.Width || minimum.Height > bounds.Height)
            return new(null, [], windows.Select(window => window.Id).ToArray());
        var cells = new List<TilingLayoutLeaf>(windows.Count);
        var root = PlaceReflow(recipe, bounds, gap, cells);
        var placed = cells.ToDictionary(cell => cell.WindowId);
        return new(root, windows.Select(window => placed[window.Id]).ToArray(), []);
    }

    private abstract record Recipe;
    private sealed record Leaf(TilingLayoutWindow Window) : Recipe;
    private sealed record Split(TilingSplitDirection Direction, double Ratio, Recipe First, Recipe Second) : Recipe;
    private readonly record struct MinimumSize(long Width, long Height);

    private static IEnumerable<Recipe> MakeRecipes(TilingLayoutWindow[] windows, bool portrait, TilingRect bounds)
    {
        if (windows.Length == 1) { yield return new Leaf(windows[0]); yield break; }
        var across = portrait ? TilingSplitDirection.Vertical : TilingSplitDirection.Horizontal;
        var along = portrait ? TilingSplitDirection.Horizontal : TilingSplitDirection.Vertical;
        if (windows.Length == 2)
        {
            yield return new Split(across, .5, new Leaf(windows[0]), new Leaf(windows[1]));
        }
        if (windows.Length == 3)
        {
            yield return new Split(across, .5, new Leaf(windows[0]),
                new Split(along, .5, new Leaf(windows[1]), new Leaf(windows[2])));
        }
        if (windows.Length == 4)
        {
            yield return new Split(across, .5,
                new Split(along, .5, new Leaf(windows[0]), new Leaf(windows[3])),
                new Split(along, .5, new Leaf(windows[1]), new Leaf(windows[2])));
        }

        if (windows.Length <= 4)
        {
            // Try alternate shallow arrangements before declaring overflow.
            // These trees are all expressible through the engine's public IPC.
            foreach (var axis in new[] { across, along })
            for (var cuts = 0; cuts < 1 << (windows.Length - 1); cuts++)
            {
                List<List<TilingLayoutWindow>> groups = [[]];
                for (var index = 0; index < windows.Length; index++)
                {
                    groups[^1].Add(windows[index]);
                    if (index < windows.Length - 1 && (cuts & (1 << index)) != 0) groups.Add([]);
                }
                var perpendicular = axis == TilingSplitDirection.Horizontal ? TilingSplitDirection.Vertical : TilingSplitDirection.Horizontal;
                yield return GroupRecipe(groups.ToArray(), 0, groups.Count, axis, perpendicular);
            }
            yield break;
        }

        // Larger sets use balanced columns (rows on portrait displays), rather than a long single strip.
        var aspect = (double)Math.Max(bounds.Width, bounds.Height) / Math.Min(bounds.Width, bounds.Height);
        var preferred = Math.Clamp((int)Math.Round(Math.Sqrt(windows.Length * aspect)), 2, windows.Length / 2);
        // Native minima can make the visually preferred column count impossible while another grid still fits.
        foreach (var groupCount in Enumerable.Range(2, windows.Length / 2 - 1)
            .OrderBy(groupCount => Math.Abs(groupCount - preferred)).ThenBy(groupCount => groupCount))
            yield return MakeGroups(windows, groupCount, across, along);
    }

    private static Recipe MakeGroups(TilingLayoutWindow[] windows, int groupCount,
        TilingSplitDirection across, TilingSplitDirection along)
    {
        var groups = Enumerable.Range(0, groupCount).Select(_ => new List<TilingLayoutWindow>()).ToArray();
        if (groupCount == 2)
        {
            groups[0].Add(windows[0]); groups[0].Add(windows[3]);
            groups[1].Add(windows[1]); groups[1].Add(windows[2]);
            for (var index = 4; index < windows.Length; index++)
                groups[groups[0].Count <= groups[1].Count ? 0 : 1].Add(windows[index]);
        }
        else
        {
            for (var index = 0; index < windows.Length; index++) groups[index % groupCount].Add(windows[index]);
        }
        return GroupRecipe(groups, 0, groups.Length, across, along);
    }

    private static Recipe GroupRecipe(List<TilingLayoutWindow>[] groups, int start, int count,
        TilingSplitDirection across, TilingSplitDirection along)
    {
        if (count == 1) return StackRecipe(groups[start], 0, groups[start].Count, along);
        var firstCount = (count + 1) / 2;
        return new Split(across, (double)firstCount / count,
            GroupRecipe(groups, start, firstCount, across, along),
            GroupRecipe(groups, start + firstCount, count - firstCount, across, along));
    }

    private static Recipe StackRecipe(List<TilingLayoutWindow> windows, int start, int count, TilingSplitDirection direction)
    {
        if (count == 1) return new Leaf(windows[start]);
        var firstCount = (count + 1) / 2;
        return new Split(direction, (double)firstCount / count,
            StackRecipe(windows, start, firstCount, direction),
            StackRecipe(windows, start + firstCount, count - firstCount, direction));
    }

    private static MinimumSize Minimum(Recipe recipe, int gap)
    {
        if (recipe is Leaf leaf)
            return new(Math.Max(SafetyMinimumWidth, leaf.Window.MinWidth), Math.Max(SafetyMinimumHeight, leaf.Window.MinHeight));
        var split = (Split)recipe;
        var first = Minimum(split.First, gap);
        var second = Minimum(split.Second, gap);
        return split.Direction == TilingSplitDirection.Horizontal
            ? new(first.Width + gap + second.Width, Math.Max(first.Height, second.Height))
            : new(Math.Max(first.Width, second.Width), first.Height + gap + second.Height);
    }

    private static IEnumerable<Guid> WindowIds(Recipe recipe)
    {
        if (recipe is Leaf leaf) { yield return leaf.Window.Id; yield break; }
        var split = (Split)recipe;
        foreach (var id in WindowIds(split.First)) yield return id;
        foreach (var id in WindowIds(split.Second)) yield return id;
    }

    private static TilingLayoutNode PlaceReflow(Recipe recipe, TilingRect bounds, int gap, List<TilingLayoutLeaf> cells)
    {
        if (recipe is Leaf leaf)
        {
            var cell = new TilingLayoutLeaf(leaf.Window.Id, bounds);
            cells.Add(cell);
            return cell;
        }
        var split = (Split)recipe;
        var children = new List<(Recipe Recipe, double Weight)>();
        void Flatten(Recipe child, double weight)
        {
            if (child is Split nested && nested.Direction == split.Direction)
            {
                Flatten(nested.First, weight * nested.Ratio);
                Flatten(nested.Second, weight * (1 - nested.Ratio));
            }
            else children.Add((child, weight));
        }
        Flatten(recipe, 1);
        var horizontal = split.Direction == TilingSplitDirection.Horizontal;
        var available = (horizontal ? bounds.Width : bounds.Height) - gap * (children.Count - 1);
        var minima = children.Select(child => Minimum(child.Recipe, gap))
            .Select(minimum => horizontal ? minimum.Width : minimum.Height).ToArray();
        var lengths = Allocate(available, children.Select(child => child.Weight).ToArray(), minima);
        var nodes = new TilingLayoutNode[children.Count];
        var position = horizontal ? bounds.X : bounds.Y;
        for (var index = 0; index < children.Count; index++)
        {
            var childBounds = horizontal ? bounds with { X = position, Width = lengths[index] }
                : bounds with { Y = position, Height = lengths[index] };
            nodes[index] = PlaceReflow(children[index].Recipe, childBounds, gap, cells);
            position = checked(position + lengths[index] + (index < children.Count - 1 ? gap : 0));
        }

        TilingLayoutNode Group(int start, int count)
        {
            if (count == 1) return nodes[start];
            var firstCount = count / 2;
            var first = Group(start, firstCount);
            var second = Group(start + firstCount, count - firstCount);
            var total = lengths.Skip(start).Take(count).Sum(length => (long)length);
            var firstWeight = lengths.Skip(start).Take(firstCount).Sum(length => (long)length);
            var groupBounds = horizontal
                ? new TilingRect(first.Bounds.X, bounds.Y, second.Bounds.Right - first.Bounds.X, bounds.Height)
                : new TilingRect(bounds.X, first.Bounds.Y, bounds.Width, second.Bounds.Bottom - first.Bounds.Y);
            // These same-axis binary ancestors only represent an n-ary group.
            // Their canonical weights exclude the internal gaps, keeping both
            // model fractions and Reflow's own output stable on the next pass.
            return new TilingLayoutSplit(split.Direction, (double)firstWeight / total, first, second, groupBounds);
        }
        return Group(0, nodes.Length);
    }

    private static int[] Allocate(int available, double[] weights, long[] minima)
    {
        var exact = new double[weights.Length];
        var active = Enumerable.Range(0, weights.Length).ToList();
        long remaining = available;
        while (active.Count > 0)
        {
            var totalWeight = active.Sum(index => weights[index]);
            double Share(int index) => remaining * (totalWeight > 0 ? weights[index] / totalWeight : 1.0 / active.Count);
            var constrained = active.Where(index => Share(index) < minima[index]).ToArray();
            if (constrained.Length == 0)
            {
                foreach (var index in active) exact[index] = Math.Max(minima[index], Share(index));
                break;
            }
            foreach (var index in constrained)
            {
                exact[index] = minima[index];
                remaining -= minima[index];
                active.Remove(index);
            }
        }
        var lengths = exact.Select((length, index) => (int)Math.Max(minima[index], Math.Floor(length))).ToArray();
        var spare = available - lengths.Sum(length => (long)length);
        // Defensive correction for floating-point round-off at an integer
        // boundary; aggregate minima already prove that this space exists.
        foreach (var index in Enumerable.Range(0, lengths.Length).OrderByDescending(index => lengths[index] - minima[index]))
        {
            if (spare >= 0) break;
            var adjustment = (int)Math.Min(-spare, lengths[index] - minima[index]);
            lengths[index] -= adjustment;
            spare += adjustment;
        }
        var order = Enumerable.Range(0, lengths.Length)
            .OrderByDescending(index => exact[index] - Math.Floor(exact[index])).ThenBy(index => index).ToArray();
        for (var index = 0; spare > 0; index++, spare--) lengths[order[index % order.Length]]++;
        return lengths;
    }

    private static TilingLayoutNode Place(Recipe recipe, TilingRect bounds, int gap, List<TilingLayoutLeaf> cells,
        Func<TilingSplitDirection, IReadOnlyList<Guid>, IReadOnlyList<Guid>, double?>? preferredRatio)
    {
        if (recipe is Leaf leaf)
        {
            var cell = new TilingLayoutLeaf(leaf.Window.Id, bounds);
            cells.Add(cell);
            return cell;
        }
        var split = (Split)recipe;
        var firstMinimum = Minimum(split.First, gap);
        var secondMinimum = Minimum(split.Second, gap);
        var horizontal = split.Direction == TilingSplitDirection.Horizontal;
        var available = (horizontal ? bounds.Width : bounds.Height) - gap;
        var minimum = horizontal ? firstMinimum.Width : firstMinimum.Height;
        var maximum = available - (horizontal ? secondMinimum.Width : secondMinimum.Height);
        var requested = preferredRatio?.Invoke(split.Direction, WindowIds(split.First).ToArray(), WindowIds(split.Second).ToArray());
        var ratio = requested is { } value && double.IsFinite(value) ? Math.Clamp(value, 0, 1) : split.Ratio;
        var firstSize = (int)Math.Clamp((long)Math.Round(available * ratio), minimum, maximum);
        var secondSize = available - firstSize;
        var firstBounds = horizontal ? bounds with { Width = firstSize } : bounds with { Height = firstSize };
        var secondBounds = horizontal
            ? bounds with { X = checked(bounds.X + firstSize + gap), Width = secondSize }
            : bounds with { Y = checked(bounds.Y + firstSize + gap), Height = secondSize };
        return new TilingLayoutSplit(split.Direction, (double)firstSize / available,
            Place(split.First, firstBounds, gap, cells, preferredRatio),
            Place(split.Second, secondBounds, gap, cells, preferredRatio), bounds);
    }
}
