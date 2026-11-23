using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
namespace Drawer.Core;

internal enum DrawerEdge { Top, Left, Right }
internal sealed record DrawerOption(string Id, DrawerEdge Edge, bool Enabled);
internal sealed record DrawerCatalogDocument(int Version, List<DrawerOption> Drawers);
internal sealed class DrawerCatalog
{
    public string Root { get; }
    public List<DrawerOption> Drawers { get; private set; } = new();
    public string Notice { get; private set; } = "";
    public DrawerCatalog(string? root=null) => Root=Path.GetFullPath(root??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Drawer"));
    private string Config=>Path.Combine(Root,"drawers.json");
    public static string Id(DrawerEdge edge)=>edge.ToString().ToLowerInvariant();
    public static string Label(DrawerEdge edge)=>edge switch {DrawerEdge.Top=>UiText.Get("EdgeTop"),DrawerEdge.Left=>UiText.Get("EdgeLeft"),_=>UiText.Get("EdgeRight")};
    public string DataRoot(DrawerEdge edge)
    {
        if(edge==DrawerEdge.Top)return Root;
        string parent=Path.Combine(Root,"Drawers");Directory.CreateDirectory(parent);Safe(parent);return Path.Combine(parent,Id(edge));
    }
    public bool Enabled(DrawerEdge edge)=>Drawers.Single(d=>d.Edge==edge).Enabled;
    private static void Safe(string path) { if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException(UiText.Get("ConfigReparse")); }
    private List<DrawerOption> Read(string path)
    {
        Safe(path); var doc=JsonSerializer.Deserialize<DrawerCatalogDocument>(File.ReadAllText(path));
        if(doc?.Version!=1 || doc.Drawers==null || doc.Drawers.Count!=3 || Enum.GetValues<DrawerEdge>().Any(e=>doc.Drawers.Count(d=>d!=null&&d.Edge==e&&d.Id==Id(e))!=1))throw new InvalidDataException(UiText.Get("InvalidDrawerConfig"));
        return doc.Drawers;
    }
    public void Load()
    {
        Directory.CreateDirectory(Root); Safe(Root);
        if(File.Exists(Config))
        {
            try { Drawers=Read(Config); return; }
            catch(Exception ex) when(ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                File.Copy(Config,Config+".corrupt-"+Guid.NewGuid().ToString("N"));
                List<DrawerOption> restored;
                try {restored=Read(Config+".bak"); Notice=UiText.Get("EdgeRestored");}
                catch(Exception backup) when(backup is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
                {restored=Defaults(true);Notice=UiText.Get("EdgeUnavailable");}
                Save(restored, keepBackup: true);
                Drawers = restored;
                return;
            }
        }
        if (File.Exists(Config + ".bak"))
        {
            try
            {
                var restored = Read(Config + ".bak");
                Save(restored, keepBackup: true);
                Drawers = restored;
                Notice = UiText.Get("EdgeMissing");
                return;
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException)
            {
                // No verified metadata to restore. Continue with legacy migration.
            }
        }
        bool legacy=File.Exists(Path.Combine(Root,"library.json")) || Directory.Exists(Path.Combine(Root,"Shortcuts"));
        if(legacy)
        {
            // Old top data stays at its original path, including GUID copy directories.
            // Commit the catalog only after an immutable pre-migration metadata backup exists.
            string backup=Path.Combine(Root,"MigrationBackups",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.GetDirectoryName(backup)!);Safe(Path.GetDirectoryName(backup)!);Directory.CreateDirectory(backup);
            foreach(string name in new[]{"library.json","library.json.bak"}) if(File.Exists(Path.Combine(Root,name))) {Safe(Path.Combine(Root,name));File.Copy(Path.Combine(Root,name),Path.Combine(backup,name));}
            File.WriteAllText(Path.Combine(backup,"migration.txt"),"Top shortcut copies remain in the original Shortcuts directory. No originals or managed copies were moved.");
            Notice=UiText.Get("LegacyTop");
        }
        var next=Defaults(legacy); Save(next); Drawers=next;
    }
    private static List<DrawerOption> Defaults(bool legacy)=>Enum.GetValues<DrawerEdge>().Select(e=>new DrawerOption(Id(e),e,!legacy||e==DrawerEdge.Top)).ToList();
    private void Save(List<DrawerOption> next, bool keepBackup = false)
    {
        Safe(Root); string temporary=Path.Combine(Root,"drawers-"+Guid.NewGuid().ToString("N")+".tmp");
        try { using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)) {stream.Write(JsonSerializer.SerializeToUtf8Bytes(new DrawerCatalogDocument(1,next),new JsonSerializerOptions{WriteIndented=true}));stream.Flush(true);}
            if(File.Exists(Config)) {Safe(Config);if(File.Exists(Config+".bak"))Safe(Config+".bak");File.Replace(temporary,Config,keepBackup ? null : Config+".bak");} else File.Move(temporary,Config);
        } finally {if(File.Exists(temporary))File.Delete(temporary);}
    }
    public void SetEnabled(DrawerEdge edge,bool enabled) {var next=Drawers.Select(d=>d.Edge==edge?d with{Enabled=enabled}:d).ToList();Save(next);Drawers=next;}
}

internal readonly record struct EdgeGeometry(PixelRect Monitor, PixelRect Work, PixelRect Drawer, PixelRect Trigger)
{
    public static EdgeGeometry Create(PixelRect monitor,PixelRect work,uint dpi,DrawerEdge edge,double width,double height)
    {
        double scale=dpi/96d; int Px(double n)=>(int)Math.Round(n*scale);
        int w=Math.Max(1,Math.Min(Px(width),work.Width)),h=Math.Max(1,Math.Min(Px(height),work.Height));
        int x=edge==DrawerEdge.Left?work.Left:edge==DrawerEdge.Right?work.Right-w:work.Left+(work.Width-w)/2;
        int y=edge==DrawerEdge.Top?work.Top:work.Top+(work.Height-h)/2;
        int guard=Px(32), depth=Math.Max(1,Px(DrawerSettings.TriggerHeight));
        int span=Math.Max(0,Math.Min(Px(DrawerSettings.TriggerWidth),(edge==DrawerEdge.Top?work.Width:work.Height)-guard*2));
        bool occupied=edge switch {DrawerEdge.Top=>work.Top>monitor.Top,DrawerEdge.Left=>work.Left>monitor.Left,_=>work.Right<monitor.Right};
        var trigger=occupied||span==0 ? new PixelRect(0,0,0,0) : edge switch {
            DrawerEdge.Top=>new(work.Left+(work.Width-span)/2,work.Top,work.Left+(work.Width+span)/2,work.Top+depth),
            DrawerEdge.Left=>new(work.Left,work.Top+(work.Height-span)/2,work.Left+depth,work.Top+(work.Height+span)/2),
            _=>new(work.Right-depth,work.Top+(work.Height-span)/2,work.Right,work.Top+(work.Height+span)/2)};
        return new(monitor,work,new(x,y,x+w,y+h),trigger);
    }
    public PixelRect Visible(DrawerEdge edge,double progress)
    {
        int w=Math.Max(1,edge==DrawerEdge.Top?Drawer.Width:(int)Math.Round(Drawer.Width*progress));
        int h=Math.Max(1,edge==DrawerEdge.Top?(int)Math.Round(Drawer.Height*progress):Drawer.Height);
        int left=edge==DrawerEdge.Right?Drawer.Right-w:Drawer.Left;
        return new(left,Drawer.Top,left+w,Drawer.Top+h);
    }
}
