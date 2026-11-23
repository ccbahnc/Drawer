using System;
using System.Collections.Generic;
using System.Linq;

namespace Drawer.Core;

internal sealed class ItemSelection
{
    private readonly HashSet<Guid> _items = new();
    public bool Active { get; private set; }
    public Guid? Anchor { get; private set; }
    public int Count => _items.Count;
    public bool Contains(Guid id) => _items.Contains(id);
    public bool IsPartOfMultiSelection(Guid id) => _items.Count > 1 && _items.Contains(id);

    public void Click(Guid[] order, Guid id, bool range, bool additive)
    {
        int end = Array.IndexOf(order, id);
        if (end < 0) return;
        Active = true;
        if (!range)
        {
            if (!_items.Remove(id))
            {
                _items.Add(id);
                Anchor = id;
            }
            else if (Anchor == id) ChooseAnchor(order);
            return;
        }

        int start = Anchor is Guid anchor && _items.Contains(anchor) ? Array.IndexOf(order, anchor) : -1;
        if (start < 0) { start = end; Anchor = id; }
        if (!additive) _items.Clear();
        for (int i = Math.Min(start, end); i <= Math.Max(start, end); i++) _items.Add(order[i]);
    }

    public void Prune(Guid[] order)
    {
        var live = order.ToHashSet();
        if (_items.RemoveWhere(id => !live.Contains(id)) > 0 && _items.Count == 0) Clear();
        else if (Anchor is Guid anchor && !_items.Contains(anchor)) ChooseAnchor(order);
    }

    // If the anchor was removed, the last selected item in data order is deterministic.
    private void ChooseAnchor(Guid[] order) => Anchor = order.Where(_items.Contains).Select(id => (Guid?)id).LastOrDefault();

    public void Clear()
    {
        _items.Clear();
        Anchor = null;
        Active = false;
    }

    public readonly record struct Snapshot(bool Active, Guid? Anchor, Guid[] Items);
    public Snapshot Capture() => new(Active, Anchor, _items.ToArray());
    public void Restore(Snapshot snapshot)
    {
        Clear();
        Active = snapshot.Active;
        Anchor = snapshot.Anchor;
        _items.UnionWith(snapshot.Items);
    }
}
