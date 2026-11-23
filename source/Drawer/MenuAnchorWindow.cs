using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Drawer.NativeMethods;

namespace Drawer;

// A tray click needs a XAML root even when every drawer window is hidden.
internal sealed class MenuAnchorWindow : Window
{
    private readonly Grid _anchor = new() { RequestedTheme = DrawerPalette.Theme };
    private readonly nint _hwnd;
    private readonly SubclassProc _subclass;
    private readonly WindowsHookProc _mouseObserver;
    private nint _mouseHook;
    private Action? _dismiss;
    private Func<Point, bool>? _isTrayIcon;

    internal MenuAnchorWindow()
    {
        Content = _anchor;
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = presenter.IsMinimizable = presenter.IsMaximizable = false;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        long style = GetWindowLongPtrW(_hwnd, -20).ToInt64();
        SetWindowLongPtrW(_hwnd, -20, (nint)((style | 0x80L) & ~0x40000L)); // TOOLWINDOW, no APPWINDOW
        DrawerPalette.SetFrameBorder(_hwnd, DrawerPalette.NoDwmBorder);
        _subclass = OnMessage;
        _mouseObserver = ObserveMouse;
        SetWindowSubclass(_hwnd, _subclass, 1, 0);
        Closed += (_, _) =>
        {
            _dismiss = null;
            _isTrayIcon = null;
            if (_mouseHook != 0) UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
            RemoveWindowSubclass(_hwnd, _subclass, 1);
        };
    }

    internal void ObserveDismissal(Action dismiss, Func<Point, bool> isTrayIcon)
    {
        _dismiss = dismiss;
        _isTrayIcon = isTrayIcon;
        _mouseHook = SetWindowsHookExW(14, _mouseObserver, GetModuleHandleW(null), 0); // WH_MOUSE_LL
        if (_mouseHook == 0) PrototypeLog.Write($"tray: outside click observer unavailable ({Marshal.GetLastWin32Error()})");
    }

    private nint ObserveMouse(int code, nuint message, nint data)
    {
        if (code == 0 && message is 0x0201 or 0x0204 or 0x0207 or 0x020B && data != 0)
        {
            var point = Marshal.PtrToStructure<LowLevelMouseData>(data).Position;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_dismiss == null || _isTrayIcon?.Invoke(point) == true) return;
                nint window = WindowFromPoint(point);
                nint root = GetAncestor(window, 2); // GA_ROOT
                GetWindowThreadProcessId(root, out uint process);
                // The menu and each submenu can live in separate WinUI popup HWNDs.
                bool inFlyout = root != 0 && root != _hwnd && process == (uint)Environment.ProcessId &&
                    (GetAncestor(root, 3) == _hwnd || ClassName(root).Contains("Popup", StringComparison.OrdinalIgnoreCase));
                if (!inFlyout)
                {
                    PrototypeLog.Write("tray: dismiss outside-click");
                    _dismiss.Invoke();
                }
            });
        }
        return CallNextHookEx(0, code, message, data);
    }

    internal async Task<Grid> Open(Point point)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Loaded(object sender, RoutedEventArgs args) => ready.TrySetResult();
        _anchor.Loaded += Loaded;
        try
        {
            SetWindowPos(_hwnd, (nint)(-1), point.X, point.Y, 1, 1, 0x0010);
            Activate();
            if (!_anchor.IsLoaded) await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
            bool activated = SetForegroundWindow(_hwnd);
            PrototypeLog.Write($"tray: anchor ready hwnd={_hwnd} foreground={GetForegroundWindow()} foregroundRequested={activated}");
            return _anchor;
        }
        finally { _anchor.Loaded -= Loaded; }
    }

    private static nint OnMessage(nint hwnd, uint message, nuint word, nint data, nuint id, nuint context)
    {
        if (message == 0x0083) return 0; // WM_NCCALCSIZE
        var result = DefSubclassProc(hwnd, message, word, data);
        if (message == 0x0024 && data != 0) // WM_GETMINMAXINFO
        {
            var sizes = Marshal.PtrToStructure<MinMaxInfo>(data);
            sizes.MinTrackSize = new Point { X = 1, Y = 1 };
            Marshal.StructureToPtr(sizes, data, false);
        }
        return result;
    }
}
