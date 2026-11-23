using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Drawer.Core;
internal sealed record ShortcutGroup(Guid Id, string Name, List<Guid> Members,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool PreserveSingleton = false);
internal sealed partial class ShortcutStore
{
    public List<ShortcutGroup> Groups { get; private set; } = new();
    public List<Guid> TopSlots { get; private set; } = new();
    public List<Guid> TopOrder => TopGridSlots.Occupied(TopSlots);
    internal static void ValidateGroups(List<ShortcutEntry> items, List<ShortcutGroup> groups, List<Guid> top)
    {
        var shortcuts = items.Select(e => e.Id).ToHashSet();
        if (groups.Any(g => g == null || g.Id == Guid.Empty || shortcuts.Contains(g.Id) || string.IsNullOrWhiteSpace(g.Name)
            || g.Name.Length > 100 || g.Members == null || g.Members.Count == 0)
            || groups.Select(g => g.Id).Distinct().Count() != groups.Count)
            throw new InvalidDataException(UiText.Get("InvalidFolderRecord"));
        var groupIds = groups.Select(g => g.Id).ToHashSet();
        var occupied = TopGridSlots.Occupied(top);
        if (occupied.Distinct().Count() != occupied.Count || occupied.Any(id => !shortcuts.Contains(id) && !groupIds.Contains(id))
            || groupIds.Any(id => !occupied.Contains(id))) throw new InvalidDataException(UiText.Get("InvalidTopOrder"));
        var ownership = occupied.Where(shortcuts.Contains).Concat(groups.SelectMany(g => g.Members)).ToList();
        if (ownership.Count != shortcuts.Count || ownership.Distinct().Count() != ownership.Count || ownership.Any(id => !shortcuts.Contains(id)))
            throw new InvalidDataException(UiText.Get("InvalidOwnership"));
    }
    private static (List<ShortcutGroup> Groups, List<Guid> Top) CollapseSmallGroups(List<ShortcutGroup> groups, List<Guid> top)
    {
        var small = groups.Where(g => g.Members.Count == 0 || g.Members.Count == 1 && !g.PreserveSingleton).ToDictionary(g => g.Id);
        return (groups.Where(g => !small.ContainsKey(g.Id)).ToList(),
            top.Select(id => small.TryGetValue(id, out var group) ? group.Members.SingleOrDefault() : id).ToList());
    }
    private void CommitGroups(List<ShortcutGroup> groups, List<Guid> top)
    {
        (groups, top) = CollapseSmallGroups(groups, top);
        ValidateGroups(Items, groups, top);
        Save(Items, groups: groups, top: top); Groups = groups; TopSlots = top;
    }
    public Guid Merge(Guid source, Guid target)
    {
        if (source == target || !TopOrder.Contains(source) || !TopOrder.Contains(target) || !Items.Any(e => e.Id == source))
            throw new ArgumentException(UiText.Get("TopLevelMergeOnly"));
        var groups = Groups.ToList(); var top = TopSlots.Select(id => id == source ? Guid.Empty : id).ToList();
        var group = groups.FirstOrDefault(g => g.Id == target);
        Guid folder = target;
        if (group != null) groups[groups.IndexOf(group)] = group with { Members = group.Members.Append(source).ToList() };
        else
        {
            if (!Items.Any(e => e.Id == target)) throw new ArgumentException(UiText.Get("MissingMergeTarget"));
            folder = Guid.NewGuid(); groups.Add(new(folder, UiText.Get("NewFolder"), new() { target, source })); top[top.IndexOf(target)] = folder;
        }
        CommitGroups(groups, top); return folder;
    }
    public ShortcutGroup RenameGroup(Guid id, string name)
    {
        name = name.Trim(); if (name.Length is < 1 or > 100) throw new ArgumentException(UiText.Get("FolderNameRange"));
        int index = Groups.FindIndex(g => g.Id == id);
        if (index < 0) throw new ArgumentException(UiText.Get("MissingFolder"));
        var group = Groups[index];
        if (group.Name == name) return group;
        var next = new List<ShortcutGroup>(Groups);
        next[index] = group with { Name = name };
        Save(Items, groups: next);
        Groups = next;
        return next[index];
    }
    public void ReorderMembers(Guid id, IEnumerable<Guid> order)
    {
        var group = Groups.Single(g => g.Id == id); var ids = order.ToList();
        if (ids.Count != group.Members.Count || !ids.OrderBy(x => x).SequenceEqual(group.Members.OrderBy(x => x))) throw new ArgumentException(UiText.Get("InvalidMemberOrder"));
        CommitGroups(Groups.Select(g => g.Id == id ? g with { Members = ids } : g).ToList(), TopSlots);
    }
    public void MoveOut(Guid member, int insertion, int pageCapacity = 0)
    {
        var group = Groups.Single(g => g.Members.Contains(member));
        var top = TopSlots.ToList();
        var groups = Groups.Select(g => g.Id == group.Id ? g with { Members = g.Members.Where(id => id != member).ToList() } : g).ToList();
        (groups, top) = CollapseSmallGroups(groups, top);
        if (pageCapacity > 0) TopGridSlots.InsertPaged(top, insertion, new[] { member }, pageCapacity);
        else TopGridSlots.Insert(top, insertion, new[] { member });
        CommitGroups(groups, top);
    }
    public void Dissolve(Guid id, int pageCapacity = 0)
    {
        var group = Groups.Single(g => g.Id == id); var top = TopSlots.ToList(); int index = top.IndexOf(id);
        top[index] = Guid.Empty;
        if (pageCapacity > 0) TopGridSlots.InsertPaged(top, index, group.Members, pageCapacity);
        else TopGridSlots.Insert(top, index, group.Members);
        CommitGroups(Groups.Where(g => g.Id != id).ToList(), top);
    }
}
