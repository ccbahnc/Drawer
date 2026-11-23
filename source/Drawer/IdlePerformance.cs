using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Drawer;

// All counters are updated on the UI thread. Only completed windows go to the writer.
internal sealed class IdlePerformance
{
    [Flags]
    internal enum Change { None = 0, Pointer = 1, Scene = 2, Interaction = 4, Material = 8, Display = 16, Layout = 32, Configuration = 64 }
    internal enum Work
    {
        MainTick, PreviewTick, LayoutTimer, FeedbackTimer, ResizeRendering, DisplayPoll, Geometry, LayoutSolve,
        StepSize, GridMetrics, SizeFolder, FolderFit, PageTick, TilePass, TileSlotsVisited, TilesRealized,
        RefreshTiles, CursorPoll, EdgeRoute, DesktopGate, ExplorerForeground, FullScreenEnumeration,
        EscapePoll, ClipboardPoll, NativeBounds, NativeZOrder, PreviewPreparation, FolderAnimationFrame, DiagnosticSample,
        HiddenIdleTick
    }

    private const int WindowSeconds = 10, MaximumWindows = 180, MaxPending = 2;
    private static readonly Assembly Assembly = typeof(IdlePerformance).Assembly;
    private static readonly string Version = Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    private static string? Metadata(string key) => Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == key)?.Value;
    private static readonly string BuildUtc = Metadata("BuildUtc") ?? "unknown";
    internal static readonly string Profile = Metadata("IdleProfile") ?? "optimized";
    internal static readonly string Build = $"{Metadata("BuildLabel") ?? $"v{Version.Split('.')[0]}"}-{BuildUtc}";
    internal static IdlePerformance? Current { get; private set; }
    private static readonly string[] WorkNames = Enum.GetNames<Work>();
    private readonly WorkTotal[] _totals = new WorkTotal[WorkNames.Length];
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly string _path;
    private Task _writer = Task.CompletedTask;
    private long _started, _nextObservation, _cpuTicks;
    private DateTimeOffset _startedUtc;
    private Snapshot? _first, _last;
    private Change _changes;
    private int _windows, _pending;
    private Point? _cursor;
    private readonly record struct Point(int X, int Y);
    private struct WorkTotal { public int Count; public long Ticks; }

    // No window titles, IDs, paths or clipboard data enter a sample.
    internal sealed record Snapshot(string Scene, string Foreground, bool Visible, bool Active,
        int PreviewWindows, int VisiblePreviews, int PreviewTimers, bool LayoutTimer, bool FeedbackTimer,
        bool RevealAnimating, bool ContentAnimating, bool ResizeRendering, bool PageTurning,
        int Dpi, Drawer.Core.PixelRect Screen, Drawer.Core.PixelRect WorkArea, int ScreenRevision,
        long ConfigurationRevision, int Rows, int Columns, double Scale, string ConfiguredMaterial, string DisplayedMaterial,
        string Scheme, int Drawers, int EnabledDrawers, int Icons, int VisibleSlots, bool ShowNavigation, int TimerIntervalMs,
        long WorkingSetBytes, long PrivateBytes);

    private IdlePerformance(string path) { _path = path; }

    internal static void Initialize()
    {
        if (!Environment.GetCommandLineArgs().Contains("--idle-perf", StringComparer.Ordinal)) return;
        try
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (!File.Exists(Path.Combine(dir.FullName, "Drawer.sln"))) continue;
                string output = Path.Combine(dir.FullName, "temp", "idle-perf");
                Directory.CreateDirectory(output);
                string run = $"{Build}-{Profile}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}";
                string path = Path.Combine(output, run + ".jsonl");
                File.WriteAllText(path, JsonSerializer.Serialize(new
                {
                    type = "start", schema = 2, version = Version, build = Build, buildUtc = BuildUtc, profile = Profile,
                    utc = DateTimeOffset.UtcNow, pid = Environment.ProcessId, os = Environment.OSVersion.Version.ToString(),
                    logicalProcessors = Environment.ProcessorCount, windowSeconds = WindowSeconds, maximumWindows = MaximumWindows,
                    cpu = "process CPU delta / monotonic elapsed; machine percent also divides by logical processors",
                    work = "elapsed wall time of each application scope, inclusive and overlapping; counts are not hardware wakeups"
                }) + Environment.NewLine);
                Current = new(path);
                PrototypeLog.Write($"idle-perf enabled build={Build} profile={Profile}");
                return;
            }
            PrototypeLog.Write("idle-perf unavailable: project directory not found");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { PrototypeLog.Write("idle-perf unavailable: " + ex.Message); }
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly IdlePerformance? _owner;
        private readonly Work _work;
        private readonly long _start;
        internal Scope(IdlePerformance owner, Work work)
        {
            _owner = owner;
            _work = work;
            _start = Stopwatch.GetTimestamp();
        }
        public void Dispose()
        {
            if (_owner == null) return;
            ref var total = ref _owner._totals[(int)_work];
            total.Count++;
            total.Ticks += Stopwatch.GetTimestamp() - _start;
        }
    }

    internal static Scope Measure(Work work) => Current is { } monitor ? new(monitor, work) : default;

    internal static void Count(Work work, int count = 1)
    {
        if (Current is { } monitor) monitor._totals[(int)work].Count += count;
    }

    internal static void Invalidate(Change change)
    {
        if (Current is { } monitor) monitor._changes |= change;
    }

    internal static void Cursor(int x, int y)
    {
        if (Current is not { } monitor) return;
        var point = new Point(x, y);
        if (monitor._cursor is { } previous && previous != point) monitor._changes |= Change.Pointer;
        monitor._cursor = point;
    }

    internal void Observe(MainWindow window)
    {
        if (window.IdleWorkInProgress) _changes |= Change.Interaction;
        long now = Stopwatch.GetTimestamp();
        if (now < _nextObservation) return;
        _nextObservation = now + Stopwatch.Frequency;
        try
        {
            Snapshot snapshot;
            long cpu;
            using (Measure(Work.DiagnosticSample))
            {
                _process.Refresh();
                snapshot = window.IdleSnapshot(_process.WorkingSet64, _process.PrivateMemorySize64);
                cpu = _process.TotalProcessorTime.Ticks;
            }
            _last = snapshot;
            if (_first == null)
            {
                Reset(now, cpu, snapshot);
                return;
            }
            _changes |= Compare(_first, snapshot);
            if (Stopwatch.GetElapsedTime(_started, now).TotalSeconds < WindowSeconds) return;
            Publish(now, cpu);
            if (++_windows >= MaximumWindows) { Stop(); return; }
            Reset(now, cpu, snapshot);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            PrototypeLog.Write($"idle-perf sampling stopped: 0x{ex.HResult:X8}");
            Stop();
        }
    }

    private void Reset(long now, long cpu, Snapshot snapshot)
    {
        Array.Clear(_totals);
        _started = now;
        _startedUtc = DateTimeOffset.UtcNow;
        _cpuTicks = cpu;
        _first = snapshot;
        _changes = snapshot.Active ? Change.Interaction : Change.None;
    }

    private static Change Compare(Snapshot first, Snapshot next)
    {
        Change changes = Change.None;
        if (next.Active) changes |= Change.Interaction;
        if (first.Scene != next.Scene || first.Foreground != next.Foreground) changes |= Change.Scene;
        if (first.ConfiguredMaterial != next.ConfiguredMaterial || first.DisplayedMaterial != next.DisplayedMaterial ||
            first.Scheme != next.Scheme) changes |= Change.Material;
        if (first.Dpi != next.Dpi || first.Screen != next.Screen || first.WorkArea != next.WorkArea ||
            first.ScreenRevision != next.ScreenRevision) changes |= Change.Display;
        if (first.ConfigurationRevision != next.ConfigurationRevision) changes |= Change.Configuration;
        if (first.Rows != next.Rows || first.Columns != next.Columns || first.Scale != next.Scale ||
            first.Drawers != next.Drawers || first.EnabledDrawers != next.EnabledDrawers || first.Icons != next.Icons ||
            first.VisibleSlots != next.VisibleSlots || first.ShowNavigation != next.ShowNavigation) changes |= Change.Layout;
        return changes;
    }

    private void Publish(long now, long cpu)
    {
        if (Volatile.Read(ref _pending) >= MaxPending) return;
        double seconds = Stopwatch.GetElapsedTime(_started, now).TotalSeconds;
        double cpuMs = TimeSpan.FromTicks(cpu - _cpuTicks).TotalMilliseconds;
        var work = _totals.Select((total, i) => new
        {
            name = WorkNames[i], count = total.Count, ms = total.Ticks * 1000d / Stopwatch.Frequency
        }).ToArray();
        var sample = new
        {
            type = "window", number = _windows + 1, build = Build, profile = Profile, startUtc = _startedUtc,
            seconds, cpuMs, oneCorePercent = cpuMs / (seconds * 10),
            machinePercent = cpuMs / (seconds * 10 * Environment.ProcessorCount),
            stable = _changes == Change.None,
            invalidReasons = _changes == Change.None ? "" : _changes.ToString(),
            pointerMoved = _changes.HasFlag(Change.Pointer), first = _first, last = _last, work
        };
        Interlocked.Increment(ref _pending);
        _writer = _writer.ContinueWith(_ =>
        {
            try { File.AppendAllText(_path, JsonSerializer.Serialize(sample) + Environment.NewLine); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally { Interlocked.Decrement(ref _pending); }
        }, TaskScheduler.Default);
    }

    internal void Stop()
    {
        if (!ReferenceEquals(Current, this)) return;
        Current = null;
        _process.Dispose();
    }
}
