using System.IO.Compression;
using System.Text;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HIB.Core.Install;
using HIB.Services;
using HIB.ViewModels;
using HIB.Views;

namespace HIB.FakePhone;

/// <summary>Records what an install did to the "OS"; shortcuts become small files so they can be checked.</summary>
sealed class FakeInstallShell : IInstallShell
{
    public Dictionary<string, object> Uninstall { get; } = new();
    public bool HasUninstallEntry;
    public string? Run;
    public readonly List<string> Calls = new();
    public readonly List<(string Path, string Target)> Shortcuts = new();
    public string? ScheduledRemoval;

    public string? ReadUninstallValue(string name) => HasUninstallEntry && Uninstall.TryGetValue(name, out var v) ? v.ToString() : null;
    public void WriteUninstallEntry(IReadOnlyList<UninstallValue> values)
    {
        Calls.Add("uninstall-entry");
        HasUninstallEntry = true;
        Uninstall.Clear();
        foreach (var v in values) Uninstall[v.Name] = v.Value;
    }
    public void DeleteUninstallEntry() { Calls.Add("delete-uninstall-entry"); HasUninstallEntry = false; Uninstall.Clear(); }
    public string? ReadRunEntry() => Run;
    public void WriteRunEntry(string command) { Calls.Add("run"); Run = command; }
    public void DeleteRunEntry() { Calls.Add("delete-run"); Run = null; }
    public void CreateShortcut(string path, string target, string arguments, string description)
    {
        Shortcuts.Add((path, target));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, target);
    }
    public void RetargetSendToShortcuts(string exePath) => Calls.Add("retarget-sendto:" + exePath);
    public void RemoveSendToShortcuts() => Calls.Add("remove-sendto");
    public void CloseRunningCopies(string installDir, int selfPid) => Calls.Add("close");
    public void ScheduleFolderRemoval(string dir) { Calls.Add("schedule-removal"); ScheduledRemoval = dir; }
}

public class InstallerTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hib-install-tests", Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    InstallPaths NewPaths()
    {
        var dir = InstallLayout.InstallDir(Path.Combine(_root, "Local"));
        return new InstallPaths(dir, Path.Combine(_root, "Start", "H.I.B.lnk"), Path.Combine(_root, "Desktop", "H.I.B.lnk"),
            new[] { Path.Combine(_root, "Roaming", "HIB") });
    }

    /// <summary>An unzipped HIB-Windows.zip: H.I.B\HIB.exe + tools\…</summary>
    string PortableCopy(string exeText = "portable exe v1")
    {
        var dir = Path.Combine(_root, "Downloads", "HIB-Windows", "H.I.B");
        Directory.CreateDirectory(Path.Combine(dir, "tools", "sub"));
        File.WriteAllText(Path.Combine(dir, "HIB.exe"), exeText);
        File.WriteAllText(Path.Combine(dir, "tools", "adb.exe"), "adb");
        File.WriteAllText(Path.Combine(dir, "tools", "scrcpy.exe"), "scrcpy");
        File.WriteAllText(Path.Combine(dir, "tools", "sub", "x.dll"), "x");
        File.WriteAllText(Path.Combine(dir, "README.txt"), "readme");
        return Path.Combine(dir, "HIB.exe");
    }

    static byte[] ToolsZip(params (string Name, string Text)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
            {
                using var w = zip.CreateEntry(name).Open();
                w.Write(Encoding.UTF8.GetBytes(text));
            }
        return ms.ToArray();
    }

    // ---------------- launch mode ----------------

    [Theory]
    [InlineData(@"C:\Users\h\Downloads\H.I.B-Setup.exe", "", SetupAction.Install, false)]
    [InlineData(@"C:\Users\h\Downloads\H.I.B-Setup (1).exe", "", SetupAction.Install, false)]
    [InlineData(@"C:\Users\h\Downloads\hib-setup.EXE", "", SetupAction.Install, false)]
    [InlineData(@"C:\Users\h\AppData\Local\Programs\H.I.B\HIB.exe", "", SetupAction.None, false)]
    [InlineData(@"C:\Users\h\AppData\Local\Programs\H.I.B\HIB.exe", "--minimized", SetupAction.None, false)]
    [InlineData(@"C:\H.I.B\HIB.exe", "--install", SetupAction.Install, false)]
    [InlineData(@"C:\H.I.B\HIB.exe", "--uninstall", SetupAction.Uninstall, false)]
    [InlineData(@"C:\H.I.B\HIB.exe", "--uninstall --quiet", SetupAction.Uninstall, true)]
    [InlineData(@"C:\H.I.B\HIB.exe", "--UNINSTALL --Quiet", SetupAction.Uninstall, true)]
    [InlineData(@"C:\Downloads\H.I.B-Setup.exe", "--uninstall", SetupAction.Uninstall, false)]
    [InlineData(@"C:\Downloads\H.I.B-Setup.exe", "--send-to phone a.txt", SetupAction.None, false)]
    [InlineData("/Users/h/hib/HIB", "", SetupAction.None, false)]
    [InlineData(null, "", SetupAction.None, false)]
    public void Setup_mode_comes_from_the_exe_name_and_arguments(string? exe, string args, SetupAction action, bool quiet)
    {
        var r = SetupRequest.Detect(exe, args.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(action, r.Action);
        Assert.Equal(quiet, r.Quiet);
    }

    // ---------------- paths ----------------

    [Fact]
    public void Install_paths_are_per_user()
    {
        Assert.True(InstallLayout.SamePath(@"C:\Users\h\AppData\Local\Programs\H.I.B", InstallLayout.InstallDir(@"C:\Users\h\AppData\Local")));
        Assert.True(InstallLayout.SamePath(@"C:\Users\h\AppData\Local\Programs\H.I.B\HIB.exe",
            InstallLayout.InstalledExe(InstallLayout.InstallDir(@"C:\Users\h\AppData\Local"))));
        Assert.True(InstallLayout.SamePath(@"C:\Users\h\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\H.I.B.lnk",
            InstallLayout.StartMenuShortcut(@"C:\Users\h\AppData\Roaming\Microsoft\Windows\Start Menu\Programs")));
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\H.I.B", InstallLayout.UninstallKeyPath);
        Assert.Equal("\"C:\\P\\H.I.B\\HIB.exe\" --minimized", InstallLayout.RunCommand(@"C:\P\H.I.B\HIB.exe"));

        const string dir = @"C:\Users\h\AppData\Local\Programs\H.I.B";
        Assert.True(InstallLayout.IsInstalledCopy(@"C:\Users\h\AppData\Local\Programs\H.I.B\HIB.exe", dir));
        Assert.True(InstallLayout.IsInstalledCopy(@"c:\users\H\appdata\local\programs\h.i.b\HIB.exe", dir + "\\"));
        Assert.False(InstallLayout.IsInstalledCopy(@"C:\Users\h\Downloads\HIB-Windows\H.I.B\HIB.exe", dir));
        Assert.False(InstallLayout.IsInstalledCopy(@"C:\Users\h\AppData\Local\Programs\H.I.B\tools\HIB.exe", dir));
        Assert.False(InstallLayout.IsInstalledCopy(null, dir));

        Assert.True(InstallLayout.IsInside(dir + @"\tools\adb.exe", dir));
        Assert.True(InstallLayout.IsInside(dir, dir));
        Assert.False(InstallLayout.IsInside(dir + @" Old\HIB.exe", dir));
        Assert.False(InstallLayout.IsInside(null, dir));
    }

    [Fact]
    public void Only_hib_folders_are_ever_removed()
    {
        const string def = @"C:\Users\h\AppData\Local\Programs\H.I.B";
        bool Exists(string p) => true;
        Assert.True(InstallLayout.IsSafeToRemove(def, def, Exists));
        Assert.True(InstallLayout.IsSafeToRemove(@"D:\Apps\H.I.B", def, Exists));
        Assert.False(InstallLayout.IsSafeToRemove(@"C:\Users\h\Downloads", def, Exists)); // a portable copy's parent
        Assert.False(InstallLayout.IsSafeToRemove(@"C:\", def, Exists));
        Assert.False(InstallLayout.IsSafeToRemove(@"C:", def, Exists));
        Assert.False(InstallLayout.IsSafeToRemove("", def, Exists));
        Assert.False(InstallLayout.IsSafeToRemove(null, def, Exists));
        Assert.False(InstallLayout.IsSafeToRemove(def, def, _ => false)); // no HIB.exe inside
    }

    // ---------------- Settings › Apps entry ----------------

    [Fact]
    public void Uninstall_entry_has_what_settings_apps_shows()
    {
        const string dir = @"C:\Users\h\AppData\Local\Programs\H.I.B";
        var v = UninstallEntry.Build(dir, "4.1.0", 130_000_000, new DateTime(2026, 11, 20)).ToDictionary(x => x.Name, x => x.Value);
        Assert.Equal("H.I.B", v["DisplayName"]);
        Assert.Equal("4.1.0", v["DisplayVersion"]);
        Assert.Equal("Haitham Hamid", v["Publisher"]);
        Assert.True(InstallLayout.SamePath(dir, (string)v["InstallLocation"]));
        Assert.Equal("\"" + InstallLayout.InstalledExe(dir) + "\" --uninstall", v["UninstallString"]);
        Assert.Equal("\"" + InstallLayout.InstalledExe(dir) + "\" --uninstall --quiet", v["QuietUninstallString"]);
        Assert.Equal(InstallLayout.InstalledExe(dir) + ",0", v["DisplayIcon"]);
        Assert.Equal(126954, v["EstimatedSize"]); // KB, rounded up
        Assert.Equal(1, v["NoModify"]);
        Assert.Equal(1, v["NoRepair"]);
        Assert.Equal("20261120", v["InstallDate"]);
        var dwords = UninstallEntry.Build(dir, "4.1.0", 1, DateTime.Today).Where(x => x.IsDword).Select(x => x.Name).ToHashSet();
        Assert.Equal(new HashSet<string> { "EstimatedSize", "NoModify", "NoRepair" }, dwords);
    }

    // ---------------- update decision ----------------

    [Theory]
    [InlineData(null, false, "4.1.0", InstallKind.Fresh)]
    [InlineData("4.0.0", false, "4.1.0", InstallKind.Fresh)] // entry left behind but the exe is gone
    [InlineData("4.0.2", true, "4.1.0", InstallKind.Upgrade)]
    [InlineData("4.1.0", true, "4.1.0", InstallKind.Reinstall)]
    [InlineData("4.1", true, "4.1.0", InstallKind.Reinstall)]
    [InlineData("4.1.0.0", true, "4.1.0", InstallKind.Reinstall)]
    [InlineData("5.0.0", true, "4.1.0", InstallKind.Downgrade)]
    [InlineData("garbage", true, "4.1.0", InstallKind.Reinstall)]
    [InlineData(null, true, "4.1.0", InstallKind.Reinstall)]
    public void Running_setup_again_updates_or_reinstalls(string? installed, bool exeExists, string setup, InstallKind kind)
        => Assert.Equal(kind, UpgradeDecision.Decide(installed, exeExists, setup));

    [Fact]
    public void Setup_titles_follow_the_decision()
    {
        Assert.Equal("Install H.I.B", UpgradeDecision.Title(InstallKind.Fresh, null, "4.1.0"));
        Assert.Equal("Update H.I.B to 4.2.0", UpgradeDecision.Title(InstallKind.Upgrade, "4.1.0", "4.2.0"));
        Assert.Equal("Update", UpgradeDecision.Button(InstallKind.Upgrade));
        Assert.Equal("Install", UpgradeDecision.Button(InstallKind.Fresh));
    }

    // ---------------- files ----------------

    [Fact]
    public void Copied_files_are_the_exe_and_safe_tool_paths()
    {
        var plan = InstallFiles.Plan(new[]
        {
            "adb.exe", "scrcpy-server", "sub/x.dll", @"sub\y.dll", "dir/", "../evil.dll", "sub/../../evil.dll", "/etc/passwd",
            @"C:\Windows\evil.dll", "a/./b.dll", "ADB.EXE", "bad|name.dll", "", "a//b.dll",
        });
        Assert.Equal(new[] { "HIB.exe", @"tools\adb.exe", @"tools\scrcpy-server", @"tools\sub\x.dll", @"tools\sub\y.dll" }, plan);
        Assert.Equal(new[] { @"old.dll", @"sub\gone.dll" },
            InstallFiles.Stale(new[] { "adb.exe", "old.dll", "sub/gone.dll", "SUB/X.DLL" }, new[] { "adb.exe", "sub/x.dll" }));
    }

    [Fact]
    public void Self_delete_waits_for_the_folder_to_free_up()
    {
        var args = SelfDelete.CmdArguments(@"C:\Users\h\AppData\Local\Programs\H.I.B\");
        Assert.StartsWith("/d /s /c \"", args);
        Assert.EndsWith(")\"", args);
        Assert.Contains("rmdir /s /q \"C:\\Users\\h\\AppData\\Local\\Programs\\H.I.B\"", args);
        Assert.Contains("for /l %i in (1,1,30)", args);
        Assert.Contains("if not exist \"C:\\Users\\h\\AppData\\Local\\Programs\\H.I.B\" exit /b 0", args);
    }

    // ---------------- install / update / uninstall with a fake OS ----------------

    [Fact]
    public void Install_from_an_unzipped_copy_registers_everything()
    {
        var shell = new FakeInstallShell();
        var paths = NewPaths();
        var installer = new Installer(shell, paths);
        var exe = PortableCopy();
        Assert.Equal(InstallKind.Fresh, installer.ReadState().KindFor("4.1.0"));
        var steps = new List<InstallProgress>();

        var installed = installer.Install(InstallSource.FromRunningApp(exe), desktopShortcut: true, new SyncProgress(steps.Add));

        Assert.Equal(paths.InstalledExe, installed);
        Assert.Equal("portable exe v1", File.ReadAllText(installed));
        Assert.Equal("adb", File.ReadAllText(Path.Combine(paths.InstallDir, "tools", "adb.exe")));
        Assert.Equal("x", File.ReadAllText(Path.Combine(paths.InstallDir, "tools", "sub", "x.dll")));
        Assert.False(File.Exists(Path.Combine(paths.InstallDir, "README.txt")));
        Assert.Empty(Directory.GetFiles(paths.InstallDir, "*.new", SearchOption.AllDirectories));
        Assert.Equal("close", shell.Calls[0]); // closes H.I.B before touching files
        Assert.Contains((paths.StartMenuShortcut, installed), shell.Shortcuts);
        Assert.Contains((paths.DesktopShortcut, installed), shell.Shortcuts);
        Assert.Contains("retarget-sendto:" + installed, shell.Calls);
        Assert.Null(shell.Run); // no Start with Windows before → none after
        Assert.Equal("H.I.B", shell.Uninstall["DisplayName"]);
        Assert.Equal(paths.InstallDir, shell.Uninstall["InstallLocation"]);
        Assert.Equal(InstallLayout.AppVersion, shell.Uninstall["DisplayVersion"]);
        Assert.Equal((int)((Installer.FolderSize(paths.InstallDir) + 1023) / 1024), shell.Uninstall["EstimatedSize"]);
        Assert.Equal(1.0, steps[^1].Fraction);
        Assert.True(steps.Select(s => s.Fraction).SequenceEqual(steps.Select(s => s.Fraction).Order()));
        Assert.Equal(InstallKind.Reinstall, installer.ReadState().KindFor(InstallLayout.AppVersion));
    }

    [Fact]
    public void Update_keeps_settings_and_moves_start_with_windows()
    {
        var shell = new FakeInstallShell();
        var paths = NewPaths();
        var installer = new Installer(shell, paths);
        var portable = PortableCopy();
        installer.Install(InstallSource.FromRunningApp(portable), desktopShortcut: true);
        File.WriteAllText(Path.Combine(paths.InstallDir, "tools", "old.dll"), "old");
        Directory.CreateDirectory(paths.DataDirs[0]);
        File.WriteAllText(Path.Combine(paths.DataDirs[0], "hib-desktop.json"), "{\"EncryptionKey\":\"k\"}");
        shell.Run = InstallLayout.RunCommand(portable); // Start with Windows was on for the unzipped copy
        shell.Uninstall["DisplayVersion"] = "0.9.0";

        var setupExe = Path.Combine(_root, "Downloads", "H.I.B-Setup.exe");
        File.WriteAllText(setupExe, "setup exe v2");
        var zip = ToolsZip(("adb.exe", "adb"), ("scrcpy.exe", "scrcpy v2"), ("new.dll", "new"), ("../evil.txt", "evil"), ("sub/", ""));
        var source = new InstallSource { ExePath = setupExe, OpenToolsZip = () => new MemoryStream(zip) };
        Assert.True(source.HasEmbeddedTools);
        Assert.Equal(InstallKind.Upgrade, installer.ReadState().KindFor("4.1.0"));

        installer.Install(source, desktopShortcut: false);

        Assert.Equal("setup exe v2", File.ReadAllText(paths.InstalledExe));
        Assert.Equal("scrcpy v2", File.ReadAllText(Path.Combine(paths.InstallDir, "tools", "scrcpy.exe")));
        Assert.True(File.Exists(Path.Combine(paths.InstallDir, "tools", "new.dll")));
        Assert.False(File.Exists(Path.Combine(paths.InstallDir, "tools", "old.dll"))); // not in the new tool set
        Assert.False(File.Exists(Path.Combine(paths.InstallDir, "tools", "sub", "x.dll")));
        Assert.False(File.Exists(Path.Combine(paths.InstallDir, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(paths.InstallDir, "..", "evil.txt")));
        Assert.Equal("{\"EncryptionKey\":\"k\"}", File.ReadAllText(Path.Combine(paths.DataDirs[0], "hib-desktop.json")));
        Assert.Equal(InstallLayout.RunCommand(paths.InstalledExe), shell.Run);
        Assert.False(File.Exists(paths.DesktopShortcut)); // unticked this time
        Assert.True(File.Exists(paths.StartMenuShortcut));
        Assert.Equal(InstallLayout.AppVersion, shell.Uninstall["DisplayVersion"]);
        Assert.True(File.Exists(portable)); // the unzipped copy is left alone
    }

    [Fact]
    public void Installed_copy_reinstalling_itself_skips_its_own_exe()
    {
        var shell = new FakeInstallShell();
        var paths = NewPaths();
        var installer = new Installer(shell, paths);
        installer.Install(InstallSource.FromRunningApp(PortableCopy()), desktopShortcut: false);
        var again = installer.Install(InstallSource.FromRunningApp(paths.InstalledExe), desktopShortcut: false);
        Assert.Equal("portable exe v1", File.ReadAllText(again));
        Assert.Equal("adb", File.ReadAllText(Path.Combine(paths.InstallDir, "tools", "adb.exe")));
    }

    [Fact]
    public void Uninstall_from_the_installed_copy_keeps_data_and_schedules_the_folder()
    {
        var shell = new FakeInstallShell();
        var paths = NewPaths();
        var installer = new Installer(shell, paths);
        installer.Install(InstallSource.FromRunningApp(PortableCopy()), desktopShortcut: true);
        shell.Run = InstallLayout.RunCommand(paths.InstalledExe);
        Directory.CreateDirectory(paths.DataDirs[0]);
        File.WriteAllText(Path.Combine(paths.DataDirs[0], "hib-desktop.json"), "{}");
        shell.Calls.Clear();

        var removed = installer.Uninstall(deleteData: false, runningExe: paths.InstalledExe);

        Assert.Equal(paths.InstallDir, removed);
        Assert.Equal("close", shell.Calls[0]);
        Assert.Null(shell.Run);
        Assert.False(shell.HasUninstallEntry);
        Assert.Contains("remove-sendto", shell.Calls);
        Assert.False(File.Exists(paths.StartMenuShortcut));
        Assert.False(File.Exists(paths.DesktopShortcut));
        Assert.True(File.Exists(Path.Combine(paths.DataDirs[0], "hib-desktop.json")));
        Assert.Equal(paths.InstallDir, shell.ScheduledRemoval); // HIB.exe is still running from there
        Assert.True(Directory.Exists(paths.InstallDir));
    }

    [Fact]
    public void Uninstall_can_delete_settings_and_never_touches_other_folders()
    {
        var shell = new FakeInstallShell();
        var paths = NewPaths();
        var installer = new Installer(shell, paths);
        installer.Install(InstallSource.FromRunningApp(PortableCopy()), desktopShortcut: false);
        Directory.CreateDirectory(paths.DataDirs[0]);
        File.WriteAllText(Path.Combine(paths.DataDirs[0], "hib-desktop.json"), "{}");
        // A tampered entry pointing at Downloads (which has a HIB.exe in it) must not be deleted.
        var downloads = Path.Combine(_root, "Downloads");
        File.WriteAllText(Path.Combine(downloads, "HIB.exe"), "x");
        shell.Uninstall["InstallLocation"] = downloads;

        var removed = installer.Uninstall(deleteData: true, runningExe: Path.Combine(_root, "elsewhere", "HIB.exe"));

        Assert.Equal(paths.InstallDir, removed);
        Assert.False(Directory.Exists(paths.InstallDir)); // not running from there: removed right away
        Assert.Null(shell.ScheduledRemoval);
        Assert.False(Directory.Exists(paths.DataDirs[0]));
        Assert.True(File.Exists(Path.Combine(downloads, "HIB.exe")));
    }

    // ---------------- quit over the pipe ----------------

    [Fact]
    public async Task Setup_asks_the_running_app_to_quit_over_the_pipe()
    {
        Assert.True(SendToPipe.IsQuit(SendToPipe.QuitMessage));
        Assert.False(SendToPipe.IsQuit(new SendToRequest("phone", new[] { "/tmp/a" }).ToJson()));
        Assert.False(SendToPipe.IsQuit("not json"));
        Assert.Null(SendToRequest.FromJson(SendToPipe.QuitMessage)); // older copies ignore it

        var name = "HIB-Test-Quit-" + Guid.NewGuid().ToString("N")[..8];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var quit = new TaskCompletionSource();
        var server = SendToPipe.ServeAsync(_ => Assert.Fail("not a send-to request"), cts.Token, () => quit.TrySetResult(), name);
        var ok = false;
        for (var i = 0; i < 20 && !ok; i++)
        {
            ok = SendToPipe.TrySendQuit(500, name);
            if (!ok) await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.True(ok);
        await quit.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cts.Cancel();
        await server;
        Assert.False(SendToPipe.TrySendQuit(200, name)); // nobody listening → false
    }

    sealed class SyncProgress(Action<InstallProgress> report) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => report(value);
    }
}

/// <summary>The Setup / uninstall window renders in each state (PNGs to HIB_SCREENSHOT_DIR when set).</summary>
public class SetupWindowTests : IDisposable
{
    static readonly string? ShotDir = Environment.GetEnvironmentVariable("HIB_SCREENSHOT_DIR");
    readonly string _root = Path.Combine(Path.GetTempPath(), "hib-setup-ui", Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    static void Save(Avalonia.Controls.Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (ShotDir is null) return;
        Directory.CreateDirectory(ShotDir);
        frame!.Save(Path.Combine(ShotDir, name + ".png"));
    }

    static async Task PumpUntil(Func<bool> done, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!done() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    (Installer Installer, InstallSource Source, string Exe) Setup()
    {
        var paths = new InstallPaths(Path.Combine(_root, "Local", "Programs", "H.I.B"), Path.Combine(_root, "Start", "H.I.B.lnk"),
            Path.Combine(_root, "Desktop", "H.I.B.lnk"), new[] { Path.Combine(_root, "Roaming", "HIB") });
        var exe = Path.Combine(_root, "Downloads", "H.I.B-Setup.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "setup");
        return (new Installer(new FakeInstallShell(), paths), new InstallSource { ExePath = exe }, exe);
    }

    [AvaloniaFact]
    public async Task Setup_window_installs_then_offers_to_open()
    {
        var (installer, source, exe) = Setup();
        var vm = new SetupViewModel(SetupAction.Install, installer, source, exe);
        string? launched = null;
        var closed = false;
        vm.LaunchApp = p => launched = p;
        var w = new SetupWindow(vm);
        vm.Close = () => closed = true;
        w.Show();
        Assert.Equal("Install H.I.B", vm.Title);
        Assert.Equal("Install", vm.PrimaryText);
        Assert.True(vm.DesktopShortcut);
        Assert.True(vm.ShowInstallOptions);
        Save(w, "setup-1-install");

        vm.Phase = SetupPhase.Working;
        vm.Status = "Unpacking adb and scrcpy…";
        vm.Progress = 64;
        Save(w, "setup-2-working");
        vm.Phase = SetupPhase.Ready;

        await vm.PrimaryCommand.ExecuteAsync(null);
        await PumpUntil(() => vm.IsDone);
        Assert.True(vm.IsDone, vm.Error);
        Assert.Equal("H.I.B is installed", vm.Title);
        Assert.Equal("Open H.I.B", vm.PrimaryText);
        Assert.True(File.Exists(installer.Paths.InstalledExe));
        Assert.True(File.Exists(installer.Paths.DesktopShortcut));
        Save(w, "setup-3-done");

        await vm.PrimaryCommand.ExecuteAsync(null);
        Assert.Equal(installer.Paths.InstalledExe, launched);
        Assert.True(closed);

        // Running Setup again → Reinstall wording.
        var again = new SetupViewModel(SetupAction.Install, installer, source, exe);
        Assert.Equal(InstallKind.Reinstall, again.Kind);
        Assert.Equal("Reinstall", again.PrimaryText);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Uninstall_window_confirms_and_keeps_data_by_default()
    {
        var (installer, source, exe) = Setup();
        installer.Install(source, desktopShortcut: true);
        var vm = new SetupViewModel(SetupAction.Uninstall, installer, source, installer.Paths.InstalledExe);
        var w = new SetupWindow(vm);
        w.Show();
        Assert.Equal("Uninstall H.I.B?", vm.Title);
        Assert.False(vm.DeleteData);
        Assert.True(vm.ShowDanger);
        Assert.False(vm.ShowPrimary);
        Save(w, "setup-4-uninstall");

        vm.UninstallCommand.Execute(null);
        await PumpUntil(() => vm.IsDone);
        Assert.True(vm.IsDone, vm.Error);
        Assert.Equal("H.I.B was removed", vm.Title);
        Assert.False(File.Exists(installer.Paths.StartMenuShortcut));
        Assert.Contains("kept", vm.DoneBody);
        Save(w, "setup-5-removed");

        vm.Error = "HIB.exe is in use. Quit H.I.B (and adb or scrcpy) and try again.";
        vm.Phase = SetupPhase.Failed;
        Save(w, "setup-6-failed");
        w.Close();
    }
}
