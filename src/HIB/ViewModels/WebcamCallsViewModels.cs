using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HIB.Features.Adb;
using HIB.Features.CallRelay;
using HIB.Features.Camera;
using HIB.Services;

namespace HIB.ViewModels;

/// <summary>"Webcam" page: USB webcam mode, the wireless H.I.B Camera window, virtual camera notes.</summary>
public partial class WebcamViewModel : ViewModelBase
{
    static readonly CameraQuality[] Qualities = [CameraQuality.Auto, CameraQuality.Hd720, CameraQuality.Hd1080, CameraQuality.Uhd4k, CameraQuality.Ar16x9, CameraQuality.Ar4x3];
    readonly FeatureHost _f;
    bool _loading;

    public WebcamViewModel(AppController app, FeatureHost f)
    {
        _f = f;
        Camera.PropertyChanged += (_, _) => Ui.Post(Refresh);
        Adb.PropertyChanged += (_, e) => Ui.Post(() =>
        {
            Refresh();
            if (e.PropertyName == nameof(AdbService.IsConnected) && Adb.IsConnected && Camera.Support == UsbWebcamSupport.Unknown)
                _ = Camera.CheckUsbAsync();
        });
        LoadOptions();
        Refresh();
        if (Adb.IsConnected) _ = Camera.CheckUsbAsync();
    }

    public PhoneCameraService Camera => _f.Camera;
    public AdbService Adb => _f.Adb;
    public string[] QualityLabels { get; } = Array.ConvertAll(Qualities, PhoneCameraOptions.QualityLabel);
    public string[] FpsLabels { get; } = Array.ConvertAll(PhoneCameraOptions.FpsChoices, f => $"{f} fps");
    public string[] RotationLabels { get; } = Array.ConvertAll(PhoneCameraOptions.RotationChoices, r => $"{r}°");
    public string[] FacingLabels { get; } = ["Front", "Back"];

    [ObservableProperty] public partial string UsbStatus { get; set; } = "";
    [ObservableProperty] public partial bool UsbCanStart { get; set; }
    [ObservableProperty] public partial bool UsbCanStop { get; set; }
    [ObservableProperty] public partial string CameraButton { get; set; } = "Open camera window";
    [ObservableProperty] public partial bool CameraCanToggle { get; set; }
    [ObservableProperty] public partial int FacingIndex { get; set; }
    [ObservableProperty] public partial int QualityIndex { get; set; }
    [ObservableProperty] public partial int FpsIndex { get; set; }
    [ObservableProperty] public partial int RotationIndex { get; set; }
    [ObservableProperty] public partial bool MicAudio { get; set; }
    [ObservableProperty] public partial bool Borderless { get; set; }
    [ObservableProperty] public partial bool OnTop { get; set; }

    public bool ObsInstalled => PhoneCameraService.IsObsInstalled;
    public bool ObsMissing => !ObsInstalled;

    void LoadOptions()
    {
        _loading = true;
        var o = Camera.Options;
        FacingIndex = o.Facing == CameraFacing.Front ? 0 : 1;
        QualityIndex = Math.Max(0, Array.IndexOf(Qualities, o.Quality));
        FpsIndex = Math.Max(0, Array.IndexOf(PhoneCameraOptions.FpsChoices, o.Fps));
        RotationIndex = Math.Max(0, Array.IndexOf(PhoneCameraOptions.RotationChoices, o.Rotation));
        MicAudio = o.WithMicAudio;
        Borderless = o.Borderless;
        OnTop = o.AlwaysOnTop;
        _loading = false;
    }

    void SaveOptions()
    {
        if (_loading) return;
        Camera.Options = new PhoneCameraOptions
        {
            Facing = FacingIndex == 1 ? CameraFacing.Back : CameraFacing.Front,
            Quality = Qualities[Math.Clamp(QualityIndex, 0, Qualities.Length - 1)],
            Fps = PhoneCameraOptions.FpsChoices[Math.Clamp(FpsIndex, 0, PhoneCameraOptions.FpsChoices.Length - 1)],
            Rotation = PhoneCameraOptions.RotationChoices[Math.Clamp(RotationIndex, 0, PhoneCameraOptions.RotationChoices.Length - 1)],
            WithMicAudio = MicAudio,
            Borderless = Borderless,
            AlwaysOnTop = OnTop,
        };
    }

    partial void OnFacingIndexChanged(int value) => SaveOptions();
    partial void OnQualityIndexChanged(int value) => SaveOptions();
    partial void OnFpsIndexChanged(int value) => SaveOptions();
    partial void OnRotationIndexChanged(int value) => SaveOptions();
    partial void OnMicAudioChanged(bool value) => SaveOptions();
    partial void OnBorderlessChanged(bool value) => SaveOptions();
    partial void OnOnTopChanged(bool value) => SaveOptions();

    void Refresh()
    {
        var active = Camera.IsUsbWebcamActive;
        UsbStatus = Camera.Support switch
        {
            UsbWebcamSupport.Checking => "Checking the phone…",
            UsbWebcamSupport.NoAdb => "ADB isn't connected.",
            UsbWebcamSupport.Supported => active ? "Webcam mode is ON." : $"Supported. USB is currently: {string.Join(", ", Camera.Functions)}",
            UsbWebcamSupport.Unsupported => "This phone doesn't offer Android's USB webcam mode (ro.usb.uvc.enabled is off). Use the wireless camera below.",
            _ => Adb.IsConnected ? "Not checked yet." : "Connect ADB (Wireless debugging) to check.",
        };
        UsbCanStart = !Camera.UsbBusy && Camera.Support == UsbWebcamSupport.Supported && !active;
        UsbCanStop = !Camera.UsbBusy && active;
        CameraButton = Camera.IsRunning ? "Close camera" : "Open camera window";
        CameraCanToggle = Camera.IsRunning || Adb.IsConnected;
    }

    [RelayCommand] Task CheckUsb() => Camera.CheckUsbAsync();
    [RelayCommand] Task StartUsb() => Camera.StartUsbWebcamAsync();
    [RelayCommand] Task StopUsb() => Camera.StopUsbWebcamAsync();
    [RelayCommand] void ToggleCamera() => Camera.Toggle();
    [RelayCommand] Task ListCameras() => Camera.ListCamerasAsync();
    [RelayCommand] void OpenObs() => PhoneCameraService.OpenObs();
}

/// <summary>"Calls" page: dialer, contacts, answer/decline/end and call audio on this PC.</summary>
public partial class CallsViewModel : ViewModelBase
{
    readonly FeatureHost _f;
    readonly AppController _app;

    public CallsViewModel(AppController app, FeatureHost f)
    {
        _app = app;
        _f = f;
        Calls.PropertyChanged += (_, _) => Ui.Post(Refresh);
        Audio.PropertyChanged += (_, _) => Ui.Post(Refresh);
        Adb.PropertyChanged += (_, _) => Ui.Post(Refresh);
        app.Core.Link.ConnectionChanged += (_, _) => Ui.Post(Refresh);
        HearThisCall = Audio.AutoHearCalls;
        Refresh();
    }

    public CallRelayService Calls => _f.Calls;
    public CallAudioRelay Audio => _f.Calls.Audio;
    public AdbService Adb => _f.Adb;

    [ObservableProperty] public partial string Number { get; set; } = "";
    [ObservableProperty] public partial string ContactQuery { get; set; } = "";
    [ObservableProperty] public partial bool HearThisCall { get; set; }
    [ObservableProperty] public partial bool CanCall { get; set; }
    [ObservableProperty] public partial bool CanControl { get; set; }
    [ObservableProperty] public partial string AudioStatus { get; set; } = "";
    [ObservableProperty] public partial string AudioButton { get; set; } = "Play call audio now";
    [ObservableProperty] public partial bool HasContacts { get; set; }

    public bool AutoHear
    {
        get => Audio.AutoHearCalls;
        set { Audio.AutoHearCalls = value; OnPropertyChanged(); }
    }

    partial void OnNumberChanged(string value) => Refresh();
    partial void OnContactQueryChanged(string value) => Calls.SearchContacts(value);

    void Refresh()
    {
        CanCall = DialNumber.Sanitize(Number) is not null && !Calls.IsDialing && Adb.CurrentSerial is not null;
        CanControl = _app.Core.Link.IsConnected || Adb.CurrentSerial is not null;
        HasContacts = Calls.Contacts.Count > 0;
        AudioButton = Audio.IsActive || Audio.IsStarting ? "Stop call audio" : "Play call audio now";
        AudioStatus = Audio.IsActive ? $"Playing call audio ({Audio.ActiveSource ?? "voice-call"})"
            : Audio.IsStarting ? "Starting call audio…"
            : Audio.AutoHearCalls ? "Will start when a call is answered or placed" : "Off";
    }

    [RelayCommand] Task Call() => DialNumber.Sanitize(Number) is null ? Task.CompletedTask : Calls.DialAsync(Number, HearThisCall);
    [RelayCommand] void PickContact(PhoneContact c) => Number = c.Number;
    [RelayCommand] Task AnswerHere() => Calls.AnswerHereAsync();
    [RelayCommand] Task Answer() => Calls.PerformAsync(CallKeyAction.Answer);
    [RelayCommand] Task Decline() => Calls.PerformAsync(CallKeyAction.Decline);
    [RelayCommand] Task End() => Calls.PerformAsync(CallKeyAction.End);
    [RelayCommand] void ToggleAudio() => Audio.Toggle();
}
