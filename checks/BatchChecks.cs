using Drawer.Core;

internal static class BatchChecks
{
    public static void Run()
    {
        int count = 0;
        void Check(bool ok, string description)
        {
            if (!ok) throw new Exception("batch: " + description);
            count++;
            Console.WriteLine("PASS batch: " + description);
        }

        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        var selected = new[] { ids[1], ids[3] };
        var preview = new DragPreview(ids, selected);
        var landing = new FolderInsertion(new FolderInsertionSequence(Guid.NewGuid(), ids, selected), 3);
        preview.PreviewMembers(landing);
        Check(preview.Index == 3 && preview.Order.SequenceEqual(new[] { ids[0], ids[2], ids[4], ids[1], ids[3], ids[5] }),
            "noncontiguous batch moves as a block using a reduced insertion index");
        preview.PreviewMembers(landing);
        Check(preview.Index == 3, "same physical boundary does not drift after reflow");
        preview.Cancel();
        Check(!preview.Valid && preview.Order.SequenceEqual(ids), "cancel restores the original noncontiguous positions");

        string project = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(project, "Drawer.sln"))) project = Directory.GetParent(project)!.FullName;
        string root = Path.Combine(project, "temp", "batch-check-" + Guid.NewGuid().ToString("N"));
        var catalog = new MultiDrawerCatalog(root);
        catalog.Load(new DrawerCatalog(root));
        Guid source = catalog.Value.Drawers[0].Id, target = catalog.Value.Drawers[1].Id;
        var from = catalog.OpenStore(source);
        var to = catalog.OpenStore(target);
        var originals = Enumerable.Range(0, 9).Select(i =>
        {
            string path = Path.Combine(root, $"source-{i}.url");
            File.WriteAllText(path, $"[InternetShortcut]\nURL=https://example.com/{i}");
            return path;
        }).ToArray();
        var contents = originals.Select(File.ReadAllText).ToArray();
        from.Import(originals.Take(6));
        to.Import(originals.Skip(6));
        var a = from.TopOrder.ToArray();
        var b = to.TopOrder.ToArray();
        var sameGrid = new DragPreview(a, new[] { a[1], a[3] }, fixedCells: true);
        sameGrid.PreviewInsertion(5);
        catalog.MoveItems(source, from, source, from, new[] { a[1], a[3] }, new(null, null, sameGrid.Index));
        Check(sameGrid.VisualSlots.SequenceEqual(new[] { a[0], a[5], a[2], Guid.Empty, a[4], Guid.Empty }) &&
            catalog.OpenStore(source).TopSlots.SequenceEqual(new[] { a[0], a[5], a[2], Guid.Empty, a[4], a[1], a[3] }),
            "preview reserves one cell, commit expands the batch and returns the displaced item");
        from.ClearEmptySlots();
        from.Reorder(a);
        var merged = catalog.MoveItems(target, to, target, to, new[] { b[1], b[2] }, new(null, b[0], 0));
        var reloadedMerge = catalog.OpenStore(target);
        Check(reloadedMerge.TopOrder.SequenceEqual(new[] { merged.Folder!.Value }) &&
            reloadedMerge.Groups.Single().Members.SequenceEqual(b), "batch drop on an ordinary item saves the target and ordered batch in a new folder");
        to.Dissolve(merged.Folder!.Value);
        var folder = from.CreateFolder(new[] { a[4], a[1], a[3] });
        Check(from.TopOrder.SequenceEqual(new[] { a[0], folder, a[2], a[5] }) &&
            from.Groups.Single().Members.SequenceEqual(new[] { a[1], a[3], a[4] }), "folder creation uses the earliest slot and grid order");
        var otherFolder = to.CreateFolder(new[] { b[0], b[1] });
        var result = catalog.MoveItems(source, from, target, to, new[] { a[5], a[0] }, new(otherFolder, null, 2));
        from = result.Source;
        to = result.Target;
        Check(to.Groups.Single().Members.SequenceEqual(new[] { b[0], b[1], a[0], a[5] }), "cross-drawer batch append keeps relative order");
        catalog.MoveItems(target, to, target, to, new[] { a[0], b[0] }, new(otherFolder, null, 2));
        Check(to.Groups.Single().Members.SequenceEqual(new[] { b[1], a[5], b[0], a[0] }), "same-group batch rearranges without duplication");
        var folderBatch = new[] { a[1], a[4] };
        var sourceMembers = from.Groups.Single().Members.ToArray();
        var targetMembers = to.Groups.Single().Members.ToArray();
        var betweenMembers = new FolderInsertion(new FolderInsertionSequence(otherFolder, targetMembers, folderBatch), 2);
        var folderDrop = new ItemDrop(otherFolder, null, betweenMembers.Index, ExpectedSourceFolder: folder,
            ExpectedMemberIndices: new[] { 0, 2 }, MemberPlacement: betweenMembers);
        catalog.BeforeCommit = _ => throw new IOException("injected member move failure");
        try { catalog.MoveItems(source, from, target, to, folderBatch, folderDrop); throw new Exception("expected member move failure"); }
        catch (IOException) { }
        catalog.BeforeCommit = null;
        Check(catalog.OpenStore(source).Groups.Single().Members.SequenceEqual(sourceMembers) &&
            catalog.OpenStore(target).Groups.Single().Members.SequenceEqual(targetMembers),
            "failed planned cross-drawer member insertion preserves both folders");
        var topBatch = new[] { a[2] };
        var topIntoFolder = new FolderInsertion(new FolderInsertionSequence(otherFolder, targetMembers, topBatch), 1);
        var planned = catalog.MoveItems(source, from, target, to, topBatch,
            new(otherFolder, null, topIntoFolder.Index, MemberPlacement: topIntoFolder));
        Check(planned.Target.Groups.Single().Members.SequenceEqual(topIntoFolder.PreviewOrder()) &&
            !planned.Source.TopOrder.Contains(a[2]), "planned cross-drawer insertion saves the previewed order and removes the source once");
        var returnMove = catalog.MoveItems(target, planned.Target, source, planned.Source, topBatch, new(null, null, 2));
        to = returnMove.Source;
        from = returnMove.Target;
        var sourceBeforeMove = from.TopOrder.ToArray();
        var targetBeforeMove = to.TopOrder.ToArray();
        catalog.BeforeCommit = _ => throw new IOException("injected move failure");
        try { catalog.MoveItems(source, from, target, to, new[] { folder, a[2] }, new(null, null, 2)); throw new Exception("expected move failure"); }
        catch (IOException) { }
        catalog.BeforeCommit = null;
        Check(catalog.OpenStore(source).TopOrder.SequenceEqual(sourceBeforeMove) &&
            catalog.OpenStore(target).TopOrder.SequenceEqual(targetBeforeMove), "failed cross-drawer batch keeps both original orders");
        result = catalog.MoveItems(source, from, target, to, new[] { a[2], folder }, new(null, null, 2));
        from = result.Source; to = result.Target;
        Check(from.TopSlots.Count == 6 && from.TopSlots.All(id => id == Guid.Empty) &&
            to.TopSlots.SequenceEqual(new[] { otherFolder, Guid.Empty, folder, a[2], b[2] }) &&
            to.Groups.Single(g => g.Id == folder).Members.SequenceEqual(new[] { a[1], a[3], a[4] }),
            "cross-drawer batch inserts at the occupied slot, leaving every displaced item in the target and holes in the source");
        var removed = to.Items.Where(e => new[] { a[1], a[3], a[4], a[2] }.Contains(e.Id)).Select(to.CopyPath).ToArray();
        string shared = removed[0];
        var kept = to.Items.Single(e => e.Id == a[1]);
        var reference = kept with { Id = Guid.NewGuid(), ManagedCopy = Path.GetRelativePath(root, shared) };
        File.WriteAllText(Path.Combine(from.Root, "library.json"), System.Text.Json.JsonSerializer.Serialize(
            new ShortcutDocument(1, new() { reference }, TopOrder: new() { reference.Id })));
        string manifest = Path.Combine(root, "collection.json");
        var before = File.ReadAllBytes(manifest);
        catalog.BeforeCommit = _ => throw new IOException("injected failure");
        try { catalog.DeleteItems(target, to, new[] { folder, a[2] }); throw new Exception("expected save failure"); }
        catch (IOException) { }
        catalog.BeforeCommit = null;
        Check(before.SequenceEqual(File.ReadAllBytes(manifest)) && removed.All(File.Exists) && catalog.OpenStore(target).TopOrder.Contains(folder),
            "failed batch deletion preserves every record and copy");
        catalog.BeforeCleanup = () => throw new IOException("injected cleanup interruption");
        to = catalog.DeleteItems(target, to, new[] { folder, a[2] });
        Check(to.TopOrder.SequenceEqual(new[] { otherFolder, b[2] }) && removed.All(File.Exists) && catalog.CleanupNotice.Length > 0,
            "committed deletion is complete even if byte cleanup must retry");
        var reload = new MultiDrawerCatalog(root);
        reload.Load(new DrawerCatalog(root));
        Check(reload.OpenStore(target).TopOrder.SequenceEqual(new[] { otherFolder, b[2] }) &&
            File.Exists(shared) && removed.Where(path => path != shared).All(path => !File.Exists(path)) &&
            originals.Select(File.ReadAllText).SequenceEqual(contents), "restart cleans exclusive copies and retains shared and original files");
        Console.WriteLine($"ALL {count} BATCH CHECKS PASSED");
    }
}
