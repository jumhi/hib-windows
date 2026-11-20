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
    private int _windowShown;
    private int _slowWarned;

    internal MirrorSession(MirrorRequest request, Process process, IReadOnlyList<string> args)
    {
        Request = request;
        Process = process;
        Arguments = args;
    }
    public Guid Id { get; } = Guid.NewGuid();
    public MirrorRequest Request { get; }
    public IReadOnlyList<string> Arguments { get; }
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
    internal Process Process { get; }
    internal StringBuilder Output { get; } = new();
    public bool IsRunning { get { try { return !Process.HasExited; } catch { return false; } } }

    /// <summary>scrcpy created its (still hidden) window and renderer (<c>INFO: Renderer:</c>).</summary>
    public bool WindowCreated { get; internal set; }
    /// <summary>The window is on screen (first frame drawn, or a visible window handle was found).</summary>
    public bool WindowShown => Volatile.Read(ref _windowShown) == 1;
    internal bool MarkWindowShown() => Interlocked.Exchange(ref _windowShown, 1) == 0;
    internal bool MarkSlowWarned() => Interlocked.Exchange(ref _slowWarned, 1) == 0;
    internal bool SlowWarned => Volatile.Read(ref _slowWarned) == 1;
    /// <summary>We brought the window to the front (Windows).</summary>
    internal bool Raised { get; set; }
    /// <summary>Closed by H.I.B (Stop/StopAll), not a failure.</summary>
    internal bool StopRequested { get; set; }

    /// <summary>The newest non-empty output line.</summary>
    public string? LastLine { get; internal set; }

    public string OutputText { get { lock (Output) return Output.ToString(); } }
}

/// <summary>scrcpy exited. <see cref="IsProblem"/> → show <see cref="Title"/> + <see cref="Reason"/>;
/// when <see cref="NeedsPairing"/>, offer "Pair with ADB QR".</summary>
public sealed record MirrorExit(MirrorSession Session, int ExitCode, string Output, bool NeedsPairing)
{
    public bool IsProblem { get; init; }
    public string Title { get; init; } = "";
    public string Reason { get; init; } = "";
}

/// <summary>scrcpy is still running <see cref="MirrorWatchdog.SlowAfter"/> after start without a window.</summary>
public sealed record MirrorSlow(MirrorSession Session, string? LastLine);

/// <summary>What an exit means for the user.</summary>
public readonly record struct MirrorExitDescription(bool IsProblem, string Title, string Reason, bool NeedsPairing);

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

    /// <summary>When to report "taking long" (<see cref="MirrorWatchdog.SlowAfter"/>; tests shorten it).</summary>
    public TimeSpan SlowAfter { get; set; } = MirrorWatchdog.SlowAfter;

    /// <summary>The monitors' work areas, for clamping a saved window position (Win32 by default; tests inject).</summary>
    public Func<IReadOnlyList<ScreenArea>> Screens { get; set; } = Win32Window.MonitorWorkAreas;

    public IReadOnlyList<MirrorSession> Sessions { get { lock (_sessions) return _sessions.ToList(); } }
    public bool IsMirroring => Sessions.Any(s => s.IsRunning);

    /// <summary>scrcpy exited. When <see cref="MirrorExit.NeedsPairing"/>, offer "Pair with ADB QR".</summary>
    public event Action<MirrorExit>? MirrorExited;
    /// <summary>The scrcpy window is on screen.</summary>
    public event Action<MirrorSession>? MirrorWindowOpened;
    /// <summary>scrcpy runs but no window appeared after <see cref="MirrorWatchdog.SlowAfter"/>.</summary>
    public event Action<MirrorSlow>? MirrorSlowToOpen;

    public Task<MirrorSession?> StartMirrorAsync(string deviceName) => StartAsync(new MirrorRequest(MirrorKind.Mirror, deviceName));
    public Task<MirrorSession?> StartDesktopAsync(string deviceName) => StartAsync(new MirrorRequest(MirrorKind.Desktop, deviceName));
    public Task<MirrorSession?> StartAppAsync(string deviceName, string package, string? label = null)
        => StartAsync(new MirrorRequest(MirrorKind.App, deviceName, package, label));
    public Task<MirrorSession?> StartCameraAsync(string deviceName, string facing = "back")
        => StartAsync(new MirrorRequest(MirrorKind.Camera, deviceName, CameraFacing: facing));

    internal static string CleanDeviceName(string name) => name.Replace("'s", "").Replace("'", "");

    /// <summary>Builds the scrcpy argument list exactly like the Mac. <paramref name="wiredSerial"/>
    /// wins when set, else <c>--tcpip=ip:port</c>. A saved manual position is clamped onto
    /// <paramref name="screens"/> when given.</summary>
    public static List<string> BuildArguments(MirrorRequest request, MirrorSettings s, string? wiredSerial, string tcpAddress,
                                              IReadOnlyList<ScreenArea>? screens = null)
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
            var (x, y) = MirrorWindowPlacement.Clamp(c.Length > 0 ? c[0] : 0, c.Length > 1 ? c[1] : 0, screens);
            args.Add($"--window-x={x}");
            args.Add($"--window-y={y}");
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

    /// <summary>Turns scrcpy's exit code and output into what the user should read (never "see the console").</summary>
    public static MirrorExitDescription DescribeExit(int code, string output, bool windowShown, bool stopRequested)
    {
        if (stopRequested) return new(false, "", "", false);
        var error = ScrcpyOutput.ExtractError(output);
        var last = ScrcpyOutput.LastMeaningfulLine(output);
        if (code != 0 && ScrcpyOutput.IndicatesPairing(output))
            return new(true, "The phone needs to be paired for ADB",
                error ?? "This computer isn't authorized to debug the phone. Pair it with the ADB QR code or adb pair.", true);
        if (code == 0)
        {
            if (windowShown) return new(false, "", "", false); // the user closed the window
            return new(true, "Mirroring closed before its window opened",
                error ?? (last is null ? "scrcpy ended without showing a window." : "scrcpy said: " + last), false);
        }
        if (code == 2)
            return new(true, "The phone disconnected", error ?? "The ADB connection to the phone dropped.", false);
        var title = windowShown ? "Mirroring stopped" : "Mirroring couldn't start";
        var reason = error ?? (last is null ? $"scrcpy exited with code {code} and printed nothing." : $"scrcpy exited with code {code}: {last}");
        return new(true, title, reason, false);
    }

    public async Task<MirrorSession?> StartAsync(MirrorRequest request)
    {
        LastError = null;
        var scrcpy = _adb.Locator.ScrcpyPath;
        if (scrcpy is null)
        {
            LastError = OperatingSystem.IsWindows()
                ? "H.I.B couldn't find scrcpy.exe. Reinstall H.I.B (it ships in its tools folder)."
                : "H.I.B couldn't find the scrcpy binary.";
            _adb.AppendLog("\nscrcpy not found.");
            return null;
        }

        string? wiredSerial = null;
        if (_adb.WiredAdbEnabled)
            wiredSerial = await _adb.ResolveWiredSerialAsync().ConfigureAwait(false);
        _adb.Mode = wiredSerial != null ? AdbConnectionMode.Wired : AdbConnectionMode.Wireless;
        if (wiredSerial is null && (string.IsNullOrEmpty(_adb.ConnectedIp) || _adb.Port <= 0))
        {
            LastError = _adb.WiredAdbEnabled
                ? "Wired ADB is on but no phone is plugged in, and the phone isn't connected over Wi-Fi ADB."
                : "Connect the phone over ADB first.";
            return null;
        }

        IReadOnlyList<ScreenArea>? screens = null;
        if (Settings.ManualPosition)
        {
            try { screens = Screens(); } catch { screens = null; }
        }
        var args = BuildArguments(request, Settings, wiredSerial, $"{_adb.ConnectedIp}:{_adb.Port}", screens);
        HibFeatures.Trace($"[mirror] Launching scrcpy: {scrcpy} {string.Join(' ', args)}");
        var env = _adb.AdbPath is { } adbPath ? AdbLocator.AdbEnvironment(adbPath, scrcpy) : null;
        if (env is not null) HibFeatures.Trace($"[mirror] scrcpy ADB={(env.TryGetValue("ADB", out var a) ? a : "(adb next to scrcpy)")}");
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
            var line = e.Data.Trim();
            if (line.Length == 0) return;
            session.LastLine = line;
            HibLog.Write("scrcpy", line);
            if (ScrcpyOutput.IsWindowCreatedLine(line)) session.WindowCreated = true;
            if (ScrcpyOutput.IsWindowShownLine(line)) OnWindowShown(session, "first frame");
        }
        process.OutputDataReceived += OnLine;
        process.ErrorDataReceived += OnLine;
        process.Exited += (_, _) => OnExited(session);

        // Listed before Start so a very fast exit still finds (and removes) it.
        lock (_sessions) _sessions.Add(session);
        try
        {
            // The click made H.I.B the foreground app: pass that right on, so scrcpy's window (which only
            // appears seconds later) may come to the front instead of opening behind H.I.B.
            Win32Window.AllowAnyForeground();
            process.Start();
            try { process.StandardInput.Close(); } catch { }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            lock (_sessions) _sessions.Remove(session);
            LastError = $"Couldn't start scrcpy ({scrcpy}): {ex.Message}";
            _adb.AppendLog("\n" + LastError);
            process.Dispose();
            return null;
        }
        HibFeatures.Trace($"[mirror] scrcpy started (pid {SafePid(process)})");
        OnPropertyChanged(nameof(Sessions));
        OnPropertyChanged(nameof(IsMirroring));
        _ = Task.Run(() => WatchAsync(session));
        return session;
    }

    private static int SafePid(Process p) { try { return p.Id; } catch { return -1; } }

    private void OnWindowShown(MirrorSession session, string how)
    {
        if (!session.MarkWindowShown()) return;
        HibFeatures.Trace($"[mirror] scrcpy window is open ({how}) after {(DateTime.UtcNow - session.StartedAtUtc).TotalSeconds:0.0} s");
        HibFeatures.Dispatch(() => MirrorWindowOpened?.Invoke(session));
    }

    /// <summary>
    /// Until the window is up: on Windows find scrcpy's visible window and bring it to the front (and onto a
    /// screen); everywhere, warn once when nothing showed after <see cref="MirrorWatchdog.SlowAfter"/>.
    /// </summary>
    private async Task WatchAsync(MirrorSession session)
    {
        var windows = OperatingSystem.IsWindows();
        while (true)
        {
            try { await Task.Delay(250).ConfigureAwait(false); } catch { return; }
            if (!session.IsRunning) return;
            var elapsed = DateTime.UtcNow - session.StartedAtUtc;

            if (windows && !session.Raised)
            {
                var hwnd = IntPtr.Zero;
                try
                {
                    session.Process.Refresh();
                    hwnd = session.Process.MainWindowHandle;
                }
                catch { }
                if (hwnd != IntPtr.Zero)
                {
                    session.Raised = true;
                    var front = Win32Window.BringToFront(hwnd, Settings.AlwaysOnTop);
                    HibFeatures.Trace($"[mirror] scrcpy window 0x{hwnd.ToInt64():X} found; brought to front: {front}");
                    OnWindowShown(session, "window handle");
                }
            }

            if (MirrorWatchdog.ShouldWarn(elapsed, true, session.WindowShown, session.SlowWarned, SlowAfter) && session.MarkSlowWarned())
            {
                var last = session.LastLine;
                HibFeatures.Trace($"[mirror] scrcpy has no window after {elapsed.TotalSeconds:0} s; last output: {last ?? "(none)"}");
                HibFeatures.Dispatch(() => MirrorSlowToOpen?.Invoke(new MirrorSlow(session, last)));
            }

            var done = session.WindowShown && (!windows || session.Raised);
            if (done || elapsed > TimeSpan.FromSeconds(120)) return;
        }
    }

    private void OnExited(MirrorSession session) => _ = Task.Run(async () =>
    {
        // Exited can fire before the last output lines arrive; the error is usually in them.
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await session.Process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch { }
        lock (_sessions) _sessions.Remove(session);
        int code;
        try { code = session.Process.ExitCode; } catch { code = -1; }
        var output = session.OutputText;
        _adb.AppendLog("\nscrcpy exited:\n" + output, toFile: false);
        var d = DescribeExit(code, output, session.WindowShown, session.StopRequested);
        HibFeatures.Trace($"[mirror] scrcpy exited with code {code}; window shown: {session.WindowShown}"
                          + (d.IsProblem ? $"; {d.Title}: {d.Reason}" : ""));
        if (d.IsProblem) LastError = d.Reason;
        // The saved Wireless debugging port is dead: forget it so the next click reconnects with fresh ports.
        if (d.IsProblem && !session.WindowShown && WiredSerialOf(session) is null && ScrcpyOutput.IndicatesAdbConnectionLost(output))
            _adb.MarkLost("scrcpy couldn't reach the phone");
        OnPropertyChanged(nameof(Sessions));
        OnPropertyChanged(nameof(IsMirroring));
        var exit = new MirrorExit(session, code, output, d.NeedsPairing) { IsProblem = d.IsProblem, Title = d.Title, Reason = d.Reason };
        HibFeatures.Dispatch(() => MirrorExited?.Invoke(exit));
        try { session.Process.Dispose(); } catch { }
    });

    private static string? WiredSerialOf(MirrorSession s)
        => s.Arguments.FirstOrDefault(a => a.StartsWith("--serial=", StringComparison.Ordinal));

    public void Stop(MirrorSession session)
    {
        session.StopRequested = true;
        ProcessRunner.KillQuietly(session.Process);
    }

    public void StopAll()
    {
        foreach (var s in Sessions) Stop(s);
    }

    public void Dispose() => StopAll();
}
