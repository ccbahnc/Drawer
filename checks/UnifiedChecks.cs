using Drawer.Core;
using System.Text.Json;
internal static class UnifiedChecks
{
    public static void Run()
    {
        int count=0;void Check(bool ok,string title){if(!ok)throw new Exception("unified: "+title);count++;Console.WriteLine("PASS unified: "+title);}
        string project=AppContext.BaseDirectory;while(!File.Exists(Path.Combine(project,"Drawer.sln")))project=Directory.GetParent(project)!.FullName;
        string root=Path.Combine(project,"temp","unified16-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var catalog=new MultiDrawerCatalog(root);catalog.Load(new DrawerCatalog(root));string manifest=Path.Combine(root,"collection.json");var saved=File.ReadAllBytes(manifest);
        var creation=new DrawerCreation(catalog.Draft(DrawerEdge.Left));
        var preview=creation.Propose(catalog.Value,3,2);
        Check(!creation.Confirmed&&catalog.Value.Drawers.Count==3&&File.ReadAllBytes(manifest).SequenceEqual(saved)&&!Directory.Exists(catalog.Resolve(creation.Definition.Storage)),"new preview allocates one identity without directory or config writes");
        Check(preview.Drawers.Last().Id==creation.Definition.Id&&preview.Drawers.Last().Rows==3&&preview.Drawers.Last().Columns==2,"preview includes draft at chosen boundary end");
        catalog.Commit(preview);creation.Accept(3,2);var edit=creation.Propose(catalog.Value,4,1);catalog.Commit(edit);creation.Accept(4,1);
        Check(catalog.Value.Drawers.Count==4&&catalog.Value.Drawers.Count(d=>d.Id==creation.Definition.Id)==1&&creation.Definition.Rows==4,"first arrow/key confirmation creates once, later edits update same ID");
        saved=File.ReadAllBytes(manifest);var hover=creation.Propose(catalog.Value,2,4);
        Check(File.ReadAllBytes(manifest).SequenceEqual(saved)&&creation.Definition.Rows==4&&hover.Drawers.Single(d=>d.Id==creation.Definition.Id).Rows==2,"hover after confirmation never replaces cancellation baseline");
        var insertion=new DrawerInsertion();var work=new PixelRect(0,0,1920,1040);var source=catalog.Value.Drawers.First(d=>d.Edge==DrawerEdge.Top);
        var second=catalog.Add(DrawerEdge.Top);var third=catalog.Add(DrawerEdge.Top);
        var baseline = AutomaticLayout.Compute(catalog.Value, work, work, 96);
        int previous=-1,changes=0;foreach(int x in Enumerable.Range(0,1920)){int slot=insertion.Find(catalog.Value,source.Id,DrawerEdge.Top,x,baseline,1);if(slot!=previous){changes++;previous=slot;}}
        Check(changes==3,"stable insertion crosses each logical slot once independently of visual motion");
        insertion.Clear();
        var neighbor = baseline.Items[second].Geometry.Drawer;
        int center = (neighbor.Left + neighbor.Right) / 2;
        int a = insertion.Find(catalog.Value, source.Id, DrawerEdge.Top, center - 2, baseline, 1);
        int b = insertion.Find(catalog.Value, source.Id, DrawerEdge.Top, center + 2, baseline, 1);
        int c = insertion.Find(catalog.Value, source.Id, DrawerEdge.Top, center - 3, baseline, 1);
        Check(a==b&&b==c,"hysteresis prevents jitter at a logical insertion boundary");
        var store=catalog.OpenStore(source.Id);string originals=Path.Combine(root,"originals");Directory.CreateDirectory(originals);
        string targetFile=Path.Combine(originals,"original-target.txt");File.WriteAllText(targetFile,"original target stays unchanged");
        var files=Enumerable.Range(0,4).Select(i=>{string file=Path.Combine(originals,i+".url");File.WriteAllText(file,"[InternetShortcut]\nURL="+(i==3?"file:///"+targetFile.Replace('\\','/')+"#3":"https://example.com/"+i));return file;}).ToArray();
        store.Import(files);store.Merge(store.TopOrder[0],store.TopOrder[1]);var paths=store.Items.Select(store.CopyPath).ToArray();
        var recipient=catalog.Value.Drawers.Single(d=>d.Id==second);var target=catalog.OpenStore(second);
        ShortcutEntry Ref(int i)=>store.Items[i] with{ManagedCopy=Path.GetRelativePath(root,paths[i])};
        File.WriteAllText(Path.Combine(target.Root,"library.json"),JsonSerializer.Serialize(new ShortcutDocument(1,new(){Ref(0)},TopOrder:new(){Ref(0).Id})));
        File.WriteAllText(Path.Combine(target.Root,"library.json.bak"),JsonSerializer.Serialize(new ShortcutDocument(1,new(){Ref(1)},TopOrder:new(){Ref(1).Id})));
        saved=File.ReadAllBytes(manifest);catalog.BeforeCommit=_=>throw new IOException("injected manifest failure");bool failed=false;
        try{catalog.Delete(source.Id);}catch(IOException){failed=true;}catalog.BeforeCommit=null;
        Check(failed&&File.ReadAllBytes(manifest).SequenceEqual(saved)&&paths.All(File.Exists)&&catalog.Value.Drawers.Any(d=>d.Id==source.Id),"failed deletion commit retains original records, order, groups and all files");
        catalog.BeforeCleanup=()=>throw new IOException("interrupted after manifest commit");catalog.Delete(source.Id);
        Check(!catalog.Value.Drawers.Any(d=>d.Id==source.Id)&&paths.All(File.Exists)&&Directory.GetFiles(Path.Combine(root,"DeletionTasks"),"*.json").Length==1&&!string.IsNullOrEmpty(catalog.CleanupNotice),"post-commit cleanup interruption retains bytes and durable retry task");
        var reload=new MultiDrawerCatalog(root);reload.Load(new DrawerCatalog(root));
        Check(File.Exists(paths[0])&&File.Exists(paths[1])&&!File.Exists(paths[2]),"restart cleanup protects live and recovery references, removes exclusive managed copy");
        Check(!File.Exists(Path.Combine(store.Root,"library.json"))&&!File.Exists(Path.Combine(store.Root,"library.json.bak"))&&Directory.GetFiles(Path.Combine(root,"DeletionTasks"),"*.json").Length==0,"deleted drawer records and completed cleanup task disappear");
        Check(files.All(File.Exists)&&files.Select((f,i)=>File.ReadAllText(f).EndsWith(i.ToString())).All(x=>x),"original shortcut bytes remain untouched");
        Check(File.ReadAllText(targetFile)=="original target stays unchanged"&&!File.Exists(paths[3]),"shortcut target file remains intact when its managed shortcut copy is deleted");
        File.WriteAllText(manifest,"broken");var recover=new MultiDrawerCatalog(root);recover.Load(new DrawerCatalog(root));
        Check(!recover.Value.Drawers.Any(d=>d.Id==source.Id)&&recover.OpenStore(second).Items.Count==1&&File.Exists(paths[0]),"recovery manifest cannot resurrect deleted records pointing to removed files");
        recover.Delete(second);Check(!File.Exists(paths[0])&&!File.Exists(paths[1]),"deleting final owner clears copies referenced by current and backup records");
        foreach(var d in recover.Value.Drawers.ToArray())recover.Delete(d.Id);
        reload=new MultiDrawerCatalog(root);reload.Load(new DrawerCatalog(root));Check(reload.Value.Drawers.Count==0,"last drawer can be deleted and empty collection survives restart");
        var newId=reload.Add(DrawerEdge.Right);Check(reload.Value.Drawers.Single().Id==newId,"empty collection still supports new drawers");
        // A managed-reference field cannot turn an arbitrary source path into a deletable application copy.
        var externalLike=Path.Combine(root,"keep-original.url");File.WriteAllText(externalLike,"[InternetShortcut]\nURL=https://example.com/keep");var unusual=reload.OpenStore(newId);
        var reference=Ref(0) with{ManagedCopy="keep-original.url",FileName="keep-original.url"};
        File.WriteAllText(Path.Combine(unusual.Root,"library.json"),JsonSerializer.Serialize(new ShortcutDocument(1,new(){reference},TopOrder:new(){reference.Id})));
        reload.Delete(newId);Check(File.Exists(externalLike),"cleanup only deletes Shortcuts/GUID/file shapes, never arbitrary referenced originals");
        var animation=new DrawerInteraction();animation.KeepOpen();animation.Update(0,.016,true,true,true);
        Check(animation.Position>0&&animation.Position<1,"opening always animates instead of honoring legacy disable flags");
        string corruptRoot=Path.Combine(root,"bad-backup");var bad=new MultiDrawerCatalog(corruptRoot);bad.Load(new DrawerCatalog(corruptRoot));
        var victim=bad.Value.Drawers.First();var survivor=bad.Value.Drawers[1];var victimStore=bad.OpenStore(victim.Id);victimStore.Import(files.Take(1));string victimCopy=victimStore.CopyPath(victimStore.Items.Single());
        var survivorStore=bad.OpenStore(survivor.Id);string malformed=Path.Combine(survivorStore.Root,"library.json.bak");File.WriteAllText(malformed,"{\"Version\":1,\"Items\":null}");
        bad.Delete(victim.Id);Check(!bad.Value.Drawers.Any(d=>d.Id==victim.Id)&&File.Exists(victimCopy)&&bad.CleanupNotice.Contains("失败"),"invalid survivor backup defers cleanup with a visible failure notice instead of throwing after commit");
        File.WriteAllText(malformed,JsonSerializer.Serialize(new ShortcutDocument(1,new())));var repaired=new MultiDrawerCatalog(corruptRoot);repaired.Load(new DrawerCatalog(corruptRoot));
        Check(!File.Exists(victimCopy)&&string.IsNullOrEmpty(repaired.CleanupNotice),"repairing a survivor backup allows durable cleanup to resume");
        Console.WriteLine($"ALL {count} UNIFIED CHECKS PASSED");
    }
}
