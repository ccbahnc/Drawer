using System;
using System.Collections.Generic;
using System.Linq;
using Drawer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Point = Windows.Foundation.Point;
using static Drawer.NativeMethods;

namespace Drawer;
public sealed partial class MainWindow
{
    private readonly Dictionary<(bool Member,Guid Id),SlotMotion> _slotMotions=new();
    private bool _tileVisualsDirty = true, _tileMotionWasActive;
    private ClipboardItems? _paintedCutItems;

    private void TileLayoutUpdated(object? sender, object args) => _tileVisualsDirty = true;

    private bool SlotsMoving()
    {
        foreach (var motion in _slotMotions.Values)
            if (motion.IsMoving) return true;
        return false;
    }
    private FrameworkElement? _dragGhost,_mergeVisual;
    private Guid? _shownMerge;
    private Point _ghostCenter;
    private static Point Slot(int index, LayoutMetrics metrics)
    {
        var (column, row) = LayoutMetrics.SlotPosition(index, metrics.Columns);
        return new(column * metrics.CellWidth, row * metrics.CellHeight);
    }

    private SlotMotion Motion(bool member,Guid id,int index,LayoutMetrics metrics)
    {
        var key=(member,id); if(!_slotMotions.TryGetValue(key,out var state)) { var p=Slot(index,metrics); _slotMotions[key]=state=new(p.X,p.Y); } return state;
    }
    private void SetPreviewTargets(GridView grid,Guid[] order)
    {
        _tileVisualsDirty = true;
        bool member=ReferenceEquals(grid,_memberGrid); var metrics=member?MemberMetrics():_metrics;
        var tiles=member?_members:_tiles.ToArray();
        var positions = new Dictionary<Guid,int>();
        for (int i = 0; i < order.Length; i++)
            if (order[i] != Guid.Empty) positions[order[i]] = i;
        for(int i=0;i<tiles.Length;i++)
        {
            var tile=tiles[i];
            if (!positions.TryGetValue(tile.Id, out int destination)) continue;
            var p=Slot(destination,metrics); var motion=Motion(member,tile.Id,i,metrics); motion.TargetX=p.X; motion.TargetY=p.Y;
        }
    }
    private void SettleVisuals()
    {
        var live = _store.Items.Select(e=>e.Id).Concat(_store.Groups.Select(g=>g.Id)).ToHashSet();
        foreach(var key in _slotMotions.Keys.Where(k=>!live.Contains(k.Id)).ToArray()) _slotMotions.Remove(key);
        SetPreviewTargets(ShortcutGrid,_store.TopSlots.ToArray());
        if(_memberGrid!=null && _openFolder!=null) SetPreviewTargets(_memberGrid,_members.Select(t=>t.Id).ToArray());
    }
    private void BeginDragVisual(double leadSize)
    {
        if(_dragGrid?.ContainerFromItem(_pressedTile) is GridViewItem source) source.Opacity=0;
        BeginDragTrail(leadSize);
        var icon = IconVisual(_pressedTile!, leadSize);
        _dragGhost = DragSprite(icon, new TranslateTransform(), .92);
        DragLayer.Children.Add(_dragGhost);
        PositionGhost(_dragGrid!.TransformToVisual(DragLayer).TransformPoint(_dragPoint));
        // Arrange all sprites once before capturing the floating snapshots.
        DragLayer.UpdateLayout();
        _ = PrepareFloatingGhost(_dragGhost, icon);
        foreach (var sprite in _trailSprites) _ = PrepareTrailPopup(sprite);
    }

    private static Grid DragSprite(FrameworkElement icon, TranslateTransform offset, double opacity)
    {
        // Position the host, so RenderTargetBitmap can capture the untransformed icon.
        var host = new Grid { Width = icon.Width, Height = icon.Height, Opacity = opacity,
            RenderTransform = offset, IsHitTestVisible = false };
        host.Children.Add(icon);
        return host;
    }

    private void PositionGhost(Point center)
    {
        if (_dragGhost?.RenderTransform is not TranslateTransform offset) return;
        _ghostCenter = center;
        double x = center.X - _dragGhost.Width / 2;
        double y = center.Y - _dragGhost.Height / 2 - DrawerSettings.DragLift;
        if (offset.X != x) offset.X = x;
        if (offset.Y != y) offset.Y = y;
    }
    private MergeAnimation? _mergeAnimation;
    private FrameworkElement? _morphTargetIcon;
    private Border? _morphBackground;
    private Guid? _morphResult;
    private bool _morphCommitted, _morphEnding, _morphExisting, _morphSelection;
    private double _morphBackgroundFrom;
    private IDisposable? _morphHold;
    private double _morphHoldSince;
    private void HoldMorph()
    {
        if (_morphHold != null) return;
        _morphHold = HoldOpen();
        _morphHoldSince = _clock.Elapsed.TotalMilliseconds;
    }
    private MorphSource[] _morphSources = Array.Empty<MorphSource>();
    private (FrameworkElement Visual, ThumbnailSlot From, ThumbnailSlot To)[] _morphResidents = [];
    private readonly Dictionary<Guid, (FrameworkElement Visual, IconPose From, IconPose To)> _dissolveSprites = new();
    private int _morphMemberCount;

    private readonly record struct IconPose(double X, double Y, double Size, double Alpha = 1)
    {
        public IconPose Mix(IconPose next, double amount) => new(
            X + (next.X - X) * amount, Y + (next.Y - Y) * amount,
            Size + (next.Size - Size) * amount, Alpha + (next.Alpha - Alpha) * amount);
    }

    private sealed class MorphSource(Guid id, FrameworkElement visual, int slot, IconPose initial, bool entersView = false)
    {
        public Guid Id { get; } = id;
        public FrameworkElement Visual { get; } = visual;
        public int Slot { get; } = slot;
        public bool EntersView { get; } = entersView;
        public IconPose From { get; set; } = initial;
        public IconPose Current { get; set; } = initial;
    }

    private void CancelMergeVisual()
    {
        if (_mergeAnimation == null || _morphCommitted || _morphEnding || _pressedTile == null) return;
        double now = _clock.Elapsed.TotalMilliseconds;
        SetMorphDirection(false, now);
        int index = _tiles.ToList().FindIndex(t => t.Id == _pressedTile.Id);
        if (index >= 0)
        {
            var origin = ShortcutGrid.TransformToVisual(DragLayer).TransformPoint(new Point());
            var slot = Slot(index, _metrics);
            _ghostCenter = new(origin.X + slot.X + _metrics.CellWidth / 2,
                origin.Y + slot.Y + _metrics.TilePadding + _pressedTile.IconSize / 2 + DrawerSettings.DragLift -
                (FindVisual<ScrollViewer>(ShortcutGrid)?.VerticalOffset ?? 0));
        }
        _morphEnding = true;
        HoldMorph();
    }

    private FrameworkElement IconVisual(ShortcutTile tile, double? requestedSize = null)
    {
        double size = requestedSize ?? tile.PictureSize;
        if (!tile.IsGroup)
            return tile.Icon is { } image
                ? new Image { Width = size, Height = size, Source = image, Stretch = Stretch.Uniform, IsHitTestVisible = false }
                : MemberIconVisual(tile.Id, size);
        var host = new Grid { Width = size, Height = size, IsHitTestVisible = false };
        LoadFolderPreview(host, tile, size);
        return host;
    }
    private IconPose GhostPose()
    {
        double size = _dragGhost?.Width ?? _pressedTile?.PictureSize ?? _metrics.PictureSize();
        return new(_ghostCenter.X - size / 2, _ghostCenter.Y - size / 2 - DrawerSettings.DragLift, size, .92);
    }

    private Canvas CreateMergeCanvas(double icon, bool existing)
    {
        var canvas = new Canvas { Width = _metrics.CellWidth, Height = _metrics.CellHeight, IsHitTestVisible = false };
        _morphBackground = new Border { Width = icon, Height = icon,
            CornerRadius = new CornerRadius(FolderThumbnailLayout.Radius(icon)),
            Background = DrawerPalette.Brush("Folder"), Opacity = existing ? 1 : 0 };
        Canvas.SetLeft(_morphBackground, (_metrics.CellWidth - icon) / 2);
        Canvas.SetTop(_morphBackground, _metrics.IconCenterY - icon / 2);
        canvas.Children.Add(_morphBackground);
        return canvas;
    }

    private void PrepareSelectedFolderVisual(ShortcutTile[] tiles, Guid? anchorId = null, IReadOnlyList<Guid>? memberOrder = null,
        bool existing = false, IReadOnlyDictionary<Guid, IconPose>? previous = null, double? previousBackground = null)
    {
        RemoveMergeVisual();
        _shownMerge = anchorId ?? tiles[0].Id;
        _morphMemberCount = tiles.Sum(tile => tile.Group?.Members.Count ?? 1);
        _morphSelection = true;
        _morphExisting = existing;
        _morphBackgroundFrom = previousBackground ?? (existing ? 1 : 0);
        double icon = _metrics.PictureSize();
        double iconTop = _metrics.IconCenterY - icon / 2;
        var canvas = CreateMergeCanvas(icon, existing);
        var sprites = new List<MorphSource>();
        var origin = ShortcutGrid.TransformToVisual(DragLayer).TransformPoint(new Point());
        double offset = _topScrollViewer?.VerticalOffset ?? GridOffset(ShortcutGrid);
        bool offscreenOverflow = false;
        int anchor = _store.TopSlots.IndexOf(_shownMerge.Value);
        var target = Slot(anchor, _metrics);
        var indices = memberOrder?.Select((id, i) => (id, i)).ToDictionary(item => item.id, item => item.i);
        int memberIndex = 0;
        foreach (var tile in tiles)
        {
            int index = _store.TopSlots.IndexOf(tile.Id);
            var motion = Motion(false, tile.Id, index, _metrics);
            bool visible = motion.Y + iconTop < offset + TopContentHeight &&
                motion.Y + iconTop + icon > offset;
            var pose = visible
                ? new IconPose(origin.X + motion.X + (_metrics.CellWidth - icon) / 2,
                    origin.Y + motion.Y - offset + iconTop, icon)
                : new IconPose(origin.X + target.X + (_metrics.CellWidth - icon * .70) / 2,
                    origin.Y + target.Y - offset + iconTop + icon * .15, icon * .70, 0);
            if (visible && ShortcutGrid.ContainerFromItem(tile) is FrameworkElement)
            {
                var bounds = TilePictureRect(ShortcutGrid, tile, DragLayer);
                pose = new(bounds.X, bounds.Y, bounds.Width);
            }
            var members = tile.Group?.Members;
            int count = members?.Count ?? 1;
            for (int i = 0; i < count; i++, memberIndex++)
            {
                Guid id = members?[i] ?? tile.Id;
                int order = indices?.GetValueOrDefault(id, memberIndex) ?? memberIndex;
                int thumbnail = order < FolderThumbnailLayout.Capacity ? order : -1;
                bool shown = visible && (members == null || i < FolderThumbnailLayout.Capacity);
                if (!shown && thumbnail < 0 && offscreenOverflow) continue;
                if (!shown && thumbnail < 0) offscreenOverflow = true;
                var from = shown ? pose : pose with { Alpha = 0 };
                if (shown && members != null)
                {
                    var slot = FolderThumbnailLayout.Slot(pose.Size, i, count);
                    from = new(pose.X + slot.X, pose.Y + slot.Y, slot.Size);
                }
                if (previous != null && previous.TryGetValue(id, out var current)) from = current;
                var visual = MemberIconVisual(id, icon);
                sprites.Add(new(id, visual, thumbnail, from, entersView: !shown));
                canvas.Children.Add(visual);
            }
        }
        _morphSources = sprites.ToArray();
        _mergeVisual = canvas;
        _mergeAnimation = new(_clock.Elapsed.TotalMilliseconds);
        DragLayer.Children.Add(canvas);
        PositionMergeVisual(_clock.Elapsed.TotalMilliseconds);
    }

    private void StartSelectedFolderAnimation() =>
        _mergeAnimation = new(_clock.Elapsed.TotalMilliseconds, DrawerSettings.FormFolderAnimationMs);

    private IconPose? CaptureDissolveOrigin(ShortcutTile tile)
    {
        if (!DropSurfaceSettled || ShortcutGrid.ContainerFromItem(tile) is not FrameworkElement) return null;
        var rect = TilePictureRect(ShortcutGrid, tile, DragLayer);
        return new IconPose(rect.X, rect.Y, rect.Width);
    }

    private void ClearDissolveMemberPositions(IEnumerable<Guid> members)
    {
        foreach (Guid member in members) _slotMotions.Remove((false, member));
    }

    private void ShowMergePreview(ShortcutTile? target, double now)
    {
        if (_morphCommitted) return;
        if (target == null) { SetMorphDirection(false, now); return; }
        if (_shownMerge == target.Id) { SetMorphDirection(true, now); return; }

        var previousPoses = _morphSources.ToDictionary(sprite => sprite.Id, sprite => sprite.Current);
        RemoveMergeVisual();
        _shownMerge = target.Id;
        _mergeAnimation = new(now);
        _morphExisting = target.IsGroup;
        if (_dragGhost != null) _dragGhost.Opacity = 0;

        double icon = _metrics.PictureSize();
        var canvas = CreateMergeCanvas(icon, target.IsGroup);

        var residents = target.Group?.Members.Where(id => !_dragBatch.Contains(id)).ToArray() ?? Array.Empty<Guid>();
        var arriving = _dragBatchTiles.Where(tile => tile.Id != target.Id)
            .SelectMany(tile => tile.Group != null && target.IsGroup
                ? tile.Group.Members.Select(id => (Id: id, Tile: tile))
                : new[] { (Id: tile.Id, Tile: tile) }).ToArray();
        int firstSlot = target.IsGroup ? residents.Length : 1;
        _morphMemberCount = firstSlot + arriving.Length;
        if (target.IsGroup)
        {
            _morphResidents = residents.Take(FolderThumbnailLayout.Capacity).Select((id, index) =>
            {
                var from = FolderThumbnailLayout.Slot(icon, index, residents.Length);
                var to = FolderThumbnailLayout.Slot(icon, index, _morphMemberCount);
                var visual = MemberIconVisual(id, from.Size);
                canvas.Children.Add(visual);
                return ((FrameworkElement)visual, from, to);
            }).ToArray();
        }
        else
        {
            _morphTargetIcon = IconVisual(target);
            canvas.Children.Add(_morphTargetIcon);
        }

        var entering = arriving.Take(Math.Max(0, FolderThumbnailLayout.Capacity - firstSlot))
            .Select((item, index) => (item.Id, item.Tile, Slot: firstSlot + index)).ToList();
        if (_pressedTile is { } pressed && arriving.FirstOrDefault(item => item.Tile == pressed) is { Tile: not null } lead &&
            !entering.Any(item => item.Id == lead.Id)) entering.Add((lead.Id, lead.Tile, -1));
        _morphSources = entering.Select(item =>
        {
            Guid id = item.Id;
            var start = previousPoses.TryGetValue(id, out var current) ? current : DragPose(item.Tile.Id);
            if (item.Tile.Group is { } source && id != item.Tile.Id && !previousPoses.ContainsKey(id))
            {
                int index = source.Members.IndexOf(id);
                var small = FolderThumbnailLayout.Slot(start.Size, Math.Min(index, FolderThumbnailLayout.Capacity - 1), source.Members.Count);
                start = new(start.X + small.X, start.Y + small.Y, small.Size, start.Alpha);
            }
            var visual = id == item.Tile.Id ? IconVisual(item.Tile) : MemberIconVisual(id, item.Tile.PictureSize,
                _stores.GetValueOrDefault(_itemSourceDrawer));
            var sprite = new MorphSource(id, visual, item.Slot, start);
            canvas.Children.Add(sprite.Visual);
            return sprite;
        }).ToArray();

        if (_metrics.Effective.ShowNames)
        {
            var label = new TextBlock { Text = target.Group?.Name ?? UiText.Get("NewFolder"), FontSize = 12,
                Foreground = DrawerPalette.Brush("Foreground"), Width = _metrics.CellWidth - 10,
                TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Canvas.SetLeft(label, 5);
            Canvas.SetTop(label, _metrics.IconSize + _metrics.TilePadding + _metrics.NameGap);
            canvas.Children.Add(label);
        }
        _mergeVisual = canvas;
        DragLayer.Children.Add(canvas);
        PositionMergeVisual(now);
        _itemDragTrace?.Record(target.IsGroup ? "animated append" : "animated merge");
    }

    private void SetMorphDirection(bool forward, double now)
    {
        if (_mergeAnimation == null || _mergeAnimation.Forward == forward) return;
        PositionMergeVisual(now);
        foreach (var source in _morphSources) source.From = source.Current;
        _mergeAnimation.ReverseTo(forward, now);
    }

    private static void PlaceSprite(FrameworkElement sprite, IconPose pose, double left, double top)
    {
        if (sprite.RenderTransform is not ScaleTransform scale) sprite.RenderTransform = scale = new ScaleTransform();
        scale.ScaleX = scale.ScaleY = pose.Size / sprite.Width;
        Canvas.SetLeft(sprite, pose.X - left);
        Canvas.SetTop(sprite, pose.Y - top);
        sprite.Opacity = pose.Alpha;
    }

    private void BeginDissolveVisual(IReadOnlyList<Guid> members, IconPose origin,
        IReadOnlyDictionary<Guid, IconPose>? previous = null, double? previousBackground = null)
    {
        RemoveMergeVisual();
        var viewport = GridHost.TransformToVisual(DragLayer).TransformPoint(new Point());
        var grid = ShortcutGrid.TransformToVisual(DragLayer).TransformPoint(new Point());
        double offset = _topScrollViewer?.VerticalOffset ?? GridOffset(ShortcutGrid);
        var canvas = new Canvas
        {
            IsHitTestVisible = false,
            Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(viewport.X, viewport.Y,
                _metrics.Columns * _metrics.CellWidth, TopContentHeight) }
        };
        var background = new Border { Width = origin.Size, Height = origin.Size,
            Background = DrawerPalette.Brush("Folder"), CornerRadius = new CornerRadius(FolderThumbnailLayout.Radius(origin.Size)),
            Opacity = previousBackground ?? 1 };
        _morphBackgroundFrom = background.Opacity;
        Canvas.SetLeft(background, origin.X);
        Canvas.SetTop(background, origin.Y);
        canvas.Children.Add(background);

        int first = _topPage * PageCapacity;
        var pageCells = new Dictionary<Guid, int>();
        for (int cell = first; cell < Math.Min(first + PageCapacity, _tiles.Count); cell++)
            if (!_tiles[cell].IsEmpty) pageCells.Add(_tiles[cell].Id, cell);

        IconPose PageIcon(int cell)
        {
            var tile = _tiles[cell];
            var slot = Slot(cell, _metrics);
            double size = tile.RenderedPictureSize;
            return new(grid.X + slot.X + (_metrics.CellWidth - size) / 2,
                grid.Y + slot.Y - offset + _metrics.TilePadding + (tile.IconSize - size) / 2, size);
        }
        var folderSlots = new Dictionary<Guid, IconPose>();
        var incoming = members.ToHashSet();
        foreach (var tile in _tiles.Skip(first).Take(PageCapacity))
        {
            if (tile.Group is not { } folder || !folder.Members.All(incoming.Contains)) continue;
            var target = PageIcon(pageCells[tile.Id]);
            var plate = new Border { Width = target.Size, Height = target.Size,
                Background = DrawerPalette.Brush("Folder"), CornerRadius = new CornerRadius(FolderThumbnailLayout.Radius(target.Size)) };
            canvas.Children.Add(plate);
            var from = target with { Alpha = 0 };
            PlaceSprite(plate, from, 0, 0);
            _dissolveSprites.Add(tile.Id, (plate, from, target));
            for (int i = 0; i < Math.Min(folder.Members.Count, FolderThumbnailLayout.Capacity); i++)
            {
                var slot = FolderThumbnailLayout.Slot(target.Size, i, folder.Members.Count);
                folderSlots.Add(folder.Members[i], new(target.X + slot.X, target.Y + slot.Y, slot.Size));
            }
        }
        for (int i = 0; i < members.Count; i++)
        {
            var id = members[i];
            bool preview = i < FolderThumbnailLayout.Capacity;
            bool onPage = pageCells.TryGetValue(id, out int cell);
            bool intoFolder = folderSlots.TryGetValue(id, out var thumbnailTarget);
            if (!preview && !onPage && !intoFolder) continue;
            var thumbnail = FolderThumbnailLayout.Slot(origin.Size, preview ? i : 0, preview ? members.Count : 1);
            var start = new IconPose(origin.X + thumbnail.X, origin.Y + thumbnail.Y, thumbnail.Size, preview ? 1 : 0);
            var to = start with { Alpha = 0 };
            if (onPage) to = PageIcon(cell);
            else if (intoFolder) to = thumbnailTarget;
            if (previous != null && previous.TryGetValue(id, out var current)) start = current;
            var visual = MemberIconVisual(id, _metrics.PictureSize());
            canvas.Children.Add(visual);
            PlaceSprite(visual, start, 0, 0);
            _dissolveSprites.Add(id, (visual, start, to));
        }

        _mergeVisual = canvas;
        _morphBackground = background;
        _mergeAnimation = new(_clock.Elapsed.TotalMilliseconds, DrawerSettings.FormFolderAnimationMs);
        HoldMorph();
        _morphCommitted = true;
        DragLayer.Children.Add(canvas);
        ApplyMotions(ShortcutGrid, _tiles, false, _metrics);
    }

    private void PositionMergeVisual(double now)
    {
        if (_mergeAnimation == null) return;
        _mergeAnimation.Advance(now);
        if (_mergeVisual == null) return;
        if (_dissolveSprites.Count != 0)
        {
            double progress = _mergeAnimation.Eased;
            if (_morphBackground != null) _morphBackground.Opacity = _morphBackgroundFrom * (1 - progress);
            foreach (var (visual, start, to) in _dissolveSprites.Values)
                PlaceSprite(visual, start.Mix(to, progress), 0, 0);
            return;
        }
        if (_shownMerge == null) return;
        var id = (_morphResult ?? _shownMerge).Value;
        int index = 0;
        while (index < _tiles.Count && _tiles[index].Id != id) index++;
        if (index == _tiles.Count) return;
        var motion = Motion(false, id, index, _metrics);
        var origin = ShortcutGrid.TransformToVisual(DragLayer).TransformPoint(new Point());
        double left = origin.X + motion.X;
        double top = origin.Y + motion.Y - ((_topScrollViewer ?? FindVisual<ScrollViewer>(ShortcutGrid))?.VerticalOffset ?? 0);
        Canvas.SetLeft(_mergeVisual, left);
        Canvas.SetTop(_mergeVisual, top);
        if (_morphSelection)
        {
            var clip = GridHost.TransformToVisual(DragLayer).TransformPoint(new Point());
            _mergeVisual.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(clip.X - left, clip.Y - top,
                _metrics.Columns * _metrics.CellWidth, TopContentHeight) };
        }

        double icon = _metrics.PictureSize();
        double x = left + (_metrics.CellWidth - icon) / 2, y = top + _metrics.IconCenterY - icon / 2;
        double amount = _mergeAnimation.Eased;
        if (_morphBackground != null) _morphBackground.Opacity = _morphSelection
            ? _morphBackgroundFrom + (1 - _morphBackgroundFrom) * amount : _morphExisting ? 1 : amount;
        IconPose Thumbnail(ThumbnailSlot slot) => new(x + slot.X, y + slot.Y, slot.Size);
        if (_morphTargetIcon != null)
            PlaceSprite(_morphTargetIcon, new IconPose(x, y, icon)
                .Mix(Thumbnail(FolderThumbnailLayout.Slot(icon, 0, _morphMemberCount)), amount), left, top);
        foreach (var resident in _morphResidents)
            PlaceSprite(resident.Visual, Thumbnail(resident.From).Mix(Thumbnail(resident.To), amount), left, top);
        foreach (var sprite in _morphSources)
        {
            var destination = !_mergeAnimation.Forward ? DragPose(sprite.Id) : sprite.Slot < 0
                ? new IconPose(x + .38 * icon, y + .38 * icon, .24 * icon, 0)
                : Thumbnail(FolderThumbnailLayout.Slot(icon, sprite.Slot, _morphMemberCount));
            sprite.Current = sprite.From.Mix(destination, MergeAnimation.Ease(_mergeAnimation.Fraction));
            if (sprite.EntersView && sprite.Slot < 0)
                sprite.Current = sprite.Current with { Alpha = .7 * Math.Sin(Math.PI * amount) };
            PlaceSprite(sprite.Visual, sprite.Current, left, top);
        }
    }

    private void CommitMergeVisual(Guid result)
    {
        if (_mergeAnimation == null || _shownMerge == null) return;
        int index = _tiles.ToList().FindIndex(t => t.Id == _shownMerge);
        if (index >= 0 && result != _shownMerge)
        {
            var before = Motion(false, _shownMerge.Value, index, _metrics);
            _slotMotions[(false, result)] = new(before.X, before.Y);
        }
        _morphResult = result;
        _morphCommitted = true;
        HoldMorph();
    }

    private void AdvanceMerge()
    {
        if (_mergeAnimation == null)
        {
            if (_morphHold != null) RemoveMergeVisual();
            return;
        }
        double now = _clock.Elapsed.TotalMilliseconds;
        if (_morphHold != null && now - _morphHoldSince >= DrawerSettings.FormFolderAnimationMs * 3)
        {
            PrototypeLog.Write("merge transition: timed out");
            _layoutDirty = true;
            RemoveMergeVisual();
            return;
        }
        PositionMergeVisual(now);
        if (_mergeAnimation.Finished && (!_mergeAnimation.Forward || _morphCommitted))
        {
            if (_morphCommitted || _morphEnding) _layoutDirty = true;
            RemoveMergeVisual();
        }
    }

    private void RemoveMergeVisual()
    {
        ReleaseHold(ref _morphHold);
        var visual = _mergeVisual;
        var target = _morphResult ?? _shownMerge;
        bool dissolved = _dissolveSprites.Count != 0;
        _mergeVisual = null;
        _shownMerge = _morphResult = null;
        _mergeAnimation = null;
        _morphCommitted = _morphEnding = _morphSelection = false;
        _historyMorph = false;
        _historyAnimatingFolder = null;
        _morphSources = Array.Empty<MorphSource>();
        _morphResidents = [];
        _morphTargetIcon = null;
        _morphBackground = null;
        _dissolveSprites.Clear();
        if (visual != null) DragLayer.Children.Remove(visual);
        if (target != null && _tiles.FirstOrDefault(tile => tile.Id == target) is { } tile &&
            ShortcutGrid.ContainerFromItem(tile) is UIElement container) container.Opacity = 1;
        if (dissolved && !_closed) ApplyMotions(ShortcutGrid, _tiles, false, _metrics);
        if (_dragGhost != null) _dragGhost.Opacity = .92;
    }

    private void ClearDragVisual()
    {
        RemoveMergeVisual();DragLayer.Children.Clear();_dragGhost=null;
    }
    private void LandDraggedTiles(bool landed)
    {
        if(_pressedTile==null || _dragGhost==null) return;
        if (_morphCommitted) return;
        var top = _tiles.ToArray();
        foreach (Guid id in _dragBatch)
        {
            int index = Array.FindIndex(top, t => t.Id == id);
            bool member = index < 0;
            if (member) index = Array.FindIndex(_members, t => t.Id == id);
            if (index < 0 || (member ? _memberGrid : ShortcutGrid) is not { } grid) continue;
            var metrics = member ? MemberMetrics() : _metrics;
            var tile = (member ? _members : top)[index];
            var origin = grid.TransformToVisual(DragLayer).TransformPoint(new Point());
            var pose = _morphSources.FirstOrDefault(sprite => sprite.Id == id)?.Current ?? DragPose(id);
            var motion = Motion(member, id, index, metrics);
            motion.X = pose.X + pose.Size / 2 - origin.X - metrics.CellWidth / 2;
            motion.Y = pose.Y + pose.Size / 2 - origin.Y - metrics.TilePadding - tile.IconSize / 2 + GridOffset(grid);
            motion.Vx = motion.Vy = 0;
            if (landed)
            {
                tile.SetIconScale(pose.Size / tile.PictureSize);
                AnimateIconScale(tile, 1);
            }
        }
    }
    private void UpdateDragPointerAndMotion(double dt)
    {
        StepIconScales();
        if (!_internalDrag && _pressedTile != null && _dragGrid != null)
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            if ((GetAsyncKeyState(1) & 0x8000) == 0)
            {
                if (now - (_itemButtonUpSince ??= now) > 100) CancelItemDrag();
            }
            else
            {
                _itemButtonUpSince = null;
                if (GetCursorPos(out var cursor) && DragPoint(cursor, _dragGrid, out var point)) TryStartDrag(point);
            }
        }
        bool slotsMoving = SlotsMoving();
        if (slotsMoving)
            foreach (var motion in _slotMotions.Values)
                if (motion.IsMoving) motion.Step(dt);
        bool tileMotionActive = slotsMoving || _internalDrag || _externalDragging || _mergeAnimation != null ||
            _historyFadeGrid != null || _folderAnimating || _folderReturning || PageTurning;
        AdvanceHistoryFades();
        if (_tileVisualsDirty || tileMotionActive || _tileMotionWasActive || _paintedCutItems != _cutItems)
        {
            _tileVisualsDirty = false;
            _paintedCutItems = _cutItems;
            ApplyMotions(ShortcutGrid, _tiles, false, _metrics);
            if (_memberGrid != null) ApplyMotions(_memberGrid, _members, true, MemberMetrics());
        }
        _tileMotionWasActive = tileMotionActive;
        AdvanceMerge();
    }
    private void ApplyMotions(GridView grid,IReadOnlyList<ShortcutTile> tiles,bool member,LayoutMetrics metrics)
    {
        using var sample = IdlePerformance.Measure(IdlePerformance.Work.TilePass);
        int realized = 0;
        for(int i=0;i<tiles.Count;i++)
        {
            if(grid.ContainerFromIndex(i) is not GridViewItem container) continue;
            realized++;
            if (container.MinWidth != 0) container.MinWidth = 0;
            if (container.MinHeight != 0) container.MinHeight = 0;
            if (member)
            {
                if (container.Width != metrics.CellWidth) container.Width = metrics.CellWidth;
                if (container.Height != metrics.CellHeight) container.Height = metrics.CellHeight;
            }
            var tile=tiles[i]; var basePoint=Slot(i,metrics);
            if(_slotMotions.TryGetValue((member,tile.Id),out var motion))
            {
                if(container.RenderTransform is not TranslateTransform transform) container.RenderTransform=transform=new TranslateTransform();
                transform.X=motion.X-basePoint.X; transform.Y=motion.Y-basePoint.Y;
            }
            else if (container.RenderTransform is TranslateTransform) container.RenderTransform = null;
            if (container.IsTabStop == tile.IsEmpty) container.IsTabStop = !tile.IsEmpty;
            bool hiddenByDrop = _internalDrag && (_dragBatch.Contains(tile.Id) ||
                ReferenceEquals(grid, _dragGrid) && _preview?.HiddenItem == tile.Id);
            double opacity = tile.IsEmpty || hiddenByDrop || (!member && _dissolveSprites.ContainsKey(tile.Id)) ||
                (_morphResult ?? _shownMerge) == tile.Id ||
                (_morphEnding && _morphSources.Any(source => source.Id == tile.Id)) ? 0 : IsCutTile(tile) ? .42 : 1;
            opacity *= HistoryItemOpacity(grid, tile.Id);
            if (container.Opacity != opacity) container.Opacity = opacity;
        }
        IdlePerformance.Count(IdlePerformance.Work.TileSlotsVisited, tiles.Count);
        IdlePerformance.Count(IdlePerformance.Work.TilesRealized, realized);
    }
}
