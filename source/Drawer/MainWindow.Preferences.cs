using System;
using System.Linq;
using Drawer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Drawer.NativeMethods;
namespace Drawer;
public sealed partial class MainWindow
{
    private MultiDrawerCatalog _preferences=null!;
    private LayoutWindow? _layoutWindow;
    private IDisposable? _layoutHold;
    private bool _trayMenuInProgress;

    private static string DrawerCaption(DrawerDefinition drawer) =>
        UiText.Get("DrawerEdgeCaption", DrawerCatalog.Label(drawer.Edge), drawer.Name);

    private bool IsTrayIconPoint(Point point)
    {
        var icon = new NotifyIconIdentifier
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NotifyIconIdentifier>(),
            Window = _tray.Window, Id = _tray.Id
        };
        return Shell_NotifyIconGetRect(ref icon, out var bounds) == 0 && bounds.Pixels.Contains(point.X, point.Y);
    }

    private void ApplyUiLanguage()
    {
        UiText.Use(_preferences.Value.Global.Language);
        ApplyWindowLanguage();
        foreach (var surface in _previewSurfaces.Values) surface.ApplyWindowLanguage();
        _layoutWindow?.ApplyLanguage();
        _confirmationWindow?.ApplyLanguage();
        _tray.Tip = UiText.Get("TrayTooltip");
        if (!_previewOnly)
        {
            uint flags = _tray.Flags;
            _tray.Flags = 4; // NIF_TIP
            Shell_NotifyIcon(1, ref _tray);
            _tray.Flags = flags;
        }
    }

    private void ApplyWindowLanguage()
    {
        var add = UiText.Get("AddTooltip");
        var gear = UiText.Get("SettingsTooltip");
        ToolTipService.SetToolTip(AddButton, add);
        ToolTipService.SetToolTip(GearButton, gear);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AddButton, add);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(GearButton, gear);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NameEditor, UiText.Get("DrawerNameEditor"));
        if (_folderEditor != null)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_folderEditor, UiText.Get("FolderNameEditor"));
        if (_previewOnly && _previewDefinition is { } draft) Title = UiText.Get("DrawerPreviewTitle", draft.Name);
        foreach (var tile in _tiles) tile.RefreshLanguage();
        foreach (var member in _members) member.RefreshLanguage();
        UpdatePageIndicator(languageOnly: true);
    }

    private void ApplyAppearance()
    {
        DrawerPalette.Apply(_preferences.Value.Global.ColorScheme);
        ApplyWindowAppearance();
        foreach (var surface in _previewSurfaces.Values) surface.ApplyWindowAppearance();
        _layoutWindow?.ApplyStyle();
        _confirmationWindow?.ApplyStyle();
    }

    private void ApplyWindowAppearance()
    {
        if (_closed) return;
        bool colorChanged = WindowSurface.RequestedTheme != DrawerPalette.Theme;
        if (colorChanged)
        {
            WindowSurface.RequestedTheme = DrawerPalette.Theme;
            ApplyDrawerFrame();
        }
        bool materialChanged = ApplyDrawerMaterial();
        if (_openFolder != null)
        {
            if (colorChanged) RefreshFolderBackdropStyle();
            else if (materialChanged) _ = CaptureFolderBackdrop();
        }
    }

    private int DrawerFrameBorder => PlacementWarning.Visibility == Visibility.Visible
        ? DrawerPalette.NativeColor("Danger") : DrawerPalette.NoDwmBorder;

    private void ApplyDrawerFrame()
    {
        _requestedFrameBorder = DrawerFrameBorder;
        _frameBorderResult = DrawerPalette.ApplyFrame(this, borderColor: _requestedFrameBorder);
        PrototypeLog.Write($"presentation: drawer border request=0x{_requestedFrameBorder:X8} result=0x{_frameBorderResult:X8}");
    }
    private bool Enabled(Guid id)=>!_unavailableStores.Contains(id)&&_preferences.Value.Drawers.Any(d=>d.Id==id&&d.Enabled);
    private DrawerLayout CurrentLayout(Guid id)
    {
        var p=VisualCollection().Drawers.FirstOrDefault(d=>d.Id==id);var g=_preferences.Value.Global;
        return new(p?.Columns??6,p?.Rows??5,38,g.ShowNames,5);
    }
    private void ApplyPreferences(bool immediateSize = false, bool restorePreferred = false, bool animatePreferredSize = false)
    {
        ApplyAppearance();
        _switching=false;_switchTarget=null;_switchDwell.Clear();
        foreach(var id in _stores.Keys.Where(id=>!_preferences.Value.Drawers.Any(d=>d.Id==id)).ToArray())_stores.Remove(id);
        _unavailableStores.RemoveWhere(id => !_preferences.Value.Drawers.Any(d => d.Id == id));
        foreach (var d in _preferences.Value.Drawers)
            if (!_stores.TryGetValue(d.Id, out var store) || !string.Equals(store.Root, System.IO.Path.GetFullPath(System.IO.Path.Combine(_preferences.Root, d.Storage)), StringComparison.OrdinalIgnoreCase))
                LoadDrawerStore(d);
        if(!_stores.ContainsKey(_drawerId))
        {
            HideImmediately();var next=AvailableDrawer();
            if(next!=null)SelectDrawer(next.Id);else{_drawerId=Guid.Empty;_edge=DrawerEdge.Top;_store=new ShortcutStore(_preferences.Resolve("Dormant"),_catalog.Root);LoadLibrary();}
        }
        else {_store=_stores[_drawerId];_edge=_preferences.Value.Drawers.Single(d=>d.Id==_drawerId).Edge;}
        _layoutDirty=true;UpdateGeometry(immediateSize: immediateSize, restorePreferred: restorePreferred,
            animatePreferredSize: animatePreferredSize);RefreshTiles();
        if (immediateSize && _visible) RenderPosition(preserveSurface: true);
    }
    private async void ShowTrayMenu()
    {
        if (_closed || _exitRequested || _historyRestoring) return;
        if (_trayMenuInProgress)
        {
            if (_menuOpen) DismissOwnedMenu();
            return;
        }
        if (_menuOpen) return;
        if (NavigationPressed || NavigationTransitionActive || _pressedTile != null || _internalDrag || _externalDragging || _pickerOpen || _importing) return;
        bool styleOnly = _layoutWindow != null || _confirmationWindow != null || _folderAnimating || _editingName;
        if (!styleOnly && _openHolds.Active) return;
        if (!GetCursorPos(out var point)) return;

        var disabled = _preferences.Value.Drawers.Where(d => !d.Enabled)
            .OrderBy(d => d.Edge).ThenBy(d => d.Order).ToArray();
        var missing = _preferences.Value.Drawers.Where(d => d.Enabled && !_edgeGeometries.ContainsKey(d.Id)).ToArray();
        var crowdedEdges = missing.Where(d => !_unavailableStores.Contains(d.Id)).Select(d => d.Edge).ToHashSet();
        var unavailable = _preferences.Value.Drawers.Where(d => d.Enabled &&
                (_unavailableStores.Contains(d.Id) || crowdedEdges.Contains(d.Edge)))
            .OrderBy(d => d.Edge).ThenBy(d => _edgeGeometries.ContainsKey(d.Id)).ThenBy(d => d.Order).ToArray();
        var menu = new DrawerMenu();
        if (styleOnly)
        {
            if (_creation != null) AppendAddDrawerMenu(menu);
            AppendLanguageMenu(menu);
            AppendAppearanceMenus(menu);
        }
        else AppendTrayCommands(menu, disabled, unavailable);
        var editingLayout = _layoutWindow;
        _trayMenuInProgress = true;
        if (editingLayout != null) editingLayout.MenuOpen = true;
        void FinishTrayMenu(bool restoreFocus)
        {
            try
            {
                if (_closed || !restoreFocus) return;
                if (_confirmationWindow != null) _confirmationWindow.Activate();
                else if (_layoutWindow is { } layout) layout.RestoreAfterMenu();
                else _activeNameEditor?.Focus(FocusState.Programmatic);
            }
            finally
            {
                if (editingLayout != null) editingLayout.MenuOpen = false;
                _trayMenuInProgress = false;
            }
        }
        PrototypeLog.Write("tray: menu opening");
        int command;
        try { command = await ShowOwnedMenu(menu, point, fromTray: true); }
        catch (Exception ex)
        {
            FinishTrayMenu(restoreFocus: true);
            ErrorStatus(UiText.Get("SettingsFailed", ex.Message));
            return;
        }
        PrototypeLog.Write("tray: menu command=" + command);
        if (command == 0)
        {
            FinishTrayMenu(restoreFocus: false);
            return;
        }
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_closed) return;
                if (command == 1) { ExitDrawer(); return; }
                if (command is UndoCommand or RedoCommand)
                {
                    if (!_editingName && !_settingsOpen && !_readingClipboard && !_openHolds.Active) HandleHistory(command == RedoCommand);
                    return;
                }
                if (command is ToggleStartupCommand or RemoveStartupCommand)
                {
                    var changed = StartupRegistration.Update(remove: command == RemoveStartupCommand);
                    Status(UiText.Get(changed switch
                    {
                        StartupState.Current => "StartupEnabled", StartupState.Disabled => "StartupDisabled",
                        _ => "StartupUnrecognized"
                    }), notify: true);
                    return;
                }
                if (_creation != null && command is >= FirstCreateCommand and <= FirstCreateCommand + (int)DrawerEdge.Right)
                {
                    _layoutWindow?.Close();
                    AbortLayoutPresentation();
                    HideImmediately();
                }
                if (RunSharedDrawerCommand(command)) return;
                int repair = command - 2000;
                if (repair >= 0 && repair < unavailable.Length)
                {
                    var drawer = unavailable[repair];
                    if (_unavailableStores.Contains(drawer.Id))
                    {
                        LoadDrawerStore(drawer);
                        if (_unavailableStores.Contains(drawer.Id)) ErrorStatus(UiText.Get("DrawerReadDeferred", drawer.Name));
                        else ApplyPreferences();
                    }
                    else OpenLayout(drawer.Id);
                    return;
                }
                int index = command - 1000;
                if (index >= 0 && index < disabled.Length)
                {
                    using (BeginDataHistory()) _preferences.Update(disabled[index].Id, drawer => drawer with { Enabled = true });
                    ApplyPreferences();
                }
            }
            catch (Exception ex) { ErrorStatus(UiText.Get("SaveSettingsFailed", ex.Message)); }
            finally { FinishTrayMenu(restoreFocus: true); }
        })) FinishTrayMenu(restoreFocus: false);
    }

    private void AppendTrayCommands(DrawerMenu menu, DrawerDefinition[] disabled, DrawerDefinition[] unavailable)
    {
        AppendSharedDrawerMenu(menu);
        menu.Add(ToggleNavigationCommand, UiText.Get("ShowNavigation"), isChecked: ShowNavigation);
        if (_preferences.CanUndo || _preferences.CanRedo)
        {
            menu.Separate();
            menu.Add(UndoCommand, UiText.Get("Undo"), enabled: _preferences.CanUndo);
            menu.Add(RedoCommand, UiText.Get("Redo"), enabled: _preferences.CanRedo);
        }
        if (disabled.Length > 0)
        {
            var restore = new DrawerMenu();
            for (int index = 0; index < disabled.Length; index++)
                restore.Add(1000 + index, DrawerCaption(disabled[index]));
            menu.AddSubmenu(UiText.Get("EnableLegacyDrawer"), restore);
        }
        if (unavailable.Length > 0)
        {
            var repair = new DrawerMenu();
            for (int index = 0; index < unavailable.Length; index++)
            {
                var drawer = unavailable[index];
                string caption = DrawerCaption(drawer);
                repair.Add(2000 + index,
                    _unavailableStores.Contains(drawer.Id) ? UiText.Get("RetryDrawerName", caption) : caption);
            }
            menu.AddSubmenu(UiText.Get("RepairDrawers"), repair);
        }
        menu.Separate();
        var startup = StartupRegistration.GetState();
        menu.Add(ToggleStartupCommand, UiText.Get(startup switch
        {
            StartupState.Outdated => "StartupUpdate", StartupState.Unrecognized => "StartupUnrecognized",
            StartupState.Unavailable => "StartupSettingsFailed", _ => "StartWithWindows"
        }), enabled: startup is StartupState.Current or StartupState.Disabled or StartupState.Outdated,
            isChecked: startup is StartupState.Current or StartupState.Disabled ? startup == StartupState.Current : null);
        if (startup == StartupState.Outdated) menu.Add(RemoveStartupCommand, UiText.Get("StartupRemoveOld"));
        menu.Separate();
        menu.Add(1, UiText.Get("ExitDrawer"));
    }
    private void OpenLayout(DrawerEdge edge)
    {var drawer=_preferences.Ordered(edge).FirstOrDefault(d=>d.Enabled);if(drawer!=null)OpenLayout(drawer.Id);}

    private void RestoreDefaultDrawerSize(Guid id)
    {
        if (!FinishNameEdit(true)) return;
        var saved = _preferences.Value;
        var drawer = saved.Drawers.Single(d => d.Id == id);
        if (drawer.ContentScale == null) return;
        CheckLayoutRevision(_screenRevision);
        var reset = drawer with { ContentScale = null };
        var proposed = saved with { Drawers = saved.Drawers.Select(d => d.Id == id ? reset : d).ToList() };
        var plan = ComputeLayout(proposed);
        var baseline = ComputeLayout(saved);
        if (saved.Drawers.Any(d => d.Enabled && d.Edge == drawer.Edge &&
            baseline.Items.ContainsKey(d.Id) && !plan.Items.ContainsKey(d.Id)))
            throw new LayoutSpaceException();
        CloseFolder();
        using (BeginDataHistory()) _preferences.Commit(proposed);
        UpdateGeometry(finalPlan: plan);
        RenderPosition(preserveSurface: true);
    }

    private void OpenLayout(Guid id)
    {
        var definition = _preferences.Value.Drawers.FirstOrDefault(d => d.Id == id);
        if (_closed || definition == null || _pressedTile != null || _openHolds.Active || PageTurning) return;
        UpdateGeometry();
        CheckLayoutRevision(_screenRevision);
        if (id != _drawerId) { HideImmediately(); SelectDrawer(id); }
        CloseFolder();
        if (!_visible && _edgeGeometries.ContainsKey(id)) OpenExplicitly();
        _settingsOpen = true;
        DropSurface.IsHitTestVisible = false;
        var hold = _layoutHold = HoldOpen();
        var initial = CurrentLayout(id);
        int revision = _screenRevision;
        var confirmed = new LayoutSnapshot(_preferences.Value, ComputeLayout(_preferences.Value));

        void Finish()
        {
            if (!ReferenceEquals(_layoutHold, hold)) return;
            KeepEscapeOnClosedSurface();
            _layoutPreview = null;
            _layoutWindow = null;
            _settingsOpen = false;
            if (!_closed) DropSurface.IsHitTestVisible = true;
            ReleaseHold(ref _layoutHold);
            ClosePreviewSurfaces(_closed || _displayRefreshPending);
            _manualUntil = 0;
            _explicitSession = false;
            _layoutDirty = true;
            if (!_closed)
            {
                UpdateGeometry(immediateSize: true);
                RenderPosition(preserveSurface: true);
                RestoreHoverStyle();
            }
        }

        try
        {
            _layoutWindow = new LayoutWindow(() => DrawerCaption(definition), initial.Rows, initial.Columns, (rows,columns,edit) =>
            {
                if (_closed || _drawerId != id || !ReferenceEquals(_layoutHold, hold))
                    throw new InvalidOperationException(UiText.Get("LayoutDisplayChanged"));
                if (edit == LayoutEdit.Restore && _displayRefreshPending) return;
                CheckLayoutRevision(revision);
                if (edit == LayoutEdit.Restore)
                {
                    _layoutPreview = confirmed;
                    UpdateLayoutEditorPreview(id);
                    ShowPreviewEdge(confirmed.Collection, confirmed.Plan, definition.Edge);
                    return;
                }
                var saved = _preferences.Value;
                var current = saved.Drawers.Single(d => d.Id == id);
                bool dimensionsChanged = rows != current.Rows || columns != current.Columns;
                var edited = dimensionsChanged ? current with { Rows = rows, Columns = columns, ContentScale = null } : current;
                var snapshot = RequireDrawerPlacement(saved, edited);
                if (edit == LayoutEdit.Commit)
                {
                    if (dimensionsChanged)
                    {
                        using var history = BeginDataHistory();
                        _preferences.Commit(snapshot.Collection);
                    }
                    confirmed = snapshot;
                }
                _layoutPreview = snapshot;
                UpdateLayoutEditorPreview(id);
                ShowPreviewEdge(snapshot.Collection, snapshot.Plan, edited.Edge);
            }, _presentationScale, IsTrayIconPoint);
            _layoutWindow.Closed += (_,_) => Finish();
            OwnLayoutWindow();
            PositionLayoutWindow();
            if (!confirmed.Plan.Items.ContainsKey(id)) _layoutWindow.ShowInitialPreview();
        }
        catch
        {
            _layoutWindow?.Close();
            Finish();
            throw;
        }
    }

    private LayoutSnapshot RequireDrawerPlacement(DrawerCollection baseline, DrawerDefinition drawer)
    {
        var proposal = baseline with { Drawers = baseline.Drawers.Select(d => d.Id == drawer.Id ? drawer : d).ToList() };
        return BuildLayoutPreview(proposal, drawer.Id);
    }

    private void UpdateLayoutEditorPreview(Guid id)
    {
        UpdateGeometry();
        if (!_visible && _edgeGeometries.ContainsKey(id))
        {
            _manualUntil = _clock.Elapsed.TotalMilliseconds + 2000;
            _interaction.KeepOpen();
        }
        RenderPosition(preserveSurface: true);
    }
    private void DeleteDrawer(Guid id)
    {
        if (!FinishNameEdit(true)) return;
        try
        {
            CheckLayoutRevision(_screenRevision);
            var before = _preferences.Value.Drawers.Single(d => d.Id == id);
            var baseline = ComputeLayout(_preferences.Value);
            CloseFolder();
            using (BeginDataHistory()) _preferences.Delete(id);
            if (_drawerId == id) HideImmediately();
            ApplyPreferences();
            if (!_displayRefreshPending)
            {
                var final = ComputeLayout(_preferences.Value);
                ShowPreviewEdge(_preferences.Value, final, before.Edge, baseline);
                ClosePreviewSurfaces(finalPlan: final, revealFinal: true);
            }
            NotifyCleanupIfNeeded();
        }
        catch (Exception ex) { ErrorStatus(UiText.Get("DeleteFailed", ex.Message)); }
    }
}
