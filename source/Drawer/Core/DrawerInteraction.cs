using System;

namespace Drawer.Core;

// A monotonic clock is supplied by the host. No queued delayed callbacks or animation restarts.
internal sealed class DrawerInteraction
{
    private double? _enteredAt;
    private double? _leftAt;
    public bool TargetOpen { get; private set; }
    public double Position { get; private set; }
    public double Velocity { get; private set; }
    public string State => Position == 0 && !TargetOpen ? (_enteredAt.HasValue ? "Dwelling" : "Hidden")
        : _leftAt.HasValue ? "ClosePending" : TargetOpen ? (Position == 1 ? "Open" : "Opening") : "Closing";

    public void Reset()
    {
        _enteredAt = _leftAt = null;
        TargetOpen = false;
        Position = Velocity = 0;
    }

    public void CloseStep(double nowMs,double dtSeconds)
    {
        TargetOpen=false; _enteredAt=_leftAt=null;
        Update(nowMs,dtSeconds,true,false,false);
    }

    public void KeepOpen()
    {
        _enteredAt = _leftAt = null;
        TargetOpen = true;
    }

    public void HoldBeforeFirstFrame() => Position = Velocity = 0;

    public void Update(double nowMs, double dtSeconds, bool allowed, bool trigger, bool inside)
    {
        if (!allowed) { Reset(); return; }
        if (Position == 0 && !TargetOpen)
        {
            _leftAt = null;
            if (!trigger) _enteredAt = null;
            else
            {
                _enteredAt ??= nowMs;
                if (nowMs - _enteredAt >= DrawerSettings.OpenDelayMs)
                {
                    TargetOpen = true;
                    _enteredAt = null;
                }
            }
        }
        else if (trigger || inside)
        {
            _leftAt = null;
            TargetOpen = true;
        }
        else if (TargetOpen)
        {
            _leftAt ??= nowMs;
            if (nowMs - _leftAt >= DrawerSettings.CloseDelayMs)
            {
                TargetOpen = false;
                _leftAt = null;
            }
        }

        // Exact critically damped spring solution: velocity survives target reversals.
        double target = TargetOpen ? 1 : 0;
        double offset = Position - target;
        double omega = DrawerSettings.SpringFrequency;
        double t = Math.Clamp(dtSeconds, 0, 0.05);
        double factor = Math.Exp(-omega * t);
        double c = Velocity + omega * offset;
        Position = Math.Clamp(target + (offset + c * t) * factor, 0, 1);
        Velocity = (Velocity - omega * c * t) * factor;
        if (Math.Abs(Position - target) < 0.001 && Math.Abs(Velocity) < 0.02)
        {
            Position = target;
            Velocity = 0;
        }
    }
}
