using System;
using System.Runtime.InteropServices;
using static Drawer.NativeMethods;

namespace Drawer;

public sealed partial class MainWindow
{
    private bool _transparentClientEnabled;
    private const uint DwmEnable = 0x1, DwmBlurRegion = 0x2;
    private const int BlackBrush = 4;

    private void EnableTransparentClient()
    {
        var margins = new FrameMargins(0, 0, 0, 0);
        Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(_hwnd, in margins));

        // Enable client alpha without selecting any visible area for blur.
        // DWM uses the clear pixels in the HWND backing surface to show the desktop.
        nint outsideClient = CreateRectRgn(-2, -2, -1, -1);
        if (outsideClient == 0) throw new InvalidOperationException("Could not create the client alpha region.");
        try
        {
            var alpha = new DwmBlurBehind { Flags = DwmEnable | DwmBlurRegion, Enabled = 1, Region = outsideClient };
            Marshal.ThrowExceptionForHR(DwmEnableBlurBehindWindow(_hwnd, in alpha));
            _transparentClientEnabled = true;
        }
        finally { DeleteObject(outsideClient); }

        nint dc = GetDC(_hwnd);
        if (dc == 0) return; // The client will also be cleared when Windows erases it.
        try { ClearTransparentClient(_hwnd, dc); }
        finally { ReleaseDC(_hwnd, dc); }
    }

    private bool ClearTransparentClient(nint hwnd, nint dc)
    {
        // A black GDI fill clears the redirection bitmap's alpha. XAML draws
        // the tint and all interactive content over this backing surface.
        return _transparentClientEnabled && dc != 0 && GetClientRect(hwnd, out var bounds) &&
            FillRect(dc, in bounds, GetStockObject(BlackBrush)) != 0;
    }

    private void DisableTransparentClient()
    {
        if (!_transparentClientEnabled) return;
        _transparentClientEnabled = false;
        if (_closed) return;
        var alpha = new DwmBlurBehind { Flags = DwmEnable };
        int result = DwmEnableBlurBehindWindow(_hwnd, in alpha);
        if (result < 0) PrototypeLog.Write($"client alpha detach hwnd={_hwnd}: 0x{result:X8}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FrameMargins(int left, int right, int top, int bottom)
    {
        private readonly int Left = left, Right = right, Top = top, Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmBlurBehind
    {
        internal uint Flags;
        internal int Enabled;
        internal nint Region;
        internal int TransitionOnMaximized;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint hwnd, in FrameMargins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(nint hwnd, in DwmBlurBehind blurBehind);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int objectIndex);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, in Rect bounds, nint brush);
}
