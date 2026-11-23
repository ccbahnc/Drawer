using Drawer.Core;
int checks = 0;
int failedSuites = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
    Console.WriteLine("PASS: " + name);
}
foreach (uint dpi in new uint[] { 96, 120, 144 })
{
    var g = DrawerGeometry.Create(new(0, 0, 1920, 1080), dpi);
    Check(g.Drawer.Width == 760 * dpi / 96 && g.Drawer.Height == 560 * dpi / 96, $"{dpi} DPI drawer size");
    Check(g.Drawer.Left + g.Drawer.Right == 1920 && g.Drawer.Top == 0, $"{dpi} DPI centered top");
    Check(g.Trigger.Width == 320 * dpi / 96 && g.Trigger.Height == 4 * dpi / 96, $"{dpi} DPI trigger size");
    Check(g.Trigger.Contains(g.Trigger.Left, 0) && g.Trigger.Contains(g.Trigger.Right - 1, g.Trigger.Bottom - 1)
        && !g.Trigger.Contains(g.Trigger.Right, 0) && !g.Trigger.Contains(g.Trigger.Left, g.Trigger.Bottom)
        && !g.Trigger.Contains(0, 0) && !g.Trigger.Contains(1919, 0), $"{dpi} DPI exclusive hit boundaries and corners");
    Console.WriteLine($"GEOMETRY {dpi}: drawer={g.Drawer}; trigger={g.Trigger}");
}
var shifted = DrawerGeometry.Create(new(-1920, -200, 0, 880), 120);
Check(shifted.Drawer.Top == -200 && shifted.Trigger.Contains(-960, -200), "negative monitor coordinates");
var small = DrawerGeometry.Create(new(0, 0, 640, 480), 144);
Check(small.Drawer.Left >= 0 && small.Drawer.Right <= 640 && small.Drawer.Height <= 432, "small display clamps");

var model = new DrawerInteraction();
double now = 0;
void Step(double ms, bool trigger = false, bool inside = false, bool allowed = true)
{
    for (int i = 0; i < (int)(ms / 10); i++)
    {
        now += 10;
        model.Update(now, .01, allowed, trigger, inside);
    }
}
Check(model.State == "Hidden" && model.Position == 0, "startup hidden");
Step(200, true);
Check(!model.TargetOpen, "dwell does not open before 200ms since entry");
Step(10, true);
Check(model.TargetOpen, "opens at 200ms since entry");
Step(500, inside: true);
Check(model.State == "Open", "settles fully open");
Step(2000, inside: true);
Check(model.State == "Open", "stays while inside");
Step(400);
Check(model.TargetOpen && model.State == "ClosePending", "400ms leave grace");
Step(10, inside: true);
Check(model.State == "Open", "return cancels close deadline");
Step(410);
Check(!model.TargetOpen && model.State == "Closing", "leave initiates close after grace");
Step(50);
double before = model.Position;
Step(10, inside: true);
Check(model.TargetOpen && Math.Abs(model.Position - before) < .15, "reverse from current position without jumping");
Step(600, inside: true);
Check(model.State == "Open", "reverse settles open");
Step(1000);
Check(model.State == "Hidden" && model.Position == 0, "close settles exactly hidden");
for (int i = 0; i < 50; i++) { Step(100, true); Step(50); }
Check(model.State == "Hidden", "50 short hovers never accumulate dwell");
Step(710, true);
Step(10, inside: true, allowed: false);
Check(model.State == "Hidden" && model.Position == 0, "desktop loss immediately suppresses visible window");
Step(2000, trigger: true, allowed: false);
Check(model.Position == 0, "blocked app cannot trigger");
Step(200, true);
Check(!model.TargetOpen, "desktop return starts a fresh dwell");
Step(510, true);
for (int i = 0; i < 100; i++) { Step(450); Step(150, inside: true); }
Step(600, inside: true);
Check(model.State == "Open", "100 close/reentry cycles never stick");
Step(1000);
Check(model.State == "Hidden", "stress ends hidden");
Console.WriteLine($"ALL {checks} CHECKS PASSED");
if (OperatingSystem.IsWindows()) RunSuite("StartupRegistrationChecks", StartupRegistrationChecks.Run);
RunSuite("InstallationLanguageChecks", InstallationLanguageChecks.Run);
RunSuite("StoreChecks", StoreChecks.Run);
if (OperatingSystem.IsWindows()) RunSuite("FolderPathImportChecks", FolderPathImportChecks.Run);
RunSuite("LayoutChecks", LayoutChecks.Run);
RunSuite("GroupChecks", GroupChecks.Run);
RunSuite("DesktopPolicyChecks", DesktopPolicyChecks.Run);
RunSuite("FullScreenRevealChecks", FullScreenRevealChecks.Run);
RunSuite("DragPreviewChecks", DragPreviewChecks.Run);
RunSuite("RevealChecks", RevealChecks.Run);
RunSuite("EdgeChecks", EdgeChecks.Run);
RunSuite("ContentChecks", ContentChecks.Run);
RunSuite("PreferenceChecks", PreferenceChecks.Run);
RunSuite("MultiDrawerChecks", MultiDrawerChecks.Run);
RunSuite("NavigationChecks", NavigationChecks.Run);
RunSuite("UnifiedChecks", UnifiedChecks.Run);
RunSuite("ItemMoveChecks", ItemMoveChecks.Run);

RunSuite("BatchChecks", BatchChecks.Run);
RunSuite("FixedGridChecks", FixedGridChecks.Run);
RunSuite("PageChecks", PageChecks.Run);
RunSuite("ExchangeChecks", ExchangeChecks.Run);

RunSuite("SelectionChecks", SelectionChecks.Run);
RunSuite("RecoveryChecks", RecoveryChecks.Run);
RunSuite("TopDropChecks", TopDropChecks.Run);
RunSuite("HistoryChecks", HistoryChecks.Run);
RunSuite("HistoryPresentationChecks", HistoryPresentationChecks.Run);
RunSuite("FolderInsertionChecks", FolderInsertionChecks.Run);

Environment.ExitCode = RegressionAssertions.Failed == 0 && failedSuites == 0 ? 0 : 1;
Console.WriteLine($"SUMMARY suitesFailed={failedSuites} failedAssertionsInContinuedSuites={RegressionAssertions.Failed}");

void RunSuite(string name, Action run)
{
    int previous = RegressionAssertions.Failed;
    try
    {
        run();
        if (RegressionAssertions.Failed == previous) Console.WriteLine("SUITE PASS " + name);
        else { failedSuites++; Console.WriteLine("SUITE FAIL " + name + ": see failed assertions above"); }
    }
    catch (Exception ex) { failedSuites++; Console.WriteLine($"SUITE FAIL {name}: {ex}"); }
}

static class RegressionAssertions
{
    private static readonly bool Continue = Array.IndexOf(Environment.GetCommandLineArgs(), "--continue-assertions") >= 0;
    public static int Failed { get; private set; }
    public static void Fail(string message)
    {
        if (!Continue) throw new Exception(message);
        Failed++;
        Console.WriteLine("ASSERTION FAIL: " + message);
    }
}


