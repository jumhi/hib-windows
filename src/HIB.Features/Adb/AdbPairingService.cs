using System.Net.Sockets;
using System.Security.Cryptography;

namespace HIB.Features.Adb;

/// <summary>
/// "Pair with ADB QR" (port of ADBPairingManager). Show <see cref="QrText"/> as a QR code; the phone
/// scans it in Developer options → Wireless debugging → Pair device with QR code. The phone then
/// advertises <c>_adb-tls-pairing._tcp</c> under <see cref="ServiceName"/>; we find it through
/// <c>adb mdns services</c> (works on Windows without Bonjour), run <c>adb pair</c>, then find the
/// phone's <c>_adb-tls-connect._tcp</c> port and run <c>adb connect</c>.
/// </summary>
public sealed class AdbPairingService : ObservableObject
{
    private readonly AdbService _adb;
    private CancellationTokenSource? _cts;

    private string _serviceName = "";
    private string _password = "";
    private string _qrText = "";
    private string _status = "Idle";
    private bool _isPairing;

    public AdbPairingService(AdbService adb) { _adb = adb; }

    public string ServiceName { get => _serviceName; private set => SetProperty(ref _serviceName, value); }
    public string Password { get => _password; private set => SetProperty(ref _password, value); }
    /// <summary><c>WIFI:T:ADB;S:&lt;serviceName&gt;;P:&lt;password&gt;;;</c> — Android's standard pairing QR.</summary>
    public string QrText { get => _qrText; private set => SetProperty(ref _qrText, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsPairing { get => _isPairing; private set => SetProperty(ref _isPairing, value); }

    /// <summary>How long to wait for the phone to scan the QR code.</summary>
    public TimeSpan ScanTimeout { get; set; } = TimeSpan.FromMinutes(3);

    internal static string BuildQrText(string serviceName, string password) => $"WIFI:T:ADB;S:{serviceName};P:{password};;";

    private static string RandomDigits(int count)
        => string.Concat(Enumerable.Range(0, count).Select(_ => RandomNumberGenerator.GetInt32(0, 10)));

    /// <summary>Generates credentials and starts watching for the phone. Returns when paired+connected,
    /// failed, or stopped. Bind <see cref="QrText"/> to show the code immediately.</summary>
    public async Task<bool> StartPairingAsync()
    {
        StopPairing();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;

        IsPairing = true;
        Status = "Generating pairing credentials...";
        ServiceName = "adb-wireless-" + RandomDigits(6);
        Password = RandomDigits(8);
        QrText = BuildQrText(ServiceName, Password);
        Status = "Waiting for device to scan QR code...";

        try
        {
            if (_adb.AdbPath is null)
            {
                Status = "ADB not found. Please install platform-tools.";
                return false;
            }
            // Make sure adb's mDNS discovery is running.
            await _adb.RunAsync(["start-server"], TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);

            // 1. Find the pairing service that carries our name.
            AdbMdnsService? pairing = null;
            var deadline = DateTime.UtcNow + ScanTimeout;
            while (pairing is null && DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var services = await _adb.MdnsServicesAsync(ct).ConfigureAwait(false);
                pairing = services.FirstOrDefault(s => s.Type.StartsWith("_adb-tls-pairing", StringComparison.Ordinal)
                    && (s.Name.Contains(ServiceName, StringComparison.Ordinal) || ServiceName.Contains(s.Name, StringComparison.Ordinal)));
                if (pairing is null) await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            if (pairing is null)
            {
                Status = "The phone didn't show up. Keep the QR screen open and make sure both devices are on the same Wi-Fi.";
                return false;
            }

            Status = $"Pairing with device at {pairing.Ip}:{pairing.Port}...";
            if (!await PairAsync(pairing.Ip, pairing.Port, ct).ConfigureAwait(false)) return false;

            // 2. Connect on the wireless debugging port.
            Status = "Pairing successful! Connecting to device...";
            int? connectPort = null;
            for (var attempt = 0; attempt < 20 && connectPort is null; attempt++)
            {
                var services = await _adb.MdnsServicesAsync(ct).ConfigureAwait(false);
                connectPort = services.FirstOrDefault(s => s.Type.StartsWith("_adb-tls-connect", StringComparison.Ordinal) && s.Ip == pairing.Ip)?.Port;
                if (connectPort is null) await Task.Delay(500, ct).ConfigureAwait(false);
            }
            if (connectPort is null)
            {
                Status = "Paired, but wireless debugging port not found. Enable Wireless Debugging on device.";
                return false;
            }
            return await ConnectAsync(pairing.Ip, connectPort.Value, ct).ConfigureAwait(false);
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
        _cts?.Cancel();
        _cts = null;
        IsPairing = false;
        Status = "Idle";
    }

    private async Task<bool> PairAsync(string ip, int port, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var reachable = await IsPortReachableAsync(ip, port).ConfigureAwait(false);
            HibFeatures.Trace($"[adb-pairing] socket check {ip}:{port} reachable={reachable}");
            if (_adb.AlwaysKillAdbBeforeConnect || attempt > 1)
            {
                await _adb.RunAsync(["kill-server"], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            var r = await _adb.RunAsync(["pair", $"{ip}:{port}", Password], TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            var output = r.Combined.Trim();
            var protocolFault = output.Contains("protocol fault") || output.Contains("Undefined error");
            if (r.ExitCode == 0 && !protocolFault) return true;

            if (attempt < 3 && (protocolFault || output.Contains("No route to host") || output.Contains("cannot connect to daemon")))
            {
                Status = $"ADB server busy. Restarting daemon and retrying pairing ({attempt + 1}/3)...";
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }
            if (!reachable || output.Contains("No route to host"))
                Status = "The PC couldn't reach the phone (No route to host). Check Windows Firewall and that both are on the same Wi-Fi.";
            else if (output.Contains("protocol fault") || output.Contains("Connection refused"))
                Status = "Could not reach device. Keep the QR code screen active on your phone & ensure both devices are on same Wi-Fi.";
            else
                Status = "Pairing failed: " + output;
            return false;
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
            if (r.ExitCode == 0 && output.Contains("connected", StringComparison.OrdinalIgnoreCase)
                && !output.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                Status = "Device successfully connected!";
                _adb.MarkConnected(ip, port, "Connected to " + address);
                return true;
            }
            if (attempt < 3 && (output.Contains("protocol fault") || output.Contains("failed to connect") || output.Contains("cannot connect to daemon")))
            {
                Status = $"Re-establishing ADB connection ({attempt + 1}/3)...";
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }
            Status = "Connection failed: " + output;
            return false;
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
