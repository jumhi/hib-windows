// H.I.B — installs itself per user into %LOCALAPPDATA%\Programs\H.I.B (no admin rights) and removes
// itself again. The Windows-only parts (registry, .lnk files, processes) sit behind IInstallShell so
// the orchestration runs, and is tested, on any OS.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace HIB.Core.Install;

/// <summary>Registry, shortcuts and processes: the OS side of an install.</summary>
public interface IInstallShell
{
    string? ReadUninstallValue(string name);
    void WriteUninstallEntry(IReadOnlyList<UninstallValue> values);
    void DeleteUninstallEntry();

    string? ReadRunEntry();
    void WriteRunEntry(string command);
    void DeleteRunEntry();

    void CreateShortcut(string path, string target, string arguments, string description);
    /// <summary>Points the Explorer "Send to" shortcuts H.I.B made at <paramref name="exePath"/>.</summary>
    void RetargetSendToShortcuts(string exePath);
    void RemoveSendToShortcuts();

    /// <summary>Asks every other running H.I.B to quit (pipe), then ends what's left, plus adb/scrcpy run from <paramref name="installDir"/>.</summary>
    void CloseRunningCopies(string installDir, int selfPid);

    /// <summary>Deletes <paramref name="dir"/> once this process has exited (a detached cmd).</summary>
    void ScheduleFolderRemoval(string dir);
}

/// <summary>Where an install puts things (all per user).</summary>
public sealed record InstallPaths(string InstallDir, string StartMenuShortcut, string DesktopShortcut, IReadOnlyList<string> DataDirs)
{
    public string InstalledExe => InstallLayout.InstalledExe(InstallDir);
    public string DefaultInstallDir { get; init; } = InstallDir;

    /// <summary>This user's real folders.</summary>
    public static InstallPaths ForThisUser()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (string.IsNullOrEmpty(programs)) programs = Path.Combine(roaming, "Microsoft", "Windows", "Start Menu", "Programs");
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var dataDirs = new List<string> { HIB.Core.Pairing.AppPaths.DataDir, Path.Combine(roaming, "HIB"), Path.Combine(roaming, "H.I.B") }
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var dir = InstallLayout.InstallDir(local);
        return new InstallPaths(dir, InstallLayout.StartMenuShortcut(programs), InstallLayout.DesktopShortcut(desktop), dataDirs);
    }
}

/// <summary>What gets installed: an exe plus its tools (embedded zip in the Setup build, or the tools folder next to a portable copy).</summary>
public sealed class InstallSource
{
    public required string ExePath { get; init; }
    /// <summary>Opens the embedded tools zip, or null.</summary>
    public Func<Stream?> OpenToolsZip { get; init; } = () => null;
    /// <summary>A tools folder to copy from (a portable copy's), or null.</summary>
    public string? ToolsDir { get; init; }

    /// <summary>The running H.I.B: its embedded tools (Setup build) or its tools folder (unzipped copy).</summary>
    public static InstallSource FromRunningApp(string exePath)
    {
        var dir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
        var tools = Path.Combine(dir, InstallLayout.ToolsFolder);
        return new InstallSource
        {
            ExePath = exePath,
            OpenToolsZip = () => typeof(InstallSource).Assembly.GetManifestResourceStream(InstallLayout.ToolsResource),
            ToolsDir = Directory.Exists(tools) ? tools : null,
        };
    }

    /// <summary>True for the Setup build (tools embedded).</summary>
    public bool HasEmbeddedTools
    {
        get
        {
            using var s = OpenToolsZip();
            return s is not null;
        }
    }
}

public sealed record InstallProgress(double Fraction, string Status);

/// <summary>The current install on this PC (if any).</summary>
public sealed record InstallState(string InstallDir, string? Version, bool ExeExists, bool HasDesktopShortcut, bool HasRunEntry)
{
    public InstallKind KindFor(string setupVersion) => UpgradeDecision.Decide(Version, ExeExists, setupVersion);
}

public sealed class InstallException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class Installer(IInstallShell shell, InstallPaths paths)
{
    public IInstallShell Shell { get; } = shell;
    public InstallPaths Paths { get; } = paths;

    /// <summary>The installer for this PC: the real Windows shell on Windows, a no-op shell elsewhere (dev only).</summary>
    public static Installer ForThisPc()
        => new(OperatingSystem.IsWindows() ? new WindowsInstallShell() : new NullInstallShell(), InstallPaths.ForThisUser());

    /// <summary>Install folder from the uninstall entry when it's one we may manage, else the default.</summary>
    public string ResolveInstallDir()
    {
        var registered = Shell.ReadUninstallValue("InstallLocation");
        return registered is not null && InstallLayout.IsSafeToRemove(registered, Paths.DefaultInstallDir, File.Exists)
            ? registered
            : Paths.InstallDir;
    }

    public InstallState ReadState()
    {
        var dir = ResolveInstallDir();
        return new InstallState(dir, Shell.ReadUninstallValue("DisplayVersion"), File.Exists(InstallLayout.InstalledExe(dir)),
            File.Exists(Paths.DesktopShortcut), Shell.ReadRunEntry() is not null);
    }

    /// <summary>
    /// Closes H.I.B, copies HIB.exe and the tools, adds the Start menu (and optional desktop) shortcut,
    /// registers the Settings › Apps entry and re-points Start with Windows / Send to at the installed exe.
    /// Settings and pairings (%APPDATA%\HIB) are never touched, so an update keeps them.
    /// </summary>
    public string Install(InstallSource source, bool desktopShortcut, IProgress<InstallProgress>? progress = null, int? selfPid = null)
    {
        var dir = Paths.InstallDir;
        var exe = InstallLayout.InstalledExe(dir);
        void Report(double f, string s) => progress?.Report(new InstallProgress(Math.Clamp(f, 0, 1), s));

        Report(0.02, "Closing H.I.B…");
        Shell.CloseRunningCopies(dir, selfPid ?? Environment.ProcessId);

        Report(0.08, "Copying H.I.B…");
        Directory.CreateDirectory(dir);
        if (!InstallLayout.SamePath(source.ExePath, exe))
            CopyWithRetry(source.ExePath, exe, f => Report(0.08 + f * 0.5, "Copying H.I.B…"));

        Report(0.6, "Unpacking adb and scrcpy…");
        CopyTools(source, dir, f => Report(0.6 + f * 0.25, "Unpacking adb and scrcpy…"));

        Report(0.86, "Adding H.I.B to the Start menu…");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Paths.StartMenuShortcut)!);
            Shell.CreateShortcut(Paths.StartMenuShortcut, exe, "", "Your phone and your computers, together.");
            if (desktopShortcut) Shell.CreateShortcut(Paths.DesktopShortcut, exe, "", "Your phone and your computers, together.");
            else TryDelete(Paths.DesktopShortcut);
        }
        catch (Exception ex) { throw new InstallException("Couldn't add the Start menu shortcut: " + ex.Message, ex); }

        Report(0.92, "Registering with Windows…");
        if (Shell.ReadRunEntry() is not null) Shell.WriteRunEntry(InstallLayout.RunCommand(exe));
        try { Shell.RetargetSendToShortcuts(exe); } catch { /* the app re-creates them on start */ }
        Shell.WriteUninstallEntry(UninstallEntry.Build(dir, InstallLayout.AppVersion, FolderSize(dir), DateTime.Now));

        Report(1, "Installed");
        return exe;
    }

    /// <summary>
    /// Removes H.I.B: closes it, removes Start with Windows, the shortcuts, the Send to entries and the
    /// Settings › Apps entry, optionally the data folder, then the install folder (after this process exits
    /// when it runs from there). Returns the folder that is (being) removed, or null.
    /// </summary>
    public string? Uninstall(bool deleteData, string? runningExe = null, int? selfPid = null)
    {
        var dir = ResolveInstallDir();
        Shell.CloseRunningCopies(dir, selfPid ?? Environment.ProcessId);

        Shell.DeleteRunEntry();
        TryDelete(Paths.StartMenuShortcut);
        TryDelete(Paths.DesktopShortcut);
        try { Shell.RemoveSendToShortcuts(); } catch { }
        Shell.DeleteUninstallEntry();

        if (deleteData)
            foreach (var data in Paths.DataDirs)
                TryDeleteDir(data);

        if (!InstallLayout.IsSafeToRemove(dir, Paths.DefaultInstallDir, File.Exists)) return null;
        if (runningExe is not null && InstallLayout.IsInside(runningExe, dir))
        {
            Shell.ScheduleFolderRemoval(dir);
        }
        else if (!TryDeleteDir(dir))
        {
            Shell.ScheduleFolderRemoval(dir);
        }
        return dir;
    }

    // ---------------- files ----------------

    /// <summary>Embedded zip first, then a portable copy's tools folder; none → the installed tools stay as they are.</summary>
    void CopyTools(InstallSource source, string dir, Action<double> fraction)
    {
        var toolsDir = Path.Combine(dir, InstallLayout.ToolsFolder);
        using var zipStream = source.OpenToolsZip();
        if (zipStream is not null)
        {
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
            var byName = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name))
                .GroupBy(e => e.FullName.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var entries = InstallFiles.SafeToolPaths(byName.Keys);
            for (var i = 0; i < entries.Count; i++)
            {
                var e = byName[entries[i]];
                var target = Path.Combine(toolsDir, entries[i].Replace('/', Path.DirectorySeparatorChar));
                WriteIfChanged(target, () => e.Open(), e.Length);
                fraction((i + 1) / (double)entries.Count);
            }
            RemoveStale(toolsDir, entries);
            return;
        }
        if (source.ToolsDir is { } from && Directory.Exists(from) && !InstallLayout.SamePath(from, toolsDir))
        {
            var files = Directory.GetFiles(from, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(from, f).Replace(Path.DirectorySeparatorChar, '/')).ToList();
            var entries = InstallFiles.SafeToolPaths(files);
            for (var i = 0; i < entries.Count; i++)
            {
                var src = Path.Combine(from, entries[i].Replace('/', Path.DirectorySeparatorChar));
                var target = Path.Combine(toolsDir, entries[i].Replace('/', Path.DirectorySeparatorChar));
                WriteIfChanged(target, () => File.OpenRead(src), new FileInfo(src).Length);
                fraction((i + 1) / (double)entries.Count);
            }
            RemoveStale(toolsDir, entries);
        }
    }

    static void RemoveStale(string toolsDir, IReadOnlyList<string> entries)
    {
        if (!Directory.Exists(toolsDir)) return;
        var existing = Directory.GetFiles(toolsDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(toolsDir, f).Replace(Path.DirectorySeparatorChar, '/')).ToList();
        foreach (var stale in InstallFiles.Stale(existing, entries))
            TryDelete(Path.Combine(toolsDir, stale.Replace('\\', Path.DirectorySeparatorChar)));
    }

    /// <summary>Skips a file that is already identical (a running adb.exe can stay); otherwise writes it via a temp file.</summary>
    static void WriteIfChanged(string target, Func<Stream> open, long length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target) && new FileInfo(target).Length == length)
        {
            using var a = open();
            using var b = File.OpenRead(target);
            if (SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b))) return;
        }
        var tmp = target + ".new";
        using (var input = open())
        using (var output = File.Create(tmp))
            input.CopyTo(output);
        MoveWithRetry(tmp, target);
    }

    static void CopyWithRetry(string from, string to, Action<double> fraction)
    {
        var tmp = to + ".new";
        try
        {
            using var input = File.OpenRead(from);
            using var output = File.Create(tmp);
            var buffer = new byte[1 << 20];
            long total = input.Length, done = 0;
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, n);
                done += n;
                fraction(total == 0 ? 1 : done / (double)total);
            }
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            throw new InstallException($"Couldn't copy H.I.B: {ex.Message}", ex);
        }
        MoveWithRetry(tmp, to);
    }

    static void MoveWithRetry(string tmp, string target)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmp, target, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 20)
            {
                Thread.Sleep(250);
            }
            catch (Exception ex)
            {
                TryDelete(tmp);
                throw new InstallException($"{Path.GetFileName(target)} is in use. Quit H.I.B (and adb or scrcpy) and try again.", ex);
            }
        }
    }

    public static long FolderSize(string dir)
    {
        try { return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    static bool TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            return true;
        }
        catch { return false; }
    }
}

/// <summary>Off Windows: no registry, shortcuts or processes (lets the Setup window run on a dev Mac).</summary>
public sealed class NullInstallShell : IInstallShell
{
    public string? ReadUninstallValue(string name) => null;
    public void WriteUninstallEntry(IReadOnlyList<UninstallValue> values) { }
    public void DeleteUninstallEntry() { }
    public string? ReadRunEntry() => null;
    public void WriteRunEntry(string command) { }
    public void DeleteRunEntry() { }
    public void CreateShortcut(string path, string target, string arguments, string description) { }
    public void RetargetSendToShortcuts(string exePath) { }
    public void RemoveSendToShortcuts() { }
    public void CloseRunningCopies(string installDir, int selfPid) { }
    public void ScheduleFolderRemoval(string dir) { }
}
