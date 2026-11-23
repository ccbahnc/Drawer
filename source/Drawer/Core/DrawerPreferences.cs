using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Drawer.Core;
[JsonConverter(typeof(JsonStringEnumConverter<DrawerColorScheme>))]
internal enum DrawerColorScheme { Light, Dark }
[JsonConverter(typeof(JsonStringEnumConverter<DrawerMaterial>))]
internal enum DrawerMaterial { Solid = 0, Acrylic = 1, Blur = 2, Transparent = 3 }
[JsonConverter(typeof(JsonStringEnumConverter<DrawerLanguage>))]
internal enum DrawerLanguage { SimplifiedChinese, English }
internal sealed record GlobalAppearance(bool ShowNames=true,int IconSize=38,int Gap=5,bool Animations=true,
    DrawerColorScheme ColorScheme=DrawerColorScheme.Light,DrawerMaterial Material=DrawerMaterial.Solid,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DrawerLanguage? Language=null,
    bool ShowNavigation=true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InstallationLanguageRevision=null,
    bool AllowInFullScreen=false);
internal sealed record EdgePreference(DrawerEdge Edge,bool Enabled,int Rows,int Columns);
internal sealed record PreferenceDocument(int Version,GlobalAppearance Global,List<EdgePreference> Edges);
internal sealed class DrawerPreferences
{
    private readonly string root;
    private string PathName=>Path.Combine(root,"settings.json");
    public PreferenceDocument Value {get;private set;}=Defaults();
    public DrawerPreferences(string root)=>this.root=root;
    public static PreferenceDocument Defaults()=>new(1,new(),Enum.GetValues<DrawerEdge>().Select(e=>new EdgePreference(e,true,e==DrawerEdge.Top?5:6,e==DrawerEdge.Top?6:1)).ToList());
    public void Load(DrawerCatalog catalog,IReadOnlyDictionary<DrawerEdge,ShortcutStore> stores)
    {
        if(File.Exists(PathName))
        {
            try{Value=Read(PathName);return;}
            catch(Exception ex) when(ex is IOException or JsonException or ArgumentException)
            {
                File.Copy(PathName,PathName+".corrupt-"+Guid.NewGuid().ToString("N"));
                Value=Read(PathName+".bak");return;
            }
        }
        // Backup metadata before migration; shortcut copies stay in place.
        string backup=Path.Combine(root,"SettingsMigration",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(backup);
        foreach(string file in new[]{"drawers.json","library.json","library.json.bak","Drawers/left/library.json","Drawers/right/library.json"})
        {string source=Path.Combine(root,file);if(File.Exists(source))File.Copy(source,Path.Combine(backup,file.Replace('/','_')));}
        var top=stores[DrawerEdge.Top].Layout;
        Commit(new(1,new(top.ShowNames,Math.Clamp(top.IconSize,16,96),Math.Clamp(top.Gap,0,20)),Enum.GetValues<DrawerEdge>().Select(e=>new EdgePreference(e,catalog.Enabled(e),Math.Clamp(stores[e].Layout.Rows,1,8),Math.Clamp(stores[e].Layout.Columns,1,12))).ToList()));
    }
    private static PreferenceDocument Read(string file)
    {var value=JsonSerializer.Deserialize<PreferenceDocument>(File.ReadAllText(file))??throw new JsonException();Validate(value);return value;}
    private static void Validate(PreferenceDocument value)
    {
        if(value.Version!=1 || value.Global==null || value.Edges==null || value.Global.IconSize is <16 or >96 || value.Global.Gap is <0 or >20 ||
            !Enum.IsDefined(value.Global.ColorScheme) || !Enum.IsDefined(value.Global.Material)
            || value.Edges.Count!=3 || Enum.GetValues<DrawerEdge>().Any(e=>value.Edges.Count(p=>p.Edge==e)!=1) || value.Edges.Any(e=>e.Rows is <1 or >8 || e.Columns is <1 or >12))throw new ArgumentException(UiText.Get("InvalidSettings"));
    }
    public void Commit(PreferenceDocument value)
    {
        Validate(value);Directory.CreateDirectory(root);string pending=PathName+".tmp-"+Guid.NewGuid().ToString("N");
        try{using(var stream=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(stream,value,new JsonSerializerOptions{WriteIndented=true});stream.Flush(true);}
            if(File.Exists(PathName))File.Replace(pending,PathName,PathName+".bak");else File.Move(pending,PathName);Value=value;
        }finally{if(File.Exists(pending))File.Delete(pending);}
    }
    public void SetEdge(DrawerEdge edge,Func<EdgePreference,EdgePreference> update)=>Commit(Value with {Edges=Value.Edges.Select(e=>e.Edge==edge?update(e):e).ToList()});
}
internal sealed class LayoutSelection
{
    public const int Minimum = 1, Maximum = 20, GridRows = 8, GridColumns = 12;
    public int Rows{get;private set;} public int Columns{get;private set;}
    public int ConfirmedRows{get;private set;} public int ConfirmedColumns{get;private set;}
    public LayoutSelection(int rows,int columns){Confirm(rows,columns);}
    public static bool Valid(int rows,int columns)=>rows is >=Minimum and <=Maximum && columns is >=Minimum and <=Maximum;
    public void Preview(int rows,int columns){if(!Valid(rows,columns))throw new ArgumentException();Rows=rows;Columns=columns;}
    public void Confirm(int rows,int columns){Preview(rows,columns);ConfirmedRows=rows;ConfirmedColumns=columns;}
    public void Cancel()=>Preview(ConfirmedRows,ConfirmedColumns);
}
