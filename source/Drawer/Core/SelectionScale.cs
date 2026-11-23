using System;

namespace Drawer.Core;

internal readonly record struct SelectionScale(double From, double To, double Start, bool Pop = false)
{
    public double Duration => Pop ? DrawerSettings.SelectionPopMs : DrawerSettings.SelectionRestoreMs;

    public static double Target(bool hovered, bool singleSelected, bool blocked) =>
        !blocked && (hovered || singleSelected) ? DrawerSettings.SelectionHeldScale : 1;

    public double At(double now)
    {
        double t = Math.Clamp((now - Start) / Duration, 0, 1);
        if (!Pop) return Blend(From, To, t);
        const double dip = .38;
        return t <= dip ? Blend(From, DrawerSettings.SelectionDipScale, t / dip)
            : Blend(DrawerSettings.SelectionDipScale, To, (t - dip) / (1 - dip));
    }

    private static double Blend(double a, double b, double t) => a + (b - a) * (t * t * (3 - 2 * t));
}
