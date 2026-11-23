namespace Drawer.Core;

internal static class DesktopPolicy
{
    // Visible desktop pixels remain desktop even when a restored application keeps focus.
    public static string Evaluate(bool maximizedOnPrimary, bool fullscreenOnPrimary, bool desktopHit, bool ownHit, bool drawerVisible)
    {
        if (fullscreenOnPrimary) return "fullscreen-on-primary";
        if (maximizedOnPrimary) return "foreground-maximized";
        if (desktopHit) return "desktop";
        if (drawerVisible && ownHit) return "drawer-hit";
        return "pointer-not-desktop";
    }
    public static bool Allowed(string reason) => reason is "desktop" or "drawer-hit";
}
