using System;

namespace Drawer.Core;

internal sealed class FolderMotion(double from, double target, double started)
{
    private double _from = from;
    private double _started = started;
    public double Target { get; private set; } = target;

    public double Sample(double now)
    {
        double duration = Math.Max(1, DrawerSettings.FolderAnimationMs * Math.Abs(Target - _from));
        double t = Math.Clamp((now - _started) / duration, 0, 1);
        return t == 1 ? Target : _from + (Target - _from) * t * t * (3 - 2 * t);
    }

    public void Retarget(double target, double now)
    {
        if (target == Target) return;
        _from = Sample(now);
        _started = now;
        Target = target;
    }
}
