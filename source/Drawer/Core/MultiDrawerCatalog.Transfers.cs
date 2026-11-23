using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Drawer.Core;

internal sealed partial class MultiDrawerCatalog
{
    internal ConcurrentQueue<string> TransferDiagnostics { get; } = new();
    private const string Published = "published";

    private IEnumerable<DrawerCollection> ArchivedCollections()
    {
        var migrations = Resolve("UnifiedMigration");
        if (!Directory.Exists(migrations)) yield break;
        foreach (string directory in Directory.EnumerateDirectories(migrations))
        foreach (string name in new[] { "collection.json", "collection.json.bak" })
        {
            string path = Resolve(Path.GetRelativePath(Root, Path.Combine(directory, name)));
            if (File.Exists(path)) yield return Read(path);
        }
    }

    private IEnumerable<string> ArchivedLegacyReferences()
    {
        var migrations = Resolve("CollectionMigration");
        if (!Directory.Exists(migrations)) yield break;
        foreach (string directory in Directory.EnumerateDirectories(migrations))
        foreach (var (storage, name) in new[] { (".", "library.json"), ("Drawers/left", "Drawers_left_library.json"),
            ("Drawers/right", "Drawers_right_library.json") })
        foreach (string archivedName in new[] { name, name + ".bak" })
        {
            string path = Resolve(Path.GetRelativePath(Root, Path.Combine(directory, archivedName)));
            if (!File.Exists(path)) continue;
            foreach (var copy in new ShortcutStore(Resolve(storage), Root).ReadManagedReferences(path))
                yield return copy;
        }
    }

    private void MarkPublishedTransfer(string storage)
    {
        string transferRoot = Path.Combine(Root, "Transfers") + Path.DirectorySeparatorChar;
        if (!storage.StartsWith(transferRoot, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var batch = Path.GetDirectoryName(storage)!;
            if (!Guid.TryParseExact(Path.GetFileName(batch), "N", out _)) return;
            Resolve(Path.GetRelativePath(Root, storage));
            string marker = Resolve(Path.GetRelativePath(Root, Path.Combine(batch, Published)));
            if (File.Exists(Path.Combine(storage, "library.json")) && !File.Exists(marker))
                WriteAtomic(marker, Array.Empty<byte>());
        }
        catch (Exception ex) when (IsReadFailure(ex))
        { TransferDiagnostics.Enqueue("Could not record published snapshot: " + ex.Message); }
    }

    internal void ReclaimTransferSnapshots()
    {
        try
        {
            string root = Resolve("Transfers");
            if (!Directory.Exists(root)) return;
            var retained = Value.Drawers.Select(d => Resolve(d.Storage)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(Config + ".bak")) retained.UnionWith(Read(Config + ".bak").Drawers.Select(d => Resolve(d.Storage)));
            foreach (var archived in ArchivedCollections()) retained.UnionWith(archived.Drawers.Select(d => Resolve(d.Storage)));
            foreach (string storage in retained) MarkPublishedTransfer(storage);

            var protectedCopies = retained.SelectMany(storage => new ShortcutStore(storage, Root).ReadManagedReferences())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            protectedCopies.UnionWith(ArchivedLegacyReferences());
            protectedCopies.UnionWith(PinnedHistoryCopies());
            protectedCopies.UnionWith(ReadHistoryCleanup().Select(Resolve));
            var journals = Resolve("DeletionTasks");
            if (Directory.Exists(journals))
            {
                foreach (string file in Directory.EnumerateFiles(journals, "*.json"))
                {
                    var path = Resolve(Path.GetRelativePath(Root, file));
                    var task = JsonSerializer.Deserialize<DrawerDeletion>(File.ReadAllText(path));
                    if (task is not { Version: 1, Copies: not null }) throw new InvalidDataException(UiText.Get("InvalidPendingDeletion"));
                    retained.Add(Resolve(task.Storage));
                    protectedCopies.UnionWith(task.Copies.Select(Resolve));
                }
            }
            int unmarked = 0;
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
                string batch = Resolve(Path.GetRelativePath(Root, directory));
                string marker = Resolve(Path.GetRelativePath(Root, Path.Combine(batch, Published)));
                if (!File.Exists(marker)) { unmarked++; continue; }
                foreach (string kind in new[] { "source", "target", "remaining" })
                {
                    var path = Resolve(Path.GetRelativePath(Root, Path.Combine(batch, kind)));
                    if (!Directory.Exists(path) || retained.Contains(path) ||
                        protectedCopies.Any(copy => copy.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
                    // Only reclaim superseded documents. Shortcut bytes and unknown files stay where they are.
                    foreach (string name in new[] { "library.json", "library.json.bak" })
                    {
                        var document = Resolve(Path.GetRelativePath(Root, Path.Combine(path, name)));
                        if (File.Exists(document)) File.Delete(document);
                    }
                    string shortcuts = Resolve(Path.GetRelativePath(Root, Path.Combine(path, "Shortcuts")));
                    if (Directory.Exists(shortcuts) && !Directory.EnumerateFileSystemEntries(shortcuts).Any()) Directory.Delete(shortcuts);
                    if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
                }
                if (Directory.EnumerateFileSystemEntries(batch).All(path => path.Equals(marker, StringComparison.OrdinalIgnoreCase)))
                { File.Delete(marker); Directory.Delete(batch); }
            }
            if (unmarked > 0) TransferDiagnostics.Enqueue($"Retained {unmarked} transfer batches without publication evidence.");
        }
        catch (Exception ex) when (IsReadFailure(ex))
        { TransferDiagnostics.Enqueue("Snapshot cleanup deferred: " + ex.Message); }
    }
}
