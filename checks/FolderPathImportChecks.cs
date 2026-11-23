using System.Runtime.Versioning;
using Drawer.Core;

[SupportedOSPlatform("windows")]
internal static class FolderPathImportChecks
{
    public static void Run()
    {
        string root = Path.Combine(Environment.CurrentDirectory, "temp", "v67-19-folder-regression", Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, "带空格的 文件夹.lnk");
        Directory.CreateDirectory(folder);
        string original = Path.Combine(folder, "source.txt");
        File.WriteAllText(original, "original folder data");

        void Check(bool valid, string label)
        {
            if (!valid) throw new InvalidOperationException("folder path import: " + label);
            Console.WriteLine("PASS folder path import: " + label);
        }

        var store = new ShortcutStore(Path.Combine(root, "drawer"));
        store.Load();
        var imported = store.Import([folder + Path.DirectorySeparatorChar, folder]);
        Check(imported is { Added: 1, Duplicates: 1, Errors.Count: 0 }, "folder imports once with or without a trailing separator");
        var entry = store.Items.Single();
        string copy = store.CopyPath(entry);
        Check(entry.ShownName == Path.GetFileName(folder) && File.Exists(copy) && !Directory.Exists(copy),
            "a folder named .lnk remains a real folder target with its original display name");
        Check(FileShellLink.ReadTarget(copy) == folder && ShortcutIdentity.ReadIconSource(copy).Target == folder,
            "the Windows Shell link and icon pipeline resolve the physical folder");
        string externalLink = Path.Combine(root, "different name.lnk");
        FileShellLink.Create(externalLink, folder);
        Check(store.Import([externalLink]) is { Added: 0, Duplicates: 1, Errors.Count: 0 } && File.Exists(externalLink),
            "an existing shortcut to the same directory is deduplicated without modifying it");
        store.RenameShortcut(entry.Id, "My folder");

        var reopened = new ShortcutStore(store.Root);
        reopened.Load();
        Check(reopened.Items.Single().ShownName == "My folder" && FileShellLink.ReadTarget(reopened.CopyPath(reopened.Items.Single())) == folder,
            "restart retains the original directory target and its separate display name");
        reopened.RemoveLegacyFixture(reopened.Items.Single());
        Check(Directory.Exists(folder) && File.ReadAllText(original) == "original folder data" && !File.Exists(copy),
            "removal only removes the managed shortcut");

        reopened.Import(["https://example.test/a", "https://example.test/b"]);
        var group = reopened.CreateFolder(reopened.TopOrder.ToArray());
        var intoGroup = reopened.Import([folder], new ItemDrop(group, null, 1));
        var member = reopened.Items.Single(item => item.ShownName == Path.GetFileName(folder));
        Check(intoGroup is { Added: 1, Errors.Count: 0 } && reopened.Groups.Single().Members[1] == member.Id &&
            FileShellLink.ReadTarget(reopened.CopyPath(member)) == folder,
            "a Windows folder is inserted as one shortcut into a Drawer folder at the requested index");
    }
}
