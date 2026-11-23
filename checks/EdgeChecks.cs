using Drawer.Core;
using System.Text.Json;
internal static class EdgeChecks
{
 public static void Run()
 {
  int n=0;void Check(bool yes,string label){if(!yes)throw new Exception("FAIL edges: "+label);n++;Console.WriteLine("PASS edges: "+label);}
  string project=AppContext.BaseDirectory;while(!File.Exists(Path.Combine(project,"Drawer.sln")))project=Directory.GetParent(project)!.FullName;
  string root=Path.Combine(project,"temp","edge-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  var fresh=new DrawerCatalog(Path.Combine(root,"fresh"));fresh.Load();Check(fresh.Drawers.All(d=>d.Enabled)&&fresh.Drawers.Select(d=>d.Id).Distinct().Count()==3,"fresh profile enables three stable empty drawers");
  var legacyRoot=Path.Combine(root,"legacy");var old=new ShortcutStore(legacyRoot);old.Load();
  var sources=Enumerable.Range(0,4).Select(i=>{var p=Path.Combine(root,$"{i}.url");File.WriteAllText(p,$"[InternetShortcut]\nURL=https://example.com/{i}");return p;}).ToArray();
  old.Import(sources);var original=old.Items.Select(i=>i.Id).ToArray();old.Merge(original[0],original[1]);old.SetLayout(new(4,3,56));
  var json=File.ReadAllText(Path.Combine(legacyRoot,"library.json"));var ids=old.TopOrder.ToArray();
  var catalog=new DrawerCatalog(legacyRoot);catalog.Load();Check(catalog.Enabled(DrawerEdge.Top)&&!catalog.Enabled(DrawerEdge.Left)&&!catalog.Enabled(DrawerEdge.Right),"legacy defaults preserve top and disable new sides");
  Check(File.ReadAllText(Directory.GetFiles(Path.Combine(legacyRoot,"MigrationBackups"),"library.json",SearchOption.AllDirectories).Single())==json,"migration backs up exact old configuration before catalog");
  var top=new ShortcutStore(catalog.DataRoot(DrawerEdge.Top));top.Load();Check(top.TopOrder.SequenceEqual(ids)&&top.Groups.Single().Members.SequenceEqual(old.Groups.Single().Members)&&top.Layout==old.Layout,"legacy top order groups and layout unchanged");
  Check(top.Items.All(e=>File.Exists(top.CopyPath(e)))&&sources.All(File.Exists),"migration keeps managed copies and originals");
  var left=new ShortcutStore(catalog.DataRoot(DrawerEdge.Left));left.Load();var right=new ShortcutStore(catalog.DataRoot(DrawerEdge.Right));right.Load();Check(left.Items.Count==0&&right.Items.Count==0,"new sides empty");
  left.Import(sources);left.Reorder(left.Items.Select(e=>e.Id).Reverse());left.Merge(left.TopOrder[0],left.TopOrder[1]);left.SetLayout(new(3,4,48));right.Import(new[]{sources[0]});right.SetLayout(new(2,2,24));
  Check(top.TopOrder.SequenceEqual(ids)&&right.Items.Count==1&&left.Items.Count==4&&right.Groups.Count==0,"drawer content group and order isolated");
  var leftAgain=new ShortcutStore(left.Root);leftAgain.Load();Check(leftAgain.TopOrder.SequenceEqual(left.TopOrder)&&leftAgain.Layout==left.Layout&&leftAgain.Groups.Count==1,"side order group and layout restore");
  foreach(var e in Enum.GetValues<DrawerEdge>())catalog.SetEnabled(e,false);
  var again=new DrawerCatalog(legacyRoot);again.Load();Check(again.Drawers.All(d=>!d.Enabled)&&leftAgain.Items.Count==4,"all disabled persists without deleting content");again.SetEnabled(DrawerEdge.Left,true);Check(again.Enabled(DrawerEdge.Left),"disabled edge can be restored");
  File.WriteAllText(Path.Combine(legacyRoot,"drawers.json"),"broken");var repaired=new DrawerCatalog(legacyRoot);repaired.Load();Check(Directory.GetFiles(legacyRoot,"drawers.json.corrupt-*").Length==1&&repaired.Drawers.Count==3,"damaged edge configuration preserved and recovered");
  foreach(uint dpi in new uint[]{96,120,144}) foreach(var work in new[]{new PixelRect(0,0,1920,1040),new PixelRect(64,0,1920,1080),new PixelRect(0,48,1920,1080),new PixelRect(0,0,1856,1080)})
  {
   var geometries=Enum.GetValues<DrawerEdge>().Select(e=>EdgeGeometry.Create(new(0,0,1920,1080),work,dpi,e,680,545)).ToArray();
   foreach(var edge in Enum.GetValues<DrawerEdge>()) {var g=geometries[(int)edge];Check(g.Drawer.Left>=work.Left&&g.Drawer.Top>=work.Top&&g.Drawer.Right<=work.Right&&g.Drawer.Bottom<=work.Bottom,$"{dpi}/{work}/{edge} window stays within work area");var v=g.Visible(edge,.25);Check(v.Left>=g.Drawer.Left&&v.Right<=g.Drawer.Right&&v.Top>=g.Drawer.Top&&v.Bottom<=g.Drawer.Bottom,$"{dpi}/{edge} reveal grows inward");}
   for(int x=0;x<1920;x+=2)for(int y=0;y<1080;y+=2)if(geometries.Count(g=>g.Trigger.Contains(x,y))>1)throw new Exception("overlapping triggers");
   Check(true,$"{dpi}/{work} corners never trigger two edges");
   Check((work.Left==0||geometries[1].Trigger.Width==0)&&(work.Top==0||geometries[0].Trigger.Height==0)&&(work.Right==1920||geometries[2].Trigger.Width==0),"taskbar-occupied edge has no hover trigger");
  }
  var interaction=new DrawerInteraction();interaction.KeepOpen();for(int i=0;i<100;i++)interaction.Update(i*10,.01,true,true,true);for(int i=0;i<100;i++)interaction.CloseStep(1000+i*10,.01);Check(interaction.Position==0&&!interaction.TargetOpen,"switch closes old animation fully before new edge can open");
  Console.WriteLine($"ALL {n} EDGE CHECKS PASSED");
 }
}
