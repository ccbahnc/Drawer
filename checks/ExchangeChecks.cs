using Drawer.Core;

internal static class ExchangeChecks
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string label)
        {
            if (!value) { RegressionAssertions.Fail("exchange: " + label); return; }
            checks++;
            Console.WriteLine("PASS exchange: " + label);
        }

        var id = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        var (a, b, c, d, e, f) = (id[0], id[1], id[2], id[3], id[4], id[5]);
        Guid empty = Guid.Empty;
        List<Guid> Place(Guid[] slots, int start, params int[] sourceCells)
        {
            var working = slots.ToList();
            var batch = sourceCells.Select(i => working[i]).ToArray();
            TopGridSlots.Vacate(working, batch.ToHashSet());
            TopGridSlots.Place(working, sourceCells, start, batch);
            return working;
        }
        Check(Place([a, b, c, d], 2, 0).SequenceEqual(new[] { c, b, a, d }), "single swap touches exactly two cells");
        Check(Place([a, b, empty, d], 2, 0).SequenceEqual(new[] { empty, b, a, d }), "single item moves into an empty cell");
        Check(Place([a, b, c, d, e, f], 2, 1, 3, 5).SequenceEqual(new[] { a, c, b, d, f, e }),
            "overlapping batch refills only unclaimed source slots in grid order");
        Check(Place([a, b, c, d, e, f], 4, 0, 1, 3).SequenceEqual(new[] { e, f, c, empty, a, b, d }),
            "batch crosses page boundary without disturbing unrelated cells");
        Check(Place([a, b, empty, d, e], 2, 0, 3).SequenceEqual(new[] { empty, b, a, d, e }),
            "partly empty landing needs no displacement");
        Check(Place([a, b, c, d], 0, 0, 1).SequenceEqual(new[] { a, b, c, d }), "dropping onto the exact source range is stable");

        var preview = new DragPreview(new[] { a, b, c, d, e, f }, new[] { a, c }, fixedCells: true);
        preview.PreviewInsertion(4);
        Check(preview.Index == 4 && preview.VisualSlots.SequenceEqual(new[] { e, b, empty, d, empty, f }),
            "multiple items preview only the first target and one displaced icon");
        preview.Cancel();
        Check(!preview.Valid && preview.VisualSlots.SequenceEqual(new[] { a, b, c, d, e, f }), "cancel restores complete recorded layout");

        var to = new List<Guid> { d, empty, e, f };
        TopGridSlots.Insert(to, 2, new[] { a, c });
        Check(to.SequenceEqual(new[] { d, empty, a, c, e, f }), "cross-drawer batch inserts exactly at target and pushes the tail past a full page");
        var crossPreview = new DragPreview(new[] { d, empty, e, f }, new[] { a, c }, fixedCells: true, insertIntoDrawer: true);
        crossPreview.PreviewInsertion(2);
        Check(crossPreview.HiddenItem == null && crossPreview.VisualSlots.SequenceEqual(new[] { d, empty, empty, e, f }),
            "cross-drawer preview opens one transparent target slot without removing the occupant");
        var members = new List<Guid> { d, e, empty, f };
        TopGridSlots.Place(members, Array.Empty<int>(), 0, new[] { a });
        Check(members.SequenceEqual(new[] { a, e, d, f }), "a folder-member drop uses the next destination vacancy when no top-level source exists");
        var extractPreview = new DragPreview(new[] { d, e, empty, f }, new[] { a }, fixedCells: true);
        extractPreview.PreviewInsertion(0);
        Check(extractPreview.HiddenItem == null && extractPreview.VisualSlots.SequenceEqual(new[] { empty, e, d, f }),
            "the first displaced resident stays visible at the vacancy during member-extraction preview");

        foreach (double x in new[] { 1d, 220d, 438d })
        {
            // The 334 DIP window has a 16 DIP lower band. Its first pixel is 318.
            Check(Band(x, -29) == -1 && Band(x, 318) == 1 && Band(x, 317) == 0,
                $"navigation and bottom overlap include x={x} DIP independent of occupied cells");
            Check(Band(x, -59) == -1 && Band(x, 353) == 1,
                $"vertical tolerance remains part of each edge band at x={x} DIP");
        }
        Check(Band(220, -61) == null && Band(447, 317) == null && Band(220, 29) == 0,
            "outside tolerance, horizontal overrun and the central drop area remain distinct");
        var edge = new PageEdgeDwell();
        edge.Step(1, 0);
        edge.Disarm();
        Check(edge.Step(1, 900) == 0 && edge.Step(0, 910) == 0 &&
            edge.Step(1, 920) == 0 && edge.Step(1, 1420) == 1, "wheel invalidates an armed edge until pointer returns inside");
        edge.Reset();
        int? Band(double x, double y) => PageEdgeDwell.BandAt(x, y, 440, -40, 334, 324, 81);
        Check(edge.Step(Band(20, 318), 0) == 0 && edge.Step(Band(220, 326), 250) == 0 &&
            edge.Step(Band(438, 345), 500) == 1 && edge.Step(Band(220, 321), 1200) == 0,
            "continuous movement across the bottom row, padding and tolerance keeps one dwell");
        Check(Band(220, 354) == null && edge.Step(null, 1250) == 0 &&
            edge.Step(Band(220, 345), 1300) == 0 && edge.Step(Band(220, 345), 1800) == 1,
            "leaving the full edge band permits a new dwell after reentry");

        VerifyDropImport(Check);

        var pop = new SelectionScale(1, DrawerSettings.SelectionHeldScale, 100, Pop: true);
        Check(pop.At(100) == 1 && Math.Abs(pop.At(176) - .9) < .0001 && Math.Abs(pop.At(300) - 1.08) < .0001,
            "selection pulse dips then holds 108 percent");
        var restore = new SelectionScale(pop.At(200), 1, 200);
        Check(restore.At(200) == pop.At(200) && restore.At(320) == 1, "deselect or add another selection restores from the current scale");
        double hover = SelectionScale.Target(true, false, false);
        Check(hover == 1.08 && SelectionScale.Target(true, true, false) == hover && SelectionScale.Target(false, true, false) == hover &&
            SelectionScale.Target(true, true, true) == 1 && SelectionScale.Target(false, false, false) == 1,
            "hover and single selection share one zoom target while dragging suppresses both");
        var popFromHover = new SelectionScale(hover, hover, 0, Pop: true);
        Check(popFromHover.At(0) == hover && Math.Abs(popFromHover.At(76) - .9) < .0001 && popFromHover.At(200) == hover,
            "selection pulse starts at the current hover size and dips without snapping to 100 percent");
        var compact = LayoutMetrics.Fit(new(IconSize: 16), 100, 100) with { TilePadding = 0, NameGap = 0 };
        Check(Math.Abs(compact.PictureSize(member: true) * hover - compact.IconSize) < .0001,
            "compact folder zoom fits above the label without borrowing padding");
        VerifyIconContent(Check);

        Console.WriteLine($"{checks} EXCHANGE CHECKS PASSED");
    }

    private static void VerifyIconContent(Action<bool, string> check)
    {
        byte[] small = new byte[64 * 64 * 4];
        for (int y = 20; y < 44; y++)
        for (int x = 19; x < 45; x++) small[(y * 64 + x) * 4 + 3] = 255;
        small[(46 * 64 + 33) * 4 + 3] = 9;
        var source = new IconPixels(64, 64, small);
        var fitted = source.FitVisibleContent();
        check(fitted.Width == fitted.Height && fitted.Width is >= 48 and < 64 &&
            fitted.Bgra.Where((_, i) => i % 4 == 3 && fitted.Bgra[i] != 0).SequenceEqual(
                small.Where((_, i) => i % 4 == 3 && small[i] != 0)),
            "transparent padded icons enlarge at most 1.35x while preserving the entire alpha silhouette and shadow");
        var opaque = new IconPixels(64, 64, Enumerable.Repeat((byte)255, 64 * 64 * 4).ToArray());
        check(ReferenceEquals(opaque, opaque.FitVisibleContent()), "opaque brand plate and square outline are retained");
        small[(32 * 64) * 4 + 3] = 1;
        small[(32 * 64 + 63) * 4 + 3] = 1;
        check(ReferenceEquals(source, source.FitVisibleContent()), "long translucent shadow is never clipped to increase icon size");
        var boxed = new byte[32 * 32 * 4];
        for (int y = 0; y < 32; y++)
        for (int x = 0; x < 32; x++)
        {
            int p = (y * 32 + x) * 4;
            if (x is 0 or 31 || y is 0 or 31) boxed[p + 3] = 100;
            else if (x is >= 8 and < 24 && y is >= 8 and < 24) { boxed[p] = 255; boxed[p + 3] = 255; }
        }
        var framed = new IconPixels(32, 32, boxed);
        var cleared = framed.RemoveDetachedHairline();
        check(cleared.Bgra[3] == 0 && boxed[3] == 100 && cleared.Bgra[4 * (16 * 32 + 16)] == 255,
            "faint detached outer frame is removed without editing the inner icon or input pixels");
        boxed[7] = 255;
        check(ReferenceEquals(framed, framed.RemoveDetachedHairline()), "opaque outline is retained");

        var padded = new byte[96 * 96 * 4];
        for (int y = 29; y <= 66; y++)
        for (int x = 29; x <= 66; x++)
        {
            int pixel = (y * 96 + x) * 4;
            padded[pixel + 2] = padded[pixel + 3] = 255;
        }
        padded[(28 * 96 + 40) * 4 + 3] = 12; // Artwork shadow inside the frame.
        var composite = (byte[])padded.Clone();
        for (int y = 0; y < 96; y++)
        for (int x = 0; x < 96; x++)
        {
            int depth = Math.Min(Math.Min(x, 95 - x), Math.Min(y, 95 - y));
            if (depth > 1 || depth == 0 && x == 47 && y == 0) continue;
            int pixel = (y * 96 + x) * 4;
            composite[pixel] = composite[pixel + 1] = composite[pixel + 2] = (byte)(depth == 0 ? 19 : 36);
            composite[pixel + 3] = (byte)(depth == 0 ? 48 : 36);
        }
        var shellFrame = new IconPixels(96, 96, composite);
        check(shellFrame.RemoveDetachedHairline().Bgra.SequenceEqual(padded) && composite[3] == 48,
            "two-pixel antialiased Shell frame is removed while artwork and shadow are unchanged");
        for (int y = 2; y < 29; y++) composite[(y * 96 + 40) * 4 + 3] = 100;
        check(ReferenceEquals(shellFrame, shellFrame.RemoveDetachedHairline()), "frame connected to artwork is retained");
        var openOutline = (byte[])padded.Clone();
        for (int x = 0; x < 96; x++) openOutline[x * 4 + 3] = 80;
        var shadow = new IconPixels(96, 96, openOutline);
        check(ReferenceEquals(shadow, shadow.RemoveDetachedHairline()), "one-sided translucent detail is retained");
    }

    private static void VerifyDropImport(Action<bool, string> check)
    {
        string project = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(project, "Drawer.sln"))) project = Directory.GetParent(project)!.FullName;
        string root = Path.Combine(project, "temp", "drop-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = Enumerable.Range(0, 9).Select(index =>
        {
            string path = Path.Combine(root, $"item-{index}.url");
            File.WriteAllText(path, $"[InternetShortcut]\nURL=https://example.com/{index}");
            return path;
        }).ToArray();
        var originals = paths.Select(File.ReadAllText).ToArray();
        var store = new ShortcutStore(Path.Combine(root, "data"));
        store.Load();
        store.Import(paths.Take(4));
        var initial = store.TopSlots.ToArray();
        var result = store.Import(new[] { paths[4], paths[0], paths[5], paths[8] + ".txt" }, new(null, null, 2));
        var added = store.Items.Where(e => !initial.Contains(e.Id)).Select(e => e.Id).ToArray();
        check(result.Added == 2 && result.Duplicates == 1 && result.Errors.Count == 1 &&
            store.TopSlots.SequenceEqual(new[] { initial[0], initial[1], added[0], added[1], initial[2], initial[3] }),
            "file batch inserts at captured slot through duplicates, failure and a full page");
        Guid folder = store.CreateFolder(new[] { initial[1], initial[0] });
        store.Import(new[] { paths[6] }, new(folder, null, 1));
        check(store.Groups.Single().Members.SequenceEqual(new[] { initial[0], store.Items.Single(e => e.Name == "item-6").Id, initial[1] }) &&
            store.TopSlots[0] == folder && store.TopSlots[1] == Guid.Empty, "folder import uses captured folder ID and member index");
        store.Import(new[] { paths[7] });
        check(store.TopSlots[1] == Guid.Empty && store.TopSlots.Last() == store.Items.Single(e => e.Name == "item-7").Id,
            "picker-style import appends after recorded slots instead of filling holes");
        var reopened = new ShortcutStore(store.Root);
        reopened.Load();
        check(reopened.TopSlots.SequenceEqual(store.TopSlots) && reopened.Groups.Single().Members.SequenceEqual(store.Groups.Single().Members) &&
            paths.Select(File.ReadAllText).SequenceEqual(originals), "restart preserves imported slots and groups with every source intact");

        var blocked = new ShortcutStore(Path.Combine(root, "blocked"));
        blocked.Load();
        Directory.CreateDirectory(Path.Combine(blocked.Root, "library.json"));
        bool failed = false;
        try { blocked.Import(paths.Take(2), new(null, null, 1)); }
        catch (IOException) { failed = true; }
        check(failed && blocked.Items.Count == 0 && blocked.TopSlots.Count == 0 &&
            !Directory.EnumerateFileSystemEntries(Path.Combine(blocked.Root, "Shortcuts")).Any() &&
            paths.Select(File.ReadAllText).SequenceEqual(originals), "failed import save clears the staged batch without touching sources");

        using var cancellation = new CancellationTokenSource();
        var before = File.ReadAllBytes(Path.Combine(store.Root, "library.json"));
        string[] ManagedFiles() => Directory.GetFiles(Path.Combine(store.Root, "Shortcuts"), "*", SearchOption.AllDirectories).Order().ToArray();
        var copies = ManagedFiles();
        IEnumerable<string> CancelAfterStaging()
        {
            yield return paths[8];
            check(ManagedFiles().Length == copies.Length + 1,
                "cancel test reaches the point after copying an uncommitted shortcut");
            cancellation.Cancel();
        }
        bool canceled = false;
        try { store.Import(CancelAfterStaging(), new(null, null, 2), cancellation.Token); }
        catch (OperationCanceledException) { canceled = true; }
        check(canceled && before.SequenceEqual(File.ReadAllBytes(Path.Combine(store.Root, "library.json"))) &&
            copies.SequenceEqual(ManagedFiles()) &&
            paths.Select(File.ReadAllText).SequenceEqual(originals), "cancel discards only staged copies and keeps saved slots and originals");
    }
}

