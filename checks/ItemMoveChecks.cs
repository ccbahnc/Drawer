using Drawer.Core;

internal static class ItemMoveChecks
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool result, string label)
        {
            if (!result) throw new Exception("item move: " + label);
            checks++;
            Console.WriteLine("PASS item move: " + label);
        }

        string project = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(project, "Drawer.sln"))) project = Directory.GetParent(project)!.FullName;
        string root = Path.Combine(project, "temp", "item-move-" + Guid.NewGuid().ToString("N"));
        var catalog = new MultiDrawerCatalog(root);
        catalog.Load(new DrawerCatalog(root));
        var source = catalog.Value.Drawers[0].Id;
        var target = catalog.Value.Drawers[1].Id;
        var from = catalog.OpenStore(source);
        var to = catalog.OpenStore(target);
        var paths = Enumerable.Range(0, 8).Select(i =>
        {
            string path = Path.Combine(root, "source-" + i + ".url");
            File.WriteAllText(path, "[InternetShortcut]\nURL=https://example.com/" + i);
            return path;
        }).ToArray();
        from.Import(paths.Take(5)); to.Import(paths.Skip(5));
        var original = paths.Select(File.ReadAllText).ToArray();
        var f = from.TopOrder.ToArray();
        var t = to.TopOrder.ToArray();
        var moving = from.Merge(f[0], f[1]);
        from.Merge(f[2], moving);
        from.RenameGroup(moving, "跨抽屉文件夹");
        var members = from.Groups.Single().Members.ToArray();
        var copies = from.Items.ToDictionary(e => e.Id, from.CopyPath);
        var targetFolder = to.Merge(t[0], t[1]);
        string manifest = Path.Combine(root, "collection.json");
        byte[] saved = File.ReadAllBytes(manifest);
        catalog.BeforeCommit = _ => throw new IOException("injected commit failure");
        try { catalog.MoveItem(source, from, target, to, moving, new(null, null, 1)); throw new Exception("commit should fail"); }
        catch (IOException) { }
        catalog.BeforeCommit = null;
        Check(saved.SequenceEqual(File.ReadAllBytes(manifest)) && from.Groups.Single().Members.SequenceEqual(members) &&
            catalog.OpenStore(source).TopOrder.Contains(moving) && !catalog.OpenStore(target).TopOrder.Contains(moving),
            "failed manifest commit retains both libraries and their ownership");
        try { catalog.MoveItem(source, from, target, to, moving, new(null, t[2], 0)); throw new Exception("nesting should fail"); }
        catch (ArgumentException) { }
        Check(saved.SequenceEqual(File.ReadAllBytes(manifest)), "a folder cannot be nested by combining it with a plain shortcut");

        bool stale = false;
        try
        {
            catalog.MoveItem(source, from, target, to, members[0],
                new(null, null, 0, ExpectedSourceFolder: moving, ExpectedMemberIndices: new[] { 1 }));
        }
        catch (InvalidOperationException) { stale = true; }
        Check(stale && saved.SequenceEqual(File.ReadAllBytes(manifest)) &&
            from.Groups.Single().Members.SequenceEqual(members) && !to.Items.Any(item => item.Id == members[0]),
            "a member drag with a stale source position leaves both libraries intact");

        catalog.MoveItem(source, from, target, to, moving, new(null, null, 2));
        catalog = new MultiDrawerCatalog(root); catalog.Load(new DrawerCatalog(root));
        from = catalog.OpenStore(source); to = catalog.OpenStore(target);
        Check(from.TopSlots.SequenceEqual(new[] { Guid.Empty, Guid.Empty, Guid.Empty, f[3], f[4] }) &&
            to.TopSlots.SequenceEqual(new[] { Guid.Empty, targetFolder, moving, t[2] }),
            "cross-drawer folder inserts at an occupied cell and leaves the source hole after restart");
        var group = to.Groups.Single(g => g.Id == moving);
        Check(group.Name == "跨抽屉文件夹" && group.Members.SequenceEqual(members) &&
            group.Members.All(id => to.CopyPath(to.Items.Single(e => e.Id == id)) == copies[id]),
            "folder ID, name, order and managed copy paths are preserved");
        catalog.MoveItem(target, to, target, to, members[0], new(targetFolder, null, 1));
        Check(to.Groups.Single(g => g.Id == targetFolder).Members[1] == members[0] &&
            !to.Groups.Single(g => g.Id == moving).Members.Contains(members[0]), "local member moves between folders in a single commit");
        catalog.MoveItem(target, to, source, from, members[1], new(null, f[3], 0));
        to = catalog.OpenStore(target); from = catalog.OpenStore(source);
        Check(!to.Groups.Any(g => g.Id == moving) && to.TopOrder.Contains(members[2]) &&
            from.Groups.Single().Members.SequenceEqual(new[] { f[3], members[1] }),
            "cross-drawer merge preserves target-first order and dissolves the one-member remainder in place");
        var moved = from.Groups.Single().Id;
        catalog.MoveItem(source, from, source, from, members[1], new(null, null, from.TopSlots.IndexOf(moved) + 1));
        Check(!from.Groups.Any(g => g.Id == moved) && from.TopOrder.SequenceEqual(new[] { f[3], members[1], f[4] }),
            "member drop to top level uses the intended insertion slot");
        catalog.Delete(target);
        Check(File.Exists(copies[members[1]]) && paths.Select(File.ReadAllText).SequenceEqual(original),
            "deleting the former owner preserves the moved copy and original shortcut files");

        foreach (var size in new[] { 46d, 87d })
        {
            var parent = LayoutMetrics.Fit(new(1, 1), 600, 600) with { CellHeight = size };
            var overlay = FolderLayout.Fit(parent, parent.CellWidth + 14, size);
            Check(overlay.Grid.Columns == 1 && overlay.Grid.Rows == 1 && overlay.Grid.Height == overlay.Grid.CellHeight &&
                overlay.Top + overlay.Height <= size + .001 && overlay.Inset >= 2 && overlay.TitleGap >= 2 &&
                overlay.TitleTop >= 2 && overlay.Grid.Effective.IconSize >= 8 &&
                overlay.Grid.Effective.IconSize + overlay.Grid.LabelHeight + 2 * overlay.Grid.TilePadding + overlay.Grid.NameGap <= overlay.Grid.CellHeight,
                $"single column includes a complete icon and name with insets at {size} DIP");
        }
        var multi = LayoutMetrics.Fit(new(6, 5), 1200, 1200);
        var compact = FolderLayout.Fit(multi, multi.Width, 5 * multi.CellHeight);
        Check(Math.Abs(compact.Width - .75 * multi.Width) < .001 &&
            Math.Abs(compact.Height - .75 * 5 * multi.CellHeight) < .001 &&
            compact.Left * 2 + compact.Width == multi.Width &&
            compact.Top * 2 + compact.Height == 5 * multi.CellHeight, "folder uses a centered 75 percent viewport");
        Check(compact.Inset == 8 && compact.TitleGap == 6 && compact.TitleTop >= 2 &&
            Math.Abs(compact.Grid.CellWidth * compact.Grid.Columns + FolderLayout.ScrollGutter + 2 * compact.Inset - compact.Width) < .001 &&
            Math.Abs(compact.Grid.CellHeight * compact.Grid.Rows + 2 * compact.Inset - compact.Height) < .001,
            "padding stays outside the scrollable cell area and the caption stays outside the panel");
        foreach (int icon in new[] { 16, 38 })
            for (int count = 1; count <= 4; count++)
            {
                var slots = Enumerable.Range(0, count).Select(i => FolderThumbnailLayout.Slot(icon, i, count)).ToArray();
                Check(slots.All(s => s.X >= .15 * icon - .001 && s.Y >= .15 * icon - .001 &&
                    s.X + s.Size <= .85 * icon + .001 && s.Y + s.Size <= .85 * icon + .001) &&
                    Math.Abs(slots.Min(s => s.X) + slots.Max(s => s.X + s.Size) - icon) < .001 &&
                    Math.Abs(slots.Min(s => s.Y) + slots.Max(s => s.Y + s.Size) - icon) < .001,
                    $"thumbnail centers {count} members and keeps a clear perimeter at {icon} DIP");
            }
        byte[] pixels = new byte[9 * 3 * 4];
        for (int i = 0; i < 27; i++) pixels[i * 4 + 3] = 255;
        pixels[(9 + 4) * 4] = 255;
        var blurred = BackdropBlur.Apply(pixels, 9, 3, 1);
        Check(blurred[(9 + 4) * 4] is > 0 and < 255 && blurred[(9 + 3) * 4] > 0 &&
            blurred.Where((_, i) => i % 4 == 3).All(alpha => alpha == 255),
            "blur spreads a bright pixel to neighboring pixels without losing opaque coverage");
        Console.WriteLine($"ALL {checks} ITEM MOVE CHECKS PASSED");
    }
}
