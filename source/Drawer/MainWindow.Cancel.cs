using System;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using static Drawer.NativeMethods;

namespace Drawer;
public sealed partial class MainWindow
{
    private bool _escapeHandled, _escapeDown;

    private void MarkEscapeHandled() => _escapeHandled = true;

    private void KeepEscapeOnClosedSurface()
    {
        if ((GetAsyncKeyState(0x1B) & 0x8001) != 0) MarkEscapeHandled();
    }

    private void Drawer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!e.Handled && (HandleDeleteSelection(e) || HandleEditShortcut(e))) { e.Handled = true; return; }
        if (e.Handled || e.Key != VirtualKey.Escape) return;
        if (e.KeyStatus.WasKeyDown) { e.Handled = _escapeHandled; return; }
        e.Handled = CancelTopOperation();
    }

    private void PollEscape()
    {
        using var sample = IdlePerformance.Measure(IdlePerformance.Work.EscapePoll);
        int state = GetAsyncKeyState(0x1B);
        bool down = (state & 0x8000) != 0;
        bool pressed = (state & 1) != 0 || (down && !_escapeDown);
        _escapeDown = down;
        if (pressed && !_escapeHandled)
        {
            if (_composingName || _menuOpen || _settingsOpen || _pickerOpen || _confirmationWindow != null) MarkEscapeHandled();
            else if (!_editingName || GetForegroundWindow() != _hwnd) CancelTopOperation();
        }
        if (!down) _escapeHandled = false;
    }

    private bool CancelTopOperation()
    {
        if (_escapeHandled) return true;
        if (_editingName)
        {
            if (_composingName) return false;
            MarkEscapeHandled(); FinishNameEdit(false); return true;
        }
        if (_settingsOpen || _pickerOpen || _menuOpen || _confirmationWindow != null) return false;
        if (_resize != null) { MarkEscapeHandled(); EndDrawerResize(commit: false, reason: "escape"); return true; }
        if (_formingFolder) { MarkEscapeHandled(); return true; }
        if (_committingDrag || _historyRestoring || _committingCut) { MarkEscapeHandled(); return true; }
        if (_boxSelection != null) { MarkEscapeHandled(); EndSelectionBox(cancel: true); return true; }
        if (NavigationPressed) { MarkEscapeHandled(); EndNavigation(false); return true; }
        if (_pressedTile != null) { MarkEscapeHandled(); CancelItemDrag(); return true; }
        if (_externalDragging) { MarkEscapeHandled(); EndExternalDrag(canceled: true); return true; }
        if (_importCancellation != null) { MarkEscapeHandled(); _importCancellation.Cancel(); return true; }
        if (_readingClipboard) { MarkEscapeHandled(); CancelClipboardPaste(); return true; }
        if (_cutItems != null && GetForegroundWindow() == _hwnd) { MarkEscapeHandled(); _cutItems = null; return true; }
        if (_selection.Active) { MarkEscapeHandled(); ClearItemSelection(); return true; }
        if (_openFolder == null) return false;
        MarkEscapeHandled(); ReturnFolder(); return true;
    }
}
