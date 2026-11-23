using System;
using System.Diagnostics;
using Drawer.Core;
using static Drawer.NativeMethods;

namespace Drawer;

internal sealed class DesktopGate(nint drawer)
{
    private long _fullScreenCheckedAt;
    private PixelRect _lastScreen;
    private nint _lastForeground;
    private bool _fullScreen;
    private nint _lastTracedBlocker;
    private int _tracedBlockers;
    private bool OwnWindow(nint hwnd)
    {
        if (hwnd == 0) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        GetWindowThreadProcessId(drawer, out uint ownPid);
        return pid != 0 && pid == ownPid;
    }
    public bool ExplorerForeground()
    {
        using var sample = IdlePerformance.Measure(IdlePerformance.Work.ExplorerForeground);
        nint shell = GetShellWindow();
        if (shell == 0) return false;
        nint foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out uint pid);
        GetWindowThreadProcessId(shell, out uint shellPid);
        return pid == shellPid && (ClassName(foreground) == "CabinetWClass" || IsDesktop(foreground));
    }
    public bool IsDesktop(nint hwnd)
    {
        if (hwnd == 0) return false;
        nint shell = GetShellWindow();
        if (shell == 0) return false;
        nint root = GetAncestor(hwnd, 2);
        GetWindowThreadProcessId(root, out uint pid);
        GetWindowThreadProcessId(shell, out uint shellPid);
        string name = ClassName(root);
        return pid == shellPid && (name == "Progman" || name == "WorkerW");
    }

    public bool Allows(Point cursor, PixelRect screen, bool visible, out string reason)
    {
        using var sample = IdlePerformance.Measure(IdlePerformance.Work.DesktopGate);
        nint foreground = GetForegroundWindow();
        bool maximized = !OwnWindow(foreground) && !IsDesktop(foreground) && IsWindowVisible(foreground) && !IsIconic(foreground)
            && IsZoomed(foreground) && MonitorFromWindow(foreground, 2) == MonitorFromPoint(new Point(), 1);
        nint hit = GetAncestor(WindowFromPoint(cursor), 2);
        reason = DesktopPolicy.Evaluate(maximized, FullScreen(screen, foreground), IsDesktop(hit), OwnWindow(hit), visible);
        return DesktopPolicy.Allowed(reason);
    }

    private bool FullScreen(PixelRect screen, nint foreground)
    {
        long now = Stopwatch.GetTimestamp();
        if (_fullScreenCheckedAt != 0 && screen == _lastScreen && foreground == _lastForeground &&
            Stopwatch.GetElapsedTime(_fullScreenCheckedAt, now).TotalMilliseconds < 120) return _fullScreen;

        using var sample = IdlePerformance.Measure(IdlePerformance.Work.FullScreenEnumeration);
        // Fail closed for any visible, uncloaked non-shell window covering the primary monitor.
        nint blocker = 0;
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == drawer || !IsWindowVisible(hwnd) || IsIconic(hwnd) || IsDesktop(hwnd)) return true;
            if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            // Non-activating layered tool windows (e.g. the idle NVIDIA overlay)
            // can span the whole display even on the desktop. They are not full-screen apps.
            // If an overlay actually intercepts the trigger, WindowFromPoint still blocks it.
            const long overlayStyles = 0x08080080L; // NOACTIVATE | LAYERED | TOOLWINDOW
            if ((GetWindowLongPtrW(hwnd, -20).ToInt64() & overlayStyles) == overlayStyles) return true;
            if (GetWindowRect(hwnd, out Rect r) && r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom)
            {
                blocker = hwnd;
                return false;
            }
            return true;
        }, 0);
        _fullScreenCheckedAt = now;
        _lastScreen = screen;
        _lastForeground = foreground;
        if (RevealTailTrace.Enabled && blocker != _lastTracedBlocker)
        {
            _lastTracedBlocker = blocker;
            if (blocker != 0 && _tracedBlockers < 12)
            {
                _tracedBlockers++;
                GetWindowThreadProcessId(blocker, out uint pid);
                string process = "unknown";
                try
                {
                    using var owner = Process.GetProcessById((int)pid);
                    process = owner.ProcessName;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                PrototypeLog.Write($"desktop gate: fullscreen blocker hwnd={blocker} pid={pid} process={process} " +
                    $"class={ClassName(blocker)} exStyle=0x{GetWindowLongPtrW(blocker, -20).ToInt64():X}");
            }
        }
        return _fullScreen = blocker != 0;
    }

    // Sampling label only. The actual gate still uses FullScreen and pointer hit testing.
    internal string ForegroundForIdleSample(PixelRect screen)
    {
        var front = GetForegroundWindow();
        if (front == 0) return "no-foreground";
        if (IsDesktop(front)) return "desktop";
        if (OwnWindow(front)) return "drawer-owned";
        if (!IsWindowVisible(front) || IsIconic(front)) return "other";
        if (IsZoomed(front) && MonitorFromWindow(front, 2) == MonitorFromPoint(new Point(), 1)) return "maximized";
        if (screen.Width > 0 && GetWindowRect(front, out var bounds) && bounds.Left <= screen.Left &&
            bounds.Top <= screen.Top && bounds.Right >= screen.Right && bounds.Bottom >= screen.Bottom) return "fullscreen";
        return "other";
    }
}
