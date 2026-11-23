using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Drawer.Core;

// For a folder, Index is measured after removing the batch. At the top level it is an absolute cell.
internal sealed record ItemDrop(Guid? Group, Guid? Merge, int Index, IReadOnlyList<int>? ExpectedSourceCells = null,
    Guid? ExpectedSourceFolder = null, IReadOnlyList<int>? ExpectedMemberIndices = null, int SourcePageCapacity = 0,
    FolderInsertion? MemberPlacement = null, bool Deduplicate = false);
internal sealed record LibraryMove(Guid? Folder, int[] ReclaimedPages, int Moved = 0, int Duplicates = 0, int Failed = 0,
    Guid[]? MovedItems = null);

internal sealed partial class ShortcutStore
{
    internal static LibraryMove MoveItems(ShortcutStore source, ShortcutStore destination, IReadOnlyList<Guid> ids,
        ItemDrop drop, ShortcutStore? sourceSnapshot = null, ShortcutStore? destinationSnapshot = null)
    {
        bool local = ReferenceEquals(source, destination);
        var sourceDoc = source.Document();
        var to = local ? sourceDoc : destination.Document();
        var batch = OrderedBatch(sourceDoc, ids, out var origin);
        var selected = batch.ToHashSet();
        var sourceCells = ReferenceEquals(origin, sourceDoc.Slots)
            ? Enumerable.Range(0, origin.Count).Where(i => selected.Contains(origin[i])).ToArray() : Array.Empty<int>();
        if (drop.ExpectedSourceCells != null && !drop.ExpectedSourceCells.SequenceEqual(sourceCells))
            throw new InvalidOperationException(UiText.Get("DragSourceChanged"));
        if (drop.ExpectedMemberIndices != null &&
            (!sourceDoc.Groups.Any(group => group.Id == drop.ExpectedSourceFolder && ReferenceEquals(group.Members, origin)) ||
             !drop.ExpectedMemberIndices.SequenceEqual(batch.Select(id => origin.IndexOf(id)))))
            throw new InvalidOperationException(UiText.Get("SourceFolderChanged"));
        var folders = sourceDoc.Groups.Where(g => selected.Contains(g.Id)).ToArray();
        if ((drop.Group != null && drop.Merge != null) || (folders.Length > 0 && drop.Merge != null))
            throw new ArgumentException(UiText.Get("NestedFolders"));

        if (drop.Group is Guid receiver && (folders.Length > 0 || drop.Deduplicate))
            return MergeFolderContents(source, destination, sourceDoc, to, batch, receiver, drop,
                sourceSnapshot, destinationSnapshot);

        int duplicates = 0, failed = 0;
        if (drop.Deduplicate)
        {
            var moving = ExpandBatch(sourceDoc, batch).ToHashSet();
            var identities = to.Items.Where(e => !local || !moving.Contains(e.Id)).Select(destination.Identity).ToHashSet();
            var entries = sourceDoc.Items.ToDictionary(e => e.Id);
            batch = batch.Where(id =>
            {
                var expanded = ExpandBatch(sourceDoc, new[] { id }).Select(member => entries[member]).ToArray();
                if (expanded.Any(e => !File.Exists(source.CopyPath(e)))) { failed += expanded.Length; return false; }
                var keys = expanded.Select(source.Identity).ToArray();
                if (keys.Distinct().Count() != keys.Length || keys.Any(identities.Contains)) { duplicates += keys.Length; return false; }
                identities.UnionWith(keys);
                return true;
            }).ToArray();
            if (batch.Length == 0) return new(null, Array.Empty<int>(), 0, duplicates, failed);
            selected = batch.ToHashSet();
            sourceCells = ReferenceEquals(origin, sourceDoc.Slots)
                ? Enumerable.Range(0, origin.Count).Where(i => selected.Contains(origin[i])).ToArray() : Array.Empty<int>();
        }

        Vacate(sourceDoc, origin, selected);
        if (!local) TransferEntries(source, sourceDoc, to, batch);

        Guid? result = null;
        if (drop.Group is Guid group)
        {
            var members = to.Groups.Single(g => g.Id == group).Members;
            if (drop.MemberPlacement is { } landing) landing.Insert(group, drop.Index, members, batch);
            else members.InsertRange(drop.Index, batch);
        }
        else if (drop.Merge is Guid merge)
        {
            int index = to.Slots.IndexOf(merge);
            if (index < 0 || !to.Items.Any(e => e.Id == merge)) throw new ArgumentException(UiText.Get("MergeTargetChanged"));
            result = Guid.NewGuid();
            to.Groups.Add(new(result.Value, UiText.Get("NewFolder"), new[] { merge }.Concat(batch).ToList()));
            to.Slots[index] = result.Value;
        }
        else
        {
            if (local)
            {
                to = sourceDoc = NormalizeGroups(sourceDoc);
                TopGridSlots.Place(to.Slots, sourceCells, drop.Index, batch);
            }
            else TopGridSlots.Insert(to.Slots, drop.Index, batch);
        }

        sourceDoc = FinishMove(sourceDoc);
        var reclaimed = TopGridSlots.ReclaimVacatedPages(sourceDoc.Slots, sourceCells, drop.SourcePageCapacity);
        if (local) source.ApplyDocument(sourceDoc);
        else
        {
            to = FinishMove(to);
            sourceSnapshot!.WriteSnapshot(sourceDoc with { Items = sourceDoc.Items.Select(source.Reference).ToList() });
            destinationSnapshot!.WriteSnapshot(to with { Items = to.Items.Select(e => e.ManagedCopy != null ? e : destination.Reference(e)).ToList() });
        }
        return new(result, reclaimed, batch.Length, duplicates, failed, batch);
    }

    private static void TransferEntries(ShortcutStore store, WorkingLibrary from, WorkingLibrary to, IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0) return;
        var selected = ids.ToHashSet();
        var groups = from.Groups.Where(g => selected.Contains(g.Id)).ToArray();
        var movingIds = ids.Concat(groups.SelectMany(g => g.Members)).ToHashSet();
        if (movingIds.Overlaps(to.Items.Select(e => e.Id).Concat(to.Groups.Select(g => g.Id))))
            throw new InvalidOperationException(UiText.Get("MoveDuplicateId"));
        foreach (var entry in from.Items.Where(e => movingIds.Contains(e.Id)).ToArray())
        {
            if (!File.Exists(store.CopyPath(entry))) throw new IOException(UiText.Get("MissingShortcutCopy", entry.FileName));
            from.Items.Remove(entry);
            to.Items.Add(store.Reference(entry));
        }
        from.Groups.RemoveAll(g => selected.Contains(g.Id));
        to.Groups.AddRange(groups);
    }

    private static void Vacate(WorkingLibrary document, List<Guid> origin, IReadOnlySet<Guid> selected)
    {
        if (ReferenceEquals(origin, document.Slots)) TopGridSlots.Vacate(origin, selected);
        else origin.RemoveAll(selected.Contains);
    }

    private static Guid[] OrderedBatch(WorkingLibrary document, IReadOnlyList<Guid> ids, out List<Guid> origin)
    {
        var selected = ids.ToHashSet();
        origin = document.Slots;
        if (ids.Count == 0 || selected.Count != ids.Count || selected.Contains(Guid.Empty)) throw new ArgumentException(UiText.Get("SelectValidItems"));
        if (!selected.IsSubsetOf(origin))
            origin = document.Groups.FirstOrDefault(g => selected.IsSubsetOf(g.Members))?.Members
                ?? throw new InvalidOperationException(UiText.Get("SelectionViewChanged"));
        return origin.Where(selected.Contains).ToArray();
    }

    public Guid CreateFolder(IReadOnlyList<Guid> ids)
    {
        var document = Document();
        var selected = OrderedBatch(document, ids, out var origin);
        if (selected.Length < 2 || origin != document.Slots)
            throw new ArgumentException(UiText.Get("SelectTwoTopItems"));
        var members = ExpandBatch(document, selected).ToList();
        int index = origin.IndexOf(selected[0]);
        var selectedIds = selected.ToHashSet();
        Vacate(document, origin, selectedIds);
        document.Groups.RemoveAll(g => selectedIds.Contains(g.Id));
        var folder = Guid.NewGuid();
        document.Groups.Add(new(folder, UiText.Get("NewFolder"), members));
        origin[index] = folder;
        ApplyDocument(FinishMove(document));
        return folder;
    }

    internal string[] StageRemoval(IReadOnlyList<Guid> ids, ShortcutStore snapshot)
    {
        var document = Document();
        var selected = OrderedBatch(document, ids, out var origin).ToHashSet();
        var removed = selected.Concat(document.Groups.Where(g => selected.Contains(g.Id)).SelectMany(g => g.Members)).ToHashSet();
        var copies = document.Items.Where(e => removed.Contains(e.Id)).Select(CopyPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Vacate(document, origin, selected);
        document.Groups.RemoveAll(g => selected.Contains(g.Id));
        document.Items.RemoveAll(e => removed.Contains(e.Id));
        snapshot.WriteSnapshot(FinishMove(document) with { Items = document.Items.Select(Reference).ToList() });
        return copies;
    }

    private sealed record WorkingLibrary(List<ShortcutEntry> Items, DrawerLayout Layout, List<ShortcutGroup> Groups, List<Guid> Slots);

    private WorkingLibrary Document() => new(Items.ToList(), Layout,
        Groups.Select(g => g with { Members = g.Members.ToList() }).ToList(), TopSlots.ToList());

    private ShortcutEntry Reference(ShortcutEntry entry) =>
        entry with { ManagedCopy = Path.GetRelativePath(sharedRoot, CopyPath(entry)) };

    private static WorkingLibrary NormalizeGroups(WorkingLibrary document)
    {
        var (groups, slots) = CollapseSmallGroups(document.Groups, document.Slots);
        return document with { Groups = groups, Slots = slots };
    }

    private static WorkingLibrary FinishMove(WorkingLibrary document)
    {
        document = NormalizeGroups(document);
        ValidateGroups(document.Items, document.Groups, document.Slots);
        return document;
    }

    private void ApplyDocument(WorkingLibrary document)
    {
        Save(document.Items, document.Layout, document.Groups, document.Slots);
        Items = document.Items; Groups = document.Groups; TopSlots = document.Slots;
    }

    private void WriteSnapshot(WorkingLibrary document)
    {
        ApplyDocument(document);
        File.Copy(Config, Config + ".bak");
    }

    private (List<ShortcutEntry> Items, List<ShortcutGroup> Groups, List<Guid> Slots, DrawerLayout Layout,
        ShortcutDocument Document)? _historyDocument;

    internal ShortcutDocument CaptureHistoryDocument()
    {
        if (_historyDocument is { } cached && ReferenceEquals(cached.Items, Items) &&
            ReferenceEquals(cached.Groups, Groups) && ReferenceEquals(cached.Slots, TopSlots) && cached.Layout == Layout)
            return cached.Document;

        // These entries have already been validated on load or save. Resolving
        // paths against the file system here would repeat that work for every edit.
        var document = new ShortcutDocument(ShortcutDocument.CurrentVersion,
            Items.Select(entry => entry.ManagedCopy != null ? entry : entry with
            {
                ManagedCopy = Path.GetRelativePath(sharedRoot, Path.Combine(Copies, entry.Id.ToString("N"), entry.FileName))
            }).ToList(), Layout, Groups.Select(group => group with { Members = group.Members.ToList() }).ToList(),
            TopSlots: TopSlots.Select(id => id == Guid.Empty ? (Guid?)null : id).ToList());
        _historyDocument = (Items, Groups, TopSlots, Layout, document);
        return document;
    }

    internal void StageHistoryDocument(ShortcutDocument document)
    {
        foreach (var entry in document.Items)
            if (!File.Exists(CopyPath(entry))) throw new IOException(UiText.Get("MissingHistoryCopy"));
        var slots = document.TopSlots!.Select(id => id ?? Guid.Empty).ToList();
        ValidateGroups(document.Items, document.Groups!, slots);
        WriteSnapshot(new(document.Items, document.Layout!, document.Groups!, slots));
    }
}
