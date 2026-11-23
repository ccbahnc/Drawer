using System;

namespace Drawer.Core;

internal readonly record struct ThumbnailSlot(double X, double Y, double Size);

internal static class FolderThumbnailLayout
{
    public const int Capacity = 4;

    public static ThumbnailSlot Slot(double icon, int index, int count)
    {
        count = Math.Clamp(count, 1, Capacity);
        double inset = icon * .15;
        double gap = icon * .08;
        double size = (icon - 2 * inset - gap) / 2;
        int row = index / 2;
        int rowCount = Math.Min(2, count - 2 * row);
        double rowWidth = rowCount * size + (rowCount - 1) * gap;
        double left = (icon - rowWidth) / 2;
        double top = count <= 2 ? (icon - size) / 2 : inset;
        return new(left + index % 2 * (size + gap), top + row * (size + gap), size);
    }

    public static double Radius(double icon) => icon * .27;
}
