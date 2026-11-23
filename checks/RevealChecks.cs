using Drawer.Core;
internal static class RevealChecks
{
    public static void Run()
    {
        int n=0;
        void Check(bool yes,string label){if(!yes)throw new Exception("FAIL reveal: "+label);n++;Console.WriteLine("PASS reveal: "+label);}
        foreach(double scale in new[]{1d,1.25,1.5})
        {
            int client=(int)Math.Round(545*scale), frame=(int)Math.Ceiling(2*scale), full=client+frame;
            var open=DrawerReveal.Create(full,frame,scale,1); Check(open.Height==full&&open.ContentOffsetY==0,$"{scale} full reveal has no clipping offset");
            int terminalClientDepth = (int)Math.Ceiling(4 * scale);
            foreach (int nativeFrame in new[] { 0, frame })
            {
                int outer = client + nativeFrame;
                var last = DrawerReveal.Create(outer, nativeFrame, scale, (double)(terminalClientDepth + nativeFrame) / outer);
                var stillDrawable = DrawerReveal.Create(outer, nativeFrame, scale, (double)(terminalClientDepth + nativeFrame + 1) / outer);
                Check(last.AtRetractionLimit(nativeFrame, scale), $"{scale} DPI frame={nativeFrame}: native minimum completes retraction");
                Check(!stillDrawable.AtRetractionLimit(nativeFrame, scale), $"{scale} DPI frame={nativeFrame}: deeper client region remains visible");
            }
            int previous=0;
            foreach(double p in new[]{0,.001,.1,.25,.5,.9,1})
            {
                var r=DrawerReveal.Create(full,frame,scale,p);
                if(r.Height<previous || r.Height<1 || r.Height>full || r.ContentOffsetY>0)throw new Exception("reveal bounds");
                Check(Math.Abs((r.ContentOffsetY*scale)+client-Math.Max(0,r.Height-frame))<.0001,$"{scale}/{p} translated bottom aligns with visible client");previous=r.Height;
            }
        }
        var right = new PixelRect(2257,51,2560,1476);
        foreach (int depth in new[] { 14, 102, 187, 303 })
        {
            var opening = DrawerReveal.Create(right.Width,0,1.5,(double)depth/right.Width);
            var outer = opening.FixedSizeBounds(right,DrawerEdge.Right);
            Check(outer.Width == right.Width && outer.Top == right.Top && outer.Bottom == right.Bottom &&
                right.Right - outer.Left == depth && outer.Right >= right.Right,
                $"right opening exposes {depth} px with a constant native width");
        }
        Check(DrawerReveal.Create(right.Width,0,1.5,1).FixedSizeBounds(right,DrawerEdge.Right) == right,
            "the fully open drawer reaches its original docked position");
        var top = new PixelRect(642,-90,1918,932);
        var left = new PixelRect(-60,51,264,1483);
        foreach (int depth in new[] { 7, 14, 31, 102 })
        {
            var topTail = DrawerReveal.Create(top.Height,0,1.5,(double)depth/top.Height).FixedSizeBounds(top,DrawerEdge.Top);
            var leftTail = DrawerReveal.Create(left.Width,0,1.5,(double)depth/left.Width).FixedSizeBounds(left,DrawerEdge.Left);
            Check(topTail.Height == top.Height && topTail.Left == top.Left && topTail.Right == top.Right &&
                topTail.Bottom == top.Top + depth, $"top retraction leaves {depth} px on the desktop");
            Check(leftTail.Width == left.Width && leftTail.Top == left.Top && leftTail.Bottom == left.Bottom &&
                leftTail.Right == left.Left + depth, $"left retraction leaves {depth} px on the desktop");
        }
        Console.WriteLine($"ALL {n} REVEAL CHECKS PASSED");
    }
}
