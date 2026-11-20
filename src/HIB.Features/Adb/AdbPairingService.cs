using System.Net.Sockets;
using System.Security.Cryptography;

namespace HIB.Features.Adb;

/// <summary>Where a pairing is.</summary>
public enum AdbPairingPhase { Idle, WaitingForPhone, Pairing, Connecting, Connected, Failed }

/// <summary>
/// Pairs this computer with the phone's Wireless debugging (docs/SPACE.md §8.3) — no terminal, the bundled adb.
/// <list type="bullet">
/// <item><b>QR:</b> <see cref="StartPairingAsync"/> shows <see cref="QrText"/>; the phone scans it in Wireless
/// debugging → Pair device with QR code and opens a pairing service under <see cref="ServiceName"/>. The phone
/// reports that service in <c>adbSetupState</c> (<see cref="UpdateSetupState"/>); when its name matches, we run
/// <c>adb pair phoneIp:pairingPort password</c> at once.</item>
/// <item><b>Pairing code:</b> <see cref="PairWithCodeAsync"/> — the port comes from <c>pairingPort</c>, the user
/// only types the 6 digits.</item>
/// <item>Then <c>adb connect phoneIp:connectPort</c>.</item>
/// </list>
/// <c>adb mdns services</c> is only the fallback for an older phone app that never sends <c>adbSetupState</c>
/// (mDNS doesn't cross subnets and is often blocked).
/// </summary>
public sealed class AdbPairingService : ObservableObject
{
    private readonly AdbService _adb;
    private readonly object _stateGate = new();
    private TaskCompletionSource _stateChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _cts;

    private string _serviceName = "";
    private string _password = "";
    private string _qrText = "";
    private string _status = "";
    private bool _isPairing;
    private AdbPairingPhase _phase;
    private AdbSetupState? _setupState;
    private string? _phoneIp;

    public AdbPairingService(AdbService adb) { _adb = adb; }

    public string ServiceName { get => _serviceName; private set => SetProperty(ref _serviceName, value); }
    public string Password { get => _password; private set => SetProperty(ref _password, value); }
    /// <summary><c>WIFI:T:ADB;S:&lt;serviceName&gt;;P:&lt;password&gt;;;</c> — Android's standard pairing QR.</summary>
    public string QrText { get => _qrText; private set => SetProperty(ref _qrText, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsPairing { get => _isPairing; private set => SetProperty(ref _isPairing, value); }
    public AdbPairingPhase Phase
    {
        get => _phase;
        private set { if (SetProperty(ref _phase, value)) OnPropertyChanged(nameof(IsBusy)); }
    }

    /// <summary>An <c>adb pair</c> / <c>adb connect</c> is running (not just waiting for the phone).</summary>
    public bool IsBusy => Phase is AdbPairingPhase.Pairing or AdbPairingPhase.Connecting;

    /// <summary>The phone's last <c>adbSetupState</c> on this link; null when it never sent one (older app) or the link dropped.</summary>
    public AdbSetupState? SetupState { get => _setupState; private set => SetProperty(ref _setupState, value); }

    /// <summary>The phone's own address (what its adbd listens on).</summary>
    public string? PhoneIp { get => _phoneIp; set => SetProperty(ref _phoneIp, value); }

    /// <summary>Raised on every <see cref="UpdateSetupState"/> (on the caller's thread).</summary>
    public event Action<AdbSetupState?>? SetupStateChanged;

    /// <summary>How long the QR stays valid while waiting for the phone.</summary>
    public TimeSpan ScanTimeout { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>After pairing: how long to wait for the phone to report its Wireless debugging port.</summary>
    public TimeSpan ConnectPortWait { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Code flow: how long to wait for the phone's pairing-code screen to appear.</summary>
    public TimeSpan PairingPortWait { get; set; } = TimeSpan.FromSeconds(10);

    internal static string BuildQrText(string serviceName, string password) => $"WIFI:T:ADB;S:{serviceName};P:{password};;";

    private static string RandomDigits(int count)
        => string.Concat(Enumerable.Range(0, count).Select(_ => RandomNumberGenerator.GetInt32(0, 10)));

    /// <summary>The phone reported its ADB setup (§8.1), or the link dropped (null).</summary>
    public void UpdateSetupState(AdbSetupState? state)
    {
        TaskCompletionSource fire;
        lock (_stateGate)
        {
            SetupState = state;
            fire = _stateChanged;
            _stateChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        fire.TrySetResult();
        SetupStateChanged?.Invoke(state);
    }

    /// <summary>Waits for the next <see cref="UpdateSetupState"/>, at most <paramref name="max"/>.</summary>
    private async Task WaitForStateAsync(TimeSpan max, CancellationToken ct)
    {
        Task next;
        lock (_stateGate) next = _stateChanged.Task;
        try { await next.WaitAsync(max, ct).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    private CancellationToken Begin()
    {
        StopPairing();
        // StopPairing raises property changes; a listener may have started another run meanwhile: cancel it too.
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _cts, cts)?.Cancel();
        IsPairing = true;
        return cts.Token;
    }

    private bool Fail(string text)
    {
        Status = text;
        Phase = AdbPairingPhase.Failed;
        HibFeatures.Trace("[adb-pairing] " + text);
        return false;
    }

    /// <summary>
    /// QR pairing: creates a name and password, shows <see cref="QrText"/> right away, waits for the phone to
    /// report a pairing service under that name, pairs and connects. Returns when connected, failed or stopped.
    /// </summary>
    public async Task<bool> StartPairingAsync()
    {
        var ct = Begin();
        var cts = _cts;
        ServiceName = "adb-wireless-" + RandomDigits(6);
        Password = RandomDigits(8);
        QrText = BuildQrText(ServiceName, Password);
        Phase = AdbPairingPhase.WaitingForPhone;
        Status = "On your phone: Wireless debugging → Pair device with QR code, then scan this code.";
        try
        {
            if (!_adb.HasAdb) return Fail(AdbService.MissingAdbText);
            string? ip = null;
            int? port = null;
            var mdnsStarted = false;
            var deadline = DateTime.UtcNow + ScanTimeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var state = SetupState;
                if (state is not null)
                {
                    // The phone tells us its own pairing service: no mDNS, works across subnets.
                    if (state.PairingPort is int p && AdbSetupFlow.SameServiceName(state.PairingName, ServiceName) && PhoneIp is { Length: > 0 } pip)
                    {
                        (ip, port) = (pip, p);
                        break;
                    }
                    await WaitForStateAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
                else
                {
                    // Older phone app: it doesn't report the service, so look for it with mDNS (same network only).
                    if (!mdnsStarted)
                    {
                        await _adb.RunAsync(["start-server"], TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                        mdnsStarted = true;
                    }
                    var services = await _adb.MdnsServicesAsync(ct).ConfigureAwait(false);
                    var svc = services.FirstOrDefault(s => s.Type.StartsWith("_adb-tls-pairing", StringComparison.Ordinal)
                                                           && AdbSetupFlow.SameServiceName(s.Name, ServiceName));
                    if (svc is not null)
                    {
                        (ip, port) = (svc.Ip, svc.Port);
                        break;
                    }
                    await WaitForStateAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
            }
            if (ip is null || port is null)
                return Fail("The phone didn't scan the code in time. Open Wireless debugging → Pair device with QR code and try again.");
            return await PairThenConnectAsync(ip, port.Value, Password, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) IsPairing = false;
        }
    }

    /// <summary>
    /// Pairing-code flow: the phone shows Wireless debugging → Pair device with pairing code; the port comes from
    /// <c>adbSetupState.pairingPort</c>, so the user only types the 6 digits.
    /// </summary>
    public async Task<bool> PairWithCodeAsync(string code)
    {
        var digits = new string((code ?? "").Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != 6)
        {
            Status = "Type the 6-digit code shown on your phone.";
            return false;
        }
        var ct = Begin();
        var cts = _cts;
        Phase = AdbPairingPhase.WaitingForPhone;
        try
        {
            if (!_adb.HasAdb) return Fail(AdbService.MissingAdbText);
            var ip = PhoneIp;
            int? port = null;
            var deadline = DateTime.UtcNow + PairingPortWait;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var state = SetupState;
                if (state?.PairingPort is int p) { port = p; break; }
                if (state is null)
                {
                    // Older phone app: find the phone's pairing service by mDNS.
                    var services = await _adb.MdnsServicesAsync(ct).ConfigureAwait(false);
                    var svc = services.FirstOrDefault(s => s.Type.StartsWith("_adb-tls-pairing", StringComparison.Ordinal) && (ip is null || s.Ip == ip))
                              ?? services.FirstOrDefault(s => s.Type.StartsWith("_adb-tls-pairing", StringComparison.Ordinal));
                    if (svc is not null) { (ip, port) = (svc.Ip, svc.Port); break; }
                }
                if (DateTime.UtcNow >= deadline) break;
                Status = "Waiting for the pairing code screen on your phone…";
                await WaitForStateAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
            if (port is null || string.IsNullOrEmpty(ip))
                return Fail("Open Wireless debugging → Pair device with pairing code on your phone, then type the code.");
            return await PairThenConnectAsync(ip, port.Value, digits, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) IsPairing = false;
        }
    }

    public void StopPairing()
    {
        Interlocked.Exchange(ref _cts, null)?.Cancel();
        IsPairing = false;
        Phase = AdbPairingPhase.Idle;
        Status = "";
    }

    /// <summary>adb pair, then adb connect on the phone's Wireless debugging port.</summary>
    private async Task<bool> PairThenConnectAsync(string ip, int pairingPort, string secret, CancellationToken ct)
    {
        Phase = AdbPairingPhase.Pairing;
        Status = "Pairing…";
        if (!await PairAsync(ip, pairingPort, secret, ct).ConfigureAwait(false)) return false;

        Phase = AdbPairingPhase.Connecting;
        Status = "Paired. Connecting…";
        var connectPort = await WaitForConnectPortAsync(ip, ct).ConfigureAwait(false);
        if (connectPort is null)
            return Fail("Paired. Waiting for Wireless debugging's port — keep Wireless debugging on, then press Mirror.");
        if (!await ConnectAsync(ip, connectPort.Value, ct).ConfigureAwait(false)) return false;
        Phase = AdbPairingPhase.Connected;
        return true;
    }

    /// <summary>The phone's Wireless debugging port: from its report (§8.1), or mDNS for an older phone app.</summary>
    private async Task<int?> WaitForConnectPortAsync(string ip, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ConnectPortWait;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var state = SetupState;
            if (state?.ConnectPort is int p) return p;
            if (state is null)
            {
                var services = await _adb.MdnsServicesAsync(ct).ConfigureAwait(false);
                var svc = services.FirstOrDefault(s => s.Type.StartsWith("_adb-tls-connect", StringComparison.Ordinal) && s.Ip == ip);
                if (svc is not null) return svc.Port;
            }
            if (DateTime.UtcNow >= deadline) return null;
            await WaitForStateAsync(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> PairAsync(string ip, int port, string secret, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (_adb.AlwaysKillAdbBeforeConnect || attempt > 1)
            {
                await _adb.RunAsync(["kill-server"], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            HibFeatures.Trace($"[adb-pairing] adb pair {ip}:{port} (attempt {attempt})");
            var r = await _adb.RunAsync(["pair", $"{ip}:{port}", secret], TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            var output = r.Combined.Trim();
            _adb.AppendLog("\n" + output.Replace(secret, "••••••", StringComparison.Ordinal));
            var protocolFault = output.Contains("protocol fault") || output.Contains("Undefined error");
            if (r.ExitCode == 0 && !protocolFault && !output.Contains("Failed", StringComparison.OrdinalIgnoreCase)) return true;

            if (attempt < 3 && (protocolFault || output.Contains("cannot connect to daemon")))
            {
                Status = $"Pairing again ({attempt + 1}/3)…";
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }
            if (output.Contains("Wrong password", StringComparison.OrdinalIgnoreCase))
                return Fail("That code didn't work. Check it on the phone and try again.");
            if (output.Contains("No route to host") || !await IsPortReachableAsync(ip, port).ConfigureAwait(false))
                return Fail($"This PC can't reach the phone at {ip}. Check that Windows Firewall allows H.I.B and that the networks can reach each other.");
            if (output.Contains("Connection refused"))
                return Fail("The phone closed its pairing screen. Open it again and retry.");
            return Fail("Pairing failed: " + output);
        }
        return false;
    }

    private async Task<bool> ConnectAsync(string ip, int port, CancellationToken ct)
    {
        var address = $"{ip}:{port}";
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (attempt > 1)
            {
                await _adb.RunAsync(["kill-server"], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            var r = await _adb.RunAsync(["connect", address], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            var output = r.Combined.Trim();
            _adb.AppendLog("\n" + output);
            if (r.ExitCode == 0 && output.Contains("connected", StringComparison.OrdinalIgnoreCase)
                && !output.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                Status = "Connected.";
                _adb.MarkConnected(ip, port, "Connected to " + address);
                return true;
            }
            if (attempt < 3 && (output.Contains("protocol fault") || output.Contains("failed to connect") || output.Contains("cannot connect to daemon")))
            {
                Status = $"Connecting again ({attempt + 1}/3)…";
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }
            return Fail("Paired, but connecting failed: " + output);
        }
        return false;
    }

    private static async Task<bool> IsPortReachableAsync(string ip, int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await client.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }
}
