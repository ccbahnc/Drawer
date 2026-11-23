using System;
using System.Linq;
using Drawer.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using static Drawer.NativeMethods;

namespace Drawer;

public sealed partial class MainWindow
{
    private sealed record BoxSelection(GridView Grid, Pointer Pointer, Windows.Foundation.Point Start,
        bool Add, ItemSelection.Snapshot Original, IDisposable Hold, TextBlock? NameLabel);
    private BoxSelection? _boxSelection;
    private Border? _selectionFrame;

    private void FocusContent(GridView grid)
    {
        ActivateContentInput();
        grid.Focus(FocusState.Pointer);
    }

    private void ActivateContentInput()
    {
        if (GetForegroundWindow() != _hwnd)
        {
            long style = GetWindowLongPtrW(_hwnd, -20).ToInt64();
            SetWindowLongPtrW(_hwnd, -20, (nint)(style & ~0x08000000L));
            SetForegroundWindow(_hwnd);
        }
    }

    private bool BeginSelectionBox(GridView grid, PointerRoutedEventArgs e, TextBlock? nameLabel)
    {
        var point = e.GetCurrentPoint(grid).Position;
        var metrics = ReferenceEquals(grid, _memberGrid) ? MemberMetrics() : _metrics;
        if (!DropSurfaceSettled || point.X < 0 || point.X >= metrics.Columns * metrics.CellWidth ||
            point.Y < 0 || point.Y >= grid.ActualHeight || !RootGrid.CapturePointer(e.Pointer)) return false;
        _boxSelection = new(grid, e.Pointer, point,
            (e.KeyModifiers & VirtualKeyModifiers.Control) != 0, _selection.Capture(), HoldOpen(), nameLabel);
        // GridView focus can scroll the top-level panel between the two name clicks.
        // A name gesture takes focus only when it becomes an edit or a box selection.
        if (nameLabel == null) FocusContent(grid);
        e.Handled = true;
        return true;
    }

    private ShortcutTile? HitIcon(GridView grid, Windows.Foundation.Point point)
    {
        if (point.Y < 0 || point.Y >= grid.ActualHeight) return null;
        bool member = ReferenceEquals(grid, _memberGrid);
        var metrics = member ? MemberMetrics() : _metrics;
        double offset = GridOffset(grid);
        int index = member ? metrics.HitIndex(point.X, point.Y, offset, grid.Items.Count, false) : TopCellAt(point.X, point.Y);
        if (index < 0 || index >= grid.Items.Count || grid.Items[index] is not ShortcutTile { IsEmpty: false } tile ||
            grid.ContainerFromIndex(index) == null) return null;
        var (column, row) = LayoutMetrics.SlotPosition(index, metrics.Columns);
        double left = column * metrics.CellWidth;
        double top = row * metrics.CellHeight - offset;
        // PictureSize includes the reserved selection marker space but not the hover or selection animation.
        double half = tile.PictureSize / 2 + DrawerSettings.IconHitPadding;
        double centerX = left + metrics.CellWidth / 2;
        double centerY = top + metrics.TilePadding + tile.IconSize / 2;
        return point.X >= Math.Max(left, centerX - half) && point.X < Math.Min(left + metrics.CellWidth, centerX + half) &&
            point.Y >= Math.Max(top, centerY - half) &&
            point.Y < Math.Min(top + metrics.CellHeight, Math.Min(top + metrics.TilePadding + tile.IconSize + metrics.NameGap, centerY + half))
            ? tile : null;
    }

    private const double SelectionBoxThreshold = 4;

    private bool MoveSelectionBox(PointerRoutedEventArgs e)
    {
        if (_boxSelection is not { } box || box.Pointer.PointerId != e.Pointer.PointerId) return false;
        var point = e.GetCurrentPoint(box.Grid).Position;
        if (_selectionFrame == null)
        {
            double dx = point.X - box.Start.X, dy = point.Y - box.Start.Y;
            if (dx * dx + dy * dy < SelectionBoxThreshold * SelectionBoxThreshold) return true;
            _lastNameClick = null;
            if (box.NameLabel != null) FocusContent(box.Grid);
            var color = DrawerPalette.Brush("SelectionFill").Color;
            _selectionFrame = new Border { IsHitTestVisible = false, BorderThickness = new Thickness(1),
                BorderBrush = DrawerPalette.Brush("SelectionFill"),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(42, color.R, color.G, color.B)) };
            DragLayer.Children.Add(_selectionFrame);
        }
        var metrics = ReferenceEquals(box.Grid, _memberGrid) ? MemberMetrics() : _metrics;
        var start = box.Grid.TransformToVisual(DragLayer).TransformPoint(box.Start);
        var origin = box.Grid.TransformToVisual(DragLayer).TransformPoint(new Windows.Foundation.Point());
        var end = new Windows.Foundation.Point(origin.X + Math.Clamp(point.X, 0, metrics.Columns * metrics.CellWidth),
            origin.Y + Math.Clamp(point.Y, 0, box.Grid.ActualHeight));
        var rect = new Windows.Foundation.Rect(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Abs(start.X - end.X), Math.Abs(start.Y - end.Y));
        Canvas.SetLeft(_selectionFrame, rect.X);
        Canvas.SetTop(_selectionFrame, rect.Y);
        _selectionFrame.Width = rect.Width;
        _selectionFrame.Height = rect.Height;
        var items = ReferenceEquals(box.Grid, _memberGrid) ? _members : _tiles.Skip(_topPage * PageCapacity).Take(PageCapacity);
        var hits = items.Where(tile => !tile.IsEmpty && box.Grid.ContainerFromItem(tile) != null).Where(tile =>
        {
            var icon = TilePictureRect(box.Grid, tile, DragLayer, normalSize: true);
            return rect.Left < icon.Right && rect.Right > icon.Left && rect.Top < icon.Bottom && rect.Bottom > icon.Top;
        }).Select(tile => tile.Id);
        var selected = (box.Add ? box.Original.Items.Concat(hits) : hits).Distinct().ToArray();
        _selection.Restore(new(selected.Length > 0, selected.Select(id => (Guid?)id).LastOrDefault(), selected));
        RefreshSelectionMarks();
        return true;
    }

    private void EndSelectionBox(bool cancel)
    {
        if (_boxSelection is not { } box) return;
        _boxSelection = null;
        if (_selectionFrame != null) DragLayer.Children.Remove(_selectionFrame);
        else if (!cancel && !box.Add) _selection.Clear();
        _selectionFrame = null;
        if (cancel) _selection.Restore(box.Original);
        if (cancel) _lastNameClick = null;
        box.Hold.Dispose();
        RootGrid.ReleasePointerCapture(box.Pointer);
        RefreshSelectionMarks();
    }

}
