// H.I.B Space (docs/SPACE.md §3.3): one direct, encrypted link to another computer.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HIB.Core.Link;

namespace HIB.Core.Space;

/// <summary>The <c>peerHello</c> payload.</summary>
public sealed record PeerHello(string Id, string Name, string Platform, string Type, int Port, IReadOnlyList<string> Ips, int Version)
{
    public object ToWire() => new Dictionary<string, object?>
    {
        ["id"] = Id,
        ["name"] = Name,
        ["platform"] = Platform,
        ["type"] = Type,
        ["port"] = Port,
        ["ips"] = Ips,
        ["v"] = Version,
    };

    public static PeerHello? Parse(JsonElement d)
    {
        var id = d.Str("id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        return new PeerHello(id, d.Str("name") ?? id, (d.Str("platform") ?? "mac").ToLowerInvariant(), d.Str("type") ?? "",
            d.Int("port") ?? 0, d.StrList("ips"), d.Int("v") ?? 1);
    }
}

/// <summary>
/// A peer WebSocket (either side) speaking the phone's framing with the space key:
/// text frames of <c>base64(nonce12 ‖ ciphertext ‖ tag16)</c>. It is an <see cref="ILinkSession"/>
/// so the §6.9 <see cref="Phone.FileSender"/>/<see cref="Phone.FileReceiver"/> run over it unchanged.
/// </summary>
public sealed class PeerLink : ILinkSession, IDisposable
{
    public const int CloseDecryptFailed = 4003;
    public const int CloseDuplicate = 4009;

    readonly WebSocket _ws;
    readonly IDisposable? _owner;
    readonly byte[] _key;
    readonly PeerHello _own;
    readonly SemaphoreSlim _sendLock = new(1, 1);
    readonly CancellationTokenSource _cts = new();
    long _lastActivityTicks = DateTime.UtcNow.Ticks;
    int _closed;
    bool _helloDone;
    bool _anyDecrypted;

    internal PeerLink(WebSocket ws, IDisposable? owner, byte[] key, PeerHello own, bool isClient, string remoteIp)
    {
        _ws = ws;
        _owner = owner;
        _key = (byte[])key.Clone();
        _own = own;
        IsClient = isClient;
        RemoteIp = remoteIp;
    }

    /// <summary>True when this computer dialled (it is the WebSocket client).</summary>
    public bool IsClient { get; }
    public string RemoteIp { get; }
    public DateTime OpenedAt { get; } = DateTime.UtcNow;
    public PeerHello? Hello { get; private set; }
    public string PeerId => Hello?.Id ?? "";
    public string PeerName => Hello?.Name ?? RemoteIp;
    public string PeerPlatform => Hello?.Platform ?? "mac";

    /// <summary>The id of the side that dialled: the duplicate tie-break key (SPACE.md §3.3).</summary>
    public string ClientId => IsClient ? _own.Id : PeerId;

    public DateTime LastActivity => new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
    public bool IsOpen => _helloDone && IsAlive;

    /// <summary>Not closed (it may still be finishing its hello).</summary>
    public bool IsAlive => Volatile.Read(ref _closed) == 0 && _ws.State == WebSocketState.Open;

    /// <summary>True once this link completed its hello and was kept (it was "up").</summary>
    public bool WasUp => _helloDone;

    /// <summary>The close code we sent or received, once closed.</summary>
    public int? CloseCode { get; private set; }

    // ---- peerActivity (SPACE.md §3.1) ----
    public bool PeerInUse { get; private set; }
    public int PeerIdleSeconds { get; private set; } = -1;
    public DateTime? PeerActivityAt { get; private set; }

    /// <summary>When the peer last touched its keyboard/mouse (UTC), if it reported activity.</summary>
    public DateTime? PeerLastActiveAt => PeerActivityAt is { } at && PeerIdleSeconds >= 0 ? at - TimeSpan.FromSeconds(PeerIdleSeconds) : null;

    // ---- ILinkSession ----
    public bool IsConnected => IsOpen;
    public PhoneDeviceInfo? Device => null;
    public string DesktopName => _own.Name;
    public int Port => _own.Port;
    public byte[] GetPairingKey() => (byte[])_key.Clone();
    public event EventHandler<LinkMessageEventArgs>? MessageReceived;
    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<PhoneDeviceInfo>? DeviceUpdated { add { } remove { } }

    internal event Action<PeerLink>? HelloCompleted;
    /// <summary>Decides, when the first hello arrives, whether this link is kept (false → closed 4009).</summary>
    internal Func<PeerLink, bool>? Admit;
    /// <summary>The link this one replaced (closed with 4009 once this one is up).</summary>
    internal PeerLink? Replaced;
    internal event Action<PeerLink>? ActivityChanged;
    internal event Action<PeerLink>? Closed;
    internal Action<string>? Log;

    public Task<bool> SendAsync(string type, object? data = null, CancellationToken cancellationToken = default)
        => SendRawAsync(type, data, cancellationToken, requireHello: true);

    async Task<bool> SendRawAsync(string type, object? data, CancellationToken ct, bool requireHello)
    {
        if (Volatile.Read(ref _closed) != 0) return false;
        if (requireHello && !_helloDone) return false;
        string frame;
        try { frame = LinkCrypto.Encrypt(LinkServer.BuildEnvelope(type, data), _key); }
        catch { return false; }
        var bytes = Encoding.UTF8.GetBytes(frame);
        try { await _sendLock.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
        try
        {
            if (_ws.State != WebSocketState.Open) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            _ = CloseAsync(1006, "send failed");
            return false;
        }
        finally { _sendLock.Release(); }
    }

    /// <summary>Client side: say hello first.</summary>
    internal Task<bool> SendHelloAsync() => SendRawAsync("peerHello", _own.ToWire(), CancellationToken.None, requireHello: false);

    /// <summary>Receives until the socket closes. <paramref name="firstFrameTimeout"/> applies to the server side.</summary>
    internal async Task RunAsync(TimeSpan firstFrameTimeout, TimeSpan helloTimeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        // Server: a first frame we can decrypt must arrive within 5 s (else 4003). Both: hello within helloTimeout.
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(IsClient ? helloTimeout : firstFrameTimeout, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (!IsClient && !_anyDecrypted) await CloseAsync(CloseDecryptFailed, "Space key mismatch").ConfigureAwait(false);
            else if (!_helloDone) await CloseAsync(1008, "No hello").ConfigureAwait(false);
        }, CancellationToken.None);

        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open && !linked.IsCancellationRequested)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, linked.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        CloseCode ??= (int?)result.CloseStatus;
                        try { await _ws.CloseOutputAsync(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false); } catch { }
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > 96 * 1024 * 1024) throw new IOException("Message too large");
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    Touch();
                    continue;
                }
                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                var plain = LinkCrypto.TryDecrypt(text, _key);
                if (plain is null)
                {
                    if (!IsClient && !_anyDecrypted)
                    {
                        await CloseAsync(CloseDecryptFailed, "Space key mismatch").ConfigureAwait(false);
                        return;
                    }
                    continue; // like the Mac: drop silently, don't refresh activity
                }
                _anyDecrypted = true;
                Touch();
                await HandleAsync(plain).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception ex) { Log?.Invoke($"Peer {PeerName}: {ex.Message}"); }
        finally
        {
            await CloseAsync(1000, "").ConfigureAwait(false);
        }
    }

    async Task HandleAsync(string plain)
    {
        using var doc = LenientJson.TryParse(plain);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return;
        var type = doc.RootElement.Str("type");
        if (string.IsNullOrEmpty(type)) return;
        var data = doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
            ? d.Clone()
            : JsonDocument.Parse("{}").RootElement.Clone();

        switch (type)
        {
            case "peerHello":
                var hello = PeerHello.Parse(data);
                if (hello is null) return;
                if (SpaceIds.Same(hello.Id, _own.Id))
                {
                    await CloseAsync(1008, "That's me").ConfigureAwait(false);
                    return;
                }
                if (_helloDone)
                {
                    // A live link re-announcing itself (new IPs or name).
                    if (Hello is not null && !SpaceIds.Same(Hello.Id, hello.Id)) await CloseAsync(1008, "Id changed").ConfigureAwait(false);
                    else Hello = hello;
                    return;
                }
                Hello = hello;
                // Duplicate rule first: a losing link is closed with 4009 and never gets our hello.
                if (Admit?.Invoke(this) == false)
                {
                    await CloseAsync(CloseDuplicate, "duplicate").ConfigureAwait(false);
                    return;
                }
                if (!IsClient && !await SendHelloAsync().ConfigureAwait(false)) return;
                _helloDone = true;
                HelloCompleted?.Invoke(this);
                return;
            case "ping":
                if (_helloDone) _ = SendAsync("pong");
                return;
            case "pong":
                return;
        }

        if (!_helloDone) return; // nothing else before hello

        if (type == "peerActivity")
        {
            PeerInUse = data.Bool("inUse") ?? false;
            PeerIdleSeconds = data.Int("idleSeconds") ?? -1;
            PeerActivityAt = DateTime.UtcNow;
            ActivityChanged?.Invoke(this);
        }

        var handler = MessageReceived;
        if (handler is null) return;
        var args = new LinkMessageEventArgs(type, data);
        foreach (EventHandler<LinkMessageEventArgs> h in handler.GetInvocationList())
        {
            try { h(this, args); }
            catch (Exception ex) { Log?.Invoke($"Peer handler for {type} threw: {ex.Message}"); }
        }
    }

    void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    /// <summary>Closes with a WebSocket close code (4009 duplicate, 4003 key mismatch…). Idempotent.</summary>
    public async Task CloseAsync(int code, string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        CloseCode ??= code;
        var wasOpen = _helloDone;
        try
        {
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var t = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _ws.CloseOutputAsync((WebSocketCloseStatus)code, reason, t.Token).ConfigureAwait(false);
            }
        }
        catch { }
        _cts.Cancel();
        try { _ws.Abort(); } catch { }
        try { _owner?.Dispose(); } catch { }
        if (wasOpen)
        {
            try { ConnectionChanged?.Invoke(this, false); } catch { }
        }
        try { Closed?.Invoke(this); } catch { }
    }

    public void Dispose() => _ = CloseAsync(1001, "Going away");

    public override string ToString() => $"{(IsClient ? "→" : "←")} {PeerName} ({RemoteIp})";
}
