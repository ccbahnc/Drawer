using Drawer.Core;

internal static class HistoryChecks
{
    public static void Run()
    {
        string project = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(project, "Drawer.sln"))) project = Directory.GetParent(project)!.FullName;
        string root = Path.Combine(project, "temp", "history37-" + Guid.NewGuid().ToString("N"));
        var catalog = new MultiDrawerCatalog(root);
        catalog.Load(new DrawerCatalog(root));
        var active = catalog.Value.Drawers.ToDictionary(drawer => drawer.Id, drawer => catalog.OpenStore(drawer.Id));
        Guid drawer = catalog.Value.Drawers[0].Id;
        Guid other = catalog.Value.Drawers[1].Id;
        int checks = 0;

        void Check(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException("history: " + label);
            Console.WriteLine("PASS history: " + label);
            checks++;
        }
        ShortcutStore Current() => active[drawer];
        bool Restore(bool redo)
        {
            var changed = catalog.ApplyHistory(redo, active.Values);
            if (changed == null) return false;
            foreach (var (id, store) in changed) active[id] = store;
            return true;
        }
        string Fixture(int index)
        {
            string path = Path.Combine(root, $"{index}.url");
            File.WriteAllText(path, $"[InternetShortcut]\nURL=https://example.com/history37/{index}");
            return path;
        }

        var originals = Enumerable.Range(0, 3).Select(Fixture).ToArray();
        using (catalog.BeginHistory(active.Values)) Current().Import(originals);
        var ids = Current().TopOrder.ToArray();
        Check(Restore(false) && Current().Items.Count == 0 && Restore(true) &&
            Current().TopOrder.SequenceEqual(ids) && originals.All(File.Exists), "undo and redo import restore the same IDs and preserve original files");
        using (catalog.BeginHistory(active.Values))
        {
            var moved = catalog.MoveItems(drawer, Current(), other, active[other], new[] { ids[1] }, new(null, null, 4));
            active[drawer] = moved.Source;
            active[other] = moved.Target;
        }
        Check(active[other].TopSlots[4] == ids[1] && Restore(false) && Current().TopOrder.SequenceEqual(ids) &&
            active[other].TopSlots.Count == 0 && Restore(true) && active[other].TopSlots[4] == ids[1] && Restore(false),
            "cross-drawer transfer records both stores in one history change");
        using (catalog.BeginHistory(active.Values)) Current().Import(originals);
        Check(catalog.CanRedo, "duplicate-only import preserves redo");
        Guid folder;
        using (catalog.BeginHistory(active.Values)) folder = Current().CreateFolder(ids);
        Check(!catalog.CanRedo, "new recorded change discards redo");
        using (catalog.BeginHistory(active.Values)) Current().Dissolve(folder, 6);
        Check(Restore(false) && Current().Groups.Single().Members.SequenceEqual(ids) &&
            Restore(true) && Current().Groups.Count == 0 && Restore(false), "dissolve undo and redo preserve member order and folder identity");

        var copies = Current().Items.Select(Current().CopyPath).ToArray();
        var copyBytes = copies.Select(File.ReadAllBytes).ToArray();
        using (catalog.BeginHistory(active.Values)) active[drawer] = catalog.DeleteItems(drawer, Current(), new[] { folder });
        catalog.RetryDeletionCleanup();
        Check(copies.All(File.Exists) && Restore(false) && Current().Groups.Single().Id == folder &&
            Current().Items.Select((entry, index) => File.ReadAllBytes(Current().CopyPath(entry)).SequenceEqual(copyBytes[index])).All(matches => matches) &&
            Restore(true), "deletion cleanup keeps history bytes and undo restores readable identical shortcut files");
        catalog.BeforeCommit = _ => throw new IOException("injected history publication failure");
        try { Restore(false); throw new InvalidOperationException("expected publication failure"); }
        catch (IOException) { }
        catalog.BeforeCommit = null;
        Check(Current().Items.Count == 0 && catalog.CanUndo && !catalog.CanRedo && copies.All(File.Exists) &&
            Restore(false) && Current().Items.Count == 3 && Restore(true), "failed history publication preserves current data and cursor");

        for (int index = 0; index < 55; index++)
            using (catalog.BeginHistory(active.Values)) catalog.Rename(drawer, "历史 " + index);
        int undone = 0;
        while (Restore(false)) undone++;
        Check(undone == 50 && !catalog.CanUndo && catalog.Value.Drawers.Single(d => d.Id == drawer).Name == "历史 4",
            "history evicts older changes after 50 entries");
        int redone = 0;
        while (Restore(true)) redone++;
        Check(redone == 50 && !catalog.CanRedo && catalog.Value.Drawers.Single(d => d.Id == drawer).Name == "历史 54",
            "all retained changes can be redone in order");
        catalog.CloseHistorySession();
        Check(copies.All(path => !File.Exists(path)) && originals.All(File.Exists) && !catalog.CanUndo && !catalog.CanRedo,
            "session cleanup reclaims unreferenced evicted copies and preserves external files");
        string config = Path.Combine(root, "collection.json");
        File.Move(config, config + ".fixture-primary");
        var reopened = new MultiDrawerCatalog(root);
        reopened.Load(new DrawerCatalog(root));
        Check(reopened.OpenStore(drawer).Items.Count == 0 && !reopened.CanUndo && !reopened.CanRedo,
            "backup remains recoverable after cleanup and restart does not retain history");

        active = catalog.Value.Drawers.ToDictionary(definition => definition.Id, definition => catalog.OpenStore(definition.Id));
        using (catalog.BeginHistory(active.Values)) catalog.Update(drawer, definition => definition with { Rows = 2, Columns = 3 });
        Check(Restore(false) && catalog.Value.Drawers.Single(d => d.Id == drawer).Rows != 2 &&
            Restore(true) && catalog.Value.Drawers.Single(d => d.Id == drawer).Columns == 3,
            "drawer layout history restores rows and columns");
        const double scale = 1.25;
        using (catalog.BeginHistory(active.Values)) catalog.Update(drawer, definition => definition with { ContentScale = scale });
        using (catalog.BeginHistory(active.Values)) catalog.Update(drawer, definition => definition with { ContentScale = null });
        Check(Restore(false) && catalog.Value.Drawers.Single(d => d.Id == drawer).ContentScale == scale &&
            Restore(true) && catalog.Value.Drawers.Single(d => d.Id == drawer) is { ContentScale: null, Rows: 2, Columns: 3 },
            "default size is independently undoable without resetting rows or columns");
        Guid added;
        using (catalog.BeginHistory(active.Values)) added = catalog.Add(DrawerEdge.Top);
        Check(Restore(false) && catalog.Value.Drawers.All(d => d.Id != added) &&
            Restore(true) && catalog.Value.Drawers.Any(d => d.Id == added), "drawer creation restores the same stable identity");
        using (catalog.BeginHistory(active.Values)) catalog.Delete(added);
        Check(Restore(false) && catalog.Value.Drawers.Any(d => d.Id == added) &&
            Restore(true) && catalog.Value.Drawers.All(d => d.Id != added), "drawer deletion can be undone and redone");

        var shared = Fixture(10);
        Current().Import(new[] { shared, Fixture(11) });
        active[other].Import(new[] { shared, Fixture(12) });
        var receiver = Current().CreateFolder(Current().TopOrder);
        var origin = active[other].CreateFolder(active[other].TopOrder);
        var sourceMembers = active[other].Groups.Single().Members.ToArray();
        ItemMoveResult merged;
        using (catalog.BeginHistory(active.Values))
        {
            merged = catalog.MoveItem(other, active[other], drawer, Current(), origin, new(receiver, null, 2));
            active[drawer] = merged.Target;
            active[other] = merged.Source;
        }
        Check(merged.Moved == 1 && merged.Duplicates == 1 &&
            active[other].Groups.Single().Members.SequenceEqual(sourceMembers.Take(1)) &&
            Restore(false) && active[other].Groups.Single().Members.SequenceEqual(sourceMembers) &&
            Current().Groups.Single().Members.Count == 2 && Restore(true) && Current().Groups.Single().Members.Count == 3,
            "partial folder merge leaves duplicates and is one undoable change across both drawers");
        using (catalog.BeginHistory(active.Values)) Current().RenameGroup(receiver, "历史文件夹");
        Check(Restore(false) && Current().Groups.Single().Name != "历史文件夹" &&
            Restore(true) && Current().Groups.Single().Name == "历史文件夹", "folder rename survives undo and redo");
        var swapItem = Fixture(13);
        Current().Import(new[] { swapItem }, new ItemDrop(null, null, 5));
        var beforeSwap = Current().TopSlots.ToArray();
        using (catalog.BeginHistory(active.Values)) catalog.MoveItem(drawer, Current(), drawer, Current(), receiver, new(null, null, 5));
        Check(Current().TopSlots[5] == receiver && Restore(false) && Current().TopSlots.SequenceEqual(beforeSwap) &&
            Restore(true) && Current().TopSlots[5] == receiver, "local exchange restores absolute cells, including holes");
        var beforePacking = Current().TopSlots.ToArray();
        using (catalog.BeginHistory(active.Values)) Current().ClearEmptySlots();
        Check(!Current().TopSlots.Contains(Guid.Empty) && Restore(false) &&
            Current().TopSlots.SequenceEqual(beforePacking) && Restore(true) && !Current().TopSlots.Contains(Guid.Empty),
            "clear-empty-slots restores exact holes on undo");
        var definitionBeforeMove = catalog.Value.Drawers.Single(d => d.Id == drawer);
        var newEdge = definitionBeforeMove.Edge == DrawerEdge.Top ? DrawerEdge.Right : DrawerEdge.Top;
        var end = catalog.Value.Drawers.Count(d => d.Edge == newEdge);
        using (catalog.BeginHistory(active.Values))
            catalog.Commit(DrawerMove.Propose(catalog.Value, drawer, newEdge, end));
        Check(Restore(false) && catalog.Value.Drawers.Single(d => d.Id == drawer).Edge == definitionBeforeMove.Edge &&
            Restore(true) && catalog.Value.Drawers.Single(d => d.Id == drawer).Edge == newEdge,
            "drawer movement restores boundary and order without rewriting its library");
        var untouched = catalog.Value.Drawers.Last(d => d.Id != drawer && d.Id != other);
        string unavailable = catalog.Resolve(untouched.Storage);
        string movedAside = unavailable + "-qa";
        Directory.Move(unavailable, movedAside);
        File.WriteAllText(unavailable, "prevent directory access");
        var healthy = active.Values.Where(store => store.Root != unavailable).ToArray();
        using (catalog.BeginHistory(healthy))
            catalog.Update(drawer, d => d with { Name = "仍可编辑" });
        Check(catalog.ApplyHistory(false, healthy) != null && catalog.Value.Drawers.Contains(untouched) &&
            File.ReadAllText(unavailable) == "prevent directory access", "healthy metadata undo preserves an unreadable drawer untouched");
        File.Delete(unavailable);
        Directory.Move(movedAside, unavailable);
        using (catalog.BeginHistory(healthy))
        {
            catalog.Update(drawer, d => d with { Name = "已提交" });
            Directory.Move(unavailable, movedAside);
            File.WriteAllText(unavailable, "fail post-commit capture");
        }
        Check(catalog.Value.Drawers.Single(d => d.Id == drawer).Name == "已提交" &&
            !catalog.CanUndo && !catalog.CanRedo && catalog.TakeHistoryNotice().Length > 0,
            "post-commit history failure keeps the change and clears stale undo and redo");
        File.Delete(unavailable);
        Directory.Move(movedAside, unavailable);

        string cleanupRoot = Path.Combine(root, "cleanup-missing");
        var cleanup = new MultiDrawerCatalog(cleanupRoot);
        cleanup.Load(new DrawerCatalog(cleanupRoot));
        var library = cleanup.OpenStore(cleanup.Value.Drawers[0].Id);
        library.Import(new[] { originals[0] });
        string kept = Path.GetRelativePath(cleanupRoot, library.CopyPath(library.Items.Single()));
        string transfers = Path.Combine("Transfers", Guid.NewGuid().ToString("N"), "target", "Shortcuts");
        string missing = Path.Combine(transfers, Guid.NewGuid().ToString("N"), "already-removed.url");
        string retired = Path.Combine(transfers, Guid.NewGuid().ToString("N"), "retired.url");
        string retiredPath = Path.Combine(cleanupRoot, retired);
        Directory.CreateDirectory(Path.GetDirectoryName(retiredPath)!);
        File.Copy(originals[0], retiredPath);
        string queue = Path.Combine(cleanupRoot, "HistoryCleanup.json");
        File.WriteAllText(queue, System.Text.Json.JsonSerializer.Serialize(new[] { missing, retired, kept }));
        cleanup.RetryDeletionCleanup();
        var pending = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(queue))!;
        Check(cleanup.CleanupNotice.Length == 0 && pending.SequenceEqual(new[] { kept }) &&
            !File.Exists(retiredPath) && File.Exists(Path.Combine(cleanupRoot, kept)) && File.Exists(originals[0]),
            "history cleanup consumes an already-removed transfer path and continues without removing a live shortcut");
        if (OperatingSystem.IsWindows())
        {
            File.Copy(originals[0], retiredPath);
            File.WriteAllText(queue, System.Text.Json.JsonSerializer.Serialize(new[] { retired }));
            using (File.Open(retiredPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                cleanup.RetryDeletionCleanup();
                Check(File.Exists(retiredPath) && File.ReadAllText(queue).Contains("retired.url") &&
                    cleanup.TakeCleanupNotice().Length > 0 && cleanup.TakeCleanupNotice().Length == 0,
                    "a genuine sharing failure stays queued and its notification is consumed only once");
            }
            cleanup.RetryDeletionCleanup();
            Check(!File.Exists(retiredPath) && File.ReadAllText(queue) == "[]" && cleanup.CleanupNotice.Length == 0,
                "deferred history cleanup succeeds after the sharing lock is released");
        }
        Console.WriteLine($"ALL {checks} HISTORY CHECKS PASSED");
    }
}
