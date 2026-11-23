using System;
using System.Collections.Generic;
using System.Text.Json;
using Drawer.Core;
using Microsoft.UI.Xaml.Media;
using static Drawer.NativeMethods;

namespace Drawer;

internal static class RevealTailTrace
{
    internal static readonly bool Enabled = Array.IndexOf(Environment.GetCommandLineArgs(), "--reveal-trace") >= 0;
    internal const int MaximumSessions = 42;
    private static int _sessions;

    internal static bool TryStartSession()
    {
        if (_sessions >= MaximumSessions) return false;
        _sessions++;
        return true;
    }
}

public sealed partial class MainWindow
{
    private const int RevealTraceMaximumDepth = 48, RevealTraceMaximumFrames = 40;
    private const int RightOpeningTraceMaximumFrames = 7;
    private List<RevealTailFrame>? _revealTail;
    private List<RevealTailFrame>? _rightOpeningFrames;
    private bool _rightOpeningUsesFixedWidth;

    private sealed record RevealTailFrame(string Phase, DateTimeOffset Utc, double AtMs, double Progress, bool TargetOpen,
        int RequestedDepth, double TimerMs, bool Visible, bool NativeVisible, int NativeLeft, int NativeTop, int NativeWidth, int NativeHeight,
        int ClientWidth, int ClientHeight, double SlideX, double SlideY, double SurfaceWidth, double SurfaceHeight,
        double CoverWidth, double CoverHeight, double GridClipWidth, double GridClipHeight,
        Drawer.Core.PixelRect? DwmFrame, int? DwmFrameThickness);

    private void RecordRevealTail(string phase, int requestedDepth)
    {
        if (!RevealTailTrace.Enabled || _closed || (_previewOnly && (!_retireSurface || _resizePreview))) return;
        if (_revealTail == null &&
            (requestedDepth <= 0 || requestedDepth > RevealTraceMaximumDepth ||
                _interaction.TargetOpen || !RevealTailTrace.TryStartSession())) return;
        var frames = _revealTail ??= new(RevealTraceMaximumFrames);
        if (frames.Count >= RevealTraceMaximumFrames)
            frames.RemoveAt(1); // Preserve the first observation and the most recent frames.
        frames.Add(ReadRevealFrame(phase, requestedDepth));
    }

    private RevealTailFrame ReadRevealFrame(string phase, int requestedDepth)
    {
        var utc = DateTimeOffset.UtcNow;
        GetWindowRect(_hwnd, out var outer);
        GetClientRect(_hwnd, out var client);
        var dwmFrame = DwmGetWindowAttribute(_hwnd, 9, out Rect extended, sizeof(int) * 4) == 0
            ? (PixelRect?)extended.Pixels : null;
        int? thickness = DwmGetWindowAttribute(_hwnd, 37, out int inset, sizeof(int)) == 0 ? inset : null;
        var clip = (GridHost.Clip as RectangleGeometry)?.Rect;
        return new(phase, utc, _clock.Elapsed.TotalMilliseconds, _interaction.Position, _interaction.TargetOpen,
            requestedDepth, _timer.Interval.TotalMilliseconds, _visible, IsWindowVisible(_hwnd),
            outer.Left, outer.Top, outer.Right - outer.Left, outer.Bottom - outer.Top,
            client.Right - client.Left, client.Bottom - client.Top,
            _drawerSlide.X, _drawerSlide.Y, WindowSurface.ActualWidth, WindowSurface.ActualHeight,
            MaterialCover.ActualWidth, MaterialCover.ActualHeight, clip?.Width ?? 0, clip?.Height ?? 0,
            dwmFrame, thickness);
    }

    private void StartRightOpeningTrace(int requestedDepth, bool fixedWidth)
    {
        if (_previewOnly || _edge != DrawerEdge.Right || !RevealTailTrace.TryStartSession()) return;
        _rightOpeningUsesFixedWidth = fixedWidth;
        _rightOpeningFrames = new(RightOpeningTraceMaximumFrames) { ReadRevealFrame("before-show", requestedDepth) };
    }

    private void RecordRightOpeningFrame(int requestedDepth)
    {
        if (_rightOpeningFrames is not { } frames) return;
        frames.Add(ReadRevealFrame(frames.Count == 1 ? "first-visible" : "opening", requestedDepth));
        if (frames.Count == RightOpeningTraceMaximumFrames || _interaction.Position == 1)
            FinishRightOpeningTrace();
    }

    private void FinishRightOpeningTrace()
    {
        if (_rightOpeningFrames is not { } frames) return;
        _rightOpeningFrames = null;
        var entry = new
        {
            build = IdlePerformance.Build, window = _hwnd.ToInt64(), edge = _edge.ToString(),
            material = _displayedMaterial.ToString(), scheme = DrawerPalette.ColorScheme.ToString(),
            dpi = _layoutDpi, scale = _presentationScale, fixedWidth = _rightOpeningUsesFixedWidth, frames
        };
        _ = System.Threading.Tasks.Task.Run(() =>
            PrototypeLog.Write("right-open-frames " + JsonSerializer.Serialize(entry)));
    }

    private void FinishRevealTail(string outcome)
    {
        if (_revealTail is not { } frames) return;
        _revealTail = null;
        PrototypeLog.Write("reveal-tail " + JsonSerializer.Serialize(new
        {
            build = IdlePerformance.Build, outcome, edge = _edge.ToString(),
            window = _hwnd.ToInt64(), surface = _previewOnly ? "layout-preview" : "drawer",
            material = _displayedMaterial.ToString(), scheme = DrawerPalette.ColorScheme.ToString(),
            dpi = _layoutDpi, scale = _presentationScale, frames
        }));
    }
}
