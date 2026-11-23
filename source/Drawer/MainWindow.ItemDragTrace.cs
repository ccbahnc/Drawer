using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;

namespace Drawer;

public sealed partial class MainWindow
{
    private ItemDragTrace? _itemDragTrace;

    private void FinishItemDragTrace(bool landed)
    {
        if (_itemDragTrace is not { } trace) return;
        _itemDragTrace = null;
        var entry = trace.Finish(landed);
        _ = Task.Run(() => PrototypeLog.Write("drag-perf " + JsonSerializer.Serialize(entry)));
    }

    private sealed class ItemDragTrace(int items)
    {
        public static readonly bool Enabled = Array.IndexOf(Environment.GetCommandLineArgs(), "--drag-trace") >= 0;
        private const int EventLimit = 64;
        private readonly List<string> _events = new(EventLimit);
        private readonly long _start = Stopwatch.GetTimestamp();
        private int _calls, _slowCalls, _longGaps, _extraEvents;
        private double? _previous;
        private double _total, _max, _gapMax;

        public int PointerEvents { get; set; }

        public void Record(string message)
        {
            if (_events.Count < EventLimit)
                _events.Add($"{Stopwatch.GetElapsedTime(_start).TotalMilliseconds:F1}ms {message}");
            else _extraEvents++;
        }

        public void TargetSample(double now, double ms)
        {
            _calls++;
            _total += ms;
            _max = Math.Max(_max, ms);
            if (ms >= 17) _slowCalls++;
            if (_previous is double previous)
            {
                double gap = now - previous;
                _gapMax = Math.Max(_gapMax, gap);
                if (gap >= 34) _longGaps++;
            }
            _previous = now;
        }

        public object Finish(bool landed) => new
        {
            build = IdlePerformance.Build, items, landed,
            elapsedMs = Stopwatch.GetElapsedTime(_start).TotalMilliseconds,
            pointerEvents = PointerEvents, targetUpdates = _calls, targetAverageMs = _total / Math.Max(1, _calls),
            targetMaxMs = _max, targetUpdatesAtLeast17Ms = _slowCalls,
            targetUpdateGapMaxMs = _gapMax, targetUpdateGapsAtLeast34Ms = _longGaps,
            omittedEvents = _extraEvents, events = _events
        };
    }
}
