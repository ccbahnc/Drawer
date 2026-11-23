using System;
using System.Runtime.InteropServices;
using Drawer.Core;

namespace Drawer;

internal static class ShellShortcutIcon
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Size { internal int Width, Height; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Bitmap
    {
        internal int Type, Width, Height, Stride;
        internal ushort Planes, BitsPerPixel;
        internal nint Bits;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, BitsPerPixel;
        internal uint Compression, SizeImage;
        internal int XPixelsPerMeter, YPixelsPerMeter;
        internal uint ColorsUsed, ImportantColors;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Size size, uint flags, out nint bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint context, in Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);
    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern int GetObjectW(nint obj, int size, out Bitmap bitmap);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint first, uint count, [Out] byte[] data, ref BitmapInfo info, uint colors);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);

    internal static IconPixels? Read(string path, int pixels)
    {
        Guid iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, 0, in iid, out var factory) < 0) return null;
        nint bitmap = 0;
        try
        {
            const uint iconOnly = 0x4, biggerSizeOk = 0x1;
            if (factory.GetImage(new Size { Width = pixels, Height = pixels }, iconOnly | biggerSizeOk, out bitmap) < 0 || bitmap == 0 ||
                GetObjectW(bitmap, Marshal.SizeOf<Bitmap>(), out var bmp) == 0 || bmp.Width is < 1 or > 1024 || bmp.Height is < 1 or > 1024)
                return null;
            var info = new BitmapInfo { Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = bmp.Width, Height = -bmp.Height, Planes = 1, BitsPerPixel = 32 };
            var data = new byte[bmp.Width * bmp.Height * 4];
            nint dc = GetDC(0);
            if (dc == 0) return null;
            try { if (GetDIBits(dc, bitmap, 0, (uint)bmp.Height, data, ref info, 0) != bmp.Height) return null; }
            finally { ReleaseDC(0, dc); }

            bool hasAlpha = false, straightAlpha = false;
            for (int i = 0; i < data.Length; i += 4)
            {
                byte alpha = data[i + 3];
                if (alpha == 0) { data[i] = data[i + 1] = data[i + 2] = 0; continue; }
                hasAlpha = true;
                straightAlpha |= data[i] > alpha || data[i + 1] > alpha || data[i + 2] > alpha;
            }
            if (!hasAlpha) return null;
            if (straightAlpha)
                for (int i = 0; i < data.Length; i += 4)
                    for (int channel = 0; channel < 3; channel++)
                        data[i + channel] = (byte)((data[i + channel] * data[i + 3] + 127) / 255);
            return new(bmp.Width, bmp.Height, data);
        }
        finally
        {
            if (bitmap != 0) DeleteObject(bitmap);
            Marshal.ReleaseComObject(factory);
        }
    }
}
