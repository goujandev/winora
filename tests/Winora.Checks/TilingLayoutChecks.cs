using Winora;

internal static class TilingLayoutChecks
{
    public static void Run(Action<bool, string> check)
    {
        var windows = Enumerable.Range(0, 16).Select(index => new TilingLayoutWindow(Id(index), 160, 120)).ToArray();
        var landscape = new TilingRect(-1920, 20, 1920, 1080);
        var one = TilingLayoutPolicy.Build(landscape, windows[..1], 8);
        check(one.FitsAll && one.Cells.Single().Bounds == landscape,
            "One tiled window fills the monitor's usable bounds");
        var two = TilingLayoutPolicy.Build(landscape, windows[..2], 8);
        check(two.Root is TilingLayoutSplit { Direction: TilingSplitDirection.Horizontal }
            && two.Cells.All(cell => cell.Bounds.Height == landscape.Height)
            && two.Cells[0].Bounds.Right + 8 == two.Cells[1].Bounds.X
            && two.Cells[1].Bounds.Right == landscape.Right
            && Math.Abs(two.Cells[0].Bounds.Width - two.Cells[1].Bounds.Width) <= 1,
            "Two landscape windows share equal left and right halves");
        var three = TilingLayoutPolicy.Build(landscape, windows[..3], 8);
        check(three.Cells[0].Bounds.Height == landscape.Height
            && three.Cells[1].Bounds.X == three.Cells[2].Bounds.X
            && three.Cells[1].Bounds.Bottom + 8 == three.Cells[2].Bounds.Y
            && three.Cells[2].Bounds.Bottom == landscape.Bottom,
            "Three landscape windows keep one full-height tile left and two stacked right");
        var four = TilingLayoutPolicy.Build(landscape, windows[..4], 8);
        check(four.Cells[1].Bounds == three.Cells[1].Bounds && four.Cells[2].Bounds == three.Cells[2].Bounds
            && four.Cells[3].Bounds.X == four.Cells[0].Bounds.X
            && four.Cells[0].Bounds.Bottom + 8 == four.Cells[3].Bounds.Y
            && four.Cells.Select(cell => cell.Bounds.Width).Distinct().Count() == 1
            && four.Cells.Select(cell => cell.Bounds.Height).Distinct().Count() == 1,
            "The fourth window splits the left column while preserving the existing right stack");

        var portrait = new TilingRect(2560, -80, 1080, 1920);
        var portraitTwo = TilingLayoutPolicy.Build(portrait, windows[..2], 8);
        var portraitThree = TilingLayoutPolicy.Build(portrait, windows[..3], 8);
        check(portraitTwo.Root is TilingLayoutSplit { Direction: TilingSplitDirection.Vertical }
            && portraitTwo.Cells.All(cell => cell.Bounds.Width == portrait.Width)
            && portraitThree.Cells[0].Bounds.Width == portrait.Width
            && portraitThree.Cells[1].Bounds.Y == portraitThree.Cells[2].Bounds.Y,
            "Portrait monitors transpose the layout into top/bottom and a bottom pair");

        var wideMinimum = TilingLayoutPolicy.Build(new(0, 0, 1000, 600),
            [windows[0] with { MinWidth = 700 }, windows[1]], 8);
        check(wideMinimum.FitsAll && wideMinimum.Cells[0].Bounds.Width == 700
            && wideMinimum.Cells[1].Bounds.Width == 292
            && wideMinimum.Cells[1].Bounds.Right == 1000,
            "A larger native minimum reallocates neighbouring space without leaving a hole");
        var tallMinimum = TilingLayoutPolicy.Build(new(0, 0, 1200, 800),
            [windows[0], windows[1] with { MinHeight = 600 }, windows[2]], 8);
        check(tallMinimum.FitsAll && tallMinimum.Cells[1].Bounds.Height == 600
            && tallMinimum.Cells[2].Bounds.Height == 192,
            "A native height minimum reallocates the other window in its stack");
        var resized = TilingLayoutPolicy.Build(new(0, 0, 1000, 600), windows[..2], 8,
            (direction, first, second) => direction == TilingSplitDirection.Horizontal
                && first.SequenceEqual([windows[0].Id]) && second.SequenceEqual([windows[1].Id]) ? .7 : null);
        check(resized.Cells[0].Bounds.Width == 694 && resized.Cells[1].Bounds.Width == 298
            && resized.Cells[1].Bounds.Right == 1000,
            "An existing user resize is retained while the neighbour fills the remaining space");
        var clampedSmall = TilingLayoutPolicy.Build(new(0, 0, 1000, 600),
            [windows[0] with { MinWidth = 300 }, windows[1] with { MinWidth = 250 }], 8, (_, _, _) => .01);
        var clampedLarge = TilingLayoutPolicy.Build(new(0, 0, 1000, 600),
            [windows[0] with { MinWidth = 300 }, windows[1] with { MinWidth = 250 }], 8, (_, _, _) => .99);
        check(clampedSmall.Cells[0].Bounds.Width == 300 && clampedSmall.Cells[1].Bounds.Width == 692
            && clampedLarge.Cells[0].Bounds.Width == 742 && clampedLarge.Cells[1].Bounds.Width == 250,
            "User resizing cannot violate either child's native minimum size");
        var nonfiniteRatio = TilingLayoutPolicy.Build(new(0, 0, 1000, 600), windows[..2], 8, (_, _, _) => double.NaN);
        check(nonfiniteRatio.Cells.All(cell => cell.Bounds.Width == 496),
            "Invalid resize measurements fall back to a safe default split");
        var manualTemplate = new TilingLayoutSplit(TilingSplitDirection.Vertical, .7,
            new TilingLayoutLeaf(windows[1].Id, default), new TilingLayoutLeaf(windows[0].Id, default), default);
        var manualReflow = TilingLayoutPolicy.Reflow(new(0, 0, 1000, 600), manualTemplate, windows[..2], 8);
        check(manualReflow.Root is TilingLayoutSplit { Direction: TilingSplitDirection.Vertical,
                First: TilingLayoutLeaf firstManual } && firstManual.WindowId == windows[1].Id
            && manualReflow.Cells[1].Bounds.Y == 0 && manualReflow.Cells[1].Bounds.Height == 414
            && manualReflow.Cells[0].Bounds.Y == 422 && manualReflow.Cells[0].Bounds.Bottom == 600,
            "Reflow preserves manual split orientation and swapped spatial order without frame-gap drift");
        var minimumReflow = TilingLayoutPolicy.Reflow(new(0, 0, 1000, 600), manualTemplate,
            [windows[0] with { MinHeight = 300 }, windows[1]], 8);
        check(minimumReflow.Cells[0].Bounds.Height == 300 && minimumReflow.Cells[1].Bounds.Height == 292
            && minimumReflow.Cells[1].Bounds.Bottom + 8 == minimumReflow.Cells[0].Bounds.Y,
            "Reflow clamps manual weights to native minima and fills neighbouring space");
        var infeasibleReflow = TilingLayoutPolicy.Reflow(new(0, 0, 1000, 600), manualTemplate,
            [windows[0] with { MinHeight = 500 }, windows[1] with { MinHeight = 500 }], 8);
        var invalidTemplateRejected = false;
        try { TilingLayoutPolicy.Reflow(landscape, manualTemplate, windows[..1]); }
        catch (ArgumentException) { invalidTemplateRejected = true; }
        check(infeasibleReflow.Root is null && infeasibleReflow.Cells.Count == 0
            && infeasibleReflow.OverflowWindowIds.SequenceEqual(windows[..2].Select(window => window.Id))
            && invalidTemplateRejected,
            "Infeasible manual templates overflow explicitly while mismatched identities are rejected");
        var stableWeights = true;
        foreach (var direction in Enum.GetValues<TilingSplitDirection>())
        foreach (var weights in new[] { new[] { .25, .25, .25, .25 }, new[] { .1, .2, .3, .4 } })
        {
            var template = WeightedTemplate(direction, windows[..4], weights);
            var bounds = new TilingRect(-20, -80, 1600, 1200);
            var plan = TilingLayoutPolicy.Reflow(bounds, template, windows[..4], 8);
            var expected = plan.Cells.Select(cell => cell.Bounds).ToArray();
            for (var pass = 0; pass < 40; pass++)
            {
                var sizes = plan.Cells.Select(cell => direction == TilingSplitDirection.Horizontal
                    ? cell.Bounds.Width : cell.Bounds.Height).ToArray();
                var total = sizes.Sum();
                template = WeightedTemplate(direction, windows[..4], sizes.Select(size => (double)size / total).ToArray());
                plan = TilingLayoutPolicy.Reflow(bounds, template, windows[..4], 8);
                stableWeights &= plan.FitsAll && plan.Cells.Select(cell => cell.Bounds).SequenceEqual(expected);
            }
        }
        check(stableWeights, "N-ary model fractions remain pixel-stable through forty reflow passes on both axes");
        var weightedMinima = TilingLayoutPolicy.Reflow(new(0, 0, 1600, 600),
            WeightedTemplate(TilingSplitDirection.Horizontal, windows[..4], [.1, .2, .3, .4]),
            [windows[0] with { MinWidth = 400 }, windows[1], windows[2], windows[3]], 8);
        check(weightedMinima.FitsAll && weightedMinima.Cells[0].Bounds.Width == 400
            && weightedMinima.Cells[1].Bounds.Width == 261 && weightedMinima.Cells[2].Bounds.Width == 392
            && weightedMinima.Cells[3].Bounds.Width == 523 && weightedMinima.Cells[3].Bounds.Right == 1600,
            "N-ary minimum clamping proportionally reflows every neighbouring tile without gaps or specks");
        var outputStable = weightedMinima;
        for (var pass = 0; pass < 40; pass++)
            outputStable = TilingLayoutPolicy.Reflow(new(0, 0, 1600, 600), outputStable.Root!,
                [windows[0] with { MinWidth = 400 }, windows[1], windows[2], windows[3]], 8);
        check(outputStable.Cells.Select(cell => cell.Bounds).SequenceEqual(weightedMinima.Cells.Select(cell => cell.Bounds)),
            "Reflow output can be reused as a template without virtual-group gap drift");
        var overflow = TilingLayoutPolicy.Build(new(0, 0, 300, 200), windows[..2], 8);
        check(!overflow.FitsAll && overflow.Cells.Single().WindowId == windows[0].Id
            && overflow.OverflowWindowIds.SequenceEqual([windows[1].Id])
            && overflow.Cells.Single().Bounds.Width == 300,
            "An infeasible split explicitly overflows the later window rather than creating a speck");
        var widePair = TilingLayoutPolicy.Build(new(0, 0, 1000, 600),
            windows[..2].Select(window => window with { MinWidth = 600 }).ToArray(), 8);
        var tallThree = TilingLayoutPolicy.Build(new(0, 0, 1000, 600),
            windows[..3].Select(window => window with { MinHeight = 400 }).ToArray(), 8);
        var tallFour = TilingLayoutPolicy.Build(new(0, 0, 1000, 600),
            windows[..4].Select(window => window with { MinHeight = 400 }).ToArray(), 8);
        check(widePair.FitsAll && widePair.Root is TilingLayoutSplit { Direction: TilingSplitDirection.Vertical }
            && tallThree.FitsAll && tallFour.FitsAll,
            "Small layouts try alternate orientations and strips before unnecessarily floating an app");
        var impossible = TilingLayoutPolicy.Build(new(0, 0, 500, 500), [windows[0] with { MinWidth = 800 }]);
        check(impossible.Root is null && impossible.Cells.Count == 0
            && impossible.OverflowWindowIds.SequenceEqual([windows[0].Id]),
            "An app larger than the monitor is reported for safe floating or another destination");
        var mixedSize = TilingLayoutPolicy.Build(new(0, 0, 500, 500),
            [windows[0] with { MinWidth = 800 }, windows[1], windows[2]]);
        check(mixedSize.Cells.Select(cell => cell.WindowId).SequenceEqual([windows[1].Id, windows[2].Id])
            && mixedSize.OverflowWindowIds.SequenceEqual([windows[0].Id]),
            "An individually oversized app does not prevent the remaining apps from tiling");
        var minimumGrid = TilingLayoutPolicy.Build(landscape,
            windows[..8].Select(window => window with { MinWidth = 600 }).ToArray());
        check(minimumGrid.FitsAll && minimumGrid.Cells.Count == 8
            && minimumGrid.Cells.All(cell => cell.Bounds.Width >= 600),
            "Larger window sets choose an alternate balanced grid when native minima require fewer columns");
        var unknownMinimum = TilingLayoutPolicy.Build(new(0, 0, 200, 100), [windows[0] with { MinWidth = 0, MinHeight = 0 }]);
        check(unknownMinimum.Cells.Count == 0 && !unknownMinimum.FitsAll,
            "Custom frames without native minima still cannot receive unusably tiny tiles");

        var allGeometriesValid = true;
        foreach (var bounds in new[] { landscape, portrait, new TilingRect(-777, -333, 1919, 1079) })
        foreach (var gap in new[] { 0, 8, 32 })
        foreach (var count in Enumerable.Range(0, windows.Length + 1))
        {
            var plan = TilingLayoutPolicy.Build(bounds, windows[..count], gap);
            allGeometriesValid &= plan.Cells.Count + plan.OverflowWindowIds.Count == count
                && plan.Cells.Select(cell => cell.WindowId).SequenceEqual(windows.Take(plan.Cells.Count).Select(window => window.Id))
                && plan.Cells.All(cell => cell.Bounds.X >= bounds.X && cell.Bounds.Y >= bounds.Y
                    && cell.Bounds.Right <= bounds.Right && cell.Bounds.Bottom <= bounds.Bottom
                    && cell.Bounds.Width >= 160 && cell.Bounds.Height >= 120);
            for (var first = 0; first < plan.Cells.Count; first++)
            for (var second = first + 1; second < plan.Cells.Count; second++)
                allGeometriesValid &= !Overlap(plan.Cells[first].Bounds, plan.Cells[second].Bounds);
        }
        check(allGeometriesValid, "Layouts from zero through sixteen windows remain bounded, non-overlapping and identity-stable");

        var main = new TilingMonitor(Id(50), true, 0, 2);
        var secondMonitor = new TilingMonitor(Id(51), false, 1, 2);
        var thirdMonitor = new TilingMonitor(Id(52), false, 2, 2);
        check(TilingLayoutPolicy.SelectMonitor([thirdMonitor, secondMonitor, main]) == main,
            "Equal monitor loads prefer the main monitor independent of enumeration order");
        check(TilingLayoutPolicy.SelectMonitor([main, thirdMonitor with { WindowCount = 1 }, secondMonitor with { WindowCount = 1 }])?.Id == secondMonitor.Id,
            "A tie between secondary monitors uses explicit display order");
        check(TilingLayoutPolicy.SelectMonitor([main, secondMonitor with { WindowCount = 0, IsFullscreenOccupied = true }, thirdMonitor with { WindowCount = 1 }])?.Id == thirdMonitor.Id,
            "New windows avoid fullscreen-occupied monitors while selecting the least loaded available display");
        check(TilingLayoutPolicy.SelectMonitor([main with { CanAcceptWindow = false }, secondMonitor with { CanAcceptWindow = false }]) is null,
            "No monitor is chosen when no destination can safely fit the new window");
        check(TilingLayoutPolicy.SelectMonitor([main with { WindowCount = 1 }, secondMonitor])?.Id == main.Id,
            "Window count is considered before monitor preference");

        var rejected = 0;
        foreach (var invalid in new Action[]
        {
            () => TilingLayoutPolicy.Build(new(0, 0, 0, 200), windows[..1]),
            () => TilingLayoutPolicy.Build(landscape, windows[..1], -1),
            () => TilingLayoutPolicy.Build(landscape, [windows[0], windows[0]]),
            () => TilingLayoutPolicy.Build(landscape, [windows[0] with { MinWidth = -1 }]),
            () => TilingLayoutPolicy.SelectMonitor([main with { WindowCount = -1 }])
        })
        {
            try { invalid(); }
            catch (ArgumentException) { rejected++; }
        }
        check(rejected == 5, "Invalid geometry, duplicate identities, gaps and monitor counts fail before native placement");
    }

    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);
    private static TilingLayoutNode WeightedTemplate(TilingSplitDirection direction,
        IReadOnlyList<TilingLayoutWindow> windows, IReadOnlyList<double> weights)
    {
        TilingLayoutNode Group(int start, int count)
        {
            if (count == 1) return new TilingLayoutLeaf(windows[start].Id, default);
            var firstCount = count / 2;
            return new TilingLayoutSplit(direction,
                weights.Skip(start).Take(firstCount).Sum() / weights.Skip(start).Take(count).Sum(),
                Group(start, firstCount), Group(start + firstCount, count - firstCount), default);
        }
        return Group(0, windows.Count);
    }
    private static bool Overlap(TilingRect first, TilingRect second) =>
        first.X < second.Right && second.X < first.Right && first.Y < second.Bottom && second.Y < first.Bottom;
}
