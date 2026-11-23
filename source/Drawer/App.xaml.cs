using Microsoft.UI.Xaml;
using System;
using System.Threading;
using Drawer.Core;

namespace Drawer;

public partial class App : Application
{
    private Window? _window;
    private Mutex? _instance;
    private Mutex? _dataInstance;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_,e) => PrototypeLog.Write("unhandled: "+e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Environment.GetCommandLineArgs() is [_, var installerArgument] && StartupRegistration.IsInstallerArgument(installerArgument))
        {
            int result;
            try { result = StartupRegistration.RunInstallerCommand(installerArgument); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                PrototypeLog.Write($"startup registration refresh failed: 0x{ex.HResult:X8}");
                result = 3;
            }
            Environment.Exit(result);
            return;
        }
        if (Mutex.TryOpenExisting(@"Global\Drawer.Installation", out var installation))
        {
            installation.Dispose();
            NativeMethods.MessageBoxW(0, UiText.Get("InstallationInProgress"), "Drawer", 0x30);
            Exit();
            return;
        }
        _instance = new Mutex(true, @"Local\Drawer.TopPrototype", out bool first);
        if (!first)
        {
            Exit();
            return;
        }
        // Legacy builds only have a session-local mutex. Refuse to share their
        // data from a second session instead of assuming they know our new lock.
        try
        {
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("Drawer"))
            {
                using (process)
                    if (process.Id != current.Id && process.SessionId != current.SessionId)
                        throw new InvalidOperationException(UiText.Get("OtherSessionRunning"));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            NativeMethods.MessageBoxW(0, UiText.Get("OtherSessionRunning"), "Drawer", 0x30);
            Exit();
            return;
        }
        _dataInstance = new Mutex(true, DataInstanceName(), out bool firstDataInstance);
        if (!firstDataInstance)
        {
            NativeMethods.MessageBoxW(0, UiText.Get("OtherSessionRunning"), "Drawer", 0x30);
            Exit();
            return;
        }
        try
        {
            PrototypeLog.Write($"startup: build={IdlePerformance.Build} profile={IdlePerformance.Profile}");
            IdlePerformance.Initialize();
            ResizePerformance.Initialize();
            DrawerPalette.Initialize();
            _window = new MainWindow();
        }
        catch (Exception ex)
        {
            PrototypeLog.Write("startup: " + ex);
            if (ex is DrawerStartupDataException)
            {
                NativeMethods.MessageBoxW(0, UiText.Get("StartupRecoveryFailed"), "Drawer", 0x10);
                Exit();
                return;
            }
            throw;
        }
        // No Activate: creating the hidden drawer must never steal foreground focus.
    }

    internal static string DataInstanceName() =>
        @"Global\Drawer.Data." + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
}

internal sealed class DrawerStartupDataException(Exception inner) : Exception("Could not open the drawer catalog.", inner);
