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
    private bool _folderAnimating, _folderReturning;
    private FolderMotion? _folderMotion;
    private IDisposable? _folderHold;
    private int _folderGeneration;
    private Rect _folderOrigin;
    private Canvas? _folderLayer;
    private readonly List<UIElement> _hiddenFolderIcons = new();

    private Rect TilePictureRect(GridView grid, ShortcutTile tile, UIElement? relativeTo = null, bool normalSize = false)
    {
        var metrics = ReferenceEquals(grid, _memberGrid) ? MemberMetrics() : _metrics;
        double size = normalSize ? tile.NormalPictureSize : tile.RenderedPictureSize;
        double top = metrics.TilePadding + (tile.IconSize - size) / 2;
        if (grid.ContainerFromItem(tile) is FrameworkElement container)
        {
            var p = container.TransformToVisual(relativeTo ?? RootGrid).TransformPoint(new Point((metrics.CellWidth - size) / 2, top));
            return new(p.X, p.Y, size, size);
        }
        return new(metrics.Padding + (metrics.CellWidth - size) / 2,
            metrics.Padding + TopChrome + top, size, size);
    }

    private void CancelFolderAnimation()
    {
        _folderGeneration++;
        _folderReturning = false;
        if (_folderLayer != null) RootGrid.Children.Remove(_folderLayer);
        _folderLayer = null;
        foreach (var icon in _hiddenFolderIcons) icon.Opacity = 1;
        _hiddenFolderIcons.Clear();
        _folderAnimating = false;
        _folderMotion = null;
        ReleaseHold(ref _folderHold);
        if (_folderPanel != null) { _folderPanel.RenderTransform = null; _folderPanel.Opacity = 1; }
        if (_folderHeading != null) _folderHeading.Opacity = 1;
    }

    private void ReturnFolder()
    {
        if (_openFolder == null || _folderReturning || !FinishNameEdit(true)) return;
        if (!_folderAnimating) { _ = AnimateFolder(false); return; }
        if (_folderMotion == null) { CloseFolder(); return; }
        _folderReturning = true;
        _folderMotion.Retarget(0, _clock.Elapsed.TotalMilliseconds);
    }

    private async Task AnimateFolder(bool opening)
    {
        if (_memberGrid == null || _folderPanel == null || _openFolder == null) return;
        var grid = _memberGrid;
        var panel = _folderPanel;
        int generation = _folderGeneration;
        try
        {
            if (_store.Groups.FirstOrDefault(g => g.Id == _openFolder) is not { } group) { CloseFolder(); return; }
            CancelFolderAnimation();
            generation = _folderGeneration;
            _folderAnimating = true;
            _folderReturning = !opening;
            _folderHold = HoldOpen();
            var layer = new Canvas { IsHitTestVisible = false };
            Grid.SetRowSpan(layer, 2);
            RootGrid.Children.Insert(RootGrid.Children.IndexOf(DragLayer), layer);
            _folderLayer = layer;
            if (opening)
            {
                panel.Opacity = 0;
                if (_folderHeading != null) _folderHeading.Opacity = 0;
            }
            else if (_store.TopSlots.IndexOf(group.Id) is int cell && cell >= 0 && cell / PageCapacity != _topPage)
            {
                GoToPage(cell / PageCapacity, animate: false);
                TickTopPage(_clock.Elapsed.TotalMilliseconds);
            }
            GridHost.UpdateLayout();
            SizeFolder();
            GridHost.UpdateLayout();
            if (opening) await CaptureFolderBackdrop();
            if (opening && grid.ContainerFromIndex(0) == null) await Task.Delay(16);
            if (generation != _folderGeneration) return;
            var layerOrigin = layer.TransformToVisual(RootGrid).TransformPoint(new Point());
            var viewport = GridHost.TransformToVisual(RootGrid).TransformPoint(new Point());
            layer.Clip = new RectangleGeometry { Rect = new Rect(viewport.X - layerOrigin.X, viewport.Y - layerOrigin.Y - FolderHeadroom,
                Math.Max(0, GridHost.ActualWidth), Math.Max(0, GridHost.ActualHeight + FolderHeadroom)) };
            var topTile = _tiles.FirstOrDefault(t => t.Id == group.Id);
            if (!opening && topTile != null && ShortcutGrid.ContainerFromItem(topTile) != null)
                _folderOrigin = TilePictureRect(ShortcutGrid, topTile);
            var thumbnailBackground = new Border
            {
                Width = _folderOrigin.Width, Height = _folderOrigin.Height,
                Background = DrawerPalette.Brush("Folder"),
                CornerRadius = new CornerRadius(FolderThumbnailLayout.Radius(_folderOrigin.Width))
            };
            Canvas.SetLeft(thumbnailBackground, _folderOrigin.X - layerOrigin.X);
            Canvas.SetTop(thumbnailBackground, _folderOrigin.Y - layerOrigin.Y);
            layer.Children.Add(thumbnailBackground);
            var sprites = new List<(FrameworkElement View, Rect Small, Rect Large, bool Preview)>();
            foreach (var tile in _members)
            {
                if (_internalDrag && _dragBatch.Contains(tile.Id)) continue;
                if (grid.ContainerFromItem(tile) is not FrameworkElement container) continue;
                var position = container.TransformToVisual(grid).TransformPoint(new Point());
                if (position.Y + MemberMetrics().CellHeight <= 0 || position.Y >= grid.ActualHeight) continue;
                int index = group.Members.IndexOf(tile.Id);
                if (index < 0) continue;
                if (tile.Icon == null && _iconCache.TryGetValue(tile.Id, out var cached)) tile.Icon = cached.Image;
                var large = TilePictureRect(grid, tile);
                bool preview = index < FolderThumbnailLayout.Capacity;
                var slot = FolderThumbnailLayout.Slot(_folderOrigin.Width, preview ? index : 0,
                    preview ? group.Members.Count : 1);
                var small = new Rect(_folderOrigin.X + slot.X, _folderOrigin.Y + slot.Y, slot.Size, slot.Size);
                var visual = IconVisual(tile, large.Width);
                visual.RenderTransform = new ScaleTransform();
                layer.Children.Add(visual);
                sprites.Add((visual, small, large, preview));
                HideAnimationIcon(container);
            }
            if (topTile != null && ShortcutGrid.ContainerFromItem(topTile) is FrameworkElement top)
                HideAnimationIcon(top);
            var panelOrigin = panel.TransformToVisual(RootGrid).TransformPoint(new Point());
            var transform = new CompositeTransform();
            panel.RenderTransform = transform;
            var motion = new FolderMotion(opening ? 0 : 1, opening ? 1 : 0, _clock.Elapsed.TotalMilliseconds);
            _folderMotion = motion;
            while (generation == _folderGeneration)
            {
                IdlePerformance.Count(IdlePerformance.Work.FolderAnimationFrame);
                double amount = motion.Sample(_clock.Elapsed.TotalMilliseconds);
                SetFolderBlur(amount);
                thumbnailBackground.Opacity = 1 - amount;
                panel.Opacity = amount;
                if (_folderHeading != null) _folderHeading.Opacity = amount;
                transform.ScaleX = amount + (1 - amount) * _folderOrigin.Width / Math.Max(1, panel.ActualWidth);
                transform.ScaleY = amount + (1 - amount) * _folderOrigin.Height / Math.Max(1, panel.ActualHeight);
                transform.TranslateX = (_folderOrigin.X - panelOrigin.X) * (1 - amount);
                transform.TranslateY = (_folderOrigin.Y - panelOrigin.Y) * (1 - amount);
                foreach (var sprite in sprites)
                {
                    var a = sprite.Small; var b = sprite.Large;
                    double scale = (a.Width + (b.Width - a.Width) * amount) / b.Width;
                    var scaleTransform = (ScaleTransform)sprite.View.RenderTransform;
                    scaleTransform.ScaleX = scaleTransform.ScaleY = scale;
                    Canvas.SetLeft(sprite.View, a.X + (b.X - a.X) * amount - layerOrigin.X);
                    Canvas.SetTop(sprite.View, a.Y + (b.Y - a.Y) * amount - layerOrigin.Y);
                    sprite.View.Opacity = sprite.Preview ? 1 : amount;
                }
                if (amount == motion.Target) break;
                await Task.Delay(16);
            }
            if (generation != _folderGeneration) return;
            if (motion.Target == 1) CancelFolderAnimation(); else CloseFolder();
        }
        catch (Exception ex)
        {
            PrototypeLog.Write("folder transition: " + ex);
            if (generation == _folderGeneration && !_closed)
                try { CloseFolder(); }
                catch (Exception cleanup) { PrototypeLog.Write("folder transition cleanup: " + cleanup); }
        }
    }

    private void HideAnimationIcon(FrameworkElement container)
    {
        if (FindVisual<Image>(container)?.Parent is not UIElement icon) return;
        icon.Opacity = 0;
        _hiddenFolderIcons.Add(icon);
    }
}
