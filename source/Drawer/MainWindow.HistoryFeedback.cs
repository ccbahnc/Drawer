using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Drawer.Core;

namespace Drawer;

public sealed partial class MainWindow
{
    private void PublishHistoryStores(Dictionary<Guid, ShortcutStore> changed, bool animateSize = false)
    {
        foreach (var (id, store) in changed) _stores[id] = store;
        ApplyPreferences(restorePreferred: true, animatePreferredSize: animateSize);
    }

    private async Task PresentHistoryDrawer(HistoryPresentation change, Dictionary<Guid, ShortcutStore> changed,
        EdgeGeometry? previousGeometry)
    {
        ClearHistoryFades();
        RemoveMergeVisual();
        _cutItems = null;
        CloseFolder();
        ClearItemSelection();
        _slotMotions.Clear();
        PublishHistoryStores(changed);

        var target = change.Drawer.Id;
        if (_displayRefreshPending || !change.Drawer.Enabled) return;
        if (!_stores.ContainsKey(target))
        {
            ErrorStatus(UiText.Get("DrawerReadDeferred", change.Drawer.Name));
            return;
        }
        if (!_edgeGeometries.ContainsKey(target))
        {
            ErrorStatus(UiText.Get("PlacementNoSpace"));
            return;
        }
        if (_drawerId != target)
        {
            HideImmediately();
            SelectDrawer(target);
        }
        UpdateGeometry(immediateSize: true, restorePreferred: true);

        int anchor = change.FirstVisibleCell(_store);
        if (anchor >= 0 && anchor / PageCapacity != _topPage) GoToPage(anchor / PageCapacity, animate: false);
        bool arrivals = change.Kind is HistoryVisualKind.Transfer or HistoryVisualKind.ItemsAdded;
        if (arrivals) PrepareHistoryArrival(change.Items);
        var finalGeometry = _sizeTarget;
        bool animateSize = !_visible && previousGeometry is { } before &&
            before.Work == finalGeometry.Work && before.Monitor == finalGeometry.Monitor && before.Drawer != finalGeometry.Drawer;
        if (animateSize) SetSizeTarget(previousGeometry!.Value, immediate: true);
        bool revealed;
        try { revealed = await RevealHistoryDrawer(); }
        finally
        {
            if (animateSize && !_closed && !_displayRefreshPending && _drawerId == target)
                SetSizeTarget(finalGeometry, immediate: !_visible);
        }
        if (!revealed || !await SettleHistoryPage(_topPage * PageCapacity))
        {
            ClearHistoryFades();
            return;
        }
        if (arrivals) StartHistoryArrival(change);
    }

    private async Task<bool> RevealHistoryDrawer()
    {
        if (!_visible) OpenExplicitly();
        _interaction.KeepOpen();
        double last = _clock.Elapsed.TotalMilliseconds, deadline = last + 1200;
        // The regular timer pauses while we publish history. Drive the existing reveal here.
        while (!_closed && !_displayRefreshPending && (!_visible || _interaction.Position < 1))
        {
            await Task.Delay(DrawerSettings.PollMs);
            double now = _clock.Elapsed.TotalMilliseconds;
            if (_closed || _displayRefreshPending || now >= deadline) break;
            _interaction.Update(now, (now - last) / 1000, true, true, true);
            if (_historyFadeGrid != null) ApplyMotions(ShortcutGrid, _tiles, false, _metrics);
            RenderPosition();
            TickTopPage(now);
            last = now;
        }
        return !_closed && !_displayRefreshPending && _visible && _interaction.Position == 1;
    }

    private void PrepareHistoryArrival(Guid[] items)
    {
        _historyFadeGrid = ShortcutGrid;
        _historyFadeStart = double.PositiveInfinity;
        foreach (var id in items) _historyFadeIn[id] = 0;
        ApplyMotions(ShortcutGrid, _tiles, false, _metrics);
    }

    private void StartHistoryArrival(HistoryPresentation change)
    {
        double dx = 0, dy = 0;
        if (change.Source is { } source && _edgeGeometries.TryGetValue(source.Id, out var from))
        {
            var dest = _geometry.Drawer;
            double x = (double)from.Drawer.Left + from.Drawer.Right - dest.Left - dest.Right;
            double y = (double)from.Drawer.Top + from.Drawer.Bottom - dest.Top - dest.Bottom;
            double length = Math.Max(1, Math.Sqrt(x * x + y * y));
            dx = x / length * 32;
            dy = y / length * 32;
        }
        int first = _topPage * PageCapacity, end = Math.Min(first + PageCapacity, _tiles.Count);
        for (int index = first; index < end; index++)
        {
            Guid id = _tiles[index].Id;
            if (!_historyFadeIn.ContainsKey(id) || dx == 0 && dy == 0) continue;
            var slot = Slot(index, _metrics);
            _slotMotions[(false, id)] = new(slot.X + dx, slot.Y + dy) { TargetX = slot.X, TargetY = slot.Y };
        }
        _historyFadeStart = _clock.Elapsed.TotalMilliseconds;
        ApplyMotions(ShortcutGrid, _tiles, false, _metrics);

        // A transfer into a folder arrives at its thumbnail, then uses the normal folder animation.
        if (change.Kind != HistoryVisualKind.Transfer) return;
        var items = change.Items.ToHashSet();
        var folder = _store.Groups.FirstOrDefault(group => items.IsSubsetOf(group.Members));
        if (folder != null && _store.TopSlots.IndexOf(folder.Id) is int folderCell && folderCell >= first && folderCell < end)
            OpenFolder(_tiles[folderCell]);
    }
}
