using Drawer.Core;
using System;
using System.Runtime.InteropServices;
using static Drawer.NativeMethods;

namespace Drawer;

// A pixel-sized layered popup keeps the same icon visible between drawer HWND positions.
// The layered + transparent styles make it invisible to mouse hit testing.
internal sealed class FloatingDragIcon : IDisposable
{
    private readonly nint _window, _dc, _bitmap, _old;
    private readonly Point _size;
    private Point _position;
    private byte? _opacity;
    private bool _visible;

    public FloatingDragIcon(int width, int height, byte[] pixels)
    {
        _size = new Point { X = width, Y = height };
        try
        {
            _window = CreateWindowExW(0x080800A8, "STATIC", "", 0x80000000,
                0, 0, width, height, 0, 0, 0, 0);
            _dc = CreateCompatibleDC(0);
            if (_window == 0 || _dc == 0) throw new InvalidOperationException(UiText.Get("DragWindowFailed"));
            var header = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 };
            _bitmap = CreateDIBSection(_dc, ref header, 0, out nint data, 0, 0);
            if (_bitmap == 0 || data == 0) throw new InvalidOperationException(UiText.Get("DragBitmapFailed"));
            _old = SelectObject(_dc, _bitmap);
            Marshal.Copy(pixels, 0, data, checked(width * height * 4));
        }
        catch { Dispose(); throw; }
    }

    public bool Show(Point cursor, int lift, byte alpha = 234)
    {
        var destination = new Point { X = cursor.X - _size.X / 2, Y = cursor.Y - _size.Y / 2 - lift };
        if (_opacity != alpha)
        {
            var source = new Point();
            var size = _size;
            uint blend = 0x01000000u | (uint)alpha << 16;
            if (!UpdateLayeredWindow(_window, 0, ref destination, ref size, _dc, ref source, 0, ref blend, 2))
            { Hide(); return false; }
            _opacity = alpha;
        }
        else if (destination.X != _position.X || destination.Y != _position.Y)
        {
            const uint MoveOnly = 0x0215; // Keep size, z-order, owner order and activation.
            if (!SetWindowPos(_window, 0, destination.X, destination.Y, 0, 0, MoveOnly))
            { Hide(); return false; }
        }
        _position = destination;
        if (!_visible)
        {
            ShowWindow(_window, 4);
            _visible = true;
        }
        return true;
    }

    public void Hide()
    {
        if (!_visible) return;
        ShowWindow(_window, 0);
        _visible = false;
    }

    public void Dispose()
    {
        if (_window != 0) DestroyWindow(_window);
        if (_old != 0) SelectObject(_dc, _old);
        if (_bitmap != 0) DeleteObject(_bitmap);
        if (_dc != 0) DeleteDC(_dc);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, Bits;
        public uint Compression, ImageSize;
        public int PixelsX, PixelsY;
        public uint ColorsUsed, ColorsImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(nint hwnd, nint destinationDc,
        ref Point destination, ref Point size, nint sourceDc, ref Point source, uint colorKey, ref uint blend, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo header,
        uint usage, out nint bits, nint section, uint offset);
}
