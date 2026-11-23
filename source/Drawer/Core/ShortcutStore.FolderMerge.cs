using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Drawer.Core;

internal sealed partial class ShortcutStore
{
    private static IEnumerable<Guid> ExpandBatch(WorkingLibrary document, IEnumerable<Guid> batch)
    {
        var groups = document.Groups.ToDictionary(g => g.Id);
        return batch.SelectMany(id => groups.TryGetValue(id, out var folder) ? folder.Members : (IEnumerable<Guid>)new[] { id });
    }

    internal Guid[] ExpandClipboardItems(IReadOnlyList<Guid> ids) => ids.SelectMany(id =>
        Groups.FirstOrDefault(group => group.Id == id)?.Members as IEnumerable<Guid> ?? new[] { id }).ToArray();

    private static LibraryMove MergeFolderContents(ShortcutStore source, ShortcutStore destination,
        WorkingLibrary sourceDocument, WorkingLibrary to, Guid[] batch, Guid receiver, ItemDrop drop,
        ShortcutStore? sourceSnapshot, ShortcutStore? destinationSnapshot)
    {
        bool local = ReferenceEquals(sourceDocument, to);
        var target = to.Groups.Single(g => g.Id == receiver);
        var expanded = ExpandBatch(sourceDocument, batch.Where(id => id != receiver || !local)).ToArray();
        var moving = expanded.ToHashSet();
        var candidates = target.Members.Where(id => !local || !moving.Contains(id)).ToArray();
        drop.MemberPlacement?.Validate(receiver, drop.Index, candidates);
        var entries = sourceDocument.Items.ToDictionary(e => e.Id);
        var identities = to.Items.Where(e => !local || !moving.Contains(e.Id)).Select(destination.Identity).ToHashSet();
        var accepted = new List<Guid>();
        int duplicates = 0, failed = 0;
        foreach (Guid id in expanded)
        {
            var entry = entries[id];
            if (!File.Exists(source.CopyPath(entry))) { failed++; continue; }
            if (!identities.Add(source.Identity(entry))) { duplicates++; continue; }
            accepted.Add(id);
        }
        if (accepted.Count == 0) return new(null, Array.Empty<int>(), 0, duplicates, failed);
        var moved = accepted.ToHashSet();
        var sourceCells = sourceDocument.Slots.Select((id, index) => (id, index))
            .Where(slot => moved.Contains(slot.id) || sourceDocument.Groups.Any(g => g.Id == slot.id && g.Members.All(moved.Contains)))
            .Select(slot => slot.index).ToArray();
        TopGridSlots.Vacate(sourceDocument.Slots, moved);
        for (int i = 0; i < sourceDocument.Groups.Count; i++)
        {
            var folder = sourceDocument.Groups[i];
            if (folder.Members.RemoveAll(moved.Contains) > 0 && folder.Id != receiver && folder.Members.Count == 1)
                sourceDocument.Groups[i] = folder with { PreserveSingleton = true };
        }
        var emptyFolders = sourceDocument.Groups.Where(g => g.Members.Count == 0 && (!local || g.Id != receiver)).Select(g => g.Id).ToHashSet();
        TopGridSlots.Vacate(sourceDocument.Slots, emptyFolders);
        sourceDocument.Groups.RemoveAll(g => emptyFolders.Contains(g.Id));
        if (!local) TransferEntries(source, sourceDocument, to, accepted);
        target.Members.AddRange(accepted);
        ValidateGroups(sourceDocument.Items, sourceDocument.Groups, sourceDocument.Slots);
        var reclaimed = TopGridSlots.ReclaimVacatedPages(sourceDocument.Slots, sourceCells, drop.SourcePageCapacity);
        if (local) source.ApplyDocument(sourceDocument);
        else
        {
            ValidateGroups(to.Items, to.Groups, to.Slots);
            sourceSnapshot!.WriteSnapshot(sourceDocument with { Items = sourceDocument.Items.Select(source.Reference).ToList() });
            destinationSnapshot!.WriteSnapshot(to with { Items = to.Items.Select(e => e.ManagedCopy != null ? e : destination.Reference(e)).ToList() });
        }
        return new(null, reclaimed, accepted.Count, duplicates, failed, accepted.ToArray());
    }
}
