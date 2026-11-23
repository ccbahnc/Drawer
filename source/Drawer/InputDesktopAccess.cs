using System.Runtime.InteropServices;

namespace Drawer;

// Borrow the UI thread desktop; never close it, switch desktops or seek extra rights.
internal sealed class InputDesktopAccess
{
    private readonly nint _desktop = GetThreadDesktop(GetCurrentThreadId());
    internal bool IsCurrent() => _desktop != 0 &&
        GetUserObjectInformationW(_desktop, 6, out int receivingInput, sizeof(int), out _) && receivingInput != 0;
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    // UOI_IO is a BOOL indicating whether this desktop currently receives input.
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformationW(nint obj, int index,
        out int info, uint bytes, out uint required);
}