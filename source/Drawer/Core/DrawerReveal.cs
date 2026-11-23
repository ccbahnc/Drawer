using System;
namespace Drawer.Core;

internal readonly record struct DrawerReveal(int Height, double ContentOffsetY)
{
    // At 150% DPI the HWND held a 6 px client strip for all three edges,
    // even when the requested client depth went down to 1 px.
    private const double MinimumClientDepthDip = 4;

    public bool AtRetractionLimit(int frameDepth, double scale) =>
        Height - Math.Max(0, frameDepth) <= Math.Ceiling(MinimumClientDepthDip * Math.Max(.01, scale));

    public PixelRect FixedSizeBounds(PixelRect drawer, DrawerEdge edge)
    {
        return edge switch
        {
            DrawerEdge.Top => new(drawer.Left, drawer.Top + Height - drawer.Height, drawer.Right, drawer.Top + Height),
            DrawerEdge.Left => new(drawer.Left + Height - drawer.Width, drawer.Top, drawer.Left + Height, drawer.Bottom),
            _ => new(drawer.Right - Height, drawer.Top, drawer.Right - Height + drawer.Width, drawer.Bottom)
        };
    }

    public static DrawerReveal Create(int fullOuterHeight,int frameHeight,double scale,double position)
    {
        int full=Math.Max(1,fullOuterHeight), frame=Math.Clamp(frameHeight,0,full);
        int height=Math.Clamp((int)Math.Round(full*Math.Clamp(position,0,1)),1,full);
        int fullClient=full-frame, client=Math.Max(0,height-frame);
        return new(height,-(fullClient-client)/Math.Max(.01,scale));
    }
}
