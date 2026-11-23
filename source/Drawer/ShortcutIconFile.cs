using System;
using System.Threading.Tasks;
using Drawer.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Drawer;

internal static class ShortcutIconFile
{
    internal static async Task<IconPixels?> ReadAsync(string path, int requested)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.IcoDecoderId, stream);
            BitmapFrame? best = null;
            int bestScore = int.MaxValue;
            for (uint i = 0; i < Math.Min(64u, decoder.FrameCount); i++)
            {
                var frame = await decoder.GetFrameAsync(i);
                if (frame.PixelWidth is 0 or > 1024 || frame.PixelHeight is 0 or > 1024) continue;
                int size = (int)Math.Max(frame.PixelWidth, frame.PixelHeight);
                int score = size >= requested ? size - requested : 1024 + requested - size;
                if (score >= bestScore) continue;
                best = frame;
                bestScore = score;
            }
            if (best == null) return null;
            var pixels = await best.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            return new((int)best.PixelWidth, (int)best.PixelHeight, pixels.DetachPixelData());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PrototypeLog.Write("explicit icon decode unavailable: " + ex.GetType().Name);
            return null;
        }
    }
}
