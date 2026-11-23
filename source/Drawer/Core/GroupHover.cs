using System;

namespace Drawer.Core;
internal static class GroupHover
{
    // The icon is above the cell midpoint because the label reserves space.
    public static bool IsCenter(double x, double y, LayoutMetrics grid, bool retaining = false)
    {
        double icon = grid.IconSize;
        double radius = icon * (retaining ? DrawerSettings.ReceiveRetainFraction : DrawerSettings.ReceiveCenterFraction) / 2;
        return Math.Abs(x - grid.CellWidth / 2) <= radius &&
            Math.Abs(y - grid.IconCenterY) <= radius;
    }
}

// These coordinates describe the logical slot on the current page. Preview
// animations can move the painted tile without changing the candidate.
internal readonly record struct DropCell(Guid Drawer, int Page, int Index, Guid Item, bool IsFolder,
    double Left, double Top, LayoutMetrics Metrics);

// Mutually exclusive intents: undecided edge, sort preview, merge preview,
// or folder reception. The receiving actions also carry their dwell phase.
internal enum TopDropAction { EdgePending, EdgeReady, MergePending, MergeReady, Append, OpenFolder }

internal sealed class TopDropHover
{
    private double _since;
    private double _anchorX, _anchorY;
    public DropCell? Candidate { get; private set; }
    public TopDropAction Action { get; private set; }

    public TopDropAction Evaluate(DropCell cell, double x, double y, double now, bool publish, bool allowReceive = true)
    {
        bool same = Candidate == cell;
        bool wasReceiving = Action is not (TopDropAction.EdgePending or TopDropAction.EdgeReady);
        bool center = allowReceive && GroupHover.IsCenter(x - cell.Left, y - cell.Top, cell.Metrics,
            retaining: same && wasReceiving);
        double dx = x - _anchorX, dy = y - _anchorY;
        double tolerance = DrawerSettings.SortEdgeAnchorTolerance;
        // Measure motion from the fixed anchor, not the last pointer sample.
        bool restart = !same || center != wasReceiving || (!center && dx * dx + dy * dy > tolerance * tolerance);
        double since = restart ? now : _since;
        var action = !center
            ? now >= since + DrawerSettings.SortEdgePreviewMs ? TopDropAction.EdgeReady : TopDropAction.EdgePending
            : cell.IsFolder
                ? now >= since + DrawerSettings.FolderOpenDelayMs ? TopDropAction.OpenFolder : TopDropAction.Append
                : now >= since + DrawerSettings.MergeAnimationMs ? TopDropAction.MergeReady : TopDropAction.MergePending;
        if (publish)
        {
            Candidate = cell;
            if (restart) { _anchorX = x; _anchorY = y; }
            _since = since;
            Action = action;
        }
        return action;
    }

    public double Elapsed(double now) => now - _since;

    public void Reset()
    {
        Candidate = null;
        Action = TopDropAction.EdgePending;
        _since = 0;
        _anchorX = _anchorY = 0;
    }
}

internal enum FolderDragVisit { Inside, Waiting, Left }

internal sealed class FolderDragEntry
{
    private (int X, int Y)? _openingPoint;
    public bool Waiting => _openingPoint.HasValue;

    public void WaitAt(int x, int y) => _openingPoint = (x, y);
    public void Reset() => _openingPoint = null;

    public FolderDragVisit Visit(int x, int y, bool inPanel)
    {
        if (_openingPoint is { } origin)
        {
            if (!inPanel || origin == (x, y)) return FolderDragVisit.Waiting;
            Reset();
        }
        return inPanel ? FolderDragVisit.Inside : FolderDragVisit.Left;
    }
}

