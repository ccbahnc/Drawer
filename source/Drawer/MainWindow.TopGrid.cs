using System;
using System.Collections.Generic;
using Drawer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using static Drawer.NativeMethods;

namespace Drawer;

public sealed partial class MainWindow
{
    private readonly PageWheel _pageWheel = new();
    private readonly PageEdgeDwell _pageEdgeDwell = new();
    private readonly Dictionary<Guid, int> _pageAnchors = new();
    private Guid _pageDrawer;
    private int _pageCapacity, _topPage;
    private bool _temporaryPage, _pageNeedsSnap, _pageReady;
    private double? _pageSlideAt;
    private readonly PageSnapDeadline _pageSnapDeadline = new();
    private double _pageSlideFrom, _lastPageHeight;
    private ScrollViewer? _topScrollViewer;
    private bool PageTurning => _pageSlideAt != null;
    private int PageCapacity => _metrics.Rows * _metrics.Columns;
    private double PageHeight => _metrics.Rows * _metrics.CellHeight;
    private int FormalPages => TopGridSlots.PageCount(_store.TopSlots.Count, PageCapacity);
    private int RenderedPages => FormalPages + (_temporaryPage ? 1 : 0);

    private int TopCellAt(double x, double y)
    {
        if (!TopSlotsArranged || y >= TopContentHeight || y >= ShortcutGrid.ActualHeight) return -1;
        int cell = _metrics.PageCellAt(x, y);
        return cell < 0 ? -1 : _topPage * PageCapacity + cell;
    }

    private DropTarget TopDrop(DropKind kind, int cell, Guid? receiver = null, FolderInsertion? member = null) =>
        new(_drawerId, _topPage, kind, member?.Index ?? cell, receiver, member)
        { TopLayout = (_metrics.Rows, _metrics.Columns, _screenRevision, cell) };

    private bool ValidTopDrop(DropTarget target) =>
        target.TopLayout is { } layout && !_displayRefreshPending &&
        layout.Rows == _metrics.Rows && layout.Columns == _metrics.Columns && layout.DisplayRevision == _screenRevision &&
        target.Drawer == _drawerId && target.Page >= 0 && target.Page < RenderedPages &&
        layout.Cell >= 0 && layout.Cell / PageCapacity == target.Page && layout.Cell < _tiles.Count &&
        (target.MemberPlacement == null ? target.Index == layout.Cell :
            layout.Cell < _store.TopSlots.Count && _store.TopSlots[layout.Cell] == target.Receiver);

    private void SyncPageModel(bool updateIndicator = true)
    {
        if (_pageDrawer != _drawerId || _pageCapacity != PageCapacity)
        {
            _pageDrawer = _drawerId;
            _pageCapacity = PageCapacity;
            _topPage = Math.Min(FormalPages - 1, PageAnchor(_drawerId) / PageCapacity);
            _temporaryPage = false;
            _pageEdgeDwell.Reset();
            _pageWheel.Reset();
            SnapTopPage();
        }
        else if (_topPage >= RenderedPages)
        {
            _topPage = RenderedPages - 1;
            if (!_internalDrag && !_externalDragging) RememberTopPage();
            SnapTopPage();
        }
        if (_lastPageHeight != PageHeight) { _lastPageHeight = PageHeight; SnapTopPage(); }
        if (updateIndicator) UpdatePageIndicator();
    }

    private void RememberTopPage()
    {
        if (!_previewOnly && _topPage < FormalPages)
            _pageAnchors[_drawerId] = _topPage * PageCapacity;
    }

    private int PageAnchor(Guid id) => _previewOwner != null
        ? _previewOwner._pageAnchors.GetValueOrDefault(id)
        : _pageAnchors.GetValueOrDefault(id);

    private void SnapTopPage()
    {
        _pageSlideAt = null;
        _pageSnapDeadline.Reset();
        _pageNeedsSnap = true;
        _pageReady = false;
    }

    private void SetTemporaryPage(bool show)
    {
        if (_temporaryPage == show) return;
        _temporaryPage = show;
        int count = RenderedPages * PageCapacity;
        while (_tiles.Count < count) _tiles.Add(new ShortcutTile());
        while (_tiles.Count > count) _tiles.RemoveAt(_tiles.Count - 1);
        UpdatePageIndicator();
    }

    private void GoToPage(int page, bool animate = true)
    {
        page = Math.Clamp(page, 0, RenderedPages - 1);
        if (animate && page == _topPage && !_pageNeedsSnap) return;
        if (page != _topPage) ClearHistoryFades();
        _topPage = page;
        if (!_internalDrag && !_externalDragging) RememberTopPage();
        if (animate && FindVisual<ScrollViewer>(ShortcutGrid) is { } scroll)
        {
            _pageSlideFrom = scroll.VerticalOffset;
            _pageSlideAt = _clock.Elapsed.TotalMilliseconds;
            _pageSnapDeadline.Reset();
            _pageNeedsSnap = true;
            _pageReady = false;
            _interaction.KeepOpen();
        }
        else SnapTopPage();
        UpdatePageIndicator();
    }

    private void TickTopPage(double now)
    {
        using var sample = IdlePerformance.Measure(IdlePerformance.Work.PageTick);
        if (_topScrollViewer == null && FindVisual<ScrollViewer>(ShortcutGrid) is { } viewer)
        {
            _topScrollViewer = viewer;
            viewer.ViewChanged += TopScrollChanged;
        }
        if (!_visible && !PageTurning) return;
        if (_topScrollViewer is not { ViewportHeight: > 0 } scroll)
        {
            _pageReady = false;
            if (_pageNeedsSnap && _pageSnapDeadline.Expired(now, _pageSlideAt)) AbortPageTransition();
            return;
        }
        if (!_pageReady && !_pageNeedsSnap) SnapTopPage();
        if (!_pageNeedsSnap) return;
        double target = _topPage * PageHeight;
        double amount = _pageSlideAt is double start ? Math.Clamp((now - start) / DrawerSettings.PageSlideMs, 0, 1) : 1;
        double offset = amount < 1 ? _pageSlideFrom + (target - _pageSlideFrom) * (1 - Math.Pow(1 - amount, 3)) : target;
        if (target > scroll.ScrollableHeight + 1) ShortcutGrid.UpdateLayout();
        if (Math.Abs(scroll.VerticalOffset - offset) > .25) scroll.ChangeView(null, offset, null, true);
        if (amount < 1) return;
        if (Math.Abs(scroll.VerticalOffset - target) > 1)
        {
            // A detached or remeasured viewport can reject ChangeView.
            // Recover to a page it can render rather than keeping the drawer
            // open forever with a permanently blocked drop surface.
            if (!_pageSnapDeadline.Expired(now, _pageSlideAt)) return;
            int reachable = (int)Math.Floor((scroll.ScrollableHeight + 1) / PageHeight);
            _topPage = Math.Clamp((int)Math.Round(scroll.VerticalOffset / PageHeight), 0, Math.Min(reachable, RenderedPages - 1));
            scroll.ChangeView(null, _topPage * PageHeight, null, true);
            if (!_internalDrag && !_externalDragging) RememberTopPage();
            UpdatePageIndicator();
            AbortPageTransition();
            return;
        }
        if (_pageSlideAt != null) PrototypeLog.Write($"page: settled {_topPage + 1}/{RenderedPages} offset={scroll.VerticalOffset:F1} temporary={_temporaryPage}");
        _pageSlideAt = null;
        _pageSnapDeadline.Reset();
        _pageNeedsSnap = false;
        _pageReady = true;
        if (_temporaryPage && _topPage < FormalPages) SetTemporaryPage(false);
    }

    private void AbortPageTransition()
    {
        _pageSlideAt = null;
        _pageSnapDeadline.Reset();
        _pageNeedsSnap = _pageReady = false;
        _pageWheel.Reset();
        _pageEdgeDwell.Reset();
        PrototypeLog.Write($"page: aborted unresolved viewport drawer={_drawerId} page={_topPage + 1}");
        if (_closed) return;
        if (_internalDrag) CancelItemDrag();
        else if (_externalDragging) EndExternalDrag(canceled: true);
        else DiscardExternalPage();
    }

    private void TopScrollChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // Keyboard focus can still request BringIntoView in GridView.
        if (!_pageNeedsSnap && sender is ScrollViewer viewer && Math.Abs(viewer.VerticalOffset - _topPage * PageHeight) > 1)
            SnapTopPage();
    }

    private void RouteWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_nameTile != null || _resize != null || _boxSelection != null || _historyRestoring || _committingDrag) { e.Handled = true; return; }
        var point = e.GetCurrentPoint(GridHost);
        if (point.Properties.IsHorizontalMouseWheel) return;
        if (_openFolder != null && e.Handled) return;
        if (_openFolder != null && !_folderReturning)
        {
            if (!_externalDragging && (!_internalDrag || !InMemberGrid)) return;
            // Pointer capture delivers the wheel directly to RootGrid.
            // The member ScrollViewer can't see that routed event.
            e.Handled = true;
            ScrollMemberWheel(point.Position, point.Properties.MouseWheelDelta);
            return;
        }
        if (point.Position.X < 0 || point.Position.X >= GridHost.ActualWidth || point.Position.Y < 0 ||
            point.Position.Y >= TopContentHeight) return;
        e.Handled = true;
        TurnTopWheel(point.Properties.MouseWheelDelta, "wheel");
    }

    private void ScrollMemberWheel(Windows.Foundation.Point point, int delta)
    {
        if (_folderLayout is not { } folder || !MemberPanelBounds(folder).Contains(point) || _memberGrid == null ||
            FindVisual<ScrollViewer>(_memberGrid) is not { } scroll) return;
        double offset = scroll.VerticalOffset - delta / 120d * MemberMetrics().CellHeight;
        scroll.ChangeView(null, Math.Clamp(offset, 0, scroll.ScrollableHeight), null, false);
    }

    private void TurnTopWheel(int delta, string input)
    {
        if (_previewOnly || _boxSelection != null || !_internalDrag && _pressedTile != null || _settingsOpen || _importing || _formingFolder || _morphCommitted || _navActive ||
            _internalDrag && (InMemberGrid || _itemSwitchTarget != null)) return;
        bool dragging = _internalDrag || _externalDragging;
        if (dragging && delta != 0) _pageEdgeDwell.Disarm();
        int step = _pageWheel.Step(delta, _clock.Elapsed.TotalMilliseconds, PageTurning || !_pageReady);
        if (step == 0) return;
        int next = _topPage + step;
        if (next < 0 || next >= RenderedPages) return;
        if (dragging) BeginDragPage(next, input);
        else GoToPage(next);
    }

    private void UpdatePageIndicator(bool languageOnly = false)
    {
        int count = RenderedPages;
        if (languageOnly && PageSegments.Children.Count != count) return;
        if (PageSegments.Children.Count != count)
        {
            PageSegments.Children.Clear();
            for (int index = 0; index < count; index++)
            {
                var segment = new Button { Tag = index, Width = 14, MinWidth = 0, MinHeight = 0,
                    Padding = new Thickness(0), Background = DrawerPalette.Brush("Transparent"), BorderThickness = new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Stretch,
                    Content = new Border { Width = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 2, 0, 2) } };
                segment.Click += PageSegment_Click;
                PageSegments.Children.Add(segment);
            }
        }
        double height = Math.Clamp(PageHeight / count, 9, 26);
        for (int index = 0; index < count; index++)
        {
            var segment = (Button)PageSegments.Children[index];
            if (!languageOnly)
            {
                segment.Height = height;
                ((Border)segment.Content).Background = DrawerPalette.Brush(index == _topPage ? "SelectionFill" : "Outline");
            }
            string label = index >= FormalPages ? UiText.Get("TemporaryPage", index + 1) : UiText.Get("PageOf", index + 1, count);
            ToolTipService.SetToolTip(segment, label);
            AutomationProperties.SetName(segment, label);
            AutomationProperties.SetItemStatus(segment, index == _topPage ? UiText.Get("CurrentPage") : "");
        }
        if (languageOnly) return;
        double railOffset = PageRail.VerticalOffset;
        double railHeight = Math.Max(9, PageRail.ViewportHeight);
        double top = _topPage * height;
        if (top < railOffset || top + height > railOffset + railHeight)
        {
            PageRail.UpdateLayout();
            PageRail.ChangeView(null, Math.Max(0, top - (railHeight - height) / 2), null, true);
        }
    }

    private void PageSegment_Click(object sender, RoutedEventArgs e)
    {
        if (_nameTile != null || _resize != null || _historyRestoring || _committingCut || _internalDrag || _externalDragging || _formingFolder || _morphCommitted || _pressedTile != null || _previewOnly || _openFolder != null || _settingsOpen ||
            _navActive || _importing || sender is not Button { Tag: int index } || index >= FormalPages) return;
        GoToPage(index);
    }

    private double TopContentHeight => Math.Min(GridHost.ActualHeight, PageHeight);

    private int? DragPageBand(NativeMethods.Point cursor)
    {
        if (!_visible || _interaction.Position != 1 || !GetWindowRect(_hwnd, out var window) ||
            cursor.X < window.Left || cursor.X >= window.Right || !DragPoint(cursor, ShortcutGrid, out var point)) return null;
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? _presentationScale;
        return PageEdgeDwell.BandAt((cursor.X - window.Left) / scale, point.Y, (window.Right - window.Left) / scale,
            point.Y + (window.Top - cursor.Y) / scale, point.Y + (window.Bottom - cursor.Y) / scale,
            PageHeight, _metrics.CellHeight);
    }

    private bool TurnPageWhileDragging(int? edge, double now)
    {
        if (!DropSurfaceSettled) return false;
        int step = _pageEdgeDwell.Step(edge, now);
        int next = _topPage + step;
        if (step == 0 || next < 0 || next > FormalPages || next == FormalPages && _temporaryPage) return false;
        if (next == FormalPages) SetTemporaryPage(true);
        BeginDragPage(next, step < 0 ? "upper edge" : "lower edge");
        return true;
    }

    private Guid? OtherDrawerTriggerAt(NativeMethods.Point cursor)
    {
        foreach (var (id, geometry) in _edgeGeometries)
            if (id != _drawerId && _stores.ContainsKey(id) && geometry.Trigger.Contains(cursor.X, cursor.Y))
                return id;
        return null;
    }

    private void BeginDragPage(int next, string input)
    {
        ClearExternalPreview();
        ResetDropPreview();
        GoToPage(next);
        PrototypeLog.Write($"drag: page {next + 1} via {input} temporary={_temporaryPage}");
    }

}
