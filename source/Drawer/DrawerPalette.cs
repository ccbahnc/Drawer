using System;
using Drawer.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Drawer;

internal static class DrawerPalette
{
    private static readonly (string Key, uint Light, uint Dark)[] Colors =
    [
        ("Surface",         0xFFF5F6F7, 0xFF25272B),
        ("Navigation",      0xFFE9EBEE, 0xFF1D1F23),
        ("AcrylicNavigation", 0x78DFE5EE, 0x7817191E),
        ("AcrylicTint",      0xFFE1E5EE, 0xFF22252B),
        ("TransparentTint",  0x1FFFFFFF, 0x4D000000),
        // Combined with the content tint, the navigation bar is about 65% light or 80% dark.
        ("TransparentNavigation", 0x99F2F4F7, 0xB624272D),
        // Keep the content tint light enough to show the host blur. The
        // navigation tint layers over it to keep the controls readable.
        ("BlurTint",         0x88FBFCFD, 0x9410141A),
        ("BlurNavigation",   0x6CE8EDF2, 0x70161A20),
        ("Folder",          0xFFE0E3E7, 0xFF3B3E44),
        ("FolderPanel",     0xEDE0E3E7, 0xEF34373D),
        ("Menu",            0xFFF5F6F7, 0xFF2C2F34),
        ("Hover",           0xFFE8EBEF, 0xFF383C43),
        ("Pressed",         0xFFDCE1E7, 0xFF424750),
        ("Foreground",      0xFF30343A, 0xFFE4E6EA),
        ("Secondary",       0xFF5A626D, 0xFFADB3BC),
        ("Disabled",        0xFF69717C, 0xFF737A85),
        ("Outline",         0xFFD5D9DE, 0xFF484D56),
        ("SelectionBorder", 0xFF9299A2, 0xFF8C949F),
        ("SelectionFill",   0xFF727C89, 0xFF697482),
        ("Check",           0xFFFFFFFF, 0xFFFFFFFF),
        ("Focus",           0xFF656F7D, 0xFFA8B0BC),
        ("Input",           0xFFFFFFFF, 0xFF25272B),
        ("LayoutCell",      0xFFEFF1F4, 0xFF25272B),
        ("LayoutPreview",   0xFFABB5C2, 0xFF697482),
        ("Shade",           0x00000000, 0x39101317),
        ("RecaptureShade",  0xEDF5F6F7, 0xED25272B),
        ("Danger",          0xFF9E3A40, 0xFFE5A4A7),
        ("Transparent",     0x00000000, 0x00000000)
    ];

    public static DrawerColorScheme ColorScheme { get; private set; }
    public static ElementTheme Theme => ColorScheme == DrawerColorScheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
    public static (float Tint, float Luminosity) AcrylicOpacity =>
        ColorScheme == DrawerColorScheme.Dark ? (.48f, .68f) : (.46f, .74f);
    public static SolidColorBrush Brush(string name) => (SolidColorBrush)Application.Current.Resources["Drawer" + name + "Brush"];

    public static void Initialize()
    {
        var resources = Application.Current.Resources;
        foreach (var (key, light, _) in Colors) Brush(key).Color = Unpack(light);

        void Alias(string key, string brush) => resources[key] = Brush(brush);
        Alias("MenuFlyoutPresenterBackground", "Menu");
        Alias("MenuFlyoutPresenterBorderBrush", "Outline");
        Alias("MenuFlyoutItemForeground", "Foreground");
        Alias("MenuFlyoutSeparatorBackground", "Outline");
        foreach (var control in new[] { "Button", "RepeatButton", "MenuFlyoutItem", "MenuFlyoutSubItem" })
        {
            Alias(control + "BackgroundPointerOver", "Hover");
            Alias(control + "BackgroundPressed", "Pressed");
            Alias(control + "ForegroundPointerOver", "Foreground");
            Alias(control + "ForegroundPressed", "Foreground");
            Alias(control + "ForegroundDisabled", "Disabled");
        }
        Alias("TextControlSelectionHighlightColor", "SelectionFill");
        Alias("TextControlBackground", "Input");
        Alias("TextControlBackgroundFocused", "Input");
        Alias("TextControlBackgroundPointerOver", "Hover");
        Alias("TextControlBorderBrush", "Outline");
        Alias("TextControlBorderBrushPointerOver", "SelectionBorder");
        Alias("TextControlBorderBrushFocused", "Focus");
        foreach (var state in new[] { "", "Focused", "PointerOver" }) Alias("TextControlForeground" + state, "Foreground");
        Alias("TextControlForegroundDisabled", "Disabled");
        Alias("SystemControlFocusVisualPrimaryBrush", "Focus");
        Alias("SystemControlFocusVisualSecondaryBrush", "Menu");
    }

    public static void Apply(DrawerColorScheme colorScheme)
    {
        if (colorScheme == ColorScheme) return;
        ColorScheme = colorScheme;
        foreach (var (key, light, dark) in Colors) Brush(key).Color = Unpack(colorScheme == DrawerColorScheme.Dark ? dark : light);
    }

    public static int NativeColor(string name)
    {
        var color = Brush(name).Color;
        return color.R | color.G << 8 | color.B << 16;
    }

    internal const int NoDwmBorder = unchecked((int)0xFFFFFFFE);
    private const uint DwmBorderColor = 34;

    internal static int SetFrameBorder(nint hwnd, int color) =>
        NativeMethods.DwmSetWindowAttribute(hwnd, DwmBorderColor, ref color, sizeof(int));

    public static int ApplyFrame(Window window, bool titled = false, int? borderColor = null)
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        int dark = ColorScheme == DrawerColorScheme.Dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        int result = SetFrameBorder(hwnd, borderColor ?? NativeColor("Outline"));
        if (!titled || !AppWindowTitleBar.IsCustomizationSupported()) return result;
        var bar = window.AppWindow.TitleBar;
        bar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        bar.BackgroundColor = bar.InactiveBackgroundColor = bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = Brush("Menu").Color;
        bar.ForegroundColor = bar.ButtonForegroundColor = bar.ButtonHoverForegroundColor = bar.ButtonPressedForegroundColor = Brush("Foreground").Color;
        bar.InactiveForegroundColor = bar.ButtonInactiveForegroundColor = Brush("Secondary").Color;
        bar.ButtonHoverBackgroundColor = Brush("Hover").Color;
        bar.ButtonPressedBackgroundColor = Brush("Pressed").Color;
        return result;
    }

    private static Color Unpack(uint value) => Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);

    public static double PanelRadius(double icon, double width, double height) =>
        Math.Min(icon / 2, Math.Min(width, height) / 4);
}
