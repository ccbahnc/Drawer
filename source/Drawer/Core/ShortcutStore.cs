using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Drawer.Core;

internal sealed record ShortcutEntry(Guid Id, string FileName, string Hash, string? ManagedCopy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayName = null)
{
    public string Name => Path.GetFileNameWithoutExtension(FileName);
    internal string ShownName => DisplayName ?? Name;
}
internal sealed record ShortcutDocument(int Version, List<ShortcutEntry> Items, DrawerLayout? Layout = null,
    List<ShortcutGroup>? Groups = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<Guid>? TopOrder = null,
    List<Guid?>? TopSlots = null)
{
    internal const int CurrentVersion = 2;
    internal bool SupportedVersion => Version is 1 or CurrentVersion;
}
internal sealed record ImportResult(int Added, int Duplicates, List<string> Errors, string? FirstFailure = null);
internal sealed record CopyFolder(string Name, string[] Paths);

// Called serially by the UI. Stores the shortcut bytes, never the resolved target.
internal sealed partial class ShortcutStore
{
    public string Root { get; }
    private string Copies => Path.Combine(Root, "Shortcuts");
    private string Config => Path.Combine(Root, "library.json");
    public List<ShortcutEntry> Items { get; private set; } = new();
    public DrawerLayout Layout { get; private set; } = new();
    public string Notice { get; private set; } = "";
    public bool RecoveryNeedsAttention { get; private set; }
    internal Action? BeforeRecoveryPublish { get; set; }
    private readonly string sharedRoot;
    public ShortcutStore(string? root = null,string? sharedRoot=null)
    {Root=Path.GetFullPath(root??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Drawer"));this.sharedRoot=Path.GetFullPath(sharedRoot??Root);}

    public static bool Supported(string path) => Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase);

    internal static bool IsManagedCopyPath(string path)
    {
        var parent = Path.GetDirectoryName(path);
        return Supported(path) && parent != null && Guid.TryParseExact(Path.GetFileName(parent), "N", out _) &&
            string.Equals(Path.GetFileName(Path.GetDirectoryName(parent)), "Shortcuts", StringComparison.OrdinalIgnoreCase);
    }

    private static void NoReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException(UiText.Get("ReparseDenied"));
    }
    private void CheckRoots()
    {
        Directory.CreateDirectory(Root); NoReparse(Root);
        Directory.CreateDirectory(Copies); NoReparse(Copies);
    }
    private static bool Valid(ShortcutEntry entry) => entry.Id != Guid.Empty && !string.IsNullOrWhiteSpace(entry.FileName)
        && entry.FileName == Path.GetFileName(entry.FileName) && entry.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && Supported(entry.FileName) && entry.Hash is { Length: 64 } && entry.Hash.All(Uri.IsHexDigit);
    public string CopyPath(ShortcutEntry entry)
    {
        if (!Valid(entry)) throw new InvalidDataException(UiText.Get("InvalidShortcutRecord"));
        CheckRoots();
        if(entry.ManagedCopy!=null)
        {
            if(Path.IsPathRooted(entry.ManagedCopy))throw new InvalidDataException(UiText.Get("InvalidCopyPath"));
            string reference=Path.GetFullPath(Path.Combine(sharedRoot,entry.ManagedCopy));
            if(!reference.StartsWith(sharedRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||Path.GetFileName(reference)!=entry.FileName)throw new InvalidDataException(UiText.Get("CopyPathOutsideRoot"));
            for(string? p=reference;p!=null;p=Path.GetDirectoryName(p)){if(File.Exists(p)||Directory.Exists(p))NoReparse(p);if(p.Equals(sharedRoot,StringComparison.OrdinalIgnoreCase))break;}
            return reference;
        }
        string dir = Path.Combine(Copies, entry.Id.ToString("N"));
        if (Directory.Exists(dir)) NoReparse(dir);
        string path = Path.Combine(dir, entry.FileName);
        if (File.Exists(path)) NoReparse(path);
        return path;
    }
    private WorkingLibrary ReadDocument(string path, bool requireCopies = false)
    {
        NoReparse(path);
        var doc = JsonSerializer.Deserialize<ShortcutDocument>(File.ReadAllText(path));
        if (doc == null || !doc.SupportedVersion || doc.Items == null || doc.Items.Any(e => e == null || !Valid(e))
            || doc.Items.Select(e => e.Id).Distinct().Count() != doc.Items.Count)
            throw new InvalidDataException(UiText.Get("InvalidLibraryFormat"));
        var slots = doc.Version == 1
            ? doc.TopOrder ?? doc.Items.Select(e => e.Id).ToList()
            : doc.TopSlots?.Select(id => id ?? Guid.Empty).ToList() ?? throw new InvalidDataException(UiText.Get("MissingTopCells"));
        if (doc.Version == 1 && slots.Contains(Guid.Empty) || doc.TopSlots?.Contains(Guid.Empty) == true)
            throw new InvalidDataException(UiText.Get("InvalidEmptyCell"));
        ValidateGroups(doc.Items, doc.Groups ?? new(), slots);
        foreach (var entry in doc.Items)
        {
            var copy = CopyPath(entry);
            if (requireCopies && !File.Exists(copy)) throw new InvalidDataException(UiText.Get("MissingRecoveryCopy", entry.FileName));
        }
        return new(doc.Items, doc.Layout ?? new DrawerLayout(), doc.Groups ?? new(), slots);
    }
    internal void ValidateRecoveryReferences()
    {
        foreach (string path in new[] { Config, Config + ".bak" })
        {
            if (!File.Exists(path)) continue;
            try { ReadDocument(path, requireCopies: true); return; }
            catch (Exception ex) when (Recoverable(ex)) { }
        }
        throw new InvalidDataException(UiText.Get("LibraryUnrecoverable", Root));
    }
    internal IEnumerable<string> ReadManagedReferences(string? documentPath = null)
    {
        var files = documentPath == null ? new[] { Config, Config + ".bak" } : new[] { documentPath };
        foreach (var file in files)
        {
            if (!File.Exists(file)) continue;
            foreach (var item in ReadDocument(file).Items) yield return CopyPath(item);
        }
    }
    private static bool Recoverable(Exception ex) => ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException;

    public void Load()
    {
        CheckRoots();
        Notice = "";
        RecoveryNeedsAttention = false;
        bool hasPrimary = File.Exists(Config);
        WorkingLibrary? document = null;
        if (hasPrimary)
        {
            try { document = ReadDocument(Config); }
            catch (Exception ex) when (Recoverable(ex))
            {
                NoReparse(Config);
                File.Copy(Config, Config + ".corrupt-" + Guid.NewGuid().ToString("N"));
            }
        }
        if (document == null)
        {
            bool hasBackup = File.Exists(Config + ".bak");
            if (hasBackup)
            {
                try { document = ReadDocument(Config + ".bak", requireCopies: true); }
                catch (Exception ex) when (Recoverable(ex))
                { Notice = UiText.Get("BackupUnavailable", ex.Message) + " "; }
            }
            if (document != null)
                Notice = hasPrimary ? UiText.Get("LibraryRecovered") : UiText.Get("LibraryMissing");
            else
            {
                string transfers = Path.Combine(sharedRoot, "Transfers") + Path.DirectorySeparatorChar;
                if (Root.StartsWith(transfers, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(UiText.Get("TransferSnapshotInvalid"));
                var recovered = RecoverCopies();
                document = new(recovered, new DrawerLayout(), new(), recovered.Select(e => e.Id).ToList());
                if (hasPrimary || hasBackup || recovered.Count > 0)
                    Notice += UiText.Get("LibraryRebuilt");
                RecoveryNeedsAttention = hasPrimary || hasBackup;
            }
            if (hasPrimary || hasBackup || document.Items.Count > 0)
            {
                document = FinishMove(document);
                Save(document.Items, document.Layout, document.Groups, document.Slots, recovering: true);
            }
        }
        Items = document.Items; Groups = document.Groups; TopSlots = document.Slots; Layout = document.Layout;
        if (Groups.Any(g => g.Members.Count < 2))
        {
            CommitGroups(Groups, TopSlots);
            Notice += UiText.Get("SingletonRestored");
        }
        int missing = Items.Count(e => !File.Exists(CopyPath(e)));
        if (missing > 0)
        {
            Notice += UiText.Get("MissingCopies", missing);
            RecoveryNeedsAttention = true;
        }
    }
    private List<ShortcutEntry> RecoverCopies()
    {
        var recovered = new List<ShortcutEntry>();
        foreach (string dir in Directory.EnumerateDirectories(Copies).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (!Guid.TryParseExact(Path.GetFileName(dir), "N", out Guid id)) continue;
            try
            {
                NoReparse(dir);
                var files = Directory.GetFiles(dir).Where(Supported).ToArray();
                if (files.Length != 1) continue;
                NoReparse(files[0]);
                recovered.Add(new(id, Path.GetFileName(files[0]), HashFile(files[0])));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return recovered;
    }
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private void Save(List<ShortcutEntry> items, DrawerLayout? layout = null, List<ShortcutGroup>? groups = null, List<Guid>? top = null, bool recovering = false)
    {
        CheckRoots();
        string temp = Path.Combine(Root, "library-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var slots = (top ?? TopSlots).Select(id => id == Guid.Empty ? (Guid?)null : id).ToList();
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new ShortcutDocument(ShortcutDocument.CurrentVersion, items, layout ?? Layout, groups ?? Groups, TopSlots: slots), new JsonSerializerOptions { WriteIndented = true });
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            if (recovering) { ReadDocument(temp, requireCopies: true); BeforeRecoveryPublish?.Invoke(); }
            if (File.Exists(Config)) { NoReparse(Config); if (File.Exists(Config + ".bak")) NoReparse(Config + ".bak"); File.Replace(temp, Config, recovering ? null : Config + ".bak"); }
            else File.Move(temp, Config);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal ShortcutEntry RenameShortcut(Guid id, string? value)
    {
        int index = Items.FindIndex(item => item.Id == id);
        if (index < 0) throw new InvalidOperationException(UiText.Get("RenameTargetChanged"));
        var previous = Items[index];
        string? name = value?.Trim();
        if (value != null && (name!.Length == 0 ||
            value.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029')))
            throw new ArgumentException(UiText.Get("InvalidShortcutDisplayName"));
        if (name == previous.Name) name = null;
        if (name == previous.DisplayName) return previous;
        var changed = previous with { DisplayName = name };
        var items = Items.ToList();
        items[index] = changed;
        Save(items);
        Items = items;
        return changed;
    }
    public ImportResult Import(IEnumerable<string> paths, ItemDrop? placement = null, System.Threading.CancellationToken cancellation = default,
        IReadOnlyList<CopyFolder>? copiedFolders = null, IReadOnlyDictionary<string, string>? copiedNames = null)
    {
        var document = Document();
        var members = placement?.Group is Guid group
            ? document.Groups.SingleOrDefault(g => g.Id == group)?.Members ?? throw new InvalidOperationException(UiText.Get("MissingDestinationFolder"))
            : null;
        if (placement is { } drop && (drop.Merge != null || drop.Index < 0 || members != null && drop.Index > members.Count))
            throw new ArgumentException(UiText.Get("InvalidDropPosition"));
        if (placement is { Group: Guid receiver, MemberPlacement: { } landing })
            landing.Validate(receiver, placement.Index, members!);
        int duplicates = 0;
        var identities = document.Items.Where(e => File.Exists(CopyPath(e))).Select(Identity).ToHashSet();
        var added = new List<ShortcutEntry>();
        var copiedIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        string? firstFailure = null;
        try
        {
            foreach (string source in paths)
            {
                cancellation.ThrowIfCancellationRequested();
                ShortcutEntry? entry = null;
                string? sourceProblem = null;
                try
                {
                    bool web = ShortcutIdentity.TryWebAddress(source, out var address);
                    bool directory = !web && Directory.Exists(source);
                    if (!web && !directory && !File.Exists(source))
                        throw new IOException(sourceProblem = UiText.Get("InvalidImportSource"));
                    string target = directory ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)) : source;
                    bool existing = !web && !directory && Supported(target);
                    CheckRoots();
                    string label = web ? address.IdnHost.Replace(':', '_') : Path.GetFileName(target);
                    string? originalLabel = null;
                    if (directory && label.Length == 0)
                    {
                        originalLabel = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        label = "Folder";
                    }
                    string name = label + (web ? ".url" : existing ? "" : ".lnk");
                    if (IsReservedDeviceName(name)) throw new IOException(sourceProblem = UiText.Get("ReservedFileName"));
                    Guid id = Guid.NewGuid();
                    if (!web && !existing && Path.Combine(Copies, id.ToString("N"), name).Length >= 260)
                    {
                        originalLabel ??= label;
                        name = directory ? "Folder.lnk" : "Document.lnk"; // The UUID directory is already unique.
                    }
                    entry = new(id, name, new string('0', 64), DisplayName: originalLabel);
                    string destination = CopyPath(entry);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (web) ShortcutIdentity.CreateWebLink(destination, address);
                    else if (existing) File.Copy(source, destination, false);
                    else ShortcutIdentity.CreateFileLink(destination, target);
                    File.SetAttributes(destination, FileAttributes.Normal);
                    // Hash the copy, since the source can change during import.
                    entry = entry with { Hash = HashFile(destination) };
                    if (!identities.Add(Identity(entry))) { duplicates++; continue; }
                    if (copiedNames != null && copiedNames.TryGetValue(source, out var displayName))
                        entry = entry with { DisplayName = displayName };
                    document.Items.Add(entry);
                    added.Add(entry);
                    copiedIds.TryAdd(source, entry.Id);
                    entry = null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    firstFailure ??= sourceProblem ?? UiText.Get("ImportCheckFiles");
                    errors.Add(UiText.Get("FileFailure", ShortcutIdentity.TryWebAddress(source, out var failedSite) ? failedSite.IdnHost : Path.GetFileName(source), ex.Message));
                }
                finally { if (entry != null) DiscardImportCopy(entry, errors); }
            }

            cancellation.ThrowIfCancellationRequested();
            if (added.Count > 0)
            {
                var ids = added.Select(e => e.Id).ToArray();
                if (placement is { Group: Guid folder, MemberPlacement: { } plan })
                {
                    var current = Groups.SingleOrDefault(g => g.Id == folder)?.Members
                        ?? throw new InvalidOperationException(UiText.Get("MissingDestinationFolder"));
                    plan.Validate(folder, placement.Index, current);
                    plan.Insert(folder, placement.Index, members!, ids);
                }
                else if (members != null) members.InsertRange(placement!.Index, ids);
                else
                {
                    var replacements = new Dictionary<Guid, Guid>();
                    foreach (var copiedFolder in copiedFolders ?? Array.Empty<CopyFolder>())
                    {
                        var content = copiedFolder.Paths.Where(copiedIds.ContainsKey).Select(path => copiedIds[path])
                            .Distinct().Where(id => !replacements.ContainsKey(id)).ToList();
                        if (content.Count == 0) continue;
                        var copiedId = Guid.NewGuid();
                        document.Groups.Add(new(copiedId, copiedFolder.Name, content, PreserveSingleton: content.Count == 1));
                        for (int index = 0; index < content.Count; index++)
                            replacements.Add(content[index], index == 0 ? copiedId : Guid.Empty);
                    }
                    var inserted = ids.Select(id => replacements.GetValueOrDefault(id, id)).Where(id => id != Guid.Empty).ToArray();
                    TopGridSlots.Insert(document.Slots, placement?.Index ?? document.Slots.Count, inserted);
                }
                ApplyDocument(document);
            }
        }
        catch
        {
            foreach (var entry in added) DiscardImportCopy(entry, errors);
            throw;
        }
        return new(added.Count, duplicates, errors, firstFailure);
    }

    private ShortcutIdentity Identity(ShortcutEntry entry) => ShortcutIdentity.Read(CopyPath(entry), entry.Hash);

    private void DiscardImportCopy(ShortcutEntry entry, List<string> errors)
    {
        try
        {
            string copy = CopyPath(entry);
            if (File.Exists(copy)) File.Delete(copy);
            string dir = Path.GetDirectoryName(copy)!;
            if (Directory.Exists(dir)) Directory.Delete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { errors.Add(UiText.Get("ImportCleanupFailed", ex.Message)); }
    }
    // Only the standalone legacy fixture tools use this path. The live UI
    // goes through MultiDrawerCatalog.DeleteItems and its durable journal.
    private static bool IsReservedDeviceName(string fileName)
    {
        int dot = fileName.IndexOf('.');
        string stem = (dot < 0 ? fileName : fileName[..dot]).TrimEnd().ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            stem[3] is >= '1' and <= '9' or '¹' or '²' or '³';
    }

    internal string RemoveLegacyFixture(ShortcutEntry entry)
    {
        if (!Items.Any(e => e.Id == entry.Id)) throw new IOException(UiText.Get("MissingItem"));
        string copy = CopyPath(entry); // Reject redirected paths BEFORE committing removal.
        var next = Items.Where(e => e.Id != entry.Id).ToList();
        var groups = Groups.Select(g => g with { Members = g.Members.Where(id => id != entry.Id).ToList() }).Where(g => g.Members.Count > 0).ToList();
        var top = TopSlots.Select(id => id == entry.Id || Groups.Any(g => g.Id == id) && !groups.Any(g => g.Id == id) ? Guid.Empty : id).ToList();
        (groups, top) = CollapseSmallGroups(groups, top);
        ValidateGroups(next, groups, top);
        Save(next, groups: groups, top: top); Items = next; Groups = groups; TopSlots = top;
        if (!IsManagedCopyPath(copy)) return UiText.Get("UnmanagedFileKept");
        try
        {
            // The first save backed up the old document. Advance the backup
            // before removing bytes that the previous document referenced.
            Save(next, groups: groups, top: top);
            if (ReadManagedReferences().Contains(copy, StringComparer.OrdinalIgnoreCase)) return UiText.Get("ReferencedCopyKept");
            if (File.Exists(copy)) File.Delete(copy);
            string dir = Path.GetDirectoryName(copy)!;
            if (Directory.Exists(dir)) Directory.Delete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return UiText.Get("RemovedCopyCleanupFailed", ex.Message); }
        return UiText.Get("RemovedOriginalKept");
    }
    public void Reorder(IEnumerable<Guid> ids)
    {
        var order = ids.ToList();
        if (order.Count != TopOrder.Count || !order.ToHashSet().SetEquals(TopOrder))
            throw new ArgumentException(UiText.Get("InvalidItemOrder"));
        int index = 0;
        var slots = TopSlots.Select(id => id == Guid.Empty ? id : order[index++]).ToList();
        if (Groups.Count == 0) { var next = ShortcutOrder.Apply(Items, order); Save(next, top: slots); Items = next; TopSlots = slots; }
        else CommitGroups(Groups, slots);
    }
    public void ClearEmptySlots()
    {
        if (TopSlots.Contains(Guid.Empty)) CommitGroups(Groups, TopOrder);
    }
    public void SetLayout(DrawerLayout layout)
    {
        Save(Items, layout); Layout = layout;
    }
}
