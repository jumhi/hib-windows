using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HIB.Features.Adb;
using HIB.Features.Mirror;
using HIB.Services;

namespace HIB.ViewModels;

public static class AdbSetupConverters
{
    /// <summary>Pending steps are dimmed.</summary>
    public static readonly Avalonia.Data.Converters.FuncValueConverter<bool, double> PendingOpacity = new(p => p ? 0.5 : 1);
}

/// <summary>One of the three steps in the card's rail, with a live tick.</summary>
public sealed partial class AdbSetupStepItem : ObservableObject
{
    public AdbSetupStepItem(int number, string title)
    {
        Number = number;
        Title = title;
    }

    public int Number { get; }
    public string Title { get; }
    [ObservableProperty] public partial bool IsDone { get; set; }
    [ObservableProperty] public partial bool IsCurrent { get; set; }
    public bool IsPending => !IsDone && !IsCurrent;
    partial void OnIsDoneChanged(bool value) => OnPropertyChanged(nameof(IsPending));
    partial void OnIsCurrentChanged(bool value) => OnPropertyChanged(nameof(IsPending));
}

/// <summary>
/// "Set up mirroring" (docs/SPACE.md §8.3), driven by the phone's <c>adbSetupState</c> through
/// <see cref="AdbSetupSession"/>: Developer options → Wireless debugging → pair (QR or 6-digit code) → ready.
/// </summary>
public sealed partial class AdbSetupViewModel : ViewModelBase, IDisposable
{
    readonly AppController _app;
    readonly FeatureHost _f;
    readonly AdbSetupSession _session;
    string _qrFor = "";

    public AdbSetupViewModel(AppController app, FeatureHost f, AdbSetupSession session)
    {
        _app = app;
        _f = f;
        _session = session;
        Steps = new ObservableCollection<AdbSetupStepItem>
        {
            new(1, "Developer options"),
            new(2, "Wireless debugging"),
            new(3, "Pair this PC"),
        };
        session.PropertyChanged += (_, _) => Ui.Post(Refresh);
        f.Pairing.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AdbPairingService.QrText) or nameof(AdbPairingService.Status) or nameof(AdbPairingService.Phase))
                Ui.Post(Refresh);
        };
        Refresh();
    }

    public AdbSetupSession Session => _session;
    public ObservableCollection<AdbSetupStepItem> Steps { get; }
    public AdbSetupStepItem DeveloperItem => Steps[0];
    public AdbSetupStepItem WirelessItem => Steps[1];
    public AdbSetupStepItem PairItem => Steps[2];
    public Bitmap? AppIcon { get; } = ImageUtil.AppIcon();
    public string PhoneName => _f.DeviceNameForWindows;

    /// <summary>What the primary button starts once ADB is ready (default: mirroring).</summary>
    public MirrorRequest? Then { get; set; }
    public Action? Close { get; set; }

    [ObservableProperty] public partial AdbSetupStep Step { get; private set; }
    [ObservableProperty] public partial string Title { get; private set; } = "";
    [ObservableProperty] public partial string Subtitle { get; private set; } = "";
    [ObservableProperty] public partial string Status { get; private set; } = "";
    /// <summary>Why the card opened (adb's words), shown small until the first step is done.</summary>
    [ObservableProperty] public partial string Note { get; set; } = "";
    [ObservableProperty] public partial Bitmap? QrImage { get; private set; }
    [ObservableProperty] public partial string Code { get; set; } = "";
    [ObservableProperty] public partial bool IsCodeMode { get; private set; }
    [ObservableProperty] public partial bool OpenedOnPhone { get; private set; }

    public bool IsPhoneStep => Step == AdbSetupStep.Phone;
    public bool IsChecking => Step == AdbSetupStep.Checking;
    public bool IsDeveloperStep => Step == AdbSetupStep.DeveloperOptions;
    public bool IsWirelessStep => Step == AdbSetupStep.WirelessDebugging;
    public bool IsQrStep => Step == AdbSetupStep.Pair && !IsCodeMode;
    public bool IsCodeStep => Step == AdbSetupStep.Pair && IsCodeMode;
    public bool IsConnectingStep => Step == AdbSetupStep.Connecting;
    public bool IsReady => Step == AdbSetupStep.Ready;
    public bool IsWorking => IsChecking || IsConnectingStep;
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public bool HasNote => !string.IsNullOrWhiteSpace(Note) && !IsReady;
    public bool CanSubmitCode => Code.Length == 6 && !_f.Pairing.IsBusy;
    /// <summary>The phone shows its pairing-code screen (it reported the port), or an older phone app.</summary>
    public bool PhoneShowsCode => _session.PhoneShowsPairingCode || _session.OldPhoneApp;
    public string ReadyButtonText => Then?.Kind switch
    {
        MirrorKind.Desktop => "Open desktop",
        MirrorKind.App => "Open " + (Then.WindowTitle ?? "the app"),
        _ => "Mirror",
    };

    partial void OnStepChanged(AdbSetupStep oldValue, AdbSetupStep newValue)
    {
        // Why the card opened only matters until the user gets past the first real step.
        if (oldValue is not (AdbSetupStep.Phone or AdbSetupStep.Checking) && newValue > oldValue) Note = "";
        NotifySteps();
    }
    partial void OnIsCodeModeChanged(bool value) => NotifySteps();
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));
    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));

    partial void OnCodeChanged(string value)
    {
        var digits = new string((value ?? "").Where(char.IsAsciiDigit).Take(6).ToArray());
        if (digits != value) { Code = digits; return; }
        OnPropertyChanged(nameof(CanSubmitCode));
        // Six digits typed and the phone is ready for them: pair right away.
        if (digits.Length == 6 && _session.PhoneShowsPairingCode && !_f.Pairing.IsBusy) _ = SubmitCode();
    }

    void NotifySteps()
    {
        foreach (var n in new[] { nameof(IsPhoneStep), nameof(IsChecking), nameof(IsDeveloperStep), nameof(IsWirelessStep), nameof(IsQrStep),
                                  nameof(IsCodeStep), nameof(IsConnectingStep), nameof(IsReady), nameof(IsWorking), nameof(HasNote),
                                  nameof(ReadyButtonText), nameof(PhoneShowsCode), nameof(CanSubmitCode) })
            OnPropertyChanged(n);
    }

    void Refresh()
    {
        var s = _session;
        Step = s.Step;
        IsCodeMode = s.CodeMode;
        OpenedOnPhone = s.OpenedOnPhone is not null;
        var phone = PhoneName;
        (Title, Subtitle) = Step switch
        {
            AdbSetupStep.Phone => ("Connect your phone first", $"Open H.I.B on {phone}. Mirroring is set up through it."),
            AdbSetupStep.Checking => ("Checking your phone…", "One moment."),
            AdbSetupStep.DeveloperOptions => ("Turn on Developer options", "On your phone, open About phone › Software information and tap Build number 7 times."),
            AdbSetupStep.WirelessDebugging => ("Turn on Wireless debugging", "In Developer options, switch on Wireless debugging."),
            AdbSetupStep.Pair when IsCodeMode => ("Pair with a code", "On your phone: Wireless debugging → Pair device with pairing code. Type the 6 digits here."),
            AdbSetupStep.Pair => ("Pair this PC", "On your phone: Wireless debugging → Pair device with QR code"),
            AdbSetupStep.Connecting => ("Connecting…", $"Talking to {phone} over Wireless debugging."),
            AdbSetupStep.Ready => ("Mirroring is ready", $"This PC is paired with {phone}. You won't need to do this again."),
            _ => ("Set up mirroring", ""),
        };
        Status = s.Status;

        var dev = s.DeveloperDone;
        var wifi = s.WirelessDone;
        var paired = s.PairedDone;
        Steps[0].IsDone = dev;
        Steps[0].IsCurrent = !dev && Step == AdbSetupStep.DeveloperOptions;
        Steps[1].IsDone = wifi;
        Steps[1].IsCurrent = !wifi && Step == AdbSetupStep.WirelessDebugging;
        Steps[2].IsDone = paired;
        Steps[2].IsCurrent = !paired && Step is AdbSetupStep.Pair or AdbSetupStep.Connecting;

        var qr = _f.Pairing.QrText;
        if (qr != _qrFor)
        {
            _qrFor = qr;
            QrImage = string.IsNullOrEmpty(qr) ? null : PairViewModel.RenderQr(qr);
        }
        NotifySteps();
    }

    [RelayCommand] Task ShowDeveloperOptions() => _session.OpenOnPhoneAsync("developer");
    [RelayCommand] Task OpenWirelessDebugging() => _session.OpenOnPhoneAsync("wireless");
    [RelayCommand] void UseCode() => _session.UseCode(true);
    [RelayCommand] void UseQr() => _session.UseCode(false);

    [RelayCommand]
    async Task SubmitCode()
    {
        if (Code.Length != 6) return;
        await _session.SubmitCodeAsync(Code);
    }

    /// <summary>"Mirror" on the ready step: start what the user asked for and close the card.</summary>
    [RelayCommand]
    async Task Go()
    {
        var then = Then ?? new MirrorRequest(MirrorKind.Mirror, _f.DeviceNameForWindows);
        Close?.Invoke();
        await _f.Launcher.StartAsync(then);
    }

    [RelayCommand] void Dismiss() => Close?.Invoke();

    public void Dispose() => _session.Dispose();
}
