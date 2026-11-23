using System;
using System.Linq;
using Drawer.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Point = Windows.Foundation.Point;
using Windows.System;
using static Drawer.NativeMethods;

namespace Drawer;
public sealed partial class MainWindow
{
    private GridView? _dragGrid;
    private bool _pressMoved, _committingDrag;
    private readonly TopDropHover _topDropHover = new();
    private ShortcutTile? _pressedTile;
    private Pointer? _sortPointer;
    private Point _pressPoint, _dragPoint;
    private double _pressedAt;
    private double? _itemButtonUpSince;
    private DragPreview? _preview;
    private Guid[]? _appliedDragSlots;
    private bool _pressControl, _pressShift, _batchContainsFolder;
    private DropTarget? _itemDrop;
    private ShortcutTile[] _dragBatchTiles = Array.Empty<ShortcutTile>();
    private Guid[] _dragBatch = Array.Empty<Guid>();
    private int[] _dragSourceCells = Array.Empty<int>();
    private int[]? _dragMemberIndices;
    private ItemSelection.Snapshot? _selectionBeforeDrag;
    private readonly FolderDragEntry _folderDragEntry = new();
    private Guid _itemSourceDrawer;
    private Guid? _itemSourceFolder;
    private int _itemSourcePage, _itemSourceCapacity;
    private double _itemSourceFolderScroll;
    private readonly DrawerDwell _itemDrawerDwell = new();
    private Guid? _itemSwitchTarget;
    private int _itemScreenRevision;
    private nint _forbiddenDropCursor, _normalDropCursor;
    private bool _dropCursorOverridden;
    private IDisposable? _itemDragHold;
    private bool IsBatchDrag => _dragBatch.Length > 1;
    private bool InMemberGrid => _dragGrid != null && ReferenceEquals(_dragGrid, _memberGrid);
    private ShortcutTile[] DragTiles => InMemberGrid ? _members : _tiles.ToArray();
    private LayoutMetrics DragMetrics => InMemberGrid ? MemberMetrics() : _metrics;

    private void Sort_Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (_previewOnly || _resize != null || ResizeEdgeAtPointer(e) != ResizeEdge.None || _historyRestoring || _editingName || _folderAnimating || NavigationPressed || NavigationTransitionActive ||
            _morphEnding || _morphCommitted || _formingFolder || _externalDragging || _importing || _pickerOpen || _settingsOpen || !_storeReady ||
            _pressedTile != null || _boxSelection != null || _readingClipboard || _previewSettling || _menuOpen || _confirmationWindow != null ||
            _clock.Elapsed.TotalMilliseconds < _suppressLaunchUntil) return;
        var grid = (GridView)sender;
        if (_openFolder != null && !ReferenceEquals(grid, _memberGrid)) return;
        if (ReferenceEquals(grid, ShortcutGrid) && (PageTurning || !_pageReady || !TopSlotsArranged)) return;
        var point = e.GetCurrentPoint(grid);
        if (!point.Properties.IsLeftButtonPressed || IsScrollControl(e.OriginalSource)) return;
        var nameLabel = e.KeyModifiers == VirtualKeyModifiers.None ? HitTileName(grid, point.Position) : null;
        if (nameLabel == null) _lastNameClick = null;
        var tile = nameLabel == null ? HitIcon(grid, point.Position) : null;
        if (tile == null)
        {
            BeginSelectionBox(grid, e, nameLabel);
            return;
        }
        if (!RootGrid.CapturePointer(e.Pointer)) return;
        _dragGrid = grid; _pressedTile = tile; _sortPointer = e.Pointer;
        _itemDragHold = HoldOpen();
        FocusContent(grid);
        _pressPoint = _dragPoint = point.Position;
        _pressedAt = _clock.Elapsed.TotalMilliseconds; _pressMoved = false;
        _pressControl = (e.KeyModifiers & VirtualKeyModifiers.Control) != 0 || (GetAsyncKeyState(0x11) & 0x8000) != 0;
        _pressShift = (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0 || (GetAsyncKeyState(0x10) & 0x8000) != 0;
        e.Handled = true;
    }

    private double GridOffset(GridView grid) => ReferenceEquals(grid, ShortcutGrid) ? _topPage * PageHeight
        : FindVisual<ScrollViewer>(grid)?.VerticalOffset ?? 0;

    private static bool IsScrollControl(object source)
    {
        for (var node = source as DependencyObject; node != null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is Microsoft.UI.Xaml.Controls.Primitives.ScrollBar) return true;
            if (node is GridView) break;
        }
        return false;
    }

    private void TryStartDrag(Point point)
    {
        if (_pressedTile == null || _dragGrid == null || _internalDrag) return;
        _dragPoint = point;
        double dx = point.X - _pressPoint.X, dy = point.Y - _pressPoint.Y;
        _pressMoved |= dx * dx + dy * dy >= DrawerSettings.DragThreshold * DrawerSettings.DragThreshold;
        if (!_pressMoved || _clock.Elapsed.TotalMilliseconds - _pressedAt < DrawerSettings.DragHoldMs || !DropSurfaceSettled) return;
        _itemSourceDrawer = _drawerId; _itemSourceFolder = _openFolder;
        _selectionBeforeDrag = _selection.Capture();
        var moving = _selection.Contains(_pressedTile.Id)
            ? DragTiles.Where(t => _selection.Contains(t.Id)).ToArray() : new[] { _pressedTile };
        _dragBatchTiles = moving;
        _dragBatch = moving.Select(t => t.Id).ToArray();
        _dragSourceCells = _itemSourceFolder == null
            ? moving.Select(t => _store.TopSlots.IndexOf(t.Id)).ToArray() : Array.Empty<int>();
        _dragMemberIndices = _itemSourceFolder == null ? null : moving.Select(t => Array.IndexOf(_members, t)).ToArray();
        _batchContainsFolder = moving.Any(t => t.IsGroup);
        double leadSize = _pressedTile.RenderedPictureSize;
        ClearItemSelection();
        foreach (var tile in moving) { _iconScales.Remove(tile); tile.SetIconScale(1); }
        _itemSourcePage = _topPage;
        _itemSourceCapacity = PageCapacity;
        _itemSourceFolderScroll = _memberGrid == null ? 0 : FindVisual<ScrollViewer>(_memberGrid)?.VerticalOffset ?? 0;
        _itemScreenRevision = _screenRevision;
        _internalDrag = true;
        _itemDragTrace = ItemDragTrace.Enabled ? new(_dragBatch.Length) : null;
        _pageWheel.Reset();
        _suppressLaunchUntil = double.PositiveInfinity;
        try
        {
            SetDragGrid(_dragGrid);
            BeginDragVisual(leadSize);
        }
        catch (Exception ex)
        {
            CancelItemDrag();
            ErrorStatus(UiText.Get("DragStartFailed", ex.Message));
            return;
        }
        PrototypeLog.Write($"drag: started {_pressedTile.Id} held={_clock.Elapsed.TotalMilliseconds - _pressedAt:F0}ms");
    }

    private void SetDragGrid(GridView grid)
    {
        if (_dragGrid != null && !ReferenceEquals(_dragGrid, grid))
        {
            _dragGrid.Footer = null;
            SettleVisuals();
        }
        _dragGrid = grid;
        _pageEdgeDwell.Reset();
        var slots = InMemberGrid ? _members.Select(t => t.Id).ToArray() : _store.TopSlots.ToArray();
        _preview = new(slots, _dragBatch, fixedCells: !InMemberGrid,
            sourceCells: _drawerId == _itemSourceDrawer ? _dragSourceCells : Array.Empty<int>(),
            insertIntoDrawer: _drawerId != _itemSourceDrawer);
        _appliedDragSlots = null;
        if (!InMemberGrid) _folderDragEntry.Reset();
        ResetDropPreview();
    }

    private void ApplyDragSlots()
    {
        if (_dragGrid == null || _preview == null) return;
        var slots = _preview.VisualSlots;
        if (ReferenceEquals(_appliedDragSlots, slots)) return;
        _appliedDragSlots = slots;
        SetPreviewTargets(_dragGrid, slots);
        if (InMemberGrid) SetMemberPreviewExtent(slots.Length);
    }

    private void Sort_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (MoveSelectionBox(e)) { e.Handled = true; return; }
        if (_committingDrag) return;
        if (_pressedTile == null || _dragGrid == null || _sortPointer?.PointerId != e.Pointer.PointerId) return;
        if (!_internalDrag) TryStartDrag(e.GetCurrentPoint(_dragGrid).Position);
        if (_internalDrag)
        {
            if (_itemDragTrace is { } trace) trace.PointerEvents++;
            // The lead icon follows input immediately. The timer alone updates targets and the trail.
            PositionGhost(e.GetCurrentPoint(DragLayer).Position);
        }
        e.Handled = true;
    }

    private async void Sort_Released(object sender, PointerRoutedEventArgs e)
    {
        if (_committingDrag) return;
        if (_boxSelection is { } box && box.Pointer.PointerId == e.Pointer.PointerId && !e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed)
        {
            MoveSelectionBox(e);
            bool nameClick = _selectionFrame == null && box.NameLabel != null;
            var namePoint = nameClick ? e.GetCurrentPoint(box.Grid).Position : default;
            EndSelectionBox(cancel: false);
            if (nameClick) CompleteTileNameClick(box, namePoint);
            e.Handled = true;
            return;
        }
        if (_pressedTile == null || _dragGrid == null || _sortPointer?.PointerId != e.Pointer.PointerId) return;
        var tile = _pressedTile;
        var grid = _dragGrid;
        var released = e.GetCurrentPoint(grid);
        if (released.Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        var point = released.Position;
        bool committed = false;
        try
        {
            if (_internalDrag)
            {
                ObserveDisplay();
                if (_displayRefreshPending || _itemScreenRevision != _screenRevision) return;
                if (_itemSwitchTarget != null || !DropSurfaceSettled || _folderDragEntry.Waiting) return;
                // A queued release may be processed after the hardware
                // pointer has already moved away from the drop location.
                var release = e.GetCurrentPoint((UIElement)Content).Position;
                double scale = RootGrid.XamlRoot?.RasterizationScale ?? _presentationScale;
                var cursor = new NativeMethods.Point { X = (int)Math.Round(release.X * scale), Y = (int)Math.Round(release.Y * scale) };
                if (!ClientToScreen(_hwnd, ref cursor)) return;
                if (!_geometry.Visible(_edge, _interaction.Position).Contains(cursor.X, cursor.Y) ||
                    !DragPoint(cursor, grid, out var dropPoint) || InMemberGrid && !IsInMemberPanel(cursor) ||
                    !InMemberGrid && (DragPageBand(cursor) is -1 or 1)) return;
                double now = _clock.Elapsed.TotalMilliseconds;
                var target = ResolveItemDrop(dropPoint.X, dropPoint.Y, now, publish: false,
                    out _, out var receiver, out _, out _);
                if (target == null || InMemberGrid && _itemDrop?.MatchesMemberSurface(target) != true)
                {
                    PrototypeLog.Write($"drag release rejected: shown={_itemDrop} release={target}");
                    return;
                }
                // A scroll may advance between the last painted preview and
                // PointerReleased. Commit the shown anchor after checking
                // that the point is still on the same valid member surface.
                if (InMemberGrid) target = _itemDrop!;
                if (target.TopLayout != null && !ValidTopDrop(target)) return;
                PositionGhost(grid.TransformToVisual(DragLayer).TransformPoint(dropPoint));
                UpdateFloatingGhost(cursor, outside: false);
                UpdateDragTrail(cursor, 0, outside: false);
                PrototypeLog.Write($"drag target: release shown={_itemDrop?.Kind} action={target.Kind} cell={target.Index} " +
                    $"receiver={target.Receiver} x={dropPoint.X:F1} y={dropPoint.Y:F1}");
                if (target.MemberPlacement is { } member)
                    PrototypeLog.Write($"folder drop: index={member.Index} previous={member.Before} next={member.After}");
                if (receiver != null) ShowMergePreview(receiver, now);
                var drop = target.Placement with { ExpectedSourceCells = _dragSourceCells,
                    ExpectedSourceFolder = _itemSourceFolder, ExpectedMemberIndices = _dragMemberIndices,
                    SourcePageCapacity = _itemSourceCapacity, Deduplicate = target.Kind == DropKind.Append };
                _committingDrag = true;
                var source = _stores[_itemSourceDrawer];
                var sourceId = _itemSourceDrawer;
                var destination = _store;
                var batch = _dragBatch;
                ItemMoveResult result;
                using (BeginDataHistory())
                    result = await System.Threading.Tasks.Task.Run(() => _preferences.MoveItems(sourceId, source,
                        target.Drawer, destination, batch, drop));
                if (_closed) return;
                ReportTransferResult(true, result.Moved, result.Duplicates, result.Failed);
                if (result.Moved == 0) return;
                committed = true;
                PresentItemMove(target, result);
                PrototypeLog.Write($"drag: committed {tile.Id} drawer={target.Drawer} page={target.Page + 1} index={target.Index} action={target.Kind} receiver={target.Receiver}");
            }
            else if (!_pressMoved && _clock.Elapsed.TotalMilliseconds >= _suppressLaunchUntil)
            {
                if (ReferenceEquals(HitIcon(grid, point), tile))
                    ClickItem(tile, _pressControl, _pressShift);
            }
        }
        catch (Exception ex)
        {
            PrototypeLog.Write("move: " + ex);
            if (_exitRequested) _exitCommitFailed = true;
            if (!_closed) ShowOperationFeedback(UiText.Get("MoveOperationFailed"));
        }
        finally
        {
            _committingDrag = false;
            if (!_closed)
            {
                if (_internalDrag && !committed) CancelItemDrag();
                else EndPointerSort(landed: committed);
            }
        }
    }

    private void PresentItemMove(DropTarget target, ItemMoveResult result)
    {
        _stores[_itemSourceDrawer] = result.Source;
        _store = _stores[target.Drawer] = result.Target;
        _temporaryPage = false;
        bool remap = result.ReclaimedPages.Length > 0;
        bool local = _itemSourceDrawer == target.Drawer;
        if (remap)
        {
            int sourcePage = TopGridSlots.RemapPage(_itemSourcePage, result.ReclaimedPages);
            int sourcePages = TopGridSlots.PageCount(result.Source.TopSlots.Count, _itemSourceCapacity);
            _pageAnchors[_itemSourceDrawer] = Math.Clamp(sourcePage, 0, sourcePages - 1) * _itemSourceCapacity;
            if (local)
            {
                GoToPage(TopGridSlots.RemapPage(target.Page, result.ReclaimedPages), animate: false);
                _slotMotions.Clear();
            }
            PrototypeLog.Write($"drag: reclaimed source pages {string.Join(",", result.ReclaimedPages.Select(p => p + 1))}");
        }
        if ((result.Folder ?? (target.Kind == DropKind.Append ? target.Receiver : null)) is Guid folder) CommitMergeVisual(folder);
        RefreshTiles();
        if (remap && local) TickTopPage(_clock.Elapsed.TotalMilliseconds);
        RememberTopPage();
    }

    private void CancelItemDrag(bool restore = true)
    {
        bool dragging = _internalDrag;
        if (!dragging && _pressedTile != null)
            _suppressLaunchUntil = _clock.Elapsed.TotalMilliseconds + 500;
        var selected = _selectionBeforeDrag;
        bool sameView = restore && !_closed && _drawerId == _itemSourceDrawer && _topPage == _itemSourcePage &&
            _openFolder == _itemSourceFolder && DropSurfaceSettled;
        EndPointerSort(animateCancel: sameView);
        if (!dragging || !restore || _closed) return;
        bool switched = _drawerId != _itemSourceDrawer;
        if (switched) ShowItemDrawer(_itemSourceDrawer);
        GoToPage(_itemSourcePage, animate: false);
        TickTopPage(_clock.Elapsed.TotalMilliseconds);
        if (switched || _openFolder != _itemSourceFolder || _folderReturning)
        {
            if (_morphEnding) RemoveMergeVisual();
            CloseFolder();
            ShortcutGrid.UpdateLayout();
            if (_itemSourceFolder is Guid folder && _tiles.FirstOrDefault(t => t.Id == folder) is { } tile)
            {
                OpenFolder(tile);
                if (_memberGrid != null)
                {
                    _memberGrid.UpdateLayout();
                    FindVisual<ScrollViewer>(_memberGrid)?.ChangeView(null, _itemSourceFolderScroll, null, true);
                }
            }
        }
        if (selected is { } snapshot) _selection.Restore(snapshot);
        RefreshSelectionMarks();
        PrototypeLog.Write("drag: canceled, original ownership retained");
    }

    private void EndPointerSort(bool landed = false, bool animateCancel = false)
    {
        if (_pressedTile == null && !_internalDrag) return;
        var pointer = _sortPointer;
        ReleaseHold(ref _itemDragHold);
        if (_internalDrag)
        {
            if (animateCancel && !_morphCommitted) CancelMergeVisual();
            if (landed || animateCancel) LandDraggedTiles(landed);
            _suppressLaunchUntil = _clock.Elapsed.TotalMilliseconds + 500;
        }
        ClearDragTrail();
        if (_dragGrid != null) _dragGrid.Footer = null;
        _pressedTile = null; _sortPointer = null; _dragGrid = null;
        _preview = null;
        _appliedDragSlots = null;
        _dragBatchTiles = Array.Empty<ShortcutTile>(); _dragBatch = Array.Empty<Guid>(); _selectionBeforeDrag = null;
        _dragSourceCells = Array.Empty<int>(); _dragMemberIndices = null;
        _folderDragEntry.Reset();
        _pageEdgeDwell.Reset();
        ClearHoverTargets();
        _itemDrop = null;
        _itemSwitchTarget = null; _itemDrawerDwell.Clear(); _itemButtonUpSince = null;
        DisposeFloatingGhost();
        RestoreItemDropCursor();
        _pageWheel.Reset();
        SetTemporaryPage(false);
        SyncPageModel();
        _internalDrag = false;
        FinishItemDragTrace(landed);
        if (!_morphCommitted && !_morphEnding) ClearDragVisual();
        else { if (_dragGhost != null) DragLayer.Children.Remove(_dragGhost); _dragGhost = null; }
        SettleVisuals();
        _layoutDirty = true;
        if (pointer != null) RootGrid.ReleasePointerCapture(pointer);
    }

    private void UpdateItemDropCursor()
    {
        if (!_internalDrag) return;
        if (_itemDrop != null) { RestoreItemDropCursor(); return; }
        if (_forbiddenDropCursor == 0) _forbiddenDropCursor = LoadCursorW(0, (nint)32648); // IDC_NO
        if (_forbiddenDropCursor == 0) return;
        SetCursor(_forbiddenDropCursor);
        _dropCursorOverridden = true;
    }

    private void RestoreItemDropCursor()
    {
        if (!_dropCursorOverridden) return;
        _dropCursorOverridden = false;
        if (_normalDropCursor == 0) _normalDropCursor = LoadCursorW(0, (nint)32512); // IDC_ARROW
        if (_normalDropCursor != 0) SetCursor(_normalDropCursor);
    }

    private void Sort_Canceled(object sender, PointerRoutedEventArgs e)
    {
        if (_boxSelection?.Pointer.PointerId == e.Pointer.PointerId) { EndSelectionBox(cancel: true); return; }
        if (!_committingDrag && _sortPointer?.PointerId == e.Pointer.PointerId) CancelItemDrag();
    }

    private void Sort_CaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, RootGrid)) Sort_Canceled(sender, e);
    }

    private void ResetDropPreview()
    {
        ClearHoverTargets();
        _itemDrop = null;
        UpdateItemDropCursor();
        if (!_morphCommitted) RemoveMergeVisual();
        _preview?.ClearLanding();
        ApplyDragSlots();
    }

    private void ClearHoverTargets()
    {
        if (_itemDragTrace is { } trace && _topDropHover.Candidate is { } cell)
            trace.Record($"leave page={cell.Page + 1} cell={cell.Index} " +
                $"phase={_topDropHover.Action} held={_topDropHover.Elapsed(_clock.Elapsed.TotalMilliseconds):F0}ms");
        _topDropHover.Reset();
    }

    private bool TryGetTopTile(int cell, out ShortcutTile? tile)
    {
        tile = null;
        if (cell < 0 || cell >= _tiles.Count) return false;
        Guid id = cell < _store.TopSlots.Count ? _store.TopSlots[cell] : Guid.Empty;
        var visible = _tiles[cell];
        if (id == Guid.Empty) return visible.IsEmpty;
        if (visible.IsEmpty || visible.Id != id) return false;
        tile = visible;
        return true;
    }

    private DropCell DescribeTopCell(int index, ShortcutTile tile, LayoutMetrics metrics)
    {
        var origin = Slot(index % PageCapacity, metrics);
        return new(_drawerId, _topPage, index, tile.Id, tile.IsGroup, origin.X, origin.Y, metrics);
    }

    private DropTarget? ResolveItemDrop(double x, double y, double now, bool publish,
        out int gap, out ShortcutTile? receiver, out bool openFolder, out bool previewSort)
    {
        gap = -1;
        receiver = null;
        openFolder = false;
        previewSort = true;
        if (_preview == null || _dragGrid == null || _pressedTile == null) return null;
        if (InMemberGrid)
            return _batchContainsFolder ? null : CaptureMemberDrop(new(x, y), DropKind.Members, _dragBatch, _itemDrop);

        var metrics = DragMetrics;
        gap = TopCellAt(x, y);

        if (!TryGetTopTile(gap, out var occupant)) { gap = -1; return null; }
        if (occupant != null && _dragBatch.Contains(occupant.Id) && !occupant.IsGroup) occupant = null;
        if (occupant == null)
        {
            if (publish) ClearHoverTargets();
        }
        else
        {
            var cell = DescribeTopCell(gap, occupant, metrics);
            var before = _topDropHover.Candidate;
            var previous = _topDropHover.Action;
            double held = before == null ? 0 : _topDropHover.Elapsed(now);
            var action = _topDropHover.Evaluate(cell, x, y, now, publish);
            if (publish && _itemDragTrace is { } trace && (before != cell || previous != action))
                trace.Record($"page={cell.Page + 1} cell={cell.Index} " +
                    $"point={x:F1},{y:F1} layout={cell.Left:F1},{cell.Top:F1} {metrics.CellWidth:F1}x{metrics.CellHeight:F1} " +
                    $"from={before?.Index}/{previous}/{held:F0}ms to={action} held={_topDropHover.Elapsed(now):F0}ms");
            if (action is TopDropAction.EdgePending or TopDropAction.EdgeReady)
                previewSort = action == TopDropAction.EdgeReady;
            else
            {
                if (occupant.Group is { } folder)
                {
                    if (_drawerId == _itemSourceDrawer && _dragBatch.Length == 1 && _dragBatch[0] == folder.Id) return null;
                    receiver = occupant;
                    openFolder = publish && !_batchContainsFolder && action == TopDropAction.OpenFolder;
                    var ids = _batchContainsFolder ? _stores[_itemSourceDrawer].ExpandClipboardItems(
                        _dragBatch.Where(id => _itemSourceDrawer != _drawerId || id != folder.Id).ToArray()) : _dragBatch;
                    var sequence = MemberSequence(folder, ids, _itemDrop);
                    var landing = new FolderInsertion(sequence, sequence.Candidates.Count);
                    return TopDrop(DropKind.Append, gap, folder.Id, landing);
                }
                if (_batchContainsFolder) return null;
                if (publish || action == TopDropAction.MergeReady)
                {
                    receiver = occupant;
                    return TopDrop(action == TopDropAction.MergeReady ? DropKind.Merge : DropKind.MergePending, gap, cell.Item);
                }
                // An early center release uses this cell's ordinary placement.
            }
        }

        var placement = _drawerId != _itemSourceDrawer ? DropKind.Insert : _itemSourceFolder != null ? DropKind.Extract :
            occupant == null ? DropKind.Place : DropKind.Swap;
        return TopDrop(placement, gap, occupant?.Id);
    }

    private bool UpdateSortTarget(double x, double y, double now)
    {
        var target = ResolveItemDrop(x, y, now, publish: true, out int gap, out var receiver, out bool open, out bool previewSort);
        _itemDrop = target;
        UpdateItemDropCursor();
        if (target == null)
        {
            if (gap < 0) ClearHoverTargets();
            ShowMergePreview(null, now);
            _preview?.ClearLanding();
            ApplyDragSlots();
            return false;
        }
        ShowMergePreview(receiver, now);
        if (receiver != null)
        {
            _preview?.Compact();
            ApplyDragSlots();
            if (_dragBatch.Contains(receiver.Id))
            {
                var slot = Slot(gap < 0 ? _store.TopSlots.IndexOf(receiver.Id) : gap, _metrics);
                var motion = Motion(false, receiver.Id, _store.TopSlots.IndexOf(receiver.Id), _metrics);
                motion.TargetX = slot.X; motion.TargetY = slot.Y;
            }
            if (open && GetCursorPos(out var cursor))
            {
                var background = _preview?.Order;
                OpenFolder(receiver);
                if (_memberGrid != null)
                {
                    SetDragGrid(_memberGrid);
                    _folderDragEntry.WaitAt(cursor.X, cursor.Y);
                    if (background != null) SetPreviewTargets(ShortcutGrid, background);
                }
            }
            return true;
        }
        if (target.Kind == DropKind.Members) _preview?.PreviewMembers(target.MemberPlacement!);
        else if (previewSort) _preview?.PreviewInsertion(gap);
        else _preview?.ClearLanding();
        ApplyDragSlots();
        return false;
    }

    private void Sort_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || e.KeyStatus.WasKeyDown || _boxSelection != null || _readingClipboard || _internalDrag || PageTurning || _editingName || _folderAnimating ||
            _settingsOpen || _pickerOpen || _importing || _externalDragging || _formingFolder || _morphCommitted || _clock.Elapsed.TotalMilliseconds < _suppressLaunchUntil ||
            _openFolder != null && !ReferenceEquals(sender, _memberGrid)) return;
        var tile = (sender as GridView)?.SelectedItem as ShortcutTile;
        for (var node = e.OriginalSource as DependencyObject; node != null && !ReferenceEquals(node, sender);
             node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is not GridViewItem { Content: ShortcutTile focused }) continue;
            tile = focused;
            break;
        }
        if (tile == null) return;
        static bool Held(VirtualKey key) =>
            (InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        ClickItem(tile, Held(VirtualKey.Control), Held(VirtualKey.Shift));
        e.Handled = true;
    }
}
