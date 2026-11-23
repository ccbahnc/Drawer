using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Drawer.Core;
using static Drawer.NativeMethods;

namespace Drawer;

public sealed partial class MainWindow : Window
{
    private readonly nint _hwnd;
    private readonly DesktopGate _gate;
    private readonly DrawerInteraction _interaction = new();
    private readonly DrawerOpenHolds _openHolds = new();
    private readonly DispatcherQueueTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly SubclassProc _subclass;
    private readonly uint _taskbarCreated;
    private NotifyIconData _tray;
    private nint _appIconLarge, _appIconSmall;
    private EdgeGeometry _geometry;
    private bool _visible, _closed, _layoutDirty = true, _menuOpen;
    private double _lastTick, _lastDisplayPoll, _lastZOrder;
    private int _lastHeight = -1, _frameWidth, _frameHeight;
    private bool _edgeSlideOpening;
    private bool _slidingRetraction;
    private uint _frameDpi;
    private int _requestedFrameBorder = DrawerPalette.NoDwmBorder;
    private int _frameBorderResult;
    private const uint DwmCornerPreference = 33;
    private const int DwmRound = 2;
    private double _presentationScale = 1;
    private readonly TranslateTransform _drawerSlide = new();
    private string _lastState = "", _lastGate = "";

    private IDisposable HoldOpen()
    {
        if (!_previewOnly) IdlePerformance.Invalidate(IdlePerformance.Change.Interaction);
        SetIdlePolling(false);
        if (_visible) _interaction.KeepOpen();
        return _openHolds.Acquire();
    }

    private static void ReleaseHold(ref IDisposable? hold)
    {
        var released = hold;
        hold = null;
        released?.Dispose();
    }
#if DEBUG
    private bool _reviewTrayPending=Array.Exists(Environment.GetCommandLineArgs(),a=>a=="--tray");
    private readonly double _reviewTrayDelay = int.TryParse(Environment.GetCommandLineArgs()
        .FirstOrDefault(arg => arg.StartsWith("--tray-delay=", StringComparison.Ordinal))?[13..], out int delay)
        ? Math.Clamp(delay, 1000, 180000) : 8000;
#endif

    public MainWindow():this(null,null,null) { }
    private MainWindow(MainWindow? owner,DrawerDefinition? definition,ShortcutStore? store)
    {
        _previewOwner = owner;
        _previewOnly=owner!=null;
        if (owner == null)
        {
            InitializeDrawers();
            DrawerPalette.Apply(_preferences.Value.Global.ColorScheme);
        }
        InitializeComponent();
        ApplyWindowLanguage();
        WindowSurface.RequestedTheme = DrawerPalette.Theme;
        ShortcutGrid.ItemsSource = _tiles;
        ShortcutGrid.LayoutUpdated += TileLayoutUpdated;
        if (owner != null)
        {
            _preferences=owner._preferences;_drawerId=definition!.Id;_edge=definition.Edge;_previewDefinition=definition;
            _store=store!;_stores[_drawerId]=_store;_storeReady=true;
            _presentationScale=owner._presentationScale;_frameWidth=owner._frameWidth;_frameHeight=owner._frameHeight;_frameDpi=owner._frameDpi;
            _layoutScreen=owner._layoutScreen;_layoutWork=owner._layoutWork;_layoutDpi=owner._layoutDpi;
            RootGrid.IsHitTestVisible=false;SetWindowLongPtrW(WinRT.Interop.WindowNative.GetWindowHandle(this),-20,(nint)(GetWindowLongPtrW(WinRT.Interop.WindowNative.GetWindowHandle(this),-20).ToInt64()|0x20));
        }
        ApplyNavigationVisibility();
        // A child GridView can mark routed drag events handled. The host owns
        // the external session and reuses it across child hit targets.
        DropSurface.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(Library_DragOver), true);
        DropSurface.AddHandler(UIElement.DragOverEvent, new DragEventHandler(Library_DragOver), true);
        DropSurface.AddHandler(UIElement.DragLeaveEvent, new DragEventHandler(Library_DragLeave), true);
        DropSurface.AddHandler(UIElement.DropEvent, new DragEventHandler(Library_Drop), true);
        ShortcutGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Sort_Pressed), true);
        ShortcutGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Sort_KeyDown), true);
        ShortcutGrid.BringIntoViewRequested += (_, args) =>
        {
            if (_nameTile != null) args.Handled = true;
        };
        RootGrid.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Sort_Moved), true);
        RootGrid.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Sort_Released), true);
        RootGrid.PointerCanceled += Sort_Canceled;
        RootGrid.PointerCaptureLost += Sort_CaptureLost;
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Drawer_KeyDown), true);
        RootGrid.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(RouteWheel), true);
        InitializeNameEditing();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WireDrawerResize();
        WireNavigationDrag();
        _gate = new DesktopGate(_hwnd);
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        SetSwitcherVisibility(false);
        long exStyle = GetWindowLongPtrW(_hwnd, -20).ToInt64();
        SetWindowLongPtrW(_hwnd, -20, (nint)((exStyle | 0x08000080L) & ~0x00040000L)); // NOACTIVATE | TOOLWINDOW
        int corner = DwmRound;
        int cornerResult = DwmSetWindowAttribute(_hwnd, DwmCornerPreference, ref corner, sizeof(int));
        ApplyDrawerFrame();
        DropSurface.RenderTransform = _drawerSlide;
        PrototypeLog.Write($"presentation: DWM corners preference={corner} result=0x{cornerResult:X8}");
        _subclass = WindowMessage;
        if (!SetWindowSubclass(_hwnd, _subclass, 1, 0)) throw new InvalidOperationException("Cannot attach drawer window procedure.");
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        if (!_previewOnly && Environment.ProcessPath is { } exe)
        {
            ExtractIconExW(exe, 0, out _appIconLarge, out _appIconSmall, 1);
            if (_appIconSmall != 0) SendMessageW(_hwnd, 0x0080, 0, _appIconSmall); // WM_SETICON, ICON_SMALL
            if (_appIconLarge != 0) SendMessageW(_hwnd, 0x0080, 1, _appIconLarge); // WM_SETICON, ICON_BIG
            PrototypeLog.Write($"icon: executable small={_appIconSmall != 0} large={_appIconLarge != 0}");
        }
        _tray = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _hwnd, Id = 1, Flags = 7,
            Callback = 0x8001, Icon = _appIconSmall != 0 ? _appIconSmall :
                _appIconLarge != 0 ? _appIconLarge : LoadIconW(0, (nint)32512),
            Tip = UiText.Get("TrayTooltip"), Info = "", InfoTitle = ""
        };
        bool trayAdded = !_previewOnly && Shell_NotifyIcon(0, ref _tray);
        PrototypeLog.Write($"Started hwnd={_hwnd} tray={trayAdded} ");
        AppWindow.Hide();
        InitializeMaterialFill();
        ApplyDrawerMaterial();
        if(!_previewOnly)UpdateGeometry();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(DrawerSettings.PollMs);
        _timer.Tick += Tick;
        Closed += OnClosed;
        _timer.Start();
        if(_previewOnly){RefreshTiles();UpdateNavigationCaption();return;}
        LoadLibrary(reload: _drawerId == Guid.Empty);
        foreach (string notice in new[] { _catalog.Notice, _preferences.RecoveryNotice, _preferences.TakeCleanupNotice() })
            if (!string.IsNullOrEmpty(notice)) Status(notice);
        var recoveryWarnings = _stores.Values.Where(store => store.RecoveryNeedsAttention)
            .Select(store => store.Notice).Distinct().ToList();
        if (_unavailableStores.Count > 0)
            recoveryWarnings.Add(UiText.Get("UnreadableDrawers", _unavailableStores.Count) + " " + UiText.Get("DrawerRecoveryFromTray"));
        if (recoveryWarnings.Count > 0) ErrorStatus(string.Join("\n", recoveryWarnings));
        if (Array.Exists(Environment.GetCommandLineArgs(),a=>a=="--settings")) DispatcherQueue.TryEnqueue(OpenSettingsFromTray);
#if DEBUG
        foreach(var creationEdge in Enum.GetValues<DrawerEdge>())
            if(Array.Exists(Environment.GetCommandLineArgs(),a=>a=="--create="+DrawerCatalog.Id(creationEdge)))DispatcherQueue.TryEnqueue(()=>BeginCreate(creationEdge));
        foreach(string argument in Environment.GetCommandLineArgs())
            if(argument.StartsWith("--layout-id=")&&Guid.TryParse(argument[12..],out var layoutId))DispatcherQueue.TryEnqueue(()=>OpenLayout(layoutId));
        foreach(string argument in Environment.GetCommandLineArgs())
            if(argument.StartsWith("--drawer=")&&Guid.TryParse(argument[9..],out var id))DispatcherQueue.TryEnqueue(()=>RequestOpenDrawer(id));
        foreach(var edge in Enum.GetValues<DrawerEdge>())
            if(Array.Exists(Environment.GetCommandLineArgs(),a=>a=="--edge="+DrawerCatalog.Id(edge))) DispatcherQueue.TryEnqueue(()=>RequestOpenEdge(edge));
        if (ReviewDataRoot() != null && Array.Exists(Environment.GetCommandLineArgs(), a => a == "--inspect-temporary-page"))
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_explicitSession) return;
                DropSurface.IsHitTestVisible = false;
                SetTemporaryPage(true);
                GoToPage(FormalPages);
            });
        foreach(var edge in Enum.GetValues<DrawerEdge>())
            if(Array.Exists(Environment.GetCommandLineArgs(),a=>a=="--layout="+DrawerCatalog.Id(edge))) DispatcherQueue.TryEnqueue(()=>OpenLayout(edge));
        if(Array.Exists(Environment.GetCommandLineArgs(),a=>a=="--review16"))foreach(var edge in Enum.GetValues<DrawerEdge>())
            if(Array.Exists(Environment.GetCommandLineArgs(),a=>a=="--inspect-edge="+DrawerCatalog.Id(edge)))DispatcherQueue.TryEnqueue(()=>InspectLayout(edge));
#endif
    }

    private bool _displayRefreshPending;

    private Arrangement ComputeLayout(DrawerCollection value, DrawerEdge? resizeEdge = null,
        IReadOnlyDictionary<Guid, double>? resizeScales = null)
    {
        using var sample = IdlePerformance.Measure(IdlePerformance.Work.LayoutSolve);
        var fixedScales = new Dictionary<Guid, double>();
        foreach (var (edge, sizing) in _resizeScales)
            if (edge != resizeEdge && value.Global.ShowNavigation == _preferences.Value.Global.ShowNavigation &&
                AutomaticLayout.LayoutOf(value, edge).SequenceEqual(sizing.Layout))
                foreach (var (id, scale) in sizing.Scales) fixedScales[id] = scale;
        if (resizeScales != null)
            foreach (var (id, scale) in resizeScales) fixedScales[id] = scale;
        return AutomaticLayout.Compute(value, _layoutScreen, _layoutWork, _layoutDpi, _frameWidth, _frameHeight,
            fixedScales: fixedScales, strictEdge: resizeEdge);
    }

    private void RecoverDisplayLayout()
    {
        AbortLayoutPresentation();
        FinishNameEdit(false);
        _importCancellation?.Cancel();
        HideImmediately();
        _switchDwell.Clear();
        _switchTarget = null;
        _switching = false;
        _itemDrawerDwell.Clear();
        _pageEdgeDwell.Reset();
        _pageWheel.Reset();
        _displayRefreshPending = false;
        _layoutDirty = true;
        UpdateGeometry(immediateSize: true, restorePreferred: true);
        PrototypeLog.Write($"display: revision={_screenRevision} dpi={_layoutDpi} work={_layoutWork} triggers=" +
            string.Join("; ", _edgeGeometries.Select(p => $"{p.Key}:{p.Value.Trigger}")));
    }

    private static (PixelRect Screen, PixelRect Work, uint Dpi)? ReadDisplay(nint hwnd)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var primary = MonitorFromPoint(new Point(), 1);
        if (!GetMonitorInfo(primary, ref info)) return null;
        var screen = info.Monitor.Pixels;
        var bar=new AppBarData {Size=(uint)Marshal.SizeOf<AppBarData>()};
        nint taskbar=FindWindowW("Shell_TrayWnd",null);
        if(taskbar!=0 && MonitorFromWindow(taskbar,2)==primary && SHAppBarMessage(5,ref bar)!=0)
        {
            int bw=Math.Clamp(bar.Rect.Right-bar.Rect.Left,0,screen.Width/3),bh=Math.Clamp(bar.Rect.Bottom-bar.Rect.Top,0,screen.Height/3);
            if(bar.Edge==0)info.Work.Left=Math.Max(info.Work.Left,screen.Left+bw);
            else if(bar.Edge==1)info.Work.Top=Math.Max(info.Work.Top,screen.Top+bh);
            else if(bar.Edge==2)info.Work.Right=Math.Min(info.Work.Right,screen.Right-bw);
            else if(bar.Edge==3)info.Work.Bottom=Math.Min(info.Work.Bottom,screen.Bottom-bh);
        }
        // A hidden HWND can retain its previous DPI. Layout uses the monitor's DPI.
        uint dpi = GetDpiForMonitor(primary, 0, out uint effectiveDpi, out _) == 0
            ? effectiveDpi : GetDpiForWindow(hwnd);
        return (screen, info.Work.Pixels, dpi == 0 ? 96 : dpi);
    }

    private void QueueDisplayRecovery()
    {
        IdlePerformance.Invalidate(IdlePerformance.Change.Display);
        if (_displayRefreshPending) return;
        SetIdlePolling(false);
        _screenRevision++;
        _displayRefreshPending = true;
    }

    private void ObserveDisplay()
    {
        if (_previewOnly || _displayRefreshPending || _layoutWork.Width <= 0) return;
        using var sample = IdlePerformance.Measure(IdlePerformance.Work.DisplayPoll);
        if (ReadDisplay(_hwnd) is { } display &&
            (display.Screen != _layoutScreen || display.Work != _layoutWork || display.Dpi != _layoutDpi))
            QueueDisplayRecovery();
    }

    private void UpdateGeometry(bool immediateSize = false, bool restorePreferred = false, Arrangement? finalPlan = null,
        bool animatePreferredSize = false)
    {
        if (_previewOnly || _displayRefreshPending || _navActive || _internalDrag || _morphCommitted || _morphEnding) return;
        IdlePerformance.Invalidate(IdlePerformance.Change.Layout);
        using var sample = IdlePerformance.Measure(IdlePerformance.Work.Geometry);
        if (ReadDisplay(_hwnd) is not { } display) return;
        var (screen, area, dpi) = display;
        bool environmentChanged = _layoutScreen != screen || _layoutWork != area || _layoutDpi != dpi;
        if (environmentChanged && _layoutWork.Width > 0 && !restorePreferred)
        {
            QueueDisplayRecovery();
            return;
        }
        double scale = dpi / 96d;
        bool hidden = !_visible && _acrylicPrimedAt == null;
        int frameWidth = _frameWidth, frameHeight = _frameHeight;
        uint frameDpi = _frameDpi;
        if (_frameDpi != dpi)
        {
            // WinUI customizes WM_NCCALCSIZE: AdjustWindowRectEx would count the removed
            // caption. Probe a hidden, non-tiny HWND and measure the actual client instead.
            if (hidden) SetWindowPos(_hwnd,0,screen.Left+screen.Width/2,screen.Top,800,600,0x14);
            if (GetDpiForWindow(_hwnd) == dpi && GetWindowRect(_hwnd,out var outer) && GetClientRect(_hwnd,out var client))
            {
                int width = Math.Max(0, outer.Right - outer.Left - client.Right);
                int height = Math.Max(0, outer.Bottom - outer.Top - client.Bottom);
                frameWidth = width;
                frameHeight = height;
                frameDpi = dpi;
            }
            else if (_layoutDpi != 0)
            {
                // Keep the same logical frame thickness until the new HWND frame is available.
                frameWidth = (int)Math.Round(_frameWidth * (double)dpi / _layoutDpi);
                frameHeight = (int)Math.Round(_frameHeight * (double)dpi / _layoutDpi);
            }
        }
        bool displayChanged = environmentChanged || frameWidth != _frameWidth || frameHeight != _frameHeight;
        if (displayChanged && !restorePreferred && _layoutPreview != null)
        {
            QueueDisplayRecovery();
            return;
        }
        _presentationScale = scale;
        _availableWidth = (Math.Min(screen.Width, area.Width) - frameWidth) / scale;
        _availableHeight = (Math.Min(screen.Height, area.Height) - frameHeight) / scale;
        var previousMetrics=_metrics;
        _layoutScreen=screen;_layoutWork=area;_layoutDpi=dpi;
        _frameWidth = frameWidth;
        _frameHeight = frameHeight;
        _frameDpi = frameDpi;
        if (displayChanged || restorePreferred)
        {
            _resizeScales.Clear();
            immediateSize |= displayChanged || !animatePreferredSize;
            _switchDwell.Clear();
            _switchTarget = null;
            _switching = false;
        }
        _preferences.BeforeCommit=ValidateLayoutProposal;
        foreach (var (edge, sizing) in _resizeScales.ToArray())
            if (!AutomaticLayout.LayoutOf(_preferences.Value, edge).SequenceEqual(sizing.Layout))
                _resizeScales.Remove(edge);
        var plan = _layoutPreview?.Plan ?? (displayChanged ? null : finalPlan) ?? ComputeLayout(_preferences.Value);
        _edgeGeometries = plan.Items.Where(p => !_unavailableStores.Contains(p.Key))
            .ToDictionary(p => p.Key, p => p.Value.Geometry);
        if (_creation == null && _layoutPreview == null)
        {
            string message = string.Join("\n", plan.Errors.Values);
            if (message.Length != 0 && message != _lastLayoutError)
                Status(UiText.Get("CannotLayout", message), notify: _layoutPublished);
            _lastLayoutError = message;
            _layoutPublished = true;
        }
        var fallback=LayoutMetrics.Fit(CurrentLayout(_drawerId),_availableWidth,_availableHeight);
        _metrics=plan.Items.TryGetValue(_drawerId,out var placed)?placed.Metrics:fallback;
        if (_metrics.Effective.ShowNames != _preferences.Value.Global.ShowNames)
            _metrics = _metrics with { Effective = _metrics.Effective with { ShowNames = _preferences.Value.Global.ShowNames } };
        _arrangedDrawer = placed;
        var metrics=_metrics;bool metricsChanged=previousMetrics!=metrics;
        var geometry=placed?.Geometry??EdgeGeometry.Create(screen,area,dpi,_edge,metrics.Width,metrics.Height) with{Trigger=new PixelRect()};
        if(placed==null){_manualUntil=0;if(_visible)HideImmediately();}
        ApplyNavigationVisibility();
        UpdateNavigationCaption();
        if(_sizeTarget!=geometry || immediateSize)
        {
            SetSizeTarget(geometry, immediateSize);
            PrototypeLog.Write($"Geometry edge={_edge} dpi={dpi} frame={_frameWidth},{_frameHeight} drawer={geometry.Drawer} trigger={geometry.Trigger} work={geometry.Work}");
        }
        if (metricsChanged)
        {
            ApplyGridMetrics();
            if (_storeReady)
            {
                RefreshTiles();

            }
        }
        _layoutDirty = false;
    }

    private void Tick(DispatcherQueueTimer sender, object args)
    {
        var trace = _resizePerformance;
        trace?.TickStarted();
        using var resizeTick = trace?.Measure(ResizePerformance.Stage.UiTick);
        try
        {
            using var sample = IdlePerformance.Measure(_previewOnly ? IdlePerformance.Work.PreviewTick : IdlePerformance.Work.MainTick);
            TickWindow();
        }
        catch (Exception ex)
        {
            PrototypeLog.Write("drawer tick: " + ex);
            EndDrawerResize(commit: false, restore: false, reason: "tick-error");
            _lastTick = _clock.Elapsed.TotalMilliseconds;
            if (_closed) return;
            try { HideImmediately(); }
            catch (Exception cleanup)
            {
                PrototypeLog.Write("drawer recovery: " + cleanup);
                DismissOwnedMenu();
                StopExternalWheel();
                ShowWindow(_hwnd, 0);
                _visible = false;
                _interaction.Reset();
            }
        }
        finally
        {
            try { ObserveFinishedResizeEdge(); }
            catch (Exception ex)
            {
                FinishResizeEdgeObservation();
                PrototypeLog.Write("resize edge observation: " + ex.Message);
            }
            if (!_previewOnly && !_closed) IdlePerformance.Current?.Observe(this);
        }
    }

    private void TickWindow()
    {
        if (_closed) return;
        double now = _clock.Elapsed.TotalMilliseconds;
        if (now - _lastDisplayPoll >= 250)
        {
            _lastDisplayPoll = now;
            ObserveDisplay();
            if (!_visible && _frameDpi != _layoutDpi) _layoutDirty = true;
        }
        if (_displayRefreshPending && !_previewOnly && !_menuOpen && !_historyRestoring && !_committingDrag && !_committingCut)
            RecoverDisplayLayout();
        if (_displayRefreshPending)
        {
            _lastTick = now;
            if (_committingDrag) AdvanceMerge();
            return;
        }
        if (_resize != null)
        {
            _lastTick = _clock.Elapsed.TotalMilliseconds;
            TickActiveResize();
            return;
        }
        if (_historyRestoring || _committingCut) { _lastTick = _clock.Elapsed.TotalMilliseconds; return; }
        if (_committingDrag)
        {
            _lastTick = _clock.Elapsed.TotalMilliseconds;
            AdvanceMerge();
            return;
        }
        if(_previewOnly){TickTopPage(_clock.Elapsed.TotalMilliseconds);TickPreviewSurface();return;}
        if(_menuOpen){_lastTick = _clock.Elapsed.TotalMilliseconds;return;}
        if (_visible && (!IsWindowVisible(_hwnd) || IsIconic(_hwnd)))
        {
            // Show Desktop can change the native HWND without changing our animation state.
            PrototypeLog.Write("presentation: shell hid/minimized drawer; reset for fresh edge dwell");
            AbortLayoutPresentation();
            CancelItemDrag(restore: false); _dragOpened = false; _manualUntil = 0;
            HideImmediately();
        }
        double dt = (now - _lastTick) / 1000;
        _lastTick = now;
        RetryAcrylicIfDue(now);
        while (_preferences.TransferDiagnostics.TryDequeue(out var diagnostic)) PrototypeLog.Write("transfers: " + diagnostic);
        if (_preferences.TakeHistoryNotice() is { Length: > 0 } notice) Status(notice, notify: true);
        TickTopPage(now);
#if DEBUG
        if(_inspectLayout)
        {
            if((GetAsyncKeyState(0x1B)&0x8000)!=0){_inspectLayout=false;ReleaseHold(ref _inspectHold);GridHost.IsHitTestVisible=true;_previewDefinition=null;_edge=_preferences.Value.Drawers.Single(d=>d.Id==_drawerId).Edge;ClosePreviewSurfaces();StartSettling();}
            else AnimatePreviewSource(now,dt);
            return;
        }
#endif
        PollEscape();
        CheckClipboardOwnership();
        UpdateNavigationDrag();
        if(_navActive){AnimatePreviewSource(now,dt);return;}
        if (_creation != null)
        {
            if (_layoutPreview != null) AnimatePreviewSource(now, dt);
            if (_layoutWindow != null && !_creationEditorShown && (_layoutPreview == null || _interaction.Position == 1))
                PositionLayoutWindow();
            return;
        }
        if(_previewSettling){AnimatePreviewSource(now,dt);FinishSettling();return;}
        bool idleHidden = CanIdleHidden(now);
        if (!idleHidden)
        {
            SizeFolder();
            UpdateDragPointerAndMotion(dt);
        }
        else
        {
            _tileVisualsDirty = true;
            IdlePerformance.Count(IdlePerformance.Work.HiddenIdleTick);
        }
        if (_internalDrag) { TickItemDrag(now, dt); return; }
        TickExternalDrag(now);
        if (_layoutDirty) UpdateGeometry();
        if (_displayRefreshPending) return;
        if (!idleHidden) StepSize(dt);
#if DEBUG
        if(_reviewTrayPending&&_visible&&_interaction.Position==1&&_clock.Elapsed.TotalMilliseconds>_reviewTrayDelay){_reviewTrayPending=false;ShowTrayMenu();return;}
#endif
        bool hasCursor;
        Point cursor;
        using (IdlePerformance.Measure(IdlePerformance.Work.CursorPoll)) hasCursor = GetCursorPos(out cursor);
        if (!hasCursor)
        {
            if (idleHidden)
            {
                ClearIdleEdgeDwell();
                SetIdlePolling(true);
            }
            else HideImmediately();
            return;
        }
        if (!_previewOnly) IdlePerformance.Cursor(cursor.X, cursor.Y);
        bool approachingEdge = idleHidden && ApproachingEdge(cursor);
        bool wasApproachingEdge = _wasApproachingEdge;
        _wasApproachingEdge = approachingEdge;
        SetIdlePolling(idleHidden && !approachingEdge);
        if (idleHidden && !approachingEdge && !wasApproachingEdge)
        {
            ClearIdleEdgeDwell();
            return;
        }
        if(RouteEdge(cursor,now,dt))return;
        bool trigger = !RetiringPreviewSurfaces && Enabled(_drawerId) && _edgeGeometries.TryGetValue(_drawerId, out var activeEdge) &&
            activeEdge.Trigger.Contains(cursor.X, cursor.Y);
        var bounds = _geometry.Drawer;
        bool inside=_visible && _geometry.Visible(_edge,_interaction.Position).Contains(cursor.X,cursor.Y);
        // Only query shell windows when there is an interaction to evaluate.
        bool allowed = true;
        string reason = "idle";
        if (trigger || _visible) allowed = _gate.Allows(cursor, _geometry.Monitor, _visible, out reason,
            _preferences.Value.Global.AllowInFullScreen);
        bool leftDown = (GetAsyncKeyState(1) & 0x8000) != 0;
        bool explorer = ((leftDown && (trigger || _visible)) || (_dragOpened && _visible)) &&
            _gate.ExplorerForeground();
        // Intentional Explorer drag exception. No extra trigger HWND or click-blocking surface.
        bool dragCandidate = leftDown && explorer && !_ignoreExternalUntilMouseUp;
        if ((trigger && dragCandidate) || (_dragOpened && explorer && _visible))
        { allowed = true; reason = "explorer-drag"; }
        if (_visible && dragCandidate) _dragOpened = true;
        // Leaving over a different window still gets the normal 400ms grace period,
        // provided desktop remains foreground. Foreground apps/fullscreen close immediately.
        if (!allowed && _visible && reason == "pointer-not-desktop" && !trigger) allowed = true;
        bool heldOpen = _visible && (_openHolds.Active || PageTurning || ShowingOperationFeedback);
        if (heldOpen || now < _manualUntil || (_explicitSession && inside)) { _interaction.KeepOpen(); allowed = true; }
        _interaction.Update(now, dt, allowed, trigger || heldOpen,
            (inside && reason != "pointer-not-desktop") || heldOpen || now < _manualUntil || (_dragOpened && leftDown));
        if (_interaction.TargetOpen && trigger && dragCandidate) _dragOpened = true;
        if (!_interaction.TargetOpen && _openFolder != null) CloseFolder();
        RenderPosition();
        QueueResizePreviewPreparation();
        if (!_visible) { _dragOpened = false; _explicitSession = false; }
        if (_lastState != _interaction.State || (trigger && reason != _lastGate))
        {
            PrototypeLog.Write($"state={_interaction.State} gate={reason} cursor={cursor.X},{cursor.Y} foreground={GetForegroundWindow()} foregroundClass={ClassName(GetForegroundWindow())} hit={GetAncestor(WindowFromPoint(cursor), 2)} hitClass={ClassName(GetAncestor(WindowFromPoint(cursor), 2))} visible={_visible} progress={_interaction.Position:F4}");
            _lastState = _interaction.State;
        }
        _lastGate = reason;
    }

    private void RenderPosition(bool preserveSurface = false, bool resizeMaterialCoverage = false, bool preserveClientPixels = false)
    {
        var r = _geometry.Drawer;
        bool vertical = _edge == DrawerEdge.Top;
        int fullDepth = vertical ? r.Height : r.Width;
        int frameDepth = vertical ? _frameHeight : _frameWidth;
        var reveal = DrawerReveal.Create(fullDepth, frameDepth, _presentationScale, _interaction.Position);
        if (_interaction.TargetOpen && _revealTail != null)
        {
            RecordRevealTail("reopening", _lastHeight);
            FinishRevealTail("reopened");
        }
        if (_visible && !_interaction.TargetOpen && _interaction.Position > 0 &&
            reveal.AtRetractionLimit(frameDepth, _presentationScale))
        {
            // Complete this frame without publishing a native size that WinUI cannot render.
            RecordRevealTail("retraction-limit", reveal.Height);
            _interaction.Reset();
        }
        if (!_interaction.TargetOpen)
        {
            _edgeSlideOpening = false;
            FinishRightOpeningTrace();
        }
        if (_interaction.Position == 0 && !_interaction.TargetOpen)
        {
            _slidingRetraction = false;
            EndAcrylicPrime();
            if (_visible)
            {
                RecordRevealTail("before-hide", _lastHeight);
                ShowWindow(_hwnd, 0);
                ClearOperationFeedback(); ClearHistoryFades(); CloseFolder();
                _visible = false; _lastHeight = -1; RestoreHoverStyle();
                RecordRevealTail("after-hide", 0);
                FinishRevealTail("hidden");
                if (!_previewOnly) DiscardPreparedResizePreviews();
            }
            return;
        }
        if (!_visible && WaitForAcrylicPrime())
        {
            _interaction.HoldBeforeFirstFrame();
            return;
        }
        if (!_visible && _acrylicPrimedAt != null)
        {
            // Elapsed time spent waiting on composition is not animation time.
            _interaction.HoldBeforeFirstFrame();
            _interaction.Update(_clock.Elapsed.TotalMilliseconds, .012, true, true, true);
            reveal = DrawerReveal.Create(fullDepth, frameDepth, _presentationScale, _interaction.Position);
        }
        int height=reveal.Height;
        bool editingLayout = LayoutWindowHandle != 0;
        if (!_visible) _edgeSlideOpening = _interaction.TargetOpen && CanSlideInFromDesktopEdge(r);
        if (resizeMaterialCoverage)
            _slidingRetraction = false;
        else if (_visible && !_interaction.TargetOpen && _lastHeight == fullDepth)
            _slidingRetraction = CanSlideOutToDesktopEdge(r);
        else if (_slidingRetraction && !CanSlideOutToDesktopEdge(r))
            _slidingRetraction = false;
        bool slideOpening = _edgeSlideOpening && !resizeMaterialCoverage && CanSlideInFromDesktopEdge(r);
        bool moveWithoutResizing = slideOpening || _slidingRetraction;
        _drawerSlide.Y = vertical && !moveWithoutResizing ? reveal.ContentOffsetY : 0;
        _drawerSlide.X = _edge == DrawerEdge.Left && !moveWithoutResizing ? reveal.ContentOffsetY : 0;
        if (height == _lastHeight && _visible)
        {
            if (!_interaction.TargetOpen) RecordRevealTail("unchanged", height);
            // Shell desktop switches can reorder an otherwise unchanged window.
            // Do not activate or move it, and never raise it above the file picker.
            if (!editingLayout && !_pickerOpen && !_openHolds.Active && _clock.Elapsed.TotalMilliseconds - _lastZOrder >= 250)
            {
                using var sample = IdlePerformance.Measure(IdlePerformance.Work.NativeZOrder);
                SetWindowPos(_hwnd, (nint)(-1), 0, 0, 0, 0, 0x0213);
                _lastZOrder = _clock.Elapsed.TotalMilliseconds;
            }
            return;
        }
        // Keep the full layout fixed and let the native window expose the visible strip.
        if (IsIconic(_hwnd)) ShowWindow(_hwnd, 4); // SW_SHOWNOACTIVATE restores without focus.
        const uint NoSize = 0x0001, NoActivate = 0x0010, NoCopyBits = 0x0100;
        uint positionFlags = preserveClientPixels || moveWithoutResizing ? NoActivate : NoActivate | NoCopyBits;
        if (editingLayout) positionFlags |= 0x0204; // Preserve both the window and owner z-order.
        int left = _edge == DrawerEdge.Right ? r.Right - height : r.Left;
        var bounds = moveWithoutResizing ? reveal.FixedSizeBounds(r, _edge)
            : new PixelRect(left, r.Top, left + (vertical ? r.Width : height), r.Top + (vertical ? height : r.Height));
        int width = bounds.Width, visibleHeight = bounds.Height;
        var trace = ResizeTrace;
        var sampleSession = _resizePerformance?.SampleNextNativeBounds() == true ? _resize : null;
        if (trace != null)
        {
            if (resizeMaterialCoverage)
                trace.Increment(preserveClientPixels ? ResizePerformance.Counter.NativeResizePreserveClient : ResizePerformance.Counter.NativeResizeDiscardClient);
            if (_lastTracedWindowBounds == bounds)
                trace.Increment(_previewOnly ? ResizePerformance.Counter.NativeNeighborRepeatedBounds : ResizePerformance.Counter.NativeMainRepeatedBounds);
            _lastTracedWindowBounds = bounds;
            if (!_previewOnly) trace.NativeMainCalled();
        }
        if (sampleSession != null) RecordResizeEdge("before-native-bounds", bounds);
        // Prepare the existing tint for the new client extent without waiting for XAML layout.
        if (resizeMaterialCoverage) SetMaterialFillSize(width - _frameWidth, visibleHeight - _frameHeight);
        else SettleMaterialFill();
        GetClientRect(_hwnd, out var clientBefore);
        bool clientWillChange = width - _frameWidth != clientBefore.Right - clientBefore.Left ||
            visibleHeight - _frameHeight != clientBefore.Bottom - clientBefore.Top;
        if (moveWithoutResizing && !clientWillChange) positionFlags |= NoSize;
        if (clientWillChange)
        {
            // 先把内容按新的尺寸排好（此时窗口还没变大，超出的部分被旧边界裁掉），
            // 再改原生窗口。这样窗口一变大，新露出的那条区域里已经是我们排好的内容，
            // 不会出现"上一帧残留/未着色"的色带（放大时的边框就是它）。
            using (trace?.Measure(ResizePerformance.Stage.Content)) WindowSurface.UpdateLayout();
        }
        if (!_visible && _interaction.TargetOpen && RevealTailTrace.Enabled)
            StartRightOpeningTrace(height, slideOpening && _edge == DrawerEdge.Right);
        using (trace?.Measure(_previewOnly ? ResizePerformance.Stage.NativeNeighbor : ResizePerformance.Stage.NativeMain))
        using (IdlePerformance.Measure(IdlePerformance.Work.NativeBounds))
            SetWindowPos(_hwnd, (nint)(_pickerOpen ? -2 : -1), bounds.Left, bounds.Top, width, visibleHeight, positionFlags);
        if (resizeMaterialCoverage && _materialFill != null)
        {
            if (GetClientRect(_hwnd, out var client))
                SetMaterialFillSize(client.Right - client.Left, client.Bottom - client.Top);
            else
                trace?.Increment(ResizePerformance.Counter.ClientRectUnavailable);
            SettleMaterialFill();
        }
        if (clientWillChange && GetClientRect(_hwnd, out var clientAfter) &&
            (clientAfter.Right != clientBefore.Right || clientAfter.Bottom != clientBefore.Bottom))
        {
            // 窗口已经变大：把这次变大的区域在本帧内补齐，不等下一次 Rendering。
            using (trace?.Measure(ResizePerformance.Stage.Content)) WindowSurface.UpdateLayout();
        }
        if (sampleSession != null)
        {
            RecordResizeEdge("after-native-bounds", bounds);
            sampleSession.SampleNextLayout = bounds;
        }
        if (!_visible)
        {
            PrototypeLog.Write($"reveal: first-visible edge={_edge} fixedSize={slideOpening} material={_displayedMaterial} " +
                $"native={width}x{visibleHeight} cover={MaterialCover.ActualWidth:F1}x{MaterialCover.ActualHeight:F1} dpi={_layoutDpi}");
            if (_acrylicPrimedAt != null)
            {
                _acrylicPrimedAt = null;
                PrototypeLog.Write($"material: reveal hwnd={_hwnd} state={_acrylicController?.State} progress={_interaction.Position:F4}");
            }
            else ShowWindow(_hwnd, 4);
            _visible = true;
            if (editingLayout) PlaceBelowLayoutWindow();
            else if (_explicitSession) SetForegroundWindow(_hwnd);
        }
        _lastHeight = height;
        if (height == fullDepth)
        {
            _edgeSlideOpening = false;
            if (_interaction.TargetOpen) _slidingRetraction = false;
        }
        if (_rightOpeningFrames != null) RecordRightOpeningFrame(height);
        if (!_interaction.TargetOpen) RecordRevealTail("resized", height);
        _lastZOrder = _clock.Elapsed.TotalMilliseconds;
    }

    // Keep the unseen portion beyond the virtual desktop, never on another monitor.
    private bool CanSlideInFromRight(PixelRect drawer) => !_previewOnly && !_surfaceFree &&
        _edge == DrawerEdge.Right && drawer.Right == _geometry.Monitor.Right &&
        drawer.Right == GetSystemMetrics(76) + GetSystemMetrics(78);

    private bool CanSlideInFromDesktopEdge(PixelRect drawer) => !_previewOnly && !_surfaceFree &&
        (_edge == DrawerEdge.Right ? CanSlideInFromRight(drawer) : CanSlideOutToDesktopEdge(drawer));

    private bool CanSlideOutToDesktopEdge(PixelRect drawer) => _edge switch
    {
        DrawerEdge.Top => drawer.Top == _geometry.Monitor.Top && drawer.Top == GetSystemMetrics(77),
        DrawerEdge.Left => drawer.Left == _geometry.Monitor.Left && drawer.Left == GetSystemMetrics(76),
        _ => false
    };

    private void HideImmediately()
    {
        _edgeSlideOpening = false;
        _slidingRetraction = false;
        FinishRightOpeningTrace();
        FinishRevealTail("immediate-hide");
        FinishNavigationTransition();
        EndNavigation(false);
        ClearOperationFeedback();
        EndDrawerResize(commit: false, restore: false, reason: _displayRefreshPending ? "display-changed" : "hidden");
        if (_readingClipboard) CancelClipboardPaste();
        EndSelectionBox(cancel: true);
        CancelItemDrag(restore: false);
        EndExternalDrag();
        DismissOwnedMenu();
        ClearHistoryFades();
        RemoveMergeVisual();
        SnapTopPage();
        _pageWheel.Reset();
        CloseFolder();
        _explicitSession = false;
        _interaction.Reset();
        EndAcrylicPrime();
        ShowWindow(_hwnd, 0);
        _visible = false;
        _lastHeight = -1;
        if (!_previewOnly) DiscardPreparedResizePreviews();
        RestoreHoverStyle();
    }

    private nint WindowMessage(nint hwnd, uint msg, nuint wp, nint lp, nuint id, nuint data)
    {
        try { return HandleWindowMessage(hwnd, msg, wp, lp); }
        catch (Exception ex) { PrototypeLog.Write($"window message 0x{msg:X}: {ex}"); return DefSubclassProc(hwnd, msg, wp, lp); }
    }

    private nint HandleWindowMessage(nint hwnd, uint msg, nuint wp, nint lp)
    {
        if (msg == 0x0010 && !_allowExit) { ExitDrawer(); return 0; } // WM_CLOSE
        if (msg == 0x0082) WindowClassBackground.Release(hwnd); // WM_NCDESTROY
        if (msg == 0x0083) return 0; // WM_NCCALCSIZE: keep the borderless client at the window bounds.
        if (msg == 0x0014 && ClearTransparentClient(hwnd, (nint)wp)) return 1; // WM_ERASEBKGND
        if (msg == 0x0020 && _internalDrag && _itemDrop == null)
        { UpdateItemDropCursor(); return 1; }
        if (msg == 0x0020 && ApplyResizeCursor()) return 1;
        if((_previewOnly || _acrylicPrimedAt != null) && msg==0x84)return -1;
        if (msg == 0x0084 && (CanStartNavigation || _nativeNavigationCapture) &&
            IsAttachedTopFrame(ScreenPointFromMessage(lp))) return 18; // HTBORDER
        if (msg == 0x00A1 && StartFrameNavigation(lp)) return 0; // WM_NCLBUTTONDOWN
        if (_nativeNavigationCapture)
        {
            if (msg == 0x0202) // WM_LBUTTONUP
            {
                var point = ScreenPointFromMessage(lp);
                if (ClientToScreen(_hwnd, ref point)) CompleteNavigation(point);
                else EndNavigation(false);
                return 0;
            }
            if (msg == 0x00A2) { CompleteNavigation(ScreenPointFromMessage(lp)); return 0; } // WM_NCLBUTTONUP
            if (msg is 0x0215 or 0x001F) EndNavigation(false); // WM_CAPTURECHANGED, WM_CANCELMODE
        }
        if (msg == 0x0021) return 3; // WM_MOUSEACTIVATE -> MA_NOACTIVATE (deliver the click)
        if(msg==0x0024&&lp!=0) // The default caption minimum otherwise silently widens single-column drawers.
        {
            var result = DefSubclassProc(hwnd,msg,wp,lp);
            try { var sizes=Marshal.PtrToStructure<MinMaxInfo>(lp);sizes.MinTrackSize=new Point{X=1,Y=1};Marshal.StructureToPtr(sizes,lp,false); }
            catch (Exception ex) { PrototypeLog.Write("window tracking limits: " + ex); }
            return result;
        }
        const uint SetWorkArea = 0x002F;
        if (!_previewOnly && msg == 0x0218 && wp is 7 or 18) // WM_POWERBROADCAST resume
        {
            _lastDisplayPoll = double.NegativeInfinity;
            SetIdlePolling(false);
        }
        if (msg is 0x007E or 0x02E0 or 0x001A)
        {
            if (!_previewOnly) IdlePerformance.Invalidate(IdlePerformance.Change.Display);
            _layoutDirty = true;
            if (!_previewOnly && (msg == 0x007E || msg == 0x02E0 && (uint)(wp & 0xffff) != _layoutDpi ||
                msg == 0x001A && wp == SetWorkArea))
            {
                QueueDisplayRecovery();
            }
            else if (msg == 0x001A) _lastDisplayPoll = double.NegativeInfinity;
        }
        if (msg is 0x031E or 0x007E or 0x001A && _backdropSettings != null)
            QueueBackdropRefresh(reapplyHost: true, compositionChanged: msg == 0x031E);
        if (!_previewOnly && msg == _taskbarCreated) Shell_NotifyIcon(0, ref _tray);
        if (msg == 0x8001 && ((long)lp == 0x0205 || (long)lp == 0x0202))
        {
            SetIdlePolling(false);
            ShowTrayMenu();
            return 0;
        }
        return DefSubclassProc(hwnd, msg, wp, lp);
    }

    private void FinishExitDrawer()
    {
        _timer.Stop();
        AbortLayoutPresentation();
        FinishNameEdit(false);
        HideImmediately();
        RemoveMergeVisual();
        StopAcrylic();
        StopNativeBackdrop();
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        FinishItemDragTrace(landed: false);
        FinishRightOpeningTrace();
        FinishResizeEdgeObservation();
        if (_nameTile != null) FinishNameEdit(false);
        FinishNavigationTransition();
        EndNavigation(false);
        ClearOperationFeedback();
        if (_feedbackTimer != null) _feedbackTimer.Tick -= FeedbackExpired;
        ClearHistoryFades();
        EndDrawerResize(commit: false, restore: false, reason: "window-closed");
        AbortPageTransition();
        _importCancellation?.Cancel();
        EndExternalDrag(landing: true);
        StopExternalWheel();
        ReleaseHold(ref _itemDragHold);
        ReleaseHold(ref _navigationHold);
        ReleaseHold(ref _morphHold);
        ReleaseHold(ref _folderHold);
        ReleaseHold(ref _nameHold);
        ReleaseHold(ref _settleHold);
        _folderGeneration++;
        _backdropGeneration++;
        DisposeFloatingGhost();
        ClearDragTrail(removeVisuals: false);
        _iconScales.Clear(); _hoveredTile = _singleSelectedTile = null;
        DismissOwnedMenu();
        _iconCache.Clear();
        _iconRetryAfter.Clear();
        _iconReads.Clear();
        if (_topScrollViewer != null) _topScrollViewer.ViewChanged -= TopScrollChanged;
        StopAcrylic();
        StopNativeBackdrop();
        DisposeMaterialFill();
        if(!_previewOnly) ClosePreviewSurfaces(true);
        _layoutWindow?.Close(); _layoutWindow=null;
        _confirmationWindow?.Close(); _confirmationWindow=null;
        _timer.Stop();
        _timer.Tick -= Tick;
        ShortcutGrid.LayoutUpdated -= TileLayoutUpdated;
        if (!_previewOnly)
        {
            IdlePerformance.Current?.Stop();
            if (!_historyRestoring && !_importing && !_committingDrag && !_committingCut)
                _preferences.CloseHistorySession();
            Shell_NotifyIcon(2, ref _tray);
        }
        if (_appIconSmall != 0)
        {
            SendMessageW(_hwnd, 0x0080, 0, 0);
            DestroyIcon(_appIconSmall);
        }
        if (_appIconLarge != 0)
        {
            SendMessageW(_hwnd, 0x0080, 1, 0);
            if (_appIconLarge != _appIconSmall) DestroyIcon(_appIconLarge);
        }
        _appIconSmall = _appIconLarge = 0;
        WindowClassBackground.Release(_hwnd);
        RemoveWindowSubclass(_hwnd, _subclass, 1);
        PrototypeLog.Write(_previewOnly?"Closed preview surface: timer stopped, subclass released.":"Closed: timer stopped, tray removed, subclass released.");
    }
}
