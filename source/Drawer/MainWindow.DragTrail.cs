using System;
using System.Linq;
using System.Threading.Tasks;
using Drawer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using static Drawer.NativeMethods;

namespace Drawer;

public sealed partial class MainWindow
{
    private sealed class TrailSprite
    {
        public Guid Id { get; }
        public FrameworkElement Icon { get; }
        public FrameworkElement Visual { get; }
        public TranslateTransform Offset { get; } = new();
        public double Alpha { get; }
        public FloatingDragIcon? Popup { get; set; }

        public TrailSprite(Guid id, FrameworkElement icon, double alpha)
        {
            Id = id;
            Icon = icon;
            Alpha = alpha;
            Visual = DragSprite(icon, Offset, alpha);
        }
    }

    private DragTrail? _dragTrail;
    private TrailSprite[] _trailSprites = Array.Empty<TrailSprite>();

    private void BeginDragTrail(double leadSize)
    {
        if (!IsBatchDrag || _pressedTile == null || _dragGrid == null || !GetCursorPos(out var cursor)) return;
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? _presentationScale;
        double size = leadSize * .82;
        var origin = new NativeMethods.Point();
        ClientToScreen(_hwnd, ref origin);
        var layerOrigin = DragLayer.TransformToVisual((UIElement)Content).TransformPoint(new());
        var gridOrigin = _dragGrid.TransformToVisual(DragLayer).TransformPoint(new());
        var tiles = _dragBatchTiles.Where(tile => tile.Id != _pressedTile.Id).Take(DragTrail.Capacity).ToArray();
        var positions = new TrailPoint[tiles.Length];
        var sprites = new TrailSprite[tiles.Length];
        for (int i = tiles.Length - 1; i >= 0; i--)
        {
            var rect = TilePictureRect(_dragGrid, tiles[i], DragLayer);
            positions[i] = _dragGrid.ContainerFromItem(tiles[i]) != null &&
                rect.Y + rect.Height > gridOrigin.Y && rect.Y < gridOrigin.Y + _dragGrid.ActualHeight
                ? new(origin.X + (rect.X + rect.Width / 2 + layerOrigin.X) * scale,
                      origin.Y + (rect.Y + rect.Height / 2 + layerOrigin.Y) * scale)
                : new(cursor.X, cursor.Y - DrawerSettings.DragLift * scale);
            var icon = IconVisual(tiles[i], size * (1 - .045 * i));
            sprites[i] = new(tiles[i].Id, icon, .74 - .10 * i);
            DragLayer.Children.Add(sprites[i].Visual);
        }
        _dragTrail = new(positions);
        _trailSprites = sprites;
        UpdateDragTrail(cursor, 0, outside: false);
    }

    private IconPose DragPose(Guid id)
    {
        var sprite = _trailSprites.FirstOrDefault(tail => tail.Id == id);
        return sprite == null ? GhostPose() : new(sprite.Offset.X, sprite.Offset.Y,
            sprite.Visual.Width, sprite.Alpha);
    }

    private async Task PrepareTrailPopup(TrailSprite sprite)
    {
        var popup = await CaptureFloatingIcon(sprite.Icon, () => _trailSprites.Contains(sprite));
        if (_trailSprites.Contains(sprite)) sprite.Popup = popup;
        else popup?.Dispose();
    }

    private void UpdateDragTrail(NativeMethods.Point cursor, double dt, bool outside)
    {
        if (_dragTrail == null || _pressedTile == null) return;
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? _presentationScale;
        _dragTrail.Step(new(cursor.X, cursor.Y - DrawerSettings.DragLift * scale),
            (_dragGhost?.Width ?? _pressedTile.PictureSize) * scale, dt);
        var origin = ((UIElement)Content).TransformToVisual(DragLayer).TransformPoint(new());
        bool receiving = _mergeAnimation != null;
        for (int i = _trailSprites.Length - 1; i >= 0; i--)
        {
            var sprite = _trailSprites[i];
            var point = _dragTrail.Points[i];
            var screen = new NativeMethods.Point { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) };
            if (outside && !receiving && sprite.Popup?.Show(screen, 0, (byte)(255 * sprite.Alpha)) == true)
            {
                sprite.Visual.Opacity = 0;
                continue;
            }
            sprite.Popup?.Hide();
            if (ScreenToClient(_hwnd, ref screen))
            {
                double x = origin.X + screen.X / scale - sprite.Visual.Width / 2;
                double y = origin.Y + screen.Y / scale - sprite.Visual.Height / 2;
                if (sprite.Offset.X != x) sprite.Offset.X = x;
                if (sprite.Offset.Y != y) sprite.Offset.Y = y;
            }
            sprite.Visual.Opacity = receiving ? 0 : sprite.Alpha;
        }
    }

    private void ClearDragTrail(bool removeVisuals = true)
    {
        foreach (var sprite in _trailSprites)
        {
            if (removeVisuals) DragLayer.Children.Remove(sprite.Visual);
            sprite.Popup?.Dispose();
        }
        _trailSprites = Array.Empty<TrailSprite>();
        _dragTrail = null;
    }
}
