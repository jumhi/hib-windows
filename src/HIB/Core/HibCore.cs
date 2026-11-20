using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HIB.Core.Discovery;
using HIB.Core.Link;
using HIB.Core.Pairing;
using HIB.Core.Phone;

namespace HIB.Core;

public sealed class HibCoreOptions
{
    public string DataDir { get; set; } = AppPaths.DataDir;
    public int Port { get; set; } = LinkServerOptions.PortFromEnvironment();
    /// <summary>mDNS + UDP presence. Off when HIB_NO_DISCOVERY=1, HIB_TEST=1 or HIB_PORT is set (test mode); HIB_DISCOVERY=1 forces it on.</summary>
    public bool EnableDiscovery { get; set; } = DiscoveryDefault();
    public Func<string> DownloadsDir { get; set; } = () => AppPaths.DownloadsDir;
    public TimeSpan? PingInterval { get; set; }
    public TimeSpan? ActivityTimeout { get; set; }

    static bool DiscoveryDefault()
    {
        if (Environment.GetEnvironmentVariable("HIB_DISCOVERY") is "1" or "true") return true;
        var off = Environment.GetEnvironmentVariable("HIB_NO_DISCOVERY");
        if (off is "1" or "true") return false;
        if (Environment.GetEnvironmentVariable("HIB_TEST") is "1" or "true") return false;
        return Environment.GetEnvironmentVariable("HIB_PORT") is null;
    }
}

/// <summary>
/// Everything that is not UI: settings, link server, discovery, phone state, file receiver.
/// The Avalonia app and the fake-phone tests both build on this.
/// </summary>
public sealed class HibCore : IDisposable
{
    readonly List<string> _log = new();
    readonly object _logGate = new();

    public HibCore(HibCoreOptions? options = null)
    {
        Options = options ?? new HibCoreOptions();
        Directory.CreateDirectory(Options.DataDir);
        Settings = AppSettings.Load(Options.DataDir);
        Link = new LinkServer(Settings, new LinkServerOptions
        {
            Port = Options.Port,
            PingInterval = Options.PingInterval ?? TimeSpan.FromSeconds(4.5),
            ActivityTimeout = Options.ActivityTimeout ?? TimeSpan.FromSeconds(20),
        });
        Link.Log += AddLog;
        Hub = new PhoneHub(Link, Settings, Options.DataDir);
        Hub.Log += AddLog;
        Link.SavedAppPackagesProvider = () => Hub.Apps.Keys.OrderBy(k => k).ToList();
        Files = new FileReceiver(Link, Options.DownloadsDir, Path.Combine(Options.DataDir, "incoming"));
        Files.Log += AddLog;
        Sender = new FileSender(Link);
        Sender.Log += AddLog;
        Ecosystem = new PhoneEcosystem(Link);
        FlickPicker = new FlickScreenPicker(Path.Combine(Options.DataDir, "flick-screens.json"));
        Link.ConnectionChanged += (_, connected) =>
        {
            if (!connected && Udp is not null) _ = Udp.BurstAsync();
        };
    }

    public HibCoreOptions Options { get; }
    public AppSettings Settings { get; }
    public LinkServer Link { get; }
    public PhoneHub Hub { get; }
    public FileReceiver Files { get; }
    /// <summary>Desktop → phone / → other computer (through the phone) file sending.</summary>
    public FileSender Sender { get; }
    /// <summary>The other computers connected to the phone (multi-desktop).</summary>
    public PhoneEcosystem Ecosystem { get; }
    public FlickScreenPicker FlickPicker { get; }
    public ClipboardSync? Clipboard { get; private set; }
    public UdpPresence? Udp { get; private set; }
    public MdnsAdvertiser? Mdns { get; private set; }

    public event Action<string>? LogAdded;

    public IReadOnlyList<string> LogLines { get { lock (_logGate) return _log.ToList(); } }

    public void AddLog(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss} {line}";
        lock (_logGate)
        {
            _log.Add(stamped);
            if (_log.Count > 500) _log.RemoveRange(0, _log.Count - 500);
        }
        System.Diagnostics.Debug.WriteLine(stamped);
        LogAdded?.Invoke(stamped);
    }

    public void AttachClipboard(IClipboardBridge bridge, TimeSpan? interval = null)
    {
        Clipboard?.Dispose();
        Clipboard = new ClipboardSync(Link, Hub, Settings, bridge, interval);
        Clipboard.Log += AddLog;
        Clipboard.Start();
    }

    public void Start()
    {
        Link.Start();
        if (Options.EnableDiscovery && Settings.Discovery) StartDiscovery();
    }

    void StartDiscovery()
    {
        Udp = new UdpPresence(() => Link.DesktopName, () => Settings.StableId!, () => Link.Port,
            () => Link.IsConnected, () => Settings.LastPhoneIps);
        Udp.Log += AddLog;
        Udp.Start();
        Mdns = new MdnsAdvertiser(Link.DesktopName, Settings.StableId!, Link.Port);
        Mdns.Log += AddLog;
        Mdns.Start();
    }

    /// <summary>Restart discovery (after a rename or network change).</summary>
    public void RestartDiscovery()
    {
        Udp?.Stop();
        Mdns?.Stop();
        Udp = null;
        Mdns = null;
        if (Options.EnableDiscovery && Settings.Discovery) StartDiscovery();
    }

    /// <summary>The QR text for the Pair page.</summary>
    public string BuildQr()
        => QrPayload.Build(NetworkInfo.GetLocalIPv4().Select(a => a.Address), Link.Port, Link.DesktopName, Settings.EncryptionKey!);

    /// <summary>New key → every phone must re-scan. Open sessions are dropped.</summary>
    public void ResetPairing()
    {
        Settings.ResetKey();
        Link.DropAllSessions();
        AddLog("Pairing key reset — scan the new QR on the phone");
    }

    /// <summary>Renaming breaks the phone's auto-reconnect until it re-scans.</summary>
    public void Rename(string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || newName == Settings.DesktopName) return;
        Settings.DesktopName = newName;
        Settings.Save();
        RestartDiscovery();
    }

    /// <summary>Asks known/discovered phones to connect to us (HTTP :8888/wakeup, UDP fallback).</summary>
    public async Task<bool> WakePhoneAsync(CancellationToken ct = default)
    {
        var ips = new List<string>(Settings.LastPhoneIps);
        if (Udp is not null)
            ips.AddRange(Udp.Phones.Where(p => Settings.LastPhoneName is null || p.Name == Settings.LastPhoneName).SelectMany(p => p.Ips));
        if (ips.Count == 0) return false;
        var ok = await WakeUpClient.WakeAsync(ips, Link.Port, Link.DesktopName, ct).ConfigureAwait(false);
        AddLog(ok ? "Wake-up sent" : "Wake-up: phone did not answer over HTTP (UDP sent)");
        return ok;
    }

    public void Dispose()
    {
        Clipboard?.Dispose();
        Udp?.Dispose();
        Mdns?.Dispose();
        Link.Dispose();
    }
}
