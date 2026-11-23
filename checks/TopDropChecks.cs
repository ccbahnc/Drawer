using Drawer.Core;

internal static class TopDropChecks
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool ok, string scenario)
        {
            if (!ok) throw new Exception("top drop: " + scenario);
            checks++;
            Console.WriteLine("PASS top drop: " + scenario);
        }

        var layout = LayoutMetrics.Fit(new(), 1500, 1000);
        var cell = new DropCell(Guid.NewGuid(), 0, 7, Guid.NewGuid(), false, layout.CellWidth, layout.CellHeight, layout);
        double x = cell.Left + layout.CellWidth / 2;
        double y = cell.Top + layout.TilePadding + layout.Effective.IconSize / 2d;
        var hover = new TopDropHover();
        TopDropAction At(double mx, double my, double time, bool publish = true) => hover.Evaluate(cell, mx, my, time, publish);

        var directions = new[] { (cell.Left + 1, y), (cell.Left + layout.CellWidth - 1, y),
            (x, cell.Top + 1), (x, cell.Top + layout.CellHeight - 1) };
        for (int i = 0; i < directions.Length; i++)
        {
            hover.Reset();
            var (mx, my) = directions[i];
            Check(At(mx, my, 0) == TopDropAction.EdgePending && At(mx, my, 249) == TopDropAction.EdgePending &&
                At(x, y, 249) == TopDropAction.MergePending && At(x, y, 448, false) == TopDropAction.MergePending &&
                At(x, y, 449, false) == TopDropAction.MergeReady && hover.Candidate == cell,
                $"approach from {new[] { "left", "right", "top", "bottom" }[i]} crosses edge then qualifies the same receiver");
        }

        double edgeX = cell.Left + 1;
        hover.Reset();
        Check(At(edgeX, y, 0) == TopDropAction.EdgePending && At(edgeX, y + 2, 100) == TopDropAction.EdgePending &&
            At(edgeX, y + 4, 249) == TopDropAction.EdgePending && At(edgeX, y + 4, 250) == TopDropAction.EdgeReady,
            "edge previews at 250 ms when movement stays within 4 DIP of the anchor");
        Check(At(x, y, 251) == TopDropAction.MergePending && At(x, y, 450) == TopDropAction.MergePending &&
            At(x, y, 451) == TopDropAction.MergeReady,
            "even a published swap preview yields to center on the original recorded slot");
        var wobble = layout.Effective.IconSize * .33;
        Check(At(x + wobble, y, 452) == TopDropAction.MergeReady &&
            At(x, y + wobble, 453) == TopDropAction.MergeReady, "minor horizontal and vertical jitter keep the same merge timer");
        Check(At(edgeX, y, 454) == TopDropAction.EdgePending && At(x, y, 460) == TopDropAction.MergePending &&
            At(x, y, 659, false) == TopDropAction.MergePending, "leaving the receiver restarts both dwell paths");
        Check(hover.Evaluate(cell with { Page = 1 }, x, y, 900, publish: false) == TopDropAction.MergePending &&
            hover.Evaluate(cell with { Item = Guid.NewGuid() }, x, y, 901, publish: false) == TopDropAction.MergePending &&
            hover.Candidate == cell && At(x, y, 902, false) == TopDropAction.MergeReady,
            "release cannot inherit another item's or page's timer and does not mutate the observed candidate");
        hover.Reset();
        Check(At(x, y, 1500, false) == TopDropAction.MergePending && hover.Candidate == null,
            "cancel and paging leave a subsequent unobserved center unqualified");
        const double fractionalStart = 900.1001;
        At(x, y, fractionalStart);
        Check(At(x, y, fractionalStart + 199.999, false) == TopDropAction.MergePending &&
            At(x, y, fractionalStart + 200, false) == TopDropAction.MergeReady,
            "a fractional clock reading qualifies at its exact deadline");

        hover.Reset();
        double edgeY = cell.Top + layout.CellHeight - 1;
        Check(At(x, edgeY, 0) == TopDropAction.EdgePending && At(x + 2, edgeY, 100) == TopDropAction.EdgePending &&
            At(x + 4, edgeY, 200) == TopDropAction.EdgePending && At(x + 6, edgeY, 249) == TopDropAction.EdgePending &&
            At(x + 6, edgeY, 250) == TopDropAction.EdgePending && At(x + 6, edgeY, 498) == TopDropAction.EdgePending &&
            At(x + 6, edgeY, 499) == TopDropAction.EdgeReady,
            "small steps that cumulatively exceed the anchor tolerance restart the 250 ms edge dwell");
        Check(At(x + 11, edgeY, 500) == TopDropAction.EdgePending && At(x + 11, edgeY, 750) == TopDropAction.EdgeReady,
            "leaving the anchor after the preview returns the edge to pending until the new dwell expires");

        hover.Reset();
        Check(At(edgeX, y, 0) == TopDropAction.EdgePending && At(edgeX, y, 50, false) == TopDropAction.EdgePending &&
            hover.Candidate == cell && hover.Action == TopDropAction.EdgePending && hover.Elapsed(50) == 50,
            "early edge release resolves the same candidate without advancing the preview state");

        hover.Reset();
        Check(hover.Evaluate(cell, x, y, 0, publish: true, allowReceive: false) == TopDropAction.EdgePending &&
            hover.Evaluate(cell, x, y, 249, publish: true, allowReceive: false) == TopDropAction.EdgePending &&
            hover.Evaluate(cell, x, y, 250, publish: true, allowReceive: false) == TopDropAction.EdgeReady,
            "external copy on an ordinary icon uses insertion dwell even at the center");
        hover.Reset();

        var folder = cell with { IsFolder = true };
        Check(hover.Evaluate(folder, x, y, 0, true) == TopDropAction.Append &&
            hover.Evaluate(folder, x, y, 599, true) == TopDropAction.Append &&
            hover.Evaluate(folder, x, y, 600, true) == TopDropAction.OpenFolder,
            "existing folder accepts immediately and opens only after 600 ms");
        Check(hover.Evaluate(folder with { Item = Guid.NewGuid() }, x, y, 601, true) == TopDropAction.Append &&
            hover.Evaluate(folder, x, y, 800, true) == TopDropAction.Append,
            "changing folder receiver resets its open timer");
        Console.WriteLine($"ALL {checks} TOP DROP CHECKS PASSED");
    }
}
