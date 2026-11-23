using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Drawer.Core;
using Windows.Foundation;

namespace Drawer;

internal sealed class ResizePerformance
{
    internal enum Stage
    {
        Input, UiTick, UiTickGap, ApplyGap, Solve, NativeMain, NativeNeighbor, NativeMainGap, Clip,
        NeighborPlan, NeighborApply, NeighborCreate, NeighborTick, Content, FinalCheck, SaveWithHistory, FinalApply,
        MainMeasure, MainArrange, NeighborMeasure, NeighborArrange, Release, ResizeFrame, ResizeFrameGap
    }

    internal enum Counter
    {
        PointerMoves, MaxPointerMovesBetweenPolls, Polls, UnchangedCursor, ChangedCursor,
        RepeatedRequestedScale, EqualCandidate, CandidateWithoutPlacement, SameCandidateRect,
        AppliedCandidates, NativeMainRepeatedBounds, NativeNeighborRepeatedBounds,
        ClipChanged, ClipUnchanged, FrameFallbackPolls, NeighborPreparedReuse, ClientRectUnavailable,
        NativeResizePreserveClient, NativeResizeDiscardClient
    }

    private record struct Sample(double AtMs, Stage Stage, double Ms);
    internal readonly record struct EdgeGeometrySample(
        PixelRect RequestedWindow, PixelRect? Window, PixelRect? VisibleFrame, PixelRect? ClientOnScreen,
        double DpiScale, uint WindowDpi, int FrameWidth, double XamlScale,
        double SurfaceWidth, double CoverWidth, double CoverOpacity, string? CoverColor,
        double TargetContentWidth, double ActualContentWidth, double RootWidth,
        double RootPadding, double GridWidth, double GridScale, Rect? HostClip, Rect? GridClip, double RailWidth,
        double SurfaceHeight, double CoverHeight, double TargetContentHeight, double ActualContentHeight,
        double RootHeight, Rect? ContentInSurface, Rect? GridHostInSurface, Rect? VisualGridInSurface,
        int? NativeFrameThickness, string RequestedNativeBorderColor, string NativeBorderSetResult,
        double FillWidth, double FillHeight, string? FillColor, bool CompositionFill,
        DrawerMaterial RequestedMaterial, DrawerMaterial DisplayedMaterial);
    private record struct EdgeSample(double AtMs, string Phase, EdgeGeometrySample Geometry);
    private struct Totals
    {
        public int Count, Over8, Over17, Over33, Over50;
        public double Total, Max;
        public void Add(double ms)
        {
            Count++;
            Total += ms;
            Max = Math.Max(Max, ms);
            if (ms >= 8) Over8++;
            if (ms >= 17) Over17++;
            if (ms >= 33) Over33++;
            if (ms >= 50) Over50++;
        }
    }

    internal readonly struct Scope(ResizePerformance owner, Stage stage) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        public void Dispose() => owner.Record(stage, _start, Stopwatch.GetTimestamp());
    }

    private const int SampleLimit = 8192, MaxSessions = 64, MaxPendingWrites = 4;
    private static readonly Stage[] Stages = Enum.GetValues<Stage>();
    private static readonly Counter[] Counters = Enum.GetValues<Counter>();
    private static readonly bool Enabled = Environment.GetCommandLineArgs().Contains("--resize-perf", StringComparer.Ordinal);
    internal static readonly bool CompareClientPixelCopy = Enabled &&
        Environment.GetCommandLineArgs().Contains("--resize-copy-ab", StringComparer.Ordinal);
    private static readonly string Build = IdlePerformance.Build;
    private static readonly string Run = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}";
    private static int _sessions, _pendingWrites;
    private static string? _directory;

    private readonly Sample[] _timeline = new Sample[SampleLimit];
    private readonly EdgeSample[] _edges = new EdgeSample[192];
    private readonly Totals[] _totals = new Totals[Stages.Length];
    private readonly int[] _counters = new int[Counters.Length];
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private readonly object _scenario;
    private readonly string _id = Guid.NewGuid().ToString("N")[..12];
    private readonly int _number;
    private int _samples, _movesSincePoll;
    private int _edgeSamples;
    private long _lastEdgeSample;
    private long _lastTick, _lastApply, _lastNativeMain, _lastFrame;
    private double? _lastRequested;
    private bool _finished;
    private long _releaseStarted, _operationEnded;

    private ResizePerformance(object scenario, int number) { _scenario = scenario; _number = number; }

    internal static void Initialize()
    {
        if (!Enabled) return;
        try
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (!File.Exists(Path.Combine(dir.FullName, "Drawer.sln"))) continue;
                _directory = Path.Combine(dir.FullName, "temp", "resize-perf");
                Directory.CreateDirectory(_directory);
                File.WriteAllText(Path.Combine(_directory, $"{Build}-{Run}-enabled.txt"),
                    $"Build={Build}\nPID={Environment.ProcessId}\nEnabled=--resize-perf\nCompareClientPixelCopy={CompareClientPixelCopy}\nSchema=1\n");
                PrototypeLog.Write($"resize-perf enabled build={Build} run={Run}");
                return;
            }
            PrototypeLog.Write("resize-perf unavailable: project directory not found");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _directory = null;
            PrototypeLog.Write("resize-perf unavailable: " + ex.GetType().Name);
        }
    }

    internal static bool Available => _directory != null;
    internal static ResizePerformance? Start(object scenario)
    {
        if (!Available) return null;
        int number = Interlocked.Increment(ref _sessions);
        return number <= MaxSessions ? new(scenario, number) : null;
    }

    internal Scope Measure(Stage stage) => new(this, stage);
    internal void Increment(Counter counter) => _counters[(int)counter]++;
    internal void PointerMoved()
    {
        Increment(Counter.PointerMoves);
        _movesSincePoll++;
    }

    internal void Poll()
    {
        Increment(Counter.Polls);
        int index = (int)Counter.MaxPointerMovesBetweenPolls;
        _counters[index] = Math.Max(_counters[index], _movesSincePoll);
        _movesSincePoll = 0;
    }

    internal void Requested(double scale)
    {
        if (_lastRequested == scale) Increment(Counter.RepeatedRequestedScale);
        _lastRequested = scale;
    }

    internal void TickStarted() => RecordGap(Stage.UiTickGap, ref _lastTick);
    internal void FrameStarted() => RecordGap(Stage.ResizeFrameGap, ref _lastFrame);
    internal void CandidateApplied()
    {
        Increment(Counter.AppliedCandidates);
        RecordGap(Stage.ApplyGap, ref _lastApply);
    }
    internal void NativeMainCalled() => RecordGap(Stage.NativeMainGap, ref _lastNativeMain);
    internal void ReleaseStarted() => _releaseStarted = Stopwatch.GetTimestamp();
    internal bool SampleNextNativeBounds()
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastEdgeSample != 0 && Stopwatch.GetElapsedTime(_lastEdgeSample, now).TotalMilliseconds < 120) return false;
        _lastEdgeSample = now;
        return true;
    }
    internal void RecordEdge(string phase, EdgeGeometrySample sample)
    {
        _edges[_edgeSamples++ % _edges.Length] = new(Stopwatch.GetElapsedTime(_started).TotalMilliseconds, phase, sample);
    }

    private void RecordGap(Stage stage, ref long previous)
    {
        long now = Stopwatch.GetTimestamp();
        if (previous != 0) Record(stage, previous, now);
        previous = now;
    }

    private void Record(Stage stage, long from, long to)
    {
        if (_finished) return;
        double ms = Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;
        _totals[(int)stage].Add(ms);
        _timeline[_samples++ % SampleLimit] = new(Stopwatch.GetElapsedTime(_started, from).TotalMilliseconds, stage, ms);
    }

    internal void MarkOperationEnded()
    {
        if (_operationEnded != 0) return;
        _operationEnded = Stopwatch.GetTimestamp();
        if (_releaseStarted != 0) Record(Stage.Release, _releaseStarted, _operationEnded);
    }

    internal void Finish(string outcome, object final)
    {
        if (_finished) return;
        MarkOperationEnded();
        _finished = true;
        double durationMs = Stopwatch.GetElapsedTime(_started, _operationEnded).TotalMilliseconds;
        string? directory = _directory;
        if (directory == null) return;
        if (Interlocked.Increment(ref _pendingWrites) > MaxPendingWrites)
        {
            Interlocked.Decrement(ref _pendingWrites);
            PrototypeLog.Write("resize-perf skipped a report because the write queue is full");
            return;
        }
        _ = Task.Run(() =>
        {
            string? temporary = null;
            try
            {
                var timeline = Enumerable.Range(Math.Max(0, _samples - SampleLimit), Math.Min(_samples, SampleLimit))
                    .Select(index => _timeline[index % SampleLimit]).OrderBy(sample => sample.AtMs).ToArray();
                var stages = Stages.Select(stage =>
                {
                    var total = _totals[(int)stage];
                    var values = timeline.Where(sample => sample.Stage == stage).Select(sample => sample.Ms).Order().ToArray();
                    return new
                    {
                        stage = stage.ToString(), total.Count, total.Total,
                        average = total.Count == 0 ? 0 : total.Total / total.Count,
                        p95 = values.Length == 0 ? (double?)null : values[(int)Math.Ceiling(values.Length * .95) - 1],
                        total.Max, total.Over8, total.Over17, total.Over33, total.Over50, retained = values.Length
                    };
                }).ToArray();
                var counters = Counters.ToDictionary(counter => counter.ToString(), counter => _counters[(int)counter]);
                var edges = Enumerable.Range(Math.Max(0, _edgeSamples - _edges.Length), Math.Min(_edgeSamples, _edges.Length))
                    .Select(index => _edges[index % _edges.Length]).ToArray();
                var report = new
                {
                    schema = 1, build = Build, run = Run, session = _id, startedAt = _startedAt, durationMs,
                    outcome, scenario = _scenario, final, stages, counters, droppedSamples = _samples - timeline.Length,
                    timing = "Stopwatch wall time on UI thread; nested stages overlap. UiTickGap measures DispatcherQueue timer callbacks. ResizeFrameGap measures XAML Rendering callbacks, not GPU frames. NativeMainGap measures gaps between SetWindowPos calls. Native call times exclude asynchronous DWM/GPU work. Timeline columns: start ms, stage index (see stages array), duration ms.",
                    pending = "Resize polls the latest cursor without an application update queue. Pointer moves between polls count delivered events, not the OS queue.",
                    edgeGeometry = edges,
                    edgeUnits = "Window and client rectangles and native border thickness are physical pixels. XAML and composition fill sizes, clips and bounds in WindowSurface are DIPs. FillWidth and FillHeight record submitted geometry, not DWM presentation frames.",
                    timeline = timeline.Select(sample => new[] { sample.AtMs, (double)sample.Stage, sample.Ms })
                };
                string path = Path.Combine(directory, $"{Build}-{Run}-{_number:D2}-{_id}.json");
                temporary = path + ".partial";
                using (var file = File.Create(temporary)) JsonSerializer.Serialize(file, report);
                File.Move(temporary, path);
            }
            catch (Exception ex) { PrototypeLog.Write("resize-perf report failed: " + ex.GetType().Name); }
            finally
            {
                if (temporary != null)
                    try { File.Delete(temporary); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                Interlocked.Decrement(ref _pendingWrites);
            }
        });
    }
}
