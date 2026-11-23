using Drawer.Core;
using System.Text.Json.Nodes;
internal static class PreferenceChecks
{
    public static void Run()
    {
        int n=0;void Check(bool value,string name){if(!value)throw new Exception("preferences: "+name);n++;Console.WriteLine("PASS preferences: "+name);}
        string project=AppContext.BaseDirectory;while(!File.Exists(Path.Combine(project,"Drawer.sln")))project=Directory.GetParent(project)!.FullName;
        string root=Path.Combine(project,"temp","prefs12-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var catalog=new DrawerCatalog(root);catalog.Load();var stores=new Dictionary<DrawerEdge,ShortcutStore>();
        foreach(var edge in Enum.GetValues<DrawerEdge>()){var store=new ShortcutStore(catalog.DataRoot(edge));store.Load();store.SetLayout(new(2+(int)edge,3,edge==DrawerEdge.Top?48:24,edge!=DrawerEdge.Top));stores.Add(edge,store);}
        catalog.SetEnabled(DrawerEdge.Left,false);
        string sources=Path.Combine(root,"sources");Directory.CreateDirectory(sources);
        var files=Enumerable.Range(0,3).Select(i=>{string path=Path.Combine(sources,i+".url");File.WriteAllText(path,"[InternetShortcut]\nURL=https://example.com/"+i);return path;}).ToArray();
        foreach(var store in stores.Values){store.Import(files);store.Merge(store.TopOrder[0],store.TopOrder[1]);store.Reorder(store.TopOrder.AsEnumerable().Reverse());}
        var bytes=stores.ToDictionary(p=>p.Key,p=>File.ReadAllBytes(Path.Combine(p.Value.Root,"library.json")));
        var prefs=new DrawerPreferences(root);prefs.Load(catalog,stores);
        Check(prefs.Value.Global.IconSize==48&&!prefs.Value.Global.ShowNames,"top wins conflicting legacy appearance");
        Check(!prefs.Value.Edges.Single(p=>p.Edge==DrawerEdge.Left).Enabled&&prefs.Value.Edges.All(p=>p.Columns==2+(int)p.Edge&&p.Rows==3),"migration preserves enable and row/column identity");
        Check(Directory.GetFiles(Path.Combine(root,"SettingsMigration"),"*",SearchOption.AllDirectories).Length>=4,"migration metadata backed up before write");
        var selector=new LayoutSelection(6,1);selector.Preview(4,5);Check(selector.Rows==4&&selector.Columns==5&&selector.ConfirmedRows==6,"hover has separate confirmed state");
        selector.Cancel();Check(selector.Rows==6&&selector.Columns==1,"hover cancel restores baseline");
        selector.Confirm(7,12);selector.Preview(2,3);selector.Cancel();Check(selector.Rows==7&&selector.Columns==12,"arrow/key confirm becomes latest cancellation baseline");
        Check(!LayoutSelection.Valid(0,1)&&!LayoutSelection.Valid(21,1)&&!LayoutSelection.Valid(1,21)&&
            LayoutSelection.Valid(20,20)&&LayoutSelection.Valid(9,13),"input limits extend beyond the preview grid");
        prefs.SetEdge(DrawerEdge.Right,p=>p with{Rows=8,Columns=12});prefs.Commit(prefs.Value with{Global=new(false,64,10,false)});
        var reload=new DrawerPreferences(root);reload.Load(catalog,stores);Check(reload.Value.Global==new GlobalAppearance(false,64,10,false)&&reload.Value.Edges.Single(p=>p.Edge==DrawerEdge.Right).Columns==12,"restart restores global and direction settings");
        prefs.Commit(DrawerPreferences.Defaults());Check(prefs.Value.Edges.All(p=>p.Enabled)&&prefs.Value.Edges.Single(p=>p.Edge==DrawerEdge.Left).Rows==6&&prefs.Value.Global==new GlobalAppearance(),"defaults reset only settings");
        Check(stores.All(p=>bytes[p.Key].SequenceEqual(File.ReadAllBytes(Path.Combine(p.Value.Root,"library.json")))) ,"migration and resets leave all content documents byte-identical");
        byte[] config=File.ReadAllBytes(Path.Combine(root,"settings.json"));bool rejected=false;try{prefs.SetEdge(DrawerEdge.Top,p=>p with{Rows=9});}catch(ArgumentException){rejected=true;}
        Check(rejected&&config.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"settings.json"))),"invalid commit never damages settings");
        string themeRoot = Path.Combine(root, "theme");
        var themed = new MultiDrawerCatalog(themeRoot);
        var legacy = new DrawerCatalog(themeRoot);
        themed.Load(legacy);
        string manifest = Path.Combine(themeRoot, "collection.json");
        var oldJson = JsonNode.Parse(File.ReadAllText(manifest))!;
        oldJson["Version"] = 4;
        var oldGlobal = oldJson["Global"]!.AsObject();
        oldGlobal.Remove("ColorScheme");
        oldGlobal.Remove("Material");
        File.WriteAllText(manifest, oldJson.ToJsonString());
        themed.Load(legacy);
        Check(themed.Value.Global.ColorScheme == DrawerColorScheme.Light && themed.Value.Global.Material == DrawerMaterial.Solid,
            "missing style in a version 4 collection uses light and solid");
        oldGlobal["Style"] = "Light";
        File.WriteAllText(manifest, oldJson.ToJsonString());
        themed.Load(legacy);
        Check(themed.Value.Global.ColorScheme == DrawerColorScheme.Light && themed.Value.Global.Material == DrawerMaterial.Solid,
            "old light style migrates to light and solid");
        oldGlobal["Style"] = "Dark";
        string oldManifest = oldJson.ToJsonString();
        File.WriteAllText(manifest, oldManifest);
        themed.Load(legacy);
        Check(themed.Value.Version == 5 && themed.Value.Global.ColorScheme == DrawerColorScheme.Dark &&
            themed.Value.Global.Material == DrawerMaterial.Solid &&
            Directory.GetFiles(Path.Combine(themeRoot, "UnifiedMigration"), "collection.json", SearchOption.AllDirectories)
                .Any(path => File.ReadAllText(path) == oldManifest), "old dark style migrates to dark and solid with a backup");
        var top = themed.Value.Drawers[0];
        var library = themed.OpenStore(top.Id);
        library.Import(files);
        library.Merge(library.TopOrder[0], library.TopOrder[1]);
        string libraryPath = Path.Combine(library.Root, "library.json");
        byte[] content = File.ReadAllBytes(libraryPath);
        var display = new PixelRect(0, 0, 1920, 1080);
        var original = themed.Value;
        long initialRevision = themed.Revision;
        themed.SetMaterial(DrawerMaterial.Acrylic);
        Check(themed.Value.Global.ColorScheme == DrawerColorScheme.Dark, "changing material preserves the current scheme");
        themed.SetColorScheme(DrawerColorScheme.Light);
        Check(themed.Value.Global.Material == DrawerMaterial.Acrylic, "changing the scheme preserves the current material");
        themed.Commit(original);
        Check(themed.Revision == initialRevision + 3 && themed.Value.Global == original.Global,
            "restoring the original configuration still advances the diagnostic revision");
        themed.SetMaterial(DrawerMaterial.Acrylic);
        themed.SetColorScheme(DrawerColorScheme.Light);
        var reloadedTheme = new MultiDrawerCatalog(themeRoot);
        int migrations = Directory.GetDirectories(Path.Combine(themeRoot, "UnifiedMigration")).Length;
        reloadedTheme.Load(legacy);
        Check(reloadedTheme.Value.Global.ColorScheme == DrawerColorScheme.Light &&
            reloadedTheme.Value.Global.Material == DrawerMaterial.Acrylic &&
            Directory.GetDirectories(Path.Combine(themeRoot, "UnifiedMigration")).Length == migrations &&
            !JsonNode.Parse(File.ReadAllText(manifest))!["Global"]!.AsObject().ContainsKey("Style"),
            "restart restores light acrylic without repeating the migration");
        Check(original.Drawers.SequenceEqual(reloadedTheme.Value.Drawers) &&
            AutomaticLayout.Compute(original, display, display, 144).Items
                .SequenceEqual(AutomaticLayout.Compute(reloadedTheme.Value, display, display, 144).Items) &&
            content.SequenceEqual(File.ReadAllBytes(libraryPath)), "style alone preserves all drawer geometry and library bytes");
        byte[] savedTheme = File.ReadAllBytes(manifest);
        reloadedTheme.BeforeCommit = _ => throw new IOException("simulated style write failure");
        bool styleFailed = false;
        long savedRevision = reloadedTheme.Revision;
        try { reloadedTheme.SetColorScheme(DrawerColorScheme.Dark); }
        catch (IOException) { styleFailed = true; }
        Check(styleFailed && reloadedTheme.Value.Global.ColorScheme == DrawerColorScheme.Light &&
            reloadedTheme.Value.Global.Material == DrawerMaterial.Acrylic &&
            reloadedTheme.Revision == savedRevision &&
            savedTheme.SequenceEqual(File.ReadAllBytes(manifest)), "failed style save leaves the visible selection and config unchanged");
        foreach(int rows in new[]{1,6,8})foreach(int cols in new[]{1,6,12})foreach(int count in new[]{0,1,6,96,100})
        {
            var a=LayoutMetrics.Fit(new(cols,rows,38,true),1920,1080,count);var b=LayoutMetrics.Fit(new(cols,rows,38,false),1920,1080,100);
            Check(a.Width==b.Width&&a.Height==b.Height&&a.CellWidth==b.CellWidth&&a.CellHeight==b.CellHeight&&a.Rows==rows&&a.Columns==cols,"count and labels never alter fixed geometry");
        }
        Console.WriteLine($"ALL {n} PREFERENCE CHECKS PASSED");
    }
}
