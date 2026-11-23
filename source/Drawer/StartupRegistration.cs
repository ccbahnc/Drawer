using Drawer.Core;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Runtime.Versioning;

namespace Drawer;

internal enum StartupState { Disabled, Current, Outdated, Unrecognized, Unavailable }

[SupportedOSPlatform("windows")]
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EntryName = "Drawer (AA_drawer)";
    internal const string RefreshArgument = "--refresh-startup-registration";
    internal static bool IsInstallerArgument(string value) => value == RefreshArgument ||
        value is "--installer-startup-on" or "--installer-startup-off" or "--installer-startup-preserve" or "--installer-uninstall-startup";

    internal static int RunInstallerCommand(string command)
    {
        if (command == RefreshArgument) return RefreshForUpgrade() ? 0 : 2;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        var state = ReadState(key);
        if (command == "--installer-uninstall-startup")
        {
            // Outdated can belong to another installation. Only our exact path is ours to remove.
            object? value = key?.GetValue(EntryName);
            if (value is string text && string.Equals(text.Trim(), Quote(CurrentPath()), StringComparison.OrdinalIgnoreCase))
                key!.DeleteValue(EntryName, throwOnMissingValue: false);
            return 0;
        }
        if (state is StartupState.Unrecognized or StartupState.Unavailable) return 2;
        if (command == "--installer-startup-off")
        {
            if (state != StartupState.Disabled) key!.DeleteValue(EntryName, throwOnMissingValue: false);
            return 0;
        }
        if (command == "--installer-startup-on" ||
            command == "--installer-startup-preserve" && state is StartupState.Current or StartupState.Outdated)
        {
            using var writable = Registry.CurrentUser.CreateSubKey(RunKey);
            if (writable == null) return 3;
            WriteCurrent(writable);
        }
        return 0;
    }

    internal static StartupState GetState()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return ReadState(key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return StartupState.Unavailable;
        }
    }

    private static StartupState ReadState(RegistryKey? key)
    {
        object? value = key?.GetValue(EntryName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value == null) return StartupState.Disabled;
        if (value is not string command) return StartupState.Unrecognized;
        return Classify(command, key!.GetValueKind(EntryName) == RegistryValueKind.ExpandString,
            CurrentPath(), File.Exists, IsDrawerExecutable);
    }

    internal static StartupState Classify(string command, bool expandVariables, string currentPath,
        Func<string, bool> exists, Func<string, bool> isDrawer)
    {
        if (expandVariables) command = Environment.ExpandEnvironmentVariables(command);
        command = command.Trim();
        if (command.Length == 0 || command.Contains('\0')) return StartupState.Unrecognized;
        nint args = CommandLineToArgvW(command, out int count);
        if (args == 0) return StartupState.Unrecognized;
        try
        {
            if (count < 1 || Marshal.PtrToStringUni(Marshal.ReadIntPtr(args)) is not { } first ||
                !Path.IsPathFullyQualified(first) || first.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
                !string.Equals(Path.GetFileName(first), "Drawer.exe", StringComparison.OrdinalIgnoreCase))
                return StartupState.Unrecognized;

            string path = Path.GetFullPath(first);
            for (int i = 1; i < count; i++)
            {
                string? option = Marshal.PtrToStringUni(Marshal.ReadIntPtr(args, i * IntPtr.Size));
                if (option is not ("--settings" or "--idle-perf" or "--resize-perf" or "--resize-copy-ab" or "--reveal-trace"))
                    return StartupState.Unrecognized;
            }
            if (string.Equals(path, Path.GetFullPath(currentPath), StringComparison.OrdinalIgnoreCase) && exists(path))
                return count == 1 ? StartupState.Current : StartupState.Outdated;
            if (exists(path)) return isDrawer(path) ? StartupState.Outdated : StartupState.Unrecognized;

            // Legacy Drawer wrote this exact form under its private value name.
            // A missing path with a different command shape has no verifiable owner.
            return count == 1 && command == Quote(first) ? StartupState.Outdated : StartupState.Unrecognized;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return StartupState.Unrecognized;
        }
        finally { LocalFree(args); }
    }

    internal static StartupState Update(bool remove = false)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey)
            ?? throw new IOException(UiText.Get("StartupSettingsFailed"));
        var state = ReadState(key);
        if (state == StartupState.Unrecognized) return state;
        if (remove || state == StartupState.Current)
        {
            if (state != StartupState.Disabled) key.DeleteValue(EntryName, throwOnMissingValue: false);
            return StartupState.Disabled;
        }
        WriteCurrent(key);
        return StartupState.Current;
    }

    // Explicit entry point for a later installer, running as the user who owns HKCU.
    internal static bool RefreshForUpgrade()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        var state = ReadState(key);
        if (state == StartupState.Unrecognized) return false;
        if (state == StartupState.Outdated) WriteCurrent(key!);
        return true;
    }

    private static string CurrentPath() =>
        Environment.ProcessPath ?? throw new IOException(UiText.Get("ExecutablePathMissing"));

    private static bool IsDrawerExecutable(string path)
    {
        var version = FileVersionInfo.GetVersionInfo(path);
        return version.ProductName == "Drawer" && version.OriginalFilename is "Drawer.dll" or "Drawer.exe";
    }

    private static string Quote(string path) => "\"" + path + "\"";

    private static void WriteCurrent(RegistryKey key)
    {
        string command = Quote(CurrentPath());
        if (command.Length > 260) throw new IOException(UiText.Get("StartupPathTooLong"));
        key.SetValue(EntryName, command, RegistryValueKind.String);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
