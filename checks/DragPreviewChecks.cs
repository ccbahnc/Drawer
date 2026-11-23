using Drawer.Core;
internal static class DragPreviewChecks
{
    public static void Run()
    {
        var ids=Enumerable.Range(0,4).Select(_=>Guid.NewGuid()).ToArray(); var drag=new DragPreview(ids,ids[0]); int n=0;
        void Check(bool yes,string label){if(!yes)throw new Exception("FAIL preview: "+label);n++;Console.WriteLine("PASS preview: "+label);}
        var sequence = new FolderInsertionSequence(Guid.NewGuid(), ids, new[] { ids[0] });
        void DragTo(int gap) => drag.PreviewMembers(new(sequence, gap));
        DragTo(2); Check(drag.Order.SequenceEqual(new[]{ids[1],ids[2],ids[0],ids[3]}),"A between C and D previews B C A D");
        Check(ids[0]==drag.Original[0] && drag.Valid,"preview leaves original order untouched");
        for(int i=0;i<30;i++)DragTo(2); Check(drag.Order.SequenceEqual(new[]{ids[1],ids[2],ids[0],ids[3]}),"stationary insertion boundary never oscillates");
        DragTo(0);Check(drag.Order.SequenceEqual(ids),"reverse through several cells restores original order");
        DragTo(3);Check(drag.Order.Last()==ids[0],"last boundary accepts an item");
        drag.Cancel();Check(!drag.Valid && drag.Order.SequenceEqual(ids),"invalid or canceled transaction restores exact original");
        DragTo(1);
        drag.Compact();
        Check(drag.IsCompact && drag.Valid && drag.Order.SequenceEqual(ids.Skip(1)), "receiving removes the source and insertion slots");
        DragTo(0);
        Check(!drag.IsCompact && drag.Order.SequenceEqual(ids), "leaving can place the item back in its source gap");
        var selected = new[] { ids[1], ids[3] };
        var batch = new DragPreview(ids, selected);
        var batchSequence = new FolderInsertionSequence(Guid.NewGuid(), ids, selected);
        void BatchTo(int gap) => batch.PreviewMembers(new(batchSequence, gap));
        BatchTo(2);
        BatchTo(2);
        Check(batch.Index == 2 && batch.Order.SequenceEqual(new[]{ids[0],ids[2],ids[1],ids[3]}),
            "the whole batch is placed relative to the compact candidates");
        BatchTo(1);
        Check(batch.Index == 1 && batch.Order.SequenceEqual(new[]{ids[0],ids[1],ids[3],ids[2]}),
            "moving the preview never changes batch order");
        BatchTo(0);
        Check(batch.VisualSlots.SequenceEqual(new[]{ids[1],ids[3],ids[0],ids[2]}),
            "both selected visuals occupy only the final destination");
        BatchTo(2);
        Check(batch.VisualSlots.SequenceEqual(new[]{ids[0],ids[2],ids[1],ids[3]}),
            "a batch at the end preserves only its final slots");
        batch.Compact();
        Check(batch.VisualSlots.SequenceEqual(new[]{ids[0],ids[2]}), "receiving removes the insertion gap and every original source hole");
        batch.Cancel();
        Check(!batch.IsCompact && !batch.Valid && batch.Order.SequenceEqual(ids), "cancel restores the original order after compaction");
        var incoming = new DragPreview(ids.Take(2).ToArray(), ids.Skip(2).ToArray());
        incoming.PreviewMembers(new(new FolderInsertionSequence(Guid.NewGuid(), ids.Take(2).ToArray(), ids.Skip(2).ToArray()), 1));
        Check(incoming.VisualSlots.SequenceEqual(new[]{ids[0],ids[2],ids[3],ids[1]}),
            "a batch entering a folder shows all destination positions");
        incoming.Compact();
        Check(incoming.Order.SequenceEqual(ids.Take(2)), "destination compaction never reserves cells for incoming items");
        DragTo(2);
        Check(drag.VisualSlots.SequenceEqual(new[]{ids[1],ids[2],ids[0],ids[3]}) &&
            drag.Order.SequenceEqual(new[]{ids[1],ids[2],ids[0],ids[3]}),
            "single item uses the compact order for both visuals and commit");
        drag.Compact();
        Check(drag.VisualSlots.SequenceEqual(ids.Skip(1)), "single merge also removes every hole");
        DragTo(2);
        Check(drag.VisualSlots[2] == ids[0], "a new insertion restores its compact landing slot");
        var trail = new DragTrail(new[]{new TrailPoint(-80, -40), new TrailPoint(-240, 0)});
        trail.Step(new(0, 0), 38, .016);
        Check(trail.Points[0].X is > -80 and < 0 && trail.Points[1].X is > -240 and < -80,
            "followers converge from their original slots without jumping to the lead");
        for(int i=0;i<40;i++) trail.Step(new(40, 0), 38, .016);
        Check(trail.Points.All(p => Math.Abs(p.X - 40) < 10), "the trail gathers near the cursor at rest");
        double beforeReverse = trail.Points[0].X;
        trail.Step(new(-40, 0), 38, .016);
        Check(trail.Points[0].X > -40 && trail.Points[0].X < beforeReverse, "reversing keeps the trail continuous");
        bool rejected=false;try{_=new DragPreview(ids.Append(Guid.Empty).ToArray(),ids[0]);}catch(ArgumentException){rejected=true;}Check(rejected,"operation tile excluded from reorder transaction");
        var motion=new SlotMotion(0,0){TargetX=200,TargetY=80};motion.Step(.016);Check(motion.X>0&&motion.X<200&&motion.Y>0,"movement begins smoothly without teleport");
        for(int i=0;i<8;i++)motion.Step(.016);double x=motion.X,v=motion.Vx;motion.TargetX=-96;Check(motion.X==x&&motion.Vx==v,"target reversal preserves current position and velocity");
        for(int i=0;i<100;i++)motion.Step(.016);Check(motion.X==-96 && motion.Y==80,"reversal converges to requested cell");
        motion.TargetX=0;motion.TargetY=0;for(int i=0;i<100;i++)motion.Step(.016);Check(motion.X==0&&motion.Y==0,"cancel animation returns to original cell");
        Console.WriteLine($"ALL {n} DRAG PREVIEW CHECKS PASSED");
    }
}
