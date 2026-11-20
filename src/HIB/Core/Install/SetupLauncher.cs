using System;
using System.Diagnostics;
using HIB.Core.Platform;

namespace HIB.Core.Install;

/// <summary>Entry points: quiet Setup/uninstall from Program, and the Settings "Install on this PC" / "Uninstall" rows.</summary>
public static class SetupLauncher
{
    /// <summary><c>--install --quiet</c> / <c>--uninstall --quiet</c> (QuietUninstallString): no window, keeps data. Returns the exit code.</summary>
    public static int RunQuiet(SetupRequest request)
    {
        var exe = PlatformShell.ExecutablePath;
        try
        {
            var installer = Installer.ForThisPc();
            if (request.Action == SetupAction.Uninstall) installer.Uninstall(deleteData: false, runningExe: exe);
            else installer.Install(InstallSource.FromRunningApp(exe), request.DesktopShortcut);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>What Settings shows: an unzipped copy offers "Install H.I.B on this PC"; the installed copy offers "Uninstall".</summary>
    public static (bool OfferInstall, bool IsInstalled, string InstallDir) Status()
    {
        // Dev aid for screenshots: HIB_INSTALL_ROW=portable|installed shows a row on any OS.
        switch (Environment.GetEnvironmentVariable("HIB_INSTALL_ROW"))
        {
            case "portable": return (true, false, @"C:\Users\you\AppData\Local\Programs\H.I.B");
            case "installed": return (false, true, @"C:\Users\you\AppData\Local\Programs\H.I.B");
        }
        // Windows only, and never for test instances (HIB_PORT).
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("HIB_PORT") is not null) return (false, false, "");
        try
        {
            var installer = Installer.ForThisPc();
            var dir = installer.ResolveInstallDir();
            var installed = InstallLayout.IsInstalledCopy(PlatformShell.ExecutablePath, dir);
            return (!installed, installed, dir);
        }
        catch { return (false, false, ""); }
    }

    /// <summary>Opens the Setup window from this copy (it asks this running H.I.B to quit when it starts copying).</summary>
    public static bool StartInstall() => Start("--install");

    /// <summary>Opens the "Uninstall H.I.B?" window.</summary>
    public static bool StartUninstall() => Start("--uninstall");

    static bool Start(string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(PlatformShell.ExecutablePath, args) { UseShellExecute = false })?.Dispose();
            return true;
        }
        catch { return false; }
    }
}
