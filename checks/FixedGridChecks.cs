using Drawer.Core;
using System.Text.Json;

internal static class FixedGridChecks
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new Exception("fixed grid: " + label);
            checks++;
            Console.WriteLine("PASS fixed grid: " + label);
        }

        var ids = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray();
        var (a, b, c, d, e) = (ids[0], ids[1], ids[2], ids[3], ids[4]);
        Guid hole = Guid.Empty;
        var original = new[] { a, b, c, hole, d, e, hole };
        var preview = new DragPreview(original, new[] { e }, fixedCells: true);
        preview.PreviewInsertion(1);
        Check(preview.VisualSlots.SequenceEqual(new[] { a, hole, c, hole, d, b, hole }),
            "swap preview moves only the target into the source cell");
        preview.Compact();
        Check(preview.VisualSlots.SequenceEqual(new[] { a, b, c, hole, d, hole, hole }),
            "receiving cancels temporary shifts and retains saved holes");
        preview.PreviewInsertion(3);
        Check(preview.Valid && preview.Index == 3 && preview.VisualSlots.SequenceEqual(new[] { a, b, c, hole, d, hole, hole }),
            "free cell accepts a drop without moving neighbors");
        preview.Cancel();
        Check(!preview.Valid && preview.VisualSlots.SequenceEqual(original), "cancel restores all cells");
        preview = new DragPreview(original, new[] { b, e }, fixedCells: true);
        preview.PreviewInsertion(9);
        Check(preview.Valid && preview.Index == 9 && preview.VisualSlots.SequenceEqual(new[] { a, hole, c, hole, d, hole, hole }),
            "batch preview records one future landing without allocating its final range");
        preview = new DragPreview(new[] { a, b, c }, new[] { d, e }, fixedCells: true);
        preview.PreviewInsertion(1);
        Check(preview.VisualSlots.SequenceEqual(new[] { a, hole, c, b }) && preview.HiddenItem == null,
            "member extraction previews the first vacancy, extending the destination when full");

        var entry = new FolderDragEntry();
        Check(entry.Visit(10, 10, true) == FolderDragVisit.Inside, "drag started inside a folder enters immediately");
        entry.WaitAt(10, 10);
        Check(entry.Visit(10, 10, true) == FolderDragVisit.Waiting &&
            entry.Visit(15, 20, false) == FolderDragVisit.Waiting &&
            entry.Visit(15, 30, false) == FolderDragVisit.Waiting,
            "opening point, heading and gap do not qualify as entering");
        Check(entry.Visit(20, 35, true) == FolderDragVisit.Inside &&
            entry.Visit(20, 40, false) == FolderDragVisit.Left, "only leaving after entry closes the panel");

        string project = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(project, "Drawer.sln"))) project = Directory.GetParent(project)!.FullName;
        string root = Path.Combine(project, "temp", "fixed-grid-" + Guid.NewGuid().ToString("N"));
        var store = new ShortcutStore(root);
        store.Load();
        var paths = Enumerable.Range(0, 6).Select(i =>
        {
            string path = Path.Combine(root, $"{i}.url");
            File.WriteAllText(path, $"[InternetShortcut]\nURL=https://example.com/{i}");
            return path;
        }).ToArray();
        store.Import(paths.Take(5));
        var oldOrder = store.TopOrder;
        string library = Path.Combine(root, "library.json");
        File.WriteAllText(library, JsonSerializer.Serialize(new ShortcutDocument(1, store.Items, store.Layout, TopOrder: oldOrder)));
        store.Load();
        Check(store.TopSlots.SequenceEqual(oldOrder), "version 1 maps to consecutive cells");
        ShortcutStore.MoveItems(store, store, new[] { oldOrder[0] }, new(null, null, 8));
        store.RemoveLegacyFixture(store.Items.Single(item => item.Id == oldOrder[0]));
        Check(store.TopSlots.Count == 9 && store.TopSlots[0] == hole && store.TopSlots[8] == hole,
            "moving and removing keeps both leading and trailing recorded holes");
        store.Import(paths.Skip(5));
        var added = store.TopSlots[9];
        Check(added != hole && store.TopSlots[8] == hole, "import appends after the last recorded empty cell");
        store.SetLayout(new(1, 6, 38));
        var reopened = new ShortcutStore(root);
        reopened.Load();
        using var saved = JsonDocument.Parse(File.ReadAllText(library));
        Check(reopened.TopSlots.SequenceEqual(store.TopSlots) && reopened.Layout.Columns == 1 &&
            saved.RootElement.GetProperty("Version").GetInt32() == 2 &&
            saved.RootElement.GetProperty("TopSlots")[8].ValueKind == JsonValueKind.Null,
            "column changes and restart preserve absolute cells serialized as nullable IDs");
        var folder = reopened.Merge(oldOrder[1], oldOrder[2]);
        Check(reopened.TopSlots[1] == hole && reopened.TopSlots[2] == folder &&
            reopened.Groups.Single().Members.SequenceEqual(new[] { oldOrder[2], oldOrder[1] }), "merge replaces the target only");
        reopened.Dissolve(folder);
        Check(reopened.TopSlots[2] == oldOrder[2] && reopened.TopSlots[3] == oldOrder[1] &&
            reopened.TopSlots[4] == oldOrder[3] && reopened.TopSlots[5] == oldOrder[4] && reopened.TopSlots[9] == added,
            "dissolving consumes just the nearest space");
        var occupied = reopened.TopOrder;
        reopened.ClearEmptySlots();
        reopened.Load();
        Check(reopened.TopSlots.SequenceEqual(occupied), "explicit compaction removes every recorded hole and preserves grid order");
        Console.WriteLine($"ALL {checks} FIXED GRID CHECKS PASSED");
    }
}
