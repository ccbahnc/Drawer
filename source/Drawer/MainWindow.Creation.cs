using System;
using System.Linq;
using Drawer.Core;
using static Drawer.NativeMethods;
namespace Drawer;
public sealed partial class MainWindow
{
    private IDisposable? _creationHold;
    private bool _creationEditorShown;
    private void BeginCreate(DrawerEdge edge)
    {
        if(_exitRequested||_creation!=null||NavigationPressed||_pressedTile!=null||_internalDrag||_externalDragging||_pickerOpen||_importing||_settingsOpen||_openHolds.Active||_closed)return;
        UpdateGeometry();
        CheckLayoutRevision(_screenRevision);
        var draft = _preferences.Draft(edge) with { Rows = 1, Columns = 1 };
        var session = new DrawerCreation(draft);
        _beforeCreation=_drawerId;HideImmediately();_creation=session;_layoutPreview=null;_creationEditorShown=false;
        _drawerId=draft.Id;_edge=edge;_store=new ShortcutStore(_preferences.Resolve(draft.Storage),_preferences.Root);_storeReady=true;
        _tiles.Clear();_slotMotions.Clear();_previewDefinition=draft;_settingsOpen=true;_creationHold=HoldOpen();DropSurface.IsHitTestVisible=false;
        try
        {
            ExposeReviewSurface();
            UpdateGeometry();
            int revision = _screenRevision;
            LayoutSnapshot? confirmed = null;
            _layoutWindow=new LayoutWindow(() => UiText.Get("CreateDrawerTitle", draft.Name),draft.Rows,draft.Columns,(rows,columns,edit)=>
            {
                if (_closed || !ReferenceEquals(_creation, session))
                    throw new InvalidOperationException(UiText.Get("LayoutDisplayChanged"));
                if (edit == LayoutEdit.Restore && _displayRefreshPending) return;
                CheckLayoutRevision(revision);
                bool initialPreview = _layoutPreview == null;
                var snapshot = edit == LayoutEdit.Restore && confirmed != null
                    ? confirmed : BuildLayoutPreview(session.Propose(_preferences.Value, rows, columns), draft.Id);
                var proposal = snapshot.Collection;
                if(edit == LayoutEdit.Commit)
                {
                    if (!session.Confirmed || rows != session.Definition.Rows || columns != session.Definition.Columns)
                    {
                        using var history = BeginDataHistory();
                        _preferences.Commit(proposal);
                    }
                    session.Accept(rows,columns);_stores[draft.Id]=_store;
                    confirmed = snapshot;
                    PrototypeLog.Write("creation: confirmed "+draft.Id+" "+rows+"x"+columns);
                }
                _layoutPreview = snapshot;
                _previewDefinition = proposal.Drawers.Single(d => d.Id == draft.Id);
                UpdateGeometry(immediateSize: initialPreview);
                if (_tiles.Count == 0) RefreshTiles();
                _interaction.KeepOpen();
                RenderPosition(preserveSurface: true);
                PrototypeLog.Write("creation: preview "+draft.Id+" "+rows+"x"+columns+" reveal="+_interaction.Position.ToString("F3"));
                ShowPreviewEdge(proposal, snapshot.Plan, edge);
            },_presentationScale,IsTrayIconPoint);
            _layoutWindow.Closed+=(_,_)=>FinishCreation(session);
            OwnLayoutWindow();
            _layoutWindow.ShowInitialPreview();
            confirmed = _layoutPreview;
            if (_layoutPreview == null) PositionLayoutWindow();
        }
        catch{_layoutWindow?.Close();FinishCreation(session);throw;}
    }
    private void PositionLayoutWindow()
    {
        if(_layoutWindow==null)return;
        if (_creation != null) _creationEditorShown = true;
        var work=_layoutWork;var size=_layoutWindow.AppWindow.Size;
        int w=Math.Clamp(size.Width,1,Math.Max(1,work.Width)),h=Math.Clamp(size.Height,1,Math.Max(1,work.Height));
        if (w != size.Width || h != size.Height) _layoutWindow.AppWindow.Resize(new(w,h));
        int x=_edge==DrawerEdge.Right?work.Left+20:work.Right-w-20,y=_edge==DrawerEdge.Top?work.Bottom-h-20:work.Top+20;
        _layoutWindow.AppWindow.Move(new Windows.Graphics.PointInt32(Math.Clamp(x,work.Left,Math.Max(work.Left,work.Right-w)),
            Math.Clamp(y,work.Top,Math.Max(work.Top,work.Bottom-h))));
        _layoutWindow.Activate();
    }

    private nint LayoutWindowHandle => (_previewOwner ?? this)._layoutWindow is { } layout
        ? WinRT.Interop.WindowNative.GetWindowHandle(layout) : 0;

    private void OwnLayoutWindow()
    {
        const uint noMoveOrSizeOrActivate = 0x0213;
        // A topmost owner would also promote its settings window. Keep this
        // group in the normal z-order while the editor is open.
        SetWindowPos(_hwnd, (nint)(-2), 0, 0, 0, 0, noMoveOrSizeOrActivate);
        var window = _layoutWindow!;
        SetWindowLongPtrW(LayoutWindowHandle, -8, _hwnd); // GWLP_HWNDPARENT sets the owner of a top-level HWND.
        window.Activated += LayoutWindowActivated;
        window.Closed += (_, _) => window.Activated -= LayoutWindowActivated;
        foreach (var surface in _previewSurfaces.Values) surface.PlaceBelowLayoutWindow();
    }

    private void LayoutWindowActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == Microsoft.UI.Xaml.WindowActivationState.Deactivated) return;
        foreach (var surface in _previewSurfaces.Values) surface.PlaceBelowLayoutWindow();
    }

    private void PlaceBelowLayoutWindow()
    {
        nint panel = LayoutWindowHandle;
        if (!_previewOnly || _closed || panel == 0) return;
        const uint noMoveOrSizeOrActivate = 0x0213;
        SetWindowPos(_hwnd, (nint)(-2), 0, 0, 0, 0, noMoveOrSizeOrActivate);
        if (IsWindowVisible(panel)) SetWindowPos(_hwnd, panel, 0, 0, 0, 0, noMoveOrSizeOrActivate);
    }
    private void FinishCreation(DrawerCreation session)
    {
        if(!ReferenceEquals(session,_creation))return;
        KeepEscapeOnClosedSurface();
        _creation=null;_layoutPreview=null;_creationEditorShown=false;_layoutWindow=null;_previewDefinition=null;_settingsOpen=false;
        if (!_closed) DropSurface.IsHitTestVisible=true;
        ReleaseHold(ref _creationHold);
        // The main window takes the original drawer's identity again on cancel.
        // Remove its preview and the other draft surfaces before showing it.
        ClosePreviewSurfaces(!session.Confirmed || _closed || _displayRefreshPending);_manualUntil=0;
        if(_closed)return;
        if(!session.Confirmed)
        {
            HideImmediately();
            if(_stores.ContainsKey(_beforeCreation))SelectDrawer(_beforeCreation);
            else ApplyPreferences();
            PrototypeLog.Write($"creation: canceled without persistence {session.Definition.Id} previewsRemaining={_previewSurfaces.Count}");
        }
        else {ApplyPreferences();PrototypeLog.Write("creation: finished "+session.Definition.Id);}
        if (!_displayRefreshPending && Enabled(_drawerId)){_interaction.KeepOpen();StartSettling();}
        else HideImmediately();
        RestoreHoverStyle();
    }
}
