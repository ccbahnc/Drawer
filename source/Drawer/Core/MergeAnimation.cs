using System;

namespace Drawer.Core;

// Use the same monotonic clock as the hover threshold, independently of the render cadence.
internal sealed class MergeAnimation(double now, double milliseconds = DrawerSettings.MergeAnimationMs)
{
    private double _since = now, _from;
    public bool Forward { get; private set; } = true;
    public double Progress { get; private set; }
    public double Fraction { get; private set; }
    public double Eased => Ease(Progress);
    public bool Finished => Fraction == 1;

    public void Advance(double time)
    {
        double duration = milliseconds * (Forward ? 1 : _from);
        Fraction = duration <= 0 ? 1 : Math.Clamp((time - _since) / duration, 0, 1);
        Progress = _from + ((Forward ? 1 : 0) - _from) * Fraction;
    }

    public void ReverseTo(bool forward, double time)
    {
        if (forward == Forward) return;
        Advance(time);
        _since = time;
        _from = Progress;
        Forward = forward;
        Fraction = 0;
    }

    public static double Ease(double amount) => amount * amount * (3 - 2 * amount);
}
