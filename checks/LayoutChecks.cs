using Drawer.Core;
using System.Text.Json;

internal static class LayoutChecks
{
    public static void Run()
    {
        int count = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL layout: " + name); count++; Console.WriteLine("PASS layout: " + name); }
        foreach (uint dpi in new uint[] {96,120,144})
        {
            var m = LayoutMetrics.Fit(new(), 2560/(dpi/96d), 1528/(dpi/96d));
            Check(m.Effective == new DrawerLayout(), $"{dpi} DPI default 6x5 retained");
            Check(m.Width >= 6*m.CellWidth+LayoutMetrics.HorizontalChrome-.01 && m.Height==5*m.CellHeight+LayoutMetrics.VerticalChrome, $"{dpi} DPI thirty complete cells");
            var extreme = LayoutMetrics.Fit(new(999,999,999),2560/(dpi/96d),1528/(dpi/96d));
            Check(extreme.Columns==20 && extreme.Rows==20 && extreme.Adjusted, $"{dpi} DPI fixed 20x20 input bounds");
        }
        foreach(var size in new[]{(640d,480d),(1024d,600d),(1920d,1080d)})
            foreach(int icon in new[]{16,38,96})
            {
                var m=LayoutMetrics.Fit(new(20,20,icon),size.Item1,size.Item2);
                Check(m.Columns==20 && m.Rows==20 && m.Effective.Columns*m.CellWidth+LayoutMetrics.HorizontalChrome<=m.Width+.01
                    && m.Effective.Rows*m.CellHeight+LayoutMetrics.VerticalChrome<=m.Height+.01,"all complete cells on small/large display " + size + " icon="+icon);
            }
        Check(LayoutMetrics.Fit(new(-1,-1,-1),1920,1080).Effective==new DrawerLayout(1,1,16),"invalid settings clamped");
        var items=Enumerable.Range(0,12).Select(i=>new ShortcutEntry(Guid.NewGuid(),$"{i}.lnk",new string('A',64))).ToList();
        var ids=items.Select(e=>e.Id).ToList();
        ids.Add(ids[0]);ids.RemoveAt(0);
        var ordered=ShortcutOrder.Apply(items,ids);
        Check(ordered[^1].Id==items[0].Id && ordered[0].Id==items[1].Id,"first moves to last");
        ids.Insert(0,ids[^1]);ids.RemoveAt(ids.Count-1);
        Check(ShortcutOrder.Apply(items,ids).Select(e=>e.Id).SequenceEqual(items.Select(e=>e.Id)),"last moves to first");
        (ids[2],ids[9])=(ids[9],ids[2]);
        Check(ShortcutOrder.Apply(items,ids)[9].Id==items[2].Id,"middle cross-row move");
        Check(items.Select(e=>e.FileName).SequenceEqual(Enumerable.Range(0,12).Select(i=>$"{i}.lnk")),"source order remains unchanged until committed");
        bool duplicate=false,unknown=false,missing=false;
        try{ShortcutOrder.Apply(items,Enumerable.Repeat(items[0].Id,12));}catch(ArgumentException){duplicate=true;}
        try{ShortcutOrder.Apply(items,items.Select(e=>Guid.NewGuid()));}catch(ArgumentException){unknown=true;}
        try{ShortcutOrder.Apply(items,items.Take(11).Select(e=>e.Id));}catch(ArgumentException){missing=true;}
        Check(duplicate&&unknown&&missing,"invalid orders rejected");
        var originalIds=items.Select(e=>e.Id).ToArray();
        Check(ShortcutOrder.Move(originalIds,originalIds[0],12)[^1]==originalIds[0],"insertion first to end accounts for removal");
        Check(ShortcutOrder.Move(originalIds,originalIds[^1],0)[0]==originalIds[^1],"insertion last to start");
        Check(ShortcutOrder.Move(originalIds,originalIds[5],9)[8]==originalIds[5],"forward insertion accounts for source index");
        Check(ShortcutOrder.Move(originalIds,originalIds[9],3)[3]==originalIds[9],"backward insertion");
        Check(ShortcutOrder.Move(originalIds,originalIds[5],6).SequenceEqual(originalIds),"drop beside same cell is stable");
        bool invalidInsertion=false;
        try{ShortcutOrder.Move(originalIds,originalIds[0],13);}catch(ArgumentException){invalidInsertion=true;}
        Check(invalidInsertion && originalIds.SequenceEqual(items.Select(e=>e.Id)),"invalid insertion never mutates source");
        var hit = LayoutMetrics.Fit(new(),1920,1080);
        Check(hit.HitIndex(1,1,0,37,false)==0 && hit.HitIndex(hit.CellWidth*.75,1,0,37,true)==1,"left/right halves choose before/after");
        Check(hit.HitIndex(1,hit.CellHeight+1,0,37,false)==6,"cross-row hit index");
        Check(hit.HitIndex(1,1,hit.CellHeight*5,37,false)==30,"scroll offset participates in hit testing");
        Check(hit.HitIndex(1,1,hit.CellHeight*7,37,true)==37 && hit.HitIndex(1,1,hit.CellHeight*7,37,false)==-1,"empty area appends but never launches nonexistent item");
        Check(hit.HitIndex(-1,1,0,37,true)==-1 && hit.HitIndex(1,-1,0,37,true)==-1 && hit.HitIndex(hit.CellWidth*6,1,0,37,true)==-1,"outside grid and scrollbar gutter are invalid");
        Check(LayoutMetrics.ScrollDirection(0,390)<0 && LayoutMetrics.ScrollDirection(389,390)>0 && LayoutMetrics.ScrollDirection(195,390)==0,"auto scroll upper/lower edge and neutral center");
        Check(LayoutMetrics.ScrollDirection(28,56)==0 && LayoutMetrics.ScrollDirection(-1,56)==0 && LayoutMetrics.ScrollDirection(56,56)==0,"single small row and outside do not spuriously scroll");
        string project=AppContext.BaseDirectory;
        while(!File.Exists(Path.Combine(project,"Drawer.sln")))project=Directory.GetParent(project)!.FullName;
        string root=Path.Combine(project,"temp","layout-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        // Existing schema without Layout must load with default values.
        File.WriteAllText(Path.Combine(root,"library.json"),JsonSerializer.Serialize(new {Version=1,Items=items}));
        var store=new ShortcutStore(root);store.Load(); Check(store.Layout==new DrawerLayout(),"previous stage config compatible");
        store.Reorder(ids);store.SetLayout(new(4,3,56));
        var restarted=new ShortcutStore(root);restarted.Load();
        Check(restarted.Items.Select(e=>e.Id).SequenceEqual(ids)&&restarted.Layout==new DrawerLayout(4,3,56),"restart restores order and layout");
        store.SetLayout(new(2,1,16));Check(store.Items.Select(e=>e.Id).SequenceEqual(ids),"layout preserves every item and order");
        Console.WriteLine($"ALL {count} ORDER/LAYOUT CHECKS PASSED");
    }
}
