using System.Text;
using HIB.Features.Abstractions;

namespace HIB.Features.Adb;

public sealed record WiredAdbDevice(string Serial, string Model);

/// <summary>One line of <c>adb mdns services</c>: name, type (e.g. _adb-tls-connect._tcp), ip, port.</summary>
public sealed record AdbMdnsService(string Name, string Type, string Ip, int Port);

public enum AdbConnectionMode { Wireless, Wired }

public sealed record AdbConnectResult(bool Success, string? Address, string Output, string? Diagnosis, string? FailureText);

/// <summary>
/// ADB connection state and one-shot commands (port of the Mac's ADBConnector + PhoneADBTarget).
/// Bind to <see cref="IsConnected"/>, <see cref="IsConnecting"/>, <see cref="ConsoleLog"/>.
/// </summary>
public sealed class AdbService : ObservableObject
{
    public const string PhonePackage = "com.sameerasw.airsync";

    private readonly AdbLocator _locator;
    private readonly ISettingsStore _settings;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private int _connecting;

    private bool _isConnected;
    private bool _isConnectingProp;
    private string _connectedIp = "";
    private int _port;
    private string _consoleLog = "";
    private string? _lastResult;
    private AdbConnectionMode _mode = AdbConnectionMode.Wireless;

    public AdbService(AdbLocator? locator = null, ISettingsStore? settings = null)
    {
        _locator = locator ?? AdbLocator.Shared;
        _settings = settings ?? new InMemorySettingsStore();
    }

    public AdbLocator Locator => _locator;
    public string? AdbPath => _locator.AdbPath;

    public bool IsConnected { get => _isConnected; private set => SetProperty(ref _isConnected, value); }
    public bool IsConnecting { get => _isConnectingProp; private set => SetProperty(ref _isConnectingProp, value); }
    public string ConnectedIp { get => _connectedIp; private set => SetProperty(ref _connectedIp, value); }
    public int Port { get => _port; private set => SetProperty(ref _port, value); }
    public AdbConnectionMode Mode { get => _mode; internal set => SetProperty(ref _mode, value); }
    /// <summary>Running console text (the Mac's adbConnectionResult).</summary>
    public string ConsoleLog { get => _consoleLog; private set => SetProperty(ref _consoleLog, value); }
    public string? LastResult { get => _lastResult; private set => SetProperty(ref _lastResult, value); }

    /// <summary>Set when the user explicitly disconnected, so a guardian doesn't reconnect behind their back.</summary>
    public bool UserRequestedDisconnect { get; private set; }

    // Preferences (same keys as the Mac's UserDefaults).
    public bool WiredAdbEnabled
    {
        get => _settings.Get<bool?>("wiredAdbEnabled") ?? false;
        set { _settings.Set("wiredAdbEnabled", value); OnPropertyChanged(); OnPropertyChanged(nameof(CurrentSerial)); }
    }
    public string? SelectedWiredSerial
    {
        get => _settings.Get<string>("selectedWiredSerial");
        set { _settings.Set("selectedWiredSerial", value); OnPropertyChanged(); OnPropertyChanged(nameof(CurrentSerial)); }
    }
    public bool FallbackToMdns
    {
        get => _settings.Get<bool?>("fallbackToMdns") ?? true;
        set { _settings.Set("fallbackToMdns", value); OnPropertyChanged(); }
    }
    public bool AlwaysKillAdbBeforeConnect
    {
        get => _settings.Get<bool?>("alwaysKillAdbBeforeConnect") ?? false;
        set { _settings.Set("alwaysKillAdbBeforeConnect", value); OnPropertyChanged(); }
    }

    /// <summary>Raised (non-silent connects only) when every port failed. The UI shows the text and
    /// offers "Pair with ADB QR".</summary>
    public event Action<AdbConnectResult>? ConnectionFailed;

    /// <summary>
    /// The adb serial H.I.B should talk to: the wired cable when it's enabled and chosen, otherwise the
    /// wireless ip:port (PhoneADBTarget.currentSerial).
    /// </summary>
    public string? CurrentSerial
    {
        get
        {
            var wired = SelectedWiredSerial;
            if (WiredAdbEnabled && !string.IsNullOrEmpty(wired)) return wired;
            if (!IsConnected || string.IsNullOrEmpty(ConnectedIp) || Port <= 0) return null;
            return $"{ConnectedIp}:{Port}";
        }
    }

    internal void AppendLog(string text)
    {
        var next = ConsoleLog + text;
        if (next.Length > 20_000) next = next[^20_000..];
        ConsoleLog = next;
    }

    private void Log(string message)
    {
        AppendLog("\n[Binary Detection] " + message);
        HibFeatures.Trace("[adb-connector] " + message);
    }

    /// <summary>Marks a connection made elsewhere (e.g. by the pairing flow).</summary>
    internal void MarkConnected(string ip, int port, string result)
    {
        ConnectedIp = ip;
        Port = port;
        IsConnected = true;
        IsConnecting = false;
        LastResult = result;
        Mode = AdbConnectionMode.Wireless;
        OnPropertyChanged(nameof(CurrentSerial));
    }

    // MARK: - Raw commands

    /// <summary>Runs <c>adb &lt;args&gt;</c>.</summary>
    public Task<ProcessResult> RunAsync(IEnumerable<string> args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var adb = AdbPath;
        if (adb is null) return Task.FromResult(new ProcessResult(-1, [], "adb not found", false));
        return ProcessRunner.RunAsync(adb, args, timeout ?? TimeSpan.FromSeconds(30), null, ct);
    }

    /// <summary>Runs <c>adb -s &lt;serial&gt; &lt;args&gt;</c> against <see cref="CurrentSerial"/>.</summary>
    public Task<ProcessResult> RunOnDeviceAsync(IEnumerable<string> args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var serial = CurrentSerial;
        if (serial is null) return Task.FromResult(new ProcessResult(-1, [], "Phone isn't connected over ADB.", false));
        return RunAsync(new[] { "-s", serial }.Concat(args), timeout, ct);
    }

    /// <summary><c>adb -s serial shell ...</c>; returns stdout text.</summary>
    public async Task<ProcessResult> ShellAsync(params string[] command)
        => await RunOnDeviceAsync(new[] { "shell" }.Concat(command)).ConfigureAwait(false);

    // MARK: - Wired devices

    public async Task<IReadOnlyList<WiredAdbDevice>> GetWiredDevicesAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(["devices", "-l"], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        return ParseWiredDevices(result.StdOutText);
    }

    internal static List<WiredAdbDevice> ParseWiredDevices(string output)
    {
        var devices = new List<WiredAdbDevice>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[1] != "device") continue;
            var serial = parts[0];
            // Wireless (ip:port) and mDNS (…._adb-tls-connect._tcp) serials aren't cables.
            if (serial.Contains(':') || serial.Contains("._adb-tls-")) continue;
            var model = "Unknown Device";
            foreach (var part in parts)
            {
                if (part.StartsWith("model:", StringComparison.Ordinal))
                {
                    model = part["model:".Length..].Replace('_', ' ');
                    break;
                }
            }
            devices.Add(new WiredAdbDevice(serial, model));
        }
        return devices;
    }

    /// <summary>Wired-mode serial choice: the mapped/selected serial if attached, else the only/first device.</summary>
    public async Task<string?> ResolveWiredSerialAsync(string? preferred = null, CancellationToken ct = default)
    {
        var devices = await GetWiredDevicesAsync(ct).ConfigureAwait(false);
        var mapped = preferred ?? SelectedWiredSerial;
        if (mapped != null && devices.Any(d => d.Serial == mapped)) return mapped;
        return devices.FirstOrDefault()?.Serial;
    }

    // MARK: - mDNS

    public async Task<IReadOnlyList<AdbMdnsService>> MdnsServicesAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(["mdns", "services"], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        return ParseMdnsServices(result.Combined);
    }

    /// <summary>Parses adb's "name\ttype\tip:port" lines (whitespace tolerant).</summary>
    internal static List<AdbMdnsService> ParseMdnsServices(string output)
    {
        var list = new List<AdbMdnsService>();
        foreach (var raw in output.Split('\n'))
        {
            var parts = raw.Trim().Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            var addr = parts[^1];
            var colon = addr.LastIndexOf(':');
            if (colon <= 0) continue;
            if (!int.TryParse(addr[(colon + 1)..], out var port) || port <= 0 || port > 65535) continue;
            var type = parts[^2];
            if (!type.StartsWith('_')) continue;
            var name = string.Join(' ', parts[..^2]);
            list.Add(new AdbMdnsService(name, type.TrimEnd('.'), addr[..colon], port));
        }
        return list;
    }

    /// <summary>The Mac's discoverADBPorts: lines of `adb mdns services` that contain the phone IP,
    /// taking the trailing :port.</summary>
    internal static List<int> ParsePortsForIp(string output, string ip)
    {
        var ports = new List<int>();
        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains(ip, StringComparison.Ordinal)) continue;
            var parts = line.Split(':');
            if (parts.Length < 2) continue;
            var digits = new string(parts[^1].Trim().Where(char.IsAsciiDigit).ToArray());
            if (int.TryParse(digits, out var port) && port is > 0 and <= 65535 && !ports.Contains(port))
                ports.Add(port);
        }
        return ports;
    }

    // MARK: - Wireless connect

    private static readonly string[] FailureSuggestions =
    [
        "Pair your device using either the \"Pair with ADB QR\" button or via the command line (adb pair)",
        "Ensure your Android device has Wireless Debugging enabled",
        "Reconnect to the exact same Wi-Fi network as your PC",
    ];

    private static readonly (string Match, string Reason)[] FailureReasons =
    [
        ("failed to authenticate", "Your PC isn't authorized to debug this device yet — it needs to be paired again."),
        ("unauthorized", "Your PC isn't authorized to debug this device yet — it needs to be paired again."),
        ("connection refused", "The device refused the connection. This usually means it hasn't been paired with this PC (via \"adb pair\") since Wireless Debugging was last turned on."),
        ("no route to host", "Your PC can't reach the device on the network — check they're both on the same Wi-Fi."),
        ("network is unreachable", "Your PC can't reach the device on the network — check they're both on the same Wi-Fi."),
        ("timed out", "The connection attempt timed out — check that both devices are on the same Wi-Fi network and Wireless Debugging is still on."),
    ];

    internal static string? Diagnosis(IEnumerable<string> outputs)
    {
        foreach (var output in outputs)
            foreach (var (match, reason) in FailureReasons)
                if (output.Contains(match, StringComparison.Ordinal)) return reason;
        return null;
    }

    internal static string BuildFailureText(IReadOnlyList<string> outputs)
    {
        var sections = new List<string>();
        if (Diagnosis(outputs) is { } d) sections.Add("What happened:\n" + d);
        sections.Add("Suggestions:\n" + string.Join("\n", FailureSuggestions.Select(s => "• " + s)));
        return string.Join("\n\n", sections);
    }

    /// <summary>
    /// Wireless connect (ADBConnector.connectToADB): tries each port the phone reported in its
    /// <c>device.adbPorts</c>; with none, falls back to <c>adb mdns services</c> when enabled.
    /// Success means the output contains "connected to". Concurrent calls are ignored.
    /// </summary>
    public async Task<AdbConnectResult> ConnectAsync(string ip, IReadOnlyList<string>? reportedPorts,
        bool killServer = false, bool silent = false, CancellationToken ct = default)
    {
        if (!silent) UserRequestedDisconnect = false;
        if (Interlocked.CompareExchange(ref _connecting, 1, 0) != 0)
        {
            Log("ADB connection already in progress, ignoring duplicate request");
            return new AdbConnectResult(false, null, "busy", null, null);
        }
        IsConnecting = true;
        try
        {
            var adb = AdbPath;
            if (adb is null)
            {
                LastResult = OperatingSystem.IsWindows()
                    ? "ADB not found. Put adb.exe in the tools folder or install Android platform-tools."
                    : "ADB not found. Please install via Homebrew: brew install android-platform-tools";
                ConsoleLog = LastResult;
                IsConnected = false;
                return new AdbConnectResult(false, null, LastResult, null, LastResult);
            }

            if (killServer || AlwaysKillAdbBeforeConnect)
            {
                Log("Manual connection requested. Killing ADB server first...");
                await RunAsync(["kill-server"], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                Log("ADB server killed.");
            }

            List<int> ports;
            if (reportedPorts is null || reportedPorts.Count == 0)
            {
                if (!FallbackToMdns)
                {
                    Log("Device reported no ADB ports and mDNS fallback is disabled.");
                    IsConnected = false;
                    return new AdbConnectResult(false, null, "", null, null);
                }
                Log("Device reported no ADB ports, attempting mDNS discovery...");
                var mdns = await RunAsync(["mdns", "services"], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                ports = ParsePortsForIp(mdns.Combined, ip);
                if (ports.Count == 0)
                {
                    Log($"mDNS discovery found no ports for {ip}.");
                    IsConnected = false;
                    LastResult = "No ADB ports reported by device and mDNS discovery failed.";
                    AppendLog("\n" + LastResult);
                    return new AdbConnectResult(false, null, LastResult, null, LastResult);
                }
                Log("mDNS discovery found ports: " + string.Join(", ", ports));
            }
            else
            {
                Log("Using ADB ports from device: " + string.Join(", ", reportedPorts));
                ports = reportedPorts.Select(p => int.TryParse(p, out var v) && v is > 0 and <= 65535 ? v : -1)
                                     .Where(v => v > 0).ToList();
                if (ports.Count == 0)
                {
                    LastResult = "Device reported ADB ports but none could be parsed as valid port numbers.";
                    IsConnected = false;
                    return new AdbConnectResult(false, null, LastResult, null, LastResult);
                }
            }

            Log($"Proceeding with ADB connection attempts to {ip}...");
            await Task.Delay(500, ct).ConfigureAwait(false);
            var failures = new List<string>();
            for (var i = 0; i < ports.Count; i++)
            {
                var address = $"{ip}:{ports[i]}";
                Log($"Attempting connection to port {ports[i]} (attempt {i + 1}/{ports.Count}): {address}");
                var result = await RunAsync(["connect", address], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                var output = result.Combined.Trim().ToLowerInvariant();
                AppendLog(result.Combined);
                if (output.Contains("connected to"))
                {
                    ConnectedIp = ip;
                    Port = ports[i];
                    IsConnected = true;
                    Mode = AdbConnectionMode.Wireless;
                    LastResult = output;
                    OnPropertyChanged(nameof(CurrentSerial));
                    Log($"ADB connection successful to {address}");
                    return new AdbConnectResult(true, address, output, null, null);
                }
                Log($"Port {ports[i]} failed, trying next...");
                failures.Add($"{address}: {output}");
                if (i + 1 < ports.Count) await Task.Delay(500, ct).ConfigureAwait(false);
            }

            IsConnected = false;
            Log("ADB connection failed on all ports.");
            var summary = failures.Count == 0 ? "Failed to connect to device on any available port." : string.Join("\n", failures);
            AppendLog("\n" + summary);
            var fail = new AdbConnectResult(false, null, summary, Diagnosis(failures), BuildFailureText(failures));
            if (!silent) ConnectionFailed?.Invoke(fail);
            return fail;
        }
        finally
        {
            IsConnecting = false;
            Interlocked.Exchange(ref _connecting, 0);
        }
    }

    /// <summary><c>adb disconnect ip:port</c> (also called when the phone link drops).</summary>
    public async Task DisconnectAsync(bool userInitiated = false)
    {
        if (userInitiated) UserRequestedDisconnect = true;
        var ip = ConnectedIp;
        var port = Port;
        if (AdbPath != null && !string.IsNullOrEmpty(ip))
            await RunAsync(["disconnect", $"{ip}:{port}"], TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        IsConnected = false;
        IsConnecting = false;
        OnPropertyChanged(nameof(CurrentSerial));
    }

    // MARK: - One-shot commands

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary><c>adb exec-out screencap -p</c>; null unless the output is a real PNG.</summary>
    public async Task<byte[]?> ScreenshotAsync(CancellationToken ct = default)
    {
        var result = await RunOnDeviceAsync(["exec-out", "screencap", "-p"], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        return IsPng(result.StdOut) ? result.StdOut : null;
    }

    internal static bool IsPng(byte[] data)
        => data.Length > PngSignature.Length && data.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature);

    /// <summary><c>adb pull remote local</c>; success unless the output mentions error/failed.</summary>
    public async Task<bool> PullAsync(string remotePath, string localPath, CancellationToken ct = default)
    {
        var r = await RunOnDeviceAsync(["pull", remotePath, localPath], TimeSpan.FromMinutes(30), ct).ConfigureAwait(false);
        var output = r.Combined;
        return r.ExitCode == 0 && !output.Contains("error") && !output.Contains("failed");
    }

    /// <summary><c>adb push local remote</c>.</summary>
    public async Task<bool> PushAsync(string localPath, string remotePath, CancellationToken ct = default)
    {
        var r = await RunOnDeviceAsync(["push", localPath, remotePath], TimeSpan.FromMinutes(30), ct).ConfigureAwait(false);
        var output = r.Combined;
        return r.ExitCode == 0 && !output.Contains("error") && !output.Contains("failed");
    }

    /// <summary>Find My lock fallback: <c>input keyevent KEYCODE_SLEEP</c>.</summary>
    public async Task<bool> LockPhoneAsync()
    {
        var r = await ShellAsync("input", "keyevent", "KEYCODE_SLEEP").ConfigureAwait(false);
        return r.ExitCode == 0;
    }

    /// <summary>Ring My Phone over ADB (§7.3). The receiver is guarded by android.permission.DUMP.</summary>
    public async Task<bool> SendRingBroadcastAsync(bool start)
    {
        var action = start ? $"{PhonePackage}.RING_PHONE" : $"{PhonePackage}.STOP_RING_PHONE";
        var r = await ShellAsync("am", "broadcast", "-a", action, "-n", $"{PhonePackage}/.service.RingCommandReceiver").ConfigureAwait(false);
        return r.ExitCode == 0;
    }
}
