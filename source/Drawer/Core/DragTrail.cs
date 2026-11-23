using System;

namespace Drawer.Core;

internal readonly record struct TrailPoint(double X, double Y);

internal sealed class DragTrail
{
    public const int Capacity = 4;
    public TrailPoint[] Points { get; }

    public DragTrail(TrailPoint[] points) => Points = points;

    public void Step(TrailPoint cursor, double icon, double dt)
    {
        dt = Math.Clamp(dt, 0, .05);
        double follow = 1 - Math.Exp(-dt * 20);
        for (int i = 0; i < Points.Length; i++)
        {
            var previous = Points[i];
            double dx = cursor.X + icon * .09 - previous.X;
            double dy = cursor.Y + icon * .13 - previous.Y;
            // Converge more quickly when the pointer moves far between ticks.
            double distance = Math.Sqrt(dx * dx + dy * dy);
            double extra = Math.Clamp((distance / icon - .7) * .25, 0, .75);
            double weight = 1 - (1 - follow) * Math.Exp(-dt * 28 * extra);
            Points[i] = cursor = new(previous.X + dx * weight, previous.Y + dy * weight);
        }
    }
}
