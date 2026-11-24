using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using HIB.Core.Pairing;

namespace HIB.Core.Link;

public sealed class LinkServerOptions
{
    public const int DefaultPort = 6996;

    public int Port { get; set; } = DefaultPort;
    public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(4.5);
    public TimeSpan ActivityTimeout { get; set; } = TimeSpan.FromSeconds(20);
    /// <summary>Must be ≥ 4.0.0 or the phone shows an "outdated" toast.</summary>
    public string AppVersion { get; set; } = "4.1.0";
    public int MaxMessageBytes { get; set; } = 96 * 1024 * 1024;

    /// <summary>HIB_PORT env var overrides the port (used by tests).</summary>
    public static int PortFromEnvironment(int fallback = DefaultPort)
        => int.TryParse(Environment.GetEnvironmentVariable("HIB_PORT"), out var p) && p is > 0 and < 65536 ? p : fallback;
}

/// <summary>
/// Plain (no TLS) WebSocket server on :port/socket, built on a raw TcpListener so it needs no
/// URL ACL / admin rights on Windows. Implements the H.I.B link: AES-GCM framing, primary
/// session selection, device → macInfo handshake, 4.5 s app pings and a 20 s inactivity drop.
/// </summary>
public sealed class LinkServer : ILinkSession, IDisposable
{
    static readonly JsonSerializerOptions SendJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly AppSettings _settings;
    readonly LinkServerOptions _options;
    readonly ConcurrentDictionary<Guid, LinkConnection> _connections = new();
    readonly object _gate = new();
    TcpListener? _listener;
    CancellationTokenSource? _cts;
    LinkConnection? _primary;
    bool _connected;
    PhoneDeviceInfo? _device;

    public LinkServer(AppSettings settings, LinkServerOptions? options = null)
    {
        _settings = settings;
        _options = options ?? new LinkServerOptions();
    }

    // ---------------- ILinkSession ----------------

    public bool IsConnected { get { lock (_gate) return _connected; } }
    public PhoneDeviceInfo? Device { get { lock (_gate) return _device; } }
    public string DesktopName => _settings.DesktopName ?? AppSettings.DefaultDesktopName();
    public int Port => _options.Port;
    public byte[] GetPairingKey() => (byte[])(_settings.LinkKeyBytes() ?? throw new InvalidOperationException("No pairing key")).Clone();

    public event EventHandler<LinkMessageEventArgs>? MessageReceived;
    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<PhoneDeviceInfo>? DeviceUpdated;

    /// <summary>Diagnostic log lines.</summary>
    public event Action<string>? Log;

    /// <summary>Supplies macInfo.savedAppPackages (the package list the desktop already has icons for).</summary>
    public Func<IReadOnlyList<string>>? SavedAppPackagesProvider { get; set; }

    /// <summary>
    /// A message sent on the same connection right after every macInfo (Point &amp; Flick's screensInfo, SPACE.md §7.2).
    /// Null result = nothing to send.
    /// </summary>
    public Func<(string Type, object Data)?>? AfterMacInfo { get; set; }

    /// <summary>Remote IP of the primary connection, if any.</summary>
    public string? PrimaryRemoteAddress { get { lock (_gate) return _primary?.RemoteIp; } }

    /// <summary>Seconds since the last inbound frame on the primary connection (for a "weak link" hint).</summary>
    public double SecondsSinceLastActivity { get { lock (_gate) return _primary is null ? 0 : (DateTime.UtcNow - _primary.LastActivity).TotalSeconds; } }

    public bool IsListening => _listener is not null;

    /// <summary>
    /// Handles an upgraded <c>/peer</c> WebSocket (SPACE.md §3.3). Runs for the whole life of the
    /// peer session; the TCP client is disposed when it returns. Null = peer links off.
    /// </summary>
    public Func<WebSocket, TcpClient, string, CancellationToken, Task>? PeerHandler { get; set; }

    /// <summary>False while this computer has no space key: <c>/peer</c> upgrades are refused.</summary>
    public Func<bool>? PeerAccepts { get; set; }

    // ---------------- lifecycle ----------------

    public void Start()
    {
        if (_listener is not null) return;
        _cts = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Any, _options.Port);
        listener.Start(64);
        _listener = listener;
        WriteLog($"Listening on ws://0.0.0.0:{_options.Port}/socket");
        _ = AcceptLoopAsync(listener, _cts.Token);
        _ = KeepAliveLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        foreach (var c in _connections.Values) c.Abort();
        _connections.Clear();
        SetPrimary(null, raise: true);
    }

    public void Dispose() => Stop();

    /// <summary>Drops every open session (e.g. after the pairing key was reset).</summary>
    public void DropAllSessions()
    {
        foreach (var c in _connections.Values) c.Abort();
    }

    /// <summary>Asks the phone to disconnect (it won't auto-reconnect until the user reconnects on the phone).</summary>
    public Task<bool> RequestPhoneDisconnectAsync() => SendAsync("disconnectRequest");

    // ---------------- sending ----------------

    public async Task<bool> SendAsync(string type, object? data = null, CancellationToken cancellationToken = default)
    {
        LinkConnection? target;
        lock (_gate) target = _primary;
        if (target is null) return false;
        return await SendToAsync(target, type, data, cancellationToken).ConfigureAwait(false);
    }

    public static string BuildEnvelope(string type, object? data)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("type", type);
            w.WritePropertyName("data");
            switch (data)
            {
                case null:
                    w.WriteStartObject();
                    w.WriteEndObject();
                    break;
                case JsonElement e:
                    e.WriteTo(w);
                    break;
                case JsonNode n:
                    n.WriteTo(w);
                    break;
                default:
                    JsonSerializer.Serialize(w, data, data.GetType(), SendJson);
                    break;
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    async Task<bool> SendToAsync(LinkConnection conn, string type, object? data, CancellationToken ct)
    {
        var key = _settings.LinkKeyBytes();
        if (key is null) return false;
        string frame;
        try { frame = LinkCrypto.Encrypt(BuildEnvelope(type, data), key); }
        catch (Exception ex)
        {
            WriteLog($"Serialize {type} failed: {ex.Message}");
            return false;
        }
        var ok = await conn.SendTextAsync(frame, ct).ConfigureAwait(false);
        if (!ok) WriteLog($"Send {type} failed");
        return ok;
    }

    // ---------------- accept + handshake ----------------

    async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                WriteLog("Accept failed: " + ex.Message);
                await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
                continue;
            }
            _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
        }
    }

    async Task HandleClientAsync(TcpClient client, CancellationToken serverCt)
    {
        client.NoDelay = true;
        var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        var remote = remoteIp is null ? "?" : (remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp).ToString();
        NetworkStream stream;
        try { stream = client.GetStream(); }
        catch { client.Dispose(); return; }

        string? request;
        using (var hsCts = CancellationTokenSource.CreateLinkedTokenSource(serverCt))
        {
            hsCts.CancelAfter(TimeSpan.FromSeconds(10));
            try { request = await ReadHttpHeadAsync(stream, hsCts.Token).ConfigureAwait(false); }
            catch { request = null; }
        }
        if (request is null) { client.Dispose(); return; }

        var lines = request.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var idx = line.IndexOf(':');
            if (idx > 0) headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }
        var path = requestLine.Length > 1 ? requestLine[1].Split('?')[0] : "/";
        var isUpgrade = headers.TryGetValue("Upgrade", out var up) && up.Equals("websocket", StringComparison.OrdinalIgnoreCase);

        // SPACE.md §3.3: computer ↔ computer peer links share this server on a second path.
        // A computer without a space key yet refuses them (403); /socket below is unchanged.
        if (isUpgrade && path == "/peer" && headers.TryGetValue("Sec-WebSocket-Key", out var peerKey))
        {
            var handler = PeerHandler;
            if (handler is null || PeerAccepts?.Invoke() != true)
            {
                const string refused = "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                try { await stream.WriteAsync(Encoding.ASCII.GetBytes(refused), serverCt).ConfigureAwait(false); } catch { }
                client.Dispose();
                return;
            }
            var peerAccept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(peerKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var peerResponse = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                               $"Sec-WebSocket-Accept: {peerAccept}\r\n\r\n";
            try { await stream.WriteAsync(Encoding.ASCII.GetBytes(peerResponse), serverCt).ConfigureAwait(false); }
            catch { client.Dispose(); return; }
            // SPACE.md §12.3: no per-message compression on the peer link (small frames, no deflate latency).
            var peerWs = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero, DangerousDeflateOptions = null });
            try { await handler(peerWs, client, remote, serverCt).ConfigureAwait(false); }
            catch (Exception ex) { WriteLog("Peer session error: " + ex.Message); }
            finally { client.Dispose(); }
            return;
        }

        if (!isUpgrade || path != "/socket" || !headers.TryGetValue("Sec-WebSocket-Key", out var wsKey))
        {
            var body = path == "/" ? "H.I.B desktop is running." : "Not found";
            var status = path == "/" && !isUpgrade ? "200 OK" : "404 Not Found";
            var resp = $"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
            try { await stream.WriteAsync(Encoding.UTF8.GetBytes(resp), serverCt).ConfigureAwait(false); } catch { }
            client.Dispose();
            return;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(wsKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                       $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        try { await stream.WriteAsync(Encoding.ASCII.GetBytes(response), serverCt).ConfigureAwait(false); }
        catch { client.Dispose(); return; }

        // ManagedWebSocket answers RFC 6455 ping control frames automatically while receiving.
        var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
        {
            IsServer = true,
            KeepAliveInterval = TimeSpan.Zero,
        });
        var conn = new LinkConnection(client, ws, remote);
        _connections[conn.Id] = conn;
        lock (_gate)
        {
            // The first session to connect becomes primary.
            if (_primary is null) _primary = conn;
        }
        WriteLog($"Session {conn.ShortId} opened from {remote}");
        try { await ReceiveLoopAsync(conn, serverCt).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // normal termination paths
        }
        catch (Exception ex) { WriteLog($"Session {conn.ShortId} error: {ex.Message}"); }
        finally
        {
            _connections.TryRemove(conn.Id, out _);
            conn.Abort();
            WriteLog($"Session {conn.ShortId} closed");
            OnConnectionGone(conn);
        }
    }

    static async Task<string?> ReadHttpHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[1];
        var sb = new StringBuilder();
        while (sb.Length < 16 * 1024)
        {
            var n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n == 0) return null;
            sb.Append((char)buf[0]);
            if (sb.Length >= 4 && sb[^1] == '\n' && sb[^2] == '\r' && sb[^3] == '\n' && sb[^4] == '\r')
                return sb.ToString(0, sb.Length - 4);
        }
        return null;
    }

    async Task ReceiveLoopAsync(LinkConnection conn, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (conn.Ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await conn.Ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try { await conn.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false); } catch { }
                    return;
                }
                message.Write(buffer, 0, result.Count);
                if (message.Length > _options.MaxMessageBytes) throw new IOException("Message too large");
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                conn.Touch(); // binary frames only refresh the activity timer
                continue;
            }

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            var key = _settings.LinkKeyBytes();
            var plain = key is null ? null : LinkCrypto.TryDecrypt(text, key);
            if (plain is null)
            {
                // Wrong key or garbage: drop silently, do not refresh activity (matches the Mac).
                WriteLog($"Session {conn.ShortId}: undecryptable frame dropped ({text.Length} chars)");
                continue;
            }
            conn.Touch();
            HandlePlaintext(conn, plain);
        }
    }

    void HandlePlaintext(LinkConnection conn, string plain)
    {
        using var doc = LenientJson.TryParse(plain);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            WriteLog($"Session {conn.ShortId}: unparsable JSON dropped");
            return;
        }
        var root = doc.RootElement;
        var type = root.Str("type");
        if (string.IsNullOrEmpty(type)) return;
        var data = root.TryGetProperty("data", out var d) ? d.Clone() : JsonDocument.Parse("{}").RootElement.Clone();

        if (type == "device")
        {
            HandleDevice(conn, data);
            return;
        }

        lock (_gate)
        {
            if (!ReferenceEquals(conn, _primary)) return; // non-primary sessions are ignored except "device"
        }

        switch (type)
        {
            case "pong":
                return;
            case "userDisconnected":
                WriteLog("Phone disconnected on purpose");
                conn.Abort();
                break;
            case "status":
                UpdateBatteryFromStatus(data);
                break;
        }

        Raise(type, data);
    }

    void Raise(string type, JsonElement data)
    {
        var handler = MessageReceived;
        if (handler is null) return;
        var args = new LinkMessageEventArgs(type, data);
        foreach (EventHandler<LinkMessageEventArgs> h in handler.GetInvocationList())
        {
            try { h(this, args); }
            catch (Exception ex) { WriteLog($"Handler for {type} threw: {ex.Message}"); }
        }
    }

    void HandleDevice(LinkConnection conn, JsonElement data)
    {
        var name = data.Str("name");
        var ip = data.Str("ipAddress");
        var port = data.Int("port");
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(ip) || port is null)
        {
            WriteLog("device message missing name/ipAddress/port — ignored");
            return;
        }
        var id = data.Str("id") ?? "";
        var target = data.Str("targetIpAddress")?.Trim();

        LinkConnection? replaced = null;
        bool becameConnected;
        PhoneDeviceInfo info;
        lock (_gate)
        {
            if (!ReferenceEquals(_primary, conn))
            {
                var sameDevice = _device is not null &&
                                 ((id.Length > 0 && id == _device.Id) || (id.Length == 0 && name == _device.Name));
                if (_primary is null)
                {
                    _primary = conn;
                }
                else if (NetworkInfo.IsTakeoverPrivate(target) || sameDevice)
                {
                    replaced = _primary;
                    _primary = conn;
                }
                else
                {
                    WriteLog($"Session {conn.ShortId}: device from non-primary session ignored");
                    return;
                }
            }

            var prev = _device;
            var samePhone = prev is not null && prev.Id == id && prev.Name == name;
            var wallpaper = LenientJson.Base64(data.Str("wallpaper")) ?? (samePhone ? prev!.Wallpaper : null);
            var ble = data.Str("bleAuthToken");
            if (ble is not null && LenientJson.Base64(ble)?.Length != 16) ble = samePhone ? prev!.BleAuthToken : null;

            info = new PhoneDeviceInfo
            {
                Id = id,
                Name = name,
                IpAddress = ip,
                RemoteAddress = conn.RemoteIp,
                Port = port.Value,
                Version = data.Str("version") ?? "2.0.0",
                AdbPorts = data.StrList("adbPorts"),
                TargetIpAddress = target,
                BleAuthToken = ble,
                Wallpaper = wallpaper,
                BatteryLevel = samePhone ? prev!.BatteryLevel : -1,
                IsCharging = samePhone && prev!.IsCharging,
                ConnectedAt = samePhone && _connected ? prev!.ConnectedAt : DateTimeOffset.Now,
            };
            _device = info;
            conn.SentDevice = true;
            becameConnected = !_connected;
            _connected = true;
        }

        if (replaced is not null)
        {
            WriteLog($"Session {conn.ShortId} took over from {replaced.ShortId}");
            replaced.Abort();
        }

        // Remember the phone for wake-up / reconnect hints.
        var ips = new List<string> { ip };
        if (conn.RemoteIp != ip && conn.RemoteIp != "?") ips.Add(conn.RemoteIp);
        if (_settings.LastPhoneName != name || !_settings.LastPhoneIps.SequenceEqual(ips))
        {
            _settings.LastPhoneName = name;
            _settings.LastPhoneIps = ips;
            _settings.Save();
        }

        WriteLog($"Handshake: {name} ({ip}) v{info.Version}");
        _ = SendToAsync(conn, "macInfo", BuildMacInfo(), CancellationToken.None);
        // Queued behind macInfo on the same connection (the send lock is FIFO).
        (string Type, object Data)? after = null;
        try { after = AfterMacInfo?.Invoke(); }
        catch (Exception ex) { WriteLog("After-macInfo message failed: " + ex.Message); }
        if (after is { } extra) _ = SendToAsync(conn, extra.Type, extra.Data, CancellationToken.None);

        if (becameConnected) SafeInvoke(() => ConnectionChanged?.Invoke(this, true));
        SafeInvoke(() => DeviceUpdated?.Invoke(this, info));
        Raise("device", data);
    }

    public object BuildMacInfo()
    {
        var saved = SavedAppPackagesProvider?.Invoke() ?? _settings.SavedAppPackages;
        const string model = "WindowsPC";
        const string category = "Windows PC";
        return new Dictionary<string, object?>
        {
            ["name"] = DesktopName,
            ["modelIdentifier"] = model,
            ["categoryType"] = category,
            ["exactDeviceName"] = $"{Environment.MachineName} ({(OperatingSystem.IsWindows() ? "Windows" : Environment.OSVersion.Platform.ToString())})",
            ["isPlusSubscription"] = true,
            ["version"] = _options.AppVersion,
            ["savedAppPackages"] = saved.ToArray(),
            ["model"] = model,
            ["type"] = category,
            ["isPlus"] = true,
        };
    }

    void UpdateBatteryFromStatus(JsonElement data)
    {
        var battery = data.Obj("battery");
        if (battery is null) return;
        var level = battery.Value.Int("level");
        var charging = battery.Value.Bool("isCharging");
        PhoneDeviceInfo? updated = null;
        lock (_gate)
        {
            if (_device is null || level is null) return;
            if (_device.BatteryLevel != level || _device.IsCharging != (charging ?? false))
            {
                _device = _device with { BatteryLevel = level.Value, IsCharging = charging ?? false };
                updated = _device;
            }
        }
        if (updated is not null) SafeInvoke(() => DeviceUpdated?.Invoke(this, updated));
    }

    void OnConnectionGone(LinkConnection conn)
    {
        bool wasPrimary;
        LinkConnection? promoted = null;
        lock (_gate)
        {
            wasPrimary = ReferenceEquals(_primary, conn);
            if (!wasPrimary) return;
            _primary = null;
            // Promote another live session that already completed a device handshake.
            promoted = _connections.Values.FirstOrDefault(c => c.SentDevice && c.Ws.State == WebSocketState.Open);
            if (promoted is not null)
            {
                _primary = promoted;
                return;
            }
        }
        SetPrimary(null, raise: true);
    }

    void SetPrimary(LinkConnection? conn, bool raise)
    {
        bool changed;
        lock (_gate)
        {
            _primary = conn;
            changed = _connected && conn is null;
            if (conn is null) _connected = false;
        }
        if (changed && raise)
        {
            WriteLog("Phone disconnected");
            SafeInvoke(() => ConnectionChanged?.Invoke(this, false));
        }
    }

    // ---------------- keepalive ----------------

    async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.PingInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = DateTime.UtcNow;
                foreach (var c in _connections.Values)
                {
                    if (now - c.LastActivity > _options.ActivityTimeout)
                    {
                        WriteLog($"Session {c.ShortId} inactive for {(now - c.LastActivity).TotalSeconds:0}s — dropping");
                        c.Abort();
                    }
                }
                LinkConnection? primary;
                lock (_gate) primary = _primary;
                if (primary is not null) await SendToAsync(primary, "ping", null, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    void SafeInvoke(Action a)
    {
        try { a(); }
        catch (Exception ex) { WriteLog("Event handler threw: " + ex.Message); }
    }

    void WriteLog(string line)
    {
        try { Log?.Invoke(line); } catch { }
    }

    // ---------------- connection ----------------

    sealed class LinkConnection
    {
        readonly SemaphoreSlim _sendLock = new(1, 1);
        long _lastActivityTicks = DateTime.UtcNow.Ticks;
        int _aborted;

        public LinkConnection(TcpClient client, WebSocket ws, string remoteIp)
        {
            Client = client;
            Ws = ws;
            RemoteIp = remoteIp;
        }

        public Guid Id { get; } = Guid.NewGuid();
        public string ShortId => Id.ToString()[..8];
        public TcpClient Client { get; }
        public WebSocket Ws { get; }
        public string RemoteIp { get; }
        public bool SentDevice { get; set; }
        public DateTime LastActivity => new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
        public void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

        public async Task<bool> SendTextAsync(string text, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            try
            {
                await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return false; }
            try
            {
                if (Ws.State != WebSocketState.Open) return false;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                await Ws.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
                return true;
            }
            catch { return false; }
            finally { _sendLock.Release(); }
        }

        public void Abort()
        {
            if (Interlocked.Exchange(ref _aborted, 1) == 1) return;
            try { Ws.Abort(); } catch { }
            try { Client.Dispose(); } catch { }
        }
    }
}
