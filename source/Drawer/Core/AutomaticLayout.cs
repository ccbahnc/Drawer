using System;
using System.Collections.Generic;
using System.Linq;

namespace Drawer.Core;

[Flags]
internal enum ResizeEdge
{
    None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8,
    TopLeft = Top | Left, TopRight = Top | Right, BottomLeft = Bottom | Left, BottomRight = Bottom | Right
}

internal sealed record DrawerResizeLimits(PixelRect Bounds, int Columns, int Rows, int FrameWidth, int FrameHeight,
    double DpiScale, DrawerEdge Edge, bool ShowNavigation = true)
{
    private bool Top => Edge == DrawerEdge.Top;
    public double MinimumScale => !ShowNavigation ? AutomaticLayout.MinimumScale : Math.Max(AutomaticLayout.MinimumScale,
        (Math.Ceiling(AutomaticLayout.NavigationMinWidth * DpiScale) - Math.Ceiling(AutomaticLayout.PageGutter * DpiScale)) /
        (Columns * AutomaticLayout.ColumnPitch * DpiScale));

    public double FindMaximumScale()
    {
        bool Fits(double scale)
        {
            var (width, height, _, _, _) = GridPixels(scale);
            return width <= Bounds.Width && height <= Bounds.Height;
        }

        double minimum = MinimumScale, maximum = AutomaticLayout.MaximumScale;
        if (!Fits(minimum)) return 0;
        if (Fits(maximum)) return maximum;
        for (int i = 0; i < 24; i++)
        {
            double middle = (minimum + maximum) / 2;
            if (Fits(middle)) minimum = middle;
            else maximum = middle;
        }
        return minimum;
    }

    // Each item owns half the inter-item gap at either end of its slot. Subtract those
    // halves from the host padding, so a single row or column has no extra gap.
    public (int Width, int Height, double CellWidth, double CellHeight, double Padding) GridPixels(double scale)
    {
        int Pixels(double dip) => (int)Math.Round(dip * DpiScale);
        // Keep the slot pitch continuous. Rounding each slot separately makes
        // the whole viewport jump by a row or column count at every threshold.
        double cellWidth = AutomaticLayout.ColumnPitch * scale * DpiScale;
        double cellHeight = AutomaticLayout.RowPitch * scale * DpiScale;
        double padding = (AutomaticLayout.BasePadding - AutomaticLayout.BaseCellGap / 2) * scale * DpiScale;
        int width = FrameWidth + (int)Math.Ceiling(AutomaticLayout.PageGutter * DpiScale) +
            (int)Math.Ceiling(2 * padding + Columns * cellWidth);
        // Round the two fixed vertical regions independently, as XAML does, while
        // reserving at least their combined logical height for the clipped viewport.
        int header = Math.Max(Pixels(AutomaticLayout.TopBand(ShowNavigation)) + Pixels(AutomaticLayout.TopGap(ShowNavigation)),
            (int)Math.Ceiling(AutomaticLayout.TopChrome(ShowNavigation) * DpiScale));
        int height = FrameHeight + header + (int)Math.Ceiling(2 * padding + Rows * cellHeight);
        return (width, height, cellWidth, cellHeight, padding);
    }

    public PixelRect Place(double scale, int? alongStart = null)
    {
        var (width, height, _, _, _) = GridPixels(scale);
        int x = Top ? alongStart ?? Bounds.Left + (Bounds.Width - width) / 2
            : Edge == DrawerEdge.Left ? Bounds.Left : Bounds.Right - width;
        int y = Top ? Bounds.Top : alongStart ?? Bounds.Top + (Bounds.Height - height) / 2;
        return new(x, y, x + width, y + height);
    }

    public double DragScale(double startScale, double maximumScale, ResizeEdge edges, int dx, int dy)
    {
        double x = edges.HasFlag(ResizeEdge.Left) ? -1 : edges.HasFlag(ResizeEdge.Right) ? 1 : 0;
        double y = edges.HasFlag(ResizeEdge.Top) ? -1 : edges.HasFlag(ResizeEdge.Bottom) ? 1 : 0;
        // Measure from the original grip. Group recentering never feeds back into the drag.
        x *= AutomaticLayout.ContentWidth(Columns) * DpiScale * (Top ? .5 : 1);
        y *= AutomaticLayout.ContentHeight(Rows) * DpiScale * (Top ? 1 : .5);
        double distanceSquared = x * x + y * y;
        if (distanceSquared == 0) return startScale;
        return Math.Clamp(startScale + (dx * x + dy * y) / distanceSquared, MinimumScale, maximumScale);
    }
}

internal sealed record ArrangedDrawer(LayoutMetrics Metrics, EdgeGeometry Geometry, DrawerResizeLimits Limits,
    double ContentScale, double MaximumScale);
internal sealed record Arrangement(Dictionary<Guid, ArrangedDrawer> Items, Dictionary<DrawerEdge, string> Errors);

internal static class AutomaticLayout
{
    public const double HeaderHeight = 32, HeaderGap = 6, HeaderChrome = HeaderHeight + HeaderGap, HiddenDragBand = 20,
        BaseCellWidth = 83, BaseCellHeight = 76, BaseCellGap = 5, BasePadding = 10, BaseTilePadding = 1.5,
        BaseIcon = 38, MinIcon = 20, BaseNameGap = 5, NavigationMinWidth = 64,
        DefaultScale = 1.2, ManualMinimumScale = .85, MaximumScale = 1.82,
        PageGutter = 14, PanelGap = 12, EndMargin = 32, DepthMargin = 16, TriggerDepth = 2, ResizeBand = 5, ResizeCorner = 10;
    public const double ColumnPitch = BaseCellWidth + BaseCellGap, RowPitch = BaseCellHeight + BaseCellGap,
        TileInset = BaseTilePadding + BaseCellGap / 2;
    public static double MinimumScale => Math.Max(ManualMinimumScale, Math.Max(MinIcon / BaseIcon,
        (LayoutMetrics.StandardLabelHeight + 1) / (RowPitch - BaseIcon - 2 * TileInset - BaseNameGap)));
    public static string Unavailable => UiText.Get("LayoutUnavailable");
    public static double TopBand(bool navigation) => navigation ? HeaderHeight : HiddenDragBand;
    public static double TopGap(bool navigation) => navigation ? HeaderGap : 0;
    public static double TopChrome(bool navigation) => navigation ? HeaderChrome : HiddenDragBand;

    public static double ContentWidth(int columns) => columns * BaseCellWidth + (columns - 1) * BaseCellGap + 2 * BasePadding;
    public static double ContentHeight(int rows) => rows * BaseCellHeight + (rows - 1) * BaseCellGap + 2 * BasePadding;

    public static Arrangement Compute(DrawerCollection value, PixelRect screen, PixelRect work, uint dpi,
        int frameWidth = 0, int frameHeight = 0, bool adapt = true,
        IReadOnlyDictionary<Guid, double>? fixedScales = null, DrawerEdge? strictEdge = null)
    {
        var items = new Dictionary<Guid, ArrangedDrawer>();
        var errors = new Dictionary<DrawerEdge, string>();
        double dpiScale = (dpi == 0 ? 96 : dpi) / 96d;
        int Px(double dip) => (int)Math.Ceiling(dip * dpiScale);
        foreach (var edge in Enum.GetValues<DrawerEdge>())
        {
            var list = value.Drawers.Where(d => d.Edge == edge && d.Enabled).OrderBy(d => d.Order).ToArray();
            if (list.Length == 0) continue;
            bool adaptEdge = adapt && edge != strictEdge;
            bool top = edge == DrawerEdge.Top;
            int margin = Px(EndMargin), depthMargin = Px(DepthMargin), gap = Px(PanelGap);
            var bounds = top ? new PixelRect(work.Left + margin, work.Top, work.Right - margin, work.Bottom - depthMargin)
                : edge == DrawerEdge.Left ? new(work.Left, work.Top + margin, work.Right - depthMargin, work.Bottom - margin)
                : new(work.Left + depthMargin, work.Top + margin, work.Right, work.Bottom - margin);
            int available = top ? bounds.Width : bounds.Height;
            var limits = list.Select(d => new DrawerResizeLimits(bounds, d.Columns, d.Rows,
                frameWidth, frameHeight, dpiScale, edge, value.Global.ShowNavigation)).ToArray();
            double[] preferred = list.Select((d, i) => fixedScales?.GetValueOrDefault(d.Id, PreferredScale(d, limits[i]))
                ?? PreferredScale(d, limits[i])).ToArray();

            double EffectiveScale(int i, double factor) => Math.Max(limits[i].MinimumScale, preferred[i] * factor);

            long GroupLength(double amount)
            {
                long length = (long)gap * (list.Length - 1);
                for (int i = 0; i < list.Length; i++)
                {
                    var l = limits[i];
                    var (width, height, _, _, _) = l.GridPixels(EffectiveScale(i, amount));
                    if (width > bounds.Width || height > bounds.Height) return long.MaxValue;
                    length += top ? width : height;
                    if (length > available) return long.MaxValue;
                }
                return length;
            }

            if (adaptEdge && GroupLength(0) == long.MaxValue)
            {
                errors[edge] = UiText.Get("EdgeLayoutRecovery", DrawerCatalog.Label(edge));
                // Keep the saved order. A drawer that cannot fit must not take the
                // rest of its edge offline, including smaller drawers later in the order.
                var retained = new List<int>();
                int remaining = available;
                for (int i = 0; i < list.Length; i++)
                {
                    var (width, height, _, _, _) = limits[i].GridPixels(limits[i].MinimumScale);
                    int length = (top ? width : height) + (retained.Count == 0 ? 0 : gap);
                    if (width > bounds.Width || height > bounds.Height || length > remaining) continue;
                    retained.Add(i);
                    remaining -= length;
                }
                if (retained.Count == 0) continue;
                list = retained.Select(i => list[i]).ToArray();
                limits = retained.Select(i => limits[i]).ToArray();
                preferred = retained.Select(i => preferred[i]).ToArray();
            }

            double fraction = 1;
            long groupLength = GroupLength(fraction);
            if (groupLength == long.MaxValue && adaptEdge)
            {
                double lower = 0, upper = 1;
                for (int i = 0; i < 24; i++)
                {
                    double middle = (lower + upper) / 2;
                    if (GroupLength(middle) != long.MaxValue) lower = middle;
                    else upper = middle;
                }
                fraction = lower;
                groupLength = GroupLength(fraction);
            }

            if (groupLength == long.MaxValue)
            {
                errors[edge] = UiText.Get("EdgeLayoutUnavailable", DrawerCatalog.Label(edge));
                continue;
            }

            int start = (top ? bounds.Left : bounds.Top) + (available - (int)groupLength) / 2;
            int spare = available - (int)groupLength;
            for (int i = 0; i < list.Length; i++)
            {
                var l = limits[i];
                double contentScale = EffectiveScale(i, fraction);
                var rect = l.Place(contentScale, start);
                int length = top ? rect.Width : rect.Height;
                int maximumLength = length + spare;
                var resizeBounds = top ? bounds with { Right = bounds.Left + maximumLength }
                    : bounds with { Bottom = bounds.Top + maximumLength };
                var resizeLimits = l with { Bounds = resizeBounds };
                var geometry = new EdgeGeometry(screen, work, rect, Trigger(edge, screen, work, rect, dpiScale));
                items.Add(list[i].Id, new(Metrics(value.Global, l, contentScale), geometry, resizeLimits,
                    contentScale, Math.Max(contentScale, resizeLimits.FindMaximumScale())));
                start += length + gap;
            }
        }
        return new(items, errors);
    }

    public static double PreferredScale(DrawerDefinition drawer, DrawerResizeLimits limits) =>
        Math.Clamp(drawer.ContentScale ?? DefaultScale, limits.MinimumScale, MaximumScale);

    private static PixelRect Trigger(DrawerEdge edge, PixelRect screen, PixelRect work, PixelRect rect, double scale)
    {
        int depth = Math.Max(1, (int)Math.Round(TriggerDepth * scale));
        return edge switch
        {
            DrawerEdge.Top when work.Top == screen.Top => new(rect.Left, work.Top, rect.Right, work.Top + depth),
            DrawerEdge.Left when work.Left == screen.Left => new(work.Left, rect.Top, work.Left + depth, rect.Bottom),
            DrawerEdge.Right when work.Right == screen.Right => new(work.Right - depth, rect.Top, work.Right, rect.Bottom),
            _ => default
        };
    }

    private static LayoutMetrics Metrics(GlobalAppearance appearance, DrawerResizeLimits limits, double scale)
    {
        var (width, height, cellWidth, cellHeight, padding) = limits.GridPixels(scale);
        double dpi = limits.DpiScale;
        var layout = new DrawerLayout(limits.Columns, limits.Rows, (int)Math.Round(BaseIcon * scale), appearance.ShowNames,
            (int)Math.Round(BaseCellGap * scale));
        return new LayoutMetrics(layout, (width - limits.FrameWidth) / dpi, (height - limits.FrameHeight) / dpi, cellWidth / dpi,
            cellHeight / dpi, LayoutSelection.Maximum, LayoutSelection.Maximum, false, limits.Columns, limits.Rows,
            padding / dpi, TileInset * scale, BaseNameGap * scale) { ExactIconSize = BaseIcon * scale };
    }

    public static void ValidateChange(DrawerCollection previous, DrawerCollection candidate, PixelRect screen, PixelRect work,
        uint dpi, int fw, int fh, Arrangement? plan = null)
    {
        var changedEdges = Enum.GetValues<DrawerEdge>().Where(edge => !LayoutOf(previous, edge).SequenceEqual(LayoutOf(candidate, edge))).ToArray();
        if (changedEdges.Length == 0) return;
        var next = plan ?? Compute(candidate, screen, work, dpi, fw, fh);
        foreach (var edge in changedEdges)
            if (next.Errors.ContainsKey(edge) &&
                !IsRemovalOnly(previous, candidate, edge) &&
                !CanReduceUnplacedEdge(previous, candidate, edge, screen, work, dpi, fw, fh, next))
                throw new ArgumentException(UiText.Get("EdgeLayoutUnavailable", DrawerCatalog.Label(edge)));
    }

    private static bool CanReduceUnplacedEdge(DrawerCollection previous, DrawerCollection candidate, DrawerEdge edge,
        PixelRect screen, PixelRect work, uint dpi, int fw, int fh, Arrangement newPlan)
    {
        var before = LayoutOf(previous, edge).ToDictionary(d => d.Id);
        var after = LayoutOf(candidate, edge).ToArray();
        // An existing overflow can be repaired in steps without displacing a usable drawer.
        if (before.Count != after.Length || !after.All(d => before.TryGetValue(d.Id, out var old) &&
            d.Rows <= old.Rows && d.Columns <= old.Columns)) return false;
        var oldPlan = Compute(previous, screen, work, dpi, fw, fh);
        if (!oldPlan.Errors.ContainsKey(edge)) return false;
        // A preference-only change cannot free minimum-size space. Keep the same
        // recoverable subset while still allowing its preferred zoom to be reset.
        if (after.All(d => d.Rows == before[d.Id].Rows && d.Columns == before[d.Id].Columns))
            return after.Select(d => d.Id).SequenceEqual(LayoutOf(previous, edge).Select(d => d.Id)) &&
                after.All(d => oldPlan.Items.ContainsKey(d.Id) == newPlan.Items.ContainsKey(d.Id));
        return after.All(d => !oldPlan.Items.TryGetValue(d.Id, out var placed) ||
            newPlan.Items.TryGetValue(d.Id, out var next) &&
            (d != before[d.Id] || next.ContentScale >= placed.ContentScale));
    }

    internal static IEnumerable<(Guid Id, int Rows, int Columns, double? Scale)> LayoutOf(DrawerCollection value, DrawerEdge edge) =>
        value.Drawers.Where(d => d.Enabled && d.Edge == edge).OrderBy(d => d.Order)
            .Select(d => (d.Id, d.Rows, d.Columns, d.ContentScale));

    private static bool IsRemovalOnly(DrawerCollection previous, DrawerCollection candidate, DrawerEdge edge)
    {
        var old = LayoutOf(previous, edge).ToArray();
        var next = LayoutOf(candidate, edge).ToArray();
        var retained = next.Select(d => d.Id).ToHashSet();
        return next.Length < old.Length && next.SequenceEqual(old.Where(d => retained.Contains(d.Id)));
    }

}

internal static class DrawerMove
{
    public static DrawerCollection Propose(DrawerCollection value,Guid id,DrawerEdge edge,int insertion)
    {
        var source=value.Drawers.Single(d=>d.Id==id);var target=value.Drawers.Where(d=>d.Edge==edge&&d.Id!=id).OrderBy(d=>d.Order).ToList();
        if(target.Any(d=>string.Equals(d.Name,source.Name,StringComparison.OrdinalIgnoreCase)))throw new ArgumentException(UiText.Get("DuplicateEdgeName"));
        target.Insert(Math.Clamp(insertion,0,target.Count),source with{Edge=edge});
        var updates=target.Select((d,i)=>d with{Order=i}).ToDictionary(d=>d.Id);
        if(source.Edge!=edge)foreach(var d in value.Drawers.Where(d=>d.Edge==source.Edge&&d.Id!=id).OrderBy(d=>d.Order).Select((d,i)=>d with{Order=i}))updates[d.Id]=d;
        return value with{Drawers=value.Drawers.Select(d=>updates.TryGetValue(d.Id,out var changed)?changed:d).ToList()};
    }
}
