// H.I.B — install / uninstall, the pure parts (no registry, no COM, no processes): where things go,
// what the Settings › Apps entry says, how a launch is recognised as Setup, which files are copied and
// whether a Setup is a fresh install, an update or a reinstall. Tested in tests/HIB.FakePhone/InstallerTests.cs.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace HIB.Core.Install;

/// <summary>What this launch should do.</summary>
public enum SetupAction { None, Install, Uninstall }

/// <summary>A Setup / uninstall launch: <c>H.I.B-Setup.exe</c>, <c>HIB.exe --install</c> or <c>HIB.exe --uninstall [--quiet]</c>.</summary>
public sealed record SetupRequest(SetupAction Action, bool Quiet = false, bool DesktopShortcut = false)
{
    public static SetupRequest None { get; } = new(SetupAction.None);

    /// <summary>
    /// <c>--uninstall</c> wins, then <c>--install</c>, then an exe whose name contains "setup"
    /// (H.I.B-Setup.exe, "H.I.B-Setup (1).exe" from a browser). <c>--send-to</c> is never Setup.
    /// </summary>
    public static SetupRequest Detect(string? exePath, IReadOnlyList<string> args)
    {
        bool Has(string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        var quiet = Has("--quiet") || Has("/quiet") || Has("/S");
        var desktop = Has("--desktop-shortcut");
        if (Has("--uninstall")) return new SetupRequest(SetupAction.Uninstall, quiet);
        if (Has("--install")) return new SetupRequest(SetupAction.Install, quiet, desktop);
        if (Has("--send-to")) return None;
        var name = string.IsNullOrEmpty(exePath) ? "" : Path.GetFileNameWithoutExtension(exePath.Replace('\\', '/').Split('/').Last());
        if (name.Contains("setup", StringComparison.OrdinalIgnoreCase)) return new SetupRequest(SetupAction.Install, quiet, desktop);
        return None;
    }
}

/// <summary>Names and places. Everything is per user (no admin rights).</summary>
public static class InstallLayout
{
    public const string AppName = "H.I.B";
    public const string ExeName = "HIB.exe";
    public const string Publisher = "Haitham Hamid";
    public const string ShortcutName = "H.I.B.lnk";
    public const string ToolsFolder = "tools";
    /// <summary>Manifest resource holding tools/win-x64 as a zip (only in the Setup build, -p:HibSetup=true).</summary>
    public const string ToolsResource = "HIB.Tools.zip";
    public const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\H.I.B";
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = "H.I.B";

    /// <summary>%LOCALAPPDATA%\Programs\H.I.B (where per-user apps like VS Code and Discord go).</summary>
    public static string InstallDir(string localAppData) => Path.Combine(localAppData, "Programs", AppName);

    public static string InstalledExe(string installDir) => Path.Combine(installDir, ExeName);

    /// <summary>%APPDATA%\Microsoft\Windows\Start Menu\Programs\H.I.B.lnk</summary>
    public static string StartMenuShortcut(string startMenuPrograms) => Path.Combine(startMenuPrograms, ShortcutName);

    public static string DesktopShortcut(string desktop) => Path.Combine(desktop, ShortcutName);

    /// <summary>The "Start with Windows" command (HKCU Run), same as AutoStart writes.</summary>
    public static string RunCommand(string exePath) => $"\"{exePath}\" --minimized";

    public static string UninstallCommand(string exePath, bool quiet = false) => $"\"{exePath}\" --uninstall" + (quiet ? " --quiet" : "");

    /// <summary>"4.1.0" from the assembly version.</summary>
    public static string AppVersion => typeof(InstallLayout).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Windows paths compare case-insensitively, with either slash and no trailing separator.</summary>
    public static string Normalize(string path)
    {
        var p = path.Replace('/', '\\').TrimEnd('\\');
        return p.Length == 2 && p[1] == ':' ? p + "\\" : p;
    }

    public static bool SamePath(string? a, string? b)
        => a is not null && b is not null && string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is <paramref name="dir"/> or anything below it.</summary>
    public static bool IsInside(string? path, string? dir)
    {
        if (path is null || dir is null) return false;
        var p = Normalize(path);
        var d = Normalize(dir);
        return string.Equals(p, d, StringComparison.OrdinalIgnoreCase)
               || p.StartsWith(d.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The running exe is the installed copy (lives directly in the install folder).</summary>
    public static bool IsInstalledCopy(string? exePath, string installDir)
        => exePath is not null && SamePath(Path.GetDirectoryName(exePath.Replace('\\', Path.DirectorySeparatorChar)), installDir);

    /// <summary>
    /// The uninstaller deletes a folder recursively, so only ever H.I.B's own: the default install folder,
    /// or a folder named "H.I.B" that holds HIB.exe and isn't a drive root or a well-known folder.
    /// </summary>
    public static bool IsSafeToRemove(string? dir, string defaultInstallDir, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        var d = Normalize(dir);
        if (d.Length <= 3) return false; // C:\ or \\
        var name = d.Split('\\').Last();
        var looksLikeOurs = SamePath(d, defaultInstallDir) || string.Equals(name, AppName, StringComparison.OrdinalIgnoreCase);
        return looksLikeOurs && fileExists(Path.Combine(dir, ExeName));
    }
}

/// <summary>One value under HKCU\…\Uninstall\H.I.B.</summary>
public sealed record UninstallValue(string Name, object Value)
{
    public bool IsDword => Value is int;
}

/// <summary>What Settings › Apps (and Control Panel › Programs) shows for H.I.B.</summary>
public static class UninstallEntry
{
    public static IReadOnlyList<UninstallValue> Build(string installDir, string version, long sizeBytes, DateTime installDate)
    {
        var exe = InstallLayout.InstalledExe(installDir);
        return new UninstallValue[]
        {
            new("DisplayName", InstallLayout.AppName),
            new("DisplayVersion", version),
            new("Publisher", InstallLayout.Publisher),
            new("DisplayIcon", exe + ",0"),
            new("InstallLocation", installDir),
            new("UninstallString", InstallLayout.UninstallCommand(exe)),
            new("QuietUninstallString", InstallLayout.UninstallCommand(exe, quiet: true)),
            new("EstimatedSize", (int)Math.Clamp((sizeBytes + 1023) / 1024, 0, int.MaxValue)),
            new("NoModify", 1),
            new("NoRepair", 1),
            new("InstallDate", installDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)),
            new("Comments", "Your phone and your computers, together."),
        };
    }
}

/// <summary>What running Setup over this PC means.</summary>
public enum InstallKind { Fresh, Upgrade, Reinstall, Downgrade }

public static class UpgradeDecision
{
    /// <summary>Compares the installed DisplayVersion with this Setup's version. No/unreadable install → Fresh.</summary>
    public static InstallKind Decide(string? installedVersion, bool installedExeExists, string setupVersion)
    {
        if (!installedExeExists || string.IsNullOrWhiteSpace(installedVersion) || !Version.TryParse(installedVersion, out var have))
            return installedExeExists ? InstallKind.Reinstall : InstallKind.Fresh;
        if (!Version.TryParse(setupVersion, out var mine)) return InstallKind.Reinstall;
        var c = Normalize(mine).CompareTo(Normalize(have));
        return c > 0 ? InstallKind.Upgrade : c < 0 ? InstallKind.Downgrade : InstallKind.Reinstall;
    }

    static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    public static string Title(InstallKind kind, string? installedVersion, string setupVersion) => kind switch
    {
        InstallKind.Upgrade => $"Update H.I.B to {setupVersion}",
        InstallKind.Reinstall => "Reinstall H.I.B",
        InstallKind.Downgrade => $"Replace H.I.B {installedVersion} with {setupVersion}",
        _ => "Install H.I.B",
    };

    public static string Button(InstallKind kind) => kind switch
    {
        InstallKind.Upgrade => "Update",
        InstallKind.Reinstall => "Reinstall",
        InstallKind.Downgrade => "Install anyway",
        _ => "Install",
    };
}

/// <summary>Which files an install writes.</summary>
public static class InstallFiles
{
    const string BadChars = "<>:\"|?*";

    /// <summary>
    /// Tool files as relative paths with '/' separators: no folders, nothing rooted, no "..", no empty
    /// segments (a zip can't write outside tools\). Duplicates (case-insensitive) collapse.
    /// </summary>
    public static IReadOnlyList<string> SafeToolPaths(IEnumerable<string> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var raw in entries)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var p = raw.Replace('\\', '/');
            if (p.EndsWith('/')) continue;                         // a folder entry
            if (p.StartsWith('/') || (p.Length > 1 && p[1] == ':')) continue; // rooted
            var parts = p.Split('/');
            if (parts.Any(s => s.Length == 0 || s == "." || s == ".." || s.Any(ch => ch < 32 || BadChars.Contains(ch))))
                continue;
            if (seen.Add(p)) list.Add(p);
        }
        return list;
    }

    /// <summary>
    /// The relative files of an install: HIB.exe plus tools\&lt;each tool&gt; (Windows separators).
    /// </summary>
    public static IReadOnlyList<string> Plan(IEnumerable<string> toolEntries)
        => new[] { InstallLayout.ExeName }
            .Concat(SafeToolPaths(toolEntries).Select(t => InstallLayout.ToolsFolder + "\\" + t.Replace('/', '\\')))
            .ToList();

    /// <summary>Files already in tools\ that the new tool set no longer has (removed on update).</summary>
    public static IReadOnlyList<string> Stale(IEnumerable<string> existingToolFiles, IEnumerable<string> newToolEntries)
    {
        var keep = new HashSet<string>(SafeToolPaths(newToolEntries).Select(p => p.Replace('/', '\\')), StringComparer.OrdinalIgnoreCase);
        return existingToolFiles.Select(p => p.Replace('/', '\\')).Where(p => !keep.Contains(p)).ToList();
    }
}

/// <summary>The detached command that deletes the install folder once the uninstaller has exited.</summary>
public static class SelfDelete
{
    /// <summary>
    /// <c>cmd.exe /d /s /c "…"</c> arguments: up to ~30 s, try <c>rmdir /s /q</c> every second until the
    /// folder is gone (HIB.exe stays locked until this process exits).
    /// </summary>
    public static string CmdArguments(string dir)
    {
        var d = InstallLayout.Normalize(dir).Replace("\"", "");
        return $"/d /s /c \"for /l %i in (1,1,30) do (rmdir /s /q \"{d}\" 2>nul & if not exist \"{d}\" exit /b 0 & ping -n 2 127.0.0.1 >nul)\"";
    }
}
