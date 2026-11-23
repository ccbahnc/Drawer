using Drawer.Core;

internal static class FolderInsertionChecks
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("folder insertion: " + name);
            Console.WriteLine("PASS folder insertion: " + name);
            checks++;
        }
        Guid[] ids = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray();
        var (a,b,c,d,e) = (ids[0],ids[1],ids[2],ids[3],ids[4]);
        var group = Guid.NewGuid();
        var one = LayoutMetrics.Fit(new(1, 3), 900, 900) with { CellWidth = 60, CellHeight = 40 };
        var three = one with { VisibleColumns = 3 };
        var local = new FolderInsertionSequence(group, ids.Take(5).ToArray(), new[] { b, d });
        var incoming = new FolderInsertionSequence(group, new[] { a,c,e }, new[] { ids[5],ids[6] });
        var copy = new FolderInsertionSequence(group, new[] { a,c,e }, Array.Empty<Guid>());
        foreach (var sequence in new[] { local, incoming, copy })
        {
            Check(sequence.Hit(1, 45, 0, 120, one)?.Index == 1 &&
                sequence.Hit(59, 45, 0, 120, one)?.Index == 1 &&
                sequence.Hit(1, 75, 0, 120, one)?.Index == 2 &&
                sequence.Hit(59, 75, 0, 120, one)?.Index == 2,
                "single-column before/after depends only on the vertical half");
        }
        Check(copy.Hit(10, 10, 0, 160, one)?.Index == 0 && copy.Hit(10, 145, 0, 160, one)?.Index == 3 &&
            new FolderInsertionSequence(group, Array.Empty<Guid>(), Array.Empty<Guid>()).Hit(10, 50, 0, 120, one)?.Index == 0,
            "first position, tail whitespace and an empty member sequence are valid");
        Check(copy.Hit(60, 30, 0, 120, one) == null && copy.Hit(1, -1, 0, 120, one) == null &&
            copy.Hit(1, 120, 0, 120, one) == null && copy.Hit(1, 0, 0, 0, one) == null,
            "header, gutter, outside viewport and unavailable viewport are invalid");
        var anchor = local.Hit(30, 70, 0, 120, one)!;
        Check(anchor.Before == c && anchor.After == e && anchor.PreviewOrder().SequenceEqual(new[] { a,c,b,d,e }),
            "noncontiguous selection shows the final order without old gaps");
        Check(local.Hit(20, 105, 0, 120, one, anchor) == anchor &&
            local.Hit(20, 12, 160, 120, one, anchor) == anchor &&
            local.Hit(20, 34, 160, 120, one, anchor)?.Index == 3,
            "the gap and projected neighbors resolve to stable anchors through scrolling");
        Check(local.Hit(25, 13, 51, 120, one) == anchor, "fractional offset is included exactly once");
        Check(local.Hit(161, 12, 0, 120, three)?.Index == 3 &&
            local.Hit(6, 42, 0, 120, three)?.Index == 3 &&
            local.Hit(65, 30, 0, 120, three)?.Index == 1, "multi-column hits use row-major order across a wrap");
        var contiguous = new FolderInsertionSequence(group, new[] { a,b,c,d,e }, new[] { b,c });
        Check(new FolderInsertion(contiguous, 1).PreviewOrder().SequenceEqual(new[] { a,b,c,d,e }) &&
            new FolderInsertion(contiguous, 0).PreviewOrder().SequenceEqual(new[] { b,c,a,d,e }) &&
            new FolderInsertion(contiguous, 3).PreviewOrder().SequenceEqual(new[] { a,d,e,b,c }),
            "contiguous batch remains stable when moved backward, forward or to its original position");

        string project = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(project, "Drawer.sln"))) project = Directory.GetParent(project)!.FullName;
        string root = Path.Combine(project, "temp", "folder-insertion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string[] paths = Enumerable.Range(0, 7).Select(i =>
        {
            string path = Path.Combine(root, $"{i}.url");
            File.WriteAllText(path, $"[InternetShortcut]\nURL=https://example.com/v33/{i}");
            return path;
        }).ToArray();
        var store = new ShortcutStore(Path.Combine(root, "store"));
        store.Load();
        store.Import(paths.Take(5));
        var members = store.TopOrder.ToArray();
        Guid folder = store.CreateFolder(members);
        var moving = new[] { members[1], members[3] };
        var position = new FolderInsertion(new FolderInsertionSequence(folder, members, moving), 2);
        var drop = new ItemDrop(folder, null, position.Index, ExpectedSourceFolder: folder,
            ExpectedMemberIndices: new[] { 1, 3 }, MemberPlacement: position);
        ShortcutStore.MoveItems(store, store, moving, drop);
        Check(store.Groups.Single().Members.SequenceEqual(position.PreviewOrder()), "persisted internal batch is exactly the preview order");

        var current = store.Groups.Single().Members.ToArray();
        var importPlan = new FolderInsertion(new FolderInsertionSequence(folder, current, Array.Empty<Guid>()), 2);
        var importDrop = new ItemDrop(folder, null, 2, MemberPlacement: importPlan);
        store.ReorderMembers(folder, current.Reverse());
        bool rejected = false;
        try { store.Import(paths.Skip(5), importDrop); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && store.Items.Count == 5 && store.Groups.Single().Members.SequenceEqual(current.Reverse()),
            "changed target anchor rejects copy without inserting anything");
        store.ReorderMembers(folder, current);
        var result = store.Import(paths.Skip(5).Append(Path.Combine(root, "missing.url")), importDrop);
        var copied = store.Items.Where(item => !members.Contains(item.Id)).Select(item => item.Id).ToArray();
        store.Load();
        Check(result.Added == 2 && result.Errors.Count == 1 && paths.All(File.Exists) &&
            store.Groups.Single().Members.SequenceEqual(current.Take(2).Concat(copied).Concat(current.Skip(2))),
            "copy with a partial failure retains the confirmed neighbors, source files and restart order");
        Console.WriteLine($"ALL {checks} FOLDER INSERTION CHECKS PASSED");
    }
}
