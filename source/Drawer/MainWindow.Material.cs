using System;
using Drawer.Core;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;
using WinRT;
using System.Runtime.InteropServices;
using Windows.UI.ViewManagement;
using static Drawer.NativeMethods;

namespace Drawer;

public sealed partial class MainWindow
{
    private SpriteVisual? _materialFill;
    private CompositionColorBrush? _materialFillBrush;
    private Vector2? _manualMaterialSize;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _acrylicConfiguration;
    private bool _acrylicUnavailable;
    private double _acrylicRetryAt;
    private DrawerMaterial _displayedMaterial;
    private UISettings? _backdropSettings;
    private bool _effectsSubscribed;
    private Windows.UI.Composition.Compositor? _backdropCompositor;
    private Windows.UI.Composition.CompositionBrush? _backdropBrush;
    private bool _hostBackdropEnabled;
    private const uint UseHostBackdropBrush = 17; // DWMWA_USE_HOSTBACKDROPBRUSH
    private const uint GetHighContrast = 0x0042; // SPI_GETHIGHCONTRAST
    private const uint HighContrastOn = 1;
    private const int RemoteSession = 0x1000; // SM_REMOTESESSION
    private double? _acrylicPrimedAt;
    private const double AcrylicWarmupMs = 140;

    private bool ApplyDrawerMaterial()
    {
        var material = _preferences.Value.Global.Material;
        if (material is DrawerMaterial.Blur or DrawerMaterial.Transparent)
        {
            StopAcrylic();
            _acrylicUnavailable = false;
            _acrylicRetryAt = 0;
            if (_displayedMaterial != material) DetachNativeBackdrop();
            return SetMaterialPolicy(ApplyNativeBackdrop(material) ? material : DrawerMaterial.Solid);
        }
        StopNativeBackdrop();
        if (material == DrawerMaterial.Solid)
        {
            bool changed = SetMaterialPolicy(DrawerMaterial.Solid);
            StopAcrylic();
            _acrylicUnavailable = false;
            _acrylicRetryAt = 0;
            return changed;
        }
        if (_acrylicUnavailable && _clock.Elapsed.TotalMilliseconds < _acrylicRetryAt) return SetMaterialPolicy(DrawerMaterial.Solid);

        try
        {
            if (_acrylicController == null)
            {
                if (!DesktopAcrylicController.IsSupported())
                {
                    _acrylicUnavailable = true;
                    _acrylicRetryAt = double.PositiveInfinity;
                    PrototypeLog.Write("material: solid fallback; desktop acrylic is unsupported");
                    return SetMaterialPolicy(DrawerMaterial.Solid);
                }
                DispatcherQueue.EnsureSystemDispatcherQueue();
                _acrylicConfiguration = new SystemBackdropConfiguration
                {
                    // An edge drawer presents transient UI without taking keyboard focus.
                    // Input-active here is backdrop policy, not HWND activation.
                    IsInputActive = true
                };
                _acrylicController = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
                ConfigureAcrylic();
                _acrylicController.SetSystemBackdropConfiguration(_acrylicConfiguration);
                _acrylicController.StateChanged += AcrylicStateChanged;
                if (!_acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>()))
                    throw new InvalidOperationException(UiText.Get("AcrylicRejected"));
                PrototypeLog.Write($"material: desktop acrylic state={_acrylicController.State} hwnd={_hwnd}");
            }
            else ConfigureAcrylic();

            _acrylicUnavailable = false;
            return SetMaterialPolicy(_acrylicController.State == SystemBackdropState.Active ? DrawerMaterial.Acrylic : DrawerMaterial.Solid);
        }
        catch (Exception ex)
        {
            bool changed = SetMaterialPolicy(DrawerMaterial.Solid);
            StopAcrylic();
            _acrylicUnavailable = true;
            _acrylicRetryAt = _clock.Elapsed.TotalMilliseconds + 30000;
            PrototypeLog.Write("material: solid fallback; " + ex.Message);
            return changed;
        }
    }

    private void RetryAcrylicIfDue(double now)
    {
        if (_acrylicUnavailable && now >= _acrylicRetryAt && !_visible &&
            _preferences.Value.Global.Material == DrawerMaterial.Acrylic)
            ApplyWindowAppearance();
    }

    private void ConfigureAcrylic()
    {
        _acrylicConfiguration!.Theme = DrawerPalette.ColorScheme == DrawerColorScheme.Dark
            ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        var (tint, luminosity) = DrawerPalette.AcrylicOpacity;
        _acrylicController!.FallbackColor = DrawerPalette.Brush("Surface").Color;
        _acrylicController.TintColor = DrawerPalette.Brush("AcrylicTint").Color;
        _acrylicController.TintOpacity = tint;
        _acrylicController.LuminosityOpacity = luminosity;
    }

    private void AcrylicStateChanged(ISystemBackdropControllerWithTargets sender, object args)
    {
        var controller = _acrylicController;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed || controller == null || controller != _acrylicController) return;
            var state = controller.State;
            if (SetMaterialPolicy(state == SystemBackdropState.Active ? DrawerMaterial.Acrylic : DrawerMaterial.Solid) && _openFolder != null)
                _ = CaptureFolderBackdrop();
            PrototypeLog.Write($"material: desktop acrylic state={state} hwnd={_hwnd} foreground={NativeMethods.GetForegroundWindow()}");
        });
    }

    private bool SetMaterialPolicy(DrawerMaterial material)
    {
        bool changed = _displayedMaterial != material;
        if (changed && !_previewOnly) IdlePerformance.Invalidate(IdlePerformance.Change.Material);
        _displayedMaterial = material;
        var (cover, navigation) = material switch
        {
            DrawerMaterial.Transparent => ("TransparentTint", "TransparentNavigation"),
            DrawerMaterial.Blur => ("BlurTint", "BlurNavigation"),
            DrawerMaterial.Acrylic => ("Surface", "AcrylicNavigation"),
            _ => ("Surface", "Navigation")
        };
        var brush = DrawerPalette.Brush(cover);
        if (_materialFillBrush != null) _materialFillBrush.Color = brush.Color;
        else MaterialCover.Background = brush;
        MaterialCover.Opacity = material == DrawerMaterial.Acrylic ? 0 : 1;
        NavigationBar.Background = DrawerPalette.Brush(navigation);
        ApplySolidWindowBackground(material);
        return changed;
    }

    private (DrawerMaterial Material, DrawerColorScheme Scheme) _solidBackgroundKey = ((DrawerMaterial)(-1), (DrawerColorScheme)(-1));

    /// <summary>
    /// 纯色材质的卡片不透明，可以把窗口类背景（DWM 圆角裁剪下露出的那 1–2 像素环）
    /// 设成同一个表面色，边缘就完全看不出来，浅色深色都成立。
    /// 半透明材质的卡片颜色取决于壁纸，用不透明画刷只会画出一条与卡片不符的线
    /// （浅色偏白、深色偏黑），所以这些材质不设窗口背景。
    /// </summary>
    private void ApplySolidWindowBackground(DrawerMaterial material)
    {
        var key = (material, DrawerPalette.ColorScheme);
        if (key == _solidBackgroundKey) return;
        if (!WindowClassBackground.Apply(_hwnd,
            material == DrawerMaterial.Solid ? DrawerPalette.NativeColor("Surface") : null)) return;
        _solidBackgroundKey = key;
        InvalidateRect(_hwnd, 0, true);
        if (!_previewOnly && RevealTailTrace.Enabled)
            PrototypeLog.Write($"gdi-count build={IdlePerformance.Build} material={material} scheme={DrawerPalette.ColorScheme} count={WindowClassBackground.GdiCount()}");
    }

    private void InitializeMaterialFill()    {
        // 卡片底色不再用"手动尺寸的合成 SpriteVisual"。窗口尺寸每变一次，XAML 岛会重建
        // 合成资源，挂在 MaterialCover 上的子视觉会丢一帧，表现为拖动放大时整张卡面
        // 短暂消失、透出桌面（各材质配色的"边框"其实是这个洞露出的桌面）。
        // 改回普通 XAML 背景：随元素布局绘制，缩放帧里也始终存在。
        MaterialCover.Background = DrawerPalette.Brush("Surface");
        PrototypeLog.Write("material: XAML background fill");
    }

    private void DisposeMaterialFill()
    {
        _materialFill?.Dispose();
        _materialFillBrush?.Dispose();
        _materialFill = null;
        _materialFillBrush = null;
        _manualMaterialSize = null;
    }

    private void SetMaterialFillSize(int clientWidth, int clientHeight)
    {
        if (_materialFill is not { } fill) return;
        double scale = MaterialCover.XamlRoot?.RasterizationScale ?? _presentationScale;
        var size = new Vector2((float)(Math.Max(0, clientWidth) / scale),
            (float)(Math.Max(0, clientHeight) / scale));
        if (_manualMaterialSize == size) return;
        fill.RelativeSizeAdjustment = Vector2.Zero;
        fill.Size = size;
        _manualMaterialSize = size;
    }

    private void SettleMaterialFill()
    {
        if (_materialFill == null) return;
        // 原生客户区比 XAML 岛每边大约 2 物理像素（150% 下约 1.33 DIP）。
        // 着色块始终按实测客户区铺满，才能盖住这圈没有着色层的边；
        // 退回“跟随元素尺寸”会把它露出来，表现为抽屉四周一圈暗边。
        if (GetClientRect(_hwnd, out var client) && client.Right > client.Left && client.Bottom > client.Top)
        {
            SetMaterialFillSize(client.Right - client.Left, client.Bottom - client.Top);
            return;
        }
        UseLayoutMaterialFill();
    }

    private void UseLayoutMaterialFill()
    {
        if (_materialFill is not { } fill || _manualMaterialSize == null) return;
        fill.Size = Vector2.Zero;
        fill.RelativeSizeAdjustment = Vector2.One;
        _manualMaterialSize = null;
    }

    private bool ApplyNativeBackdrop(DrawerMaterial material)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return false;
        string stage = "check system effects policy";
        try
        {
            if (_backdropSettings == null)
            {
                _backdropSettings = new UISettings();
                try
                {
                    _backdropSettings.AdvancedEffectsEnabledChanged += BackdropEffectsChanged;
                    _effectsSubscribed = true;
                }
                catch (Exception ex)
                {
                    // Desktop windows also recheck policy on WM_SETTINGCHANGE.
                    PrototypeLog.Write($"backdrop effects event unavailable: 0x{ex.HResult:X8}");
                }
            }
            var contrast = new HighContrastSettings { Size = (uint)Marshal.SizeOf<HighContrastSettings>() };
            bool effects = _backdropSettings.AdvancedEffectsEnabled;
            bool contrastKnown = SystemParametersInfoW(GetHighContrast, contrast.Size, ref contrast, 0);
            bool highContrast = (contrast.Flags & HighContrastOn) != 0;
            bool remote = GetSystemMetrics(RemoteSession) != 0;
            bool desktopComposed = DwmIsCompositionEnabled(out bool composition) == 0 && composition;
            if (!effects || !contrastKnown || highContrast || remote || !desktopComposed)
            {
                PrototypeLog.Write($"backdrop policy: {material} effects={effects} contrastKnown={contrastKnown} highContrast={highContrast} remote={remote} composed={desktopComposed}");
                DetachNativeBackdrop();
                return false;
            }
            if (_backdropBrush != null) return true;

            // The WinUI system backdrop target takes brushes from the OS compositor.
            stage = "initialize system dispatcher queue";
            DispatcherQueue.EnsureSystemDispatcherQueue();
            if (material == DrawerMaterial.Blur)
            {
                stage = "enable DWM host backdrop";
                int enabled = 1;
                Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(_hwnd, UseHostBackdropBrush, ref enabled, sizeof(int)));
                _hostBackdropEnabled = true;
            }
            else
            {
                stage = "enable DWM client alpha";
                EnableTransparentClient();
            }
            stage = "create system compositor";
            _backdropCompositor = new Windows.UI.Composition.Compositor();
            stage = "create backdrop brush";
            _backdropBrush = material == DrawerMaterial.Blur
                ? _backdropCompositor.CreateHostBackdropBrush()
                : _backdropCompositor.CreateColorBrush(Microsoft.UI.Colors.Transparent);
            stage = "attach backdrop brush";
            this.As<ICompositionSupportsSystemBackdrop>().SystemBackdrop = _backdropBrush;
            PrototypeLog.Write($"material: {material} attached hwnd={_hwnd}");
            return true;
        }
        catch (Exception ex)
        {
            PrototypeLog.Write($"backdrop fallback material={material} hwnd={_hwnd} stage={stage}: {ex}");
            DetachNativeBackdrop();
            return false;
        }
    }

    private void BackdropEffectsChanged(UISettings sender, object args) => QueueBackdropRefresh();
    private void QueueBackdropRefresh(bool reapplyHost = false, bool compositionChanged = false) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closed || _preferences.Value.Global.Material is not (DrawerMaterial.Blur or DrawerMaterial.Transparent)) return;
        if (reapplyHost && _hostBackdropEnabled)
        {
            // Reassert the DWM attribute without replacing the attached brush.
            int enabled = 1;
            if (DwmSetWindowAttribute(_hwnd, UseHostBackdropBrush, ref enabled, sizeof(int)) < 0)
                DetachNativeBackdrop();
        }
        if (compositionChanged && _transparentClientEnabled) DetachNativeBackdrop();
        ApplyWindowAppearance();
    });

    private void DetachNativeBackdrop()
    {
        var brush = _backdropBrush;
        var compositor = _backdropCompositor;
        _backdropBrush = null;
        _backdropCompositor = null;
        try
        {
            if (brush != null && !_closed)
                this.As<ICompositionSupportsSystemBackdrop>().SystemBackdrop = null;
        }
        catch (Exception ex) { PrototypeLog.Write("backdrop detach: " + ex.Message); }
        finally
        {
            brush?.Dispose();
            compositor?.Dispose();
            if (_hostBackdropEnabled && !_closed)
            {
                int disabled = 0;
                DwmSetWindowAttribute(_hwnd, UseHostBackdropBrush, ref disabled, sizeof(int));
            }
            _hostBackdropEnabled = false;
            DisableTransparentClient();
        }
    }

    private void StopNativeBackdrop()
    {
        DetachNativeBackdrop();
        if (_effectsSubscribed && _backdropSettings != null)
        {
            try { _backdropSettings.AdvancedEffectsEnabledChanged -= BackdropEffectsChanged; }
            catch (Exception ex) { PrototypeLog.Write($"backdrop effects event detach: 0x{ex.HResult:X8}"); }
        }
        _effectsSubscribed = false;
        _backdropSettings = null;
    }

    private void PrimeAcrylic()
    {
        if (_closed || (_acrylicController == null && _displayedMaterial == DrawerMaterial.Solid) ||
            _visible || _acrylicPrimedAt != null) return;
        var bounds = _geometry.Drawer;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        // Prepare the real, full-size composition target beyond the entire
        // virtual screen. It never activates or participates in desktop hit tests.
        bool slideOpening = CanSlideInFromDesktopEdge(bounds);
        int offscreenX = GetSystemMetrics(76) - bounds.Width - 32;
        int offscreenY = bounds.Top;
        if (slideOpening)
        {
            // Keep the entire client outside the desktop while the native backdrop initializes.
            if (_edge == DrawerEdge.Top) offscreenY = bounds.Top - bounds.Height - 32;
            else if (_edge == DrawerEdge.Left) offscreenX = bounds.Left - bounds.Width - 32;
            else offscreenX = bounds.Right + 32;
        }
        _drawerSlide.X = _drawerSlide.Y = 0;
        WindowSurface.UpdateLayout();
        if (!SetWindowPos(_hwnd, 0, offscreenX, offscreenY,
            bounds.Width, bounds.Height, 0x0214)) return;
        _acrylicPrimedAt = _clock.Elapsed.TotalMilliseconds;
        ShowWindow(_hwnd, 4); // SW_SHOWNOACTIVATE
        PrototypeLog.Write($"material: priming offscreen hwnd={_hwnd} edge={_edge} fixedReveal={slideOpening} size={bounds.Width}x{bounds.Height}");
    }

    private bool WaitForAcrylicPrime()
    {
        PrimeAcrylic();
        if (_acrylicPrimedAt is not double since) return false;
        if (_clock.Elapsed.TotalMilliseconds - since < AcrylicWarmupMs) return true;
        DwmFlush();
        return false;
    }

    private void EndAcrylicPrime()
    {
        if (_acrylicPrimedAt == null) return;
        _acrylicPrimedAt = null;
        if (!_visible) ShowWindow(_hwnd, 0); // SW_HIDE
    }

    private void StopAcrylic()
    {
        if (_acrylicController == null) return;
        var controller = _acrylicController;
        _acrylicController = null;
        _acrylicConfiguration = null;
        controller.StateChanged -= AcrylicStateChanged;
        if (!_closed) controller.RemoveAllSystemBackdropTargets();
        controller.Dispose();
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint hwnd, nint rect, [MarshalAs(UnmanagedType.Bool)] bool erase);
    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrastSettings { internal uint Size, Flags; internal nint Scheme; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint parameter, ref HighContrastSettings settings, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
