using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HIB.Core.Install;
using HIB.Services;

namespace HIB.ViewModels;

public enum SetupPhase { Ready, Working, Done, Failed }

/// <summary>The small Setup / uninstall window (H.I.B-Setup.exe, HIB.exe --install, HIB.exe --uninstall).</summary>
public partial class SetupViewModel : ViewModelBase
{
    readonly Installer _installer;
    readonly InstallSource _source;
    readonly string _runningExe;
    readonly InstallState _state;
    readonly InstallKind _kind;
    string? _installedExe;

    public SetupViewModel(SetupAction action, Installer installer, InstallSource source, string runningExe)
    {
        IsUninstall = action == SetupAction.Uninstall;
        _installer = installer;
        _source = source;
        _runningExe = runningExe;
        _state = installer.ReadState();
        _kind = _state.KindFor(Version);
        DesktopShortcut = _kind == InstallKind.Fresh || _state.HasDesktopShortcut;
        // Installing from an unzipped copy (not the Setup build): the old folder can go afterwards.
        FromPortableCopy = !IsUninstall && !source.HasEmbeddedTools
                           && !InstallLayout.IsInstalledCopy(runningExe, installer.Paths.InstallDir);
    }

    public bool IsUninstall { get; }
    public bool IsInstall => !IsUninstall;
    public bool FromPortableCopy { get; }
    public string Version => InstallLayout.AppVersion;
    public Bitmap? AppIcon { get; } = ImageUtil.AppIcon();
    public string InstallDir => IsUninstall ? _state.InstallDir : _installer.Paths.InstallDir;
    public InstallKind Kind => _kind;

    /// <summary>Starts the installed app ("Open H.I.B"); replaceable in tests.</summary>
    public Action<string> LaunchApp { get; set; } = exe =>
    {
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" }); }
        catch { }
    };

    /// <summary>Closes the window (set by SetupWindow).</summary>
    public Action? Close { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(IsWorking), nameof(IsDone), nameof(IsFailed), nameof(ShowInstallOptions),
        nameof(ShowUninstallOptions), nameof(Title), nameof(Subtitle), nameof(PrimaryText), nameof(ShowPrimary), nameof(ShowDanger),
        nameof(SecondaryText), nameof(ShowSecondary))]
    public partial SetupPhase Phase { get; set; } = SetupPhase.Ready;

    [ObservableProperty] public partial bool DesktopShortcut { get; set; }
    [ObservableProperty] public partial bool DeleteData { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial string DoneTitle { get; set; } = "";
    [ObservableProperty] public partial string DoneBody { get; set; } = "";

    public bool IsReady => Phase == SetupPhase.Ready;
    public bool IsWorking => Phase == SetupPhase.Working;
    public bool IsDone => Phase == SetupPhase.Done;
    public bool IsFailed => Phase == SetupPhase.Failed;
    public bool ShowInstallOptions => IsReady && IsInstall;
    public bool ShowUninstallOptions => IsReady && IsUninstall;

    public string Title => (IsUninstall, Phase) switch
    {
        (true, SetupPhase.Done) => "H.I.B was removed",
        (true, SetupPhase.Working) => "Uninstalling H.I.B…",
        (true, _) => "Uninstall H.I.B?",
        (false, SetupPhase.Done) => "H.I.B is installed",
        (false, SetupPhase.Working) => _kind == InstallKind.Upgrade ? "Updating H.I.B…" : "Installing H.I.B…",
        _ => UpgradeDecision.Title(_kind, _state.Version, Version),
    };

    public string Subtitle => IsUninstall
        ? (_state.Version is { } v ? $"Version {v} · Made by Haitham Hamid" : "Made by Haitham Hamid")
        : _kind switch
        {
            InstallKind.Upgrade => $"You have {_state.Version}. Your settings and pairings stay.",
            InstallKind.Reinstall or InstallKind.Downgrade => $"Version {Version} · your settings and pairings stay",
            _ => $"Version {Version} · Made by Haitham Hamid",
        };

    public string PrimaryText => Phase switch
    {
        SetupPhase.Done => IsUninstall ? "Done" : "Open H.I.B",
        SetupPhase.Failed => "Try again",
        _ => UpgradeDecision.Button(_kind),
    };

    /// <summary>The cyan button (everything but "Uninstall", which is the red one).</summary>
    public bool ShowPrimary => Phase is SetupPhase.Done or SetupPhase.Failed || (Phase == SetupPhase.Ready && IsInstall);
    public bool ShowDanger => Phase == SetupPhase.Ready && IsUninstall;
    public string SecondaryText => Phase == SetupPhase.Ready ? "Cancel" : "Close";
    public bool ShowSecondary => Phase != SetupPhase.Working && !(Phase == SetupPhase.Done && IsUninstall);

    [RelayCommand]
    async Task Primary()
    {
        switch (Phase)
        {
            case SetupPhase.Done when IsInstall:
                if (_installedExe is { } exe) LaunchApp(exe);
                Close?.Invoke();
                break;
            case SetupPhase.Done:
                Close?.Invoke();
                break;
            default:
                await RunAsync();
                break;
        }
    }

    [RelayCommand] void Uninstall() => _ = RunAsync();

    [RelayCommand] void Secondary() => Close?.Invoke();

    /// <summary>Installs or uninstalls on a worker thread, reporting progress here.</summary>
    public async Task RunAsync()
    {
        if (Phase == SetupPhase.Working) return;
        Phase = SetupPhase.Working;
        Error = "";
        Progress = 0;
        Status = IsUninstall ? "Closing H.I.B…" : "Getting ready…";
        var progress = new Progress<InstallProgress>(p =>
        {
            Progress = p.Fraction * 100;
            Status = p.Status;
        });
        var desktop = DesktopShortcut;
        var deleteData = DeleteData;
        try
        {
            if (IsUninstall)
            {
                await Task.Run(() => _installer.Uninstall(deleteData, _runningExe));
                Progress = 100;
                DoneTitle = "H.I.B is no longer on this PC";
                DoneBody = deleteData
                    ? "Its shortcuts, Send to entries and Start with Windows are gone, and so are your settings and pairings."
                    : "Its shortcuts, Send to entries and Start with Windows are gone. Your settings and pairings are kept in case you install it again.";
            }
            else
            {
                _installedExe = await Task.Run(() => _installer.Install(_source, desktop, progress));
                Progress = 100;
                DoneTitle = _kind == InstallKind.Upgrade ? $"Updated to {Version}" : "Ready in the Start menu";
                DoneBody = "Search for H.I.B in Start. To remove it later, use Settings › Apps › H.I.B."
                           + (FromPortableCopy && Path.GetDirectoryName(_runningExe) is { } old
                               ? $" You can delete the copy you unzipped ({old})."
                               : "");
            }
            Phase = SetupPhase.Done;
        }
        catch (Exception ex)
        {
            Error = ex is InstallException ? ex.Message : (IsUninstall ? "Couldn't uninstall: " : "Couldn't install: ") + ex.Message;
            Phase = SetupPhase.Failed;
        }
    }
}
