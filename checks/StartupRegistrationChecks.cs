using Drawer;
using System.Runtime.Versioning;

[SupportedOSPlatform("windows")]
internal static class StartupRegistrationChecks
{
    public static void Run()
    {
        const string current = @"C:\Program Files\Drawer\Drawer.exe";
        const string old = @"C:\Drawer-v66\Drawer.exe";
        const string absent = @"C:\Moved Drawer\Drawer.exe";
        const string foreign = @"C:\Another Product\Drawer.exe";
        bool Exists(string path) => path == current || path == old || path == foreign;
        string Quote(string path) => $"\"{path}\"";
        void Check(string command, StartupState wanted, string description, bool expand = false)
        {
            var actual = StartupRegistration.Classify(command, expand, current, Exists, path => path == old);
            if (actual != wanted) throw new Exception($"startup: {description}. Expected {wanted}, got {actual}.");
            Console.WriteLine("PASS startup: " + description);
        }

        Check(Quote(current), StartupState.Current, "quoted path with spaces is current");
        Check(Quote(current) + " \"--settings\"", StartupState.Outdated, "recognized extra argument can be removed");
        Check(Quote(old), StartupState.Outdated, "owned previous binary can be refreshed");
        Check(old, StartupState.Outdated, "unquoted unambiguous path can be refreshed");
        Check(Quote(absent), StartupState.Outdated, "missing legacy launcher can be refreshed");
        Check(Quote(absent) + " --settings", StartupState.Unrecognized, "missing command with extra arguments is preserved");
        Check(Quote(foreign), StartupState.Unrecognized, "other executable with the same file name is preserved");
        Check(current, StartupState.Unrecognized, "ambiguous unquoted path with spaces is rejected");
        Check(Quote(current) + " \"unknown option\"", StartupState.Unrecognized, "unrecognized argument is preserved");
        Check("   ", StartupState.Unrecognized, "empty command is preserved");
        Check(Quote(@"C:\Windows\System32\notepad.exe") + " " + Quote(current), StartupState.Unrecognized, "wrapper command is preserved");
        string? previous = Environment.GetEnvironmentVariable("DRAWER_STARTUP_TEST_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("DRAWER_STARTUP_TEST_ROOT", @"C:\Program Files\Drawer");
            Check(Quote(@"%DRAWER_STARTUP_TEST_ROOT%\Drawer.exe"), StartupState.Current,
                "expandable registry path resolves to the current build", expand: true);
        }
        finally { Environment.SetEnvironmentVariable("DRAWER_STARTUP_TEST_ROOT", previous); }
        Console.WriteLine("ALL 12 STARTUP COMMAND CHECKS PASSED");
    }
}
