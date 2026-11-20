using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HIB.Core.Link;
using HIB.Core.Space;

namespace HIB.FakePhone;

/// <summary>
/// SPACE.md §3.3 diagnostics: a linked computer sends <c>peerDiag</c> and gets <c>peerDiagLog {version, text}</c>
/// back — only on an authenticated link (after a valid peerHello), never before, and never with a key in it.
/// </summary>
public class PeerDiagTests
{
    static readonly byte[] SpaceKey = Enumerable.Range(1, 32).Select(i => (byte)(i * 11)).ToArray();

    static SpaceOptions Options(Func<string> log) => new()
    {
        ActivitySampler = () => new ActivitySample(false, 300),
        ActivityCheck = TimeSpan.FromMilliseconds(200),
        Peer = new PeerNetworkOptions
        {
            DialTick = TimeSpan.FromMilliseconds(100),
            PingInterval = TimeSpan.FromMilliseconds(500),
            DropAfter = TimeSpan.FromSeconds(10),
            FirstFrameTimeout = TimeSpan.FromSeconds(3),
            HelloTimeout = TimeSpan.FromSeconds(3),
            ConnectTimeout = TimeSpan.FromSeconds(2),
            DiagnosticsLog = log,
        },
    };

    /// <summary>A raw peer: sends encrypted frames and collects every decrypted (type, data) it receives.</summary>
    sealed class RawPeer : IDisposable
    {
        readonly ClientWebSocket _ws = new();
        public readonly ConcurrentQueue<(string Type, JsonElement Data)> Inbox = new();
        public volatile bool Closed;

        public async Task ConnectAsync(int port)
        {
            await _ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/peer"), TestContext.Current.CancellationToken);
            _ = Task.Run(ReceiveLoopAsync);
        }

        async Task ReceiveLoopAsync()
        {
            var buf = new byte[64 * 1024];
            using var msg = new MemoryStream();
            try
            {
                while (_ws.State == WebSocketState.Open)
                {
                    msg.SetLength(0);
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await _ws.ReceiveAsync(buf, CancellationToken.None);
                        if (r.MessageType == WebSocketMessageType.Close) { Closed = true; return; }
                        msg.Write(buf, 0, r.Count);
                    } while (!r.EndOfMessage);
                    var plain = LinkCrypto.TryDecrypt(Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length), SpaceKey);
                    if (plain is null) continue;
                    using var doc = JsonDocument.Parse(plain);
                    var type = doc.RootElement.GetProperty("type").GetString() ?? "";
                    var data = doc.RootElement.TryGetProperty("data", out var d) ? d.Clone() : default;
                    Inbox.Enqueue((type, data));
                }
            }
            catch { }
            finally { Closed = true; }
        }

        public Task SendAsync(string type, object data)
        {
            var frame = LinkCrypto.Encrypt(LinkServer.BuildEnvelope(type, data), SpaceKey);
            return _ws.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
        }

        public bool Saw(string type) => Inbox.Any(m => m.Type == type);

        public void Dispose() => _ws.Dispose();
    }

    [Fact]
    public async Task PeerDiag_is_answered_only_after_a_valid_hello_and_carries_the_scrubbed_log()
    {
        var keyText = Convert.ToBase64String(SpaceKey);
        var log = "2026-11-20 10:00:00.000 [scrcpy] ERROR: Could not create renderer\n"
                + $"2026-11-20 10:00:01.000 [app] desktopsList {{\"space\":{{\"key\":\"{keyText}\"}}}} raw {keyText}\n"
                + "2026-11-20 10:00:02.000 [mirror] last line\n";
        await using var host = new TestHost(name: "Office PC", space: Options(() => log));
        host.Core.SpaceEngine.SetSpaceKey(SpaceKey);

        using var peer = new RawPeer();
        await peer.ConnectAsync(host.Core.Link.Port);

        // Before hello: decryptable, but ignored.
        await peer.SendAsync("peerDiag", new { });
        await peer.SendAsync("peerHello", new { id = "Remote Mac", name = "Remote Mac", platform = "mac", type = "MacBook Pro", port = 1, ips = new[] { "127.0.0.1" }, v = 1 });
        Assert.True(await Eventually.TrueAsync(() => peer.Saw("peerHello")), "no peerHello reply");
        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.False(peer.Saw("peerDiagLog"), "peerDiag was answered before the hello");
        Assert.False(peer.Closed);

        // After hello: answered with the version and the (scrubbed) log.
        await peer.SendAsync("peerDiag", new { });
        Assert.True(await Eventually.TrueAsync(() => peer.Saw("peerDiagLog")), "no peerDiagLog reply");
        var reply = peer.Inbox.First(m => m.Type == "peerDiagLog").Data;
        Assert.Equal(typeof(PeerLink).Assembly.GetName().Version!.ToString(3), reply.GetProperty("version").GetString());
        var text = reply.GetProperty("text").GetString()!;
        Assert.Contains("ERROR: Could not create renderer", text);
        Assert.Contains("[mirror] last line", text);
        Assert.DoesNotContain(keyText, text);
        Assert.Contains("[redacted]", text);
    }

    [Fact]
    public async Task PeerDiag_reply_is_capped_at_256_KB_on_a_line_boundary()
    {
        var line = new string('x', 99) + "\n";
        var big = string.Concat(Enumerable.Repeat(line, 4000)) + "the newest line\n"; // ~400 KB
        await using var host = new TestHost(name: "Office PC", space: Options(() => big));
        host.Core.SpaceEngine.SetSpaceKey(SpaceKey);
        using var peer = new RawPeer();
        await peer.ConnectAsync(host.Core.Link.Port);
        await peer.SendAsync("peerHello", new { id = "Remote Mac", name = "Remote Mac", platform = "mac", type = "MacBook Pro", port = 1, ips = new[] { "127.0.0.1" }, v = 1 });
        Assert.True(await Eventually.TrueAsync(() => peer.Saw("peerHello")));
        await peer.SendAsync("peerDiag", new { });
        Assert.True(await Eventually.TrueAsync(() => peer.Saw("peerDiagLog")));
        var text = peer.Inbox.First(m => m.Type == "peerDiagLog").Data.GetProperty("text").GetString()!;
        Assert.True(Encoding.UTF8.GetByteCount(text) <= PeerLink.DiagnosticsMaxBytes);
        Assert.EndsWith("the newest line\n", text);
        Assert.StartsWith(line, text);
    }
}
