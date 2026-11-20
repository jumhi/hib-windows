using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HIB.Features.Abstractions;
using HIB.Features.Adb;

namespace HIB.Features.Mirror;

/// <summary>
/// Play Phone Audio on this PC: scrcpy audio-only mode. scrcpy captures playback through ADB's
/// shell identity, so Android never shows the "share your screen" prompt.
/// <c>scrcpy -s &lt;serial&gt; --no-video --no-control --no-window --audio-source=playback --audio-codec=opus --audio-buffer=80 [--audio-dup]</c>
/// </summary>
public sealed class PhoneAudioBridge : ObservableObject, IDisposable
{
    private const string KeepOnPhoneKey = "phoneAudioKeepOnPhone";
    private readonly AdbService _adb;
    private readonly IPhoneLink? _link;
    private readonly ISettingsStore _settings;
    private readonly object _lock = new();
    private Process? _process;
    private bool _stopRequested;
    private readonly StringBuilder _outputTail = new();

    private bool _isActive;
    private bool _isStarting;
    private string? _lastError;

    public PhoneAudioBridge(AdbService adb, IPhoneLink? link, ISettingsStore settings)
    {
        _adb = adb;
        _link = link;
        _settings = settings;
        _adb.PropertyChanged += OnAdbChanged;
        if (_link != null) _link.MessageReceived += OnMessage;
    }

    public bool IsActive { get => _isActive; private set => SetProperty(ref _isActive, value); }
    public bool IsStarting { get => _isStarting; private set => SetProperty(ref _isStarting, value); }
    public string? LastError { get => _lastError; private set => SetProperty(ref _lastError, value); }

    /// <summary>Keep audio playing on the phone as well (<c>--audio-dup</c>, Android 13+).</summary>
    public bool KeepPlayingOnPhone
    {
        get => _settings.Get<bool?>(KeepOnPhoneKey) ?? false;
        set { _settings.Set(KeepOnPhoneKey, value); OnPropertyChanged(); }
    }

    public bool CanStart => _adb.IsConnected;

    /// <summary>The scrcpy arguments used for a serial (exposed for tests and the ADB console).</summary>
    public static List<string> BuildArguments(string serial, bool keepPlayingOnPhone)
    {
        var args = new List<string>
        {
            "-s", serial,
            "--no-video",
            "--no-control",
            "--no-window",
            "--audio-source=playback",
            "--audio-codec=opus",
            "--audio-buffer=80",
        };
        if (keepPlayingOnPhone) args.Add("--audio-dup");
        return args;
    }

    private void OnAdbChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Stop cleanly when ADB drops; scrcpy would exit with an error anyway.
        if (e.PropertyName == nameof(AdbService.IsConnected) && !_adb.IsConnected) Stop();
    }

    private void OnMessage(string type, JsonElement data)
    {
        if (type != "phoneAudioControl") return;
        var action = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString() : "toggle";
        HibFeatures.Dispatch(() =>
        {
            switch (action)
            {
                case "start": Start(); break;
                case "stop": Stop(); break;
                default: Toggle(); break;
            }
        });
    }

    public void Toggle()
    {
        if (IsActive || IsStarting) Stop(); else Start();
    }

    public void Start()
    {
        lock (_lock) { if (_process != null) return; }
        LastError = null;
        var serial = _adb.CurrentSerial;
        if (serial is null) { Fail("Connect the phone with ADB first (Wireless Debugging)."); return; }
        var scrcpy = _adb.Locator.ScrcpyPath;
        if (scrcpy is null)
        {
            Fail(OperatingSystem.IsWindows()
                ? "scrcpy isn't installed. Put it in the tools folder or install it with: winget install Genymobile.scrcpy"
                : "scrcpy isn't installed. Install it with: brew install scrcpy");
            return;
        }
        var adbPath = _adb.AdbPath;
        if (adbPath is null) { Fail("adb isn't installed."); return; }

        var audioArgs = BuildArguments(serial, KeepPlayingOnPhone);
        HibFeatures.Trace($"[audio] Launching scrcpy: {scrcpy} {string.Join(' ', audioArgs)}");
        var psi = ProcessRunner.CreateStartInfo(scrcpy, audioArgs, AdbLocator.AdbEnvironment(adbPath, scrcpy));
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        lock (_outputTail) _outputTail.Clear();

        void OnLine(object? _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (_outputTail)
            {
                _outputTail.Append(e.Data).Append('\n');
                if (_outputTail.Length > 2000) _outputTail.Remove(0, _outputTail.Length - 2000);
            }
            HibLog.Write("scrcpy-audio", e.Data);
            if (IsStarting && (e.Data.Contains("Device:") || e.Data.Contains("audio")))
                MarkActive(process);
        }
        process.OutputDataReceived += OnLine;
        process.ErrorDataReceived += OnLine;
        process.Exited += (_, _) => OnExited(process);

        try
        {
            _stopRequested = false;
            IsStarting = true;
            process.Start();
            try { process.StandardInput.Close(); } catch { }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            lock (_lock) _process = process;
            // scrcpy prints little once streaming; treat a live process as active shortly after.
            _ = Task.Delay(2500).ContinueWith(_ =>
            {
                bool running;
                try { running = !process.HasExited; } catch { running = false; }
                if (running) MarkActive(process);
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            IsStarting = false;
            process.Dispose();
            Fail(ex.Message);
        }
    }

    private void MarkActive(Process process)
    {
        lock (_lock) { if (!ReferenceEquals(_process, process) && _process != null) return; }
        if (!IsStarting) return;
        IsStarting = false;
        IsActive = true;
        NotifyPhone();
    }

    private void OnExited(Process finished)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_process, finished)) return;
            _process = null;
        }
        var wasUserStop = _stopRequested;
        _stopRequested = false;
        IsStarting = false;
        IsActive = false;
        int code;
        try { code = finished.ExitCode; } catch { code = -1; }
        if (!wasUserStop && code != 0)
        {
            string tail;
            lock (_outputTail) tail = _outputTail.ToString();
            var detail = ScrcpyOutput.ExtractError(tail) ?? $"scrcpy stopped (code {code}).";
            LastError = detail;
        }
        NotifyPhone();
        finished.Dispose();
    }

    public void Stop()
    {
        Process? process;
        lock (_lock) process = _process;
        if (process is null)
        {
            IsActive = false;
            IsStarting = false;
            return;
        }
        _stopRequested = true;
        ProcessRunner.KillQuietly(process);
    }

    private void Fail(string message)
    {
        LastError = message;
        IsActive = false;
        NotifyPhone();
    }

    private void NotifyPhone()
    {
        if (_link is null || !_link.IsConnected) return;
        var data = new JsonObject { ["active"] = IsActive || IsStarting };
        if (LastError != null) data["error"] = LastError;
        _ = _link.SendAsync("phoneAudioState", data);
    }

    public void Dispose()
    {
        _adb.PropertyChanged -= OnAdbChanged;
        if (_link != null) _link.MessageReceived -= OnMessage;
        _stopRequested = true;
        Process? p;
        lock (_lock) { p = _process; _process = null; }
        if (p != null) ProcessRunner.KillQuietly(p);
    }
}
