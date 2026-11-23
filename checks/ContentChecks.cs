using Drawer.Core;
using System.Text.Json;

internal static class ContentChecks
{
    public static void Run()
    {
        int checks=0;
        void Check(bool pass,string title) {if(!pass)throw new Exception("FAIL content: "+title);checks++;Console.WriteLine("PASS content: "+title);}
        foreach(uint dpi in new uint[]{96,120,144})
        foreach(var edge in Enum.GetValues<DrawerEdge>())
        foreach(bool names in new[]{true,false})
        foreach(int count in new[]{0,1,2,5,7,30,31,100})
        {
            var request=new DrawerLayout(6,5,38,names);
            var m=LayoutMetrics.Fit(request,1920/(dpi/96d),1040/(dpi/96d),count,edge);
            Check(m.Width==m.Columns*m.CellWidth+LayoutMetrics.HorizontalChrome && m.Height==m.Rows*m.CellHeight+LayoutMetrics.VerticalChrome
                && m.Columns<=6 && m.Rows<=5 && m.Effective==request && m.Width<=1920/(dpi/96d)-64 && m.Height<=1040/(dpi/96d)-32,$"{dpi}/{edge}/{names}/{count}: complete bounded cells; capacity preserved");
            if(count<=1)Check(m.Columns==6 && m.Rows==5,"empty capacity stays fixed");
            if(edge!=DrawerEdge.Top && count<=5)Check(m.Columns==6 && m.Rows==5,"side uses configured capacity");
        }
        var one=LayoutMetrics.Fit(new(1,5,38),1920,1080,7,DrawerEdge.Left);
        var hidden=LayoutMetrics.Fit(new(1,5,38,false),1920,1080,7,DrawerEdge.Left);
        Check(one.Width==122 && hidden.Width==122 && hidden.CellHeight==81 && one.CellHeight==81,"names preserve all geometry");
        var thirty=LayoutMetrics.Fit(new(),1920,1080,31);
        Check(thirty.Columns==6 && thirty.Rows==5 && thirty.HitIndex(1,1,thirty.CellHeight*5,31,false)==30,"cells beyond full capacity remain scrollable");
        Check(JsonSerializer.Deserialize<DrawerLayout>("{\"Columns\":3,\"Rows\":2,\"IconSize\":24}")!.ShowNames,"old layout missing name flag defaults on");
        var animation = new MergeAnimation(100);
        animation.Advance(150);
        Check(animation.Progress == .25 && animation.Eased > 0 && animation.Eased < 1, "entry has intermediate scale and position before the threshold");
        animation.ReverseTo(false, 150);
        Check(animation.Progress == .25, "reversing preserves the last painted progress");
        animation.Advance(175);
        Check(animation.Progress > 0 && animation.Progress < .25, "leaving reverses from current progress");
        animation.Advance(200);
        Check(animation.Finished && animation.Progress == 0, "cancel converges exactly with no residual");
        animation.ReverseTo(true, 250);
        animation.Advance(300);
        animation.ReverseTo(true, 300); // Confirming an early append must not restart the preview.
        animation.Advance(450);
        Check(animation.Progress == 1 && animation.Finished, "early append completes 200ms from entry without restarting");
        string project=AppContext.BaseDirectory;while(!File.Exists(Path.Combine(project,"Drawer.sln")))project=Directory.GetParent(project)!.FullName;
        string root=Path.Combine(project,"temp","content-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var paths=Enumerable.Range(0,4).Select(i=>{string path=Path.Combine(root,i+".url");File.WriteAllText(path,"[InternetShortcut]\nURL=https://example.com/"+i);return path;}).ToArray();
        var store=new ShortcutStore(Path.Combine(root,"data"));store.Load();store.Import(paths);var ids=store.TopOrder.ToArray();
        var folder=store.Merge(ids[0],ids[2]);
        Check(store.TopSlots.SequenceEqual(new[]{Guid.Empty,ids[1],folder,ids[3]}) && store.Groups.Single().Members.SequenceEqual(new[]{ids[2],ids[0]}),"merge keeps the target cell and vacates the source atomically");
        store.SetLayout(new(1,5,38,false));var again=new ShortcutStore(store.Root);again.Load();
        Check(!again.Layout.ShowNames && again.TopOrder.SequenceEqual(store.TopOrder) && again.Groups.Single().Members.SequenceEqual(store.Groups.Single().Members),"restart restores hidden names, group ownership and order");
        var saved=File.ReadAllBytes(Path.Combine(store.Root,"library.json"));bool rejected=false;
        try{store.Merge(ids[1],Guid.Empty);}catch(ArgumentException){rejected=true;}
        Check(rejected && saved.SequenceEqual(File.ReadAllBytes(Path.Combine(store.Root,"library.json"))),"invalid merge target leaves committed document unchanged");
        Console.WriteLine($"ALL {checks} CONTENT CHECKS PASSED");
    }
}
