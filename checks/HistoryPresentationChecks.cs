using Drawer.Core;

internal static class HistoryPresentationChecks
{
    public static void Run()
    {
        string root = Path.Combine(Environment.CurrentDirectory, "temp", "v67-18-history-regression", Guid.NewGuid().ToString("N"));
        var catalog = new MultiDrawerCatalog(root);
        catalog.Load(new DrawerCatalog(root));
        var stores = catalog.Value.Drawers.ToDictionary(drawer => drawer.Id, drawer => catalog.OpenStore(drawer.Id));
        var original = catalog.Value.Drawers[0];
        var destination = catalog.Value.Drawers[1];

        void Check(bool valid, string label)
        {
            if (!valid) throw new InvalidOperationException("history feedback: " + label);
            Console.WriteLine("PASS history feedback: " + label);
        }

        HistoryPresentation Restore(bool redo)
        {
            var before = catalog.Value.Drawers.ToArray();
            var changed = catalog.ApplyHistory(redo, stores.Values) ?? throw new InvalidOperationException("Missing history entry");
            var presentation = HistoryPresentation.Describe(before, catalog.Value.Drawers, stores, changed, destination.Id)
                ?? throw new InvalidOperationException("Missing history description");
            foreach (var (id, store) in changed) stores[id] = store;
            return presentation;
        }

        string[] sources = Enumerable.Range(0, 3).Select(index =>
        {
            string file = Path.Combine(root, $"item{index}.url");
            File.WriteAllText(file, $"[InternetShortcut]\r\nURL=https://example.com/drawer-history/{index}\r\n");
            return file;
        }).ToArray();
        stores[original.Id].Import(sources);
        var items = stores[original.Id].TopSlots.ToArray();

        using (catalog.BeginHistory(stores.Values))
        {
            var moved = catalog.MoveItems(original.Id, stores[original.Id], destination.Id, stores[destination.Id],
                [items[0]], new(null, null, 4));
            stores[original.Id] = moved.Source;
            stores[destination.Id] = moved.Target;
        }

        var undo = Restore(false);
        Check(undo.Kind == HistoryVisualKind.Transfer && undo.Drawer.Id == original.Id && undo.Source?.Id == destination.Id &&
            undo.Items.SequenceEqual([items[0]]) && undo.FirstVisibleCell(stores[original.Id]) == 0,
            "undo opens the original drawer at the restored cell");
        var redo = Restore(true);
        Check(redo.Kind == HistoryVisualKind.Transfer && redo.Drawer.Id == destination.Id && redo.Source?.Id == original.Id &&
            redo.Items.SequenceEqual([items[0]]) && redo.FirstVisibleCell(stores[destination.Id]) == 4,
            "redo opens the destination at the recorded cell");

        Guid folder = stores[original.Id].CreateFolder(items[1..]);
        using (catalog.BeginHistory(stores.Values))
        {
            var moved = catalog.MoveItems(original.Id, stores[original.Id], destination.Id, stores[destination.Id],
                [folder], new(null, null, 6));
            stores[original.Id] = moved.Source;
            stores[destination.Id] = moved.Target;
        }
        var folderUndo = Restore(false);
        Check(folderUndo.Kind == HistoryVisualKind.Transfer && folderUndo.Items.SequenceEqual([folder]) &&
            folderUndo.FirstVisibleCell(stores[original.Id]) == stores[original.Id].TopSlots.IndexOf(folder),
            "whole folder transfer still animates a single tile");
        using (catalog.BeginHistory(stores.Values))
        {
            var moved = catalog.MoveItems(destination.Id, stores[destination.Id], original.Id, stores[original.Id],
                [items[0]], new(folder, null, 0));
            stores[destination.Id] = moved.Source;
            stores[original.Id] = moved.Target;
        }
        Restore(false);
        var folderRedo = Restore(true);
        Check(folderRedo.Kind == HistoryVisualKind.Transfer && folderRedo.Items.SequenceEqual([items[0]]) &&
            folderRedo.FirstVisibleCell(stores[original.Id]) == stores[original.Id].TopSlots.IndexOf(folder),
            "a transferred member selects the page containing its receiving folder");

        using (catalog.BeginHistory(stores.Values)) catalog.Delete(original.Id);
        var restored = Restore(false);
        Check(restored.Kind == HistoryVisualKind.DrawerAppeared && restored.Drawer.Id == original.Id && restored.Drawer.Name == original.Name,
            "undo deletion identifies the restored drawer instead of the current drawer");
        var removed = Restore(true);
        Check(removed.Kind == HistoryVisualKind.DrawerRemoved && removed.Drawer.Id == original.Id,
            "redo deletion identifies the removed drawer");
    }
}
