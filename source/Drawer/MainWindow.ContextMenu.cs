using System;
using System.Linq;
using Drawer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using static Drawer.NativeMethods;

namespace Drawer;

internal enum ContextMenuCommand
{
    DeleteDrawer = 4,
    Cut = 70, Copy, Rename, RestoreName, CreateFolder, DissolveFolder, MoveOutOfFolder,
    DeleteItems, ConfirmDeleteItems, Paste, AddItems, ClearEmptySlots
}

public sealed partial class MainWindow
{
    private void DropSurface_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        _lastNameClick = null;
        if (e.Handled || EditingDataBlocked || _displayRefreshPending || _pageNeedsSnap || IsScrollControl(e.OriginalSource) ||
            _clock.Elapsed.TotalMilliseconds < _suppressLaunchUntil || !GetCursorPos(out var anchor)) return;
        for (var node = e.OriginalSource as DependencyObject; node != null && node != DropSurface; node = VisualTreeHelper.GetParent(node))
            if (node is Button or TextBox || node == PageRail || node == _folderHeading) return;
        if (!FinishNameEdit(true)) return;
        var grid = _openFolder == null ? ShortcutGrid : _memberGrid;
        if (grid == null) return;
        if (_openFolder != null && (_folderLayout is not { } folder || !MemberPanelBounds(folder).Contains(e.GetPosition(GridHost)))) return;

        var point = e.GetPosition(grid);
        e.Handled = true;
        if (HitIcon(grid, point) is { } tile)
        {
            var items = _selection.Contains(tile.Id) ? CurrentItems.Where(item => _selection.Contains(item.Id)).ToArray() : [tile];
            DropTarget? cutTarget = _openFolder == null && SameDrawerCut != null ? ClipboardTarget(grid, point) : null;
            ShowItemContextMenu(items, anchor, cutTarget);
        }
        else ShowBlankContextMenu(anchor, ClipboardTarget(grid, point));
    }

    private static void AppendContextAction(DrawerMenu menu, ContextMenuCommand command, string key, bool enabled = true, bool danger = false) =>
        menu.Add((int)command, UiText.Get(key), enabled, danger: danger);

    private bool ContextMenuStillCurrent(Guid drawer, Guid? folder) =>
        !EditingDataBlocked && !_displayRefreshPending && _drawerId == drawer && _openFolder == folder;

    private async void ShowItemContextMenu(ShortcutTile[] items, Point anchor, DropTarget? cutTarget)
    {
        if (items.Length == 0) return;
        var drawer = _drawerId;
        var folder = _openFolder;
        var ids = items.Select(item => item.Id).ToArray();
        var single = items.Length == 1 ? items[0] : null;
        int folders = items.Count(item => item.IsGroup);
        var menu = new DrawerMenu();
        AppendContextAction(menu, ContextMenuCommand.Cut, "Cut");
        AppendContextAction(menu, ContextMenuCommand.Copy, "Copy");
        if (cutTarget != null) AppendContextAction(menu, ContextMenuCommand.Paste, "Paste");
        if (single != null)
        {
            AppendContextAction(menu, ContextMenuCommand.Rename, "Rename");
            if (single.Entry?.DisplayName != null) AppendContextAction(menu, ContextMenuCommand.RestoreName, "RestoreShortcutName");
            if (single.IsGroup) AppendContextAction(menu, ContextMenuCommand.DissolveFolder, "DissolveFolder");
            else if (folder != null) AppendContextAction(menu, ContextMenuCommand.MoveOutOfFolder, "MoveOutOfFolder");
        }
        else if (folder == null) AppendContextAction(menu, ContextMenuCommand.CreateFolder, "CreateFolder");

        menu.Separate();
        if (single != null || folders != 0) AppendContextAction(menu, ContextMenuCommand.DeleteItems, "Delete", danger: true);
        else
        {
            var confirm = new DrawerMenu();
            confirm.Add((int)ContextMenuCommand.ConfirmDeleteItems, UiText.Get("ConfirmDeleteCount", UiText.Items(ids.Length)), danger: true);
            menu.AddSubmenu(UiText.Get("Delete"), confirm, danger: true);
        }

        try
        {
            var command = (ContextMenuCommand)await ShowOwnedMenu(menu, anchor);
            if (command == 0 || !ContextMenuStillCurrent(drawer, folder)) return;
            switch (command)
            {
                case ContextMenuCommand.Cut:
                case ContextMenuCommand.Copy:
                    PutClipboard(drawer, folder, ids, cut: command == ContextMenuCommand.Cut);
                    break;
                case ContextMenuCommand.Paste when cutTarget != null:
                    await PasteClipboard(cutTarget);
                    break;
                case ContextMenuCommand.Rename when single != null:
                    QueueTileNameEdit(drawer, folder, single.Id);
                    break;
                case ContextMenuCommand.RestoreName when single != null:
                    RestoreShortcutName(drawer, folder, single.Id);
                    break;
                case ContextMenuCommand.DissolveFolder when single != null:
                    DissolveFolder(single);
                    break;
                case ContextMenuCommand.MoveOutOfFolder when single != null:
                    MoveShortcutOutOfFolder(single);
                    break;
                case ContextMenuCommand.DeleteItems:
                case ContextMenuCommand.ConfirmDeleteItems:
                    await DeleteSelectedItems(drawer, folder, ids, folders);
                    break;
                case ContextMenuCommand.CreateFolder when folder == null:
                    if (folders > 0 && !await ConfirmSelectionAction(ids.Length, folders, combine: true)) return;
                    if (ContextMenuStillCurrent(drawer, folder)) await CreateSelectedFolder(drawer, items);
                    break;
            }
        }
        catch (Exception ex) { if (!_closed) ErrorStatus(UiText.Get("OperationFailed", ex.Message)); }
    }

    private async void ShowBlankContextMenu(Point anchor, DropTarget? target)
    {
        var drawer = _drawerId;
        var folder = _openFolder;
        if (folder != null && target == null) return;
        var menu = new DrawerMenu();
        if (folder == null)
        {
            AppendContextAction(menu, ContextMenuCommand.ClearEmptySlots, "ClearEmptySlots", _store.TopSlots.Contains(Guid.Empty));
            AppendContextAction(menu, ContextMenuCommand.AddItems, "AddItems");
        }
        AppendContextAction(menu, ContextMenuCommand.Paste, "Paste", target != null);
        if (folder == null)
        {
            menu.Separate();
            menu.Add(ToggleNavigationCommand, UiText.Get("ShowNavigation"), isChecked: ShowNavigation);
            menu.AddSubmenu(UiText.Get("Settings"), CreateDrawerSettingsMenu());
        }

        try
        {
            int command = await ShowOwnedMenu(menu, anchor);
            if (command == 0 || !ContextMenuStillCurrent(drawer, folder)) return;
            switch ((ContextMenuCommand)command)
            {
                case ContextMenuCommand.Paste when target != null:
                    await PasteClipboard(target);
                    break;
                case ContextMenuCommand.AddItems:
                    await PickShortcutsToImport();
                    break;
                case ContextMenuCommand.ClearEmptySlots:
                    try
                    {
                        using (BeginDataHistory()) _store.ClearEmptySlots();
                        RefreshTiles();
                    }
                    catch (Exception ex) { ErrorStatus(UiText.Get("ClearEmptySlotsFailed", ex.Message)); }
                    break;
                default:
                    if (folder == null) RunDrawerSettingsCommand(drawer, command);
                    break;
            }
        }
        catch (Exception ex)
        {
            if (!_closed) ErrorStatus(UiText.Get("OperationFailed", ex.Message));
        }
    }
}
