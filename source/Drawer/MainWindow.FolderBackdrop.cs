using System;
using System.Threading.Tasks;
using Drawer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Security.Cryptography;

namespace Drawer;

public sealed partial class MainWindow
{
    private int _backdropGeneration;
    private Image[] _backdropLevels = Array.Empty<Image>();
    private bool _backdropStylePending;
    private double _folderProgress;

    private void RefreshFolderBackdropStyle()
    {
        // Keep the blurred silhouette behind a tint in the new palette
        // until the new pixels are ready, instead of flashing a sharp grid.
        _backdropStylePending = _backdropLevels.Length > 0;
        SetFolderBlur(_folderProgress);
        _ = CaptureFolderBackdrop();
    }

    private async Task CaptureFolderBackdrop()
    {
        int generation = ++_backdropGeneration;
        bool applied = false;
        try
        {
            BackgroundSurface.UpdateLayout();
            double width = BackgroundSurface.ActualWidth, height = BackgroundSurface.ActualHeight;
            if (width < 1 || height < 1) return;
            double scale = Math.Min(.65, 640 / Math.Max(width, height));
            var capture = new RenderTargetBitmap();
            await capture.RenderAsync(BackgroundSurface, Math.Max(1, (int)Math.Round(width * scale)),
                Math.Max(1, (int)Math.Round(height * scale)));
            if (_closed || generation != _backdropGeneration) return;
            int w = capture.PixelWidth, h = capture.PixelHeight;
            if (w == 0 || h == 0) throw new InvalidOperationException(UiText.Get("EmptyBackdropSnapshot"));
            CryptographicBuffer.CopyToByteArray(await capture.GetPixelsAsync(), out var pixels);
            var levels = await Task.Run(() => new[]
            {
                BackdropBlur.Apply(pixels, w, h, Math.Max(1, (int)Math.Round(2.5 * scale))),
                BackdropBlur.Apply(pixels, w, h, Math.Max(1, (int)Math.Round(5 * scale))),
                BackdropBlur.Apply(pixels, w, h, Math.Max(1, (int)Math.Round(8 * scale)))
            });
            if (_closed || generation != _backdropGeneration || _openFolder == null) return;
            var images = new Image[levels.Length];
            for (int i = 0; i < levels.Length; i++)
            {
                images[i] = new Image { Source = CopyBgraImage(levels[i], w, h), Stretch = Stretch.Fill, Opacity = 0 };
            }
            FolderBlurImages.Children.Clear();
            foreach (var image in images) FolderBlurImages.Children.Add(image);
            _backdropLevels = images;
            applied = true;
            PrototypeLog.Write($"folder: blurred backdrop ready ({w}×{h})");
        }
        catch (Exception ex) { PrototypeLog.Write("folder backdrop: " + ex.Message); }
        finally
        {
            if (!_closed && generation == _backdropGeneration)
            {
                if (_backdropStylePending && !applied)
                {
                    FolderBlurImages.Children.Clear();
                    _backdropLevels = Array.Empty<Image>();
                }
                _backdropStylePending = false;
                SetFolderBlur(_folderProgress);
            }
        }
    }

    private void SetFolderBlur(double amount)
    {
        _folderProgress = amount;
        FolderBackdrop.Visibility = amount > 0 || _backdropLevels.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        FolderShade.Background = DrawerPalette.Brush(_backdropStylePending ? "RecaptureShade" : "Shade");
        FolderShade.Opacity = amount;
        double level = amount * _backdropLevels.Length;
        // The snapshot contains icons and the navigation overlay only. Both
        // the solid and desktop-acrylic window backgrounds stay live underneath.
        LiveDrawerContent.Opacity = Math.Clamp(1 - level, 0, 1);
        for (int i = 0; i < _backdropLevels.Length; i++)
            _backdropLevels[i].Opacity = Math.Clamp(1 - Math.Abs(level - i - 1), 0, 1);
    }

    private void ClearFolderBackdrop()
    {
        _backdropGeneration++;
        _backdropStylePending = false;
        _folderProgress = 0;
        _backdropLevels = Array.Empty<Image>();
        LiveDrawerContent.Opacity = 1;
        FolderBlurImages.Children.Clear();
        FolderShade.Opacity = 0;
        FolderBackdrop.Visibility = Visibility.Collapsed;
    }
}
