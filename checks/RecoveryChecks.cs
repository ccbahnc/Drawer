using Drawer.Core;

internal static class RecoveryChecks
{
    internal static void Run()
    {
        string project = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(project, "Drawer.sln"))) project = Directory.GetParent(project)!.FullName;
        string root = Path.Combine(project, "temp", "recovery31-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int checks = 0;
        void Check(bool ok, string label)
        {
            if (!ok) throw new Exception("recovery: " + label);
            checks++;
            Console.WriteLine("PASS recovery: " + label);
        }
        static bool Fails(Action operation)
        {
            try { operation(); return false; }
            catch (IOException) { return true; }
            catch (InvalidDataException) { return true; }
        }
        var paths = Enumerable.Range(0, 5).Select(i =>
        {
            var path = Path.Combine(root, $"original-{i}.url");
            File.WriteAllText(path, $"[InternetShortcut]\nURL=https://example.com/recovery/{i}");
            return path;
        }).ToArray();
        var original = paths.Select(File.ReadAllText).ToArray();
        var legacy = new DrawerCatalog(Path.Combine(root, "legacy"));
        legacy.Load();
        legacy.SetEnabled(DrawerEdge.Right, false);
        string legacyFile = Path.Combine(legacy.Root, "drawers.json");
        File.Copy(legacyFile, legacyFile + ".bak", true);
        byte[] legacyBackup = File.ReadAllBytes(legacyFile + ".bak");
        File.WriteAllText(legacyFile, "{\"Version\":99,\"Drawers\":[]}");
        legacy.Load();
        Check(!legacy.Enabled(DrawerEdge.Right) && legacy.Notice.Contains("已从备份恢复") &&
            legacyBackup.SequenceEqual(File.ReadAllBytes(legacyFile + ".bak")), "unsupported legacy version restores backup without replacing it");
        File.Delete(legacyFile);
        legacy.Load();
        Check(!legacy.Enabled(DrawerEdge.Right) && File.Exists(legacyFile), "missing legacy catalog also restores backup");
        File.WriteAllText(legacyFile, "{\"Version\":99,\"Drawers\":[]}");
        File.WriteAllText(legacyFile + ".bak", "{\"Version\":99,\"Drawers\":[]}");
        legacy.Load();
        Check(legacy.Enabled(DrawerEdge.Top) && !legacy.Enabled(DrawerEdge.Left) && legacy.Notice.Contains("不可用"),
            "two structurally invalid legacy catalogs enter the conservative fallback");

        var library = new ShortcutStore(Path.Combine(root, "library"));
        library.Load();
        library.Import(paths.Take(4));
        var first = library.TopSlots[0];
        var next = library.TopSlots[1];
        Guid group = library.CreateFolder(new[] { first, next });
        var moved = library.TopSlots[3];
        ShortcutStore.MoveItems(library, library, new[] { moved }, new(null, null, 10));
        library.SetLayout(new(3, 2));
        var expected = library.TopSlots.ToArray();
        var file = Path.Combine(library.Root, "library.json");
        File.Copy(file, file + ".bak", true);
        byte[] backup = File.ReadAllBytes(file + ".bak");
        File.Delete(file);
        library.BeforeRecoveryPublish = () => throw new IOException("interrupted after staging");
        Check(Fails(library.Load) && !File.Exists(file) && backup.SequenceEqual(File.ReadAllBytes(file + ".bak")),
            "interrupted missing-library restore preserves the authoritative backup");
        library = new ShortcutStore(library.Root);
        library.Load();
        Check(library.TopSlots.SequenceEqual(expected) && library.Groups.Single(g => g.Id == group).Members.SequenceEqual(new[] { first, next }) &&
            library.Layout.Columns == 3 && library.Layout.Rows == 2, "restart restores full sparse library, layout and group from backup");
        File.WriteAllText(file, "{broken");
        library.BeforeRecoveryPublish = () => throw new IOException("failed recovery write");
        Check(Fails(library.Load) && File.ReadAllText(file) == "{broken" && backup.SequenceEqual(File.ReadAllBytes(file + ".bak")),
            "failed corrupted-library restore preserves the original and the backup");
        library.BeforeRecoveryPublish = null;
        library.Load();
        Check(library.TopSlots.SequenceEqual(expected) && File.Exists(file), "second restore publishes verified library");
        File.WriteAllText(file, "{broken");
        File.WriteAllText(file + ".bak", "{broken");
        library.Load();
        Check(library.Items.Count == 4 && library.Groups.Count == 0 && library.Notice.Contains("重建"),
            "when both library copies are invalid, downgrade to independently managed shortcut bytes");

        string catalogRoot = Path.Combine(root, "collection");
        var catalog = new MultiDrawerCatalog(catalogRoot);
        catalog.Load(new DrawerCatalog(catalogRoot));
        var source = catalog.Value.Drawers[0].Id;
        var target = catalog.Value.Drawers[1].Id;
        catalog.Update(target, drawer => drawer with { Enabled = false, Rows = 3, Columns = 2 });
        var oldTarget = catalog.Value.Drawers.Single(d => d.Id == target);
        var oldAppearance = catalog.Value.Global;
        catalog.Update(target, drawer => drawer with { Enabled = true });
        Check(catalog.Value.Drawers.Single(d => d.Id == target) == (oldTarget with { Enabled = true }) &&
            catalog.Value.Global == oldAppearance, "enabling a legacy drawer only changes its enabled flag");
        var from = catalog.OpenStore(source);
        var to = catalog.OpenStore(target);
        from.Import(paths.Take(1));
        var item = from.Items.Single();
        string copy = from.CopyPath(item);
        var move = catalog.MoveItem(source, from, target, to, item.Id, new(null, null, 8, SourcePageCapacity: 4));
        Check(move.ReclaimedPages.SequenceEqual(new[] { 0 }) && move.Source.TopSlots.Count == 0 &&
            move.Target.TopSlots[8] == item.Id && File.Exists(copy), "source-page reclaim and cross-drawer insertion publish together");

        var manifest = Path.Combine(catalogRoot, "collection.json");
        File.Copy(manifest, manifest + ".bak", true);
        var saved = File.ReadAllBytes(manifest + ".bak");
        File.Delete(manifest);
        catalog.BeforeRecoveryPublish = () => throw new IOException("injected after recovery validation");
        Check(Fails(() => catalog.Load(new(catalogRoot))) && !File.Exists(manifest) && saved.SequenceEqual(File.ReadAllBytes(manifest + ".bak")),
            "interrupted manifest restore keeps the backup and does not fall through to migration");
        catalog = new MultiDrawerCatalog(catalogRoot);
        catalog.Load(new DrawerCatalog(catalogRoot));
        Check(catalog.OpenStore(target).TopSlots[8] == item.Id && catalog.Value.Drawers.Any(d => d.Id == source),
            "missing manifest restores snapshot ownership and sparse cells");
        File.WriteAllText(manifest, "{invalid");
        catalog.Load(new DrawerCatalog(catalogRoot));
        Check(catalog.RecoveryNotice.Contains("损坏") && catalog.OpenStore(target).TopSlots[8] == item.Id,
            "corrupt manifest prefers verified backup");
        File.WriteAllText(manifest, "{invalid");
        File.WriteAllText(manifest + ".bak", "{invalid");
        Check(Fails(() => catalog.Load(new(catalogRoot))) && File.Exists(copy) && File.ReadAllText(manifest) == "{invalid",
            "unusable manifest and backup preserve snapshots and stop unsafe legacy migration");
        File.WriteAllBytes(manifest, saved);
        File.WriteAllBytes(manifest + ".bak", saved);
        catalog.Load(new DrawerCatalog(catalogRoot));
        var obsolete = Path.GetDirectoryName(catalog.OpenStore(target).Root)!;
        from = catalog.OpenStore(source);
        to = catalog.OpenStore(target);
        for (int i = 0; i < 24; i++)
        {
            var result = i % 2 == 0
                ? catalog.MoveItem(target, to, source, from, item.Id, new(null, null, 0, SourcePageCapacity: 4))
                : catalog.MoveItem(source, from, target, to, item.Id, new(null, null, 8, SourcePageCapacity: 4));
            (from, to) = i % 2 == 0 ? (result.Target, result.Source) : (result.Source, result.Target);
            Check(from.Items.Concat(to.Items).Count(e => e.Id == item.Id) == 1, $"cross-drawer cycle {i + 1} retains exactly one owner");
        }
        var pending = Path.Combine(catalogRoot, "Transfers", Guid.NewGuid().ToString("N"), "source");
        Directory.CreateDirectory(pending);
        File.Copy(Path.Combine(to.Root, "library.json"), Path.Combine(pending, "library.json"));
        catalog.ReclaimTransferSnapshots();
        Check(!Directory.Exists(obsolete) && Directory.Exists(pending) && File.Exists(copy),
            "reclaim removes superseded published documents and retains unmarked transactions and shortcut bytes");
        catalog.BeforeCleanup = () => throw new IOException("simulated interruption after single-item commit");
        catalog.DeleteItems(target, to, new[] { item.Id });
        Check(File.Exists(copy) && !catalog.OpenStore(target).Items.Any(e => e.Id == item.Id),
            "single deletion commits records before retryable byte cleanup");
        catalog = new MultiDrawerCatalog(catalogRoot);
        catalog.Load(new DrawerCatalog(catalogRoot));
        Check(!File.Exists(copy) && catalog.OpenStore(target).Items.Count == 0 && paths.Select(File.ReadAllText).SequenceEqual(original),
            "deletion resumes on restart, preserves source files and leaves no stale record");
        File.WriteAllText(manifest, "{invalid");
        catalog.Load(new DrawerCatalog(catalogRoot));
        Check(catalog.OpenStore(target).Items.Count == 0 && !File.Exists(copy),
            "after physical deletion the recovery manifest cannot resurrect a broken icon");

        var local = new ShortcutStore(Path.Combine(root, "local"));
        local.Load();
        local.Import(paths.Take(3));
        var ids = local.TopSlots.ToArray();
        ShortcutStore.MoveItems(local, local, new[] { ids[2] }, new(null, null, 9));
        var landing = ShortcutStore.MoveItems(local, local, new[] { ids[0], ids[1] }, new(null, null, 12, SourcePageCapacity: 4));
        Check(landing.ReclaimedPages.SequenceEqual(new[] { 0 }) && TopGridSlots.RemapPage(3, landing.ReclaimedPages) == 2 &&
            local.TopSlots[5] == ids[2] && local.TopSlots[8] == ids[0] && local.TopSlots[9] == ids[1] &&
            local.TopSlots.Skip(0).Take(4).All(id => id == Guid.Empty),
            "vacating earlier page remaps destination, preserves previous empty page and preserves remaining grid holes");
        Console.WriteLine($"ALL {checks} RECOVERY CHECKS PASSED");
    }
}
