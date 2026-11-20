using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HIB.Core.Pairing;

namespace HIB.Core.Discovery;

/// <summary>
/// Advertises "AirSync-&lt;name&gt;._airsync._tcp.local" with TXT id,name,port,ver=1,type=mac.
/// On Windows 10 1809+ it uses the OS mDNS responder (DnsServiceRegister); everywhere else, or if
/// that fails, it runs a small built-in responder on UDP 5353.
/// </summary>
public sealed class MdnsAdvertiser : IDisposable
{
    public const string ServiceType = "_airsync._tcp";

    readonly string _desktopName;
    readonly string _stableId;
    readonly int _port;
    WindowsDnsSd? _native;
    ManagedMdnsResponder? _managed;

    public MdnsAdvertiser(string desktopName, string stableId, int port)
    {
        _desktopName = desktopName;
        _stableId = stableId;
        _port = port;
    }

    public event Action<string>? Log;

    public string InstanceName => "AirSync-" + _desktopName;

    public string Mode => _native is not null ? "Windows DNS-SD" : _managed is not null ? "built-in responder" : "off";

    public Dictionary<string, string> Txt => new()
    {
        ["id"] = _stableId,
        ["name"] = _desktopName,
        ["port"] = _port.ToString(),
        ["ver"] = "1",
        ["type"] = "mac",
    };

    public void Start()
    {
        var hostLabel = "HIB-" + new string(_stableId.Where(char.IsLetterOrDigit).Take(8).ToArray());
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            try
            {
                var n = new WindowsDnsSd();
                var ip = NetworkInfo.GetLocalIPv4().FirstOrDefault()?.Address;
                var err = n.Register(InstanceName + "." + ServiceType + ".local", hostLabel + ".local", ip, (ushort)_port, Txt);
                if (err == 0)
                {
                    _native = n;
                    Log?.Invoke($"mDNS: registered {InstanceName} via Windows DNS-SD");
                    return;
                }
                Log?.Invoke($"mDNS: DnsServiceRegister failed ({err}); using built-in responder");
            }
            catch (Exception ex)
            {
                Log?.Invoke("mDNS: Windows DNS-SD unavailable: " + ex.Message);
            }
        }

        try
        {
            _managed = new ManagedMdnsResponder(InstanceName, hostLabel, (ushort)_port, Txt);
            _managed.Log += s => Log?.Invoke(s);
            _managed.Start();
            Log?.Invoke($"mDNS: advertising {InstanceName} (built-in responder)");
        }
        catch (Exception ex)
        {
            _managed = null;
            Log?.Invoke("mDNS: could not start responder: " + ex.Message);
        }
    }

    public void Stop()
    {
        try
        {
            if (OperatingSystem.IsWindows()) _native?.Deregister();
        }
        catch { }
        _native = null;
        _managed?.Dispose();
        _managed = null;
    }

    public void Dispose() => Stop();
}

/// <summary>Minimal RFC 6762/6763 responder for one service instance.</summary>
internal sealed class ManagedMdnsResponder : IDisposable
{
    static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    const int MdnsPort = 5353;
    const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeSrv = 33, TypeAny = 255;

    readonly string[] _serviceName;
    readonly string[] _instanceName;
    readonly string[] _hostName;
    readonly string[] _enumName = { "_services", "_dns-sd", "_udp", "local" };
    readonly ushort _port;
    readonly Dictionary<string, string> _txt;
    Socket? _socket;
    CancellationTokenSource? _cts;

    public ManagedMdnsResponder(string instanceLabel, string hostLabel, ushort port, Dictionary<string, string> txt)
    {
        _serviceName = new[] { "_airsync", "_tcp", "local" };
        _instanceName = new[] { instanceLabel, "_airsync", "_tcp", "local" };
        _hostName = new[] { hostLabel, "local" };
        _port = port;
        _txt = txt;
    }

    public event Action<string>? Log;

    public void Start()
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        s.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        foreach (var local in NetworkInfo.GetLocalIPv4())
        {
            try { s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, IPAddress.Parse(local.Address))); }
            catch (Exception ex) { Log?.Invoke($"mDNS: join on {local.Address} failed: {ex.Message}"); }
        }
        _socket = s;
        _cts = new CancellationTokenSource();
        _ = ReceiveLoopAsync(s, _cts.Token);
        _ = AnnounceLoopAsync(_cts.Token);
    }

    public void Dispose()
    {
        if (_socket is null) return;
        try { SendMulticast(BuildResponse(0, announce: true, ttlOverride: 0)); } catch { } // goodbye
        _cts?.Cancel();
        try { _socket.Dispose(); } catch { }
        _socket = null;
    }

    async Task AnnounceLoopAsync(CancellationToken ct)
    {
        try
        {
            for (var i = 0; i < 3; i++)
            {
                SendMulticast(BuildResponse(0, announce: true));
                await Task.Delay(TimeSpan.FromSeconds(1 << i), ct).ConfigureAwait(false);
            }
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
                SendMulticast(BuildResponse(0, announce: true));
            }
        }
        catch (OperationCanceledException) { }
    }

    void SendMulticast(byte[] packet)
    {
        var s = _socket;
        if (s is null) return;
        foreach (var local in NetworkInfo.GetLocalIPv4())
        {
            try
            {
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Parse(local.Address).GetAddressBytes());
                s.SendTo(packet, new IPEndPoint(Group, MdnsPort));
            }
            catch { }
        }
    }

    async Task ReceiveLoopAsync(Socket s, CancellationToken ct)
    {
        var buf = new byte[9000];
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try { r = await s.ReceiveFromAsync(buf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            try { HandlePacket(buf.AsSpan(0, r.ReceivedBytes).ToArray(), (IPEndPoint)r.RemoteEndPoint); }
            catch { /* malformed */ }
        }
    }

    void HandlePacket(byte[] p, IPEndPoint from)
    {
        var packet = BuildReply(p, from.Port, out var unicast);
        if (packet is null) return;
        if (unicast || from.Port != MdnsPort)
        {
            try { _socket?.SendTo(packet, from); } catch { }
        }
        if (from.Port == MdnsPort) SendMulticast(packet);
    }

    /// <summary>Answers a query packet, or returns null if it isn't about us (pure; used by tests).</summary>
    internal byte[]? BuildReply(byte[] p, int sourcePort, out bool unicast)
    {
        unicast = false;
        if (p.Length < 12) return null;
        var id = (ushort)(p[0] << 8 | p[1]);
        var flags = (ushort)(p[2] << 8 | p[3]);
        if ((flags & 0x8000) != 0) return null; // a response, not a query
        var qd = p[4] << 8 | p[5];
        var off = 12;
        bool wantPtr = false, wantInstance = false, wantHost = false, wantEnum = false;
        for (var i = 0; i < qd; i++)
        {
            var name = ReadName(p, ref off);
            var qtype = (ushort)(p[off] << 8 | p[off + 1]);
            var qclass = (ushort)(p[off + 2] << 8 | p[off + 3]);
            off += 4;
            if ((qclass & 0x8000) != 0) unicast = true;
            if (NameEq(name, _serviceName) && qtype is TypePtr or TypeAny) wantPtr = true;
            else if (NameEq(name, _instanceName) && qtype is TypeSrv or TypeTxt or TypeAny) wantInstance = true;
            else if (NameEq(name, _hostName) && qtype is TypeA or TypeAny) wantHost = true;
            else if (NameEq(name, _enumName) && qtype is TypePtr or TypeAny) wantEnum = true;
        }
        if (!(wantPtr || wantInstance || wantHost || wantEnum)) return null;
        return BuildResponse(sourcePort == MdnsPort ? (ushort)0 : id, announce: false,
            ptr: wantPtr, instance: wantInstance || wantPtr, host: wantHost || wantPtr || wantInstance, enumerate: wantEnum);
    }

    byte[] BuildResponse(ushort id, bool announce, bool ptr = true, bool instance = true, bool host = true, bool enumerate = false, uint? ttlOverride = null)
    {
        var answers = new List<byte[]>();
        var additional = new List<byte[]>();
        uint longTtl = ttlOverride ?? 4500, shortTtl = ttlOverride ?? 120;

        if (enumerate || announce)
            answers.Add(Record(_enumName, TypePtr, false, longTtl, EncodeName(_serviceName)));
        if (ptr || announce)
            answers.Add(Record(_serviceName, TypePtr, false, longTtl, EncodeName(_instanceName)));
        var target = announce || (instance && !ptr) ? answers : additional;
        if (instance || announce)
        {
            target.Add(Record(_instanceName, TypeSrv, true, shortTtl, SrvData()));
            target.Add(Record(_instanceName, TypeTxt, true, longTtl, TxtData()));
        }
        if (host || announce)
        {
            var hostTarget = announce || (!ptr && !instance) ? answers : additional;
            foreach (var local in NetworkInfo.GetLocalIPv4())
                hostTarget.Add(Record(_hostName, TypeA, true, shortTtl, IPAddress.Parse(local.Address).GetAddressBytes()));
        }

        using var ms = new MemoryStream();
        void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        U16(id);
        U16(0x8400);
        U16(0);
        U16(answers.Count);
        U16(0);
        U16(additional.Count);
        foreach (var a in answers) ms.Write(a);
        foreach (var a in additional) ms.Write(a);
        return ms.ToArray();
    }

    byte[] SrvData()
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0, 0, 0, 0, (byte)(_port >> 8), (byte)_port });
        ms.Write(EncodeName(_hostName));
        return ms.ToArray();
    }

    byte[] TxtData()
    {
        using var ms = new MemoryStream();
        foreach (var kv in _txt)
        {
            var b = Encoding.UTF8.GetBytes(kv.Key + "=" + kv.Value);
            var len = Math.Min(b.Length, 255);
            ms.WriteByte((byte)len);
            ms.Write(b, 0, len);
        }
        return ms.ToArray();
    }

    static byte[] Record(string[] name, ushort type, bool cacheFlush, uint ttl, byte[] rdata)
    {
        using var ms = new MemoryStream();
        ms.Write(EncodeName(name));
        ms.WriteByte((byte)(type >> 8)); ms.WriteByte((byte)type);
        var cls = (ushort)(cacheFlush ? 0x8001 : 0x0001);
        ms.WriteByte((byte)(cls >> 8)); ms.WriteByte((byte)cls);
        ms.WriteByte((byte)(ttl >> 24)); ms.WriteByte((byte)(ttl >> 16)); ms.WriteByte((byte)(ttl >> 8)); ms.WriteByte((byte)ttl);
        ms.WriteByte((byte)(rdata.Length >> 8)); ms.WriteByte((byte)rdata.Length);
        ms.Write(rdata);
        return ms.ToArray();
    }

    internal static byte[] EncodeName(string[] labels)
    {
        using var ms = new MemoryStream();
        foreach (var l in labels)
        {
            var b = Encoding.UTF8.GetBytes(l);
            var len = Math.Min(b.Length, 63);
            ms.WriteByte((byte)len);
            ms.Write(b, 0, len);
        }
        ms.WriteByte(0);
        return ms.ToArray();
    }

    internal static List<string> ReadName(byte[] p, ref int off)
    {
        var labels = new List<string>();
        var pos = off;
        var jumped = false;
        var guard = 0;
        while (true)
        {
            if (pos >= p.Length || guard++ > 128) throw new InvalidDataException();
            int len = p[pos];
            if (len == 0) { pos++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                var ptr = (len & 0x3F) << 8 | p[pos + 1];
                if (!jumped) off = pos + 2;
                jumped = true;
                pos = ptr;
                continue;
            }
            labels.Add(Encoding.UTF8.GetString(p, pos + 1, len));
            pos += len + 1;
        }
        if (!jumped) off = pos;
        return labels;
    }

    static bool NameEq(List<string> a, string[] b)
        => a.Count == b.Length && a.Zip(b).All(x => string.Equals(x.First, x.Second, StringComparison.OrdinalIgnoreCase));
}

/// <summary>P/Invoke wrapper for the Windows 10 1809+ DNS-SD registration API (dnsapi.dll).</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDnsSd
{
    const uint DnsRequestPending = 9506;

    [StructLayout(LayoutKind.Sequential)]
    struct DnsServiceRegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr pServiceInstance;
        public IntPtr pRegisterCompletionCallback;
        public IntPtr pQueryContext;
        public IntPtr hCredentials;
        public int unicastEnabled;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate void RegisterComplete(uint status, IntPtr context, IntPtr instance);

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern IntPtr DnsServiceConstructInstance(string pServiceName, string pHostName, IntPtr pIp4, IntPtr pIp6,
        ushort wPort, ushort wPriority, ushort wWeight, uint dwPropertiesCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] keys,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] values);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    static extern uint DnsServiceRegister(ref DnsServiceRegisterRequest request, IntPtr cancel);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    static extern uint DnsServiceDeRegister(ref DnsServiceRegisterRequest request, IntPtr cancel);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    static extern void DnsServiceFreeInstance(IntPtr instance);

    // Kept alive for the lifetime of the registration.
    static readonly RegisterComplete Callback = OnComplete;
    IntPtr _instance;
    IntPtr _ip4;
    DnsServiceRegisterRequest _request;

    static void OnComplete(uint status, IntPtr context, IntPtr instance)
    {
        if (instance != IntPtr.Zero) DnsServiceFreeInstance(instance);
    }

    public uint Register(string serviceName, string hostName, string? ipv4, ushort port, Dictionary<string, string> txt)
    {
        if (ipv4 is not null && IPAddress.TryParse(ipv4, out var addr))
        {
            _ip4 = Marshal.AllocHGlobal(4);
            Marshal.Copy(addr.GetAddressBytes(), 0, _ip4, 4); // IP4_ADDRESS is network byte order
        }
        var keys = txt.Keys.ToArray();
        var values = keys.Select(k => txt[k]).ToArray();
        _instance = DnsServiceConstructInstance(serviceName, hostName, _ip4, IntPtr.Zero, port, 0, 0, (uint)keys.Length, keys, values);
        if (_instance == IntPtr.Zero) return 1;
        _request = new DnsServiceRegisterRequest
        {
            Version = 1,
            InterfaceIndex = 0,
            pServiceInstance = _instance,
            pRegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(Callback),
            pQueryContext = IntPtr.Zero,
            hCredentials = IntPtr.Zero,
            unicastEnabled = 0,
        };
        var rc = DnsServiceRegister(ref _request, IntPtr.Zero);
        return rc == DnsRequestPending ? 0u : rc;
    }

    public void Deregister()
    {
        if (_instance == IntPtr.Zero) return;
        try { DnsServiceDeRegister(ref _request, IntPtr.Zero); } catch { }
        // The instance is owned by the OS until the deregistration completes; leak it rather than risk a double free.
        _instance = IntPtr.Zero;
    }
}
