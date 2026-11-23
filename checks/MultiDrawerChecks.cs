using Drawer.Core;
using System.Text.Json;
internal static class MultiDrawerChecks
{
    public static void Run()
    {
        int checks=0;void Check(bool value,string name){if(!value)throw new Exception("multi: "+name);checks++;Console.WriteLine("PASS multi: "+name);}
        void Reject(Action action,string title){bool rejected=false;try{action();}catch(Exception ex)when(ex is ArgumentException or IOException or InvalidOperationException){rejected=true;}Check(rejected,title);}
        string project=AppContext.BaseDirectory;while(!File.Exists(Path.Combine(project,"Drawer.sln")))project=Directory.GetParent(project)!.FullName;
        string root=Path.Combine(project,"temp","multi14-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var legacy=new DrawerCatalog(root);legacy.Load();var stores=new Dictionary<DrawerEdge,ShortcutStore>();
        var sources=Enumerable.Range(0,3).Select(i=>{string p=Path.Combine(root,$"source{i}.url");File.WriteAllText(p,"[InternetShortcut]\nURL=https://example.com/"+i);return p;}).ToArray();
        foreach(var edge in Enum.GetValues<DrawerEdge>()){var s=new ShortcutStore(legacy.DataRoot(edge),root);s.Load();s.Import(sources);s.Merge(s.TopOrder[0],s.TopOrder[1]);s.Reorder(s.TopOrder.AsEnumerable().Reverse());s.SetLayout(new(3,4,33,false));stores[edge]=s;}
        legacy.SetEnabled(DrawerEdge.Left,false);var prefs=new DrawerPreferences(root);prefs.Load(legacy,stores);
        var docs=stores.ToDictionary(p=>p.Key,p=>File.ReadAllBytes(Path.Combine(p.Value.Root,"library.json")));
        var catalog=new MultiDrawerCatalog(root);catalog.Load(legacy);
        Check(catalog.Value.Drawers.Count==3&&catalog.Value.Drawers.All(d=>d.Id==MultiDrawerCatalog.LegacyId(d.Edge)&&d.Rows==4&&d.Columns==3),"legacy stable identities and layout preserved");
        Check(!catalog.Value.Drawers.Single(d=>d.Edge==DrawerEdge.Left).Enabled&&!catalog.Value.Global.ShowNames&&catalog.Value.Global.IconSize==33,"legacy flags and global options retained");
        Check(docs.All(p=>p.Value.SequenceEqual(File.ReadAllBytes(Path.Combine(stores[p.Key].Root,"library.json"))))&&Directory.GetFiles(Path.Combine(root,"CollectionMigration"),"*",SearchOption.AllDirectories).Length>=4,"pre-migration backup and byte-identical content");
        var reload=new MultiDrawerCatalog(root);reload.Load(legacy);Check(reload.Value.Drawers.SequenceEqual(catalog.Value.Drawers),"repeat startup never duplicates drawers");
        var top=catalog.Value.Drawers.Single(d=>d.Edge==DrawerEdge.Top);var left=catalog.Value.Drawers.Single(d=>d.Edge==DrawerEdge.Left);
        Guid second=catalog.Add(DrawerEdge.Top),third=catalog.Add(DrawerEdge.Top);
        Check(catalog.Ordered(DrawerEdge.Top).Select(d=>d.Name).SequenceEqual(new[]{"顶部抽屉 1","顶部抽屉 2","顶部抽屉 3"})&&catalog.OpenStore(second).Items.Count==0,"new drawer has independent empty content and sequential name");
        catalog.Rename(second,"Tools");Reject(()=>catalog.Rename(third," tools "),"same-edge names reject trim/case duplicate");Reject(()=>catalog.Rename(third,"  "),"empty name rejected");catalog.Rename(left.Id,"Tools");
        var otherDrawers=catalog.Value.Drawers.Where(d=>d.Id!=second).ToArray();catalog.Update(second,d=>d with{Rows=2,Columns=3});Check(otherDrawers.SequenceEqual(catalog.Value.Drawers.Where(d=>d.Id!=second))&&catalog.Value.Drawers.Single(d=>d.Id==second).Name=="Tools","renamed drawer layout updates only its stable identity");
        Reject(()=>catalog.Update(second,d=>d with{Id=Guid.NewGuid()}),"settings cannot replace a stable drawer identity");
        var dwell=new DrawerDwell();Check(!dwell.Update(second,0)&&!dwell.Update(second,199)&&dwell.Update(second,200),"switch dwell uses existing 200ms delay");Check(!dwell.Update(third,201)&&!dwell.Update(second,300)&&!dwell.Update(null,500)&&!dwell.Update(second,501),"rapid transit and reverse movements discard stale candidates");
        var allOff=catalog.Value with{Drawers=catalog.Value.Drawers.Select(d=>d with{Enabled=false}).ToList()};catalog.Commit(allOff);
        Check(AutomaticLayout.Compute(catalog.Value,new(0,0,1920,1080),new(0,0,1920,1040),144).Items.Count==0,"all disabled has no trigger");
        Console.WriteLine($"ALL {checks} MULTI DRAWER CHECKS PASSED");
    }
}
