using System;
using System.Collections.Generic;
using System.Linq;
using Drawer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using static Drawer.NativeMethods;

namespace Drawer;

public sealed partial class MainWindow
{
    private const string InternalDragKey = "Drawer.InternalReorder";
    private bool _internalDrag;
    private bool _settingsOpen;
    private double _suppressLaunchUntil;
    private LayoutMetrics _metrics = LayoutMetrics.Fit(new(), 1920, 1080);
    private double _availableWidth = 1920, _availableHeight = 1080;
    private bool ShowNavigation => _preferences.Value.Global.ShowNavigation;
    private double TopChrome => ContentTopRow.Height.Value + GridHost.Margin.Top;
    private SlotMotion? _navigationChrome;
    private IDisposable? _navigationTransitionHold;
    private bool NavigationTransitionActive => _navigationChrome != null;

    private void ApplyNavigationVisibility()
    {
        if (NavigationTransitionActive) return;
        NavigationBar.Visibility = ShowNavigation ? Visibility.Visible : Visibility.Collapsed;
        NavigationBar.IsHitTestVisible = ShowNavigation;
        NavigationBar.Opacity = 1;
        NavigationBar.Clip = null;
        SetNavigationChrome(ShowNavigation ? 1 : 0);
    }

    private void SetNavigationChrome(double visibility)
    {
        double height = AutomaticLayout.HiddenDragBand + (AutomaticLayout.HeaderHeight - AutomaticLayout.HiddenDragBand) * visibility;
        if (ContentTopRow.Height.Value != height) DrawerTopRow.Height = ContentTopRow.Height = new GridLength(height);
        double gap = AutomaticLayout.HeaderGap * visibility;
        if (GridHost.Margin.Top != gap) GridHost.Margin = new Thickness(0, gap, 0, 0);
    }

    private void BeginNavigationTransition(bool wasShown)
    {
        _navigationChrome = new(wasShown ? 1 : 0, 0) { TargetX = ShowNavigation ? 1 : 0 };
        _navigationTransitionHold = HoldOpen();
        NavigationBar.Visibility = Visibility.Visible;
        NavigationBar.IsHitTestVisible = false;
        if (_openFolder != null)
        {
            _backdropGeneration++;
            FolderBlurImages.Children.Clear();
            _backdropLevels = Array.Empty<Image>();
            _backdropStylePending = false;
            SetFolderBlur(_folderProgress);
        }
        UpdateNavigationTransition(0, DropSurface.Width);
    }

    private void UpdateNavigationTransition(double dt, double contentWidth)
    {
        if (_navigationChrome is not { } motion) return;
        motion.Step(dt);
        if (Math.Abs(motion.X - motion.TargetX) < .003 && MotionSettled(_sizeMotion) && MotionSettled(_positionMotion))
        {
            FinishNavigationTransition();
            if (_openFolder != null && !_folderAnimating) _ = CaptureFolderBackdrop();
            return;
        }
        double opacity = Math.Clamp(motion.X, 0, 1);
        SetNavigationChrome(opacity);
        NavigationBar.Opacity = opacity;
        var bounds = new Windows.Foundation.Rect(0, 0, Math.Max(0, contentWidth - 2 * _metrics.Padding), ContentTopRow.Height.Value);
        if (NavigationBar.Clip is RectangleGeometry clip) clip.Rect = bounds;
        else NavigationBar.Clip = new RectangleGeometry { Rect = bounds };
    }

    private void FinishNavigationTransition()
    {
        if (!NavigationTransitionActive) return;
        _navigationChrome = null;
        ReleaseHold(ref _navigationTransitionHold);
        ApplyNavigationVisibility();
    }

    private void ApplyGridMetrics()
    {
        using var sample = IdlePerformance.Measure(IdlePerformance.Work.GridMetrics);
        if (ShortcutGrid == null || _resize != null) return;
        double width = _metrics.Columns * _metrics.CellWidth;
        if (ShortcutGrid.Width != width) ShortcutGrid.Width = width;
        double height = Math.Max(1, Math.Min(_metrics.Rows * _metrics.CellHeight,
            DropSurface.Height - 2 * _metrics.Padding - TopChrome));
        if (ShortcutGrid.Height != height) ShortcutGrid.Height = height;
        var clipBounds = new Windows.Foundation.Rect(0, 0, width, height);
        if (ShortcutGrid.Clip is not RectangleGeometry clip) ShortcutGrid.Clip = new RectangleGeometry { Rect = clipBounds };
        else if (!clip.Rect.Equals(clipBounds)) clip.Rect = clipBounds;
        if (PageRail != null && PageRail.MaxHeight != height) PageRail.MaxHeight = height;
        if (ShortcutGrid.ItemsPanelRoot is TopSlotPanel panel) panel.SetMetrics(_metrics);
    }

    private void TopSlotPanel_Loaded(object sender, RoutedEventArgs e)
    {
        var panel = (TopSlotPanel)sender;
        if (ResizePerformance.Available)
        {
            panel.GetResizeTrace = () => ResizeTrace;
            panel.IsPreview = _previewOnly;
        }
        panel.SetMetrics(_metrics);
    }

    private bool TopSlotsArranged => ShortcutGrid.ItemsPanelRoot is TopSlotPanel panel && panel.IsArranged(_metrics);
    private static T? FindVisual<T>(DependencyObject root, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        if (root is T found && (predicate == null || predicate(found))) return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindVisual(VisualTreeHelper.GetChild(root, i), predicate) is T child) return child;
        return null;
    }
    private bool DragPoint(NativeMethods.Point cursor, UIElement target, out Windows.Foundation.Point point)
    {
        point = default;
        if (!ScreenToClient(_hwnd, ref cursor)) return false;
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? _presentationScale;
        point = ((UIElement)Content).TransformToVisual(target)
            .TransformPoint(new Windows.Foundation.Point(cursor.X / scale, cursor.Y / scale));
        return true;
    }

    private static bool MotionSettled(SlotMotion? motion) => motion == null ||
        Math.Abs(motion.X - motion.TargetX) <= .5 && Math.Abs(motion.Y - motion.TargetY) <= .5;

    private bool DropSurfaceSettled => _visible && _interaction.Position == 1 && _pageReady && TopSlotsArranged && !PageTurning && !NavigationTransitionActive &&
        !_folderAnimating && !_folderReturning && !_previewSettling && MotionSettled(_sizeMotion) && MotionSettled(_positionMotion);

    private bool IsInMemberPanel(NativeMethods.Point cursor) => _folderLayout is { } folder &&
        DragPoint(cursor, GridHost, out var point) && MemberPanelBounds(folder).Contains(point);

    private void TrackItemPointer(NativeMethods.Point cursor, double now, double dt)
    {
        try
        {
            var trace = _itemDragTrace;
            long start = trace == null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
            UpdateItemDragTarget(cursor, now, dt);
            trace?.TargetSample(now, System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            CancelItemDrag();
            Status(UiText.Get("DragPreviewCanceled", ex.Message));
        }
    }

    private void UpdateItemDragTarget(NativeMethods.Point cursor, double now, double dt)
    {
        if (_dragGrid == null || _pressedTile == null) return;
        if (DragPoint(cursor, DragLayer, out var center)) PositionGhost(center);
        var bounds = _geometry.Visible(_edge, _interaction.Position);
        bool inside = bounds.Contains(cursor.X, cursor.Y);
        UpdateDragTrail(cursor, dt, !inside || _itemSwitchTarget != null);
        UpdateFloatingGhost(cursor, !inside || _itemSwitchTarget != null);

        Guid? other = inside || _folderDragEntry.Waiting ? null : OtherDrawerTriggerAt(cursor);
        if (other != _itemSwitchTarget) _itemSwitchTarget = null;
        if (_itemDrawerDwell.Update(other, now)) _itemSwitchTarget = other;
        if (other != null)
        {
            _pageEdgeDwell.Reset();
            ResetDropPreview();
            return;
        }

        if (InMemberGrid)
        {
            if (_folderAnimating || _folderLayout == null)
            {
                ResetDropPreview();
                return;
            }
            var visit = _folderDragEntry.Visit(cursor.X, cursor.Y, IsInMemberPanel(cursor));
            if (visit == FolderDragVisit.Waiting)
            {
                ResetDropPreview();
                return;
            }
            if (visit == FolderDragVisit.Left)
            {
                ReturnFolder();
                SetDragGrid(ShortcutGrid);
            }
        }

        if (!InMemberGrid && _openFolder == null)
        {
            int? band = DragPageBand(cursor);
            TurnPageWhileDragging(band, now);
            if (band is -1 or 1)
            {
                if (_itemDrop != null || _topDropHover.Candidate != null) ResetDropPreview();
                return;
            }
        }

        // During reveal, only the physically visible part of the current grid accepts a drop.
        if (!inside || !DragPoint(cursor, _dragGrid, out var point))
        {
            UpdateSortTarget(-1, -1, now);
            if (!inside && _temporaryPage && _topPage == FormalPages)
                GoToPage(FormalPages - 1);
            return;
        }
        if (!DropSurfaceSettled)
        {
            ResetDropPreview();
            return;
        }
        if (InMemberGrid && dt > 0 && FindVisual<ScrollViewer>(_dragGrid) is { } scroll &&
            point.X >= 0 && point.X < DragMetrics.Columns * DragMetrics.CellWidth)
        {
            double direction = LayoutMetrics.ScrollDirection(point.Y, _dragGrid.ActualHeight);
            if (direction != 0)
            {
                double speed = Math.Min(DrawerSettings.DragScrollMax, DragMetrics.CellHeight * 7);
                double offset = Math.Clamp(scroll.VerticalOffset + direction * Math.Abs(direction) * speed * Math.Min(dt, .05),
                    0, scroll.ScrollableHeight);
                if (Math.Abs(offset - scroll.VerticalOffset) >= .01) scroll.ChangeView(null, offset, null, true);
            }
        }
        UpdateSortTarget(point.X, point.Y, now);
    }

    private Windows.Foundation.Rect MemberPanelBounds(FolderLayout folder) =>
        new(folder.Left, folder.Top - FolderHeadroom, folder.Width, folder.Height);

    private void TickItemDrag(double now, double dt)
    {
        if (_screenRevision != _itemScreenRevision || !GetCursorPos(out var cursor))
        { CancelItemDrag(); return; }
        if ((GetAsyncKeyState(1) & 0x8000) != 0) _itemButtonUpSince = null;
        else
        {
            if (now - (_itemButtonUpSince ??= now) > 100) CancelItemDrag();
            return;
        }
        TrackItemPointer(cursor, now, dt);
        if (!_internalDrag) return;
        if (_itemSwitchTarget is Guid target)
        {
            _interaction.CloseStep(now, dt);
            if (_interaction.Position == 0)
            {
                _itemSwitchTarget = null;
                _itemDrawerDwell.Clear();
                ShowItemDrawer(target);
                SetDragGrid(ShortcutGrid);
            }
        }
        else
        {
            _interaction.KeepOpen();
            _interaction.Update(now, dt, true, true, true);
        }
        RenderPosition();
    }

    private void ShowItemDrawer(Guid id)
    {
        if (!_stores.TryGetValue(id, out var store) || !_edgeGeometries.TryGetValue(id, out var geometry)) return;
        var definition = _preferences.Value.Drawers.Single(d => d.Id == id);
        var arranged = ComputeLayout(_preferences.Value);
        if (!arranged.Items.TryGetValue(id, out var placement)) return;
        EndAcrylicPrime();
        CloseFolder();
        SetTemporaryPage(false);
        if (_internalDrag && _dragGrid != null) { _dragGrid.Footer = null; _dragGrid = null; }
        _slotMotions.Clear(); _tiles.Clear();
        _drawerId = id; _edge = definition.Edge; _store = store; _metrics = placement.Metrics; _arrangedDrawer = placement;
        _sizeMotion = null; _positionMotion = null; _lastHeight = -1;
        SetSizeTarget(geometry);
        StepSize(0, duringDrag: true);
        RefreshTiles(); SettleVisuals(); UpdateNavigationCaption();
        RootGrid.UpdateLayout();
        TickTopPage(_clock.Elapsed.TotalMilliseconds);
        _interaction.Reset(); _interaction.KeepOpen();
        _interaction.Update(_clock.Elapsed.TotalMilliseconds, .016, true, true, true);
        RenderPosition();
        PrototypeLog.Write("drag: preview target " + id);
    }
}
