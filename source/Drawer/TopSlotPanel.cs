using System;
using Drawer.Core;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Drawer;

// Top-level slots keep their row and column even while the viewport is resizing.
// The scroll viewer clips this panel without participating in its layout.
public sealed class TopSlotPanel : Panel
{
    private LayoutMetrics? _metrics;
    private LayoutMetrics? _arranged;
    internal Func<ResizePerformance?>? GetResizeTrace { get; set; }
    internal bool IsPreview { get; set; }

    internal void SetMetrics(LayoutMetrics metrics)
    {
        if (SameSlots(_metrics, metrics)) return;
        _metrics = metrics;
        _arranged = null;
        InvalidateMeasure();
    }

    internal bool IsArranged(LayoutMetrics metrics) => SameSlots(_arranged, metrics);

    private static bool SameSlots(LayoutMetrics? first, LayoutMetrics second) =>
        first != null && first.Columns == second.Columns && first.Rows == second.Rows &&
        first.CellWidth == second.CellWidth && first.CellHeight == second.CellHeight;

    protected override Size MeasureOverride(Size availableSize)
    {
        using var perf = GetResizeTrace?.Invoke()?.Measure(IsPreview
            ? ResizePerformance.Stage.NeighborMeasure : ResizePerformance.Stage.MainMeasure);
        if (_metrics is not { } metrics) return new Size();
        var cell = new Size(metrics.CellWidth, metrics.CellHeight);
        foreach (var child in Children) child.Measure(cell);
        int rows = (int)Math.Ceiling(Children.Count / (double)metrics.Columns);
        return new Size(metrics.Columns * cell.Width, rows * cell.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        using var perf = GetResizeTrace?.Invoke()?.Measure(IsPreview
            ? ResizePerformance.Stage.NeighborArrange : ResizePerformance.Stage.MainArrange);
        if (_metrics is not { } metrics) return finalSize;
        for (int i = 0; i < Children.Count; i++)
        {
            var (column, row) = LayoutMetrics.SlotPosition(i, metrics.Columns);
            Children[i].Arrange(new Rect(column * metrics.CellWidth, row * metrics.CellHeight,
                metrics.CellWidth, metrics.CellHeight));
        }
        _arranged = metrics;
        return finalSize;
    }
}
