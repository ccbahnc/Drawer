using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;

namespace Drawer.Core;

internal readonly record struct ShortcutIdentity(string Target, string Arguments, string Directory)
{
    internal static bool TryWebAddress(string value, out Uri address)
    {
        value = value.Trim();
        if (value.Length <= 8192 && !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) &&
            Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
            parsed.Scheme is "http" or "https" && !string.IsNullOrEmpty(parsed.Host))
        {
            address = parsed;
            return true;
        }
        address = null!;
        return false;
    }

    internal static (string? Url, bool HasIcon) ReadInternetShortcut(string path)
    {
        string? url = null;
        bool hasIcon = false;
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) url ??= line[4..].Trim();
            else if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase)) hasIcon |= !string.IsNullOrWhiteSpace(line[9..]);
        }
        return (url, hasIcon);
    }

    internal static void CreateWebLink(string path, Uri address) =>
        File.WriteAllText(path, $"[InternetShortcut]\r\nURL={address.AbsoluteUri}\r\n", Encoding.Unicode);

    internal static void CreateFileLink(string path, string target)
    {
        target = Path.GetFullPath(target);
        if (!OperatingSystem.IsWindows()) throw new IOException(UiText.Get("ShortcutsUnsupported"));
        try
        {
            FileShellLink.Create(path, target);
        }
        catch (IOException ex) when (target.Length < 260 && !File.Exists(path))
        {
            try
            {
                WithShellLink(path, link => { link.TargetPath = target; link.Save(); return true; });
            }
            catch (Exception fallback) when (fallback is COMException or ArgumentException or IOException or
                Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                throw new IOException($"{UiText.Get("FileShortcutFailed")} {ex.Message}; WScript (0x{fallback.HResult:X8}): {fallback.Message}", fallback);
            }
        }
    }

    internal static (string? Target, string? IconFile) ReadIconSource(string path)
    {
        try
        {
            return WithShellLink<(string?, string?)>(path, link =>
            {
                string icon = (string?)link.IconLocation ?? "";
                int comma = icon.LastIndexOf(',');
                string location = (comma < 0 ? icon : icon[..comma]).Trim().Trim('"');
                if (location.Length > 0)
                {
                    // Other explicit resources (such as DLL indexes) still go through the Shell.
                    string file = Environment.ExpandEnvironmentVariables(location);
                    bool defaultIndex = comma < 0 || int.TryParse(icon[(comma + 1)..], out int index) && index == 0;
                    return (null, defaultIndex && Path.IsPathFullyQualified(file) &&
                        Path.GetExtension(file).Equals(".ico", StringComparison.OrdinalIgnoreCase) ? file : null);
                }
                string? target = ReadLinkTarget(path, (string?)link.TargetPath);
                return (target != null && (File.Exists(target) || System.IO.Directory.Exists(target)) ? target : null, null);
            });
        }
        catch (Exception ex) when (ex is COMException or IOException or ArgumentException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            string? target = OperatingSystem.IsWindows() ? FileShellLink.ReadTarget(path) : null;
            return (target != null && (File.Exists(target) || System.IO.Directory.Exists(target)) ? target : null, null);
        }
    }

    public static ShortcutIdentity Read(string path, string hash)
    {
        string extension = Path.GetExtension(path).ToUpperInvariant();
        try
        {
            if (extension == ".URL")
            {
                if (Uri.TryCreate(ReadInternetShortcut(path).Url, UriKind.Absolute, out var url))
                    return new(url.AbsoluteUri, "", "");
            }
            else if (OperatingSystem.IsWindows())
            {
                // WScript can refuse to open valid file links, including PIDL-only links.
                if (FileShellLink.ReadFileLink(path) is { } file)
                    return new(NormalizePath(file.Target), file.Arguments, NormalizePath(file.Directory));
                var identity = WithShellLink<ShortcutIdentity?>(path, link =>
                {
                    string? target = (string?)link.TargetPath;
                    return string.IsNullOrWhiteSpace(target) ? null : new ShortcutIdentity(NormalizePath(target),
                        (string?)link.Arguments ?? "", NormalizePath((string?)link.WorkingDirectory ?? ""));
                });
                if (identity is { } value) return value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            COMException or NotSupportedException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        return new(extension + ":sha256:" + hash.ToUpperInvariant(), "", "");
    }

    private static string? ReadLinkTarget(string path, string? target) =>
        OperatingSystem.IsWindows() && (string.IsNullOrEmpty(target) || target.Length >= 259)
            ? FileShellLink.ReadTarget(path) ?? target : target;

    private static T WithShellLink<T>(string path, Func<dynamic, T> action)
    {
        if (!OperatingSystem.IsWindows() || Type.GetTypeFromProgID("WScript.Shell") is not { } type)
            throw new IOException(UiText.Get("ShortcutsUnsupported"));
        object? shell = null, link = null;
        try
        {
            shell = Activator.CreateInstance(type) ?? throw new IOException(UiText.Get("ShortcutServiceFailed"));
            link = ((dynamic)shell).CreateShortcut(path);
            return link != null ? action(link) : throw new IOException(UiText.Get("ShortcutUnreadable"));
        }
        finally
        {
            if (link != null && Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
            if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }
    }

    private static string NormalizePath(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim());
        if (Path.IsPathFullyQualified(path)) path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return path.ToUpperInvariant();
    }
}
