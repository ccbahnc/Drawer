using Drawer.Core;
using System.Text.Json;
internal static class NavigationChecks
{
    public static void Run()
    {
        string context="general";int n=0;void Check(bool ok,string label){if(!ok){RegressionAssertions.Fail($"navigation: {context}: {label}");return;}n++;Console.WriteLine("PASS navigation: "+label);}
        void Reject(Action action,string label){bool failed=false;try{action();}catch(ArgumentException){failed=true;}Check(failed,label);}
        var screen=new PixelRect(0,0,2560,1600);var work=screen with{Bottom=1528};
        foreach(uint dpi in new uint[]{96,120,144})foreach(var edge in Enum.GetValues<DrawerEdge>())foreach(int count in new[]{1,2,3})
        {
            context=$"{dpi}/{edge}/{count}";var defs=Enumerable.Range(0,count).Select(i=>new DrawerDefinition(Guid.NewGuid(),"D"+i,edge,i,true,edge==DrawerEdge.Top?5:3,edge==DrawerEdge.Top?4:1,"L"+i,true)).ToList();
            var state=new DrawerCollection(2,new(),defs);var result=AutomaticLayout.Compute(state,screen,work,dpi,16,2);
            Check(result.Errors.Count==0&&result.Items.Count==count,$"{dpi}/{edge}/{count} valid arrangement");
            var regions=defs.Select(d=>result.Items[d.Id]).ToArray();int depth=(int)Math.Round(2*dpi/96d);
            Check(regions.All(p=>p.Geometry.Drawer.Left>=work.Left&&p.Geometry.Drawer.Right<=work.Right&&p.Geometry.Drawer.Top>=work.Top&&p.Geometry.Drawer.Bottom<=work.Bottom),"entire panels fit work area");
            Check(regions.All(p => p.Metrics.Effective.IconSize >= AutomaticLayout.MinIcon && p.ContentScale >= p.Limits.MinimumScale), "readable minima retained");
            Check(regions.All(p=>edge==DrawerEdge.Top?p.Geometry.Trigger.Left==p.Geometry.Drawer.Left&&p.Geometry.Trigger.Right==p.Geometry.Drawer.Right&&p.Geometry.Trigger.Height==depth:p.Geometry.Trigger.Top==p.Geometry.Drawer.Top&&p.Geometry.Trigger.Bottom==p.Geometry.Drawer.Bottom&&p.Geometry.Trigger.Width==depth),"trigger equals final projection and two DIP depth");
            bool gap=false;int max=edge==DrawerEdge.Top?work.Right:work.Bottom;
            for(int coordinate=0;coordinate<max;coordinate++)
            {
                int x=edge==DrawerEdge.Top?coordinate:edge==DrawerEdge.Left?0:work.Right-1,y=edge==DrawerEdge.Top?0:coordinate;
                int hits=regions.Count(p=>p.Geometry.Trigger.Contains(x,y));if(hits>1)throw new Exception("overlapping trigger");
                bool projected=regions.Any(p=>edge==DrawerEdge.Top?coordinate>=p.Geometry.Drawer.Left&&coordinate<p.Geometry.Drawer.Right:coordinate>=p.Geometry.Drawer.Top&&coordinate<p.Geometry.Drawer.Bottom);
                if((hits==1)!=projected)throw new Exception("trigger extends beyond projection");
                if(coordinate>64&&coordinate<max-64&&!projected)gap=true;
            }
            Check(gap,"physical gaps never trigger, rather than full-edge partition");
            var hidden=state with{Drawers=defs.Select(d=>d.Id==defs[0].Id?d with{Enabled=false}:d).ToList()};
            Check(!AutomaticLayout.Compute(hidden,screen,work,dpi).Items.ContainsKey(defs[0].Id),"disabled drawer has no cached trigger");
            var occupied=edge==DrawerEdge.Top?work with{Top=48}:edge==DrawerEdge.Left?work with{Left=48}:work with{Right=2512};
            Check(AutomaticLayout.Compute(state,screen,occupied,dpi).Items.Values.All(p=>p.Geometry.Trigger.Width==0),"taskbar occupied boundary does not trigger");
            var noNames=state with{Global=state.Global with{ShowNames=false}};var namesOff=AutomaticLayout.Compute(noNames,screen,work,dpi,16,2);
            Check(regions.Select(p=>(p.Geometry,p.Metrics.CellWidth,p.Metrics.CellHeight)).SequenceEqual(defs.Select(d=>namesOff.Items[d.Id]).Select(p=>(p.Geometry,p.Metrics.CellWidth,p.Metrics.CellHeight))),"name visibility preserves all geometry and slots");
            Check(regions.All(p => p.Metrics.Height + .001 >= p.Metrics.Rows * p.Metrics.CellHeight + 2 * p.Metrics.Padding + AutomaticLayout.HeaderChrome),
                "navigation height never consumes configured grid row");
            var group = new PixelRect(regions[0].Geometry.Drawer.Left, regions[0].Geometry.Drawer.Top,
                regions[^1].Geometry.Drawer.Right, regions[^1].Geometry.Drawer.Bottom);
            Check(IsCentered(group, work, edge), "the group is centered by its actual span");
            var original = regions[0];
            var resizeEdge = edge == DrawerEdge.Top ? ResizeEdge.Left : ResizeEdge.Bottom;
            var draggedScale = original.Limits.DragScale(original.ContentScale, original.MaximumScale, resizeEdge, 77, -77);
            var resized = state with { Drawers = defs.Select((d, i) => i == 0 ? d with { ContentScale = draggedScale } : d).ToList() };
            // Manual resizing pins neighbours to their actual scales, including an
            // already space-adapted edge. This is the same input used by ResizeFrame.
            var pinned = defs.Skip(1).ToDictionary(d => d.Id, d => result.Items[d.Id].ContentScale);
            var resizedLayout = AutomaticLayout.Compute(resized, screen, work, dpi, 16, 2,
                strictEdge: edge, fixedScales: pinned);
            Check(defs.Skip(1).Select(d => resizedLayout.Items[d.Id].Metrics).SequenceEqual(regions.Skip(1).Select(p => p.Metrics)),
                "resize preserves neighboring dimensions");
            double dpiScale = original.Limits.DpiScale;
            bool columnsFit = true;
            for (int step = 0; step <= 32; step++)
            {
                double ratio = original.Limits.MinimumScale + (original.MaximumScale - original.Limits.MinimumScale) * step / 32;
                var (width, height, cellWidth, cellHeight, padding) = original.Limits.GridPixels(ratio);
                columnsFit &= width <= original.Limits.Bounds.Width && height <= original.Limits.Bounds.Height &&
                    width - original.Limits.FrameWidth - 2 * padding - original.Metrics.Columns * cellWidth >= AutomaticLayout.PageGutter * dpiScale - .001 &&
                    height - original.Limits.FrameHeight - 2 * padding - original.Metrics.Rows * cellHeight >= AutomaticLayout.HeaderChrome * dpiScale - .001;
            }
            Check(columnsFit, "all physical widths leave room for every column");
        }
        context="general";var many=new DrawerCollection(2,new(),Enumerable.Range(0,8).Select(i=>new DrawerDefinition(Guid.NewGuid(),"T"+i,DrawerEdge.Top,i,true,8,12,"L"+i)).ToList());
        Check(AutomaticLayout.Compute(many,screen,work,144).Errors.ContainsKey(DrawerEdge.Top),"impossible edge rejected instead of overlapping or clipping");
        var simple=many with{Drawers=many.Drawers.Take(1).Select(d=>d with{Rows=2,Columns=2}).ToList()};
        Reject(()=>AutomaticLayout.ValidateChange(simple,many,screen,work,144,0,0),"proposal cannot replace last valid layout with impossible one");
        var a=simple.Drawers[0];var b=new DrawerDefinition(Guid.NewGuid(),"Other",DrawerEdge.Top,1,true,2,2,"Other",false);var c=new DrawerDefinition(Guid.NewGuid(),"Side",DrawerEdge.Left,0,true,2,1,"Side",false);
        var initial=simple with{Drawers=new(){a,b,c}};var moved=DrawerMove.Propose(initial,a.Id,DrawerEdge.Left,0);
        Check(initial.Drawers[0]==a&&moved.Drawers.Single(d=>d.Id==a.Id)==a with{Edge=DrawerEdge.Left,Order=0}&&moved.Drawers.Single(d=>d.Id==c.Id).Order==1&&moved.Drawers.Single(d=>d.Id==b.Id).Order==0,"cross-edge preview is pure and preserves all independent data");
        var reordered=DrawerMove.Propose(initial,a.Id,DrawerEdge.Top,1);
        Check(reordered.Drawers.Where(d=>d.Edge==DrawerEdge.Top).OrderBy(d=>d.Order).Select(d=>d.Id).SequenceEqual(new[]{b.Id,a.Id}),"same-edge reorder commits insertion order");
        Reject(()=>DrawerMove.Propose(initial with{Drawers=new(){a,c with{Name=a.Name}}},a.Id,DrawerEdge.Left,0),"conflicting destination name rejected before mutation");
        string project=AppContext.BaseDirectory;while(!File.Exists(Path.Combine(project,"Drawer.sln")))project=Directory.GetParent(project)!.FullName;
        string root=Path.Combine(project,"temp","nav16-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var old = new DrawerCollection(3, new(false, 64, 12, false), initial.Drawers);
        var legacy = JsonSerializer.SerializeToNode(old)!;
        var oldDrawers = legacy["Drawers"]!.AsArray();
        for (int i = 0; i < oldDrawers.Count; i++) oldDrawers[i]!["ShowNames"] = i == 1;
        string path = Path.Combine(root, "collection.json");
        File.WriteAllText(path, legacy.ToJsonString());
        byte[] bytes = File.ReadAllBytes(path);
        var catalog = new MultiDrawerCatalog(root);
        catalog.Load(new DrawerCatalog(root));
        Check(catalog.Value.Version == MultiDrawerCatalog.CurrentVersion && catalog.Value.Global.ShowNames && catalog.Value.Global.IconSize == 64 &&
            catalog.Value.Drawers.All(d => d.Animations == true) &&
            catalog.Value.Drawers.Select(d => d.Id).SequenceEqual(initial.Drawers.Select(d => d.Id)),
            "v3 migration resets global names on once and retains drawer data");
        using (var json = JsonDocument.Parse(File.ReadAllText(path)))
            Check(json.RootElement.GetProperty("Drawers").EnumerateArray().All(d => !d.TryGetProperty("ShowNames", out _)),
                "only the global names field is persisted");
        Check(Directory.GetFiles(Path.Combine(root, "UnifiedMigration"), "collection.json", SearchOption.AllDirectories)
            .Any(p => File.ReadAllBytes(p).SequenceEqual(bytes)), "byte-exact migration backup before atomic upgrade");
        catalog.SetShowNames(false);
        var reload = new MultiDrawerCatalog(root);
        reload.Load(new DrawerCatalog(root));
        Check(!reload.Value.Global.ShowNames && reload.Value.Drawers.All(d => d.Animations == true),
            "one global value survives restart and animations remain enabled");
        var arranged = AutomaticLayout.Compute(reload.Value, screen, work, 144);
        Check(arranged.Items.Values.All(drawer => !drawer.Metrics.Effective.ShowNames), "all boundaries use the same global option");
        Check(Directory.GetDirectories(Path.Combine(root, "UnifiedMigration")).Length == 1, "repeat startup never repeats global migration");
        catalog.BeforeCommit=value=>AutomaticLayout.ValidateChange(catalog.Value,value,screen,work,144,0,0);bytes=File.ReadAllBytes(path);
        Reject(()=>catalog.Commit(many),"geometry validation runs before atomic configuration commit");
        Check(File.ReadAllBytes(path).SequenceEqual(bytes)&&catalog.Value.Drawers.Count==3,"failed proposal preserves exact saved state");
        var legacyDrawer = JsonSerializer.SerializeToNode(a)!.AsObject();
        legacyDrawer["Size"] = JsonSerializer.SerializeToNode(new { Width = 430, Height = 320 });
        var withoutManualSize = legacyDrawer.Deserialize<DrawerDefinition>();
        Check(withoutManualSize == a && withoutManualSize?.ContentScale == null, "legacy independent dimensions fall back to the default scale");
        var scaled = a with { Rows = 3, Columns = 6, ContentScale = 1.23 };
        var six = simple with { Drawers = new() { scaled } };
        var two = six with { Drawers = new() { scaled with { Columns = 2 } } };
        var beforeEdit = AutomaticLayout.Compute(six, screen, work, 144).Items[a.Id];
        var afterEdit = AutomaticLayout.Compute(two, screen, work, 144).Items[a.Id];
        Check(beforeEdit.ContentScale == 1.23 && afterEdit.ContentScale == beforeEdit.ContentScale &&
            afterEdit.Metrics.IconSize == beforeEdit.Metrics.IconSize && afterEdit.Geometry.Drawer.Width < beforeEdit.Geometry.Drawer.Width,
            "column edits use the saved scale and reflow into the new width");
        var defaultSize = AutomaticLayout.Compute(six with { Drawers = new() { scaled with { ContentScale = null } } }, screen, work, 96).Items[a.Id];
        Check(defaultSize.ContentScale == 1.2 && defaultSize.Limits.MinimumScale == .85 && defaultSize.MaximumScale == 1.82 &&
            defaultSize.Limits.DragScale(defaultSize.ContentScale, defaultSize.MaximumScale, ResizeEdge.Right, -2000, 0) == .85 &&
            defaultSize.Limits.DragScale(defaultSize.ContentScale, defaultSize.MaximumScale, ResizeEdge.Right, 2000, 0) == 1.82,
            "new default and lower and upper resize limits use the previous scale baseline");
        Console.WriteLine($"{n} NAVIGATION CHECKS PASSED");
    }

    private static bool IsCentered(PixelRect rect, PixelRect lane, DrawerEdge edge) => edge == DrawerEdge.Top
        ? Math.Abs(rect.Left + rect.Right - lane.Left - lane.Right) <= 1
        : Math.Abs(rect.Top + rect.Bottom - lane.Top - lane.Bottom) <= 1;
}


