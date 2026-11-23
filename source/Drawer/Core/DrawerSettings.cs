namespace Drawer.Core;

// All dimensions are DIPs; native window positions and cursor hit tests use physical pixels.
internal static class DrawerSettings
{
    public const double Width = 760;
    public const double Height = 560;
    public const double TriggerWidth = 320;
    public const double TriggerHeight = 4;
    public const double SideMargin = 32;
    public const double OpenDelayMs = 200;
    public const double CloseDelayMs = 400;
    public const double SpringFrequency = 28;
    public const double DragHoldMs = 220;
    public const double DrawerDragHoldMs = 400;
    public const double DrawerSnapDistance = 36;
    public const double MergeAnimationMs = 200;
    public const double ReceiveCenterFraction = .60;
    public const double ReceiveRetainFraction = .72;
    public const double FormFolderAnimationMs = 340;
    public const double SortEdgePreviewMs = 250;
    public const double SortEdgeAnchorTolerance = 4;
    public const double DragPageDwellMs = 500;
    public const double DragPageOutsideTolerance = 20;
    public const double DragPageBottomBand = 16;
    public const double DragPageBottomRowOverlap = .30;
    public const double PageSlideMs = 250;
    public const double PageWheelQuietMs = 180;
    public const double SelectionPopMs = 200;
    public const double SelectionRestoreMs = 120;
    public const double SelectionDipScale = .90;
    public const double SelectionHeldScale = 1.08;
    public const double IconPictureMultiplier = 1.22;
    public const double IconPictureMargin = 1;
    public const double IconHitPadding = 2;
    public const double FolderOpenDelayMs = 600;
    public const double FolderAnimationMs = 320;
    public const double DragLift = 7;
    public const double DragScrollMax = 480;
    public const double DragThreshold = 8;
    public const double ReorderSpring = 24;
    public const int PollMs = 16;
}

internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

internal readonly record struct DrawerGeometry(PixelRect Monitor, PixelRect Drawer, PixelRect Trigger)
{
    public static DrawerGeometry Create(PixelRect screen, uint dpi, double? requestedWidth = null, double? requestedHeight = null)
    {
        double scale = dpi / 96d;
        int Px(double value) => (int)System.Math.Round(value * scale);
        int width = System.Math.Min(Px(requestedWidth ?? DrawerSettings.Width), System.Math.Max(1, screen.Width - 2 * Px(DrawerSettings.SideMargin)));
        int height = System.Math.Min(Px(requestedHeight ?? DrawerSettings.Height), System.Math.Max(1, screen.Height - Px(DrawerSettings.SideMargin)));
        int left = screen.Left + (screen.Width - width) / 2;
        int triggerWidth = System.Math.Min(Px(DrawerSettings.TriggerWidth), width);
        int triggerLeft = screen.Left + (screen.Width - triggerWidth) / 2;
        return new(screen, new(left, screen.Top, left + width, screen.Top + height),
            new(triggerLeft, screen.Top, triggerLeft + triggerWidth, screen.Top + System.Math.Max(1, Px(DrawerSettings.TriggerHeight))));
    }
}

