using System;
using System.Collections.Generic;
using System.Linq;

namespace Drawer.Core;

internal sealed record DrawerLayout(int Columns = 6, int Rows = 5, int IconSize = 38, bool ShowNames = true, int Gap = 5);
internal sealed record LayoutMetrics(DrawerLayout Effective, double Width, double Height, double CellWidth, double CellHeight,
    int MaxColumns, int MaxRows, bool Adjusted, int VisibleColumns = 0, int VisibleRows = 0, double Padding = 10, double TilePadding = 5, double NameGap = 5)
{
    public const double HorizontalChrome = 34; // 10 DIP padding per side + 14 DIP scrollbar gutter.
    public const double VerticalChrome = 58; // 20 DIP padding + 32 DIP navigation + 6 DIP separation.
    public int Columns => VisibleColumns > 0 ? VisibleColumns : Effective.Columns;
    public int Rows => VisibleRows > 0 ? VisibleRows : Effective.Rows;
    public double? ExactIconSize { get; init; }
    public double IconSize => ExactIconSize ?? Effective.IconSize;
    public double IconCenterY => TilePadding + IconSize / 2;

    public double PictureSize(bool member = false)
    {
        double icon = member ? Effective.IconSize : IconSize;
        double centerY = TilePadding + icon / 2;
        double labelTop = TilePadding + icon + NameGap;
        double radius = Math.Min(CellWidth / 2, Math.Min(centerY, labelTop - centerY));
        // Compact folder cells may already use the available space at full zoom.
        double margin = Math.Clamp(radius - icon * DrawerSettings.SelectionHeldScale / 2,
            0, DrawerSettings.IconPictureMargin);
        return Math.Min(icon * DrawerSettings.IconPictureMultiplier,
            2 * (radius - margin) / DrawerSettings.SelectionHeldScale);
    }
    public const double StandardLabelHeight = 16;
    public static double LabelHeightFor(double cellHeight) => cellHeight < 32 ? 12 : StandardLabelHeight;
    public double LabelHeight => LabelHeightFor(CellHeight);
    public double LabelFontSize => CellHeight < 32 ? 10 : 12;
    public static (int Column, int Row) SlotPosition(int index, int columns) => (index % columns, index / columns);

    public int PageCellAt(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 ||
            x >= CellWidth * Columns || y >= CellHeight * Rows) return -1;
        int column = (int)(x / CellWidth), row = (int)(y / CellHeight);
        return column < Columns && row < Rows ? row * Columns + column : -1;
    }

    public int HitIndex(double x, double y, double scrollOffset, int count, bool insertion)
    {
        if (x < 0 || y < 0 || x >= CellWidth * Columns || y >= CellHeight * Rows) return -1;
        int row = (int)((y + Math.Max(0, scrollOffset)) / CellHeight);
        int index = row * Columns + (int)(x / CellWidth);
        if (insertion && x % CellWidth > CellWidth / 2) index++;
        return insertion ? Math.Min(index, count) : index < count ? index : -1;
    }
    public static double ScrollDirection(double y, double viewportHeight)
    {
        if (y < 0 || y >= viewportHeight || viewportHeight <= 0) return 0;
        double edge = Math.Min(32, viewportHeight / 3);
        return y < edge ? -(edge-y)/edge : y > viewportHeight-edge ? (y-viewportHeight+edge)/edge : 0;
    }
    public static LayoutMetrics Fit(DrawerLayout request, double availableWidth, double availableHeight, int itemCount = int.MaxValue, DrawerEdge edge = DrawerEdge.Top)
    {
        int columns=Math.Clamp(request.Columns,LayoutSelection.Minimum,LayoutSelection.Maximum), rows=Math.Clamp(request.Rows,LayoutSelection.Minimum,LayoutSelection.Maximum);
        int icon=Math.Clamp(request.IconSize,16,96),gap=Math.Clamp(request.Gap,0,20);
        double cellWidth=Math.Max(88,icon+20)+gap-5,cellHeight=icon+38+gap;
        var effective=new DrawerLayout(columns,rows,icon,request.ShowNames,gap);
        return new(effective,columns*cellWidth+HorizontalChrome,rows*cellHeight+VerticalChrome,
            cellWidth,cellHeight,LayoutSelection.Maximum,LayoutSelection.Maximum,effective!=request,columns,rows);
    }
}

internal static class ShortcutOrder
{
    public static Guid[] Move(IReadOnlyList<Guid> current, Guid id, int insertion)
    {
        if (insertion < 0 || insertion > current.Count || current.Count(x => x == id) != 1) throw new ArgumentException(UiText.Get("InvalidInsertion"));
        var next = current.ToList();
        int from = next.IndexOf(id); next.RemoveAt(from);
        if (insertion > from) insertion--;
        next.Insert(insertion, id);
        return next.ToArray();
    }
    public static List<ShortcutEntry> Apply(IReadOnlyList<ShortcutEntry> current, IEnumerable<Guid> orderedIds)
    {
        var ids = orderedIds.ToArray();
        if (ids.Length != current.Count || ids.Distinct().Count() != ids.Length)
            throw new ArgumentException(UiText.Get("OrderMustIncludeAll"));
        var map = current.ToDictionary(e => e.Id);
        if (ids.Any(id => !map.ContainsKey(id))) throw new ArgumentException(UiText.Get("UnknownOrderItem"));
        return ids.Select(id => map[id]).ToList();
    }
}
