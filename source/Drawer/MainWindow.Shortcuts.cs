using System;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Drawer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Graphics.Imaging;
using static Drawer.NativeMethods;

namespace Drawer;

public sealed partial class MainWindow
{
    private ShortcutStore _store = new();
    private readonly ObservableCollection<ShortcutTile> _tiles = new();
    private readonly Dictionary<Guid, (int Requested, ImageSource Image)> _iconCache = new();
    private readonly Dictionary<(Guid Id, int Requested), Task<ImageSource?>> _iconReads = new();
    private readonly Dictionary<Guid, double> _iconRetryAfter = new();
    private bool _storeReady, _importing, _dragOpened, _pickerOpen;
    private CancellationTokenSource? _importCancellation;
    private double _manualUntil;
    private bool _explicitSession;
    private bool _switcherPolicyUnavailable;
    private void SetSwitcherVisibility(bool visible)
    {
        if (_switcherPolicyUnavailable) return;
        try { AppWindow.IsShownInSwitchers = visible; }
        catch (NotImplementedException ex)
        {
            // Some Windows sessions cannot set the shell policy. Native
            // tool-window styles still control the ordinary drawer presentation.
            _switcherPolicyUnavailable = true;
            PrototypeLog.Write("window switcher policy unavailable: " + ex.Message);
        }
    }
    private void OpenExplicitly()
    {
        _explicitSession = true;
        SetSwitcherVisibility(true);
        SetWindowLongPtrW(_hwnd, -20, (nint)(GetWindowLongPtrW(_hwnd, -20).ToInt64() & ~0x08000080L));
        _manualUntil = _clock.Elapsed.TotalMilliseconds + 2000;
#if DEBUG
        // Keep an explicitly opened isolated review window targetable for screenshots.
        if(Array.Exists(Environment.GetCommandLineArgs(),a=>a=="--review"||a=="--review16"||a=="--review19"||a=="--review20"||a=="--review21"||a=="--review21-reflow")) _manualUntil=_clock.Elapsed.TotalMilliseconds+600000;
#endif
        _interaction.KeepOpen(); RenderPosition();
    }
    private void RestoreHoverStyle()
    {
        SetSwitcherVisibility(false);
        SetWindowLongPtrW(_hwnd, -20, (nint)((GetWindowLongPtrW(_hwnd, -20).ToInt64() | 0x08000080L) & ~0x40000L));
    }
    private void Status(string message, bool notify = false)
    {
        if (notify) NotifyTransient(message);
        PrototypeLog.Write("library: " + message);
    }
    private void ErrorStatus(string message)
    {
        if (_exitRequested) _exitCommitFailed = true;
        Status(message, notify: true);
    }
    private void NotifyCleanupIfNeeded()
    {
        if (_preferences.TakeCleanupNotice() is { Length: > 0 } notice) ErrorStatus(notice);
    }
    private void LoadLibrary(bool reload = true)
    {
        try { if (reload) _store.Load(); _storeReady = true; _layoutDirty = true; UpdateGeometry(); RefreshTiles(); if (!string.IsNullOrEmpty(_store.Notice)) ErrorStatus(_store.Notice); }
        catch (Exception ex) { _storeReady = false; ErrorStatus(UiText.Get("ImportBlockedLibrary", ex.Message)); }
    }
    private void RefreshTiles()
    {
        _tileVisualsDirty = true;
        using var sample = IdlePerformance.Measure(IdlePerformance.Work.RefreshTiles);
        using var refresh = ResizeTrace?.Measure(ResizePerformance.Stage.Content);
        if (_previewOnly) _previewLibrary = (_store.Items, _store.Groups, _store.TopSlots);
        SyncPageModel();
        var old = _tiles.Where(t => !t.IsEmpty).DistinctBy(t => t.Id).ToDictionary(t => t.Id);
        var desired = new List<ShortcutTile>();
        var groups = _store.Groups.ToDictionary(g => g.Id);
        var entries = _store.Items.ToDictionary(e => e.Id);
        foreach (var id in _iconCache.Keys.Where(id => !entries.ContainsKey(id)).ToArray()) _iconCache.Remove(id);
        foreach (var id in _iconRetryAfter.Keys.Where(id => !entries.ContainsKey(id)).ToArray()) _iconRetryAfter.Remove(id);
        foreach (var id in _store.TopSlots)
        {
            if (id == Guid.Empty)
            {
                int index = desired.Count;
                desired.Add(index < _tiles.Count && _tiles[index].IsEmpty ? _tiles[index] : new ShortcutTile());
                continue;
            }
            var group = groups.GetValueOrDefault(id);
            var entry = entries.GetValueOrDefault(id);
            if (group == null && entry == null)
            {
                PrototypeLog.Write("library: missing item for slot " + id);
                desired.Add(new ShortcutTile());
                continue;
            }
            if (old.TryGetValue(id, out var existing) && SameGroup(existing.Group, group) &&
                existing.ShowNames == _metrics.Effective.ShowNames &&
                (entry == null || existing.RebindEntry(entry)))
                desired.Add(existing);
            else desired.Add(group != null
                ? new ShortcutTile(group, _metrics.IconSize, _metrics.Effective.ShowNames)
                : new ShortcutTile(entry!, _metrics.IconSize, _metrics.Effective.ShowNames));
        }
        while (desired.Count < RenderedPages * PageCapacity)
        {
            int index = desired.Count;
            desired.Add(index < _tiles.Count && _tiles[index].IsEmpty ? _tiles[index] : new ShortcutTile());
        }
        bool backgroundChanged = UpdateTileList(_tiles, desired);
        foreach (var tile in _tiles) ApplyTopTileMetrics(tile);
        RefreshFolder();
        if (backgroundChanged && _openFolder != null && !_folderAnimating) _ = CaptureFolderBackdrop();
        RefreshSelectionMarks();
        _layoutDirty = true;
        if (!_internalDrag) SettleVisuals();
    }
    private static bool SameGroup(ShortcutGroup? left, ShortcutGroup? right) =>
        ReferenceEquals(left, right) || left != null && right != null && left.Name == right.Name &&
        left.Members.SequenceEqual(right.Members);

    private static bool UpdateTileList(ObservableCollection<ShortcutTile> current, IReadOnlyList<ShortcutTile> desired)
    {
        bool changed = false;
        var retained = desired.ToHashSet();
        for (int i = current.Count - 1; i >= 0; i--)
            if (!retained.Contains(current[i])) { current.RemoveAt(i); changed = true; }
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < current.Count && ReferenceEquals(current[i], desired[i])) continue;
            int previous = current.IndexOf(desired[i]);
            if (previous < 0) current.Insert(i, desired[i]);
            else current.Move(previous, i);
            changed = true;
        }
        return changed;
    }

    private void ShortcutGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyGridMetrics();
    }
    private async Task ImportPaths(Func<Task<string[]>> readPaths, DropTarget? drop = null, int? previousPage = null,
        bool clipboard = false, IReadOnlyList<CopyFolder>? copiedFolders = null, int unavailable = 0,
        IReadOnlyDictionary<string, string>? copiedNames = null)
    {
        if (_closed || _exitRequested) return;
        if (!_storeReady || _importing || _internalDrag) { ShowOperationFeedback(UiText.Get("DataBusy")); return; }
        _importing = true;
        using var hold = HoldOpen();
        using var cancellation = new CancellationTokenSource();
        _importCancellation = cancellation;
        bool committed = false;
        try
        {
            var receiver = _store;
            var input = await readPaths();
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closed) return;
            if (!ReferenceEquals(_store, receiver) || drop != null && _drawerId != drop.Drawer)
                throw new InvalidOperationException(UiText.Get("DropDrawerChanged"));
            if (drop is { TopLayout: not null } && !ValidTopDrop(drop))
                throw new InvalidOperationException(UiText.Get("InvalidDropPosition"));
            if (clipboard && drop != null && !StillValidClipboardTarget(drop))
                throw new InvalidOperationException(UiText.Get("PasteTargetChanged"));
            var oldSlots = clipboard ? receiver.TopOrder.ToHashSet() : null;
            ImportResult result;
            using (BeginDataHistory()) result = await Task.Run(() => receiver.Import(input, drop?.Placement, cancellation.Token, copiedFolders, copiedNames));
            committed = result.Added > 0;
            if (_closed) return;
            if (drop != null && result.Added > 0) SetTemporaryPage(false);
            if (committed) RefreshTiles();
            int failed = result.Errors.Count + unavailable;
            if (_exitRequested && failed > 0) _exitCommitFailed = true;
            PrototypeLog.Write($"import result: {(clipboard ? "clipboard" : drop == null ? "picker" : "drop")} added={result.Added} duplicates={result.Duplicates} failed={failed}");
            foreach (string error in result.Errors) PrototypeLog.Write("import: " + error);
            if (input.Length == 0 && failed == 0)
                ShowOperationFeedback(UiText.Get("ImportEmpty"));
            else
                ReportTransferResult(false, result.Added, result.Duplicates, failed,
                    failed > 0 ? result.FirstFailure ?? UiText.Get("ImportCheckFiles") : null);
            if (result.Added > 0)
            {
                if (drop == null) GoToPage((receiver.TopSlots.Count - 1) / PageCapacity);
                else if (_topPage != drop.Page) GoToPage(drop.Page, animate: clipboard);
                else if (clipboard && drop.Kind == DropKind.CopyMembers) ShowClipboardFolderEnd();
                else
                {
                    if (oldSlots != null) AnimateClipboardLanding(drop.Index, receiver.TopOrder.Where(id => !oldSlots.Contains(id)));
                    RememberTopPage();
                }
            }
        }
        catch (OperationCanceledException) { if (!_closed) Status(UiText.Get("ImportCanceled")); }
        catch (Exception ex)
        {
            PrototypeLog.Write("import: " + ex);
            if (_exitRequested) _exitCommitFailed = true;
            if (!_closed) ShowOperationFeedback(UiText.Get("ImportOperationFailed"));
        }
        finally
        {
            if (drop != null && !_closed)
            {
                SetMemberPreviewExtent(0);
                DiscardExternalPage();
                if (!committed && previousPage is int page && _topPage != page) GoToPage(page, animate: false);
                SettleVisuals();
            }
            _importCancellation = null;
            _importing = false;
        }
    }
    private void Add_Click(object sender, RoutedEventArgs e) => _ = PickShortcutsToImport();

    private async Task PickShortcutsToImport()
    {
        if (_closed || _resize != null || _historyRestoring || _importing || _boxSelection != null || _readingClipboard || _pickerOpen || !_storeReady || _internalDrag || _externalDragging || _formingFolder || _morphCommitted ||
            _pressedTile != null || NavigationPressed || NavigationTransitionActive || _menuOpen || _confirmationWindow != null ||
            _navActive || _settingsOpen || _previewOnly || !FinishNameEdit(true)) return;
        if (PageTurning) GoToPage(_topPage, animate: false);
        using var hold = HoldOpen();
        nint previousStyle = GetWindowLongPtrW(_hwnd, -20);
        _pickerOpen = true;
        try
        {
            // Explicit picker action may activate. Hover remains non-activating.
            SetWindowLongPtrW(_hwnd, -20, (nint)(previousStyle.ToInt64() & ~0x08000000L));
            SetForegroundWindow(_hwnd);
            // The drawer is normally topmost; the independent picker must stay unobstructed.
            SetWindowPos(_hwnd, (nint)(-2), 0, 0, 0, 0, 0x0013);
            var paths = await ShortcutPicker.PickAsync(_hwnd);
            if (_closed) return;
            if (paths.Count > 0) await ImportPaths(() => Task.FromResult(paths.ToArray()));
            else Status(UiText.Get("PickerCanceled"));
        }
        catch (Exception ex)
        {
            PrototypeLog.Write("file picker: " + ex);
            if (!_closed) ShowOperationFeedback(UiText.Get("PickerOpenFailed"));
        }
        finally
        {
            _pickerOpen = false;
            if (!_closed) { SetWindowLongPtrW(_hwnd, -20, previousStyle); SetWindowPos(_hwnd, (nint)(-1), 0, 0, 0, 0, 0x0013); }
        }
    }
    private Guid? _lastLaunched;
    private double _lastLaunchTime = double.NegativeInfinity;
    private void LaunchTile(ShortcutTile tile)
    {
        if(_settingsOpen||!FinishNameEdit(true))return;
        if(_folderAnimating)return;
        if (tile.IsEmpty) return;
        if (tile.IsGroup) { OpenFolder(tile); return; }
        if (tile.Entry is not { } entry) return;
        double launchTime = _clock.Elapsed.TotalMilliseconds;
        if (_lastLaunched == tile.Id && launchTime - _lastLaunchTime < 500) return;
        _lastLaunched = tile.Id; _lastLaunchTime = launchTime;
        try
        {
            string path = _store.CopyPath(entry);
            if (!File.Exists(path)) throw new FileNotFoundException(UiText.Get("SavedShortcutMissing"));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, ErrorDialog = false });
            Status(UiText.Get("LaunchHandedOff", tile.Name));
        }
        catch (Exception ex) { ErrorStatus(UiText.Get("LaunchFailed", tile.Name, ex.Message)); }
    }
    private void Icon_Loaded(object sender, RoutedEventArgs e) => _ = LoadIconSafely(sender);

    private async Task LoadIconSafely(object sender)
    {
        try { await LoadTileIcon(sender); }
        catch (Exception ex) { PrototypeLog.Write("tile icon: " + ex); }
    }

    private async Task LoadTileIcon(object sender)
    {
        if (_closed || sender is not Image image || image.DataContext is not ShortcutTile tile) return;
        if (tile.IsEmpty) return;
        if (image.Parent is Grid previewParent)
        {
            var preview = previewParent.Children.OfType<Grid>().FirstOrDefault(g => g.Tag is ShortcutTile);
            if (preview != null && ReferenceEquals(preview.Tag, tile)) return;
            if (preview != null) previewParent.Children.Remove(preview);
            if (tile.IsGroup) { LoadFolderPreview(previewParent, tile); return; }
        }
        if (tile.Entry is not { } entry) return;
        var store = _store;
        if (tile.Icon == null && _iconCache.TryGetValue(entry.Id, out var cached)) tile.Icon = cached.Image;
        if (_resize != null && tile.Icon != null) return;
        double size = _resize?.Placement.Metrics.PictureSize() ?? tile.NormalPictureSize;
        if (await GetIcon(store, entry, size) is { } icon && !_closed && ReferenceEquals(_store, store) &&
            ReferenceEquals(image.DataContext, tile) && (_resize == null || tile.Icon == null))
            tile.Icon = _iconCache.TryGetValue(entry.Id, out var latest) ? latest.Image : icon;
    }

    private Task<ImageSource?> GetIcon(ShortcutStore store, ShortcutEntry entry, double size)
    {
        double topIconSize = _resize?.Placement.Metrics.PictureSize() ?? _metrics.PictureSize();
        double pixels = Math.Max(size, topIconSize) * _presentationScale * DrawerSettings.SelectionHeldScale;
        int requested = pixels <= 64 ? 64 : pixels <= 96 ? 96 : pixels <= 128 ? 128 : pixels <= 192 ? 192 : 256;
        if (_iconCache.TryGetValue(entry.Id, out var cached) && cached.Requested >= requested) return Task.FromResult<ImageSource?>(cached.Image);
        var key = (entry.Id, requested);
        if (_iconReads.TryGetValue(key, out var read)) return read;
        if (_iconRetryAfter.TryGetValue(entry.Id, out double retry) && _clock.Elapsed.TotalMilliseconds < retry)
            return Task.FromResult(_iconCache.TryGetValue(entry.Id, out cached) ? cached.Image : null);
        // WinRT can reenter the dispatcher while starting the thumbnail request.
        var pending = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _iconReads.Add(key, pending.Task);
        _ = CompleteIconRead(store, entry, requested, pending);
        return pending.Task;
    }

    private async Task CompleteIconRead(ShortcutStore store, ShortcutEntry entry, int requested, TaskCompletionSource<ImageSource?> pending)
    {
        ImageSource? image = null;
        try
        {
            image = await ReadIcon(store, entry, requested);
            if (_closed) image = null;
            else if (ReferenceEquals(store, _store) && store.Items.Any(item => item.Id == entry.Id))
            {
                if (image == null) _iconRetryAfter[entry.Id] = _clock.Elapsed.TotalMilliseconds + 30000;
                else _iconRetryAfter.Remove(entry.Id);
                if (_iconCache.TryGetValue(entry.Id, out var cached) && (image == null || cached.Requested >= requested)) image = cached.Image;
                else if (image != null) _iconCache[entry.Id] = (requested, image);
            }
        }
        catch (Exception ex)
        {
            PrototypeLog.Write("icon read: " + ex);
            image = null;
            if (!_closed) _iconRetryAfter[entry.Id] = _clock.Elapsed.TotalMilliseconds + 30000;
        }
        finally
        {
            pending.TrySetResult(_closed ? null : image);
            _iconReads.Remove((entry.Id, requested));
        }
    }

    private async Task<ImageSource?> ReadIcon(ShortcutStore store, ShortcutEntry entry, int requested)
    {
        string path;
        try { path = store.CopyPath(entry); }
        catch (Exception ex) { PrototypeLog.Write("icon path: " + ex.Message); return null; }
        if (Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var (url, hasIcon) = await Task.Run(() => ShortcutIdentity.ReadInternetShortcut(path));
                if (hasIcon && await ReadStorageIcon(path, entry, requested) is { } custom) return custom;
                if (url != null && ShortcutIdentity.TryWebAddress(url, out var address) &&
                    await WebsiteIcons.ReadAsync(_preferences.Resolve("WebsiteIcons"), address) is { } icon)
                    return !_closed ? CreateIconSource(icon, entry) : null;
            }
            catch (Exception ex) { PrototypeLog.Write("website icon: " + ex.GetType().Name); }
            return null;
        }
        try
        {
            var raw = await Task.Run(async () =>
            {
                var source = ShortcutIdentity.ReadIconSource(path);
                if (source.IconFile is { } iconFile &&
                    await ShortcutIconFile.ReadAsync(iconFile, requested) is { } original) return original;

                // The target supplies the file-type icon for ordinary links.
                var extracted = (source.Target != null ? ShellShortcutIcon.Read(source.Target, requested) : null) ??
                    ShellShortcutIcon.Read(path, requested);
                return extracted?.RemoveDetachedHairline();
            });
            if (raw != null) return CreateIconSource(raw, entry);
        }
        catch (Exception ex) { PrototypeLog.Write("shell icon: " + ex.Message); }
        return await ReadStorageIcon(path, entry, requested);
    }

    private async Task<ImageSource?> ReadStorageIcon(string path, ShortcutEntry entry, int requested)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var actual = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, (uint)requested, ThumbnailOptions.None);
            // Windows may supply a preview image with a document frame. Only a
            // real icon belongs here; otherwise leave the tile's fallback glyph.
            if (actual == null || actual.Type != ThumbnailType.Icon) return null;
            var decoder = await BitmapDecoder.CreateAsync(actual);
            int width = (int)decoder.PixelWidth, height = (int)decoder.PixelHeight;
            if (width is < 1 or > 1024 || height is < 1 or > 1024) return null;
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            return CreateIconSource(new IconPixels(width, height, data.DetachPixelData()).RemoveDetachedHairline(), entry);
        }
        catch (Exception ex) { PrototypeLog.Write("icon fallback: " + entry.FileName + " " + ex.Message); return null; }
    }

    private static ImageSource CreateIconSource(IconPixels raw, ShortcutEntry entry)
    {
        var icon = raw.FitVisibleContent();
#if DEBUG
        if (!ReferenceEquals(raw, icon)) PrototypeLog.Write($"icon: padded {entry.FileName} {raw.Width}×{raw.Height} → {icon.Width}×{icon.Height}");
#endif
        return CopyBgraImage(icon.Bgra, icon.Width, icon.Height);
    }

    private static WriteableBitmap CopyBgraImage(byte[] pixels, int width, int height)
    {
        // Own the pixels in the XAML image. An asynchronously consumed
        // SoftwareBitmap can outlive the SetBitmapAsync call and its source.
        var image = new WriteableBitmap(width, height);
        using (var stream = image.PixelBuffer.AsStream()) stream.Write(pixels, 0, pixels.Length);
        image.Invalidate();
        return image;
    }
    private void Icon_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs e) => Icon_Loaded(sender, new RoutedEventArgs());
}
