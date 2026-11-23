using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace Drawer.Core;

internal sealed record ItemMoveResult(ShortcutStore Source, ShortcutStore Target, Guid? Folder, int[] ReclaimedPages,
    int Moved = 0, int Duplicates = 0, int Failed = 0, Guid[]? MovedItems = null);

internal sealed partial class MultiDrawerCatalog
{
    public ItemMoveResult MoveItem(Guid sourceId, ShortcutStore source, Guid targetId, ShortcutStore target, Guid item, ItemDrop drop)
        => MoveItems(sourceId, source, targetId, target, new[] { item }, drop);

    public ItemMoveResult MoveItems(Guid sourceId, ShortcutStore source, Guid targetId, ShortcutStore target, IReadOnlyList<Guid> items, ItemDrop drop)
    {
        var from = Value.Drawers.Single(d => d.Id == sourceId);
        var to = Value.Drawers.Single(d => d.Id == targetId);
        if (source.Root != Resolve(from.Storage) || target.Root != Resolve(to.Storage))
            throw new InvalidOperationException(UiText.Get("DrawerContentChanged"));
        if (sourceId == targetId)
        {
            var local = ShortcutStore.MoveItems(source, source, items, drop);
            return new(source, source, local.Folder, local.ReclaimedPages, local.Moved, local.Duplicates, local.Failed, local.MovedItems);
        }

        string transaction = "Transfers/" + Guid.NewGuid().ToString("N");
        var sourceSnapshot = new ShortcutStore(Resolve(transaction + "/source"), Root);
        var targetSnapshot = new ShortcutStore(Resolve(transaction + "/target"), Root);
        try
        {
            var result = ShortcutStore.MoveItems(source, target, items, drop, sourceSnapshot, targetSnapshot);
            if (result.Moved == 0) return new(source, target, null, result.ReclaimedPages, 0, result.Duplicates, result.Failed);
            // Both documents become active together. Prior documents still back the recovery manifest.
            Commit(Value with { Drawers = Value.Drawers.Select(d => d.Id == sourceId
                ? d with { Storage = transaction + "/source" } : d.Id == targetId
                ? d with { Storage = transaction + "/target" } : d).ToList() });
            TrackHistoryStore(sourceSnapshot);
            TrackHistoryStore(targetSnapshot);
            return new(sourceSnapshot, targetSnapshot, result.Folder, result.ReclaimedPages, result.Moved, result.Duplicates, result.Failed, result.MovedItems);
        }
        catch
        {
            foreach (var snapshot in new[] { sourceSnapshot, targetSnapshot })
                foreach (var name in new[] { "library.json", "library.json.bak" })
                    try { File.Delete(Path.Combine(snapshot.Root, name)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}
