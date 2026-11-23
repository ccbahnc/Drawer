using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Drawer.Core;

internal sealed partial class MultiDrawerCatalog
{
    private sealed record HistoryState(List<DrawerDefinition> Drawers, Dictionary<Guid, ShortcutDocument> Libraries);
    private sealed record HistoryChange(HistoryState Before, HistoryState After);
    private readonly List<HistoryChange> _history = new();
    private HistoryState? _historyPending;
    private Dictionary<string, ShortcutStore>? _historyStores;
    private int _historyIndex;
    private string _historyNotice = "";

    internal string TakeHistoryNotice()
    {
        string notice = _historyNotice;
        _historyNotice = "";
        return notice;
    }
    public bool CanUndo => _historyPending == null && _historyIndex > 0;
    public bool CanRedo => _historyPending == null && _historyIndex < _history.Count;

    private HistoryState CaptureHistory(IReadOnlyDictionary<string, ShortcutStore> stores)
    {
        var drawers = Value.Drawers.ToList();
        var libraries = new Dictionary<Guid, ShortcutDocument>();
        foreach (var drawer in drawers)
        {
            string path = Path.GetFullPath(Path.Combine(Root, drawer.Storage));
            try
            {
                var store = stores.TryGetValue(path, out var loaded) ? loaded : OpenStore(drawer.Id);
                libraries.Add(drawer.Id, store.CaptureHistoryDocument());
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                System.Diagnostics.Trace.TraceWarning($"History: unavailable library {drawer.Id}: {ex.GetType().Name}");
            }
        }
        return new(drawers, libraries);
    }

    private static bool SameLibrary(ShortcutDocument left, ShortcutDocument right) => ReferenceEquals(left, right) ||
        left.Layout == right.Layout && left.Items.SequenceEqual(right.Items) && left.TopSlots!.SequenceEqual(right.TopSlots!) &&
        left.Groups!.Count == right.Groups!.Count && left.Groups.Zip(right.Groups).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name &&
            pair.First.PreserveSingleton == pair.Second.PreserveSingleton && pair.First.Members.SequenceEqual(pair.Second.Members));

    private static bool SameHistory(HistoryState before, HistoryState after) => before.Drawers.Count == after.Drawers.Count &&
        before.Drawers.Zip(after.Drawers).All(pair => pair.First with { Storage = "" } == pair.Second with { Storage = "" } &&
            (before.Libraries.TryGetValue(pair.First.Id, out var left)
                ? after.Libraries.TryGetValue(pair.Second.Id, out var right) && SameLibrary(left, right)
                : !after.Libraries.ContainsKey(pair.Second.Id)));

    private IEnumerable<string> HistoryCopies(HistoryState state) => state.Libraries.Values.SelectMany(d => d.Items)
        .Select(e => Path.GetFullPath(Path.Combine(Root, e.ManagedCopy!)));

    private static Dictionary<string, ShortcutStore> IndexHistoryStores(IEnumerable<ShortcutStore> stores) =>
        stores.DistinctBy(store => store.Root, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(store => store.Root, StringComparer.OrdinalIgnoreCase);

    private void TrackHistoryStore(ShortcutStore store)
    {
        if (_historyStores != null) _historyStores[store.Root] = store;
    }

    private HashSet<string> PinnedHistoryCopies()
    {
        var documents = _history.SelectMany(h => h.Before.Libraries.Values.Concat(h.After.Libraries.Values));
        if (_historyPending is { } pending) documents = documents.Concat(pending.Libraries.Values);
        return documents.Distinct<ShortcutDocument>(ReferenceEqualityComparer.Instance)
            .SelectMany(document => document.Items).Select(entry => entry.ManagedCopy!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(relative => Path.GetFullPath(Path.Combine(Root, relative)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public IDisposable BeginHistory(IEnumerable<ShortcutStore> stores)
    {
        if (_historyPending != null) throw new InvalidOperationException(UiText.Get("EditInProgress"));
        var indexed = IndexHistoryStores(stores);
        _historyPending = CaptureHistory(indexed);
        _historyStores = indexed;
        return new HistoryScope(this);
    }

    private sealed class HistoryScope(MultiDrawerCatalog owner) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.FinishHistory();
        }
    }

    private void FinishHistory()
    {
        var before = _historyPending!;
        try
        {
            var after = CaptureHistory(_historyStores!);
            if (after.Drawers.Any(d => !after.Libraries.ContainsKey(d.Id) && 
                (before.Libraries.ContainsKey(d.Id) || !before.Drawers.Contains(d))))
                throw new IOException(UiText.Get("HistoryLibraryUnreadable"));
            if (SameHistory(before, after)) return;
            var retired = _history.Skip(_historyIndex).ToList();
            _history.RemoveRange(_historyIndex, _history.Count - _historyIndex);
            _history.Add(new(before, after));
            if (_history.Count > 50) { retired.Add(_history[0]); _history.RemoveAt(0); }
            _historyIndex = _history.Count;
            if (retired.Count > 0)
            {
                var removed = retired.SelectMany(h => HistoryCopies(h.Before).Concat(HistoryCopies(h.After)))
                    .Except(HistoryCopies(after), StringComparer.OrdinalIgnoreCase).ToArray();
                if (removed.Length > 0)
                    QueueHistoryCleanup(removed.Except(PinnedHistoryCopies(), StringComparer.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex) when (IsReadFailure(ex) || ex is InvalidOperationException)
        {
            // Dispose runs after the data operation. A history failure must not report
            // a rollback or leave a stale undo/redo snapshot applicable.
            _historyNotice = UiText.Get("HistoryCleared");
            System.Diagnostics.Trace.TraceError("History: could not record completed operation: " + ex);
            _history.Clear();
            _historyIndex = 0;
        }
        finally { _historyPending = null; _historyStores = null; }
    }

    public Dictionary<Guid, ShortcutStore>? ApplyHistory(bool redo, IEnumerable<ShortcutStore> stores)
    {
        if (redo ? !CanRedo : !CanUndo) return null;
        var change = _history[redo ? _historyIndex : _historyIndex - 1];
        var state = redo ? change.After : change.Before;
        var current = CaptureHistory(IndexHistoryStores(stores));
        foreach (var unavailable in current.Drawers.Where(d => !current.Libraries.ContainsKey(d.Id)))
            if (!state.Drawers.Contains(unavailable) || state.Libraries.ContainsKey(unavailable.Id))
                throw new IOException(UiText.Get("HistoryCannotVerify"));
        var staged = new Dictionary<Guid, ShortcutStore>();
        try
        {
            QueueHistoryCleanup(HistoryCopies(current).Except(HistoryCopies(state), StringComparer.OrdinalIgnoreCase));
            var definitions = new List<DrawerDefinition>();
            foreach (var drawer in state.Drawers)
            {
                var existing = current.Drawers.FirstOrDefault(d => d.Id == drawer.Id);
                if (!state.Libraries.TryGetValue(drawer.Id, out var document))
                {
                    if (existing != drawer || current.Libraries.ContainsKey(drawer.Id))
                        throw new IOException(UiText.Get("HistoryCannotRestore"));
                    definitions.Add(existing);
                    continue;
                }
                if (existing != null && current.Libraries.TryGetValue(drawer.Id, out var existingLibrary) && SameLibrary(document, existingLibrary))
                {
                    definitions.Add(drawer with { Storage = existing.Storage });
                    continue;
                }
                string storage = "Transfers/" + Guid.NewGuid().ToString("N") + "/remaining";
                var store = new ShortcutStore(Resolve(storage), Root);
                staged.Add(drawer.Id, store);
                store.StageHistoryDocument(document);
                definitions.Add(drawer with { Storage = storage });
            }
            Commit(Value with { Drawers = definitions });
            _historyIndex += redo ? 1 : -1;
            foreach (var store in staged.Values) MarkPublishedTransfer(store.Root);
        }
        catch
        {
            foreach (var store in staged.Values)
                foreach (var name in new[] { "library.json", "library.json.bak" })
                    try { File.Delete(Path.Combine(store.Root, name)); }
                    catch (Exception ex) when (IsReadFailure(ex)) { }
            throw;
        }
        return staged;
    }

    public void CloseHistorySession()
    {
        if (_historyPending != null) return;
        _history.Clear();
        _historyIndex = 0;
        RetryDeletionCleanup();
    }

    private string HistoryCleanupPath => Resolve("HistoryCleanup.json");

    private void QueueHistoryCleanup(IEnumerable<string> paths)
    {
        try
        {
            var retired = paths.Select(path => Path.GetRelativePath(Root, path)).ToArray();
            if (retired.Length == 0) return;
            var pending = ReadHistoryCleanup();
            if (!retired.Any(path => !pending.Contains(path))) return;
            pending.UnionWith(retired);
            WriteAtomic(HistoryCleanupPath, JsonSerializer.SerializeToUtf8Bytes(pending));
        }
        catch (Exception ex) when (IsReadFailure(ex)) { CleanupNotice = UiText.Get("HistoryCleanupDeferred", ex.Message); }
    }

    private HashSet<string> ReadHistoryCleanup() => new(File.Exists(HistoryCleanupPath)
        ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(HistoryCleanupPath)) ?? throw new IOException(UiText.Get("InvalidHistoryCleanup"))
        : Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

    private void ProcessHistoryCleanup()
    {
        var pending = ReadHistoryCleanup();
        if (pending.Count == 0) return;
        var live = LiveCopies();
        live.UnionWith(PinnedHistoryCopies());
        var recovery = ArchivedCollections().Concat(File.Exists(Config + ".bak") ? new[] { Read(Config + ".bak") } : Array.Empty<DrawerCollection>());
        foreach (var storage in recovery.SelectMany(c => c.Drawers).Select(d => d.Storage).Distinct(StringComparer.OrdinalIgnoreCase))
            live.UnionWith(new ShortcutStore(Resolve(storage), Root).ReadManagedReferences());
        var directory = Resolve("DeletionTasks");
        if (Directory.Exists(directory))
            foreach (string journal in Directory.GetFiles(directory, "*.json"))
            {
                var task = JsonSerializer.Deserialize<DrawerDeletion>(File.ReadAllText(Resolve(Path.GetRelativePath(Root, journal))));
                if (task is not { Version: 1, Copies: not null }) throw new IOException(UiText.Get("DeletionCannotVerify"));
                live.UnionWith(task.Copies.Select(Resolve));
            }
        pending.RemoveWhere(relative =>
        {
            if (!IsManagedCopy(relative, out string path)) return true;
            if (live.Contains(path)) return false;
            try { File.Delete(path); }
            // A previous cleanup may already have removed the transfer directory.
            catch (DirectoryNotFoundException) { }
            return true;
        });
        WriteAtomic(HistoryCleanupPath, JsonSerializer.SerializeToUtf8Bytes(pending));
    }
}
