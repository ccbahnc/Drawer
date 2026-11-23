using System;
using System.Linq;
using System.Threading.Tasks;
using Drawer.Core;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Point = Windows.Foundation.Point;
using static Drawer.NativeMethods;

namespace Drawer;

public sealed partial class MainWindow
{
    private DropTarget? _externalPreview;
    private readonly TopDropHover _externalDropHover = new();
    private bool _externalDragging, _externalMouseButtonSeen;
    private DataPackageView? _externalData, _canceledExternalData, _externalTextData;
    private Task<string?>? _externalText;
    private Point _externalLastPoint;
    private double _externalLastOver;
    private bool _ignoreExternalUntilMouseUp;
    private int _externalStartPage;
    private IDisposable? _externalDragHold;
    private double? _externalLeftAt, _externalReleasedAt;
    private const double ExternalDropGraceMs = 120;
    private const double ExternalDragIdleMs = 1500;

    private bool CanAcceptExternal(DragEventArgs e) => _visible && !_closed && !_previewOnly && _storeReady &&
        !_ignoreExternalUntilMouseUp && !Equals(_canceledExternalData, e.DataView) &&
        _resize == null && !_historyRestoring && !_importing && !_internalDrag && _pressedTile == null && _boxSelection == null && !_readingClipboard && !_settingsOpen && !_pickerOpen &&
        !_editingName && !NavigationPressed && !NavigationTransitionActive && !_menuOpen && _confirmationWindow == null &&
        !_morphCommitted && !_morphEnding && !_formingFolder &&
        !e.DataView.Properties.ContainsKey(InternalDragKey) && HasExternalItems(e.DataView);

#pragma warning disable CS0618 // Some desktop drag sources still publish the legacy URI format.
    private static bool HasStructuredExternalItems(DataPackageView data) => data.Contains(StandardDataFormats.StorageItems) ||
        data.Contains(StandardDataFormats.WebLink) || data.Contains(StandardDataFormats.Uri);
    private static bool HasExternalItems(DataPackageView data) => HasStructuredExternalItems(data) || data.Contains(StandardDataFormats.Text);

    private static async Task<string?> ReadExternalText(DataPackageView data)
    {
        try { return ShortcutIdentity.TryWebAddress(await data.GetTextAsync(), out var url) ? url.AbsoluteUri : null; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PrototypeLog.Write("external text format unavailable: " + ex.GetType().Name);
            return null;
        }
    }

    private static async Task<string[]> ReadExternalItems(DataPackageView data)
    {
        bool hasWeb = data.Contains(StandardDataFormats.WebLink), hasLegacyUri = data.Contains(StandardDataFormats.Uri),
            hasText = data.Contains(StandardDataFormats.Text);
        if (data.Contains(StandardDataFormats.StorageItems))
        {
            try
            {
                var paths = (await data.GetStorageItemsAsync()).Select(item => item.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
                if (paths.Length > 0) return paths;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { PrototypeLog.Write("external file format unavailable: " + ex.GetType().Name); }
        }
        foreach (bool modern in new[] { true, false })
        {
            if (modern ? !hasWeb : !hasLegacyUri) continue;
            try
            {
                var link = modern ? await data.GetWebLinkAsync() : await data.GetUriAsync();
                if (link != null && ShortcutIdentity.TryWebAddress(link.ToString(), out var address)) return new[] { address.AbsoluteUri };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { PrototypeLog.Write("external link format unavailable: " + ex.GetType().Name); }
        }
        if (hasText && await ReadExternalText(data) is { } url) return new[] { url };
        throw new InvalidOperationException(UiText.Get("DropFormatsInvalid"));
    }
#pragma warning restore CS0618

    private DropTarget? CaptureExternalDrop(Point host, double now, bool publish, out bool previewSort)
    {
        DropTarget? WithoutCandidate(DropTarget? target = null)
        {
            if (publish) _externalDropHover.Reset();
            return target;
        }

        previewSort = true;
        if (!DropSurfaceSettled) return WithoutCandidate();
        if (_openFolder != null)
        {
            if (_folderLayout is not { } layout || _memberGrid is not { } grid || !MemberPanelBounds(layout).Contains(host))
                return WithoutCandidate();
            return WithoutCandidate(CaptureMemberDrop(GridHost.TransformToVisual(grid).TransformPoint(host), DropKind.CopyMembers,
                Array.Empty<Guid>(), _externalPreview));
        }
        if (ExternalPageBand(host) is -1 or 1) return WithoutCandidate();
        var p = GridHost.TransformToVisual(ShortcutGrid).TransformPoint(host);
        int cell = TopCellAt(p.X, p.Y);
        if (!TryGetTopTile(cell, out var tile)) return WithoutCandidate();
        var copy = TopDrop(DropKind.Copy, cell);
        if (tile == null) return WithoutCandidate(copy);
        var action = _externalDropHover.Evaluate(DescribeTopCell(cell, tile, _metrics), p.X, p.Y, now, publish,
            allowReceive: tile.IsGroup);
        if (tile.Group is { } group && action is (TopDropAction.Append or TopDropAction.OpenFolder))
        {
            var sequence = MemberSequence(group, Array.Empty<Guid>(), _externalPreview);
            var landing = new FolderInsertion(sequence, sequence.Candidates.Count);
            return TopDrop(DropKind.CopyMembers, cell, group.Id, landing);
        }
        previewSort = action == TopDropAction.EdgeReady;
        return copy;
    }

    private DropTarget? ResolveExternalDrop(Point host, double now, bool publish)
    {
        try
        {
            var target = CaptureExternalDrop(host, now, publish, out bool previewSort);
            if (publish) UpdateExternalPreview(previewSort ? target : null);
            return target;
        }
        catch (Exception ex)
        {
            EndExternalDrag(canceled: true);
            Status(UiText.Get("ExternalDropCanceled", ex.Message));
            return null;
        }
    }

    private async void Library_DragOver(object sender, DragEventArgs e)
    {
        try { await HandleExternalOver(e); }
        catch (Exception ex)
        {
            PrototypeLog.Write("external drag over: " + ex);
            try { EndExternalDrag(canceled: true); }
            catch (Exception cleanup) { PrototypeLog.Write("external drag cleanup: " + cleanup); }
        }
    }

    private async Task HandleExternalOver(DragEventArgs e)
    {
        var data = e.DataView;
        if (_canceledExternalData != null)
        {
            if (!Equals(_canceledExternalData, data) || _clock.Elapsed.TotalMilliseconds - _externalLastOver >= ExternalDragIdleMs)
                _canceledExternalData = null;
            else _externalLastOver = _clock.Elapsed.TotalMilliseconds;
        }
        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.None;
        if (!CanAcceptExternal(e)) return;
        var point = e.GetPosition(GridHost);
        if (HasStructuredExternalItems(data))
        {
            _externalTextData = null;
            _externalText = null;
            PublishExternalOver(e, data, point);
            return;
        }
        if (!Equals(_externalTextData, data))
        {
            _externalTextData = data;
            _externalText = ReadExternalText(data);
        }
        var read = _externalText!;
        var deferral = e.GetDeferral();
        try
        {
            if (await read.WaitAsync(TimeSpan.FromMilliseconds(ExternalDragIdleMs)) != null &&
                ReferenceEquals(read, _externalText) && CanAcceptExternal(e))
                PublishExternalOver(e, data, point);
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { PrototypeLog.Write("external text format timed out"); }
        finally { deferral.Complete(); }
    }

    private static bool ExternalMouseButtonDown() =>
        (GetAsyncKeyState(1) & 0x8000) != 0 || (GetAsyncKeyState(2) & 0x8000) != 0;

    private void PublishExternalOver(DragEventArgs e, DataPackageView data, Point point)
    {
        bool entered = !_externalDragging;
        _externalDragging = _dragOpened = true;
        _externalData = data;
        _externalLastPoint = point;
        _externalLastOver = _clock.Elapsed.TotalMilliseconds;
        _externalMouseButtonSeen |= ExternalMouseButtonDown();
        if (entered) { _externalDragHold = HoldOpen(); _externalStartPage = _topPage; _pageEdgeDwell.Reset(); StartExternalWheel(); }
        _externalLeftAt = null;
        _interaction.KeepOpen();
        var target = ResolveExternalDrop(point, _externalLastOver, publish: true);
        if (target == null) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = target.Kind == DropKind.CopyMembers &&
            _store.Groups.FirstOrDefault(g => g.Id == target.Receiver) is { } folder
            ? UiText.Get("DropCopyTo", folder.Name)
            : UiText.Get("DropCopyFiles");
    }

    private void Library_DragLeave(object sender, DragEventArgs e)
    {
        if (_externalDragging) _externalLeftAt = _clock.Elapsed.TotalMilliseconds;
        _externalTextData = null;
        _externalText = null;
        e.Handled = true;
    }

    private void TickExternalDrag(double now)
    {
        if (!_externalDragging && !_ignoreExternalUntilMouseUp) return;
        bool held = ExternalMouseButtonDown();
        if (!held) _ignoreExternalUntilMouseUp = false;
        if (!_externalDragging) return;
        if (!_externalMouseButtonSeen)
        {
            if (now - _externalLastOver >= ExternalDragIdleMs ||
                _externalLeftAt is double left && now - left >= ExternalDropGraceMs)
                EndExternalDrag();
            else if (_openFolder != null || !UpdateExternalPageDwell(ExternalPageBand(_externalLastPoint), now))
                ResolveExternalDrop(_externalLastPoint, now, publish: true);
            return;
        }
        if (!GetCursorPos(out var cursor)) { EndExternalDrag(); return; }
        if (!held)
        {
            // A queued Drop may follow the physical release.
            if (now - (_externalReleasedAt ??= now) >= ExternalDropGraceMs) EndExternalDrag();
            return;
        }
        _externalReleasedAt = null;
        UpdateExternalWheelArea();
        TrackExternalPointer(cursor, now);
    }

    private void TrackExternalPointer(NativeMethods.Point cursor, double now)
    {
        int? band = _openFolder == null ? DragPageBand(cursor) : null;
        bool inside = _geometry.Visible(_edge, _interaction.Position).Contains(cursor.X, cursor.Y);
        if (!inside && OtherDrawerTriggerAt(cursor) != null)
        {
            EndExternalDrag();
            return;
        }
        if (UpdateExternalPageDwell(band, now)) return;
        if (!inside || _externalLeftAt is double left && now - left >= ExternalDropGraceMs)
        {
            EndExternalDrag();
            return;
        }
        if (!DragPoint(cursor, GridHost, out var point)) { ClearExternalPreview(); return; }
        ResolveExternalDrop(point, now, publish: true);
    }

    private int? ExternalPageBand(Point point)
    {
        var client = GridHost.TransformToVisual((UIElement)Content).TransformPoint(point);
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? _presentationScale;
        var cursor = new NativeMethods.Point { X = (int)Math.Round(client.X * scale), Y = (int)Math.Round(client.Y * scale) };
        return ClientToScreen(_hwnd, ref cursor) ? DragPageBand(cursor) : null;
    }

    private bool UpdateExternalPageDwell(int? band, double now)
    {
        if (_openFolder != null) return false;
        TurnPageWhileDragging(band, now);
        if (band is not (-1 or 1)) return false;
        ClearExternalPreview();
        return true;
    }

    private void UpdateExternalPreview(DropTarget? target)
    {
        if (target == _externalPreview) return;
        if (_externalPreview != null && _externalPreview.Kind != target?.Kind)
        {
            SetMemberPreviewExtent(0);
            SettleVisuals();
        }
        _externalPreview = target;
        if (target is { Kind: DropKind.Copy })
            SetPreviewTargets(ShortcutGrid, TopGridSlots.PreviewInsert(_store.TopSlots, target.Index));
        else if (target is { MemberPlacement: { } landing } && _openFolder == target.Receiver && _memberGrid is { } grid)
        {
            var slots = landing.PreviewOrder();
            SetPreviewTargets(grid, slots);
            SetMemberPreviewExtent(slots.Length);
        }
    }

    private void ClearExternalPreview()
    {
        _externalDropHover.Reset();
        UpdateExternalPreview(null);
    }

    private void DiscardExternalPage()
    {
        if (!_temporaryPage) return;
        if (_topPage >= FormalPages) GoToPage(FormalPages - 1, animate: false);
        SetTemporaryPage(false);
    }

    private void EndExternalDrag(bool landing = false, bool canceled = false)
    {
        _externalTextData = null;
        _externalText = null;
        if (!_externalDragging) return;
        _externalDragging = false;
        ReleaseHold(ref _externalDragHold);
        _ignoreExternalUntilMouseUp = canceled && _externalMouseButtonSeen && ExternalMouseButtonDown();
        _canceledExternalData = canceled ? _externalData : null;
        _externalData = null;
        _externalMouseButtonSeen = false;
        _dragOpened = false;
        StopExternalWheel();
        _externalLeftAt = _externalReleasedAt = null;
        _pageEdgeDwell.Reset();
        _pageWheel.Reset();
        if (landing) { _externalDropHover.Reset(); _externalPreview = null; }
        else
        {
            ClearExternalPreview();
            DiscardExternalPage();
            if (_topPage != _externalStartPage) GoToPage(_externalStartPage, animate: false);
            RememberTopPage();
        }
    }

    private async void Library_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.None;
        ObserveDisplay();
        if (_displayRefreshPending) { EndExternalDrag(canceled: true); return; }
        var release = _externalDragging && CanAcceptExternal(e)
            ? ResolveExternalDrop(e.GetPosition(GridHost), _clock.Elapsed.TotalMilliseconds, publish: false) : null;
        // Top-level release can be valid before the dwell has painted an insertion.
        // An open folder instead commits the last painted member anchor.
        var drop = _openFolder == null ? release
            : _externalPreview?.MatchesMemberSurface(release) == true ? _externalPreview : null;
        if (drop == null || (drop.TopLayout != null && !ValidTopDrop(drop)))
        {
            PrototypeLog.Write($"external drop rejected: shown={_externalPreview} release={release}");
            bool announce = _externalDragging && HasStructuredExternalItems(e.DataView);
            EndExternalDrag();
            if (announce) ShowOperationFeedback(UiText.Get("InvalidDropPosition"));
            return;
        }
        int previousPage = _externalStartPage;
        var deferral = e.GetDeferral();
        e.AcceptedOperation = DataPackageOperation.Copy;
        EndExternalDrag(landing: true);
        PrototypeLog.Write($"external drop: drawer={drop.Drawer} page={drop.Page + 1} cell={drop.Index} folder={drop.Placement.Group}");
        if (drop.MemberPlacement is { } member)
            PrototypeLog.Write($"folder copy: index={member.Index} previous={member.Before} next={member.After}");
        try { await ImportPaths(() => ReadExternalItems(e.DataView), drop, previousPage); }
        finally { deferral.Complete(); }
    }
}
