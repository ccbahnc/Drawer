using System;
using System.Linq;
using Drawer.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using static Drawer.NativeMethods;
namespace Drawer;
public sealed partial class MainWindow
{
    private PixelRect _layoutScreen,_layoutWork;private uint _layoutDpi=96;private string _lastLayoutError="";
    private bool _layoutPublished;
    private Pointer? _navPointer;private bool _navActive;private double _navAt;private NativeMethods.Point _navStart;
    private LayoutSnapshot? _navCandidate;
    private DrawerCollection? _navOriginal;
    private int _screenRevision,_navRevision;
    private ArrangedDrawer? _navSource;private readonly DrawerInsertion _drawerInsertion=new();
    private Arrangement? _navBaseline;
    private IDisposable? _navigationHold;
    private UIElement? _navigationCapture;
    private bool _nativeNavigationCapture;
    private bool NavigationPressed => _navPointer != null || _nativeNavigationCapture;
    private (DrawerEdge Edge, int Insertion)? _navDrop;
    private (DrawerEdge Edge, int Insertion)? _navEvaluated;
    private bool _navNoSpace;
    private double? _navButtonUpSince;
    private sealed record LayoutSnapshot(DrawerCollection Collection, Arrangement Plan);
    private LayoutSnapshot? _layoutPreview;
    private DrawerCollection VisualCollection() => _layoutPreview is { } preview
        ? preview.Collection with { Global = _preferences.Value.Global }
        : _preferences.Value;

    private void CheckLayoutRevision(int revision)
    {
        ObserveDisplay();
        if (_displayRefreshPending || revision != _screenRevision)
            throw new ArgumentException(UiText.Get("LayoutDisplayChanged"));
    }

    private LayoutSnapshot BuildLayoutPreview(DrawerCollection proposal, Guid id)
    {
        var plan = ComputeLayout(proposal);
        try { ValidateLayoutProposal(proposal, plan); }
        catch (ArgumentException ex) { throw new LayoutSpaceException(ex); }
        if (!plan.Items.ContainsKey(id)) throw new LayoutSpaceException();
        return new(proposal, plan);
    }
    private void ValidateLayoutProposal(DrawerCollection value) => ValidateLayoutProposal(value, null);
    private void ValidateLayoutProposal(DrawerCollection value, Arrangement? plan)
    {
        if (_historyRestoring) return;
        var previous = _preferences.Value;
        if (Enum.GetValues<DrawerEdge>().All(edge =>
            AutomaticLayout.LayoutOf(previous, edge).SequenceEqual(AutomaticLayout.LayoutOf(value, edge)))) return;
        AutomaticLayout.ValidateChange(previous, value, _layoutScreen, _layoutWork, _layoutDpi,
            _frameWidth, _frameHeight, plan ?? ComputeLayout(value));
    }
    private void UpdateNavigationCaption()
    {
        var definition=_previewDefinition??VisualCollection().Drawers.FirstOrDefault(d=>d.Id==_drawerId);
        var name = definition?.Name ?? "Drawer";
        if (DrawerName.Text != name)
        {
            DrawerName.Text = name;
            ToolTipService.SetToolTip(DrawerName, name);
        }
        GearButton.IsEnabled=definition!=null&&!_navActive;
    }
    private void Gear_Click(object sender,RoutedEventArgs e)
    {
        var anchor = ScreenAnchor(GearButton, _edge == DrawerEdge.Right ? GearButton.ActualWidth : 0, GearButton.ActualHeight);
        ShowDrawerSettings(_drawerId, anchor);
    }

    private DrawerMenu CreateDrawerSettingsMenu()
    {
        var menu = new DrawerMenu();
        if (ShowNavigation) menu.Add(1, UiText.Get("Rename"));
        var layout = new DrawerMenu();
        layout.Add(2, UiText.Get("RowsAndColumns"));
        layout.Add(5, UiText.Get("ResetZoom"));
        menu.AddSubmenu(UiText.Get("LayoutAndSize"), layout);
        menu.Separate();
        AppendSharedDrawerMenu(menu);
        menu.Separate();
        var delete = new DrawerMenu();
        AppendContextAction(delete, ContextMenuCommand.DeleteDrawer, "ConfirmDelete", danger: true);
        menu.AddSubmenu(UiText.Get("DeleteDrawer"), delete);
        return menu;
    }

    private async void ShowDrawerSettings(Guid id, NativeMethods.Point anchor)
    {
        if (EditingDataBlocked || _openHolds.Active || _displayRefreshPending || id != _drawerId ||
            !_preferences.Value.Drawers.Any(d => d.Id == id)) return;
        try
        {
            int command = await ShowOwnedMenu(CreateDrawerSettingsMenu(), anchor);
            if (command != 0 && !_displayRefreshPending) RunDrawerSettingsCommand(id, command);
        }
        catch (Exception ex) { if (!_closed) ErrorStatus(UiText.Get("SettingsFailed", ex.Message)); }
    }

    private void RunDrawerSettingsCommand(Guid id, int command)
    {
        if (_closed || _exitRequested || _drawerId != id) return;
        try
        {
            if (RunSharedDrawerCommand(command)) return;
            switch (command)
            {
                case 1 when ShowNavigation: RenameDrawerFromMenu(id); break;
                case 2: OpenLayout(id); break;
                case (int)ContextMenuCommand.DeleteDrawer: DeleteDrawer(id); break;
                case 5: RestoreDefaultDrawerSize(id); break;
            }
        }
        catch (Exception ex) { ErrorStatus(UiText.Get("SettingsFailed", ex.Message)); }
    }
    private void WireNavigationDrag()
    {
        WindowSurface.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Navigation_Pressed), true);
        WindowSurface.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Navigation_Released), true);
        WindowSurface.PointerCanceled += Navigation_Canceled;
        WindowSurface.PointerCaptureLost += Navigation_CaptureLost;
    }

    private bool CanStartNavigation => _visible && _arrangedDrawer != null && !EditingDataBlocked && !_editingName &&
        !_openHolds.Active && !_displayRefreshPending && _openFolder == null &&
        _clock.Elapsed.TotalMilliseconds >= _suppressLaunchUntil;

    private bool IsTopDragPoint(NativeMethods.Point cursor)
    {
        if (!GetWindowRect(_hwnd, out var window) || cursor.X < window.Left || cursor.X >= window.Right ||
            cursor.Y < window.Top || !DragPoint(cursor, GridHost, out var point)) return false;
        // GridHost starts at the first content row. Everything above it, including
        // the outer frame and both sides of the content padding, is the drag band.
        return point.Y < 0;
    }

    private void BeginNavigation(NativeMethods.Point cursor)
    {
        _navStart = cursor;
        _navAt = _clock.Elapsed.TotalMilliseconds;
        _navRevision = _screenRevision;
        _navigationHold = HoldOpen();
        PrototypeLog.Write("navigation: pressed " + _drawerId);
    }

    private void Navigation_Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (!CanStartNavigation || _resize != null ||
            e.GetCurrentPoint(WindowSurface).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed ||
            !TryGetResizePointer(e, out var cursor) ||
            (CanResizeDrawer && ResizeEdgesAt(cursor) != ResizeEdge.None) || !IsTopDragPoint(cursor)) return;
        for (var node = e.OriginalSource as DependencyObject; node != null && node != WindowSurface;
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            if (node is Button or TextBox) return;
        // Keep the title's own double-tap recognition while the pointer is held.
        var capture = e.OriginalSource as UIElement ?? WindowSurface;
        if (!capture.CapturePointer(e.Pointer)) return;
        _navigationCapture = capture;
        _navPointer = e.Pointer;
        BeginNavigation(cursor);
        e.Handled = true;
    }

    private bool StartFrameNavigation(nint parameter)
    {
        if (!CanStartNavigation) return false;
        var cursor = ScreenPointFromMessage(parameter);
        if (!IsAttachedTopFrame(cursor)) return false;
        SetCapture(_hwnd);
        if (GetCapture() != _hwnd) return false;
        _nativeNavigationCapture = true;
        BeginNavigation(cursor);
        return true;
    }

    private bool IsAttachedTopFrame(NativeMethods.Point cursor)
    {
        if (!_visible || _edge != DrawerEdge.Top || !GetWindowRect(_hwnd, out var window) ||
            cursor.X < window.Left || cursor.X >= window.Right || cursor.Y < window.Top) return false;
        var client = new NativeMethods.Point();
        return ClientToScreen(_hwnd, ref client) && cursor.Y < client.Y;
    }

    private static NativeMethods.Point ScreenPointFromMessage(nint parameter) => new()
    {
        X = unchecked((short)(long)parameter),
        Y = unchecked((short)((long)parameter >> 16))
    };

    private void Navigation_Released(object sender,PointerRoutedEventArgs e)
    {
        if (_navPointer?.PointerId != e.Pointer.PointerId) return;
        if (e.GetCurrentPoint(WindowSurface).Properties.IsLeftButtonPressed) return;
        if (TryGetResizePointer(e, out var cursor)) CompleteNavigation(cursor);
        else EndNavigation(false);
        e.Handled = true;
    }

    private void CompleteNavigation(NativeMethods.Point cursor)
    {
        ObserveDisplay();
        bool matches = _navActive && _navRevision == _screenRevision && _navDrop is { } shown &&
            NavigationEdgeAt(cursor) == shown.Edge &&
            NavigationInsertion(shown.Edge, cursor) == shown.Insertion;
        EndNavigation(matches);
    }
    private void Navigation_Canceled(object sender,PointerRoutedEventArgs e){if(_navPointer?.PointerId==e.Pointer.PointerId){PrototypeLog.Write("navigation: pointer canceled "+e.OriginalSource.GetType().Name);EndNavigation(false);}}
    private void Navigation_CaptureLost(object sender,PointerRoutedEventArgs e){if(ReferenceEquals(e.OriginalSource,_navigationCapture))Navigation_Canceled(sender,e);}

    private DrawerEdge? NavigationEdgeAt(NativeMethods.Point cursor)
    {
        if (cursor.X < _layoutWork.Left || cursor.X > _layoutWork.Right || cursor.Y < _layoutWork.Top || cursor.Y > _layoutWork.Bottom) return null;
        int top = cursor.Y - _layoutWork.Top, left = cursor.X - _layoutWork.Left, right = _layoutWork.Right - cursor.X;
        int distance = Math.Min(top, Math.Min(left, right));
        return distance > DrawerSettings.DrawerSnapDistance * _presentationScale ? null
            : top == distance ? DrawerEdge.Top : left == distance ? DrawerEdge.Left : DrawerEdge.Right;
    }

    private int NavigationInsertion(DrawerEdge edge, NativeMethods.Point cursor) =>
        _drawerInsertion.Find(_navOriginal!, _drawerId, edge, edge == DrawerEdge.Top ? cursor.X : cursor.Y, _navBaseline!, _presentationScale);

    private void UpdateNavigationDrag()
    {
        if(!NavigationPressed)return;
        if(_screenRevision!=_navRevision){EndNavigation(false);Status(UiText.Get("DisplayChanged"));return;}
        if ((GetAsyncKeyState(1) & 0x8000) == 0)
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            if (now - (_navButtonUpSince ??= now) > 100)
            {
                if (_nativeNavigationCapture && GetCapture() == _hwnd && GetCursorPos(out var release)) CompleteNavigation(release);
                else EndNavigation(false);
            }
            return;
        }
        _navButtonUpSince = null;
        if(!GetCursorPos(out var cursor))return;
        double moved=Math.Pow(cursor.X-_navStart.X,2)+Math.Pow(cursor.Y-_navStart.Y,2);
        if(!_navActive&&_clock.Elapsed.TotalMilliseconds-_navAt>=DrawerSettings.DrawerDragHoldMs&&moved>=Math.Pow(6*_presentationScale,2))
        {
            if (_arrangedDrawer is not { } placed) { EndNavigation(false); return; }
            _navOriginal=_preferences.Value;_navSource=placed with{Metrics=_metrics,Geometry=_geometry};_drawerInsertion.Clear();_navActive=true;
            _navBaseline = ComputeLayout(_navOriginal);
            PrototypeLog.Write("navigation: real drag started "+_drawerId);GearButton.IsEnabled=false;GridHost.IsHitTestVisible=false;
        }
        if(!_navActive)return;
        try
        {
            var source = _navOriginal!.Drawers.Single(d => d.Id == _drawerId);
            var edge = NavigationEdgeAt(cursor);
            if(edge!=null)
            {
                var target = (Edge: edge.Value, Insertion: NavigationInsertion(edge.Value, cursor));
                if (_navEvaluated != target)
                {
                    _navEvaluated = target;
                    _navCandidate = null;
                    _navDrop = null;
                    _navNoSpace = false;
                    try
                    {
                        var candidate = DrawerMove.Propose(_navOriginal, _drawerId, target.Edge, target.Insertion);
                        _navCandidate = BuildLayoutPreview(candidate, _drawerId);
                        _navDrop = target;
                    }
                    catch (LayoutSpaceException) { _navNoSpace = true; }
                    catch (ArgumentException ex) { PrototypeLog.Write("navigation: rejected " + ex.Message); }
                    ShowPreviewEdges(_navCandidate?.Collection ?? _navOriginal,
                        _navCandidate?.Plan ?? _navBaseline!, source.Edge, target.Edge);
                    if (_navCandidate is { } snapshot)
                        Present(snapshot.Collection.Drawers.Single(d => d.Id == _drawerId), snapshot.Plan.Items[_drawerId], true);
                }
            }
            else if (_navEvaluated != null)
            {
                _navEvaluated = _navDrop = null;
                _navCandidate = null;
                _navNoSpace = false;
                foreach (var surface in _previewSurfaces.Values) { surface._previewWanted = false; surface._retireSurface = false; }
            }
            if (_navCandidate == null)
            {
                var original=_navSource!.Geometry.Drawer;int width=original.Width,height=original.Height;
                int x=Math.Clamp(cursor.X-_navStart.X+original.Left,_layoutWork.Left,_layoutWork.Right-width),y=Math.Clamp(cursor.Y-_navStart.Y+original.Top,_layoutWork.Top,_layoutWork.Bottom-height);
                Present(source,_navSource with{Geometry=_navSource.Geometry with{Drawer=new(x,y,x+width,y+height),Trigger=new()}},true);
            }
            PaintInvalidPlacement(_navNoSpace);
        }
        catch(Exception ex){EndNavigation(false);ErrorStatus(UiText.Get("DragPreviewFailed", ex.Message));}
    }
    private void EndNavigation(bool commit)
    {
        if(!NavigationPressed)return;
        var pointer = _navPointer; var candidate = _navCandidate; bool wasActive = _navActive;
        bool nativeCapture = _nativeNavigationCapture;
        _nativeNavigationCapture = false;
        _navPointer=null;_navActive=false;_navCandidate=null;_navDrop=null;_navEvaluated=null;_navNoSpace=false;_navButtonUpSince=null;_navOriginal=null;_navSource=null;_navBaseline=null;_drawerInsertion.Clear();_previewDefinition=null;
        var capture = _navigationCapture;
        _navigationCapture = null;
        ReleaseHold(ref _navigationHold);
        if (nativeCapture && GetCapture() == _hwnd) ReleaseCapture();
        if (pointer != null) capture?.ReleasePointerCapture(pointer);
        if (_closed) return;
        GearButton.IsEnabled=true;GridHost.IsHitTestVisible=true;PaintInvalidPlacement(false);
        PrototypeLog.Write("navigation: real drag end commit="+commit+" valid="+(candidate!=null));
        if(!wasActive)return;
        ApplyNavigationResult(candidate, commit);
    }

    private void ApplyNavigationResult(LayoutSnapshot? candidate, bool commit)
    {
        Arrangement? committed = null;
        try
        {
            if(commit&&candidate!=null)
            {
                CheckLayoutRevision(_navRevision);
                using (BeginDataHistory()) _preferences.Commit(candidate.Collection);
                committed = candidate.Plan;
                PrototypeLog.Write($"navigation: saved {_drawerId} edge={candidate.Collection.Drawers.Single(d => d.Id == _drawerId).Edge}");
            }
        }
        catch(Exception ex){ErrorStatus(UiText.Get("DrawerMoveFailed", ex.Message));}
        finally
        {
            ClosePreviewSurfaces(_closed || _displayRefreshPending, committed);
            if(!_closed){_edge=_preferences.Value.Drawers.Single(d=>d.Id==_drawerId).Edge;StartSettling(committed);}
        }
    }
}
