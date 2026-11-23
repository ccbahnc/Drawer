using System;
using System.Collections.Generic;
using System.Linq;

namespace Drawer.Core;

internal enum HistoryVisualKind
{
    DrawerAppeared, DrawerRemoved, Transfer, ItemsAdded, OtherChange
}

// Derived from the actual restored state. It does not participate in saving history.
internal sealed record HistoryPresentation(HistoryVisualKind Kind, DrawerDefinition Drawer, Guid[] Items,
    DrawerDefinition? Source = null)
{
    internal static HistoryPresentation? Describe(IReadOnlyList<DrawerDefinition> before,
        IReadOnlyList<DrawerDefinition> after, IReadOnlyDictionary<Guid, ShortcutStore> previous,
        IReadOnlyDictionary<Guid, ShortcutStore> changed, Guid preferred)
    {
        var oldDrawers = before.ToDictionary(drawer => drawer.Id);
        var newDrawers = after.ToDictionary(drawer => drawer.Id);
        if (after.FirstOrDefault(drawer => !oldDrawers.ContainsKey(drawer.Id)) is { } appeared)
            return new(HistoryVisualKind.DrawerAppeared, appeared, []);
        if (before.FirstOrDefault(drawer => !newDrawers.ContainsKey(drawer.Id)) is { } removed)
            return new(HistoryVisualKind.DrawerRemoved, removed, []);

        var deltas = after.Where(drawer => previous.ContainsKey(drawer.Id) && changed.ContainsKey(drawer.Id))
            .Select(drawer => new HistoryLibraryDelta(drawer, previous[drawer.Id], changed[drawer.Id])).ToArray();
        foreach (var destination in deltas)
            foreach (var source in deltas)
            {
                if (source == destination || destination.Added.Length == 0 || source.Removed.Length == 0) continue;
                var moved = destination.Added.Intersect(source.Removed).ToArray();
                if (moved.Length == 0) continue;
                var originalGroups = source.Before.Groups.Select(group => group.Id).ToHashSet();
                var movedFolders = destination.After.Groups.Where(group => originalGroups.Contains(group.Id)).ToArray();
                var items = moved.Except(movedFolders.SelectMany(group => group.Members))
                    .Concat(movedFolders.Select(group => group.Id)).ToArray();
                return new(HistoryVisualKind.Transfer, destination.Drawer, items, source.Drawer);
            }

        // An operation can also renumber its neighbours. Prefer the drawer whose own settings changed.
        var metadata = after.Where(drawer => drawer with { Storage = "" } != oldDrawers[drawer.Id] with { Storage = "" })
            .OrderByDescending(drawer => drawer with { Order = 0, Storage = "" } !=
                oldDrawers[drawer.Id] with { Order = 0, Storage = "" })
            .ThenByDescending(drawer => drawer.Id == preferred).FirstOrDefault();
        if (metadata != null) return new(HistoryVisualKind.OtherChange, metadata, []);

        var delta = deltas.OrderByDescending(change => change.Drawer.Id == preferred).FirstOrDefault();
        if (delta == null) return null;
        if (delta.Added.Length != 0) return new(HistoryVisualKind.ItemsAdded, delta.Drawer, delta.Added);
        if (delta.Removed.Length != 0) return new(HistoryVisualKind.OtherChange, delta.Drawer, []);

        var previousEntryNames = delta.Before.Items.ToDictionary(item => item.Id, item => item.ShownName);
        if (delta.After.Items.FirstOrDefault(item => previousEntryNames[item.Id] != item.ShownName) is { } item)
            return new(HistoryVisualKind.OtherChange, delta.Drawer, [item.Id]);
        var previousGroups = delta.Before.Groups.ToDictionary(group => group.Id);
        var currentGroups = delta.After.Groups.ToDictionary(group => group.Id);
        if (delta.After.Groups.FirstOrDefault(group => !previousGroups.ContainsKey(group.Id)) is { } formed)
            return new(HistoryVisualKind.OtherChange, delta.Drawer, [formed.Id]);
        if (delta.Before.Groups.FirstOrDefault(group => !currentGroups.ContainsKey(group.Id)) is { } dissolved)
            return new(HistoryVisualKind.OtherChange, delta.Drawer, dissolved.Members.ToArray());
        if (delta.After.Groups.FirstOrDefault(group => previousGroups[group.Id].Name != group.Name) is { } renamed)
            return new(HistoryVisualKind.OtherChange, delta.Drawer, [renamed.Id]);
        if (delta.After.Groups.FirstOrDefault(group => !group.Members.SequenceEqual(previousGroups[group.Id].Members)) is { } updated)
            return new(HistoryVisualKind.OtherChange, delta.Drawer, [updated.Id]);
        var rearranged = delta.After.TopSlots.Where((id, index) => id != Guid.Empty &&
            (index >= delta.Before.TopSlots.Count || id != delta.Before.TopSlots[index])).ToArray();
        return new(HistoryVisualKind.OtherChange, delta.Drawer, rearranged);
    }

    internal int FirstVisibleCell(ShortcutStore store)
    {
        var ids = Items.ToHashSet();
        foreach (var group in store.Groups)
            if (group.Members.Any(ids.Contains)) ids.Add(group.Id);
        return store.TopSlots.FindIndex(ids.Contains);
    }

    private sealed record HistoryLibraryDelta(DrawerDefinition Drawer, ShortcutStore Before, ShortcutStore After)
    {
        public Guid[] Added { get; } = After.Items.Select(item => item.Id).Except(Before.Items.Select(item => item.Id)).ToArray();
        public Guid[] Removed { get; } = Before.Items.Select(item => item.Id).Except(After.Items.Select(item => item.Id)).ToArray();
    }
}
