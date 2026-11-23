using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
namespace Drawer.Core;
internal sealed record DrawerDefinition(Guid Id,string Name,DrawerEdge Edge,int Order,bool Enabled,int Rows,int Columns,string Storage,bool? Animations=null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] double? ContentScale=null);
internal sealed record DrawerCollection(int Version,GlobalAppearance Global,List<DrawerDefinition> Drawers);
internal sealed partial class MultiDrawerCatalog
{
    internal const int CurrentVersion = 5;
    public string Root{get;}
    public Action<DrawerCollection>? BeforeCommit{get;set;}
    internal Action? BeforeRecoveryPublish { get; set; }
    public string RecoveryNotice { get; private set; } = "";
    private string Config=>Path.Combine(Root,"collection.json");
    public DrawerCollection Value{get;private set;}=new(1,new(),new());
    private long _revision;
    internal long Revision => System.Threading.Interlocked.Read(ref _revision);
    public MultiDrawerCatalog(string root)=>Root=Path.GetFullPath(root);
    private string ResolveLocation(string relative)
    {
        if(Path.IsPathRooted(relative))throw new InvalidDataException(UiText.Get("AbsoluteDataPath"));
        string path=Path.GetFullPath(Path.Combine(Root,relative));
        if(path!=Root&&!path.StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException(UiText.Get("DataPathOutsideRoot"));
        return path;
    }
    public string Resolve(string relative)
    {
        string path = ResolveLocation(relative);
        for(string? p=path;p!=null;p=Path.GetDirectoryName(p)){if((Directory.Exists(p)||File.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException(UiText.Get("LinkedDataPath"));if(p.Equals(Root,StringComparison.OrdinalIgnoreCase))break;}
        return path;
    }
    public void Load(DrawerCatalog legacy)
    {
        var request = InstallationLanguage.Read(this);
        LoadCore(legacy, request);
        if (request is { } selection && Value.Global.InstallationLanguageRevision != selection.Revision)
            Commit(Value with { Global = Value.Global with
            {
                Language = selection.Language,
                InstallationLanguageRevision = selection.Revision
            } });
        UiText.Use(Value.Global.Language);
    }

    private void LoadCore(DrawerCatalog legacy, InstallationLanguage? request)
    {
        Directory.CreateDirectory(Root);Resolve(".");
        RecoveryNotice = "";
        bool primary = File.Exists(Config);
        bool backupExists = File.Exists(Config + ".bak");
        DrawerCollection? read = null;
        if (primary)
        {
            try { read = Read(Config); }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                Resolve("collection.json");
                File.Copy(Config, Config + ".corrupt-" + Guid.NewGuid().ToString("N"));
            }
        }
        if (read == null && backupExists)
        {
            try
            {
                read = Read(Config + ".bak");
                ValidateRecoveryReferences(read);
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                throw new InvalidDataException(UiText.Get("CollectionUnrecoverable"), ex);
            }
            UiText.Use(request is { } selection && read.Global.InstallationLanguageRevision != selection.Revision
                ? selection.Language : read.Global.Language);
            Publish(Upgrade(read), recovering: true);
            RecoveryNotice = primary ? UiText.Get("CollectionRestored") : UiText.Get("CollectionMissing");
            RetryDeletionCleanup();
            ReclaimTransferSnapshots();
            return;
        }
        if (read != null)
        {
            UiText.Use(request is { } selection && read.Global.InstallationLanguageRevision != selection.Revision
                ? selection.Language : read.Global.Language);
            if (read.Version < CurrentVersion)
            {
                string attributesBackup = Resolve("UnifiedMigration/" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(attributesBackup);
                File.Copy(Config, Path.Combine(attributesBackup, "collection.json"));
                if (File.Exists(Config + ".bak")) File.Copy(Config + ".bak", Path.Combine(attributesBackup, "collection.json.bak"));
                Commit(Upgrade(read));
            }
            else Value = read;
            RetryDeletionCleanup();
            ReclaimTransferSnapshots();
            return;
        }
        if (primary || Directory.Exists(Resolve("Transfers")) || Directory.Exists(Resolve("Libraries")))
            throw new InvalidDataException(UiText.Get("CollectionMigrationStopped"));
        string backup=Resolve("CollectionMigration/"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(backup);
        foreach(string file in new[]{"settings.json","settings.json.bak","drawers.json","library.json","library.json.bak","Drawers/left/library.json","Drawers/left/library.json.bak","Drawers/right/library.json","Drawers/right/library.json.bak"})
            if(File.Exists(Resolve(file)))File.Copy(Resolve(file),Path.Combine(backup,file.Replace('/','_')));
        legacy.Load();var stores=new Dictionary<DrawerEdge,ShortcutStore>();
        foreach(var edge in Enum.GetValues<DrawerEdge>()){var store=new ShortcutStore(legacy.DataRoot(edge),Root);store.Load();if(edge!=DrawerEdge.Top&&!File.Exists(Path.Combine(store.Root,"library.json")))store.SetLayout(new(1,6,38));stores.Add(edge,store);}
        var prefs=new DrawerPreferences(Root);prefs.Load(legacy,stores);
        var global = request is { } initial ? prefs.Value.Global with
            { Language = initial.Language, InstallationLanguageRevision = initial.Revision } : prefs.Value.Global;
        UiText.Use(global.Language);
        Commit(new(1,global,prefs.Value.Edges.Select(p=>new DrawerDefinition(LegacyId(p.Edge),
            UiText.Get("DefaultDrawerName", DrawerCatalog.Label(p.Edge), 1),p.Edge,0,p.Enabled,p.Rows,p.Columns,
            p.Edge==DrawerEdge.Top?".":"Drawers/"+DrawerCatalog.Id(p.Edge))).ToList()));
    }
    internal static bool IsReadFailure(Exception ex) => ex is IOException or InvalidDataException or JsonException
        or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException;

    private void ValidateRecoveryReferences(DrawerCollection value)
    {
        foreach (var drawer in value.Drawers)
        {
            string path = Resolve(drawer.Storage);
            if (IsTransferStorage(path) || File.Exists(Path.Combine(path, "library.json")) ||
                File.Exists(Path.Combine(path, "library.json.bak")))
                new ShortcutStore(path, Root).ValidateRecoveryReferences();
        }
    }

    private bool IsTransferStorage(string path) => path.StartsWith(Resolve("Transfers") + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase);
    // Version 3 allowed independent drawer overrides. Earlier versions already used a global value.
    private static DrawerCollection Upgrade(DrawerCollection value) => value.Version == 3
        ? value with { Global = value.Global with { ShowNames = true } } : value;

    public void SetShowNames(bool show) => Commit(Value with { Global = Value.Global with { ShowNames = show } });
    public void SetAllowInFullScreen(bool allow)
    {
        if (Value.Global.AllowInFullScreen != allow)
            Commit(Value with { Global = Value.Global with { AllowInFullScreen = allow } });
    }
    public void SetShowNavigation(bool show) => Commit(Value with { Global = Value.Global with { ShowNavigation = show } });
    public void SetColorScheme(DrawerColorScheme colorScheme)
    {
        if (Value.Global.ColorScheme != colorScheme)
            Commit(Value with { Global = Value.Global with { ColorScheme = colorScheme } });
    }
    public void SetMaterial(DrawerMaterial material)
    {
        if (Value.Global.Material != material)
            Commit(Value with { Global = Value.Global with { Material = material } });
    }
    public void SetLanguage(DrawerLanguage language)
    {
        if (Value.Global.Language != language)
            Commit(Value with { Global = Value.Global with { Language = language } });
    }

    internal static Guid LegacyId(DrawerEdge edge)=>new($"aadd0000-0000-0000-0000-{(int)edge+1:000000000000}");
    private DrawerCollection Read(string path)
    {
        Resolve(Path.GetRelativePath(Root, path));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        // Older independent Width/Height values in Size have no unique content scale.
        // The unknown property is ignored; a missing ContentScale uses the default.
        var value = document.RootElement.Deserialize<DrawerCollection>() ?? throw new JsonException();
        if (value.Version < 5 && value.Global != null)
        {
            var oldGlobal = document.RootElement.GetProperty("Global");
            var scheme = oldGlobal.TryGetProperty("Style", out var style)
                ? style.Deserialize<DrawerColorScheme>() : DrawerColorScheme.Light;
            value = value with { Global = value.Global with { ColorScheme = scheme, Material = DrawerMaterial.Solid } };
        }
        Validate(value);
        return value;
    }
    private void Validate(DrawerCollection value)
    {
        if((value.Version is <1 or >CurrentVersion)||value.Global==null||value.Drawers==null||value.Global.IconSize is <16 or >96||value.Global.Gap is <0 or >20||
            !Enum.IsDefined(value.Global.ColorScheme)||!Enum.IsDefined(value.Global.Material)||
            (value.Global.Language is { } language && !Enum.IsDefined(language)))throw new InvalidDataException(UiText.Get("InvalidCollection"));
        if(value.Drawers.Any(d=>d==null||d.Id==Guid.Empty||!Enum.IsDefined(d.Edge)||d.Order<0||!LayoutSelection.Valid(d.Rows,d.Columns)||
            (d.ContentScale is double scale && (!double.IsFinite(scale) || scale <= 0))||string.IsNullOrWhiteSpace(d.Name)||d.Name.Length>80||d.Name.Any(char.IsControl))||value.Drawers.Select(d=>d.Id).Distinct().Count()!=value.Drawers.Count)throw new ArgumentException(UiText.Get("InvalidDrawerRecord"));
        foreach(var edge in Enum.GetValues<DrawerEdge>()){var list=value.Drawers.Where(d=>d.Edge==edge).ToArray();if(list.Select(d=>d.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=list.Length||list.Select(d=>d.Order).Distinct().Count()!=list.Length)throw new ArgumentException(UiText.Get("DuplicateEdgeOrder"));}
        if(value.Drawers.Select(d=>ResolveLocation(d.Storage)).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=value.Drawers.Count)throw new ArgumentException(UiText.Get("DuplicateStorage"));
    }
    public void Commit(DrawerCollection value) => Publish(value, recovering: false);
    private void Publish(DrawerCollection value, bool recovering)
    {
        value=value with{Version=CurrentVersion,Global=value.Global with{Animations=true},Drawers=value.Drawers.Select(d=>d with{Animations=true}).ToList()};
        Validate(value);
        var currentPaths = Value.Drawers.ToDictionary(d => d.Id, d => d.Storage);
        foreach (var drawer in value.Drawers)
            if (!currentPaths.TryGetValue(drawer.Id, out var storage) || storage != drawer.Storage)
                Resolve(drawer.Storage);
        BeforeCommit?.Invoke(value);Resolve("collection.json");if(File.Exists(Config+".bak"))Resolve("collection.json.bak");string pending=Config+".tmp-"+Guid.NewGuid().ToString("N");
        try
        {
            using(var file=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            { JsonSerializer.Serialize(file,value,new JsonSerializerOptions{WriteIndented=true});file.Flush(true); }
            if (recovering) { ValidateRecoveryReferences(Read(pending)); BeforeRecoveryPublish?.Invoke(); }
            if(File.Exists(Config))File.Replace(pending,Config,recovering ? null : Config+".bak");
            else File.Move(pending,Config);
            Value=value;
            System.Threading.Interlocked.Increment(ref _revision);
        }
        finally{if(File.Exists(pending))File.Delete(pending);}
        foreach (var drawer in value.Drawers)
            if (!currentPaths.TryGetValue(drawer.Id, out var previous) || previous != drawer.Storage)
                MarkPublishedTransfer(ResolveLocation(drawer.Storage));
    }
    public IEnumerable<DrawerDefinition> Ordered(DrawerEdge edge)=>Value.Drawers.Where(d=>d.Edge==edge).OrderBy(d=>d.Order);
    public ShortcutStore OpenStore(Guid id){var store=new ShortcutStore(Resolve(Value.Drawers.Single(d=>d.Id==id).Storage),Root);store.Load();return store;}
    private DrawerDefinition New(DrawerEdge edge,List<DrawerDefinition> list)
    {
        int n = 1;
        string name;
        do { name = UiText.Get("DefaultDrawerName", DrawerCatalog.Label(edge), n++); }
        while (list.Any(d => d.Edge == edge && string.Equals(d.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)));
        Guid id = Guid.NewGuid();
        return new(id, name, edge, list.Where(d=>d.Edge==edge).Select(d=>d.Order+1).DefaultIfEmpty(0).Max(), true,
            edge==DrawerEdge.Top?5:6,edge==DrawerEdge.Top?6:1,"Libraries/"+id.ToString("N"),true);
    }
    public DrawerDefinition Draft(DrawerEdge edge)=>New(edge,Value.Drawers);
    public Guid Add(DrawerEdge edge){var d=New(edge,Value.Drawers);Commit(Value with{Drawers=Value.Drawers.Append(d).ToList()});return d.Id;}
    public void Update(Guid id,Func<DrawerDefinition,DrawerDefinition> update)
    {
        var original=Value.Drawers.SingleOrDefault(d=>d.Id==id)??throw new ArgumentException(UiText.Get("MissingDrawer"));var next=update(original);
        if(next.Id!=original.Id||next.Edge!=original.Edge||next.Order!=original.Order||next.Storage!=original.Storage)throw new ArgumentException(UiText.Get("InvalidDrawerUpdate"));
        Commit(Value with{Drawers=Value.Drawers.Select(d=>d.Id==id?next:d).ToList()});
    }
    public void Rename(Guid id,string name){name=name.Trim();if(string.IsNullOrWhiteSpace(name)||name.Length>80||name.Any(char.IsControl))throw new ArgumentException(UiText.Get("DrawerNameRange"));var drawer=Value.Drawers.Single(d=>d.Id==id);if(Value.Drawers.Any(d=>d.Id!=id&&d.Edge==drawer.Edge&&string.Equals(d.Name,name,StringComparison.OrdinalIgnoreCase)))throw new ArgumentException(UiText.Get("DuplicateDrawerName"));Update(id,d=>d with{Name=name});}

}
internal sealed class DrawerDwell
{
    private Guid? candidate;private double entered;
    public bool Update(Guid? id,double now){if(id!=candidate){candidate=id;entered=now;}return id!=null&&now-entered>=DrawerSettings.OpenDelayMs;}
    public void Clear(){candidate=null;entered=0;}
}
