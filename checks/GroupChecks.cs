using Drawer.Core;
using System.Text.Json;
internal static class GroupChecks
{
    public static void Run()
    {
        string project = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(project,"Drawer.sln"))) project = Directory.GetParent(project)!.FullName;
        string root = Path.Combine(project,"temp","group-tests-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string[] paths = Enumerable.Range(0,6).Select(i => { string p=Path.Combine(root,$"{i}.url"); File.WriteAllText(p,$"[InternetShortcut]\nURL=https://example.com/{i}"); return p; }).ToArray();
        var s = new ShortcutStore(Path.Combine(root,"data")); s.Load(); s.Import(paths);
        var ids = s.Items.Select(e=>e.Id).ToArray(); int n=0;
        void Check(bool value,string title) { if(!value) throw new Exception("FAIL group: "+title); n++; Console.WriteLine("PASS group: "+title); }
        void Reject(Action action,string title) { var before=File.ReadAllText(Path.Combine(s.Root,"library.json")); bool rejected=false; try { action(); } catch(ArgumentException) { rejected=true; } Check(rejected && before==File.ReadAllText(Path.Combine(s.Root,"library.json")),title); }
        var folder=s.Merge(ids[0],ids[3]);
        Check(s.TopOrder.SequenceEqual(new[]{ids[1],ids[2],folder,ids[4],ids[5]}),"folder replaces target; other relative order preserved");
        Check(s.Groups.Single().Members.SequenceEqual(new[]{ids[3],ids[0]}),"target before source; no duplicate copies");
        Check(s.Items.Count==6 && paths.All(File.Exists) && s.Items.All(e=>File.Exists(s.CopyPath(e))),"copies reused and source files unchanged");
        Reject(()=>s.Merge(folder,ids[1]),"group nesting rejected atomically");
        s.Merge(ids[1],folder); Check(s.Groups.Single().Members.Count==3,"add top shortcut to group");
        s.ReorderMembers(folder,new[]{ids[1],ids[0],ids[3]}); Check(s.Groups.Single().Members[0]==ids[1],"member order saved");
        Reject(()=>s.ReorderMembers(folder,new[]{ids[0],ids[0],ids[3]}),"duplicate member order rejected atomically");
        s.RenameGroup(folder,"测试分组"); s.SetLayout(new(4,3,56));
        var reload=new ShortcutStore(s.Root); reload.Load(); Check(reload.Groups.Single().Name=="测试分组" && reload.Groups.Single().Members.SequenceEqual(s.Groups.Single().Members) && reload.TopOrder.SequenceEqual(s.TopOrder) && reload.Layout==s.Layout,"restart restores complete groups/order/layout");
        s.MoveOut(ids[1],0); s.MoveOut(ids[0],0); Check(s.Groups.Count==0 && s.TopOrder.Contains(ids[3]) && !s.TopOrder.Contains(folder),"move-out collapses singleton at folder position");
        Check(s.TopOrder.Count==6 && s.TopOrder.Distinct().Count()==6,"collapse preserves unique ownership and all items");
        folder=s.Merge(ids[0],ids[3]); s.Merge(ids[1],folder); var expected=s.TopOrder.SelectMany(id=>id==folder?s.Groups.Single().Members:new List<Guid>{id}).ToArray();
        s.Dissolve(folder); Check(s.TopOrder.SequenceEqual(expected) && s.Groups.Count==0,"dissolve inserts ordered members at folder position");
        folder=s.Merge(ids[0],ids[3]); s.RemoveLegacyFixture(s.Items.Single(e=>e.Id==ids[0])); Check(s.Groups.Count==0 && s.TopOrder.Contains(ids[3]) && !s.TopOrder.Contains(folder),"remove member collapses singleton");
        s.RemoveLegacyFixture(s.Items.Single(e=>e.Id==ids[3])); Check(s.Groups.Count==0 && s.Items.Count==4 && paths.All(File.Exists),"remove last member prunes folder; sources intact");
        var legacyFolder = Guid.NewGuid(); var legacyMember = s.Items[0].Id;
        var singletonDoc = new ShortcutDocument(1,s.Items,s.Layout,new(){new(legacyFolder,"旧单成员",new(){legacyMember})},s.TopOrder.Select(id=>id==legacyMember?legacyFolder:id).ToList());
        File.WriteAllText(Path.Combine(s.Root,"library.json"),JsonSerializer.Serialize(singletonDoc));
        reload=new ShortcutStore(s.Root); reload.Load(); Check(reload.Groups.Count==0 && reload.TopOrder.SequenceEqual(s.TopOrder),"legacy singleton migrates in place with order intact");
        var again=new ShortcutStore(s.Root); again.Load(); Check(again.Groups.Count==0 && again.TopOrder.SequenceEqual(s.TopOrder),"singleton migration persists across restart");
        var doc=Path.Combine(s.Root,"library.json"); File.WriteAllText(doc,JsonSerializer.Serialize(new {Version=1,Items=s.Items,Layout=s.Layout}));
        reload=new ShortcutStore(s.Root); reload.Load(); Check(reload.TopOrder.SequenceEqual(s.Items.Select(e=>e.Id)) && reload.Groups.Count==0,"old flat schema migrates without loss");
        var hitGrid = LayoutMetrics.Fit(new(), 1200, 1000) with { CellWidth = 100, CellHeight = 80 };
        double iconCenterY = hitGrid.TilePadding + hitGrid.Effective.IconSize / 2d;
        Check(GroupHover.IsCenter(hitGrid.CellWidth / 2, iconCenterY, hitGrid) &&
            !GroupHover.IsCenter(10, iconCenterY, hitGrid) && !GroupHover.IsCenter(hitGrid.CellWidth / 2, 0, hitGrid),
            "center and insertion edge zones separated");
        Console.WriteLine($"ALL {n} GROUP CHECKS PASSED");
    }
}
