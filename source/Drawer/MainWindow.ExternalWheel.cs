using System;
using System.Runtime.InteropServices;
using Drawer.Core;
using Microsoft.UI.Xaml;
using Rect = Windows.Foundation.Rect;
using static Drawer.NativeMethods;

namespace Drawer;

public sealed partial class MainWindow
{
    private nint _externalWheelHook;
    private WindowsHookProc? _externalWheelCallback;
    private PixelRect _externalWheelArea;
    private int _externalWheelSession;

    private void StartExternalWheel()
    {
        if (_externalWheelHook != 0) return;
        _pageWheel.Reset();
        UpdateExternalWheelArea();
        _externalWheelCallback ??= ObserveExternalWheel;
        _externalWheelHook = SetWindowsHookExW(14, _externalWheelCallback, GetModuleHandleW(null), 0); // WH_MOUSE_LL
        if (_externalWheelHook == 0) PrototypeLog.Write($"external wheel: hook unavailable ({Marshal.GetLastWin32Error()})");
    }

    private void StopExternalWheel()
    {
        _externalWheelSession++;
        _externalWheelArea = default;
        if (_externalWheelHook != 0) UnhookWindowsHookEx(_externalWheelHook);
        _externalWheelHook = 0;
    }

    private void UpdateExternalWheelArea()
    {
        _externalWheelArea = default;
        if (!_visible || _folderAnimating || _folderReturning) return;
        Rect area;
        if (_openFolder != null)
        {
            if (_folderLayout is not { } folder || _memberGrid == null) return;
            area = MemberPanelBounds(folder);
        }
        else area = new Rect(0, 0, GridHost.ActualWidth, TopContentHeight);
        var origin = GridHost.TransformToVisual((UIElement)Content).TransformPoint(new Windows.Foundation.Point());
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? _presentationScale;
        var start = new NativeMethods.Point { X = (int)Math.Ceiling((origin.X + area.X) * scale), Y = (int)Math.Ceiling((origin.Y + area.Y) * scale) };
        var end = new NativeMethods.Point { X = (int)Math.Ceiling((origin.X + area.Right) * scale), Y = (int)Math.Ceiling((origin.Y + area.Bottom) * scale) };
        if (!ClientToScreen(_hwnd, ref start) || !ClientToScreen(_hwnd, ref end)) return;
        var visible = _geometry.Visible(_edge, _interaction.Position);
        _externalWheelArea = new(Math.Max(start.X, visible.Left), Math.Max(start.Y, visible.Top),
            Math.Min(end.X, visible.Right), _openFolder == null ? visible.Bottom : Math.Min(end.Y, visible.Bottom));
    }

    private nint ObserveExternalWheel(int code, nuint message, nint data)
    {
        try { if (TryHandleExternalWheel(code, message, data)) return 1; }
        catch (Exception ex) { PrototypeLog.Write("external wheel: " + ex); }
        return CallNextHookEx(0, code, message, data);
    }

    private bool TryHandleExternalWheel(int code, nuint message, nint data)
    {
        // Explorer's OLE loop owns the drag and may never route its wheel to XAML.
        // Only consume a vertical wheel over this drawer during the current drop.
        if (code == 0 && message == 0x020A && _externalDragging && !_closed)
        {
            var mouse = Marshal.PtrToStructure<LowLevelMouseData>(data);
            int delta = unchecked((short)(mouse.MouseData >> 16));
            if (delta != 0 && _externalWheelArea.Contains(mouse.Position.X, mouse.Position.Y) &&
                GetAncestor(WindowFromPoint(mouse.Position), 2) == _hwnd)
            {
                int session = _externalWheelSession;
                if (DispatcherQueue.TryEnqueue(() =>
                {
                    if (_closed || !_externalDragging || session != _externalWheelSession ||
                        !DragPoint(mouse.Position, GridHost, out var point)) return;
                    if (_openFolder != null) ScrollMemberWheel(point, delta);
                    else TurnTopWheel(delta, "external wheel");
                })) return true;
            }
        }
        return false;
    }
}
