using System;
using Drawer.Core;

namespace Drawer;

public sealed partial class MainWindow
{
    private const int HiddenPollMs = 40;
    private const double EdgeApproachDip = 24;
    private bool _slowPolling, _wasApproachingEdge;

    private bool CanIdleHidden(double now) => !_visible && !_interaction.TargetOpen &&
        _interaction.Position == 0 && _acrylicPrimedAt == null && now >= _manualUntil &&
        !_layoutDirty && !_displayRefreshPending && !_switching && !_openHolds.Active &&
        !_pickerOpen && !_settingsOpen && !_menuOpen && _confirmationWindow == null &&
        !_editingName && !NavigationPressed && !NavigationTransitionActive &&
        _resize == null && _creation == null && _layoutPreview == null && !_previewSettling &&
        _previewSurfaces.Count == 0 && !_resizePreparationQueued && _pressedTile == null &&
        !_internalDrag && !_externalDragging && !_ignoreExternalUntilMouseUp && !_dragOpened &&
        _boxSelection == null && _openFolder == null && !_folderAnimating && !PageTurning &&
        _mergeAnimation == null && _historyFadeGrid == null && !_importing && !_readingClipboard &&
        !_committingDrag && !_committingCut && !_historyRestoring && !_formingFolder && !_deletingSelection &&
        _sizeMotion?.IsMoving != true && _positionMotion?.IsMoving != true && !SlotsMoving();

    private bool ApproachingEdge(NativeMethods.Point cursor)
    {
        int margin = (int)Math.Ceiling(EdgeApproachDip * _presentationScale);
        foreach (var geometry in _edgeGeometries.Values)
        {
            var trigger = geometry.Trigger;
            if (trigger.Width > 0 && trigger.Height > 0 &&
                cursor.X >= trigger.Left - margin && cursor.X < trigger.Right + margin &&
                cursor.Y >= trigger.Top - margin && cursor.Y < trigger.Bottom + margin) return true;
        }
        return false;
    }

    private void ClearIdleEdgeDwell()
    {
        _switchDwell.Clear();
        _interaction.Reset();
    }

    private void SetIdlePolling(bool slow)
    {
        if (_previewOnly || _closed || _slowPolling == slow || _timer is not { IsRunning: true }) return;
        _slowPolling = slow;
        _timer.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(slow ? HiddenPollMs : DrawerSettings.PollMs);
        _timer.Start();
    }
}
