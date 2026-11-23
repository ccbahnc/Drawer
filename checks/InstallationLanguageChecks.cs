using Drawer.Core;

internal static class InstallationLanguageChecks
{
    public static void Run()
    {
        int count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("Installation language: " + message);
            Console.WriteLine("PASS installation language: " + message); count++;
        }
        string root = Path.Combine(Path.GetTempPath(), "Drawer-language-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        void Request(string language, string revision) =>
            File.WriteAllText(Path.Combine(root, InstallationLanguage.FileName), language + "\r\n" + revision + "\r\n");
        MultiDrawerCatalog Load()
        {
            var result = new MultiDrawerCatalog(root);
            result.Load(new DrawerCatalog(root));
            return result;
        }
        Request("english", "first-en");
        var catalog = Load();
        Check(catalog.Value.Global.Language == DrawerLanguage.English && UiText.Language == DrawerLanguage.English,
            "fresh installation applies English before first UI");
        Check(catalog.Value.Drawers.All(d => d.Name.Contains("1") && !d.Name.Contains("抽屉")),
            "fresh default drawer names use installation language");
        Check(catalog.Value.Global.InstallationLanguageRevision == "first-en", "language and consumed revision saved together");
        var original = catalog.Value.Drawers.ToArray();
        catalog.SetLanguage(DrawerLanguage.SimplifiedChinese);
        catalog = Load();
        Check(catalog.Value.Global.Language == DrawerLanguage.SimplifiedChinese, "later app choice survives restart with request still present");
        Check(catalog.Value.Drawers.SequenceEqual(original), "language changes preserve identities names and layouts");
        Request("chinesesimp", "upgrade-zh");
        catalog = Load();
        Check(catalog.Value.Global.Language == DrawerLanguage.SimplifiedChinese &&
            catalog.Value.Global.InstallationLanguageRevision == "upgrade-zh", "upgrade adopts new Chinese installation revision");
        Request("english", "upgrade-en");
        catalog = Load();
        Check(catalog.Value.Global.Language == DrawerLanguage.English && UiText.Language == DrawerLanguage.English,
            "upgrade explicit English overrides earlier app choice");
        Check(catalog.Value.Drawers.SequenceEqual(original), "upgrade language never renames user content");
        catalog.SetLanguage(DrawerLanguage.SimplifiedChinese);
        string before = File.ReadAllText(Path.Combine(root, "collection.json"));
        Request("invalid", "bad-request");
        bool rejected = false;
        try { Load(); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && File.ReadAllText(Path.Combine(root, "collection.json")) == before,
            "invalid request cannot change real configuration");
        Request("english", "save-failure");
        catalog.BeforeCommit = _ => throw new IOException("isolated injected write failure");
        rejected = false;
        try { catalog.Load(new DrawerCatalog(root)); } catch (IOException) { rejected = true; }
        Check(rejected && File.ReadAllText(Path.Combine(root, "collection.json")) == before,
            "failed commit does not mark request applied or fake success");
        catalog = Load();
        Check(catalog.Value.Global.Language == DrawerLanguage.English &&
            catalog.Value.Global.InstallationLanguageRevision == "save-failure", "retry safely applies uncommitted request");
        File.Delete(Path.Combine(root, InstallationLanguage.FileName));
        catalog.SetLanguage(DrawerLanguage.SimplifiedChinese);
        Check(Load().Value.Global.Language == DrawerLanguage.SimplifiedChinese, "absent installer request preserves existing preference");
        string fresh = Path.Combine(root, "fresh-zh"); Directory.CreateDirectory(fresh);
        File.WriteAllText(Path.Combine(fresh, InstallationLanguage.FileName), "chinesesimp\nfirst-zh\n");
        var chinese = new MultiDrawerCatalog(fresh); chinese.Load(new DrawerCatalog(fresh));
        Check(chinese.Value.Global.Language == DrawerLanguage.SimplifiedChinese && chinese.Value.Drawers.All(d => d.Name.Contains("抽屉")),
            "fresh Chinese installation creates Chinese defaults");
        Console.WriteLine($"ALL {count} INSTALLATION LANGUAGE CHECKS PASSED; fixtures={root}");
    }
}
