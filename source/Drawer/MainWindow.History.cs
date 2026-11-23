using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Drawer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Drawer;

public sealed partial class MainWindow
{
    private bool _historyRestoring;
    private bool _historyMorph;
    private Guid? _historyAnimatingFolder;
    private readonly Queue<bool> _pendingHistory = new();
    private sealed record HistoryFolderTransition(ShortcutGroup Folder, bool Forming, int BeforeCell, int AfterCell,
        Guid[] SeparateItems, Guid Anchor, bool Existing);
    private const double HistoryFadeMs = 170;
    private readonly Dictionary<Guid, double> _historyFadeIn = new();
    private readonly List<(Guid Id, FrameworkElement Visual, double Opacity)> _historyFadeOut = new();
    private GridView? _historyFadeGrid;
    private Canvas? _historyFadeLayer;
    private double _historyFadeStart;

    private IDisposable BeginDataHistory() => _preferences.BeginHistory(_stores.Values.Append(_store));

    private async void HandleHistory(bool redo, bool repeated = false)
    {
        if (_closed || _exitRequested || (_morphCommitted && !_historyMorph)) return;
        if (_historyRestoring)
        {
            if (!repeated || _pendingHistory.Count == 0) _pendingHistory.Enqueue(redo);
            return;
        }
        using var hold = HoldOpen();
        _historyRestoring = true;
        try
        {
            while (!_closed)
            {
                if (redo ? _preferences.CanRedo : _preferences.CanUndo) await ApplyNextHistoryChange(redo);
                if (NativeMethods.GetForegroundWindow() != _hwnd || !_pendingHistory.TryDequeue(out redo)) break;
            }
        }
        catch (Exception ex) { if (!_closed) ErrorStatus(UiText.Get("HistoryRestoreFailed", ex.Message)); }
        finally { _pendingHistory.Clear(); _historyRestoring = false; }
    }

    private async Task ApplyNextHistoryChange(bool redo)
    {
        var stores = new Dictionary<Guid, ShortcutStore>(_stores);
        var definitions = _preferences.Value.Drawers.ToArray();
        var previousGeometries = _edgeGeometries;
        uint previousDpi = _layoutDpi;
        var folder = _openFolder;
        var previous = _store;
        Guid drawer = _drawerId;
        var previousLayout = definitions.FirstOrDefault(d => d.Id == drawer);
        var changed = await Task.Run(() => _preferences.ApplyHistory(redo, stores.Values));
        if (_closed || changed == null) return;
        try
        {
            var presentation = HistoryPresentation.Describe(definitions, _preferences.Value.Drawers, stores, changed, drawer);
            if (presentation != null && presentation.Kind != HistoryVisualKind.DrawerRemoved &&
                (presentation.Kind is HistoryVisualKind.Transfer or HistoryVisualKind.DrawerAppeared ||
                    presentation.Drawer.Id != drawer || !_visible))
            {
                var prior = definitions.FirstOrDefault(d => d.Id == presentation.Drawer.Id);
                bool resized = GridDimensionsChanged(prior, presentation.Drawer) && prior!.Edge == presentation.Drawer.Edge;
                EdgeGeometry? priorGeometry = resized && previousDpi == _layoutDpi &&
                    previousGeometries.TryGetValue(presentation.Drawer.Id, out var geometry) ? geometry : null;
                await PresentHistoryDrawer(presentation, changed, priorGeometry);
            }
            else await PresentHistoryChange(drawer, previous, changed, folder, previousLayout);
        }
        catch (Exception ex)
        {
            // The history transaction has finished. Publish its result even if an animation fails.
            PrototypeLog.Write("history presentation: " + ex);
            if (!_closed) { ClearHistoryFades(); RemoveMergeVisual(); PublishHistoryStores(changed); }
        }
    }

    private static bool GridDimensionsChanged(DrawerDefinition? before, DrawerDefinition? after) =>
        before != null && after != null && (before.Rows != after.Rows || before.Columns != after.Columns);

    private static HistoryFolderTransition? FindHistoryFolderTransition(ShortcutStore before, ShortcutStore after)
    {
        return FindFormation(before, after, forming: true) ?? FindFormation(after, before, forming: false);
    }

    private static HistoryFolderTransition? FindFormation(ShortcutStore separate, ShortcutStore grouped, bool forming)
    {
        var remaining = grouped.TopSlots.ToHashSet();
        foreach (var folder in grouped.Groups)
        {
            var members = folder.Members.ToHashSet();
            var old = separate.Groups.FirstOrDefault(g => g.Id == folder.Id);
            if (old != null && (old.Members.Count >= members.Count || !old.Members.All(members.Contains))) continue;
            var separateItems = separate.TopSlots.Where(id => id != Guid.Empty && !remaining.Contains(id) &&
                (members.Contains(id) || separate.Groups.Any(g => g.Id == id && g.Members.All(members.Contains)))).ToArray();
            if (separateItems.Length < (old == null ? 2 : 1)) continue;
            if (old != null) separateItems = separateItems.Append(old.Id).ToArray();
            if (!members.SetEquals(separate.ExpandClipboardItems(separateItems))) continue;
            int folderCell = grouped.TopSlots.IndexOf(folder.Id);
            if (folderCell < 0) continue;
            Guid anchor = old?.Id ?? (folderCell < separate.TopSlots.Count && separateItems.Contains(separate.TopSlots[folderCell])
                ? separate.TopSlots[folderCell] : separateItems[0]);
            int memberCell = separate.TopSlots.IndexOf(anchor);
            if (memberCell >= 0)
                return new(folder, forming, forming ? memberCell : folderCell, forming ? folderCell : memberCell, separateItems, anchor, old != null);
        }
        return null;
    }

    private async Task<bool> SettleHistoryPage(int cell)
    {
        int page = cell / PageCapacity;
        if (!_visible || page >= FormalPages) return false;
        if (page != _topPage) GoToPage(page, animate: false);
        // The main timer pauses while history is being published. Settle the
        // page explicitly before sampling an icon or showing a transition.
        for (int attempt = 0; attempt < 12 && !_closed && _visible; attempt++)
        {
            TickTopPage(_clock.Elapsed.TotalMilliseconds);
            if (_pageReady && _topPage == page)
            {
                ShortcutGrid.UpdateLayout();
                return true;
            }
            await Task.Delay(16);
        }
        return false;
    }

    private async Task PresentHistoryChange(Guid drawer, ShortcutStore previous,
        Dictionary<Guid, ShortcutStore> changed, Guid? openFolder, DrawerDefinition? previousLayout)
    {
        var next = changed.GetValueOrDefault(drawer) ?? previous;
        var restoredLayout = _preferences.Value.Drawers.FirstOrDefault(d => d.Id == drawer);
        bool dimensionsChanged = GridDimensionsChanged(previousLayout, restoredLayout);
        bool layoutChanged = previousLayout != null && restoredLayout != null &&
            (dimensionsChanged ||
             previousLayout.ContentScale != restoredLayout.ContentScale || previousLayout.Edge != restoredLayout.Edge);
        var retainedGroup = openFolder is Guid groupId && !layoutChanged && restoredLayout != null
            ? next.Groups.FirstOrDefault(group => group.Id == groupId) : null;
        HistoryFolderTransition? transition = null;
        var grid = _memberGrid ?? ShortcutGrid;
        Dictionary<Guid, (ShortcutTile Tile, IconPose Pose)> previousFrame = new();
        Dictionary<Guid, double> previousGhosts = new();
        var removed = new List<(Guid Id, FrameworkElement Visual, IconPose Pose)>();
        var morphPoses = new Dictionary<Guid, IconPose>();
        int morphPage = _topPage;
        double? morphBackground = null;
        try
        {
            if (openFolder == null && !layoutChanged && drawer == _drawerId && restoredLayout != null && !ReferenceEquals(next, previous))
                transition = FindHistoryFolderTransition(previous, next);
            if (_historyMorph && transition?.Folder.Id == _historyAnimatingFolder)
            {
                foreach (var source in _morphSources) morphPoses[source.Id] = source.Current;
                foreach (var (id, sprite) in _dissolveSprites)
                    if (_mergeAnimation is { } motion) morphPoses[id] = sprite.From.Mix(sprite.To, motion.Eased);
                morphBackground = _morphBackground?.Opacity;
            }
            previousFrame = CaptureHistoryFrame(grid);
            previousGhosts = _historyFadeOut.ToDictionary(item => item.Id, item => item.Visual.Opacity);
            var nextIds = (retainedGroup?.Members ?? next.TopSlots).ToHashSet();
            if (transition == null && restoredLayout != null && (openFolder == null || retainedGroup != null))
                foreach (var (itemId, frame) in previousFrame)
                    if (!nextIds.Contains(itemId)) removed.Add((itemId, IconVisual(frame.Tile, frame.Pose.Size), frame.Pose));
            ClearHistoryFades();
            RemoveMergeVisual();
        }
        catch (Exception ex) { PrototypeLog.Write("history visual capture: " + ex.Message); }
        _cutItems = null;
        if (retainedGroup == null) CloseFolder();
        ClearItemSelection();
        _slotMotions.Clear();

        IconPose? origin = null;
        bool forming = false;
        try
        {
            if (transition != null && await SettleHistoryPage(transition.BeforeCell))
            {
                if (transition.Forming)
                {
                    var tiles = _tiles.Where(tile => !tile.IsEmpty).ToDictionary(tile => tile.Id);
                    PrepareSelectedFolderVisual(transition.SeparateItems.Select(id => tiles[id]).ToArray(),
                        transition.Anchor, transition.Folder.Members, transition.Existing,
                        _topPage == morphPage ? morphPoses : null, _topPage == morphPage ? morphBackground : null);
                    CommitMergeVisual(transition.Folder.Id);
                    forming = true;
                }
                else if (_tiles.FirstOrDefault(tile => tile.Id == transition.Folder.Id) is { } tile)
                    origin = CaptureDissolveOrigin(tile);
            }
        }
        catch (Exception ex)
        {
            RemoveMergeVisual();
            PrototypeLog.Write("history folder animation setup: " + ex.Message);
        }
        if (_closed) return;
        PublishHistoryStores(changed, animateSize: dimensionsChanged && previousLayout!.Edge == restoredLayout!.Edge);

        if (transition != null && (forming || origin != null))
        {
            try
            {
                if (await SettleHistoryPage(transition.AfterCell))
                {
                    if (forming)
                    {
                        StartSelectedFolderAnimation();
                        ApplyMotions(ShortcutGrid, _tiles, false, _metrics);
                        PositionMergeVisual(_clock.Elapsed.TotalMilliseconds);
                    }
                    else if (origin is { } pose) BeginDissolveVisual(transition.Folder.Members, pose,
                        _topPage == morphPage ? morphPoses : null, _topPage == morphPage ? morphBackground : null);
                    _historyMorph = _morphCommitted;
                    _historyAnimatingFolder = _historyMorph ? transition.Folder.Id : null;
                    return;
                }
                if (!_closed) RemoveMergeVisual();
            }
            catch (Exception ex)
            {
                if (!_closed) RemoveMergeVisual();
                PrototypeLog.Write("history folder animation: " + ex.Message);
            }
        }
        try
        {
            if (!_closed && restoredLayout != null && drawer == _drawerId &&
                (ReferenceEquals(grid, _memberGrid) || ReferenceEquals(grid, ShortcutGrid) && await SettleHistoryPage(_topPage * PageCapacity)))
                PresentHistoryFrame(grid, previousFrame, previousGhosts, removed, (openFolder == null ? previous.TopSlots :
                    previous.Groups.FirstOrDefault(group => group.Id == openFolder)?.Members ?? new()).ToHashSet());
        }
        catch (Exception ex) { ClearHistoryFades(); PrototypeLog.Write("history item animation: " + ex.Message); }
        if (!_closed && retainedGroup == null && openFolder is Guid folderToReopen &&
            _tiles.FirstOrDefault(tile => tile.Id == folderToReopen) is { } remaining &&
            _store.TopSlots.IndexOf(folderToReopen) / PageCapacity == _topPage) OpenFolder(remaining);
    }

    private Dictionary<Guid, (ShortcutTile Tile, IconPose Pose)> CaptureHistoryFrame(GridView grid, HashSet<Guid>? ids = null)
    {
        var frame = new Dictionary<Guid, (ShortcutTile, IconPose)>();
        if (!_visible || _interaction.Position < .999 || _folderAnimating || grid.ActualHeight <= 0 ||
            (ReferenceEquals(grid, ShortcutGrid) && !_pageReady)) return frame;
        var bounds = HistoryViewport(grid);
        int start = ReferenceEquals(grid, ShortcutGrid) ? _topPage * PageCapacity : 0;
        int end = ReferenceEquals(grid, ShortcutGrid) ? Math.Min(start + PageCapacity, grid.Items.Count) : grid.Items.Count;
        for (int i = start; i < end; i++)
        {
            if (grid.Items[i] is not ShortcutTile { IsEmpty: false } tile ||
                (ids != null && !ids.Contains(tile.Id)) ||
                grid.ContainerFromIndex(i) is not FrameworkElement container) continue;
            var rect = TilePictureRect(grid, tile, DragLayer);
            if (rect.Left >= bounds.Right || rect.Right <= bounds.Left || rect.Top >= bounds.Bottom || rect.Bottom <= bounds.Top) continue;
            double opacity = _historyMorph && container.Opacity == 0 ? 1 : container.Opacity;
            frame[tile.Id] = (tile, new(rect.X, rect.Y, rect.Width, opacity));
        }
        return frame;
    }

    private Rect HistoryViewport(GridView grid)
    {
        bool member = ReferenceEquals(grid, _memberGrid);
        var host = member ? (FrameworkElement)grid : GridHost;
        var point = host.TransformToVisual(DragLayer).TransformPoint(new Point());
        return new(point.X, point.Y, host.ActualWidth, member ? host.ActualHeight : TopContentHeight);
    }

    private void PresentHistoryFrame(GridView grid, Dictionary<Guid, (ShortcutTile Tile, IconPose Pose)> before,
        Dictionary<Guid, double> previousGhosts, List<(Guid Id, FrameworkElement Visual, IconPose Pose)> removed, HashSet<Guid> previousIds)
    {
        bool member = ReferenceEquals(grid, _memberGrid);
        var tiles = member ? (IReadOnlyList<ShortcutTile>)_members : _tiles;
        var metrics = member ? MemberMetrics() : _metrics;
        grid.UpdateLayout();
        ApplyMotions(grid, tiles, member, metrics);
        var after = CaptureHistoryFrame(grid);
        _historyFadeGrid = grid;
        _historyFadeStart = _clock.Elapsed.TotalMilliseconds;
        int first = member ? 0 : _topPage * PageCapacity;
        int end = member ? tiles.Count : Math.Min(tiles.Count, first + PageCapacity);
        for (int index = first; index < end; index++)
        {
            Guid id = tiles[index].Id;
            if (!after.TryGetValue(id, out var current)) continue;
            if (before.TryGetValue(id, out var earlier))
            {
                double centerShift = (earlier.Pose.Size - current.Pose.Size) / 2;
                double dx = earlier.Pose.X - current.Pose.X + centerShift, dy = earlier.Pose.Y - current.Pose.Y + centerShift;
                if (Math.Abs(dx) > .05 || Math.Abs(dy) > .05)
                {
                    var slot = Slot(index, metrics);
                    _slotMotions[(member, id)] = new(slot.X + dx, slot.Y + dy) { TargetX = slot.X, TargetY = slot.Y };
                }
                if (earlier.Pose.Alpha < 1) _historyFadeIn[id] = earlier.Pose.Alpha;
            }
            else if (!previousIds.Contains(id)) _historyFadeIn[id] = previousGhosts.GetValueOrDefault(id);
        }
        if (removed.Count > 0) AttachItemFadeOut(HistoryViewport(grid), removed);
        ApplyMotions(grid, tiles, member, metrics);
    }

    private void AttachItemFadeOut(Rect viewport, List<(Guid Id, FrameworkElement Visual, IconPose Pose)> removed)
    {
        if (removed.Count == 0) return;
        var layer = new Canvas { IsHitTestVisible = false, Clip = new RectangleGeometry { Rect = viewport } };
        _historyFadeLayer = layer;
        DragLayer.Children.Add(layer);
        foreach (var (id, visual, pose) in removed)
        {
            layer.Children.Add(visual);
            PlaceSprite(visual, pose, 0, 0);
            _historyFadeOut.Add((id, visual, pose.Alpha));
        }
    }

    private double HistoryFadeProgress => MergeAnimation.Ease(Math.Clamp((_clock.Elapsed.TotalMilliseconds - _historyFadeStart) / HistoryFadeMs, 0, 1));

    private double HistoryItemOpacity(GridView grid, Guid id) => grid == _historyFadeGrid && _historyFadeIn.TryGetValue(id, out double start)
        ? start + (1 - start) * HistoryFadeProgress : 1;

    private void AdvanceHistoryFades()
    {
        if (_historyFadeGrid == null) return;
        double progress = HistoryFadeProgress;
        if (progress >= 1) { ClearHistoryFades(); return; }
        foreach (var (_, visual, opacity) in _historyFadeOut) visual.Opacity = opacity * (1 - progress);
    }

    private void ClearHistoryFades()
    {
        _historyFadeIn.Clear();
        _historyFadeOut.Clear();
        if (_historyFadeLayer != null) DragLayer.Children.Remove(_historyFadeLayer);
        _historyFadeLayer = null;
        _historyFadeGrid = null;
    }
}
