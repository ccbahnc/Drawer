using Drawer.Core;
using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using static Drawer.NativeMethods;

namespace Drawer;
public sealed partial class MainWindow
{
    private bool _editingName, _composingName, _imeEnter, _finishNameAfterComposition;
    private Guid _nameDrawer;
    private Guid? _nameFolder;
    private ShortcutTile? _nameTile;
    private Grid? _tileNameSlot;
    private string _nameOriginal = "";
    private TextBox? _activeNameEditor;
    private TextBlock? _activeNameLabel;
    private IDisposable? _nameHold;
    private (GridView Grid, Guid Drawer, Guid? Folder, Guid Tile, Windows.Foundation.Point Point, double At)? _lastNameClick;

    private void InitializeNameEditing()
    {
        WireNameComposition(NameEditor);
        WindowSurface.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (!_editingName || e.OriginalSource is not DependencyObject node) return;
            for (var p = node; p != null; p = VisualTreeHelper.GetParent(p))
                if (ReferenceEquals(p, _activeNameEditor)) return;
            if (_composingName)
            {
                _finishNameAfterComposition = true;
                (_memberGrid ?? ShortcutGrid).Focus(FocusState.Programmatic);
                e.Handled = true;
                return;
            }
            bool editingTile = _nameTile != null;
            if (!FinishNameEdit(true) || editingTile) e.Handled = true;
        }), true);
    }

    private void WireNameComposition(TextBox editor)
    {
        editor.TextCompositionStarted += (_, _) => { if (editor == _activeNameEditor) { _composingName = true; _imeEnter = false; } };
        editor.TextCompositionEnded += (_, _) =>
        {
            if (editor != _activeNameEditor) return;
            _composingName = false;
            _imeEnter = (GetAsyncKeyState(0x0D) & 0x8000) != 0;
            if ((GetAsyncKeyState(0x1B) & 0x8000) != 0) MarkEscapeHandled();
            if (_finishNameAfterComposition) DispatcherQueue.TryEnqueue(() =>
            {
                if (editor == _activeNameEditor && _finishNameAfterComposition && !_composingName)
                    FinishNameEdit(true);
            });
        };
        editor.KeyUp += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) _imeEnter = false; };
    }

    private void Name_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        EndNavigation(false);
        if (_clock.Elapsed.TotalMilliseconds >= _suppressLaunchUntil) BeginNameEdit(null);
        e.Handled = true;
    }

    private void RenameDrawerFromMenu(Guid id)
    {
        if (id == _drawerId && FinishNameEdit(true)) BeginNameEdit(null);
    }

    private TextBlock? HitTileName(GridView grid, Windows.Foundation.Point point)
    {
        int index = ReferenceEquals(grid, _memberGrid)
            ? MemberMetrics().HitIndex(point.X, point.Y, GridOffset(grid), grid.Items.Count, false)
            : TopCellAt(point.X, point.Y);
        if (index < 0 || index >= grid.Items.Count || grid.Items[index] is not ShortcutTile { ShowNames: true, IsEmpty: false } ||
            grid.ContainerFromIndex(index) is not GridViewItem container || FindTileNameSlot(container) is not { } slot ||
            slot.Children[0] is not TextBlock { Visibility: Visibility.Visible } label) return null;
        return TileNameContains(label, grid, point) ? label : null;
    }

    private static Grid? FindTileNameSlot(GridViewItem container) =>
        FindVisual<Grid>(container, element => element.Name == "ShortcutNameSlot");

    private static bool TileNameContains(TextBlock label, GridView grid, Windows.Foundation.Point point) =>
        label.ActualWidth > 0 && label.ActualHeight > 0 &&
        label.TransformToVisual(grid).TransformBounds(new Windows.Foundation.Rect(0, 0, label.ActualWidth, label.ActualHeight)).Contains(point);

    private void CompleteTileNameClick(BoxSelection box, Windows.Foundation.Point point)
    {
        var previous = _lastNameClick;
        _lastNameClick = null;
        if (box.NameLabel is not { DataContext: ShortcutTile tile, Visibility: Visibility.Visible } label ||
            !tile.ShowNames || !ReferenceEquals(box.Grid, _memberGrid ?? ShortcutGrid) ||
            box.Grid.ContainerFromItem(tile) is not GridViewItem || !TileNameContains(label, box.Grid, point)) return;
        double now = _clock.Elapsed.TotalMilliseconds;
        if (previous is { } last && ReferenceEquals(last.Grid, box.Grid) && last.Drawer == _drawerId &&
            last.Folder == _openFolder && last.Tile == tile.Id && now - last.At <= GetDoubleClickTime() &&
            Math.Abs(point.X - last.Point.X) <= SelectionBoxThreshold && Math.Abs(point.Y - last.Point.Y) <= SelectionBoxThreshold)
        {
            QueueTileNameEdit(_drawerId, _openFolder, tile.Id);
            return;
        }
        _lastNameClick = (box.Grid, _drawerId, _openFolder, tile.Id, point, now);
    }

    private void BeginNameEdit(Guid? folder)
    {
        if (folder == null && !ShowNavigation) return;
        if (_previewOnly || _navActive || _internalDrag || _externalDragging || _formingFolder || _morphCommitted || _folderAnimating || _settingsOpen || _pickerOpen || _importing) return;
        if (_editingName && !FinishNameEdit(true)) return;
        var editor = folder == null ? NameEditor : _folderEditor;
        var label = folder == null ? DrawerName : _folderTitle;
        if (editor == null || label == null || (folder != null && folder != _openFolder)) return;
        string original = folder is Guid id ? _store.Groups.Single(g => g.Id == id).Name : _preferences.Value.Drawers.Single(d => d.Id == _drawerId).Name;
        StartNameEdit(editor, label, folder, original);
    }

    private void StartNameEdit(TextBox editor, TextBlock label, Guid? folder, string original)
    {
        _lastNameClick = null;
        _nameDrawer = _drawerId; _nameFolder = folder;
        _nameOriginal = original;
        _activeNameEditor = editor; _activeNameLabel = label;
        _editingName = true; _finishNameAfterComposition = false; _nameHold = HoldOpen();
        label.Visibility = Visibility.Collapsed;
        editor.Visibility = Visibility.Visible;
        if (_nameTile == null || _nameTile.IsGroup) editor.MaxLength = folder == null && _nameTile == null ? 80 : 100;
        editor.Text = _nameOriginal;
        ToolTipService.SetToolTip(editor, null);
        if (_nameTile != null) ActivateContentInput();
        else SetForegroundWindow(_hwnd);
        editor.Focus(FocusState.Programmatic);
        editor.SelectAll();
    }

    private bool FinishNameEdit(bool save)
    {
        if (!_editingName || _activeNameEditor == null) return true;
        if (save && _composingName) return false;
        _finishNameAfterComposition = false;
        var editor = _activeNameEditor;
        var tile = _nameTile;
        try
        {
            string value = editor.Text.Trim();
            if (save && tile != null && value.Length > 0)
                SaveTileName(_nameDrawer, _nameFolder, tile, editor.Text);
            else if (save && value.Length > 0 && value != _nameOriginal)
            {
                if (_nameDrawer != _drawerId) throw new InvalidOperationException(UiText.Get("RenameTargetChanged"));
                using var history = BeginDataHistory();
                if (_nameFolder is Guid folder)
                {
                    var renamed = _store.RenameGroup(folder, value);
                    _tiles.FirstOrDefault(item => item.Id == folder)?.RebindGroup(renamed);
                    if (_folderTitle != null)
                    {
                        _folderTitle.Text = renamed.Name;
                        ToolTipService.SetToolTip(_folderTitle, renamed.Name);
                    }
                }
                else _preferences.Rename(_nameDrawer, value);
            }
            _editingName = false; _composingName = _imeEnter = false;
            ReleaseHold(ref _nameHold);
            _activeNameEditor = null; _nameFolder = null;
            _nameTile = null;
            editor.Visibility = Visibility.Collapsed;
            _activeNameLabel!.Visibility = Visibility.Visible;
            _activeNameLabel = null;
            editor.ClearValue(Control.BorderBrushProperty);
            if (tile != null)
            {
                _tileNameSlot?.Children.Remove(editor);
                _tileNameSlot = null;
                if (!_closed && GetForegroundWindow() == _hwnd)
                    (_memberGrid ?? ShortcutGrid).Focus(FocusState.Programmatic);
            }
            else
            {
                if (_openFolder != null && _memberGrid != null) _memberGrid.Focus(FocusState.Programmatic);
                RefreshTiles();
                UpdateNavigationCaption();
            }
            return true;
        }
        catch (Exception ex)
        {
            editor.BorderBrush = DrawerPalette.Brush("Danger");
            ToolTipService.SetToolTip(editor, ex.Message);
            return false;
        }
    }

    private ShortcutTile? TileNameTarget(Guid drawer, Guid? folder, Guid id)
    {
        if (_drawerId != drawer || _openFolder != folder || _selection.IsPartOfMultiSelection(id) || !_visible || EditingDataBlocked)
            return null;
        return CurrentItems.FirstOrDefault(tile => tile.Id == id);
    }

    private void QueueTileNameEdit(Guid drawer, Guid? folder, Guid id) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_editingName || TileNameTarget(drawer, folder, id) is not { } tile) return;
        var grid = _memberGrid ?? ShortcutGrid;
        if (grid.ContainerFromItem(tile) is not GridViewItem container ||
            FindTileNameSlot(container) is not { } slot ||
            slot.Children[0] is not TextBlock label) return;
        var editor = new TextBox
        {
            MinWidth = 0, MinHeight = 0, Padding = new Thickness(2, 0, 2, 0),
            BorderThickness = new Thickness(1), FontSize = tile.NameFontSize,
            TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            AcceptsReturn = false, Visibility = Visibility.Collapsed
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(editor, UiText.Get(tile.IsGroup ? "FolderNameEditor" : "Rename"));
        WireNameComposition(editor);
        editor.KeyDown += Name_KeyDown;
        editor.LostFocus += Name_LostFocus;
        slot.Children.Add(editor);
        _nameTile = tile;
        _tileNameSlot = slot;
        StartNameEdit(editor, label, folder, tile.Name);
        if (editor.FocusState == FocusState.Unfocused && ReferenceEquals(_activeNameEditor, editor))
        {
            slot.UpdateLayout();
            if (editor.Focus(FocusState.Programmatic)) editor.SelectAll();
            else FinishNameEdit(false);
        }
    });

    private void RestoreShortcutName(Guid drawer, Guid? folder, Guid id)
    {
        if (_editingName || TileNameTarget(drawer, folder, id) is not { Entry.DisplayName: not null } tile) return;
        try { SaveTileName(drawer, folder, tile, null); }
        catch (Exception ex) { ErrorStatus(UiText.Get("OperationFailed", ex.Message)); }
    }

    private void SaveTileName(Guid drawer, Guid? folder, ShortcutTile tile, string? value)
    {
        if (drawer != _drawerId || folder != _openFolder || !CurrentItems.Contains(tile))
            throw new InvalidOperationException(UiText.Get("RenameTargetChanged"));
        if ((value == null && tile.Entry?.DisplayName == null) || value == tile.Name) return;
        using var history = BeginDataHistory();
        if (tile.IsGroup) tile.RebindGroup(_store.RenameGroup(tile.Id, value!));
        else tile.RebindEntry(_store.RenameShortcut(tile.Id, value));
    }

    private void Name_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && !_composingName && !_escapeHandled)
        { MarkEscapeHandled(); FinishNameEdit(false); e.Handled = true; }
        else if (e.Key == Windows.System.VirtualKey.Enter && !_composingName && !_imeEnter)
        { FinishNameEdit(true); e.Handled = true; }
    }

    private void Name_LostFocus(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _activeNameEditor) && !_composingName && !_menuOpen && !_trayMenuInProgress &&
            !(GetCursorPos(out var point) && IsTrayIconPoint(point))) FinishNameEdit(true);
    }
}
