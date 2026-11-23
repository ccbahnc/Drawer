using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Drawer.Core;
using Microsoft.UI.Xaml;
using static Drawer.NativeMethods;
namespace Drawer;
public sealed partial class MainWindow
{
    private readonly DrawerCatalog _catalog=new(ReviewDataRoot());
    private static string? ReviewDataRoot()
    {
#if DEBUG
        if (Array.Exists(Environment.GetCommandLineArgs(), a => a == "--review21-reflow"))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Drawer", "Review21Reflow");
        if (Array.Exists(Environment.GetCommandLineArgs(), a => a == "--review21"))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Drawer", "Review21");
        if (Array.Exists(Environment.GetCommandLineArgs(), a => a == "--review20"))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Drawer", "Review20");
        if (Array.Exists(Environment.GetCommandLineArgs(), a => a == "--review19"))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Drawer", "Review19");
        if(Array.Exists(Environment.GetCommandLineArgs(),a=>a=="--review16-last"))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Drawer","Review16Last");
        if(Array.Exists(Environment.GetCommandLineArgs(),a=>a=="--review16"))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Drawer","Review16");
        if(Array.Exists(Environment.GetCommandLineArgs(),a=>a=="--review"))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Drawer","Review14");
#endif
        return null;
    }
    private readonly Dictionary<Guid,ShortcutStore> _stores=new();
    private readonly HashSet<Guid> _unavailableStores = new();
    private Dictionary<Guid,EdgeGeometry> _edgeGeometries=new();
    private Guid _drawerId;
    private DrawerEdge _edge=DrawerEdge.Top;
    private Guid? _switchTarget;
    private bool _switching;
    private readonly DrawerDwell _switchDwell=new();
    private bool DrawerOperationActive => _openHolds.Active || ReviewLocked || PageTurning || _pressedTile != null ||
        _displayRefreshPending || RetiringPreviewSurfaces || !MotionSettled(_positionMotion) || !MotionSettled(_sizeMotion);
    private bool ExplorerDragHeld => _dragOpened && (GetAsyncKeyState(1) & 0x8000) != 0;
    private bool InteractionLocked => DrawerOperationActive || ExplorerDragHeld;
    private bool ReviewLocked
    {
        get {
#if DEBUG
            return Array.Exists(Environment.GetCommandLineArgs(),a=>a=="--review"||a=="--review16"||a=="--review19"||a=="--review20"||a=="--review21"||a=="--review21-reflow") && _explicitSession && _clock.Elapsed.TotalMilliseconds<_manualUntil;
#else
            return false;
#endif
        }
    }
    private void InitializeDrawers()
    {
        _preferences = new MultiDrawerCatalog(_catalog.Root);
        try { _preferences.Load(_catalog); }
        catch (Exception ex) when (MultiDrawerCatalog.IsReadFailure(ex))
        { throw new DrawerStartupDataException(ex); }
        foreach (var drawer in _preferences.Value.Drawers) LoadDrawerStore(drawer);
        var first = AvailableDrawer();
        _drawerId=first?.Id??Guid.Empty;_edge=first?.Edge??DrawerEdge.Top;
        _store=first!=null?_stores[first.Id]:new ShortcutStore(_preferences.Resolve("Dormant"),_catalog.Root);
    }
    private DrawerDefinition? AvailableDrawer() => _preferences.Value.Drawers.Where(d => _stores.ContainsKey(d.Id))
        .OrderByDescending(d => d.Enabled).FirstOrDefault();

    private void LoadDrawerStore(DrawerDefinition drawer)
    {
        try
        {
            _stores[drawer.Id] = _preferences.OpenStore(drawer.Id);
            _unavailableStores.Remove(drawer.Id);
            if (_stores[drawer.Id].Notice is { Length: > 0 } notice)
                PrototypeLog.Write($"library recovery {drawer.Id}: {notice}");
        }
        catch (Exception ex) when (MultiDrawerCatalog.IsReadFailure(ex))
        {
            _stores.Remove(drawer.Id);
            _unavailableStores.Add(drawer.Id);
            PrototypeLog.Write($"library unavailable: drawer={drawer.Id} {ex}");
        }
    }
    private void SelectDrawer(Guid id)
    {
        EndAcrylicPrime();
        FinishNameEdit(true);
        var definition=_preferences.Value.Drawers.Single(d=>d.Id==id);
        EndPointerSort();CloseFolder();_slotMotions.Clear();_tiles.Clear();
        _drawerId=id;_edge=definition.Edge;_store=_stores[id];_storeReady=false;_explicitSession=false;_manualUntil=0;_dragOpened=false;
        _interaction.Reset();_lastHeight=-1;_layoutDirty=true;_switchDwell.Clear();
        LoadLibrary(reload: false);
        PrototypeLog.Write("drawer: selected "+id+" "+definition.Name);
    }
    private bool RouteEdge(Point cursor,double now,double dt)
    {
        using var sample = IdlePerformance.Measure(IdlePerformance.Work.EdgeRoute);
        Guid? target=_preferences.Value.Drawers.Where(d=>d.Enabled&&_stores.ContainsKey(d.Id)).Select(d=>(Guid?)d.Id).FirstOrDefault(id=>_edgeGeometries.TryGetValue(id!.Value,out var g)&&g.Trigger.Contains(cursor.X,cursor.Y));
        bool inside=_visible&&_geometry.Visible(_edge,_interaction.Position).Contains(cursor.X,cursor.Y);
        bool externalHandoff = target != null && target != _drawerId && !_ignoreExternalUntilMouseUp;
        if (DrawerOperationActive || inside || ExplorerDragHeld && !externalHandoff)
        {
            _switchDwell.Clear();
            _switching = false;
            _switchTarget = null;
            return false;
        }
        if(target!=null && !_gate.Allows(cursor,_geometry.Monitor,_visible,out _,_preferences.Value.Global.AllowInFullScreen) &&
            !((GetAsyncKeyState(1)&0x8000)!=0&&_gate.ExplorerForeground())) target=null;
        if(_switching)
        {
            if(target!=_switchTarget){_switching=false;_switchTarget=null;_switchDwell.Clear();return false;}
            _interaction.CloseStep(now,dt);RenderPosition();
            if(_interaction.Position==0&&target!=null){_switching=false;_switchTarget=null;SelectDrawer(target.Value);_interaction.KeepOpen();RenderPosition();}
            return true;
        }
        if(target!=null&&target!=_drawerId)
        {
            if(_switchDwell.Update(target,now)){_switching=true;_switchTarget=target;_interaction.CloseStep(now,dt);RenderPosition();if(_interaction.Position==0){_switching=false;_switchTarget=null;SelectDrawer(target.Value);_interaction.KeepOpen();RenderPosition();}return true;}
        }
        else _switchDwell.Clear();
        if(!Enabled(_drawerId)&&!_explicitSession){_interaction.CloseStep(now,dt);RenderPosition();return true;}
        return false;
    }
    private void RequestOpenEdge(DrawerEdge edge)
    {
        var drawer=_preferences.Ordered(edge).FirstOrDefault(d=>d.Enabled)??_preferences.Ordered(edge).FirstOrDefault();
        if(drawer!=null)RequestOpenDrawer(drawer.Id);
    }
    private void RequestOpenDrawer(Guid id){if(InteractionLocked||!_stores.ContainsKey(id))return;HideImmediately();SelectDrawer(id);OpenExplicitly();}
    private void OpenSettingsFromTray()=>ShowTrayMenu();
}
