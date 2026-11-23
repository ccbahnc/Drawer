using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Drawer;

// Drawer HWNDs share class state. Calls are serialized by the WinUI thread.
internal static class WindowClassBackground
{
    private const int ClassAtom = -32, BackgroundBrush = -10;
    private static readonly Dictionary<nint, ClassState> Classes = new();
    private static readonly Dictionary<nint, ClassState> Windows = new();
    private static readonly Dictionary<int, nint> Brushes = new();
    private static bool _restoreUncertain;

    private sealed class ClassState(nint atom, nint original)
    {
        public nint Atom { get; } = atom;
        public nint Original { get; } = original;
        public nint Current { get; set; } = original;
        public int WindowCount { get; set; }
    }

    internal static bool Apply(nint hwnd, int? color)
    {
        if (!Windows.TryGetValue(hwnd, out var state))
        {
            nint atom = GetClassLongPtrW(hwnd, ClassAtom);
            if (atom == 0) return false;
            if (!Classes.TryGetValue(atom, out state))
            {
                nint original = GetClassLongPtrW(hwnd, BackgroundBrush);
                if (original == 0 && Marshal.GetLastPInvokeError() != 0) return false;
                Classes.Add(atom, state = new(atom, original));
            }
            Windows.Add(hwnd, state);
            state.WindowCount++;
        }

        nint desired = 0;
        if (color is int colorRef && !Brushes.TryGetValue(colorRef, out desired))
        {
            desired = CreateSolidBrush(colorRef);
            if (desired == 0) return false;
            Brushes.Add(colorRef, desired);
        }

        if (state.Current != desired && !SetBrush(hwnd, desired)) return false;
        state.Current = desired;
        return true;
    }

    internal static void Release(nint hwnd)
    {
        if (!Windows.Remove(hwnd, out var state) || --state.WindowCount != 0) return;
        Classes.Remove(state.Atom);
        if (state.Current != state.Original && !SetBrush(hwnd, state.Original))
        {
            // Keep the bounded cache alive if Windows could still reference a brush.
            _restoreUncertain = true;
            PrototypeLog.Write("window class background: restoration unavailable");
        }
        if (Classes.Count != 0 || _restoreUncertain) return;
        foreach (nint brush in Brushes.Values) DeleteObject(brush);
        Brushes.Clear();
    }

    internal static uint GdiCount()
    {
        using var process = Process.GetCurrentProcess();
        return GetGuiResources(process.Handle, 0);
    }

    private static bool SetBrush(nint hwnd, nint brush) =>
        SetClassLongPtrW(hwnd, BackgroundBrush, brush) != 0 || Marshal.GetLastPInvokeError() == 0;

    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(int color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetClassLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClassLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint flags);
}
