using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Drawer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Drawer;
public sealed partial class MainWindow
{
    private Guid? _openFolder;
    private GridView? _memberGrid;
    private Grid? _folderOverlay;
    private Border? _folderPanel;
    private TextBlock? _folderTitle;
    private TextBox? _folderEditor;
    private Grid? _folderHeading;
    private FolderLayout? _folderLayout;
    private ShortcutTile[] _members = Array.Empty<ShortcutTile>();
    private readonly ObservableCollection<ShortcutTile> _memberTiles = new();

    private void OpenFolder(ShortcutTile tile)
    {
        if (_openFolder == tile.Id || !FinishNameEdit(true)) return;
        CloseFolder();
        _folderOrigin = TilePictureRect(ShortcutGrid, tile);
        _openFolder = tile.Id;
        _folderOverlay = new Grid { Margin = new Thickness(-_metrics.Padding) };
        Grid.SetRowSpan(_folderOverlay, 2);
        var backdrop = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        backdrop.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(backdrop).Properties.IsLeftButtonPressed) e.Handled = true;
        };
        backdrop.PointerReleased += (_, e) =>
        {
            if (e.GetCurrentPoint(backdrop).Properties.PointerUpdateKind != Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased)
                return;
            e.Handled = true;
            if (!_internalDrag) ReturnFolder();
        };
        _folderOverlay.Children.Add(backdrop);
        _folderPanel = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Background = DrawerPalette.Brush("FolderPanel"),
            BorderBrush = DrawerPalette.Brush("Outline"), BorderThickness = new Thickness(1)
        };
        _folderOverlay.Children.Add(_folderPanel);
        _folderHeading = new Grid
        {
            Background = DrawerPalette.Brush("Transparent"),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        _folderOverlay.Children.Add(_folderHeading);
        _folderTitle = new TextBlock
        {
            FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = DrawerPalette.Brush("Foreground"), VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1
        };
        _folderTitle.DoubleTapped += (_, e) =>
        {
            if (_clock.Elapsed.TotalMilliseconds >= _suppressLaunchUntil) BeginNameEdit(tile.Id);
            e.Handled = true;
        };
        _folderHeading.Children.Add(_folderTitle);
        _folderEditor = new TextBox { Visibility = Visibility.Collapsed, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(2, 0, 2, 0), FontSize = 11, TextAlignment = TextAlignment.Center };
        _folderEditor.KeyDown += Name_KeyDown;
        _folderEditor.LostFocus += Name_LostFocus;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_folderEditor, UiText.Get("FolderNameEditor"));
        WireNameComposition(_folderEditor);
        _folderHeading.Children.Add(_folderEditor);
        _memberGrid = new GridView
        {
            ItemTemplate = ShortcutGrid.ItemTemplate, ItemContainerStyle = ShortcutGrid.ItemContainerStyle,
            ItemsPanel = (ItemsPanelTemplate)GridHost.Resources["FolderItemsPanel"], ItemsSource = _memberTiles,
            ItemContainerTransitions = new TransitionCollection(),
            Padding = new Thickness(0, 0, FolderLayout.ScrollGutter, 0),
            SelectionMode = ListViewSelectionMode.Single, VerticalAlignment = VerticalAlignment.Top, AllowDrop = true,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_memberGrid, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollMode(_memberGrid, ScrollMode.Enabled);
        ScrollViewer.SetHorizontalScrollMode(_memberGrid, ScrollMode.Disabled);
        _memberGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Sort_Pressed), true);
        _memberGrid.LayoutUpdated += TileLayoutUpdated;
        _memberGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Sort_KeyDown), true);
        _memberGrid.Loaded += (_, _) =>
        {
            SizeFolder();
            if (NativeMethods.GetForegroundWindow() == _hwnd) _memberGrid?.Focus(FocusState.Programmatic);
        };
        _folderPanel.Child = _memberGrid;
        RootGrid.Children.Insert(RootGrid.Children.IndexOf(DragLayer), _folderOverlay);
        ShortcutGrid.IsHitTestVisible = false;
        ShortcutGrid.IsTabStop = false;
        GearButton.IsTabStop = false;
        AddButton.IsTabStop = false;
        RefreshFolder();
        _interaction.KeepOpen();
        _ = AnimateFolder(true);
    }

    private LayoutMetrics MemberMetrics() => _folderLayout?.Grid ?? _metrics;

    private FolderInsertionSequence MemberSequence(ShortcutGroup group, Guid[] batch, DropTarget? shown)
    {
        var existing = shown?.MemberPlacement?.Sequence;
        return existing?.Matches(group.Id, group.Members, batch) == true
            ? existing : new FolderInsertionSequence(group.Id, group.Members, batch);
    }

    // The point is in the member grid's viewport, in DIPs. The layout adds
    // the current ScrollViewer offset exactly once.
    private DropTarget? CaptureMemberDrop(Windows.Foundation.Point point, DropKind kind, Guid[] batch, DropTarget? shown)
    {
        if (_memberGrid == null || _folderLayout == null || _folderReturning || _openFolder is not Guid id ||
            _store.Groups.FirstOrDefault(group => group.Id == id) is not { } folder ||
            FindVisual<ScrollViewer>(_memberGrid) is not { } scroll) return null;
        var sequence = MemberSequence(folder, batch, shown);
        var landing = sequence.Hit(point.X, point.Y, scroll.VerticalOffset,
            Math.Min(_memberGrid.ActualHeight, scroll.ViewportHeight), MemberMetrics(), shown?.MemberPlacement);
        return landing == null ? null : new(_drawerId, _topPage, kind, landing.Index, id, landing);
    }

    private void SetMemberPreviewExtent(int itemCount)
    {
        if (_memberGrid == null) return;
        var metrics = MemberMetrics();
        int Rows(int count) => (count + metrics.Columns - 1) / metrics.Columns;
        double height = Math.Max(0, Rows(itemCount) - Rows(_memberGrid.Items.Count)) * metrics.CellHeight;
        if (height == 0) _memberGrid.Footer = null;
        else if (_memberGrid.Footer is Border footer) footer.Height = height;
        else _memberGrid.Footer = new Border { Height = height };
    }

    private double FolderHeadroom => _metrics.Rows == 1 ? AutomaticLayout.TopGap(ShowNavigation) : 0;

    private void SizeFolder()
    {
        using var sample = IdlePerformance.Measure(IdlePerformance.Work.SizeFolder);
        if (_memberGrid == null || _folderPanel == null || _openFolder == null || _folderReturning) return;
        using var fit = IdlePerformance.Measure(IdlePerformance.Work.FolderFit);
        var host = GridHost.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point());
        var overlay = _folderOverlay!.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point());
        double viewportTop = host.Y - FolderHeadroom;
        double availableHeight = GridHost.ActualHeight > 0 ? GridHost.ActualHeight : _metrics.Rows * _metrics.CellHeight;
        double availableWidth = GridHost.ActualWidth > 0 ? GridHost.ActualWidth : _metrics.Columns * _metrics.CellWidth + 14;
        var layout = FolderLayout.Fit(_metrics, availableWidth, availableHeight + FolderHeadroom);
        bool metricsChanged = _folderLayout?.Grid != layout.Grid;
        _folderLayout = layout;
        var metrics = layout.Grid;
        _folderOverlay.Margin = new Thickness(-_metrics.Padding);
        _folderPanel.Margin = new Thickness(host.X - overlay.X + layout.Left, viewportTop - overlay.Y + layout.Top, 0, 0);
        _folderPanel.Width = layout.Width;
        _folderPanel.Height = layout.Height;
        _folderPanel.Padding = new Thickness(layout.Inset - 1);
        _folderPanel.CornerRadius = new CornerRadius(DrawerPalette.PanelRadius(metrics.Effective.IconSize, layout.Width, layout.Height));
        _folderHeading!.Margin = new Thickness(host.X - overlay.X + layout.Left, viewportTop - overlay.Y + layout.TitleTop, 0, 0);
        _folderHeading.Width = layout.Width;
        _folderHeading.Height = layout.TitleHeight;
        _memberGrid.Height = metrics.Height;
        if (_memberGrid.ItemsPanelRoot is VariableSizedWrapGrid wrap)
        {
            wrap.HorizontalAlignment = HorizontalAlignment.Left;
            wrap.Width = metrics.Columns * metrics.CellWidth;
            wrap.MaximumRowsOrColumns = metrics.Columns;
            wrap.ItemWidth = metrics.CellWidth;
            wrap.ItemHeight = metrics.CellHeight;
        }
        if (metricsChanged && _members.Length > 0 && !_internalDrag)
        {
            RefreshFolderTiles();
            SetPreviewTargets(_memberGrid, _members.Select(tile => tile.Id).ToArray());
        }
    }

    private void RefreshFolder()
    {
        if (_openFolder == null || _folderReturning) return;
        var group = _store.Groups.FirstOrDefault(g => g.Id == _openFolder);
        if (group == null) { CloseFolder(); return; }
        SizeFolder();
        RefreshFolderTiles();
    }

    private void RefreshFolderTiles()
    {
        if (_openFolder == null || _store.Groups.FirstOrDefault(g => g.Id == _openFolder) is not { } group) return;
        _tileVisualsDirty = true;
        var metrics = MemberMetrics();
        var old = _members.ToDictionary(t => t.Id);
        _members = group.Members.Select(id =>
        {
            var entry = _store.Items.Single(e => e.Id == id);
            return old.TryGetValue(id, out var tile) && tile.ShowNames == metrics.Effective.ShowNames &&
                tile.RebindEntry(entry)
                ? tile : new ShortcutTile(entry, metrics.Effective.IconSize, metrics.Effective.ShowNames);
        }).ToArray();
        foreach (var member in _members)
        {
            member.ResizeIcon(metrics.Effective.IconSize);
            member.ApplyMetrics(metrics, member: true);
        }
        UpdateTileList(_memberTiles, _members);
        RefreshSelectionMarks();
        if (_folderTitle != null) { _folderTitle.Text = group.Name; ToolTipService.SetToolTip(_folderTitle, group.Name); }
    }

    private void CloseFolder()
    {
        if (_historyFadeGrid != null && _historyFadeGrid == _memberGrid) ClearHistoryFades();
        if (_boxSelection?.Grid == _memberGrid && _boxSelection != null) EndSelectionBox(cancel: true);
        ClearItemSelection();
        if (_nameFolder != null) FinishNameEdit(false);
        CancelFolderAnimation();
        if (_folderOverlay != null) RootGrid.Children.Remove(_folderOverlay);
        if (_memberGrid != null) _memberGrid.LayoutUpdated -= TileLayoutUpdated;
        _folderOverlay = null; _folderPanel = null; _folderTitle = null; _folderEditor = null; _folderHeading = null;
        _memberGrid = null; _folderLayout = null; _openFolder = null; _members = Array.Empty<ShortcutTile>();
        _memberTiles.Clear();
        ClearFolderBackdrop();
        ShortcutGrid.IsHitTestVisible = true;
        ShortcutGrid.IsTabStop = true;
        GearButton.IsTabStop = true;
        AddButton.IsTabStop = true;
        foreach (var key in _slotMotions.Keys.Where(k => k.Member).ToArray()) _slotMotions.Remove(key);
    }

    private void LoadFolderPreview(Grid parent, ShortcutTile tile, double? requestedSize = null)
    {
        if (tile.Group == null) return;
        double size = requestedSize ?? tile.IconSize;
        var radius = new CornerRadius(FolderThumbnailLayout.Radius(size));
        var preview = new Grid { CornerRadius = radius, Width = size, Height = size, Tag = tile,
            Background = DrawerPalette.Brush("Folder"), IsHitTestVisible = false };
        var thumbnails = new Canvas { Width = size, Height = size };
        preview.Children.Add(thumbnails);
        parent.Children.Add(preview);
        int i = 0;
        foreach (Guid id in tile.Group.Members.Take(FolderThumbnailLayout.Capacity))
        {
            var slot = FolderThumbnailLayout.Slot(size, i++, tile.Group.Members.Count);
            var icon = MemberIconVisual(id, slot.Size);
            Canvas.SetLeft(icon, slot.X);
            Canvas.SetTop(icon, slot.Y);
            thumbnails.Children.Add(icon);
        }
    }

    private static void ResizeFolderPreview(Grid parent, ShortcutTile tile)
    {
        if (tile.Group is not { } group ||
            parent.Children.OfType<Grid>().FirstOrDefault(child => ReferenceEquals(child.Tag, tile)) is not { } preview ||
            preview.Children[0] is not Canvas thumbnails) return;

        double size = tile.IconSize;
        preview.Width = preview.Height = thumbnails.Width = thumbnails.Height = size;
        preview.CornerRadius = new CornerRadius(FolderThumbnailLayout.Radius(size));
        for (int i = 0; i < thumbnails.Children.Count; i++)
        {
            var icon = (Grid)thumbnails.Children[i];
            var slot = FolderThumbnailLayout.Slot(size, i, group.Members.Count);
            icon.Width = icon.Height = slot.Size;
            Canvas.SetLeft(icon, slot.X);
            Canvas.SetTop(icon, slot.Y);
            if (icon.Children[0] is FontIcon fallback) fallback.FontSize = slot.Size * .8;
        }
    }

    private Grid MemberIconVisual(Guid id, double size, ShortcutStore? source = null)
    {
        var host = new Grid { Width = size, Height = size, IsHitTestVisible = false };
        if (_iconCache.TryGetValue(id, out var cached))
        {
            host.Children.Add(new Image { Source = cached.Image, Stretch = Stretch.Uniform });
            return host;
        }
        var store = source ?? _store;
        var entry = store.Items.FirstOrDefault(item => item.Id == id);
        var fallback = new FontIcon { Glyph = ShortcutTile.GlyphFor(entry), FontSize = size * .8, Foreground = DrawerPalette.Brush("SelectionFill") };
        host.Children.Add(fallback);
        if (entry != null) _ = FillMemberIcon(host, fallback, store, entry);
        return host;
    }

    private async Task FillMemberIcon(Grid host, FontIcon fallback, ShortcutStore store, ShortcutEntry entry)
    {
        if (await GetIcon(store, entry, host.Width) is not { } bitmap || _closed) return;
        host.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform });
        fallback.Visibility = Visibility.Collapsed;
    }
    private void DissolveFolder(ShortcutTile tile)
    {
        if (_importing || _internalDrag || _externalDragging || _formingFolder || _morphCommitted || PageTurning ||
            !tile.IsGroup || !FinishNameEdit(true)) return;
        if (_store.Groups.FirstOrDefault(group => group.Id == tile.Id) is not { } folder) return;
        try
        {
            var origin = CaptureDissolveOrigin(tile);
            using (BeginDataHistory()) _store.Dissolve(folder.Id, PageCapacity);
            ClearDissolveMemberPositions(folder.Members);
            RefreshTiles();
            if (origin is { } from)
            {
                try { BeginDissolveVisual(folder.Members, from); }
                catch (Exception ex)
                {
                    RemoveMergeVisual();
                    PrototypeLog.Write("folder dissolve animation: " + ex.Message);
                }
            }
            Status(UiText.Get("FolderDissolved"));
        }
        catch (Exception ex) { ErrorStatus(UiText.Get("OperationFailed", ex.Message)); }
    }
    private void MoveShortcutOutOfFolder(ShortcutTile tile)
    {
        if (_importing || _internalDrag || tile.IsGroup) return;
        var group = _store.Groups.FirstOrDefault(g => g.Members.Contains(tile.Id)); if (group == null) return;
        try { using (BeginDataHistory()) _store.MoveOut(tile.Id,_store.TopSlots.IndexOf(group.Id)+1, PageCapacity); RefreshTiles(); Status(UiText.Get("MovedOutOfFolder")); } catch(Exception ex) { ErrorStatus(UiText.Get("OperationFailed", ex.Message)); }
    }
}
