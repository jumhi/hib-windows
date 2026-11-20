using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace HIB.Core.Platform;

/// <summary>Opening files/folders/URLs in the OS shell.</summary>
public static class PlatformShell
{
    public static void Open(string pathOrUrl)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", Quote(pathOrUrl));
            else
                Process.Start("xdg-open", Quote(pathOrUrl));
        }
        catch { }
    }

    public static void RevealInFolder(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", $"-R {Quote(path)}");
            else
                Process.Start("xdg-open", Quote(Path.GetDirectoryName(path) ?? path));
        }
        catch { }
    }

    public static bool IsSafeUrl(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "HIB.exe");
}

/// <summary>"Start with Windows" via HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
public static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "H.I.B";

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static bool IsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            return GetWindows();
        }
    }

    [SupportedOSPlatform("windows")]
    static bool GetWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch { return false; }
    }

    public static bool Set(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return false;
        return SetWindows(enabled);
    }

    [SupportedOSPlatform("windows")]
    static bool SetWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (enabled) key.SetValue(ValueName, HIB.Core.Install.InstallLayout.RunCommand(PlatformShell.ExecutablePath));
            else key.DeleteValue(ValueName, false);
            return true;
        }
        catch { return false; }
    }
}

/// <summary>
/// Windows Firewall: adds inbound allow rules for H.I.B (TCP 6996 WebSocket, UDP 8889 presence,
/// UDP 5353 mDNS) through an elevated netsh. Only runs when the user clicks the button (UAC prompt).
/// </summary>
public static class FirewallHelper
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    public static string BuildCommand(int port)
    {
        var exe = PlatformShell.ExecutablePath;
        return string.Join(" & ",
            "netsh advfirewall firewall delete rule name=\"H.I.B\"",
            $"netsh advfirewall firewall add rule name=\"H.I.B\" dir=in action=allow program=\"{exe}\" enable=yes profile=private,domain",
            $"netsh advfirewall firewall add rule name=\"H.I.B\" dir=in action=allow protocol=TCP localport={port} enable=yes profile=private,domain",
            "netsh advfirewall firewall add rule name=\"H.I.B\" dir=in action=allow protocol=UDP localport=8889 enable=yes profile=private,domain",
            "netsh advfirewall firewall add rule name=\"H.I.B\" dir=in action=allow protocol=UDP localport=5353 enable=yes profile=private,domain");
    }

    /// <summary>Shows a UAC prompt; returns true if netsh ran and exited 0.</summary>
    public static bool AddRules(int port)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + BuildCommand(port))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p is null) return false;
            p.WaitForExit(30_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; } // user declined UAC
    }
}

/// <summary>Cheap clipboard change detection on Windows.</summary>
public static partial class NativeClipboard
{
    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();

    public static uint? SequenceNumber
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return null;
            try { return GetClipboardSequenceNumber(); } catch { return null; }
        }
    }
}
