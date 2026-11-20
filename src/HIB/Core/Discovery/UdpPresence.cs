using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HIB.Core.Link;
using HIB.Core.Pairing;

namespace HIB.Core.Discovery;

/// <summary>A phone heard over UDP presence (or mDNS).</summary>
public sealed record DiscoveredPhone(string Id, string Name, IReadOnlyList<string> Ips, DateTime LastSeenUtc);

/// <summary>
/// UDP presence on port 8889: broadcasts {"type":"presence","deviceType":"mac",…} so the phone
/// (re)connects, and listens for the phone's own presence/bye.
/// </summary>
public sealed class UdpPresence : IDisposable
{
    public const int BroadcastPort = 8889;

    readonly Func<string> _name;
    readonly Func<string> _id;
    readonly Func<int> _port;
    readonly Func<bool> _isConnected;
    readonly Func<IEnumerable<string>> _knownPhoneIps;
    readonly ConcurrentDictionary<string, DiscoveredPhone> _phones = new();
    CancellationTokenSource? _cts;
    UdpClient? _listener;

    public UdpPresence(Func<string> name, Func<string> id, Func<int> port, Func<bool> isConnected, Func<IEnumerable<string>> knownPhoneIps)
    {
        _name = name;
        _id = id;
        _port = port;
        _isConnected = isConnected;
        _knownPhoneIps = knownPhoneIps;
    }

    public event Action<string>? Log;
    public event Action? PhonesChanged;

    public IReadOnlyList<DiscoveredPhone> Phones
        => _phones.Values.Where(p => DateTime.UtcNow - p.LastSeenUtc < TimeSpan.FromSeconds(35)).OrderBy(p => p.Name).ToList();

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        try
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.EnableBroadcast = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, BroadcastPort));
            _listener = udp;
            _ = ListenLoopAsync(udp, _cts.Token);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"UDP {BroadcastPort} listen failed: {ex.Message} (broadcasting only)");
        }
        _ = PeriodicLoopAsync(_cts.Token);
        _ = BurstAsync();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        try { _listener?.Dispose(); } catch { }
        _listener = null;
    }

    public void Dispose() => Stop();

    public string BuildPresenceJson()
    {
        var ips = NetworkInfo.GetLocalIPv4().Select(a => a.Address).ToArray();
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "presence",
            ["deviceType"] = "mac",
            ["id"] = _id(),
            ["name"] = _name(),
            ["ips"] = ips,
            ["port"] = _port(),
        });
    }

    /// <summary>3 sends 100 ms apart (start, network change, disconnect).</summary>
    public async Task BurstAsync()
    {
        for (var i = 0; i < 3; i++)
        {
            SendOnce();
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    public void SendOnce()
    {
        var payload = Encoding.UTF8.GetBytes(BuildPresenceJson());
        var locals = NetworkInfo.GetLocalIPv4();
        foreach (var local in locals)
        {
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.EnableBroadcast = true;
                s.Bind(new IPEndPoint(IPAddress.Parse(local.Address), 0));
                s.SendTo(payload, new IPEndPoint(IPAddress.Broadcast, BroadcastPort));
                if (local.Broadcast is not null)
                    s.SendTo(payload, new IPEndPoint(IPAddress.Parse(local.Broadcast), BroadcastPort));
            }
            catch { /* adapter may not allow broadcast */ }
        }
        // Unicast to phones we know about (same subnet or not; harmless).
        foreach (var ip in _knownPhoneIps().Concat(_phones.Values.SelectMany(p => p.Ips)).Distinct())
        {
            if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork) continue;
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.SendTo(payload, new IPEndPoint(addr, BroadcastPort));
            }
            catch { }
        }
    }

    async Task PeriodicLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!_isConnected()) SendOnce();
                var stale = _phones.Where(p => DateTime.UtcNow - p.Value.LastSeenUtc > TimeSpan.FromSeconds(35)).Select(p => p.Key).ToList();
                foreach (var k in stale) _phones.TryRemove(k, out _);
                if (stale.Count > 0) PhonesChanged?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task ListenLoopAsync(UdpClient udp, CancellationToken ct)
    {
        var localIps = new HashSet<string>(NetworkInfo.GetLocalIPv4().Select(a => a.Address));
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            try
            {
                var text = Encoding.UTF8.GetString(r.Buffer);
                using var doc = LenientJson.TryParse(text);
                if (doc is null) continue;
                var root = doc.RootElement;
                if (root.Str("deviceType") != "android") continue;
                var id = root.Str("id") ?? r.RemoteEndPoint.Address.ToString();
                switch (root.Str("type"))
                {
                    case "presence":
                        var ips = root.StrList("ips").Where(ip => NetworkInfo.IsPrivate(ip) && !localIps.Contains(ip)).ToList();
                        var sender = r.RemoteEndPoint.Address.ToString();
                        if (!ips.Contains(sender) && NetworkInfo.IsPrivate(sender) && !localIps.Contains(sender)) ips.Insert(0, sender);
                        var isNew = !_phones.ContainsKey(id);
                        _phones[id] = new DiscoveredPhone(id, root.Str("name") ?? "Android", ips, DateTime.UtcNow);
                        if (isNew)
                        {
                            Log?.Invoke($"UDP: found phone {root.Str("name")} at {string.Join(",", ips)}");
                            PhonesChanged?.Invoke();
                        }
                        break;
                    case "bye":
                        if (_phones.TryRemove(id, out _)) PhonesChanged?.Invoke();
                        break;
                }
            }
            catch { }
        }
    }

    /// <summary>Records a phone found by other means (mDNS), so wake-up can target it.</summary>
    public void AddOrUpdate(DiscoveredPhone phone)
    {
        _phones[phone.Id] = phone;
        PhonesChanged?.Invoke();
    }
}
