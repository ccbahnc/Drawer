using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
namespace Drawer.Core;

internal sealed record DrawerDeletion(int Version,Guid Source,string Storage,List<string> Copies,bool ReplaceContents=false);
internal sealed partial class MultiDrawerCatalog
{
    public string CleanupNotice {get;private set;}="";
    internal string TakeCleanupNotice()
    {
        string notice = CleanupNotice;
        CleanupNotice = "";
        return notice;
    }
    internal Action? BeforeCleanup {get;set;}
    private void WriteAtomic(string path,byte[] bytes)
    {
        Resolve(Path.GetRelativePath(Root,path));string pending=path+".tmp-"+Guid.NewGuid().ToString("N");
        try{using(var stream=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(bytes);stream.Flush(true);}if(File.Exists(path))File.Replace(pending,path,null);else File.Move(pending,path);}
        finally{if(File.Exists(pending))File.Delete(pending);}
    }
    public void Delete(Guid id)
    {
        var definition=Value.Drawers.Single(d=>d.Id==id);var store=OpenStore(id);
        // Resolve every surviving reference BEFORE committing. A corrupt survivor stops the operation safely.
        foreach (var d in Value.Drawers.Where(d => d.Id != id))
            _ = OpenStore(d.Id); // Load validates all managed paths in the active document.
        var owned=store.Items.ToList();string sourceBackup=Path.Combine(store.Root,"library.json.bak");
        if(File.Exists(sourceBackup)){Resolve(Path.GetRelativePath(Root,sourceBackup));var doc=JsonSerializer.Deserialize<ShortcutDocument>(File.ReadAllText(sourceBackup));if(doc==null||!doc.SupportedVersion||doc.Items==null)throw new IOException(UiText.Get("SourceBackupUnreadable"));owned.AddRange(doc.Items);}
        var task=new DrawerDeletion(1,id,definition.Storage,owned.Select(e=>Path.GetRelativePath(Root,store.CopyPath(e))).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        string directory=Resolve("DeletionTasks");Directory.CreateDirectory(directory);string journal=Path.Combine(directory,Guid.NewGuid().ToString("N")+".json");
        WriteAtomic(journal,JsonSerializer.SerializeToUtf8Bytes(task));
        try{Commit(Value with{Drawers=Value.Drawers.Where(d=>d.Id!=id).ToList()});}
        catch{File.Delete(journal);throw;}
        if (_historyPending == null) RetryDeletionCleanup();
    }
    public ShortcutStore DeleteItems(Guid id, ShortcutStore store, IReadOnlyList<Guid> ids)
    {
        var definition = Value.Drawers.Single(d => d.Id == id);
        if (store.Root != Resolve(definition.Storage)) throw new InvalidOperationException(UiText.Get("ContentChanged"));
        string storage = "Transfers/" + Guid.NewGuid().ToString("N") + "/remaining";
        var snapshot = new ShortcutStore(Resolve(storage), Root);
        string journal = Resolve("DeletionTasks/" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var copies = store.StageRemoval(ids, snapshot).Select(path => Path.GetRelativePath(Root, path)).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
            WriteAtomic(journal, JsonSerializer.SerializeToUtf8Bytes(new DrawerDeletion(1, id, definition.Storage, copies, true)));
            Commit(Value with { Drawers = Value.Drawers.Select(d => d.Id == id ? d with { Storage = storage } : d).ToList() });
            TrackHistoryStore(snapshot);
        }
        catch
        {
            foreach (string path in new[] { journal, Path.Combine(snapshot.Root, "library.json"), Path.Combine(snapshot.Root, "library.json.bak") })
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        if (_historyPending == null) RetryDeletionCleanup();
        return snapshot;
    }

    private HashSet<string> LiveCopies()
    {
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var d in Value.Drawers)
            paths.UnionWith(new ShortcutStore(Resolve(d.Storage), Root).ReadManagedReferences());
        paths.UnionWith(ArchivedLegacyReferences());
        return paths;
    }
    private bool IsManagedCopy(string relative,out string path)
    {
        path=Resolve(relative);
        return ShortcutStore.IsManagedCopyPath(path);
    }
    public void RetryDeletionCleanup()
    {
        try { ProcessDeletionTasks(); ProcessHistoryCleanup(); }
        catch (Exception ex) when (IsReadFailure(ex))
        { CleanupNotice = UiText.Get("CopyCleanupDeferred", ex.Message); }
    }
    private void ProcessDeletionTasks()
    {
        CleanupNotice="";string directory=Resolve("DeletionTasks");if(!Directory.Exists(directory))return;
        var pinned = PinnedHistoryCopies();
        HashSet<string>? live = null, archived = null;
        bool backupUpdated = false;
        foreach(string journal in Directory.GetFiles(directory,"*.json"))
        {
            try
            {
                Resolve(Path.GetRelativePath(Root,journal));var task=JsonSerializer.Deserialize<DrawerDeletion>(File.ReadAllText(journal))??throw new IOException(UiText.Get("DeletionTaskUnreadable"));
                if(task.Version!=1||task.Source==Guid.Empty||task.Copies==null)throw new IOException(UiText.Get("InvalidDeletionTask"));
                if (task.Copies.Select(Resolve).Any(pinned.Contains)) continue;
                if(Value.Drawers.Any(d=>d.Id==task.Source && (!task.ReplaceContents || d.Storage==task.Storage))){File.Delete(journal);continue;} // Manifest commit never happened, or recovery restored it.
                BeforeCleanup?.Invoke();
                if (live == null || BeforeCleanup != null)
                {
                    var current = LiveCopies();
                    var recovery = ArchivedCollections().SelectMany(value => value.Drawers).Select(d => Resolve(d.Storage))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (string path in recovery)
                        current.UnionWith(new ShortcutStore(path, Root).ReadManagedReferences());
                    live = current;
                    archived = recovery;
                }
                // Both recovery manifests must exclude the deleted drawer before any shortcut bytes are removed.
                if (!backupUpdated || BeforeCleanup != null)
                {
                    WriteAtomic(Config + ".bak", File.ReadAllBytes(Config));
                    backupUpdated = true;
                }
                string storage=Resolve(task.Storage);
                if(!archived!.Contains(storage) && !Value.Drawers.Any(d=>Resolve(d.Storage).Equals(storage,StringComparison.OrdinalIgnoreCase)))
                    foreach(string name in new[]{"library.json","library.json.bak"}){string file=Resolve(Path.GetRelativePath(Root,Path.Combine(storage,name)));if(File.Exists(file))File.Delete(file);}
                foreach(string relative in task.Copies)
                {
                    if(!IsManagedCopy(relative,out var path)||live.Contains(path))continue;
                    if(File.Exists(path))File.Delete(path);
                    string parent=Path.GetDirectoryName(path)!;if(Directory.Exists(parent)&&!Directory.EnumerateFileSystemEntries(parent).Any())Directory.Delete(parent);
                }
                File.Delete(journal);
            }
            catch(Exception ex) when(IsReadFailure(ex))
            {CleanupNotice=UiText.Get("CopyCleanupFailed", ex.Message);}
        }
    }
}
