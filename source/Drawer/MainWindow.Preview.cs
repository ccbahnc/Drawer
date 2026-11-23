using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Drawer.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using static Drawer.NativeMethods;
namespace Drawer;
public sealed partial class MainWindow
{
    private readonly bool _previewOnly;
    private readonly MainWindow? _previewOwner;
    private DrawerDefinition? _previewDefinition;
    private bool _surfaceFree,_previewWanted,_retireSurface,_previewSettling;
    private readonly Dictionary<Guid,MainWindow> _previewSurfaces=new();
    private DrawerCreation? _creation;
    private Guid _beforeCreation;
    private IDisposable? _settleHold;
    private bool _closingPreview;
    private bool _resizePreview, _parkedResizePreview, _resizePreparationQueued;
    private Guid? _resizePreviewsPreparedFor;
    private (List<ShortcutEntry> Items, List<ShortcutGroup> Groups, List<Guid> Slots) _previewLibrary;
    private bool RetiringPreviewSurfaces => _previewSurfaces.Values.Any(surface => surface._retireSurface && !surface._closed);
#if DEBUG
    private bool _inspectLayout;
    private IDisposable? _inspectHold;
    private void InspectLayout(DrawerEdge edge)
    {
        var candidate=DrawerMove.Propose(_preferences.Value,_drawerId,edge,0);ValidateLayoutProposal(candidate);
        var plan=ComputeLayout(candidate);
        _inspectLayout=true;_inspectHold??=HoldOpen();GridHost.IsHitTestVisible=false;
        Present(candidate.Drawers.Single(d=>d.Id==_drawerId),plan.Items[_drawerId],true);ShowPreviewEdge(candidate,plan,edge);
        PrototypeLog.Write("review: read-only target layout "+edge+"; no drag input or commit");
    }
#endif

    private void Present(DrawerDefinition definition,ArrangedDrawer arranged,bool free=false)
    {
        ExposeReviewSurface();
        var previous=_metrics;_previewDefinition=definition;_edge=definition.Edge;_metrics=arranged.Metrics;_surfaceFree=free;
        ApplyNavigationVisibility();UpdateNavigationCaption();SetSizeTarget(arranged.Geometry);
        if(previous!=_metrics){RefreshTiles();ApplyGridMetrics();}
        _previewWanted=true;_retireSurface=false;_interaction.KeepOpen();
    }
    private void TickPreviewSurface()
    {
        using var perf = ResizeTrace?.Measure(ResizePerformance.Stage.NeighborTick);
        double now=_clock.Elapsed.TotalMilliseconds,dt=(now-_lastTick)/1000;_lastTick=now;
        if (!_previewWanted && _interaction.Position == 0)
        {
            RenderPosition();
            if (_retireSurface) RetirePreviewSurface();
            return;
        }
        StepSize(dt);UpdateDragPointerAndMotion(dt);
        bool movingToFinal = _retireSurface && (!MotionSettled(_sizeMotion) || !MotionSettled(_positionMotion));
        if (_previewWanted || movingToFinal)
        {
            _interaction.KeepOpen();
            _interaction.Update(now, dt, true, true, true);
        }
        else _interaction.CloseStep(now, dt);
        RenderPosition();
        if (_retireSurface && _interaction.Position == 0) RetirePreviewSurface();
    }

    private void RetirePreviewSurface()
    {
        if (_resizePreview && _previewOwner is { _visible: true, _closed: false })
            ParkResizePreview();
        else ClosePreviewWindow();
    }

    private void ParkResizePreview()
    {
        _previewWanted = _retireSurface = false;
        _parkedResizePreview = true;
        _interaction.Reset();
        EndAcrylicPrime();
        if (_visible)
        {
            ShowWindow(_hwnd, 0);
            _visible = false;
            _lastHeight = -1;
        }
        _timer.Stop();
    }

    private void DiscardPreparedResizePreviews()
    {
        _resizePreviewsPreparedFor = null;
        foreach (var surface in _previewSurfaces.Values.Where(surface => surface._resizePreview).ToArray())
            surface.ClosePreviewWindow();
    }

    private void QueueResizePreviewPreparation()
    {
        if (_resizePreparationQueued || _resizePreviewsPreparedFor == _drawerId || !CanPrepareResizePreviews) return;
        var drawer = _drawerId;
        int revision = _screenRevision;
        _resizePreparationQueued = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            using var sample = IdlePerformance.Measure(IdlePerformance.Work.PreviewPreparation);
            _resizePreparationQueued = false;
            if (!CanPrepareResizePreviews || _drawerId != drawer || _screenRevision != revision) return;
            try
            {
                var collection = _preferences.Value;
                var next = collection.Drawers.FirstOrDefault(d => d.Id != drawer && d.Enabled && d.Edge == _edge &&
                    _edgeGeometries.ContainsKey(d.Id) && _stores.ContainsKey(d.Id) && !_previewSurfaces.ContainsKey(d.Id));
                if (next == null)
                {
                    _resizePreviewsPreparedFor = drawer;
                    return;
                }
                var plan = ComputeLayout(collection);
                if (!plan.Items.TryGetValue(next.Id, out var placement))
                {
                    _resizePreviewsPreparedFor = drawer;
                    return;
                }
                long started = Stopwatch.GetTimestamp();
                var surface = CreatePreviewSurface(next);
                surface._resizePreview = true;
                surface.Present(next, placement, true);
                surface.ParkResizePreview();
                if (ResizePerformance.Available)
                    PrototypeLog.Write($"resize preview prepared: edge={next.Edge} ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
            }
            catch (Exception ex)
            {
                DiscardPreparedResizePreviews();
                _resizePreviewsPreparedFor = drawer;
                PrototypeLog.Write("resize preview preparation: " + ex);
            }
        });
    }

    private bool CanPrepareResizePreviews => !_closed && !_previewOnly && _visible &&
        _interaction.Position == 1 && _interaction.TargetOpen && !_displayRefreshPending && !_surfaceFree &&
        _resize == null && !InteractionLocked;

    private MainWindow CreatePreviewSurface(DrawerDefinition definition)
    {
        using var create = ResizeTrace?.Measure(ResizePerformance.Stage.NeighborCreate);
        var surface = new MainWindow(this, definition, _stores[definition.Id]);
        var id = definition.Id;
        _previewSurfaces[id] = surface;
        surface.Closed += (_, _) =>
        {
            if (_previewSurfaces.TryGetValue(id, out var current) && ReferenceEquals(current, surface))
            {
                _previewSurfaces.Remove(id);
                if (surface._resizePreview) _resizePreviewsPreparedFor = null;
            }
        };
        surface.PlaceBelowLayoutWindow();
        surface.Title = UiText.Get("DrawerPreviewTitle", definition.Name);
        return surface;
    }
    private void ClosePreviewWindow()
    {
        if (_closed || _closingPreview) return;
        _closingPreview = true;
        _timer.Stop();
        // Acrylic priming can show the HWND before _visible becomes true.
        // Hide the surface before detaching its backdrop.
        ShowWindow(_hwnd, 0);
        _visible = false;
        EndAcrylicPrime();
        StopAcrylic();
        Close();
    }
    private void AnimatePreviewSource(double now,double dt)
    {
        StepSize(dt);UpdateDragPointerAndMotion(dt);_interaction.KeepOpen();_interaction.Update(now,dt,true,true,true);RenderPosition();
    }
    private void ShowPreviewEdge(DrawerCollection visual, Arrangement plan, DrawerEdge edge, Arrangement? baseline = null,
        bool forResize = false) =>
        ShowPreviewEdges(visual, plan, edge, edge, baseline, forResize);

    private void ShowPreviewEdges(DrawerCollection visual, Arrangement plan, DrawerEdge first, DrawerEdge second,
        Arrangement? baseline = null, bool forResize = false)
    {
        var trace = ResizeTrace;
        DrawerDefinition[] wanted;
        using (trace?.Measure(ResizePerformance.Stage.NeighborPlan))
            wanted = visual.Drawers.Where(d => d.Enabled && (d.Edge == first || d.Edge == second) &&
                plan.Items.TryGetValue(d.Id, out var target) && _stores.ContainsKey(d.Id) &&
                (baseline == null ? d.Id != _drawerId : baseline.Items.TryGetValue(d.Id, out var prior) && prior.Geometry != target.Geometry)).ToArray();
        using var apply = trace?.Measure(ResizePerformance.Stage.NeighborApply);
        var initial = baseline;
        var ids=wanted.Select(d=>d.Id).ToHashSet();
        foreach(var pair in _previewSurfaces.ToArray())
        {
            if(pair.Value._closed){_previewSurfaces.Remove(pair.Key);continue;}
            if (pair.Value._parkedResizePreview &&
                (!forResize || !_stores.TryGetValue(pair.Key, out var store) || !ReferenceEquals(store, pair.Value._store)))
            {
                pair.Value.ClosePreviewWindow();
                continue;
            }
            if(!ids.Contains(pair.Key)){pair.Value._previewWanted=false;pair.Value._retireSurface=false;}
        }
        foreach(var d in wanted)
        {
            if(!_previewSurfaces.TryGetValue(d.Id,out var surface))
            {
                surface = CreatePreviewSurface(d);
                initial ??= _navBaseline ?? ComputeLayout(_preferences.Value);
                if (initial.Items.TryGetValue(d.Id, out var prior) && prior.Geometry != plan.Items[d.Id].Geometry)
                    surface.Present(d, prior, true);
                PrototypeLog.Write("preview: opened real surface "+d.Id);
            }
            else if (surface._parkedResizePreview)
            {
                trace?.Increment(ResizePerformance.Counter.NeighborPreparedReuse);
                surface._parkedResizePreview = false;
                initial ??= ComputeLayout(_preferences.Value);
                if (initial.Items.TryGetValue(d.Id, out var prior))
                {
                    surface.Present(d, prior, true);
                    if (surface._geometry != prior.Geometry)
                        surface.SetSizeTarget(prior.Geometry, immediate: true);
                }
                if (surface._previewLibrary != (surface._store.Items, surface._store.Groups, surface._store.TopSlots))
                    surface.RefreshTiles();
                surface._lastTick = surface._clock.Elapsed.TotalMilliseconds;
                surface._timer.Start();
            }
            surface._resizePreview = forResize;
            surface.Present(d,plan.Items[d.Id],true);
        }
    }
    private void ClosePreviewSurfaces(bool immediate=false, Arrangement? finalPlan = null, bool revealFinal = false)
    {
        var original=immediate ? null : finalPlan ?? ComputeLayout(_preferences.Value);
        foreach(var surface in _previewSurfaces.Values.ToArray())
        {
            if(surface._closed)continue;
            if(immediate){surface.ClosePreviewWindow();continue;}
            if (surface._parkedResizePreview) continue;
            if(original!.Items.TryGetValue(surface._drawerId,out var arranged))
            {surface.Present(_preferences.Value.Drawers.Single(d=>d.Id==surface._drawerId),arranged,true);}
            surface._previewWanted=false;surface._retireSurface=true;
            if (revealFinal) surface._interaction.Update(surface._clock.Elapsed.TotalMilliseconds, .016, true, true, true);
        }
        if(immediate)_previewSurfaces.Clear();
    }
    private void StartSettling(Arrangement? finalPlan = null)
    {
        if(!_previewSettling){_previewSettling=true;_settleHold=HoldOpen();}
        _surfaceFree=true;_previewDefinition=null;_layoutDirty=true;UpdateGeometry(finalPlan: finalPlan);_manualUntil=0;
        if (_edgeGeometries.TryGetValue(_drawerId, out var placement))
            PrototypeLog.Write($"navigation: published {_drawerId} edge={_edge} trigger={placement.Trigger}");
        if(!_edgeGeometries.ContainsKey(_drawerId)||!Enabled(_drawerId))
        {_previewSettling=false;_surfaceFree=false;ReleaseHold(ref _settleHold);HideImmediately();}
    }
    private void AbortLayoutPresentation()
    {
        _layoutWindow?.Close();EndNavigation(false);
        _previewSettling=false;ReleaseHold(ref _settleHold);
#if DEBUG
        if(_inspectLayout){_inspectLayout=false;ReleaseHold(ref _inspectHold);GridHost.IsHitTestVisible=true;}
#endif
        _surfaceFree=false;_previewDefinition=null;ClosePreviewSurfaces(true);_manualUntil=0;
    }
    private void FinishSettling()
    {
        if(!MotionSettled(_sizeMotion)||!MotionSettled(_positionMotion))return;
        _previewSettling=false;_surfaceFree=false;ReleaseHold(ref _settleHold);
        _manualUntil=0;_explicitSession=false;_layoutDirty=true;RestoreHoverStyle();
    }
    private void PaintInvalidPlacement(bool invalid)
    {
        if ((PlacementWarning.Visibility == Visibility.Visible) == invalid) return;
        PlacementWarning.Visibility = invalid ? Visibility.Visible : Visibility.Collapsed;
        PlacementWarningText.Text = invalid ? UiText.Get("PlacementNoSpace") : "";
        _requestedFrameBorder = DrawerFrameBorder;
        _frameBorderResult = DrawerPalette.SetFrameBorder(_hwnd, _requestedFrameBorder);
    }
    private void ExposeReviewSurface()
    {
#if DEBUG
        if(Array.Exists(Environment.GetCommandLineArgs(),a=>a=="--review16"))
        {SetSwitcherVisibility(true);long style=GetWindowLongPtrW(_hwnd,-20).ToInt64();if((style&0x08000080L)!=0)SetWindowLongPtrW(_hwnd,-20,(nint)(style&~0x08000080L));}
#endif
    }
}
