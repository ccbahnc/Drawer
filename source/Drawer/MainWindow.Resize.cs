using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Drawer.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using static Drawer.NativeMethods;
using PerfStage = Drawer.ResizePerformance.Stage;
using PerfCounter = Drawer.ResizePerformance.Counter;

namespace Drawer;

public sealed class DrawerResizeSurface : Grid
{
    private InputSystemCursorShape? _cursor;

    internal void SetResizeCursor(InputSystemCursorShape? cursor)
    {
        if (_cursor == cursor) return;
        _cursor = cursor;
        ProtectedCursor = cursor is { } shape ? InputSystemCursor.Create(shape) : null;
    }
}

public sealed partial class MainWindow
{
    private ArrangedDrawer? _arrangedDrawer;
    private DrawerResizeSession? _resize;
    private ResizePerformance? _resizePerformance;
    private const double ResizeFrameFallbackMs = 50;
    // 拖动缩放时原生窗口每帧最多增长多少物理像素（150% 下约 10.7 DIP）。
    // 该值决定"新露出区域"的最大宽度，也就是放大瞬间那条色带的最大宽度。
    private const int MaxResizeStepPx = 16;
    private const uint VisibleFrameThicknessAttribute = 37;
    private ResizePerformance? ResizeTrace => _resizePerformance ?? _previewOwner?._resizePerformance;
    private PixelRect? _lastTracedWindowBounds;
    private bool _lastResizePreservedClientPixels;
    private FinishedResizeEdge? _finishedResizeEdge;

    private sealed class FinishedResizeEdge(ResizePerformance trace, Guid drawer, string outcome, object final)
    {
        public ResizePerformance Trace { get; } = trace;
        public Guid Drawer { get; } = drawer;
        public string Outcome { get; } = outcome;
        public object Final { get; } = final;
        public long Started { get; } = Stopwatch.GetTimestamp();
        public bool AfterRelease { get; set; }
        public bool HeldOpen { get; set; }
        public bool Hidden { get; set; }
    }
    // Preserve neighbors' actual sizes after a manual resize. The next change
    // to the edge's membership, order, or preferred layout solves from scratch.
    private sealed record ResizeScales((Guid Id, int Rows, int Columns, double? Scale)[] Layout, Dictionary<Guid, double> Scales);
    private readonly Dictionary<DrawerEdge, ResizeScales> _resizeScales = new();

    private sealed class DrawerResizeSession(DrawerDefinition drawer, ArrangedDrawer placement,
        ResizeEdge edges, Pointer pointer, NativeMethods.Point anchor, int revision, IDisposable hold, bool preserveClientPixels)
    {
        public DrawerDefinition Drawer { get; } = drawer;
        public ArrangedDrawer Placement { get; } = placement;
        public ResizeEdge Edges { get; } = edges;
        public Pointer Pointer { get; } = pointer;
        public NativeMethods.Point Anchor { get; } = anchor;
        public NativeMethods.Point LastCursor { get; set; } = anchor;
        public int Revision { get; } = revision;
        public IDisposable Hold { get; } = hold;
        public bool PreserveClientPixels { get; } = preserveClientPixels;
        public ArrangedDrawer Current { get; set; } = placement;
        public ScaleTransform GridScale { get; } = new() { ScaleX = 1, ScaleY = 1 };
        public DrawerCollection? Proposal { get; set; }
        public Dictionary<Guid, double>? Neighbors { get; set; }
        public long LastFrame { get; set; } = Stopwatch.GetTimestamp();
        public bool Applying { get; set; }
        public PixelRect? SampleNextLayout { get; set; }
    }

    private void WireDrawerResize()
    {
        WindowSurface.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ResizePressed), true);
        WindowSurface.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(ResizeMoved), true);
        WindowSurface.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ResizeReleased), true);
        WindowSurface.PointerEntered += (_, e) => ApplyResizeCursor(ResizeEdgeAtPointer(e));
        WindowSurface.PointerExited += (_, _) =>
        {
            if (_resize == null && ResizeEdgeAtCursor() == ResizeEdge.None) WindowSurface.SetResizeCursor(null);
        };
        WindowSurface.PointerCanceled += ResizeCanceled;
        WindowSurface.PointerCaptureLost += ResizeCanceled;
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
                EndDrawerResize(commit: false, reason: "deactivated");
        };
    }

    private bool CanResizeDrawer => !_closed && !_exitRequested && _arrangedDrawer != null && DropSurfaceSettled && !_previewOnly && !_surfaceFree &&
        !_editingName && !_openHolds.Active && !_menuOpen && !_settingsOpen && !_pickerOpen &&
        !_importing && !_readingClipboard && !_externalDragging && !_internalDrag &&
        !_historyRestoring && !_committingCut && !_committingDrag && _pressedTile == null && _boxSelection == null &&
        !NavigationPressed && !_formingFolder && !_morphCommitted && !_morphEnding && _confirmationWindow == null;

    private ResizeEdge ResizeEdgeAtCursor() => CanResizeDrawer && GetCursorPos(out var cursor) ? ResizeEdgesAt(cursor) : ResizeEdge.None;

    private ResizeEdge ResizeEdgeAtPointer(PointerRoutedEventArgs e) =>
        CanResizeDrawer && TryGetResizePointer(e, out var cursor) ? ResizeEdgesAt(cursor) : ResizeEdge.None;

    private bool TryGetResizePointer(PointerRoutedEventArgs e, out NativeMethods.Point cursor)
    {
        var point = e.GetCurrentPoint(WindowSurface).Position;
        double scale = WindowSurface.XamlRoot?.RasterizationScale ?? _presentationScale;
        cursor = new NativeMethods.Point { X = (int)Math.Round(point.X * scale), Y = (int)Math.Round(point.Y * scale) };
        return ClientToScreen(_hwnd, ref cursor);
    }

    private ResizeEdge ResizeEdgesAt(NativeMethods.Point cursor)
    {
        if (!GetClientRect(_hwnd, out var client)) return ResizeEdge.None;
        var origin = new NativeMethods.Point();
        if (!ClientToScreen(_hwnd, ref origin)) return ResizeEdge.None;
        int x = cursor.X - origin.X, y = cursor.Y - origin.Y;
        if (x < 0 || y < 0 || x >= client.Right || y >= client.Bottom) return ResizeEdge.None;
        double band = AutomaticLayout.ResizeBand * _presentationScale;
        double corner = AutomaticLayout.ResizeCorner * _presentationScale;
        int right = client.Right - x - 1, bottom = client.Bottom - y - 1;
        return _edge switch
        {
            DrawerEdge.Top => (x < band || bottom < band && x < corner ? ResizeEdge.Left : ResizeEdge.None) |
                (right < band || bottom < band && right < corner ? ResizeEdge.Right : ResizeEdge.None) |
                (bottom < band || bottom < corner && (x < band || right < band) ? ResizeEdge.Bottom : ResizeEdge.None),
            DrawerEdge.Left => (y < band || right < band && y < corner ? ResizeEdge.Top : ResizeEdge.None) |
                (bottom < band || right < band && bottom < corner ? ResizeEdge.Bottom : ResizeEdge.None) |
                (right < band || right < corner && (y < band || bottom < band) ? ResizeEdge.Right : ResizeEdge.None),
            _ => (y < band || x < band && y < corner ? ResizeEdge.Top : ResizeEdge.None) |
                (bottom < band || x < band && bottom < corner ? ResizeEdge.Bottom : ResizeEdge.None) |
                (x < band || x < corner && (y < band || bottom < band) ? ResizeEdge.Left : ResizeEdge.None)
        };
    }

    private bool ApplyResizeCursor(ResizeEdge? hovered = null)
    {
        var edges = _resize?.Edges ?? hovered ?? ResizeEdgeAtCursor();
        if (edges == ResizeEdge.None)
        {
            WindowSurface.SetResizeCursor(null);
            return false;
        }
        var (shape, id) = edges switch
        {
            ResizeEdge.TopLeft or ResizeEdge.BottomRight => (InputSystemCursorShape.SizeNorthwestSoutheast, 32642),
            ResizeEdge.BottomLeft or ResizeEdge.TopRight => (InputSystemCursorShape.SizeNortheastSouthwest, 32643),
            ResizeEdge.Left or ResizeEdge.Right => (InputSystemCursorShape.SizeWestEast, 32644),
            _ => (InputSystemCursorShape.SizeNorthSouth, 32645)
        };
        WindowSurface.SetResizeCursor(shape);
        var handle = LoadCursorW(0, (nint)id);
        if (handle == 0) return false;
        SetCursor(handle);
        return true;
    }

    private void ResizePressed(object sender, PointerRoutedEventArgs e)
    {
        if (_resize != null || !CanResizeDrawer || !e.GetCurrentPoint(WindowSurface).Properties.IsLeftButtonPressed ||
            _arrangedDrawer is not { } placement || !TryGetResizePointer(e, out var cursor)) return;
        var edges = ResizeEdgesAt(cursor);
        if (edges == ResizeEdge.None) return;
        var drawer = _preferences.Value.Drawers.FirstOrDefault(item => item.Id == _drawerId);
        if (drawer == null || !WindowSurface.CapturePointer(e.Pointer)) return;
        e.Handled = true;
        FinishResizeEdgeObservation();
        bool preserveClientPixels = ResizePerformance.Available && ResizePerformance.CompareClientPixelCopy && !_lastResizePreservedClientPixels;
        _lastResizePreservedClientPixels = preserveClientPixels;
        var session = new DrawerResizeSession(drawer, placement, edges, e.Pointer, cursor, _screenRevision, HoldOpen(), preserveClientPixels);
        _resize = session;
        try
        {
            if (ResizePerformance.Available)
            {
                var group = ComputeLayout(_preferences.Value);
                var appearance = _preferences.Value.Global;
                _resizePerformance = ResizePerformance.Start(new
                {
                    drawer = drawer.Id, edge = drawer.Edge.ToString(), grip = edges.ToString(),
                    screenWidth = _layoutScreen.Width, screenHeight = _layoutScreen.Height,
                    workWidth = _layoutWork.Width, workHeight = _layoutWork.Height,
                    effectiveDpi = _layoutDpi, scalePercent = 100 * _presentationScale,
                    windowDpi = GetDpiForWindow(_hwnd), xamlScale = WindowSurface.XamlRoot?.RasterizationScale,
                    material = appearance.Material.ToString(), displayedMaterial = _displayedMaterial.ToString(),
                    acrylicState = _acrylicController?.State.ToString(),
                    color = appearance.ColorScheme.ToString(), navigation = ShowNavigation,
                    transparentClientAlpha = _transparentClientEnabled,
                    clientPixelCopy = session.PreserveClientPixels ? "preserve" : "discard",
                    rootBackground = (WindowSurface.Background as SolidColorBrush)?.Color.ToString(),
                    materialCoverOpacity = MaterialCover.Opacity,
                    page = _topPage + 1, pages = FormalPages, icons = _store.Items.Count,
                    initialWidth = placement.Geometry.Drawer.Width, initialHeight = placement.Geometry.Drawer.Height,
                    sameEdge = _preferences.Ordered(drawer.Edge).Where(d => d.Enabled).Select(d => new
                    {
                        drawer = d.Id, d.Rows, d.Columns, preferred = d.ContentScale ?? AutomaticLayout.DefaultScale,
                        actual = group.Items.GetValueOrDefault(d.Id)?.ContentScale
                    }).ToArray()
                });
                _lastTracedWindowBounds = placement.Geometry.Drawer;
                RecordResizeEdge("start", placement.Geometry.Drawer);
            }
            using var content = _resizePerformance?.Measure(PerfStage.Content);
            if (_openFolder != null) CloseFolder();
            _slotMotions.Clear();
            ApplyMotions(ShortcutGrid, _tiles, false, _metrics);
            ShortcutGrid.RenderTransform = session.GridScale;
            ApplyResizeCursor();
            if (ReferenceEquals(_resize, session)) CompositionTarget.Rendering += ResizeRendering;
        }
        catch (Exception ex)
        {
            EndDrawerResize(commit: false, reason: "setup-error");
            ErrorStatus(UiText.Get("ResizeFailed", ex.Message));
        }
    }

    private void ResizeRendering(object? sender, object args)
    {
        using var sample = IdlePerformance.Measure(IdlePerformance.Work.ResizeRendering);
        if (_resize is not { } session || session.Applying || _displayRefreshPending) return;
        session.LastFrame = Stopwatch.GetTimestamp();
        var trace = _resizePerformance;
        trace?.FrameStarted();
        using var frame = trace?.Measure(PerfStage.ResizeFrame);
        if (session.SampleNextLayout is { } bounds)
        {
            session.SampleNextLayout = null;
            RecordResizeEdge("next-rendering-callback", bounds);
        }
        PollResizeCursor(session);
    }

    private void RecordResizeEdge(string phase, PixelRect requested)
    {
        if (_resizePerformance is not { } trace || _closed) return;
        RecordResizeEdge(trace, phase, requested);
    }

    private void RecordResizeEdge(ResizePerformance trace, string phase, PixelRect requested)
    {
        var origin = new NativeMethods.Point();
        PixelRect? client = GetClientRect(_hwnd, out var local) && ClientToScreen(_hwnd, ref origin)
            ? new(origin.X, origin.Y, origin.X + local.Right, origin.Y + local.Bottom) : null;
        PixelRect? outer = GetWindowRect(_hwnd, out var window) ? window.Pixels : null;
        const uint ExtendedFrameBounds = 9;
        PixelRect? frame = DwmGetWindowAttribute(_hwnd, ExtendedFrameBounds, out Rect extended, sizeof(int) * 4) == 0
            ? extended.Pixels : null;
        int? frameThickness = DwmGetWindowAttribute(_hwnd, VisibleFrameThicknessAttribute, out int thickness, sizeof(int)) == 0 ? thickness : null;
        Windows.Foundation.Rect? BoundsInSurface(FrameworkElement element) => _visible
            ? element.TransformToVisual(WindowSurface).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight))
            : null;
        trace.RecordEdge(phase, new(requested, outer, frame, client, _presentationScale, GetDpiForWindow(_hwnd), _frameWidth,
            WindowSurface.XamlRoot?.RasterizationScale ?? 0, WindowSurface.ActualWidth, MaterialCover.ActualWidth, MaterialCover.Opacity,
            (MaterialCover.Background as SolidColorBrush)?.Color.ToString(), DropSurface.Width, DropSurface.ActualWidth, RootGrid.ActualWidth,
            RootGrid.Padding.Left, ShortcutGrid.ActualWidth, _resize?.GridScale.ScaleX ?? 1,
            (GridHost.Clip as RectangleGeometry)?.Rect, (ShortcutGrid.Clip as RectangleGeometry)?.Rect, PageRail.ActualWidth,
            WindowSurface.ActualHeight, MaterialCover.ActualHeight, DropSurface.Height, DropSurface.ActualHeight,
            RootGrid.ActualHeight, BoundsInSurface(DropSurface), BoundsInSurface(GridHost), BoundsInSurface(ShortcutGrid),
            frameThickness, $"0x{_requestedFrameBorder:X8}", $"0x{_frameBorderResult:X8}",
            _manualMaterialSize?.X ?? MaterialCover.ActualWidth, _manualMaterialSize?.Y ?? MaterialCover.ActualHeight,
            _materialFillBrush?.Color.ToString(), _materialFill != null,
            _preferences.Value.Global.Material, _displayedMaterial));
    }

    private void ObserveFinishedResizeEdge()
    {
        if (_finishedResizeEdge is not { } observation) return;
        if (_closed || _drawerId != observation.Drawer || _displayRefreshPending ||
            Stopwatch.GetElapsedTime(observation.Started).TotalSeconds >= 12)
        {
            FinishResizeEdgeObservation();
            return;
        }
        if (!observation.AfterRelease)
        {
            observation.AfterRelease = true;
            RecordResizeEdge(observation.Trace, "first-tick-after-resize", _geometry.Drawer);
        }
        if (_visible && !observation.HeldOpen && _interaction.Position == 1 && Stopwatch.GetElapsedTime(observation.Started).TotalMilliseconds >= 500)
        {
            observation.HeldOpen = true;
            RecordResizeEdge(observation.Trace, "settled-still-open", _geometry.Drawer);
        }
        if (!_visible && !observation.Hidden)
        {
            observation.Hidden = true;
            RecordResizeEdge(observation.Trace, "hidden-after-resize", _geometry.Drawer);
        }
        else if (observation.Hidden && _visible && _interaction.Position == 1)
        {
            RecordResizeEdge(observation.Trace, "reopened-after-resize", _geometry.Drawer);
            FinishResizeEdgeObservation();
        }
    }

    private void FinishResizeEdgeObservation()
    {
        if (_finishedResizeEdge is not { } observation) return;
        _finishedResizeEdge = null;
        observation.Trace.Finish(observation.Outcome, observation.Final);
    }

    private void TickActiveResize()
    {
        PollEscape();
        if (_resize is not { } session) return;
        if (!IsWindowVisible(_hwnd) || IsIconic(_hwnd))
        {
            EndDrawerResize(commit: false, reason: "window-hidden");
            return;
        }
        // Rendering can be suspended while the window is occluded.
        if (!session.Applying && Stopwatch.GetElapsedTime(session.LastFrame).TotalMilliseconds >= ResizeFrameFallbackMs)
        {
            _resizePerformance?.Increment(PerfCounter.FrameFallbackPolls);
            PollResizeCursor(session);
        }
    }

    private void PollResizeCursor(DrawerResizeSession session)
    {
        try
        {
            session.Applying = true;
            _resizePerformance?.Poll();
            bool hasCursor;
            NativeMethods.Point cursor;
            using (_resizePerformance?.Measure(PerfStage.Input)) hasCursor = GetCursorPos(out cursor);
            if (hasCursor) UpdateDrawerResize(session, cursor);
            else EndDrawerResize(commit: false, reason: "cursor-unavailable");
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_resize, session)) EndDrawerResize(commit: false, reason: "preview-error");
            PrototypeLog.Write("resize preview: " + ex);
            ErrorStatus(UiText.Get("ResizeFailed", ex.Message));
        }
        finally { session.Applying = false; }
    }

    private void ResizeMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_resize is not { } session)
        {
            ApplyResizeCursor(ResizeEdgeAtPointer(e));
            return;
        }
        if (session.Pointer.PointerId != e.Pointer.PointerId) return;
        using var input = _resizePerformance?.Measure(PerfStage.Input);
        _resizePerformance?.PointerMoved();
        e.Handled = true;
    }

    private void UpdateDrawerResize(DrawerResizeSession session, NativeMethods.Point cursor)
    {
        var trace = _resizePerformance;
        if (session.Revision != _screenRevision) { EndDrawerResize(commit: false, reason: "display-changed"); return; }
        if (cursor.X == session.LastCursor.X && cursor.Y == session.LastCursor.Y)
        { trace?.Increment(PerfCounter.UnchangedCursor); return; }
        trace?.Increment(PerfCounter.ChangedCursor);
        session.LastCursor = cursor;
        var start = session.Placement;
        DrawerDefinition edited;
        DrawerCollection proposal;
        Arrangement plan;
        using (trace?.Measure(PerfStage.Solve))
        {
            double scale = start.Limits.DragScale(start.ContentScale, start.MaximumScale, session.Edges,
                cursor.X - session.Anchor.X, cursor.Y - session.Anchor.Y);
            trace?.Requested(scale);
            var saved = _preferences.Value;
            edited = session.Drawer with { ContentScale = scale };
            proposal = saved with { Drawers = saved.Drawers.Select(d => d.Id == edited.Id ? edited : d).ToList() };
            var neighbors = session.Neighbors ??= ComputeLayout(saved).Items
                .Where(pair => pair.Key != edited.Id && saved.Drawers.Any(d => d.Id == pair.Key && d.Edge == edited.Edge))
                .ToDictionary(pair => pair.Key, pair => pair.Value.ContentScale);
            plan = ComputeLayout(proposal, edited.Edge, neighbors);
        }
        if (!plan.Items.TryGetValue(edited.Id, out var preview)) { trace?.Increment(PerfCounter.CandidateWithoutPlacement); return; }
        if (preview == session.Current) { trace?.Increment(PerfCounter.EqualCandidate); return; }
        if (preview.Geometry.Drawer == session.Current.Geometry.Drawer) trace?.Increment(PerfCounter.SameCandidateRect);
        trace?.CandidateApplied();
        session.Current = preview;
        session.Proposal = proposal;
        // Scale the existing viewport around its visible origin. Its scroll
        // offset and all item containers stay on the original page layout.
        double visualScale = preview.ContentScale / start.ContentScale;
        using (trace?.Measure(PerfStage.Content)) session.GridScale.ScaleX = session.GridScale.ScaleY = visualScale;
        SetSizeTarget(preview.Geometry, immediate: true);
        RenderPosition(preserveSurface: true, resizeMaterialCoverage: true, preserveClientPixels: session.PreserveClientPixels);
        if (ReferenceEquals(_resize, session))
            ShowPreviewEdge(proposal, plan, edited.Edge, forResize: true);
    }

    private void ApplyTopTileMetrics(ShortcutTile tile)
    {
        if (tile.IsEmpty) return;
        bool iconChanged = tile.IconSize != _metrics.IconSize;
        tile.ResizeIcon(_metrics.IconSize);
        tile.ApplyMetrics(_metrics);
        if (iconChanged && tile.IsGroup && ShortcutGrid.ContainerFromItem(tile) is DependencyObject container &&
            FindVisual<Image>(container)?.Parent is Grid icon)
            ResizeFolderPreview(icon, tile);
    }

    private void ResizeReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_resize is not { } session || session.Pointer.PointerId != e.Pointer.PointerId ||
            e.GetCurrentPoint(WindowSurface).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        try
        {
            session.Applying = true;
            _resizePerformance?.ReleaseStarted();
            bool hasPoint;
            NativeMethods.Point cursor;
            using (_resizePerformance?.Measure(PerfStage.Input)) hasPoint = TryGetResizePointer(e, out cursor);
            if (hasPoint) UpdateDrawerResize(session, cursor);
            if (ReferenceEquals(_resize, session))
                EndDrawerResize(commit: hasPoint, reason: hasPoint ? "release" : "release-position-unavailable");
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_resize, session)) EndDrawerResize(commit: false, reason: "release-error");
            ErrorStatus(UiText.Get("ResizeFailed", ex.Message));
        }
    }

    private void ResizeCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_resize?.Pointer.PointerId == e.Pointer.PointerId) EndDrawerResize(commit: false, reason: "pointer-canceled-or-capture-lost");
    }

    private void EndDrawerResize(bool commit, bool restore = true, string reason = "canceled")
    {
        if (_resize is not { } session) return;
        CompositionTarget.Rendering -= ResizeRendering;
        var trace = _resizePerformance;
        string outcome = reason;
        try
        {
            outcome = FinishDrawerResize(session, commit, restore, reason);
        }
        catch
        {
            outcome = "finish-error";
            throw;
        }
        finally
        {
            _resizePerformance = null;
            if (trace != null)
            {
                var expected = session.Current.Geometry.Drawer;
                var applied = GetWindowRect(_hwnd, out var bounds) ? bounds.Pixels : _geometry.Drawer;
                var final = new
                {
                    reason, requestedCommit = commit,
                    candidateWidth = expected.Width, candidateHeight = expected.Height,
                    finalWidth = applied.Width, finalHeight = applied.Height,
                    deltaX = applied.Left - expected.Left, deltaY = applied.Top - expected.Top,
                    candidateScale = session.Current.ContentScale, finalScale = _arrangedDrawer?.ContentScale,
                    finalAcrylicState = _acrylicController?.State.ToString()
                };
                trace.MarkOperationEnded();
                if (!_closed && restore) _finishedResizeEdge = new(trace, session.Drawer.Id, outcome, final);
                else trace.Finish(outcome, final);
            }
        }
    }

    private string FinishDrawerResize(DrawerResizeSession session, bool commit, bool restore, string reason)
    {
        var trace = _resizePerformance;
        bool shouldSave;
        using (trace?.Measure(PerfStage.FinalCheck))
        {
            if (commit) ObserveDisplay();
            shouldSave = commit && session.Revision == _screenRevision && session.Drawer.Id == _drawerId && session.Proposal != null &&
                session.Current.Geometry.Drawer != session.Placement.Geometry.Drawer;
        }
        _resize = null;
        ShortcutGrid.RenderTransform = null;
        session.Hold.Dispose();
        if (!_closed) WindowSurface.ReleasePointerCapture(session.Pointer);
        _layoutDirty = true;
        if (!restore || _closed) return reason;
        string outcome = shouldSave ? "committed" : commit ? "no-change-or-stale" : reason;
        try
        {
            if (shouldSave)
            {
                using var save = trace?.Measure(PerfStage.SaveWithHistory);
                using var history = BeginDataHistory();
                _preferences.Commit(session.Proposal!);
                if (session.Neighbors is { Count: > 0 } neighbors)
                    _resizeScales[session.Drawer.Edge] = new(AutomaticLayout.LayoutOf(_preferences.Value, session.Drawer.Edge).ToArray(), neighbors);
            }
        }
        catch (Exception ex) { outcome = "save-error"; ErrorStatus(UiText.Get("ResizeFailed", ex.Message)); }
        try
        {
            using var finalApply = trace?.Measure(PerfStage.FinalApply);
            ClosePreviewSurfaces(immediate: _displayRefreshPending);
            UpdateGeometry(immediateSize: true);
            UpdatePageIndicator();
            if (_pageNeedsSnap) _pageSnapDeadline.Reset();
            RenderPosition(preserveSurface: true, resizeMaterialCoverage: true, preserveClientPixels: session.PreserveClientPixels);
            RecordResizeEdge("final-submit", _geometry.Drawer);
            if (_pageNeedsSnap && _topPage != 0 && !_displayRefreshPending)
            {
                WindowSurface.UpdateLayout();
                TickTopPage(_clock.Elapsed.TotalMilliseconds);
            }
            using (trace?.Measure(PerfStage.Content)) RefreshResizedPageIcons();
            ApplyResizeCursor();
        }
        catch (Exception ex) { outcome = "final-apply-error"; _layoutDirty = true; ErrorStatus(UiText.Get("ResizeUpdateFailed", ex.Message)); }
        return outcome;
    }

    private void RefreshResizedPageIcons()
    {
        for (int index = _topPage * PageCapacity; index < Math.Min(_tiles.Count, (_topPage + 1) * PageCapacity); index++)
            if (_tiles[index].Entry != null && ShortcutGrid.ContainerFromIndex(index) is DependencyObject container &&
                FindVisual<Image>(container) is { } image)
                _ = LoadIconSafely(image);
    }
}
