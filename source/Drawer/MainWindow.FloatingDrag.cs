using System;
using System.Threading.Tasks;
using Drawer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Drawer;
public sealed partial class MainWindow
{
    private FloatingDragIcon? _floatingGhost;

    private async Task PrepareFloatingGhost(FrameworkElement host, FrameworkElement icon)
    {
        var popup = await CaptureFloatingIcon(icon, () => _internalDrag && _dragGhost == host);
        if (_internalDrag && _dragGhost == host) _floatingGhost = popup;
        else popup?.Dispose();
    }

    private async Task<FloatingDragIcon?> CaptureFloatingIcon(FrameworkElement visual, Func<bool> current)
    {
        try
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(visual);
            if (!current() || bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0) return null;
            var buffer = await bitmap.GetPixelsAsync();
            if (!current()) return null;
            using var reader = DataReader.FromBuffer(buffer);
            var pixels = new byte[buffer.Length];
            reader.ReadBytes(pixels);
            return new FloatingDragIcon(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
        }
        catch (Exception ex) { PrototypeLog.Write("drag icon: " + ex.Message); }
        return null;
    }

    private void UpdateFloatingGhost(NativeMethods.Point cursor, bool outside)
    {
        if (_dragGhost == null) return;
        if (_floatingGhost != null && outside && _floatingGhost.Show(cursor, (int)Math.Round(DrawerSettings.DragLift * _presentationScale)))
        {
            if (_mergeAnimation != null && !_morphCommitted) RemoveMergeVisual();
            _dragGhost.Opacity = 0;
        }
        else
        {
            _floatingGhost?.Hide();
            _dragGhost.Opacity = _mergeAnimation == null ? .92 : 0;
        }
    }

    private void DisposeFloatingGhost()
    {
        _floatingGhost?.Dispose();
        _floatingGhost = null;
    }
}
