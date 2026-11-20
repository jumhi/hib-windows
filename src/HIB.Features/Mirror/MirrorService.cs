using System.Diagnostics;
using System.Text;
using HIB.Features.Abstractions;
using HIB.Features.Adb;

namespace HIB.Features.Mirror;

/// <summary>scrcpy mirroring preferences (keys follow the Mac's UserDefaults names).</summary>
public sealed class MirrorSettings
{
    private readonly ISettingsStore _s;
    public MirrorSettings(ISettingsStore s) { _s = s; }

    public int BitrateMbps { get => _s.Get<int?>("scrcpyBitrate") ?? 4; set => _s.Set("scrcpyBitrate", value); }
    public int Resolution { get => _s.Get<int?>("scrcpyResolution") ?? 1200; set => _s.Set("scrcpyResolution", value); }
    public bool AlwaysOnTop { get => _s.Get<bool?>("scrcpyOnTop") ?? false; set => _s.Set("scrcpyOnTop", value); }
    public bool StayAwake { get => _s.Get<bool?>("stayAwake") ?? false; set => _s.Set("stayAwake", value); }
    public bool TurnScreenOff { get => _s.Get<bool?>("turnScreenOff") ?? false; set => _s.Set("turnScreenOff", value); }
    public bool NoAudio { get => _s.Get<bool?>("noAudio") ?? false; set => _s.Set("noAudio", value); }
    public bool ManualPosition { get => _s.Get<bool?>("manualPosition") ?? false; set => _s.Set("manualPosition", value); }
    public int[] ManualPositionCoords { get => _s.Get<int[]>("manualPositionCoords") ?? [0, 0]; set => _s.Set("manualPositionCoords", value); }
    /// <summary>Keep the app running on the virtual display after the window closes (--no-vd-destroy-content).</summary>
    public bool ContinueApp { get => _s.Get<bool?>("continueApp") ?? false; set => _s.Set("continueApp", value); }
    /// <summary>Direct key input (--keyboard=uhid).</summary>
    public bool DirectKeyInput { get => _s.Get<bool?>("directKeyInput") ?? false; set => _s.Set("directKeyInput", value); }
    public string DesktopDpi { get => _s.Get<string>("scrcpyDesktopDpi") ?? "192"; set => _s.Set("scrcpyDesktopDpi", value); }
}

public enum MirrorKind { Mirror, Desktop, App, Camera }

/// <summary>What to launch.</summary>
public sealed record MirrorRequest(MirrorKind Kind, string DeviceName, string? Package = null,
                                   string? WindowTitle = null, string? CameraFacing = null);

/// <summary>One running scrcpy window.</summary>
public sealed class MirrorSession
{
    internal MirrorSession(MirrorRequest request, Process process, IReadOnlyList<string> args)
    {
        Request = request;
        Process = process;
        Arguments = args;
    }
    public Guid Id { get; } = Guid.NewGuid();
    public MirrorRequest Request { get; }
    public IReadOnlyList<string> Arguments { get; }
    internal Process Process { get; }
    internal StringBuilder Output { get; } = new();
    public bool IsRunning { get { try { return !Process.HasExited; } catch { return false; } } }
}

public sealed record MirrorExit(MirrorSession Session, int ExitCode, string Output, bool NeedsPairing);

/// <summary>
/// scrcpy mirroring (ADBConnector.startScrcpy): full mirror, desktop mode (new display), a single app
/// in its own window (<c>--new-display --start-app</c>), or the phone camera. scrcpy mirrors through
/// ADB, so Android never shows its screen-share prompt.
/// </summary>
public sealed class MirrorService : ObservableObject, IDisposable
{
    private readonly AdbService _adb;
    private readonly List<MirrorSession> _sessions = new();
    private string? _lastError;

    public MirrorService(AdbService adb, ISettingsStore settings)
    {
        _adb = adb;
        Settings = new MirrorSettings(settings);
    }

    public MirrorSettings Settings { get; }
    public string? LastError { get => _lastError; private set => SetProperty(ref _lastError, value); }

    public IReadOnlyList<MirrorSession> Sessions { get { lock (_sessions) return _sessions.ToList(); } }
    public bool IsMirroring => Sessions.Any(s => s.IsRunning);

    /// <summary>scrcpy exited. When <see cref="MirrorExit.NeedsPairing"/>, offer "Pair with ADB QR".</summary>
    public event Action<MirrorExit>? MirrorExited;

    public Task<MirrorSession?> StartMirrorAsync(string deviceName) => StartAsync(new MirrorRequest(MirrorKind.Mirror, deviceName));
    public Task<MirrorSession?> StartDesktopAsync(string deviceName) => StartAsync(new MirrorRequest(MirrorKind.Desktop, deviceName));
    public Task<MirrorSession?> StartAppAsync(string deviceName, string package, string? label = null)
        => StartAsync(new MirrorRequest(MirrorKind.App, deviceName, package, label));
    public Task<MirrorSession?> StartCameraAsync(string deviceName, string facing = "back")
        => StartAsync(new MirrorRequest(MirrorKind.Camera, deviceName, CameraFacing: facing));

    internal static string CleanDeviceName(string name) => name.Replace("'s", "").Replace("'", "");

    /// <summary>Builds the scrcpy argument list exactly like the Mac. <paramref name="wiredSerial"/>
    /// wins when set, else <c>--tcpip=ip:port</c>.</summary>
    public static List<string> BuildArguments(MirrorRequest request, MirrorSettings s, string? wiredSerial, string tcpAddress)
    {
        var device = CleanDeviceName(request.DeviceName);
        var title = request.WindowTitle != null ? $"{request.WindowTitle} · {device}"
                  : request.Kind == MirrorKind.Camera ? $"Galaxy Camera · {device}" : device;
        var args = new List<string>
        {
            $"--window-title={title}",
            $"--video-bit-rate={s.BitrateMbps}M",
            "--video-codec=h265",
            "--no-power-on",
        };
        args.Add(wiredSerial != null ? $"--serial={wiredSerial}" : $"--tcpip={tcpAddress}");

        if (s.ManualPosition)
        {
            var c = s.ManualPositionCoords;
            args.Add($"--window-x={(c.Length > 0 ? c[0] : 0)}");
            args.Add($"--window-y={(c.Length > 1 ? c[1] : 0)}");
        }
        if (s.AlwaysOnTop) args.Add("--always-on-top");
        if (request.Kind == MirrorKind.Camera)
        {
            // Camera capture has no device control; scrcpy refuses control-only options with it.
            args.Add("--video-source=camera");
            args.Add($"--camera-facing={request.CameraFacing ?? "back"}");
            args.Add("--no-control");
            args.Add("--no-audio");
        }
        else
        {
            if (s.StayAwake) args.Add("--stay-awake");
            if (s.TurnScreenOff) args.Add("--turn-screen-off");
            if (s.NoAudio) args.Add("--no-audio");
            if (s.DirectKeyInput) args.Add("--keyboard=uhid");
        }

        var display = $"/{s.DesktopDpi}";
        if (request.Kind == MirrorKind.Desktop)
        {
            args.Add($"--new-display={display}");
            args.Add("-x");
        }
        else if (request.Kind == MirrorKind.Mirror)
        {
            args.Add($"--max-size={s.Resolution}");
        }

        if (request.Kind == MirrorKind.App && !string.IsNullOrEmpty(request.Package))
        {
            args.AddRange([$"--new-display={display}", $"--start-app={request.Package}", "--no-vd-system-decorations", "-x"]);
            if (s.ContinueApp) args.Add("--no-vd-destroy-content");
        }
        return args;
    }

    public async Task<MirrorSession?> StartAsync(MirrorRequest request)
    {
        LastError = null;
        var scrcpy = _adb.Locator.ScrcpyPath;
        if (scrcpy is null)
        {
            LastError = "H.I.B couldn't find the scrcpy binary.";
            _adb.AppendLog("\nscrcpy not found.");
            return null;
        }

        string? wiredSerial = null;
        if (_adb.WiredAdbEnabled)
            wiredSerial = await _adb.ResolveWiredSerialAsync().ConfigureAwait(false);
        _adb.Mode = wiredSerial != null ? AdbConnectionMode.Wired : AdbConnectionMode.Wireless;
        if (wiredSerial is null && (string.IsNullOrEmpty(_adb.ConnectedIp) || _adb.Port <= 0))
        {
            LastError = "Connect the phone over ADB first.";
            return null;
        }

        var args = BuildArguments(request, Settings, wiredSerial, $"{_adb.ConnectedIp}:{_adb.Port}");
        HibFeatures.Trace($"[mirror] Launching scrcpy: {scrcpy} {string.Join(' ', args)}");
        var env = _adb.AdbPath is { } adbPath ? AdbLocator.AdbEnvironment(adbPath) : null;
        var psi = ProcessRunner.CreateStartInfo(scrcpy, args, env);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var session = new MirrorSession(request, process, args);

        void OnLine(object? _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (session.Output)
            {
                session.Output.Append(e.Data).Append('\n');
                if (session.Output.Length > 16_000) session.Output.Remove(0, session.Output.Length - 16_000);
            }
        }
        process.OutputDataReceived += OnLine;
        process.ErrorDataReceived += OnLine;
        process.Exited += (_, _) => OnExited(session);

        try
        {
            process.Start();
            try { process.StandardInput.Close(); } catch { }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            LastError = "Failed to start scrcpy: " + ex.Message;
            _adb.AppendLog("\n" + LastError);
            process.Dispose();
            return null;
        }
        lock (_sessions) _sessions.Add(session);
        OnPropertyChanged(nameof(Sessions));
        OnPropertyChanged(nameof(IsMirroring));
        return session;
    }

    private void OnExited(MirrorSession session)
    {
        lock (_sessions) _sessions.Remove(session);
        int code;
        try { code = session.Process.ExitCode; } catch { code = -1; }
        string output;
        lock (session.Output) output = session.Output.ToString();
        _adb.AppendLog("\nscrcpy exited:\n" + output);
        var lower = output.ToLowerInvariant();
        var needsPairing = code != 0 && (lower.Contains("unauthorized") || lower.Contains("auth") || lower.Contains("pair"));
        if (code != 0)
            LastError = needsPairing
                ? "Your Android device is not paired or authorized with this PC. Pair it first using the ADB QR code or adb pair."
                : "Mirroring ended with errors. See the ADB console for details.";
        OnPropertyChanged(nameof(Sessions));
        OnPropertyChanged(nameof(IsMirroring));
        HibFeatures.Dispatch(() => MirrorExited?.Invoke(new MirrorExit(session, code, output, needsPairing)));
        session.Process.Dispose();
    }

    public void Stop(MirrorSession session) => ProcessRunner.KillQuietly(session.Process);

    public void StopAll()
    {
        foreach (var s in Sessions) Stop(s);
    }

    public void Dispose() => StopAll();
}
