using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace Drawer.Core;

[SupportedOSPlatform("windows")]
internal static class FileShellLink
{
    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");

    internal static void Create(string path, string target)
    {
        nint item = 0;
        object? link = null;
        try
        {
            Check(SHParseDisplayName(target, 0, out item, 0, out _), "SHParseDisplayName");
            link = NewLink();
            Check(((IShellLinkW)link).SetIDList(item), "IShellLinkW.SetIDList");
            ((IPersistFile)link).Save(path, false);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            throw new IOException($"IPersistFile/ShellLink (0x{ex.HResult:X8}): {ex.Message}", ex);
        }
        finally
        {
            if (link != null) Marshal.ReleaseComObject(link);
            Marshal.FreeCoTaskMem(item);
        }
    }

    internal static string? ReadTarget(string path) => ReadFileLink(path)?.Target;

    internal static (string Target, string Arguments, string Directory)? ReadFileLink(string path)
    {
        nint item = 0;
        object? link = null;
        try
        {
            link = NewLink();
            ((IPersistFile)link).Load(path, 0);
            var shellLink = (IShellLinkW)link;
            if (shellLink.GetIDList(out item) != 0 || item == 0) return null;
            var buffer = new StringBuilder(32768);
            if (!SHGetPathFromIDListEx(item, buffer, (uint)buffer.Capacity, 0) || buffer.Length == 0) return null;
            string pathFromShell = buffer.ToString();
            // The Shell may prepend the extended prefix to an ordinary local path.
            if (pathFromShell.StartsWith(@"\\?\", StringComparison.Ordinal) && pathFromShell.Length >= 7 &&
                char.IsAsciiLetter(pathFromShell[4]) && pathFromShell[5] == ':' && pathFromShell[6] == '\\')
            {
                string dosPath = pathFromShell[4..];
                if (File.Exists(dosPath) || Directory.Exists(dosPath)) pathFromShell = dosPath;
            }
            buffer.Clear();
            Check(shellLink.GetArguments(buffer, buffer.Capacity), "IShellLinkW.GetArguments");
            string arguments = buffer.ToString();
            buffer.Clear();
            Check(shellLink.GetWorkingDirectory(buffer, buffer.Capacity), "IShellLinkW.GetWorkingDirectory");
            return (pathFromShell, arguments, buffer.ToString());
        }
        catch (Exception ex) when (ex is COMException or IOException or ArgumentException) { return null; }
        finally
        {
            if (link != null) Marshal.ReleaseComObject(link);
            Marshal.FreeCoTaskMem(item);
        }
    }

    private static object NewLink() =>
        Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClass, throwOnError: true)!) ??
        throw new IOException(UiText.Get("ShortcutServiceFailed"));

    private static void Check(int result, string operation)
    {
        if (result < 0) throw new IOException($"{operation} (0x{result:X8})", result);
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, nint data, uint flags);
        [PreserveSig] int GetIDList(out nint item);
        [PreserveSig] int SetIDList(nint item);
        [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int count);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int count);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, nint context, out nint item, uint flags, out uint attributes);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListEx(nint item, [Out] StringBuilder path, uint capacity, uint flags);
}
