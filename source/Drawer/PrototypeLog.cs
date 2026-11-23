using System;
using System.IO;

namespace Drawer;

internal static class PrototypeLog
{
    private static readonly string? FilePath = FindPath();
    private static readonly object Sync = new();
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private const long MaximumBytes = 2 * 1024 * 1024;
    private static string? FindPath()
    {
        try
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Drawer.sln")))
                {
                    string logs = Path.Combine(dir.FullName, "temp", "logs");
                    Directory.CreateDirectory(logs);
                    return Path.Combine(logs, $"drawer-{Environment.ProcessId}.log");
                }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) return null;
            string logs = Path.Combine(local, "Drawer", "logs");
            Directory.CreateDirectory(logs);
            return Path.Combine(logs, "drawer.log");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
    public static void Write(string message)
    {
        if (FilePath == null) return;
        try
        {
            if (Home.Length > 0) message = message.Replace(Home, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            lock (Sync)
            {
                var log = new FileInfo(FilePath);
                if (log.Exists && log.Length >= MaximumBytes) File.Move(FilePath, FilePath + ".previous", overwrite: true);
                using var writer = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
                writer.WriteLine($"{DateTimeOffset.Now:O} {message}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
