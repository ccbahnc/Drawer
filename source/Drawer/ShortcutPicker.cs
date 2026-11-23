using Drawer.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
namespace Drawer;
internal static class ShortcutPicker
{
    public static Task<IReadOnlyList<string>> PickAsync(nint owner)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { completion.SetResult(Pick(owner)); } catch (Exception ex) { completion.SetException(ex); } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int Size; public nint Owner, Instance; public string Filter; public nint CustomFilter;
        public int MaxCustomFilter, FilterIndex; public nint File; public int MaxFile; public nint FileTitle;
        public int MaxFileTitle; public string? InitialDir; public string Title; public uint Flags;
        public short FileOffset, FileExtension; public string? DefaultExtension; public nint CustomData, Hook;
        public string? Template; public nint Reserved; public int ReservedValue; public uint FlagsEx;
    }
    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetOpenFileName(ref OpenFileName dialog);
    [DllImport("comdlg32.dll")] private static extern uint CommDlgExtendedError();
    public static IReadOnlyList<string> Pick(nint owner)
    {
        const int capacity = 65536;
        nint buffer = Marshal.AllocHGlobal(capacity * 2);
        try
        {
            Marshal.Copy(new char[capacity], 0, buffer, capacity);
            var dialog = new OpenFileName { Size = Marshal.SizeOf<OpenFileName>(), Owner = owner,
                Filter = UiText.Get("PickerFilter"), File = buffer, MaxFile = capacity,
                Title = UiText.Get("PickerTitle"),
                // NODEREFERENCELINKS returns the shortcut rather than its executable.
                Flags = 0x200 | 0x80000 | 0x1000 | 0x8 | 0x100000 | 0x2000000 };
            if (!GetOpenFileName(ref dialog))
            { uint error = CommDlgExtendedError(); if (error != 0) throw new Win32Exception((int)error, UiText.Get("PickerError")); return Array.Empty<string>(); }
            var parts = Marshal.PtrToStringUni(buffer, capacity)!.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1) return parts;
            var paths = new List<string>();
            for (int i = 1; i < parts.Length; i++) paths.Add(Path.Combine(parts[0], parts[i]));
            return paths;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
