using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Drawer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Point = Windows.Foundation.Point;
using static Drawer.NativeMethods;

namespace Drawer;

public sealed partial class MainWindow
{
    private const string ClipboardFormat = "Drawer.Shortcuts.v1";
    private readonly Guid _clipboardSession = Guid.NewGuid();
    private sealed record ClipboardItems(Guid Session, Guid Token, Guid Drawer, Guid? Folder, Guid[] Ids, bool Cut);
    private ClipboardItems? _cutItems;
    private uint _cutSequence;
    private bool _readingClipboard;
    private bool _committingCut;
    private bool _pasteRequiresFocus;
    private int _pasteGeneration;

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private void CheckClipboardOwnership()
    {
        using var sample = IdlePerformance.Measure(IdlePerformance.Work.ClipboardPoll);
        if (_cutItems != null && GetClipboardSequenceNumber() != _cutSequence) _cutItems = null;
        if (_pasteRequiresFocus && GetForegroundWindow() != _hwnd) CancelClipboardPaste();
    }

    private void CancelClipboardPaste()
    {
        _pasteRequiresFocus = false;
        _pasteGeneration++;
        _importCancellation?.Cancel();
    }

    private bool CanFinishPaste(int generation, DropTarget target) => generation == _pasteGeneration &&
        (!_pasteRequiresFocus || GetForegroundWindow() == _hwnd) && StillValidClipboardTarget(target);

    private bool IsCutTile(ShortcutTile tile) => _cutItems is { } cut && cut.Drawer == _drawerId && cut.Ids.Contains(tile.Id);

    private bool EditingDataBlocked => _exitRequested || _historyRestoring || _committingDrag || _committingCut || _morphCommitted || EditingDataSurfaceBlocked;
    private bool EditingDataSurfaceBlocked => !_storeReady || _previewOnly || _closed || _resize != null || _boxSelection != null || _pressedTile != null ||
        _internalDrag || _externalDragging || NavigationPressed || NavigationTransitionActive || _settingsOpen || _pickerOpen || _importing || _readingClipboard ||
        _formingFolder || _folderAnimating || PageTurning || _menuOpen || _confirmationWindow != null;

    private bool HandleEditShortcut(KeyRoutedEventArgs e)
    {
        if (_editingName || GetForegroundWindow() != _hwnd || (GetAsyncKeyState(0x11) & 0x8000) == 0 ||
            e.Key is not (VirtualKey.C or VirtualKey.X or VirtualKey.V or VirtualKey.Z or VirtualKey.Y)) return false;
        for (var node = e.OriginalSource as DependencyObject; node != null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            if (node is TextBox or PasswordBox or RichEditBox) return false;
        if (e.Key is VirtualKey.Z or VirtualKey.Y)
        {
            if (_historyRestoring || !EditingDataSurfaceBlocked && (!_morphCommitted || _historyMorph))
                HandleHistory(e.Key == VirtualKey.Y, repeated: e.KeyStatus.WasKeyDown);
            return true;
        }
        if (EditingDataBlocked || e.KeyStatus.WasKeyDown) return true;
        if (e.Key == VirtualKey.V)
        {
            var grid = _openFolder == null ? ShortcutGrid : _memberGrid;
            if (grid != null && GetCursorPos(out var cursor) && GetAncestor(WindowFromPoint(cursor), 2) == _hwnd &&
                _geometry.Visible(_edge, _interaction.Position).Contains(cursor.X, cursor.Y) &&
                DragPoint(cursor, grid, out var point) && ClipboardTarget(grid, point) is { } target)
                _ = PasteClipboard(target, requiresFocus: true);
        }
        else
        {
            var selected = CurrentItems.Where(t => _selection.Contains(t.Id)).ToArray();
            if (selected.Length == 0 && HoveredIcon() is { } tile) selected = new[] { tile };
            PutClipboard(_drawerId, _openFolder, selected.Select(tile => tile.Id).ToArray(), cut: e.Key == VirtualKey.X);
        }
        return true;
    }

    private void PutClipboard(Guid drawer, Guid? folder, Guid[] ids, bool cut)
    {
        if (ids.Length == 0 || drawer != _drawerId || folder != _openFolder) return;
        try
        {
            var items = new ClipboardItems(_clipboardSession, Guid.NewGuid(), drawer, folder, ids, cut);
            var package = new DataPackage { RequestedOperation = cut ? DataPackageOperation.Move : DataPackageOperation.Copy };
            package.SetData(ClipboardFormat, JsonSerializer.Serialize(items));
            Clipboard.SetContent(package);
            _cutSequence = GetClipboardSequenceNumber();
            _cutItems = cut ? items : null;
            if (cut) ClearItemSelection();
            Status(cut ? UiText.Get("CutResult", ids.Length) : UiText.Get("CopyResult", ids.Length));
        }
        catch (Exception ex) { ErrorStatus(UiText.Get("ClipboardWriteFailed", ex.Message)); }
    }

    private ClipboardItems? SameDrawerCut => _cutItems is { } cut && cut.Drawer == _drawerId &&
        GetClipboardSequenceNumber() == _cutSequence ? cut : null;

    private DropTarget? ClipboardTarget(GridView grid, Point point)
    {
        if (!DropSurfaceSettled || point.X < 0 || point.Y < 0) return null;
        if (_openFolder is Guid folder)
        {
            if (!ReferenceEquals(grid, _memberGrid) ||
                CaptureMemberDrop(point, DropKind.CopyMembers, Array.Empty<Guid>(), null) == null) return null;
            if (HitIcon(grid, point) != null) return null;
            var group = _store.Groups.Single(g => g.Id == folder);
            var sequence = MemberSequence(group, Array.Empty<Guid>(), null);
            var landing = new FolderInsertion(sequence, sequence.Candidates.Count);
            return new(_drawerId, _topPage, DropKind.CopyMembers, landing.Index, folder, landing);
        }
        int index = TopCellAt(point.X, point.Y);
        if (index >= 0 && SameDrawerCut != null) return TopDrop(DropKind.Place, index);
        if (!IsClipboardBlank(index)) return null;
        while (index > _topPage * PageCapacity && IsClipboardBlank(index - 1)) index--;
        return TopDrop(DropKind.Copy, index);
    }

    private bool IsClipboardBlank(int cell) => cell >= 0 && (cell >= _store.TopSlots.Count || _store.TopSlots[cell] == Guid.Empty);

    private bool StillValidClipboardTarget(DropTarget target)
    {
        if (_closed || !_visible || target.Drawer != _drawerId || target.Receiver != _openFolder) return false;
        if (target.MemberPlacement is { } landing)
            return _store.Groups.FirstOrDefault(g => g.Id == target.Receiver) is { } group &&
                landing.Sequence.Matches(group.Id, group.Members, Array.Empty<Guid>());
        return target.Page < FormalPages && ValidTopDrop(target) &&
            (target.Kind == DropKind.Place ? SameDrawerCut != null : IsClipboardBlank(target.Index));
    }

    private async Task PasteClipboard(DropTarget target, bool requiresFocus = false)
    {
        if (EditingDataBlocked || !StillValidClipboardTarget(target)) return;
        var localCut = target.Kind == DropKind.Place ? SameDrawerCut : null;
        if (target.Kind == DropKind.Place && localCut == null) return;
        _readingClipboard = true;
        _pasteRequiresFocus = requiresFocus;
        int generation = ++_pasteGeneration;
        using var hold = HoldOpen();
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(ClipboardFormat))
            {
                var data = await content.GetDataAsync(ClipboardFormat);
                var items = data is string json ? JsonSerializer.Deserialize<ClipboardItems>(json) : null;
                if (!CanFinishPaste(generation, target)) return;
                if (items == null || items.Session != _clipboardSession) { ShowOperationFeedback(UiText.Get("ClipboardItemsExpired")); return; }
                if (localCut != null && (!items.Cut || localCut.Token != items.Token)) return;
                if (items.Cut)
                {
                    CheckClipboardOwnership();
                    if (_cutItems is not { } pending || pending.Token != items.Token) { ShowOperationFeedback(UiText.Get("CutCanceled")); return; }
                    await PasteCut(pending, target);
                }
                else if (_stores.TryGetValue(items.Drawer, out var source))
                {
                    var (paths, folders, names, unavailable) = await Task.Run(() => ReadClipboardCopies(items, target, source));
                    if (CanFinishPaste(generation, target))
                        await ImportPaths(() => Task.FromResult(paths), target, clipboard: true, copiedFolders: folders,
                            unavailable: unavailable, copiedNames: names);
                }
                else ShowOperationFeedback(UiText.Get("ClipboardDrawerMissing"));
            }
            else if (localCut == null && HasExternalItems(content))
            {
                var items = await ReadExternalItems(content);
                if (!CanFinishPaste(generation, target)) return;
                await ImportPaths(() => Task.FromResult(items), target, clipboard: true);
            }
            else ShowOperationFeedback(UiText.Get("ClipboardEmpty"));
        }
        catch (Exception ex)
        {
            PrototypeLog.Write("paste: " + ex);
            if (_exitRequested) _exitCommitFailed = true;
            if (!_closed) ShowOperationFeedback(UiText.Get("PasteOperationFailed"));
        }
        finally { _readingClipboard = false; _pasteRequiresFocus = false; }
    }

    private static (string[] Paths, List<CopyFolder> Folders, Dictionary<string, string> Names, int Unavailable) ReadClipboardCopies(
        ClipboardItems items, DropTarget target, ShortcutStore source)
    {
        var byId = source.Items.ToDictionary(entry => entry.Id);
        var paths = new List<string>();
        var folders = new List<CopyFolder>();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int unavailable = 0;

        string AddPath(ShortcutEntry entry)
        {
            var path = source.CopyPath(entry);
            if (added.Add(path) && entry.DisplayName is { } displayName) names.Add(path, displayName);
            paths.Add(path);
            return path;
        }

        foreach (Guid id in items.Ids)
        {
            if (source.Groups.FirstOrDefault(group => group.Id == id) is { } group)
            {
                var members = group.Members.Select(member => AddPath(byId[member])).ToArray();
                if (target.Receiver == null) folders.Add(new(group.Name, members));
            }
            else if (byId.TryGetValue(id, out var entry)) AddPath(entry);
            else unavailable++;
        }
        return (paths.ToArray(), folders, names, unavailable);
    }

    private async Task PasteCut(ClipboardItems cut, DropTarget target)
    {
        if (!_stores.TryGetValue(cut.Drawer, out var source)) { ShowOperationFeedback(UiText.Get("CutSourceMissing")); return; }
        var sourceOrder = cut.Folder is Guid folder ? source.Groups.FirstOrDefault(g => g.Id == folder)?.Members : source.TopSlots;
        if (sourceOrder == null) { ShowOperationFeedback(UiText.Get("CutSourceMissing")); return; }
        var ids = sourceOrder.Where(cut.Ids.Contains).ToArray();
        if (ids.Length == 0) { ShowOperationFeedback(UiText.Get("CutSourceMissing")); return; }
        var definition = _preferences.Value.Drawers.Single(d => d.Id == cut.Drawer);
        var layout = ComputeLayout(_preferences.Value);
        int sourceCapacity = layout.Items.TryGetValue(definition.Id, out var placement)
            ? placement.Metrics.Rows * placement.Metrics.Columns : definition.Rows * definition.Columns;
        ItemMoveResult result;
        var destination = _store;
        if (!StillValidClipboardTarget(target)) return;
        _committingCut = true;
        try
        {
            using (BeginDataHistory())
                result = await Task.Run(() => _preferences.MoveItems(cut.Drawer, source, target.Drawer, destination, ids,
                    target.Placement with { Deduplicate = true, SourcePageCapacity = sourceCapacity, MemberPlacement = null }));
        }
        finally { _committingCut = false; }
        if (_closed) return;
        CheckClipboardOwnership();
        if (result.Moved > 0)
        {
            _stores[cut.Drawer] = result.Source;
            _store = _stores[target.Drawer] = result.Target;
            var currentSource = cut.Folder is Guid sourceFolder
                ? result.Source.Groups.FirstOrDefault(g => g.Id == sourceFolder)?.Members : result.Source.TopSlots;
            var remaining = ids.Where(id => currentSource?.Contains(id) == true &&
                result.MovedItems?.Contains(id) != true).ToArray();
            if (_cutItems?.Token == cut.Token) _cutItems = remaining.Length == 0 ? null : cut with { Ids = remaining };
            if (cut.Drawer == target.Drawer && result.ReclaimedPages.Length > 0)
                GoToPage(TopGridSlots.RemapPage(target.Page, result.ReclaimedPages), animate: false);
            RefreshTiles();
            if (target.Receiver != null) ShowClipboardFolderEnd();
            else AnimateClipboardLanding(target.Index, result.MovedItems ?? Array.Empty<Guid>());
        }
        ReportTransferResult(true, result.Moved, result.Duplicates, result.Failed);
    }

    private void ShowClipboardFolderEnd()
    {
        _memberGrid?.UpdateLayout();
        if (_memberGrid != null && FindVisual<ScrollViewer>(_memberGrid) is { } scroll)
            scroll.ChangeView(null, scroll.ScrollableHeight, null, false);
    }

    private void AnimateClipboardLanding(int index, IEnumerable<Guid> ids)
    {
        var start = Slot(_topPage * PageCapacity + index % PageCapacity, _metrics);
        foreach (Guid id in ids)
        {
            int cell = _store.TopSlots.IndexOf(id);
            if (cell < 0 || cell >= _tiles.Count || cell / PageCapacity != _topPage) continue;
            var tile = _tiles[cell];
            var motion = Motion(false, id, cell, _metrics);
            motion.X = start.X; motion.Y = start.Y; motion.Vx = motion.Vy = 0;
            tile.SetIconScale(.75);
            AnimateIconScale(tile, 1);
        }
    }

}
