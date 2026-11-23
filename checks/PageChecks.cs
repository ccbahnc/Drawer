using Drawer.Core;
using System.Text.Json;

internal static class PageChecks
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string description)
        {
            if (!value) throw new Exception("pages: " + description);
            checks++;
            Console.WriteLine("PASS pages: " + description);
        }

        var ids = Enumerable.Range(0, 18).Select(_ => Guid.NewGuid()).ToArray();
        var (a, b, c, d, e, f, x, y) = (ids[0], ids[1], ids[2], ids[3], ids[4], ids[5], ids[16], ids[17]);
        Guid hole = Guid.Empty;
        static Guid[] Insert(Guid[] source, int cell, params Guid[] batch)
        {
            var slots = source.ToList();
            TopGridSlots.InsertPaged(slots, cell, batch, 4);
            return slots.ToArray();
        }
        Check(TopGridSlots.PageCount(0, 4) == 1 && TopGridSlots.PageCount(4, 4) == 1 &&
            TopGridSlots.PageCount(5, 4) == 2, "there is always one page and boundaries are exact");
        Check(Insert([a], 3, x).SequenceEqual(new[] { a, hole, hole, x }), "last unrecorded cell is droppable");
        Check(Insert([a, b, hole, c, d, e], 1, x).SequenceEqual(new[] { a, x, b, c, d, e }), "nearest forward hole stays within page");
        Check(Insert([hole, a, b, c, d, hole], 2, x).SequenceEqual(new[] { a, b, x, c, d, hole }), "nearest earlier hole preferred to overflow");
        Check(Insert([a, b, c, d, e, hole, f], 2, x).SequenceEqual(new[] { a, b, x, c, d, e, f }), "full page pushes tail to next page");
        Check(Insert([a, hole, b, c], 3, x, y).SequenceEqual(new[] { a, b, x, y, c }), "batch uses earlier spare space at the end of the page");
        Check(Insert([a, b, c, d, e, hole], 3, x, y).SequenceEqual(new[] { a, b, c, x, y, d, e }), "overflow preserves batch order before displaced items");

        var preview = new DragPreview(new[] { a, b, c, d, e, f }, new[] { a }, fixedCells: true);
        preview.PreviewInsertion(3);
        Check(preview.VisualSlots.SequenceEqual(new[] { d, b, c, hole, e, f }), "dragging inside a full page uses source vacancy for swap");
        preview.PreviewInsertion(9);
        Check(preview.Valid && preview.Index == 9 && preview.VisualSlots[0] == hole, "preview can target a temporary page");
        preview.Cancel();
        Check(!preview.Valid && preview.Order.SequenceEqual(new[] { a, b, c, d, e, f }), "cancel discards temporary pages");

        bool stable = true;
        for (int mask = 0; mask < 256 && stable; mask++)
        {
            var source = ids.Take(8).Select((id, i) => (mask & (1 << i)) == 0 ? id : hole).ToArray();
            for (int cell = 0; cell < 12; cell++)
            {
                var result = Insert(source, cell, x, y);
                stable &= TopGridSlots.Occupied(result).Where(id => id != x && id != y).SequenceEqual(TopGridSlots.Occupied(source)) &&
                    result.Count(id => id == x) == 1 && result.Count(id => id == y) == 1 && Array.IndexOf(result, x) < Array.IndexOf(result, y);
            }
        }
        Check(stable, "3072 sparse and full placements preserve resident and batch order");

        var wheel = new PageWheel();
        Check(wheel.Step(-40, 0, false) == 0 && wheel.Step(-40, 15, false) == 0 &&
            wheel.Step(-40, 35, false) == 1, "fine wheel input accumulates to a single page");
        Check(wheel.Step(-360, 70, true) == 0 && wheel.Step(-120, 190, false) == 0 &&
            wheel.Step(240, 375, false) == -1, "wheel burst and animation do not overshoot");
        var edge = new PageEdgeDwell();
        Check(edge.Step(1, 0) == 0 && edge.Step(1, 499) == 0 && edge.Step(1, 500) == 1 &&
            edge.Step(1, 1800) == 0, "edge flips once at 500ms while held");
        Check(edge.Step(null, 1801) == 0 && edge.Step(-1, 2500) == 0 &&
            edge.Step(0, 2510) == 0 && edge.Step(-1, 2520) == 0 && edge.Step(-1, 3020) == -1,
            "leaving the previous edge clears its latch and entering another edge starts a fresh dwell");
        var shown = new DropTarget(a, 1, DropKind.Merge, 5, b);
        Check(shown.MatchesRelease(shown with { }) && !shown.MatchesRelease(shown with { Page = 2 }) &&
            !shown.MatchesRelease(shown with { Drawer = c }) && !shown.MatchesRelease(shown with { Receiver = c }) &&
            !shown.MatchesRelease(shown with { Index = 6 }) && !shown.MatchesRelease(shown with { Kind = DropKind.Swap }),
            "release must match the drawer, page, cell, receiver and operation that was shown");
        var pending = shown with { Kind = DropKind.MergePending };
        Check(!pending.MatchesRelease(pending) && !pending.MatchesRelease(shown) && !shown.MatchesRelease(null),
            "a pending merge or an unsettled surface cannot commit");
        var openHolds = new DrawerOpenHolds();
        var menu = openHolds.Acquire();
        var picker = openHolds.Acquire();
        menu.Dispose();
        menu.Dispose();
        Check(openHolds.Active, "closing the menu twice leaves the picker hold intact");
        picker.Dispose();
        Check(!openHolds.Active, "closing the final operation releases the drawer");
        var interaction = new DrawerInteraction();
        interaction.KeepOpen();
        interaction.Update(0, .016, true, false, false);
        interaction.Update(DrawerSettings.CloseDelayMs - 1, .016, true, false, false);
        Check(interaction.TargetOpen, "after the last hold leaves, closing still uses the normal pointer-out delay");
        interaction.Update(DrawerSettings.CloseDelayMs, .016, true, false, false);
        Check(!interaction.TargetOpen, "the normal close delay expires when the pointer stays outside");
        var motion = new FolderMotion(0, 1, 0);
        double now = DrawerSettings.FolderAnimationMs / 3d;
        double shownBlur = motion.Sample(now);
        motion.Retarget(0, now);
        Check(shownBlur is > 0 and < 1 && motion.Sample(now) == shownBlur &&
            motion.Sample(now + 1) < shownBlur && motion.Sample(now + DrawerSettings.FolderAnimationMs) == 0,
            "reversing a folder animation continues from the current blur and reaches the closed state");
        var grid = LayoutMetrics.Fit(new(1, 1, 16), 800, 600) with { CellWidth = 48, CellHeight = 40, TilePadding = 2 };
        double center = grid.TilePadding + grid.Effective.IconSize / 2d;
        double margin = grid.Effective.IconSize * .33;
        Check(GroupHover.IsCenter(24, center, grid) && !GroupHover.IsCenter(4, center, grid) &&
            !GroupHover.IsCenter(24, 1, grid) && !GroupHover.IsCenter(24, 38, grid),
            "small icon visual center receives and all four edges still sort");
        Check(!GroupHover.IsCenter(24 + margin, center, grid) && GroupHover.IsCenter(24 + margin, center, grid, retaining: true) &&
            !GroupHover.IsCenter(24 + grid.Effective.IconSize * .37, center, grid, retaining: true),
            "receiver hysteresis stays inside 72 percent of the normal icon bounds");
        foreach (double dpi in new[] { 1d, 1.25, 1.5, 2d })
            Check(GroupHover.IsCenter((24 * dpi) / dpi, (center * dpi) / dpi, grid) &&
                !GroupHover.IsCenter(((24 + margin) * dpi) / dpi, (center * dpi) / dpi, grid),
                $"logical hit region stays fixed at {dpi:P0} scaling");
        var deadline = new PageSnapDeadline();
        Check(!deadline.Expired(250, 0) && !deadline.Expired(999, 0) && deadline.Expired(1000, 0),
            "a page slide times out even when no viewport ever becomes available");
        deadline.Reset();
        Check(!deadline.Expired(2000, null) && !deadline.Expired(2749, null) && deadline.Expired(2750, null),
            "aborting one page snap clears its deadline for the next viewport");

        string project = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(project, "Drawer.sln"))) project = Directory.GetParent(project)!.FullName;
        string root = Path.Combine(project, "temp", "page-tests-" + Guid.NewGuid().ToString("N"));
        var store = new ShortcutStore(root);
        store.Load();
        string[] paths = Enumerable.Range(0, 3).Select(i =>
        {
            string path = Path.Combine(root, $"page-{i}.url");
            File.WriteAllText(path, $"[InternetShortcut]\nURL=https://example.com/{i}");
            return path;
        }).ToArray();
        store.Import(paths.Take(2));
        string file = Path.Combine(root, "library.json");
        var first = store.TopSlots[0];
        var second = store.TopSlots[1];
        File.WriteAllText(file, JsonSerializer.Serialize(new ShortcutDocument(2, store.Items, TopSlots:
            new() { first, null, null, null, null, null, null, null, second })));
        store.Load();
        Check(TopGridSlots.PageCount(store.TopSlots.Count, 4) == 3 && store.TopSlots.Skip(4).Take(4).All(id => id == hole),
            "legacy sparse linear slots yield a formal empty middle page");
        ShortcutStore.MoveItems(store, store, new[] { second }, new(null, null, 1, new[] { 8 }));
        store.Load();
        Check(store.TopSlots.Count == 9 && store.TopSlots.Skip(4).All(id => id == hole), "evacuated formal pages survive restart");
        store.Import(paths.Skip(2));
        Check(store.TopSlots.Count == 10 && store.TopSlots[8] == hole && store.TopSlots[9] != hole, "import appends after recorded cells, including trailing holes");
        store.ClearEmptySlots();
        store.Load();
        Check(store.TopSlots.Count == 3 && store.TopSlots[0] == first && store.TopSlots[1] == second &&
            TopGridSlots.PageCount(store.TopSlots.Count, 4) == 1, "explicit compaction drops excess pages");
        Console.WriteLine($"ALL {checks} PAGE CHECKS PASSED");
    }
}
