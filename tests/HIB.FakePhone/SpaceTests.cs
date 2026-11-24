using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HIB.Core.Link;
using HIB.Core.Phone;
using HIB.Core.Space;
using SkiaSharp;

namespace HIB.FakePhone;

/// <summary>A controllable activity source.</summary>
sealed class FakeActivity
{
    public volatile bool InUse;
    public int Idle = 3;
    public ActivitySample Sample() => new(InUse, InUse ? Idle : 300);
}

sealed class FakeAudio : IPhoneAudioPlayer
{
    public bool IsActive { get; private set; }
    public bool IsStarting { get; private set; }
    public bool CanStart => true;
    public string? LastError => null;
    public int Starts, Stops;
    public void Start() { Starts++; IsStarting = true; _ = Task.Delay(150).ContinueWith(_ => { IsStarting = false; IsActive = true; StateChanged?.Invoke(); }); }
    public void Stop() { Stops++; IsActive = false; IsStarting = false; StateChanged?.Invoke(); }
    public event Action? StateChanged;
}

/// <summary>
/// H.I.B Space (docs/SPACE.md): the extended desktopsList, the target rule, the UTF-8 tie-break, the direct peer
/// link between two in-process computers (hello, files, text, duplicates, wrong key), activity and Flick to the phone.
/// </summary>
public class SpaceTests
{
    static readonly byte[] SpaceKey = Enumerable.Range(1, 32).Select(i => (byte)(i * 7)).ToArray();

    static SpaceOptions FastSpace(FakeActivity? activity = null) => new()
    {
        ActivitySampler = (activity ?? new FakeActivity()).Sample,
        ActivityCheck = TimeSpan.FromMilliseconds(100),
        ActivityHeartbeat = TimeSpan.FromSeconds(30),
        Peer = new PeerNetworkOptions
        {
            DialTick = TimeSpan.FromMilliseconds(100),
            Backoff = new[] { TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(600) },
            PingInterval = TimeSpan.FromMilliseconds(500),
            DropAfter = TimeSpan.FromSeconds(5),
            FirstFrameTimeout = TimeSpan.FromSeconds(2),
            HelloTimeout = TimeSpan.FromSeconds(3),
            ConnectTimeout = TimeSpan.FromSeconds(2),
        },
    };

    static object DesktopsList(string you, string? inUse, string? playingOn, byte[]? key, params object[] desktops) => new Dictionary<string, object?>
    {
        ["you"] = you,
        ["phone"] = new { id = "phone", name = "Haitham's S26", model = "SM-S948B", battery = 80, charging = false },
        ["space"] = key is null ? null : new { key = Convert.ToBase64String(key), v = 1 },
        ["inUse"] = inUse,
        ["playingOn"] = playingOn,
        ["desktops"] = desktops,
    };

    static object Desk(string id, string platform, bool connected, int port = 0, bool inUse = false, params string[] ips)
        => new { id, name = id, platform, type = platform == "windows" ? "Windows PC" : "MacBook Pro", connected, inUse, idleSeconds = inUse ? 4 : 400, ips, port };

    internal static string MakePng(string dir, string name, int w = 1600, int h = 1000)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp))
        {
            using var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, h), new[] { SKColors.DeepSkyBlue, SKColors.Navy }, SKShaderTileMode.Clamp) };
            c.DrawRect(0, 0, w, h, paint);
            // noise so the PNG isn't trivially small
            var rnd = new Random(7);
            for (var i = 0; i < 4000; i++) c.DrawPoint(rnd.Next(w), rnd.Next(h), new SKColor((uint)rnd.Next() | 0xFF000000));
        }
        var path = Path.Combine(dir, name);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    // ---------------- directory parsing + target rule ----------------

    [Fact]
    public async Task Extended_desktops_list_builds_the_device_list()
    {
        await using var host = new TestHost(name: "Office PC", space: FastSpace());
        await using var phone = await host.ConnectPhoneAsync();
        await phone.SendAsync("status", new { battery = new { level = 64, isCharging = true } });
        await phone.SendAsync("desktopsList", DesktopsList("Office PC", "Haitham’s MacBook", "Haitham’s MacBook", SpaceKey,
            Desk("Haitham’s MacBook", "mac", true, 6996, true, "10.107.0.216", "192.168.1.20"),
            Desk("Studio PC", "windows", false, 6996, false, "10.107.0.50"),
            Desk("Office PC", "windows", true, 7000)));

        var space = host.Core.Space;
        Assert.True(await Eventually.TrueAsync(() => space.Devices.Count == 3));
        var devices = space.Devices;
        Assert.Equal(new[] { "phone", "Haitham’s MacBook", "Studio PC" }, devices.Select(d => d.Id).ToArray());

        var p = devices[0];
        Assert.Equal("Fake Pixel", p.Name); // the live device name wins
        Assert.Equal("SM-S948B", p.Model);
        Assert.Equal(SpaceRoute.PhoneLink, p.Route);
        Assert.Equal(64, p.Battery);

        var mac = devices[1];
        Assert.Equal(SpaceDeviceKind.Mac, mac.Kind);
        Assert.Equal("MacBook Pro", mac.Model);
        Assert.True(mac.Online);
        Assert.True(mac.InUse);
        Assert.Equal("In use", mac.StatusText);
        Assert.Equal(SpaceRoute.ViaPhone, mac.Route);
        Assert.Equal("Through phone", mac.RouteText);
        Assert.True(mac.IsPlayingAudio);

        var studio = devices[2];
        Assert.Equal(SpaceDeviceKind.Windows, studio.Kind);
        Assert.False(studio.Online);
        Assert.Equal(SpaceRoute.Unavailable, studio.Route);

        Assert.Equal("Haitham’s MacBook", space.InUseId);
        Assert.Equal("Haitham’s MacBook", space.PlayingOn);
        Assert.True(space.HasSpaceKey);
        var known = host.Core.SpaceEngine.KnownPeers.Single(k => k.Id == "Haitham’s MacBook");
        Assert.Equal(new[] { "10.107.0.216", "192.168.1.20" }, known.Ips);
        Assert.Equal(6996, known.Port);
        Assert.Equal("mac", known.Platform);

        // §1.4: the computer in use is the default target.
        Assert.Equal("Haitham’s MacBook", space.DefaultTarget?.Id);
    }

    [Fact]
    public async Task Target_rule_falls_back_from_in_use_to_last_used_to_the_phone()
    {
        await using var host = new TestHost(name: "Office PC", space: FastSpace());
        await using var phone = await host.ConnectPhoneAsync();
        // This PC is the one in use: never a target of itself → the phone.
        await phone.SendAsync("desktopsList", DesktopsList("Office PC", "Office PC", null, null,
            Desk("Haitham’s MacBook", "mac", true), Desk("Studio PC", "windows", true)));
        var space = host.Core.Space;
        Assert.True(await Eventually.TrueAsync(() => space.Devices.Count == 3));
        Assert.Equal("Office PC", space.InUseId);
        Assert.Equal("phone", space.DefaultTarget?.Id);
        Assert.True(space.ThisDevice.InUse);
        Assert.True(space.ThisDeviceInUse);
        Assert.False(space.Devices.Any(d => d.InUse));

        // Using a computer makes it "the last one used".
        var progress = new List<string>();
        space.SendProgress += (device, _) => { lock (progress) progress.Add(device); };
        var file = Path.Combine(host.Root, "a.txt");
        File.WriteAllText(file, "hello");
        var send = space.SendFilesAsync("Studio PC", new[] { file });
        var init = await phone.WaitForAsync("fileTransferInit");
        Assert.Equal("Studio PC", init.Data.Str("relayTo"));
        var id = init.Data.Str("id")!;
        await phone.WaitForAsync("fileChunk");
        await phone.SendAsync("fileChunkAck", new { id, index = 0 });
        await phone.WaitForAsync("fileTransferComplete");
        await phone.SendAsync("transferVerified", new { id, verified = true });
        Assert.True((await send).Single().Ok);
        lock (progress) Assert.All(progress, d => Assert.Equal("Studio PC", d)); // relayed progress names the computer, not the phone
        Assert.Equal("Studio PC", space.LastTargetId);
        Assert.Equal("Studio PC", space.DefaultTarget?.Id);

        // The Mac becomes the one in use: it wins over "last used".
        await phone.SendAsync("desktopsList", DesktopsList("Office PC", "Haitham’s MacBook", null, null,
            Desk("Haitham’s MacBook", "mac", true, inUse: true), Desk("Studio PC", "windows", true)));
        Assert.True(await Eventually.TrueAsync(() => space.DefaultTarget?.Id == "Haitham’s MacBook"));

        // An offline computer isn't reachable.
        var r = await space.SendFilesAsync("Nowhere PC", new[] { file });
        Assert.False(r.Single().Ok);
        Assert.Contains("isn't reachable", r.Single().Error);
    }

    [Fact]
    public void Ids_compare_by_utf8_bytes_for_the_tie_break()
    {
        Assert.True(SpaceIds.Compare("Haitham’s MacBook", "Office PC") < 0);
        Assert.True(SpaceIds.Compare("Office PC", "Office PC 2") < 0);
        Assert.Equal(0, SpaceIds.Compare("Same", "Same"));
        // UTF-16 puts the surrogate pair (U+1F600) before U+FFFD; UTF-8 bytes put it after.
        Assert.True(string.CompareOrdinal("😀", "�") < 0);
        Assert.True(SpaceIds.Compare("😀", "�") > 0);
        Assert.True(PeerNetwork.KeepsLinkDialledBy("A PC", "B PC"));
        Assert.False(PeerNetwork.KeepsLinkDialledBy("B PC", "A PC"));
        Assert.False(PeerNetwork.KeepsLinkDialledBy("A PC", "A PC")); // same client: the existing link stays
        Assert.True(SpaceIds.Same("Office PC", "office pc"));         // "same computer" ignores case, like the Mac
    }

    [Fact]
    public void Text_kind_detects_links()
    {
        Assert.Equal("link", SpaceIds.TextKind("https://example.com/a?b=c"));
        Assert.Equal("text", SpaceIds.TextKind("www.example.com")); // like the Mac: only a whole http(s) URL
        Assert.Equal("text", SpaceIds.TextKind("meet at 7"));
        Assert.Equal("text", SpaceIds.TextKind("see https://example.com now"));
    }

    [Fact]
    public async Task Space_key_and_peers_survive_a_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "hib-tests", Guid.NewGuid().ToString("N")[..8]);
        await using (var host = new TestHost(name: "Office PC", space: FastSpace(), root: root) { KeepRoot = true })
        {
            await using var phone = await host.ConnectPhoneAsync();
            await phone.SendAsync("desktopsList", DesktopsList("Office PC", null, null, SpaceKey, Desk("Haitham’s MacBook", "mac", true, 6996, false, "10.0.0.9")));
            Assert.True(await Eventually.TrueAsync(() => host.Core.Space.HasSpaceKey));
        }
        await using (var again = new TestHost(name: "Office PC", space: FastSpace(), root: root))
        {
            Assert.True(again.Core.Space.HasSpaceKey);
            var peer = Assert.Single(again.Core.SpaceEngine.KnownPeers);
            Assert.Equal("Haitham’s MacBook", peer.Id);
            // No phone: remembered computers are listed, offline.
            var mac = again.Core.Space.Find("Haitham’s MacBook");
            Assert.NotNull(mac);
            Assert.False(mac!.Online);
            Assert.Equal(SpaceRoute.Unavailable, mac.Route);
            // The key file never holds the base64 text of the key.
            var keyFile = Directory.GetFiles(Path.Combine(root, "data"), "space-key.*").Single();
            Assert.DoesNotContain(Convert.ToBase64String(SpaceKey), File.ReadAllText(keyFile));
        }
    }

    // ---------------- the direct peer link ----------------

    static async Task<(TestHost a, TestHost b)> TwoLinkedComputersAsync(FakeActivity? actA = null, FakeActivity? actB = null)
    {
        var a = new TestHost(name: "A PC", space: FastSpace(actA));
        var b = new TestHost(name: "B Mac", space: FastSpace(actB));
        a.Core.SpaceEngine.SetSpaceKey(SpaceKey);
        b.Core.SpaceEngine.SetSpaceKey(SpaceKey);
        // Both remember each other at once, so both dial: the duplicate rule must leave one link.
        a.Core.SpaceEngine.RememberPeer("B Mac", "B Mac", "mac", new[] { "127.0.0.1" }, b.Core.Link.Port);
        b.Core.SpaceEngine.RememberPeer("A PC", "A PC", "windows", new[] { "127.0.0.1" }, a.Core.Link.Port);
        Assert.True(await Eventually.TrueAsync(() =>
            a.Core.Space.Find("B Mac")?.Route == SpaceRoute.Direct && b.Core.Space.Find("A PC")?.Route == SpaceRoute.Direct, TimeSpan.FromSeconds(10)));
        return (a, b);
    }

    [Fact]
    public async Task Two_computers_link_directly_and_keep_one_link()
    {
        var (a, b) = await TwoLinkedComputersAsync();
        await using var _a = a;
        await using var _b = b;
        await Task.Delay(1500); // let any duplicate settle
        var la = Assert.Single(a.Core.SpaceEngine.PeerLinks);
        var lb = Assert.Single(b.Core.SpaceEngine.PeerLinks);
        // Both keep the link dialled by the lower id ("A PC" < "B Mac").
        Assert.Equal("A PC", la.ClientId);
        Assert.Equal("A PC", lb.ClientId);
        Assert.True(la.IsClient);
        Assert.False(lb.IsClient);
        Assert.Equal("windows", lb.Hello!.Platform);
        Assert.Equal("Windows PC", lb.Hello.Type);
        Assert.Equal(a.Core.Link.Port, lb.Hello.Port);
        var dev = a.Core.Space.Find("B Mac")!;
        Assert.True(dev.Online);
        Assert.Equal("Direct", dev.RouteText);
        Assert.Equal(SpaceDeviceKind.Windows, dev.Kind); // the hello says what it really is
        // The hello taught B where A answers from.
        Assert.Contains("127.0.0.1", b.Core.SpaceEngine.KnownPeers.Single(p => p.Id == "A PC").Ips);
    }

    [Fact]
    public async Task Computers_stay_listed_when_the_phone_drops_and_keep_their_direct_route()
    {
        var (a, b) = await TwoLinkedComputersAsync();
        await using var _a = a;
        await using var _b = b;
        var phone = await a.ConnectPhoneAsync();
        await phone.SendAsync("desktopsList", DesktopsList("A PC", null, null, SpaceKey,
            Desk("B Mac", "mac", true, b.Core.Link.Port, false, "127.0.0.1"), Desk("Studio PC", "windows", true, 6996, false, "10.0.0.7")));
        Assert.True(await Eventually.TrueAsync(() => a.Core.Space.Find("Studio PC")?.Route == SpaceRoute.ViaPhone));
        Assert.NotNull(a.Core.Space.Find("Studio PC")!.LastSeen);

        await phone.DisposeAsync();
        Assert.True(await Eventually.TrueAsync(() => !a.Core.Link.IsConnected));
        var devices = a.Core.Space.Devices;
        // The phone is still listed (offline, last seen just now); computers don't vanish with it.
        var p = devices.Single(d => d.IsPhone);
        Assert.False(p.Online);
        Assert.NotNull(p.LastSeen);
        Assert.Equal(SpaceRoute.Direct, devices.Single(d => d.Id == "B Mac").Route);
        var studio = devices.Single(d => d.Id == "Studio PC");
        Assert.False(studio.Online);
        Assert.Equal(SpaceRoute.Unavailable, studio.Route);
        Assert.True(DateTimeOffset.Now - studio.LastSeen!.Value < TimeSpan.FromMinutes(1));
    }

    /// <summary>Dials <paramref name="port"/>/peer as <paramref name="id"/>; returns (got a peerHello, close code).</summary>
    static async Task<(bool hello, int? close, ClientWebSocket ws)> RawDialAsync(int port, string id, bool waitForClose)
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/peer"), TestContext.Current.CancellationToken);
        var hello = LinkServer.BuildEnvelope("peerHello", new { id, name = id, platform = "mac", type = "MacBook Pro", port = 1, ips = new[] { "127.0.0.1" }, v = 1 });
        await ws.SendAsync(Encoding.UTF8.GetBytes(LinkCrypto.Encrypt(hello, SpaceKey)), WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
        var buf = new byte[64 * 1024];
        var gotHello = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) return (gotHello, (int?)r.CloseStatus, ws);
            var plain = LinkCrypto.TryDecrypt(Encoding.UTF8.GetString(buf, 0, r.Count), SpaceKey);
            if (plain?.Contains("\"peerHello\"") == true)
            {
                gotHello = true;
                if (!waitForClose) return (true, null, ws);
            }
        }
    }

    [Fact]
    public async Task Losing_duplicate_is_closed_with_4009_and_never_gets_a_hello()
    {
        var (a, b) = await TwoLinkedComputersAsync();
        await using var _a = a;
        await using var _b = b;
        var keep = Assert.Single(a.Core.SpaceEngine.PeerLinks);
        Assert.Equal("A PC", keep.ClientId);

        // B (the higher id) dials A again: the link dialled by "A PC" sorts lower and stays.
        var (hello, close, ws) = await RawDialAsync(a.Core.Link.Port, "B Mac", waitForClose: true);
        ws.Dispose();
        Assert.False(hello);
        Assert.Equal(4009, close);
        Assert.Same(keep, Assert.Single(a.Core.SpaceEngine.PeerLinks));

        // A second link from the same client ("A PC" dialling B again): B keeps the existing one.
        (hello, close, ws) = await RawDialAsync(b.Core.Link.Port, "A PC", waitForClose: true);
        ws.Dispose();
        Assert.False(hello);
        Assert.Equal(4009, close);
        Assert.True(Assert.Single(b.Core.SpaceEngine.PeerLinks).IsOpen);
    }

    [Fact]
    public async Task A_lower_client_id_replaces_the_existing_link()
    {
        await using var a = new TestHost(name: "A PC", space: FastSpace());
        await using var b = new TestHost(name: "B Mac", space: FastSpace());
        a.Core.SpaceEngine.SetSpaceKey(SpaceKey);
        b.Core.SpaceEngine.SetSpaceKey(SpaceKey);
        // Only B knows A, so B (the higher id) dials after its polite delay.
        b.Core.SpaceEngine.RememberPeer("A PC", "A PC", "windows", new[] { "127.0.0.1" }, a.Core.Link.Port);
        Assert.True(await Eventually.TrueAsync(() => b.Core.SpaceEngine.PeerLinks.Count == 1, TimeSpan.FromSeconds(8)));
        var old = b.Core.SpaceEngine.PeerLinks[0];
        Assert.Equal("B Mac", old.ClientId);

        // "A PC" dials B: its client id sorts lower, so B answers with a hello and closes the old link with 4009.
        var (hello, _, ws) = await RawDialAsync(b.Core.Link.Port, "A PC", waitForClose: false);
        using var _ws = ws;
        Assert.True(hello);
        Assert.True(await Eventually.TrueAsync(() => old.CloseCode == 4009));
        Assert.Equal("A PC", Assert.Single(b.Core.SpaceEngine.PeerLinks).ClientId);
    }

    [Fact]
    public async Task Peer_endpoint_refuses_a_wrong_key_with_4003_and_without_a_key_at_all()
    {
        await using var host = new TestHost(name: "A PC", space: FastSpace());
        // No space key yet: /peer is refused, /socket still works.
        using (var none = new ClientWebSocket())
        {
            await Assert.ThrowsAnyAsync<WebSocketException>(() => none.ConnectAsync(new Uri($"ws://127.0.0.1:{host.Core.Link.Port}/peer"), TestContext.Current.CancellationToken));
        }
        await using (var phone = await host.ConnectPhoneAsync()) Assert.True(phone.IsOpen);

        host.Core.SpaceEngine.SetSpaceKey(SpaceKey);
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{host.Core.Link.Port}/peer"), TestContext.Current.CancellationToken);
        var wrong = LinkCrypto.Encrypt(LinkServer.BuildEnvelope("peerHello", new { id = "Evil", name = "Evil" }), RandomNumberGenerator.GetBytes(32));
        await ws.SendAsync(Encoding.UTF8.GetBytes(wrong), WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
        var r = await ws.ReceiveAsync(new byte[1024], TestContext.Current.CancellationToken);
        Assert.Equal(WebSocketMessageType.Close, r.MessageType);
        Assert.Equal(4003, (int)r.CloseStatus!.Value);
        Assert.Empty(host.Core.SpaceEngine.PeerLinks);
    }

    [Fact]
    public async Task Files_go_direct_and_land_like_a_relayed_flick()
    {
        var (a, b) = await TwoLinkedComputersAsync();
        await using var _a = a;
        await using var _b = b;
        FlickInfo? flick = null;
        ReceivedFile? received = null;
        b.Core.Space.FlickArrived += f => flick = f;
        b.Core.Files.Received += f => received = f;
        var progress = new List<(string device, SendProgress p)>();
        a.Core.Space.SendProgress += (device, p) => { lock (progress) progress.Add((device, p)); };

        var png = MakePng(a.Root, "holiday.png");
        var results = await a.Core.Space.SendFilesAsync("B Mac", new[] { png });
        Assert.True(results.Single().Ok, results.Single().Error);
        Assert.True(await Eventually.TrueAsync(() => received is not null));
        Assert.Equal("A PC", received!.From);
        Assert.Equal(File.ReadAllBytes(png), File.ReadAllBytes(received.Path));
        Assert.NotNull(flick);
        Assert.Equal("A PC", flick!.From);
        Assert.Equal("image", flick.Kind);
        Assert.Equal(received.FlickId, flick.Id);
        Assert.NotNull(flick.Thumbnail);
        Assert.True(flick.Thumbnail!.Length <= FlickThumbnail.MaxBytes);
        Assert.Equal(0xFF, flick.Thumbnail[0]); // JPEG SOI
        Assert.Equal(0xD8, flick.Thumbnail[1]);
        lock (progress) Assert.Contains(progress, x => x.device == "B Mac" && x.p.Done && !x.p.Failed && x.p.TargetName == "B Mac");
        Assert.Equal("B Mac", a.Core.Space.LastTargetId);
    }

    [Fact]
    public async Task Text_and_links_flick_over_the_peer_link()
    {
        var (a, b) = await TwoLinkedComputersAsync();
        await using var _a = a;
        await using var _b = b;
        var flicks = new List<FlickInfo>();
        b.Core.Space.FlickArrived += f => { lock (flicks) flicks.Add(f); };

        Assert.True(await a.Core.Space.SendTextAsync("B Mac", "Meet at 7"));
        Assert.True(await a.Core.Space.SendTextAsync("B Mac", "https://example.com/hib"));
        Assert.True(await Eventually.TrueAsync(() => { lock (flicks) return flicks.Count == 2; }));
        lock (flicks)
        {
            Assert.Equal("text", flicks[0].Kind);
            Assert.Equal("Meet at 7", flicks[0].Text);
            Assert.Equal("A PC", flicks[0].From);
            Assert.Equal("link", flicks[1].Kind);
            Assert.Equal("https://example.com/hib", flicks[1].Text);
        }
        // Nobody to reach it through: false.
        Assert.False(await a.Core.Space.SendTextAsync("Nowhere", "x"));
    }

    [Fact]
    public async Task Peer_activity_decides_in_use_without_the_phone()
    {
        var actA = new FakeActivity { InUse = false };
        var actB = new FakeActivity { InUse = true, Idle = 2 };
        var (a, b) = await TwoLinkedComputersAsync(actA, actB);
        await using var _a = a;
        await using var _b = b;
        Assert.True(await Eventually.TrueAsync(() => a.Core.Space.Find("B Mac")?.InUse == true));
        Assert.Equal("B Mac", a.Core.Space.InUseId);
        Assert.Equal("B Mac", a.Core.Space.DefaultTarget?.Id);
        // A is touched now: it becomes the one in use (newest activity), and B no longer is.
        actA.InUse = true;
        actA.Idle = 0;
        Assert.True(await Eventually.TrueAsync(() => a.Core.Space.InUseId == "A PC"));
        Assert.True(await Eventually.TrueAsync(() => b.Core.Space.Find("A PC")?.InUse == true));
        Assert.False(a.Core.Space.Find("B Mac")!.InUse);
    }

    [Fact]
    public async Task Link_drop_is_noticed_and_the_dialler_relinks()
    {
        var (a, b) = await TwoLinkedComputersAsync();
        await using var _a = a;
        await using var _b = b;
        var first = Assert.Single(a.Core.SpaceEngine.PeerLinks);
        await first.CloseAsync(1001, "test");
        Assert.True(await Eventually.TrueAsync(() => a.Core.SpaceEngine.PeerLinks.Count == 1 && !ReferenceEquals(a.Core.SpaceEngine.PeerLinks[0], first), TimeSpan.FromSeconds(8)));
        Assert.Equal(SpaceRoute.Direct, b.Core.Space.Find("A PC")!.Route);
    }

    // ---------------- through the phone ----------------

    [Fact]
    public async Task Phone_introduces_two_computers_and_they_link_directly()
    {
        await using var a = new TestHost(name: "A PC", space: FastSpace());
        await using var b = new TestHost(name: "B Mac", space: FastSpace());
        await using var pa = await a.ConnectPhoneAsync();
        await using var pb = await b.ConnectPhoneAsync();
        await pa.SendAsync("desktopsList", DesktopsList("A PC", null, null, SpaceKey, Desk("B Mac", "mac", true, b.Core.Link.Port, false, "127.0.0.1")));
        await pb.SendAsync("desktopsList", DesktopsList("B Mac", null, null, SpaceKey, Desk("A PC", "windows", true, a.Core.Link.Port, false, "127.0.0.1")));
        Assert.True(await Eventually.TrueAsync(() => a.Core.Space.Find("B Mac")?.Route == SpaceRoute.Direct, TimeSpan.FromSeconds(10)));
        Assert.True(await Eventually.TrueAsync(() => b.Core.Space.Find("A PC")?.Route == SpaceRoute.Direct));
        // Clipboard never travels on the peer link (the phone already shares it).
        Assert.DoesNotContain(pa.History, m => m.Type == "clipboardUpdate");
    }

    [Fact]
    public async Task Flick_to_the_phone_sends_flickStart_with_a_small_jpeg_preview_first()
    {
        await using var host = new TestHost(name: "Office PC", space: FastSpace());
        await using var phone = await host.ConnectPhoneAsync();
        var png = MakePng(host.Root, "screenshot.png", 2400, 1500);

        var send = host.Core.Space.SendFilesAsync("phone", new[] { png });
        var start = await phone.WaitForAsync("flickStart");
        var init = await phone.WaitForAsync("fileTransferInit");
        var d = start.Data;
        Assert.Equal("image", d.Str("kind"));
        Assert.Equal("screenshot.png", d.Str("name"));
        Assert.Equal("image/png", d.Str("mime"));
        Assert.Equal(new FileInfo(png).Length, d.Long("size"));
        Assert.Equal("Office PC", d.Str("from"));
        Assert.Equal("windows", d.Str("fromPlatform"));
        var thumb = Convert.FromBase64String(d.Str("thumbnail")!);
        Assert.InRange(thumb.Length, 100, FlickThumbnail.MaxBytes);
        Assert.Equal(0xFF, thumb[0]);
        Assert.Equal(d.Str("id"), init.Data.Str("flickId"));
        Assert.Null(init.Data.Str("relayTo"));

        var id = init.Data.Str("id")!;
        var size = init.Data.Long("size")!.Value;
        for (var i = 0; i * 65536L < size; i++)
        {
            var c = await phone.WaitForAsync("fileChunk");
            await phone.SendAsync("fileChunkAck", new { id, index = c.Data.Int("index") });
        }
        await phone.WaitForAsync("fileTransferComplete");
        await phone.SendAsync("transferVerified", new { id, verified = true });
        Assert.True((await send).Single().Ok);
        Assert.Equal("phone", host.Core.Space.LastTargetId);
    }

    [Fact]
    public async Task Text_and_links_flick_to_the_phone()
    {
        await using var host = new TestHost(name: "Office PC", space: FastSpace());
        await using var phone = await host.ConnectPhoneAsync();
        Assert.True(await host.Core.Space.SendTextAsync("phone", "https://hib.example/x"));
        var m = await phone.WaitForAsync("flickStart");
        Assert.Equal("link", m.Data.Str("kind"));
        Assert.Equal("https://hib.example/x", m.Data.Str("text"));
        Assert.Equal("Office PC", m.Data.Str("from"));
        Assert.True(await host.Core.Space.SendTextAsync("phone", "Copied words"));
        m = await phone.WaitForAsync("flickStart");
        Assert.Equal("text", m.Data.Str("kind"));
    }

    [Fact]
    public async Task Text_for_a_computer_without_a_direct_link_goes_through_the_phone_clipboard()
    {
        await using var host = new TestHost(name: "Office PC", space: FastSpace());
        await using var phone = await host.ConnectPhoneAsync();
        await phone.SendAsync("desktopsList", DesktopsList("Office PC", null, null, null, Desk("Haitham’s MacBook", "mac", true)));
        Assert.True(await Eventually.TrueAsync(() => host.Core.Space.Find("Haitham’s MacBook")?.Route == SpaceRoute.ViaPhone));
        Assert.True(await host.Core.Space.SendTextAsync("Haitham’s MacBook", "the wifi password is hunter2"));
        var m = await phone.WaitForAsync("clipboardUpdate");
        Assert.Equal("the wifi password is hunter2", m.Data.Str("text"));
    }

    [Fact]
    public async Task Desktop_activity_is_sent_after_the_handshake_and_when_it_flips()
    {
        var act = new FakeActivity { InUse = false };
        await using var host = new TestHost(name: "Office PC", space: FastSpace(act));
        await using var phone = await host.ConnectPhoneAsync();
        var first = await phone.WaitForAsync("desktopActivity");
        Assert.False(first.Data.Bool("inUse"));
        Assert.Equal(300, first.Data.Int("idleSeconds"));
        act.InUse = true;
        var flipped = await phone.WaitForAsync("desktopActivity", match: m => m.Data.Bool("inUse") == true);
        Assert.Equal(3, flipped.Data.Int("idleSeconds"));
    }

    [Fact]
    public void Real_activity_sampler_follows_the_spec_off_windows()
    {
        var s = DesktopActivity.Sample();
        if (OperatingSystem.IsWindows()) Assert.True(s.IdleSeconds >= 0);
        else Assert.Equal(new ActivitySample(false, -1), s);
    }

    [Fact]
    public async Task Play_here_starts_the_audio_and_reports_playing_on_this_pc()
    {
        await using var host = new TestHost(name: "Office PC", space: FastSpace());
        await using var phone = await host.ConnectPhoneAsync();
        var space = host.Core.Space;
        Assert.False(await space.PlayHereAsync(true)); // nothing attached yet
        var audio = new FakeAudio();
        host.Core.SpaceEngine.AttachAudio(audio);
        Assert.True(space.CanPlayHere);
        Assert.True(await space.PlayHereAsync(true));
        Assert.Equal(1, audio.Starts);
        Assert.Equal("Office PC", space.PlayingOn);
        Assert.True(space.IsPlayingHere);
        Assert.True(space.ThisDevice.IsPlayingAudio);
        Assert.True(await space.PlayHereAsync(false));
        Assert.Equal(1, audio.Stops);
        Assert.Null(space.PlayingOn);
        // The phone says the Mac plays now.
        await phone.SendAsync("desktopsList", DesktopsList("Office PC", null, "Haitham’s MacBook", null, Desk("Haitham’s MacBook", "mac", true)));
        Assert.True(await Eventually.TrueAsync(() => space.PlayingOn == "Haitham’s MacBook"));
        Assert.False(space.IsPlayingHere);
        Assert.True(space.Find("Haitham’s MacBook")!.IsPlayingAudio);
    }

    // ---------------- Flick bundles (SPACE.md §14) ----------------

    [Fact]
    public async Task Several_files_to_the_phone_go_as_one_bundle_thrown_before_any_file()
    {
        await using var host = new TestHost(name: "Office PC", space: FastSpace());
        await using var phone = await host.ConnectPhoneAsync();
        var files = new[] { MakePng(host.Root, "a.png", 320, 200), MakePng(host.Root, "b.png", 320, 200), MakePng(host.Root, "c.png", 320, 200) };

        var send = host.Core.Space.SendFilesAsync("phone", files);
        var starts = new List<DesktopMessage>();
        for (var i = 0; i < 3; i++) starts.Add(await phone.WaitForAsync("flickStart", match: m => starts.All(s => s.Data.Str("id") != m.Data.Str("id"))));
        var bundles = starts.Select(m => m.Data.Obj("bundle")!.Value).ToList();
        Assert.Single(bundles.Select(b => b.Str("id")).Distinct());
        Assert.Equal(new long?[] { 0, 1, 2 }, bundles.Select(b => b.Long("index")).ToArray());
        Assert.All(bundles, b => Assert.Equal(3, b.Long("count")));
        Assert.Equal(new[] { "a.png", "b.png", "c.png" }, starts.Select(m => m.Data.Str("name")).ToArray());
        Assert.All(starts, m => Assert.NotNull(m.Data.Str("thumbnail")));
        // One throw: every flickStart goes before the first file.
        var history = phone.History.Select(m => m.Type).ToList();
        Assert.True(history.LastIndexOf("flickStart") < history.IndexOf("fileTransferInit") || !history.Contains("fileTransferInit"));

        // Then the files, each with its own flick id.
        for (var n = 0; n < 3; n++)
        {
            var init = await phone.WaitForAsync("fileTransferInit");
            Assert.Equal(starts[n].Data.Str("id"), init.Data.Str("flickId"));
            var id = init.Data.Str("id")!;
            var size = init.Data.Long("size")!.Value;
            for (var i = 0; i * 65536L < size; i++)
            {
                var c = await phone.WaitForAsync("fileChunk");
                await phone.SendAsync("fileChunkAck", new { id, index = c.Data.Int("index") });
            }
            await phone.WaitForAsync("fileTransferComplete");
            await phone.SendAsync("transferVerified", new { id, verified = true });
        }
        Assert.All(await send, r => Assert.True(r.Ok, r.Error));

        // A single file is still a plain flick.
        var one = host.Core.Space.SendFilesAsync("phone", new[] { files[0] });
        var single = await phone.WaitForAsync("flickStart", match: m => starts.All(s => s.Data.Str("id") != m.Data.Str("id")));
        Assert.Null(single.Data.Obj("bundle"));
        var last = await phone.WaitForAsync("fileTransferInit");
        var lid = last.Data.Str("id")!;
        await phone.SendAsync("fileChunkAck", new { id = lid, index = (await phone.WaitForAsync("fileChunk")).Data.Int("index") });
        await phone.WaitForAsync("fileTransferComplete");
        await phone.SendAsync("transferVerified", new { id = lid, verified = true });
        Assert.True((await one).Single().Ok);
    }

    [Fact]
    public async Task Several_files_to_a_computer_arrive_as_one_bundle()
    {
        var (a, b) = await TwoLinkedComputersAsync();
        await using var _a = a;
        await using var _b = b;
        var flicks = new List<FlickInfo>();
        var received = new List<ReceivedFile>();
        b.Core.Space.FlickArrived += f => { lock (flicks) flicks.Add(f); };
        b.Core.Files.Received += f => { lock (received) received.Add(f); };

        var files = Enumerable.Range(0, 4).Select(i => MakePng(a.Root, $"shot {i}.png", 400, 300)).ToList();
        files.Add(Path.Combine(a.Root, "missing.png")); // not sent, not counted
        var results = await a.Core.Space.SendFilesAsync("B Mac", files);
        Assert.Equal(5, results.Count);
        Assert.All(results.Take(4), r => Assert.True(r.Ok, r.Error));
        Assert.False(results[4].Ok);
        Assert.True(await Eventually.TrueAsync(() => { lock (received) return received.Count == 4; }));
        lock (flicks)
        {
            Assert.Equal(4, flicks.Count);
            var tags = flicks.Select(f => f.Bundle!).ToList();
            Assert.Single(tags.Select(t => t.Id).Distinct());
            Assert.Equal(new[] { 0, 1, 2, 3 }, tags.Select(t => t.Index).OrderBy(i => i).ToArray());
            Assert.All(tags, t => Assert.Equal(4, t.Count));
            Assert.All(flicks, f => Assert.Equal("A PC", f.From));
        }
        lock (received) Assert.All(received, r => Assert.Contains(flicks, f => f.Id == r.FlickId));
    }
}
