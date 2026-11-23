using Drawer.Core;
using System.Text.Json.Nodes;

internal static class FullScreenRevealChecks
{
    public static void Run()
    {
        int count = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new Exception("fullscreen reveal: " + label);
            count++; Console.WriteLine("PASS fullscreen reveal: " + label);
        }
        Check(!new GlobalAppearance().AllowInFullScreen, "default is disabled");
        for (int bits = 0; bits < 32; bits++)
        {
            bool max=(bits&1)!=0, full=(bits&2)!=0, desktop=(bits&4)!=0, own=(bits&8)!=0, visible=(bits&16)!=0;
            var original=DesktopPolicy.Evaluate(max,full,desktop,own,visible);
            Check(original==DesktopPolicy.Evaluate(max,full,desktop,own,visible,false),$"off preserves original reason for {bits}");
            var enabled=DesktopPolicy.Evaluate(max,full,desktop,own,visible,true);
            Check(DesktopPolicy.Allowed(enabled)==(max||full||desktop||(own&&visible)), $"enabled policy combination {bits}");
            if(!max&&!full) Check(enabled==original,$"restored application hit behavior remains unchanged for {bits}");
        }
        string root=Path.Combine(Path.GetTempPath(),"Drawer-fullscreen-check-"+Guid.NewGuid().ToString("N"));
        var catalog=new MultiDrawerCatalog(root); catalog.Load(new DrawerCatalog(root));
        catalog.SetLanguage(DrawerLanguage.English);
        var before=catalog.Value; long revision=catalog.Revision;
        catalog.SetAllowInFullScreen(true);
        Check(catalog.Value.Global.AllowInFullScreen && catalog.Revision==revision+1,"change commits once");
        Check(catalog.Value.Drawers.SequenceEqual(before.Drawers) && catalog.Value.Global with {AllowInFullScreen=false}==before.Global,
            "contents geometry and other global settings are unchanged");
        Check(!catalog.CanUndo&&!catalog.CanRedo,"global setting creates no content history");
        catalog.SetAllowInFullScreen(true);
        Check(catalog.Revision==revision+1,"unchanged value creates no redundant commit");
        var reload=new MultiDrawerCatalog(root);reload.Load(new DrawerCatalog(root));
        Check(reload.Value.Global.AllowInFullScreen,"enabled value survives restart");
        string config=Path.Combine(root,"collection.json");string saved=File.ReadAllText(config);
        reload.BeforeCommit=_=>throw new IOException("isolated simulated save failure");
        bool failed=false;try{reload.SetAllowInFullScreen(false);}catch(IOException){failed=true;}
        Check(failed&&reload.Value.Global.AllowInFullScreen&&File.ReadAllText(config)==saved,"failed save changes neither runtime nor disk");
        reload.BeforeCommit=null; long start=reload.Revision;
        reload.SetAllowInFullScreen(false);reload.SetAllowInFullScreen(true);
        Check(reload.Revision==start+2,"toggle away and back changes monotonic diagnostic revision");
        var json=JsonNode.Parse(File.ReadAllText(config))!;json["Global"]!.AsObject().Remove("AllowInFullScreen");File.WriteAllText(config,json.ToJsonString());
        var legacy=new MultiDrawerCatalog(root);legacy.Load(new DrawerCatalog(root));
        Check(!legacy.Value.Global.AllowInFullScreen&&legacy.Value.Global.Language==DrawerLanguage.English,"old configuration defaults off without resetting other preferences");
        var interaction=new DrawerInteraction();double now=0;
        string gate=DesktopPolicy.Evaluate(true,true,false,false,false,true);
        for(int i=0;i<100;i++){now+=10;interaction.Update(now,.01,DesktopPolicy.Allowed(gate),false,false);}
        Check(interaction.Position==0,"allowing fullscreen alone does not open without an edge trigger");
        for(int i=0;i<19;i++){now+=10;interaction.Update(now,.01,true,true,false);}
        Check(!interaction.TargetOpen,"original edge dwell remains in effect");
        for(int i=0;i<80;i++){now+=10;interaction.Update(now,.01,true,true,true);}
        Check(interaction.Position==1,"original interaction opens under enabled fullscreen policy");
        now+=10;interaction.Update(now,.01,DesktopPolicy.Allowed(DesktopPolicy.Evaluate(true,true,false,true,true,false)),true,true);
        Check(interaction.Position==0,"disabled gate restores suppression when no interaction hold applies");
        Console.WriteLine($"ALL {count} FULLSCREEN REVEAL CHECKS PASSED; fixtures={root}");
    }
}
