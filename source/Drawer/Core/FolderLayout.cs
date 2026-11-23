using System;

namespace Drawer.Core;

internal sealed record FolderLayout(LayoutMetrics Grid, double TitleHeight, double Inset, double TitleGap, double Left, double Top)
{
    public const double ScrollGutter = 10;
    private const double Ratio = .75;
    public double Width => Grid.Width + 2 * Inset;
    public double Height => Grid.Height + 2 * Inset;
    public double TitleTop => Top - TitleHeight - TitleGap;
    public static double MemberLabelExtra(double cellHeight) => Math.Clamp(cellHeight - 24, 0, 2);

    public static FolderLayout Fit(LayoutMetrics parent, double width, double height)
    {
        double panelWidth = width * Ratio;
        double title = height >= 100 ? 24 : height >= 64 ? 20 : 15;
        double gapAbove = height >= 100 ? 6 : 3;
        double titleSpace = title + gapAbove;
        double panelHeight = Math.Max(1, Math.Min(height * Ratio, height - titleSpace - 4));
        double top = Math.Max((height - panelHeight) / 2, titleSpace + 2);
        double inset = Math.Clamp((panelHeight - 32) * .2, 2, 8);
        double viewport = panelHeight - 2 * inset;
        double gridWidth = panelWidth - 2 * inset;
        double cellArea = gridWidth - ScrollGutter;
        int columns = Math.Clamp((int)(cellArea / 46), 1, parent.Columns);
        int rows = Math.Clamp((int)(viewport / parent.CellHeight), 1, parent.Rows);
        double cellWidth = cellArea / columns;
        double cellHeight = viewport / rows;
        double padding = Math.Clamp((Math.Min(cellWidth, cellHeight) - 37) / 8, 0, parent.TilePadding);
        double gap = Math.Min(parent.NameGap, padding);
        int icon = (int)Math.Max(1, Math.Min(parent.IconSize,
            Math.Min(cellWidth - 2 * padding, cellHeight - 2 * padding - gap -
                LayoutMetrics.LabelHeightFor(cellHeight) - MemberLabelExtra(cellHeight))));
        var grid = parent with
        {
            Effective = parent.Effective with { IconSize = icon }, CellWidth = cellWidth, CellHeight = cellHeight,
            VisibleColumns = columns, VisibleRows = rows, Width = gridWidth, Height = viewport,
            TilePadding = padding, NameGap = gap
        };
        return new(grid, title, inset, gapAbove, (width - panelWidth) / 2, top);
    }
}
