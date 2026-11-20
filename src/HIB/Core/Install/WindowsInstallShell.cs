// H.I.B — the Windows side of install/uninstall: HKCU registry, .lnk shortcuts (the same IShellLinkW
// code as Explorer "Send to"), closing running copies and the detached folder removal.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using HIB.Services;
using Microsoft.Win32;

namespace HIB.Core.Install;

public sealed class WindowsInstallShell : IInstallShell
{
    public string? ReadUninstallValue(string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InstallLayout.UninstallKeyPath);
            return key?.GetValue(name)?.ToString();
        }
        catch { return null; }
    }

    public void WriteUninstallEntry(IReadOnlyList<UninstallValue> values)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { Write(values); }
        catch (Exception ex) { throw new InstallException("Couldn't add H.I.B to Settings › Apps: " + ex.Message, ex); }
    }

    [SupportedOSPlatform("windows")]
    static void Write(IReadOnlyList<UninstallValue> values)
    {
        using var key = Registry.CurrentUser.CreateSubKey(InstallLayout.UninstallKeyPath, writable: true);
        foreach (var v in values)
            key.SetValue(v.Name, v.Value, v.IsDword ? RegistryValueKind.DWord : RegistryValueKind.String);
    }

    public void DeleteUninstallEntry()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { Registry.CurrentUser.DeleteSubKeyTree(InstallLayout.UninstallKeyPath, throwOnMissingSubKey: false); } catch { }
    }

    public string? ReadRunEntry()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InstallLayout.RunKeyPath);
            return key?.GetValue(InstallLayout.RunValueName) as string;
        }
        catch { return null; }
    }

    public void WriteRunEntry(string command)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(InstallLayout.RunKeyPath, writable: true);
            key.SetValue(InstallLayout.RunValueName, command);
        }
        catch { }
    }

    public void DeleteRunEntry()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InstallLayout.RunKeyPath, writable: true);
            key?.DeleteValue(InstallLayout.RunValueName, throwOnMissingValue: false);
        }
        catch { }
    }

    public void CreateShortcut(string path, string target, string arguments, string description)
        => SendToShortcuts.CreateShortcut(path, target, arguments, description);

    public void RetargetSendToShortcuts(string exePath) => SendToShortcuts.Retarget(exePath);

    public void RemoveSendToShortcuts() => SendToShortcuts.RemoveAll();

    /// <summary>
    /// 1. "quit" over the --send-to pipe (a running H.I.B saves and exits cleanly); 2. wait up to 8 s;
    /// 3. end any H.I.B still running (older copies don't know "quit"), from any folder; 4. end adb/scrcpy
    /// started from the install folder so their files can be replaced or removed.
    /// </summary>
    public void CloseRunningCopies(string installDir, int selfPid)
    {
        if (!OperatingSystem.IsWindows()) return;
        var asked = SendToPipe.TrySendQuit();
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(asked ? 8 : 1);
        while (RunningHib(selfPid).Count > 0 && DateTime.UtcNow < until)
            System.Threading.Thread.Sleep(200);

        foreach (var p in RunningHib(selfPid)) End(p);
        foreach (var name in new[] { "scrcpy", "adb" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (InstallLayout.IsInside(p.MainModule?.FileName, installDir)) End(p);
                    else p.Dispose();
                }
                catch { p.Dispose(); }
            }
        }
    }

    /// <summary>Other H.I.B processes (HIB.exe from any folder; product name H.I.B when the exe has one).</summary>
    static List<Process> RunningHib(int selfPid)
    {
        var list = new List<Process>();
        foreach (var p in Process.GetProcessesByName("HIB"))
        {
            if (p.Id == selfPid) { p.Dispose(); continue; }
            try
            {
                var product = p.MainModule?.FileVersionInfo.ProductName;
                if (string.IsNullOrEmpty(product) || product == InstallLayout.AppName) { list.Add(p); continue; }
            }
            catch { list.Add(p); continue; } // can't inspect: it is named HIB.exe and runs as this user
            p.Dispose();
        }
        return list;
    }

    static void End(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
        }
        catch { }
        finally { p.Dispose(); }
    }

    public void ScheduleFolderRemoval(string dir)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            Process.Start(new ProcessStartInfo(Path.Combine(system, "cmd.exe"), SelfDelete.CmdArguments(dir))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath(),
            })?.Dispose();
        }
        catch { }
    }
}
