using Drawer.Core;
using System.Security.Cryptography;
using System.Text.Json;

internal static class StoreChecks
{
    public static void Run()
    {
        string project = AppContext.BaseDirectory;
        // Explicit workspace-contained disposable test data; never use production LocalAppData.
        while (!File.Exists(Path.Combine(project, "Drawer.sln"))) project = Directory.GetParent(project)?.FullName ?? throw new Exception("Workspace not found");
        string root = Path.Combine(project, "temp", "store-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "sources"); Directory.CreateDirectory(source);
        string a = Path.Combine(source, "A.lnk"), b = Path.Combine(source, "B.url"), bad = Path.Combine(source, "bad.txt");
        File.WriteAllBytes(a, new byte[] {1,2,3,4,5});
        File.WriteAllText(b, "[InternetShortcut]\r\nURL=https://example.com/\r\n");
        string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
        string original = Hash(a);
        int n = 0;
        void Check(bool valid, string text) { if (!valid) throw new Exception("FAIL store: " + text); n++; Console.WriteLine("PASS store: " + text); }
        var store = new ShortcutStore(Path.Combine(root, "data")); store.Load();
        var result = store.Import(new[] {a,b,bad,Path.Combine(source,"missing.lnk")});
        Check(result.Added == 2 && result.Errors.Count == 2, "mixed batch preserves successful items");
        Check(Hash(a) == original && Hash(store.CopyPath(store.Items[0])) == original, "source and copied bytes unchanged");
        Check(store.Import(new[]{a}).Duplicates == 1 && store.Items.Count == 2, "identical bytes skipped");
        string other = Path.Combine(source, "other"); Directory.CreateDirectory(other);
        string sameName = Path.Combine(other, "A.lnk"); File.WriteAllBytes(sameName,new byte[]{9,8,7});
        Check(store.Import(new[]{sameName}).Added == 1 && store.Items.Select(e=>e.Id).Distinct().Count()==3, "same name different content independent IDs");
        var ids = store.Items.Select(e=>e.Id).ToArray();
        var restarted = new ShortcutStore(store.Root); restarted.Load();
        Check(ids.SequenceEqual(restarted.Items.Select(e=>e.Id)), "restart restores IDs and order");
        var removed = restarted.Items[0]; string copy = restarted.CopyPath(removed); restarted.RemoveLegacyFixture(removed);
        Check(!File.Exists(copy) && File.Exists(a) && Hash(a)==original, "remove deletes managed copy only");
        var again = new ShortcutStore(store.Root); again.Load(); Check(again.Items.Count == 2, "removal persists");
        string config = Path.Combine(store.Root,"library.json"); File.WriteAllText(config,"{broken");
        var recovered = new ShortcutStore(store.Root); recovered.Load();
        Check(recovered.Notice.Contains("配置损坏") && Directory.GetFiles(store.Root,"*.corrupt-*").Length==1, "corrupt config preserved and backup restored");
        using var parsed=JsonDocument.Parse(File.ReadAllText(config)); Check(parsed.RootElement.GetProperty("Version").GetInt32()==2,"recovery writes complete valid JSON");
        File.WriteAllText(config,"{}"); File.WriteAllText(config+".bak","{}");
        var rebuilt = new ShortcutStore(store.Root); rebuilt.Load(); Check(rebuilt.Items.Count==2 && rebuilt.Notice.Contains("重建"),"double corruption rebuilds surviving managed copies");
        bool rejected=false; try { rebuilt.CopyPath(new(Guid.NewGuid(),"../outside.lnk",new string('A',64))); } catch(InvalidDataException){rejected=true;}
        Check(rejected,"path traversal rejected");
        for(int i=0;i<35;i++) { string p=Path.Combine(source,$"item-{i}.url"); File.WriteAllText(p,$"[InternetShortcut]\nURL=https://example.com/{i}\n"); rebuilt.Import(new[]{p}); }
        var many=new ShortcutStore(store.Root); many.Load(); Check(many.Items.Count==37,"more than one screen saves and restores");
        Check(!Directory.GetFiles(store.Root,"*.tmp").Any(),"atomic saves leave no partial temp files");
        var inputs = new ShortcutStore(Path.Combine(root, "document-and-web")); inputs.Load();
        string document = Path.Combine(source, "项目笔记.txt");
        File.WriteAllText(document, "document contents");
        const string website = "https://example.test/notes?q=1#part";
        var links = inputs.Import(new[] { document, website, document, website, "javascript:alert(1)" });
        Check(links.Added == 2 && links.Duplicates == 2 && links.Errors.Count == 1, "file and HTTP link import are deduplicated, other schemes are rejected");
        var fileLink = inputs.Items[0];
        Check(fileLink.FileName == "项目笔记.txt.lnk" && ShortcutIdentity.ReadIconSource(inputs.CopyPath(fileLink)).Target == document &&
            File.ReadAllText(document) == "document contents", "real Windows link targets the original Unicode document");
        var webLink = inputs.Items[1];
        Check(webLink.FileName == "example.test.url" && ShortcutIdentity.ReadInternetShortcut(inputs.CopyPath(webLink)).Url == website,
            "web shortcut preserves URL query and fragment");
        inputs.RemoveLegacyFixture(fileLink);
        Check(File.Exists(document) && !File.Exists(inputs.CopyPath(fileLink)), "removing a document link preserves the document");
        if (OperatingSystem.IsWindows())
        {
            string label = "着色器【参考】_" + new string('测', 110) + ".pdf";
            var deep = Path.Combine(source, new string('d', 270 - source.Length - label.Length - 2));
            Directory.CreateDirectory(deep);
            string longDocument = Path.Combine(deep, label);
            File.WriteAllText(longDocument, "document fixture");
            var deepStore = new ShortcutStore(Path.Combine(root, new string('s', 95))); deepStore.Load();
            var longLink = deepStore.Import(new[] { longDocument, longDocument });
            Check(longLink.Added == 1 && longLink.Duplicates == 1 && longLink.Errors.Count == 0,
                "long Unicode file path imports and deduplicates: " + string.Join(", ", longLink.Errors));
            var stored = deepStore.Items.Single();
            Check(FileShellLink.ReadTarget(deepStore.CopyPath(stored)) == longDocument && stored.ShownName == label &&
                File.ReadAllText(longDocument) == "document fixture", "long link preserves the exact target and display label");
            var reloaded = new ShortcutStore(deepStore.Root); reloaded.Load();
            var repeatDrop = reloaded.Import(new[] { longDocument });
            Check(repeatDrop is { Added: 0, Duplicates: 1, Errors.Count: 0 } && reloaded.Items.Single().Id == stored.Id,
                "reimport after restart skips the original PDF and keeps its existing entry");

            string pdf = Path.Combine(source, "报告【定稿】.pdf");
            File.WriteAllText(pdf, "document fixture");
            var pdfStore = new ShortcutStore(Path.Combine(root, "pdf")); pdfStore.Load();
            Check(pdfStore.Import(new[] { pdf }).Added == 1, "PDF can be added for the first time");
            string originalCopy = pdfStore.CopyPath(pdfStore.Items.Single());
            string secondLink = Path.Combine(source, "另一显示名称.lnk");
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
            dynamic shortcut = shell.CreateShortcut(secondLink);
            try
            {
                shortcut.TargetPath = pdf;
                shortcut.Description = "A shortcut with independent metadata";
                shortcut.Save();
                var repeat = pdfStore.Import(new[] { secondLink });
                Check(Hash(originalCopy) != Hash(secondLink) && repeat is { Added: 0, Duplicates: 1, Errors.Count: 0 },
                    "different shortcut bytes to the same PDF are detected as duplicates");
                shortcut.Arguments = "/mode";
                shortcut.Save();
                Check(pdfStore.Import(new[] { secondLink }).Added == 1, "arguments still distinguish shortcuts to the same target");
                shortcut.WorkingDirectory = source;
                shortcut.Save();
                Check(pdfStore.Import(new[] { secondLink }).Added == 1, "working directory still distinguishes shortcuts to the same target");
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
        }
        Console.WriteLine($"ALL {n} STORE CHECKS PASSED; evidence: {root}");
    }
}
