using System;
using Drawer.Core;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using static Drawer.NativeMethods;

namespace Drawer;
public sealed partial class MainWindow
{
    private readonly ItemSelection _selection = new();
    private readonly Dictionary<ShortcutTile, SelectionScale> _iconScales = new();
    private ShortcutTile? _singleSelectedTile;
    private ShortcutTile? _hoveredTile;
    private RemovalConfirmation? _confirmationWindow;
    private bool _formingFolder;
    private bool _deletingSelection;

    private void RefreshSelectionMarks(bool animateSelection = false)
    {
        var items = CurrentItems;
        if (_selection.Active) _selection.Prune(items.Select(tile => tile.Id).ToArray());
        foreach (var tile in _tiles) tile.SetSelection(_selection.Active && _openFolder == null, _selection.Contains(tile.Id));
        foreach (var tile in _members) tile.SetSelection(_selection.Active && _openFolder != null, _selection.Contains(tile.Id));
        var single = _selection.Count == 1 ? items.FirstOrDefault(tile => _selection.Contains(tile.Id)) : null;
        if (!ReferenceEquals(single, _singleSelectedTile))
        {
            var previous = _singleSelectedTile;
            _singleSelectedTile = single;
            if (previous != null) AnimateIconScale(previous, FeedbackScale(previous), interruptPop: true);
            if (single != null) AnimateIconScale(single, FeedbackScale(single), animateSelection && !IconFeedbackBlocked);
        }
        foreach (var tile in _iconScales.Keys.Where(tile => !items.Contains(tile)).ToArray())
        {
            tile.SetIconScale(1);
            _iconScales.Remove(tile);
        }
    }

    private bool IconFeedbackBlocked => _internalDrag || _externalDragging || NavigationPressed || NavigationTransitionActive || _boxSelection != null ||
        _folderAnimating || _formingFolder || _morphCommitted || _morphEnding;

    private double FeedbackScale(ShortcutTile tile) => SelectionScale.Target(
        ReferenceEquals(tile, _hoveredTile), ReferenceEquals(tile, _singleSelectedTile), IconFeedbackBlocked);

    private ShortcutTile? HoveredIcon()
    {
        if (!_visible || _interaction.Position < .999 || IconFeedbackBlocked || PageTurning || _pageNeedsSnap ||
            _menuOpen || _settingsOpen || _pickerOpen || !GetCursorPos(out var cursor) ||
            !_geometry.Visible(_edge, _interaction.Position).Contains(cursor.X, cursor.Y)) return null;
        bool member = _openFolder != null;
        var grid = member ? _memberGrid : ShortcutGrid;
        if (grid == null || !DragPoint(cursor, grid, out var point) || point.Y >= grid.ActualHeight) return null;
        return GetAncestor(WindowFromPoint(cursor), 2) == _hwnd ? HitIcon(grid, point) : null;
    }

    private void RefreshIconFeedback()
    {
        var hovered = HoveredIcon();
        if (!ReferenceEquals(hovered, _hoveredTile))
        {
            var previous = _hoveredTile;
            _hoveredTile = hovered;
            if (previous != null) AnimateIconScale(previous, FeedbackScale(previous));
            if (hovered != null) AnimateIconScale(hovered, FeedbackScale(hovered));
        }
        if (_singleSelectedTile is { } selected) AnimateIconScale(selected, FeedbackScale(selected));
    }

    private void AnimateIconScale(ShortcutTile tile, double to, bool pop = false, bool interruptPop = false)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        bool moving = _iconScales.TryGetValue(tile, out var current);
        if (!pop && (moving ? current.To == to && !(interruptPop && current.Pop) : tile.IconScale == to)) return;
        double from = moving ? current.At(now) : tile.IconScale;
        tile.SetIconScale(from);
        _iconScales[tile] = new(from, to, now, pop);
    }

    private void StepIconScales()
    {
        RefreshIconFeedback();
        if (_iconScales.Count == 0) return;
        double now = _clock.Elapsed.TotalMilliseconds;
        foreach (var (tile, motion) in _iconScales.ToArray())
        {
            tile.SetIconScale(motion.At(now));
            if (now - motion.Start >= motion.Duration) _iconScales.Remove(tile);
        }
    }

    private void ClearItemSelection()
    {
        if (!_selection.Active) return;
        _selection.Clear();
        RefreshSelectionMarks();
    }

    private void ClickItem(ShortcutTile tile, bool control, bool shift)
    {
        if (tile.IsEmpty) return;
        if (_selection.Active || control || shift)
        {
            _selection.Click(CurrentItems.Select(item => item.Id).ToArray(), tile.Id, range: shift, additive: control);
            RefreshSelectionMarks(animateSelection: true);
        }
        else if (!shift) LaunchTile(tile);
    }

    private ShortcutTile[] CurrentItems => _openFolder == null ? _tiles.Where(t => !t.IsEmpty).ToArray() : _members;

    private bool HandleDeleteSelection(KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete || _editingName || GetForegroundWindow() != _hwnd ||
            RootGrid.XamlRoot is not { } root) return false;
        var grid = _openFolder == null ? ShortcutGrid : _memberGrid;
        if (grid == null) return false;
        for (var node = FocusManager.GetFocusedElement(root) as DependencyObject; node != null;
            node = VisualTreeHelper.GetParent(node))
        {
            if (node is TextBox or PasswordBox or RichEditBox) return false;
            if (!ReferenceEquals(node, grid)) continue;
            if (e.KeyStatus.WasKeyDown || EditingDataBlocked || _morphEnding || _folderReturning || _displayRefreshPending) return true;
            var selected = CurrentItems.Where(tile => _selection.Contains(tile.Id)).ToArray();
            if (selected.Length == 0) return false;
            _ = DeleteSelectedItems(_drawerId, _openFolder, selected.Select(tile => tile.Id).ToArray(),
                selected.Count(tile => tile.IsGroup));
            return true;
        }
        return false;
    }

    private async Task DeleteSelectedItems(Guid drawer, Guid? group, Guid[] ids, int folders)
    {
        if (_deletingSelection || _closed || _importing || _internalDrag || _displayRefreshPending ||
            ids.Length == 0 || drawer != _drawerId || group != _openFolder) return;
        _deletingSelection = true;
        try
        {
            if (folders > 0 && !await ConfirmSelectionAction(ids.Length, folders)) return;
            if (_closed || _displayRefreshPending || drawer != _drawerId || group != _openFolder) return;
            var grid = _memberGrid ?? ShortcutGrid;
            var removed = new List<(Guid Id, FrameworkElement Visual, IconPose Pose)>();
            Windows.Foundation.Rect viewport = default;
            try
            {
                var before = CaptureHistoryFrame(grid, ids.ToHashSet());
                if (before.Count > 0) viewport = HistoryViewport(grid);
                foreach (var (id, frame) in before)
                    removed.Add((id, DeleteFadeVisual(grid, frame.Tile, frame.Pose), frame.Pose));
            }
            catch (Exception ex)
            {
                removed.Clear();
                PrototypeLog.Write("delete animation capture: " + ex.Message);
            }
            using (BeginDataHistory()) _store = _stores[drawer] = _preferences.DeleteItems(drawer, _store, ids);
            ClearItemSelection();
            RefreshTiles();
            if (removed.Count > 0 && _visible && ReferenceEquals(grid, _memberGrid ?? ShortcutGrid))
            {
                try
                {
                    ClearHistoryFades();
                    _historyFadeGrid = grid;
                    _historyFadeStart = _clock.Elapsed.TotalMilliseconds;
                    AttachItemFadeOut(viewport, removed);
                }
                catch (Exception ex) { ClearHistoryFades(); PrototypeLog.Write("delete animation: " + ex.Message); }
            }
            NotifyCleanupIfNeeded();
        }
        catch (Exception ex) { if (!_closed) ErrorStatus(UiText.Get("DeleteFailed", ex.Message)); }
        finally { _deletingSelection = false; }
    }

    private FrameworkElement DeleteFadeVisual(GridView grid, ShortcutTile tile, IconPose pose)
    {
        var icon = IconVisual(tile, pose.Size);
        if (!tile.ShowNames || grid.ContainerFromItem(tile) is not FrameworkElement container ||
            FindVisual<TextBlock>(container, label => label.Name == "ShortcutNameLabel") is not { ActualWidth: > 0 } label)
            return icon;
        var position = label.TransformToVisual(DragLayer).TransformPoint(new Windows.Foundation.Point());
        var sprite = new Canvas { Width = pose.Size, Height = pose.Size, IsHitTestVisible = false };
        var text = new TextBlock
        {
            Text = label.Text, Width = label.ActualWidth, FontSize = label.FontSize,
            Foreground = label.Foreground, FontFamily = label.FontFamily, TextAlignment = label.TextAlignment,
            TextTrimming = label.TextTrimming, MaxLines = 1, Opacity = label.Opacity
        };
        Canvas.SetLeft(text, position.X - pose.X);
        Canvas.SetTop(text, position.Y - pose.Y);
        sprite.Children.Add(icon);
        sprite.Children.Add(text);
        return sprite;
    }

    private async Task CreateSelectedFolder(Guid drawer, ShortcutTile[] tiles)
    {
        if (_formingFolder || _morphCommitted || _externalDragging || _importing) return;
        var ids = tiles.Select(tile => tile.Id).ToArray();
        int first = _store.TopSlots.IndexOf(ids[0]);
        if (first < 0) return;
        int previousPage = _topPage;
        _formingFolder = true;
        using var hold = HoldOpen();
        DropSurface.IsHitTestVisible = false;
        try
        {
            GoToPage(first / PageCapacity);
            double deadline = _clock.Elapsed.TotalMilliseconds + DrawerSettings.PageSlideMs + 750;
            while (!_closed && _pageNeedsSnap && _clock.Elapsed.TotalMilliseconds < deadline) await Task.Delay(16);
            if (_closed || _drawerId != drawer) return;
            if (_pageNeedsSnap) throw new InvalidOperationException(UiText.Get("PageNotReady"));
            try { PrepareSelectedFolderVisual(tiles); }
            catch (Exception ex)
            {
                RemoveMergeVisual();
                PrototypeLog.Write("selected folder animation setup: " + ex.Message);
            }
            Guid folder;
            using (BeginDataHistory()) folder = _store.CreateFolder(ids);
            if (_mergeVisual != null)
            {
                try { StartSelectedFolderAnimation(); CommitMergeVisual(folder); }
                catch (Exception ex)
                {
                    RemoveMergeVisual();
                    PrototypeLog.Write("selected folder animation: " + ex.Message);
                }
            }
            ClearItemSelection();
            RefreshTiles();
        }
        catch
        {
            if (!_morphCommitted && !_closed)
            {
                RemoveMergeVisual();
                GoToPage(previousPage);
            }
            throw;
        }
        finally
        {
            _formingFolder = false;
            if (!_closed) DropSurface.IsHitTestVisible = true;
        }
    }

    private async Task<bool> ConfirmSelectionAction(int items, int folders, bool combine = false)
    {
        if (_confirmationWindow != null) return false;
        using var hold = HoldOpen();
        DropSurface.IsHitTestVisible = false;
        try
        {
            _confirmationWindow = new RemovalConfirmation(_hwnd, items, folders, _geometry.Work, _presentationScale, combine);
            _confirmationWindow.Activate();
            return await _confirmationWindow.Result;
        }
        finally
        {
            _confirmationWindow = null;
            if (!_closed)
            {
                DropSurface.IsHitTestVisible = true;
                if (_visible) SetForegroundWindow(_hwnd);
            }
            KeepEscapeOnClosedSurface();
        }
    }
}
