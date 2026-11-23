using System.Linq;

namespace Drawer;

public sealed partial class MainWindow
{
    internal bool IdleWorkInProgress =>
        _interaction.Position != (_interaction.TargetOpen ? 1d : 0d) || _acrylicPrimedAt != null ||
        _folderAnimating || _folderReturning || NavigationTransitionActive || _iconScales.Count != 0 ||
        _mergeAnimation != null || _historyFadeGrid != null || _previewSettling ||
        !MotionSettled(_sizeMotion) || !MotionSettled(_positionMotion) || _openHolds.Active ||
        _menuOpen || _settingsOpen || _pickerOpen || _confirmationWindow != null || _editingName ||
        _layoutDirty || _displayRefreshPending || _resize != null || NavigationPressed || _navActive ||
        _pressedTile != null || _internalDrag || _externalDragging || _boxSelection != null ||
        _creation != null || _importing || _readingClipboard || _historyRestoring || _committingDrag ||
        _committingCut || _deletingSelection || _formingFolder || PageTurning || _resizePreparationQueued;

    internal IdlePerformance.Snapshot IdleSnapshot(long workingSet, long privateBytes)
    {
        string foreground = _gate.ForegroundForIdleSample(_layoutScreen);
        int previews = 0, visiblePreviews = 0, previewTimers = 0;
        foreach (var preview in _previewSurfaces.Values)
        {
            if (preview._closed) continue;
            previews++;
            if (preview._visible || preview._acrylicPrimedAt != null) visiblePreviews++;
            if (preview._timer.IsRunning) previewTimers++;
        }
        bool reveal = _interaction.Position != (_interaction.TargetOpen ? 1d : 0d) || _acrylicPrimedAt != null;
        bool animation = _folderAnimating || _folderReturning || NavigationTransitionActive || _iconScales.Count != 0 ||
            _mergeAnimation != null || _historyFadeGrid != null || _previewSettling ||
            !MotionSettled(_sizeMotion) || !MotionSettled(_positionMotion);
        bool active = IdleWorkInProgress || previewTimers > 0 || visiblePreviews > 0;
        var config = _preferences.Value;
        return new(_visible ? "drawer-visible" : foreground, foreground, _visible, active,
            previews, visiblePreviews, previewTimers, _layoutWindow?.OutsideTimerRunning == true,
            _feedbackTimer?.IsRunning == true, reveal, animation, _resize != null, PageTurning,
            (int)_layoutDpi, _layoutScreen, _layoutWork, _screenRevision, _preferences.Revision,
            _metrics.Rows, _metrics.Columns, _arrangedDrawer?.ContentScale ?? 0,
            config.Global.Material.ToString(), _displayedMaterial.ToString(), config.Global.ColorScheme.ToString(),
            config.Drawers.Count, config.Drawers.Count(drawer => drawer.Enabled),
            _stores.Values.Sum(store => store.Items.Count), _visible ? _tiles.Count + _members.Length : 0,
            ShowNavigation, (int)_timer.Interval.TotalMilliseconds, workingSet, privateBytes);
    }
}
