using System;

namespace Drawer.Core;

internal sealed class PageSnapDeadline
{
    private double? _deadline;
    internal bool Expired(double now, double? slideStarted)
    {
        _deadline ??= (slideStarted is double start ? start + DrawerSettings.PageSlideMs : now) + 750;
        return now >= _deadline.Value;
    }
    internal void Reset() => _deadline = null;
}

internal sealed class PageWheel
{
    private int _total;
    private bool _used;
    private double _last = double.NegativeInfinity;

    public int Step(int delta, double now, bool sliding)
    {
        if (now - _last >= DrawerSettings.PageWheelQuietMs) { _total = 0; _used = false; }
        _last = now;
        if (sliding) { _used = true; return 0; }
        if (_used || delta == 0) return 0;
        if (Math.Sign(delta) != Math.Sign(_total)) _total = 0;
        _total += delta;
        if (Math.Abs(_total) < 120) return 0;
        _used = true;
        return -Math.Sign(_total);
    }

    public void Reset() { _total = 0; _used = false; _last = double.NegativeInfinity; }
}

internal sealed class PageEdgeDwell
{
    private int _edge;
    private double _since;
    private bool _latched;

    // Y is relative to the first row. Window bounds include the navigation bar and padding.
    public static int? BandAt(double x, double y, double width, double windowTop, double windowBottom,
        double gridBottom, double cellHeight)
    {
        double tolerance = DrawerSettings.DragPageOutsideTolerance;
        if (gridBottom <= 0 || cellHeight <= 0 || x < 0 || x >= width ||
            y < windowTop - tolerance || y >= windowBottom + tolerance) return null;
        double lastRowBottom = Math.Min(gridBottom, windowBottom);
        double lowerBandStart = Math.Max(lastRowBottom - cellHeight * DrawerSettings.DragPageBottomRowOverlap,
            Math.Min(lastRowBottom, windowBottom - DrawerSettings.DragPageBottomBand));
        return y < 0 ? -1 : y >= lowerBandStart ? 1 : 0;
    }

    // null means outside the window and its tolerance; zero means the interior.
    public int Step(int? edge, double now)
    {
        if (edge == 0) { Reset(); return 0; }
        // A wheel disarm has no edge and requires a return to the interior.
        if (_latched && (_edge == 0 || edge == _edge)) return 0;
        if (_edge != (edge ?? 0))
        {
            _edge = edge ?? 0;
            _since = now;
            _latched = false;
        }
        if (_edge == 0 || now - _since < DrawerSettings.DragPageDwellMs) return 0;
        _latched = true;
        return _edge;
    }

    public void Reset() { _edge = 0; _latched = false; }
    public void Disarm() { _edge = 0; _latched = true; }
}
