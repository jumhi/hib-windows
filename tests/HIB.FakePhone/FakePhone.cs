using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HIB.Core.Link;
using HIB.Core.Pairing;

namespace HIB.FakePhone;

/// <summary>A message the fake phone decrypted from the desktop.</summary>
public sealed record DesktopMessage(string Type, JsonElement Data, string Raw);

/// <summary>
/// Plays the Android H.I.B app against the desktop: parses the QR like the phone, connects to
/// ws://ip:port/socket, encrypts with the pairing key, sends device, answers pings with pong.
/// Uses ClientWebSocket with KeepAliveTimeout so RFC 6455 pings are sent like OkHttp does and
/// the connection fails if the server stops answering them.
/// </summary>
public sealed class FakePhone : IAsyncDisposable
{
    readonly ClientWebSocket _ws = new();
    readonly Channel<DesktopMessage> _inbox = Channel.CreateUnbounded<DesktopMessage>();
    readonly List<DesktopMessage> _history = new();
    readonly SemaphoreSlim _sendLock = new(1, 1);
    readonly CancellationTokenSource _cts = new();
    Task? _receiveTask;

    public FakePhone(string qr)
    {
        Qr = QrPayload.Parse(qr);
        Key = Convert.FromBase64String(Qr.Key);
    }

    public QrPayload.Parsed Qr { get; }
    public byte[] Key { get; set; }
    public bool AutoPong { get; set; } = true;
    public string Name { get; set; } = "Fake Pixel";
    public string Id { get; set; } = "fa4e0000deadbeef";
    public int PingsReceived;
    public bool IsOpen => _ws.State == WebSocketState.Open;
    public IReadOnlyList<DesktopMessage> History { get { lock (_history) return _history.ToList(); } }

    public async Task ConnectAsync(string? host = null, TimeSpan? keepAlive = null)
    {
        _ws.Options.KeepAliveInterval = keepAlive ?? TimeSpan.FromSeconds(1);
        _ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(3); // pings + expects pongs (like OkHttp pingInterval)
        var target = host ?? (Qr.Ips.Contains("127.0.0.1") ? "127.0.0.1" : "127.0.0.1");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _ws.ConnectAsync(new Uri($"ws://{target}:{Qr.Port}/socket"), cts.Token);
        _receiveTask = Task.Run(ReceiveLoopAsync);
    }

    public Task SendDeviceAsync(string? wallpaperB64 = null, string? targetIp = "192.168.1.20", string? bleToken = null, bool includePort = true)
    {
        var data = new Dictionary<string, object?>
        {
            ["id"] = Id,
            ["name"] = Name,
            ["ipAddress"] = "192.168.1.33",
            ["version"] = "4.1.0",
            ["adbPorts"] = new[] { "37145", "40211" },
            ["wallpaper"] = wallpaperB64,
            ["bleAuthToken"] = bleToken,
            ["targetIpAddress"] = targetIp,
        };
        if (includePort) data["port"] = Qr.Port;
        return SendAsync("device", data);
    }

    public Task SendAsync(string type, object? data = null) => SendPlainAsync(LinkServer.BuildEnvelope(type, data));

    /// <summary>Sends raw (possibly hand-built, malformed) JSON, encrypted.</summary>
    public async Task SendPlainAsync(string json)
    {
        var frame = LinkCrypto.Encrypt(json, Key);
        await SendFrameAsync(frame);
    }

    public async Task SendFrameAsync(string frame)
    {
        await _sendLock.WaitAsync();
        try { await _ws.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, _cts.Token); }
        finally { _sendLock.Release(); }
    }

    async Task ReceiveLoopAsync()
    {
        var buf = new byte[64 * 1024];
        using var ms = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buf, _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                if (r.MessageType != WebSocketMessageType.Text) continue;
                var plain = LinkCrypto.TryDecrypt(Encoding.UTF8.GetString(ms.ToArray()), Key);
                if (plain is null) continue; // the real phone would close with 4003 here
                using var doc = JsonDocument.Parse(plain);
                var msg = new DesktopMessage(doc.RootElement.GetProperty("type").GetString()!,
                    doc.RootElement.GetProperty("data").Clone(), plain);
                if (msg.Type == "ping")
                {
                    Interlocked.Increment(ref PingsReceived);
                    if (AutoPong) _ = SendAsync("pong");
                }
                lock (_history) _history.Add(msg);
                await _inbox.Writer.WriteAsync(msg);
            }
        }
        catch { }
        finally { _inbox.Writer.TryComplete(); }
    }

    /// <summary>Waits for the next message of <paramref name="type"/> (skipping others).</summary>
    public async Task<DesktopMessage> WaitForAsync(string type, TimeSpan? timeout = null, Func<DesktopMessage, bool>? match = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var msg = await _inbox.Reader.ReadAsync(cts.Token);
            if (msg.Type == type && (match is null || match(msg))) return msg;
        }
    }

    /// <summary>True if a message of this type arrives within the window.</summary>
    public async Task<bool> ReceivesWithinAsync(string type, TimeSpan window)
    {
        try { await WaitForAsync(type, window); return true; }
        catch (OperationCanceledException) { return false; }
        catch (ChannelClosedException) { return false; }
    }

    /// <summary>Waits until the socket is no longer open.</summary>
    public async Task<bool> WaitClosedAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (_ws.State != WebSocketState.Open) return true;
            if (_receiveTask is { IsCompleted: true }) return true;
            await Task.Delay(100);
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(1000);
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
            }
        }
        catch { }
        _cts.Cancel();
        _ws.Dispose();
    }
}
