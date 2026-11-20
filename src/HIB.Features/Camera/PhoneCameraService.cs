using System.Diagnostics;
using System.Text;
using HIB.Features.Abstractions;
using HIB.Features.Adb;

namespace HIB.Features.Camera;

public enum UsbWebcamSupport { Unknown, Checking, Supported, Unsupported, NoAdb }

/// <summary>
/// Phone as webcam.
/// <list type="bullet">
/// <item>USB: Android 14+ UVC gadget (<c>svc usb setFunctions uvc</c>); Zoom/Teams/Meet see a normal webcam.</item>
/// <item>Wireless: scrcpy <c>--video-source=camera</c> in a borderless "H.I.B Camera" window for
/// "share window" or an OBS Window Capture.</item>
/// </list>
/// A real wireless virtual camera on Windows 11 would need <c>MFCreateVirtualCamera</c> plus a
/// COM-registered native media source DLL (IMFMediaSource running inside the Frame Server service).
/// That can't be done robustly from managed code here, so H.I.B points to OBS Virtual Camera instead.
/// </summary>
public sealed class PhoneCameraService : ObservableObject, IDisposable
{
    const string OptionsKey = "hibPhoneCameraOptions";
    const string PreviousUsbKey = "hibUsbWebcamPreviousFunctions";
    readonly AdbService _adb;
    readonly ISettingsStore _settings;
    readonly object _lock = new();
    readonly StringBuilder _tail = new();
    Process? _process;
    bool _stopRequested;

    UsbWebcamSupport _support;
    IReadOnlyList<string> _functions = Array.Empty<string>();
    bool _usbBusy, _isRunning, _isListing;
    string? _usbMessage, _lastError, _cameraList;

    public PhoneCameraService(AdbService adb, ISettingsStore settings)
    {
        _adb = adb;
        _settings = settings;
    }

    // ---------------- options ----------------

    public PhoneCameraOptions Options
    {
        get => _settings.Get<PhoneCameraOptions>(OptionsKey) ?? new PhoneCameraOptions();
        set { _settings.Set(OptionsKey, value); OnPropertyChanged(); }
    }

    // ---------------- USB webcam ----------------

    public UsbWebcamSupport Support { get => _support; private set => SetProperty(ref _support, value); }
    public IReadOnlyList<string> Functions { get => _functions; private set { if (SetProperty(ref _functions, value)) OnPropertyChanged(nameof(IsUsbWebcamActive)); } }
    public bool IsUsbWebcamActive => UsbWebcam.IsWebcamActive(Functions);
    public bool UsbBusy { get => _usbBusy; private set => SetProperty(ref _usbBusy, value); }
    public string? UsbMessage { get => _usbMessage; private set => SetProperty(ref _usbMessage, value); }

    /// <summary>Read-only check: <c>getprop ro.usb.uvc.enabled</c> and <c>svc usb getFunctions</c>.</summary>
    public async Task CheckUsbAsync()
    {
        if (_adb.CurrentSerial is null) { Support = UsbWebcamSupport.NoAdb; return; }
        Support = UsbWebcamSupport.Checking;
        var prop = await _adb.RunOnDeviceAsync(UsbWebcam.UvcPropArguments, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var fn = await _adb.RunOnDeviceAsync(UsbWebcam.GetFunctionsArguments, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (prop.ExitCode == -1 && fn.ExitCode == -1) { Support = UsbWebcamSupport.NoAdb; return; }
        Functions = UsbWebcam.ParseFunctions(fn.Combined);
        Support = UsbWebcam.IsUvcSupported(prop.StdOutText) ? UsbWebcamSupport.Supported : UsbWebcamSupport.Unsupported;
    }

    public async Task StartUsbWebcamAsync()
    {
        var serial = _adb.CurrentSerial;
        if (serial is null) { UsbMessage = "Connect the phone with ADB first."; return; }
        UsbBusy = true;
        try
        {
            await CheckUsbAsync().ConfigureAwait(false);
            if (Support != UsbWebcamSupport.Supported)
            {
                UsbMessage = "This phone doesn't offer Android's USB webcam mode (ro.usb.uvc.enabled isn't true).";
                return;
            }
            if (!IsUsbWebcamActive) _settings.Set(PreviousUsbKey, Functions.ToArray());
            var r = await _adb.RunAsync(UsbWebcam.SetFunctionsArguments(serial, "uvc"), TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            await Task.Delay(1500).ConfigureAwait(false);
            await CheckUsbAsync().ConfigureAwait(false);
            UsbMessage = IsUsbWebcamActive
                ? "Webcam mode is on. Pick the phone as the camera in your video app."
                : "The phone didn't switch to webcam mode. " + (r.Combined.Trim() is { Length: > 0 } d ? d : "Is the USB cable plugged in?");
        }
        finally { UsbBusy = false; }
    }

    public async Task StopUsbWebcamAsync()
    {
        var serial = _adb.CurrentSerial;
        if (serial is null) { UsbMessage = "ADB isn't connected. On the phone, tap the USB notification and choose File transfer."; return; }
        UsbBusy = true;
        try
        {
            var restore = UsbWebcam.RestoreFunction(_settings.Get<string[]>(PreviousUsbKey) ?? []);
            await _adb.RunAsync(UsbWebcam.SetFunctionsArguments(serial, restore), TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            await Task.Delay(1000).ConfigureAwait(false);
            await CheckUsbAsync().ConfigureAwait(false);
            UsbMessage = IsUsbWebcamActive ? "The phone is still in webcam mode."
                : $"Webcam mode is off (USB set back to {(restore.Length == 0 ? "charging" : restore)}).";
        }
        finally { UsbBusy = false; }
    }

    // ---------------- wireless camera window ----------------

    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }
    public string? LastError { get => _lastError; private set => SetProperty(ref _lastError, value); }
    public string? CameraList { get => _cameraList; private set => SetProperty(ref _cameraList, value); }
    public bool IsListing { get => _isListing; private set => SetProperty(ref _isListing, value); }

    public void Toggle() { if (IsRunning) Stop(); else Start(); }

    public void Start(CameraFacing? facing = null)
    {
        if (facing is { } f) Options = Options with { Facing = f };
        Stop();
        LastError = null;
        var serial = _adb.CurrentSerial;
        if (serial is null) { LastError = "Connect the phone with ADB first (Wireless debugging)."; return; }
        var scrcpy = _adb.Locator.ScrcpyPath;
        if (scrcpy is null) { LastError = "scrcpy isn't installed (it ships in the tools folder)."; return; }
        var env = _adb.AdbPath is { } adbPath ? AdbLocator.AdbEnvironment(adbPath) : null;
        var process = new Process { StartInfo = ProcessRunner.CreateStartInfo(scrcpy, Options.Arguments(serial), env), EnableRaisingEvents = true };
        lock (_tail) _tail.Clear();
        void OnLine(object? _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (_tail)
            {
                _tail.Append(e.Data).Append('\n');
                if (_tail.Length > 3000) _tail.Remove(0, _tail.Length - 3000);
            }
        }
        process.OutputDataReceived += OnLine;
        process.ErrorDataReceived += OnLine;
        process.Exited += (_, _) =>
        {
            lock (_lock) { if (!ReferenceEquals(_process, process)) return; _process = null; }
            IsRunning = false;
            int code;
            try { code = process.ExitCode; } catch { code = -1; }
            if (!_stopRequested && code != 0)
            {
                string tail;
                lock (_tail) tail = _tail.ToString();
                LastError = tail.Split('\n').LastOrDefault(l => l.Contains("ERROR")) ?? $"scrcpy stopped (code {code}).";
            }
            process.Dispose();
        };
        try
        {
            _stopRequested = false;
            process.Start();
            try { process.StandardInput.Close(); } catch { }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            lock (_lock) _process = process;
            IsRunning = true;
        }
        catch (Exception ex)
        {
            process.Dispose();
            LastError = "Failed to start scrcpy: " + ex.Message;
        }
    }

    public void Stop()
    {
        Process? p;
        lock (_lock) { p = _process; _process = null; }
        if (p is null) return;
        _stopRequested = true;
        ProcessRunner.KillQuietly(p);
        IsRunning = false;
    }

    /// <summary>Read-only: <c>scrcpy --list-cameras</c> (doesn't open a camera).</summary>
    public async Task ListCamerasAsync()
    {
        var serial = _adb.CurrentSerial;
        var scrcpy = _adb.Locator.ScrcpyPath;
        if (serial is null || scrcpy is null) { CameraList = "Connect the phone with ADB first."; return; }
        IsListing = true;
        try
        {
            var env = _adb.AdbPath is { } adbPath ? AdbLocator.AdbEnvironment(adbPath) : null;
            var r = await ProcessRunner.RunAsync(scrcpy, PhoneCameraOptions.ListCamerasArguments(serial), TimeSpan.FromSeconds(30), env).ConfigureAwait(false);
            var lines = r.Combined.Split('\n').Select(l => l.Trim()).Where(l => l.Contains("--camera-id=")).ToList();
            CameraList = lines.Count > 0 ? string.Join("\n", lines)
                : r.Combined.Split('\n').LastOrDefault(l => l.Contains("ERROR")) ?? "No cameras reported.";
        }
        finally { IsListing = false; }
    }

    // ---------------- OBS virtual camera (detection only) ----------------

    /// <summary>obs64.exe, when OBS Studio is installed in the usual place.</summary>
    public static string? ObsPath
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return null;
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                if (string.IsNullOrEmpty(root)) continue;
                var exe = Path.Combine(root, "obs-studio", "bin", "64bit", "obs64.exe");
                if (File.Exists(exe)) return exe;
            }
            return null;
        }
    }

    public static bool IsObsInstalled => ObsPath is not null;

    public static void OpenObs()
    {
        if (ObsPath is not { } exe) return;
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! }); } catch { }
    }

    public void Dispose() => Stop();
}
