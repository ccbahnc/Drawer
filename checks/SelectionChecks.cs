using Drawer.Core;

internal static class SelectionChecks
{
    public static void Run()
    {
        var order = Enumerable.Range(0, 45).Select(_ => Guid.NewGuid()).ToArray();
        var selection = new ItemSelection();
        int checks = 0;
        void Check(bool ok, string title)
        {
            if (!ok) throw new Exception("selection: " + title);
            checks++;
            Console.WriteLine("PASS selection: " + title);
        }

        bool Is(params int[] slots) => order.Where(selection.Contains).SequenceEqual(slots.Select(i => order[i]));
        void Click(int slot, bool shift = false, bool control = false) =>
            selection.Click(order, order[slot], range: shift, additive: control);

        Click(3, shift: true);
        Check(selection.Active && selection.Anchor == order[3] && Is(3), "shift without an anchor selects only the endpoint");
        Check(!selection.IsPartOfMultiSelection(order[3]), "the sole selected tile is a single-item rename target");
        Click(7, shift: true);
        Check(selection.IsPartOfMultiSelection(order[3]) && selection.IsPartOfMultiSelection(order[7]) &&
            !selection.IsPartOfMultiSelection(order[8]), "right-clicking an unselected tile remains single-item even with an active batch");
        Click(5, shift: true);
        Check(selection.Anchor == order[3] && Is(3, 4, 5), "repeated shift shrinks forward range without moving the anchor");
        Click(1, shift: true);
        Check(selection.Anchor == order[3] && Is(1, 2, 3), "same anchor supports a backward range");
        Click(8, control: true);
        Click(10, shift: true, control: true);
        Check(selection.Anchor == order[8] && Is(1, 2, 3, 8, 9, 10), "ctrl shift appends range and preserves disconnected items");
        Click(8, control: true);
        Check(selection.Anchor == order[10] && Is(1, 2, 3, 9, 10), "toggling off the anchor falls back to the last selected item in order");
        Click(41, shift: true);
        Check(selection.Anchor == order[10] && Is(Enumerable.Range(10, 32).ToArray()), "selection spans offscreen cells in model order");
        var saved = selection.Capture();
        selection.Clear();
        Check(!selection.Active && selection.Anchor == null && Is(), "clearing a view removes the range anchor");
        selection.Restore(saved);
        Check(selection.Active && selection.Anchor == order[10] && Is(Enumerable.Range(10, 32).ToArray()), "canceled drag restores range and anchor");
        selection.Prune(order.Where(id => id != order[10]).ToArray());
        Check(selection.Anchor == order[41] && !selection.Contains(order[10]), "removed anchor is discarded when refreshing the view");
        selection.Prune(order.Take(10).ToArray());
        Check(!selection.Active && selection.Anchor == null && Is(), "removing all selected items clears selection mode");
        selection.Click(order, Guid.Empty, range: true, additive: false);
        Check(!selection.Active && selection.Anchor == null, "action tile is outside the selectable order");
        Console.WriteLine($"ALL {checks} SELECTION CHECKS PASSED");
    }
}
