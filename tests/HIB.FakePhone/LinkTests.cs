using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HIB.Core.Link;
using HIB.Core.Pairing;
using HIB.Core.Phone;

namespace HIB.FakePhone;

public class PairingTests
{
    [Fact]
    public void Qr_uses_question_mark_separators_raw_key_and_strict_name_encoding()
    {
        var key = Convert.ToBase64String(LinkCrypto.GenerateKey());
        var qr = QrPayload.Build(new[] { "192.168.1.20", "10.0.0.5" }, 6996, "Haitham’s PC & Co?=x+y", key);

        Assert.StartsWith("airsync://192.168.1.20,10.0.0.5:6996?name=", qr);
        Assert.Contains("?plus=true?key=" + key, qr); // key not url-encoded
        Assert.DoesNotContain("&", qr);
        var namePart = qr.Split("?name=")[1].Split("?plus=")[0];
        Assert.DoesNotContain("+", namePart);
        Assert.DoesNotContain(" ", namePart);
        Assert.Contains("%20", namePart);
        Assert.Contains("%2B", namePart);
        Assert.Contains("%3F", namePart);
        Assert.Contains("%3D", namePart);

        var parsed = QrPayload.Parse(qr);
        Assert.Equal(new[] { "192.168.1.20", "10.0.0.5" }, parsed.Ips);
        Assert.Equal(6996, parsed.Port);
        Assert.Equal("Haitham’s PC & Co?=x+y", parsed.Name);
        Assert.True(parsed.Plus);
        Assert.Equal(key, parsed.Key);
        Assert.Equal(32, Convert.FromBase64String(parsed.Key).Length);
    }

    [Fact]
    public void Settings_persist_key_name_and_id()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hib-tests", Guid.NewGuid().ToString("N"));
        var a = AppSettings.Load(dir);
        var b = AppSettings.Load(dir);
        Assert.Equal(a.EncryptionKey, b.EncryptionKey);
        Assert.Equal(a.DesktopName, b.DesktopName);
        Assert.Equal(a.StableId, b.StableId);
        Assert.Equal(32, a.LinkKeyBytes()!.Length);
        var old = a.EncryptionKey;
        a.ResetKey();
        Assert.NotEqual(old, AppSettings.Load(dir).EncryptionKey);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Crypto_matches_phone_layout()
    {
        var key = LinkCrypto.GenerateKey();
        var frame = LinkCrypto.Encrypt("{\"type\":\"ping\",\"data\":{}}", key);
        var raw = Convert.FromBase64String(frame);
        Assert.Equal(12 + "{\"type\":\"ping\",\"data\":{}}".Length + 16, raw.Length);

        // Decrypt by hand exactly like Java "AES/GCM/NoPadding": nonce = first 12, rest = ct‖tag.
        using var gcm = new AesGcm(key, 16);
        var pt = new byte[raw.Length - 28];
        gcm.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(12, pt.Length), raw.AsSpan(12 + pt.Length), pt);
        Assert.Equal("{\"type\":\"ping\",\"data\":{}}", Encoding.UTF8.GetString(pt));

        Assert.Null(LinkCrypto.TryDecrypt(frame, LinkCrypto.GenerateKey()));
        Assert.Null(LinkCrypto.TryDecrypt("not base64!", key));
    }

    [Fact]
    public void Lenient_json_repairs_unescaped_quotes_in_hand_built_status()
    {
        var bad = "{\"type\":\"status\",\"data\":{\"battery\":{\"level\":82,\"isCharging\":false},\"isPaired\":true,\"music\":{\"isPlaying\":true,\"title\":\"Song \"Live\" (Remix)\",\"artist\":\"A\nB\",\"volume\":60,\"isMuted\":false}}}";
        using var doc = LenientJson.TryParse(bad);
        Assert.NotNull(doc);
        var music = doc!.RootElement.GetProperty("data").GetProperty("music");
        Assert.Equal("Song \"Live\" (Remix)", music.Str("title"));
        Assert.Equal("A\nB", music.Str("artist"));
    }
}

public class LinkTests
{
    [Fact]
    public async Task Handshake_device_gets_macInfo_with_plus_and_same_name()
    {
        await using var host = new TestHost();
        await using var phone = await host.ConnectPhoneAsync(handshake: false);
        var wallpaper = Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF, 1, 2, 3 });
        var ble = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        await phone.SendDeviceAsync(wallpaper, bleToken: ble);
        var info = await phone.WaitForAsync("macInfo", TimeSpan.FromSeconds(10));

        Assert.True(info.Data.GetProperty("isPlusSubscription").GetBoolean());
        Assert.True(info.Data.GetProperty("isPlus").GetBoolean());
        Assert.Equal(phone.Qr.Name, info.Data.GetProperty("name").GetString());
        Assert.True(new Version(info.Data.GetProperty("version").GetString()!) >= new Version(4, 0, 0));
        var type = info.Data.GetProperty("type").GetString();
        Assert.NotEqual("iPhone", type);
        Assert.NotEqual("iPad", type);
        Assert.Equal(JsonValueKind.Array, info.Data.GetProperty("savedAppPackages").ValueKind);

        Assert.True(await Eventually.TrueAsync(() => host.Core.Link.IsConnected));
        var dev = host.Core.Link.Device!;
        Assert.Equal("Fake Pixel", dev.Name);
        Assert.Equal(new[] { "37145", "40211" }, dev.AdbPorts);
        Assert.Equal("192.168.1.20", dev.TargetIpAddress);
        Assert.Equal(ble, dev.BleAuthToken);
        Assert.Equal(6, dev.Wallpaper!.Length);
        Assert.Equal(host.Core.Settings.LinkKeyBytes(), host.Core.Link.GetPairingKey());
    }

    [Fact]
    public async Task Device_with_ip_as_bleToken_and_missing_port_handled()
    {
        await using var host = new TestHost();
        await using var phone = await host.ConnectPhoneAsync(handshake: false);
        await phone.SendDeviceAsync(includePort: false);
        Assert.False(await phone.ReceivesWithinAsync("macInfo", TimeSpan.FromSeconds(1.5)));
        await phone.SendDeviceAsync(bleToken: "192.168.1.20"); // phone bug: IP in the token slot
        await phone.WaitForAsync("macInfo");
        Assert.Null(host.Core.Link.Device!.BleAuthToken);
    }

    [Fact]
    public async Task Pings_every_interval_and_pong_keeps_session_alive()
    {
        await using var host = new TestHost(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(2));
        await using var phone = await host.ConnectPhoneAsync();
        await Task.Delay(3500); // > activity timeout, kept alive by pongs
        Assert.True(phone.IsOpen);
        Assert.True(phone.PingsReceived >= 5, $"pings: {phone.PingsReceived}");
        Assert.True(host.Core.Link.IsConnected);
    }

    [Fact]
    public async Task Silent_phone_is_dropped_after_inactivity_timeout()
    {
        await using var host = new TestHost(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1.5));
        await using var phone = await host.ConnectPhoneAsync();
        phone.AutoPong = false;
        Assert.True(await phone.WaitClosedAsync(TimeSpan.FromSeconds(6)));
        Assert.True(await Eventually.TrueAsync(() => !host.Core.Link.IsConnected));
    }

    [Fact]
    public async Task Wrong_key_frames_are_dropped_without_macInfo()
    {
        await using var host = new TestHost();
        await using var phone = new FakePhone(host.Core.BuildQr());
        phone.Key = LinkCrypto.GenerateKey();
        await phone.ConnectAsync();
        await phone.SendDeviceAsync();
        Assert.False(await phone.ReceivesWithinAsync("macInfo", TimeSpan.FromSeconds(1.5)));
        Assert.False(host.Core.Link.IsConnected);
    }

    [Fact]
    public async Task New_session_with_private_target_takes_over_primary()
    {
        await using var host = new TestHost();
        await using var first = await host.ConnectPhoneAsync();
        await using var second = new FakePhone(host.Core.BuildQr()) { Id = "other", Name = "Other Phone" };
        await second.ConnectAsync();
        await second.SendDeviceAsync(targetIp: "192.168.1.20");
        await second.WaitForAsync("macInfo");
        Assert.True(await first.WaitClosedAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("Other Phone", host.Core.Link.Device!.Name);
        Assert.True(await host.Core.Hub.RingAsync(true));
        await second.WaitForAsync("ringPhone");
    }

    [Fact]
    public async Task UserDisconnected_marks_link_down()
    {
        await using var host = new TestHost();
        await using var phone = await host.ConnectPhoneAsync();
        var changes = new List<bool>();
        host.Core.Link.ConnectionChanged += (_, c) => { lock (changes) changes.Add(c); };
        await phone.SendAsync("userDisconnected");
        Assert.True(await Eventually.TrueAsync(() => !host.Core.Link.IsConnected));
        Assert.Contains(false, changes);
    }

    [Fact]
    public async Task Non_socket_paths_get_404_and_root_200()
    {
        await using var host = new TestHost();
        using var http = new HttpClient();
        var root = await http.GetAsync($"http://127.0.0.1:{host.Core.Link.Port}/");
        Assert.Equal(System.Net.HttpStatusCode.OK, root.StatusCode);
        var other = await http.GetAsync($"http://127.0.0.1:{host.Core.Link.Port}/nope");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, other.StatusCode);
    }
}

public class FeatureTests
{
    [Fact]
    public async Task Notification_list_dismiss_and_actions_round_trip()
    {
        await using var host = new TestHost();
        await using var phone = await host.ConnectPhoneAsync();
        PhoneNotification? posted = null;
        host.Core.Hub.NotificationPosted += n => posted = n;
        await phone.SendPlainAsync("{\"type\":\"notification\",\"data\":{\"id\":\"0|com.whatsapp|1|null|10123\",\"title\":\"Ali\",\"body\":\"hey \\\"you\\\"\",\"app\":\"WhatsApp\",\"package\":\"com.whatsapp\",\"priority\":\"alerting\",\"actions\":[{\"name\":\"Reply\",\"type\":\"reply\"},{\"name\":\"Mark as read\",\"type\":\"button\"}]}}");
        Assert.True(await Eventually.TrueAsync(() => posted is not null));
        Assert.Equal("hey \"you\"", posted!.Body);
        Assert.Equal(2, posted.Actions.Count);
        Assert.True(posted.Actions[0].IsReply);

        await host.Core.Hub.SendNotificationActionAsync(posted.Id, "Reply", "on my way");
        var reply = await phone.WaitForAsync("notificationAction");
        Assert.Equal("on my way", reply.Data.GetProperty("text").GetString());
        Assert.Equal("Reply", reply.Data.GetProperty("name").GetString());

        await host.Core.Hub.SendNotificationActionAsync(posted.Id, "Mark as read");
        var button = await phone.WaitForAsync("notificationAction");
        Assert.False(button.Data.TryGetProperty("text", out _)); // absent, not empty

        await host.Core.Hub.DismissNotificationAsync(posted.Id);
        var dismiss = await phone.WaitForAsync("dismissNotification");
        Assert.Equal(posted.Id, dismiss.Data.GetProperty("id").GetString());
        Assert.Empty(host.Core.Hub.Notifications);

        // Phone-side dismissal removes it on the desktop.
        string? removed = null;
        host.Core.Hub.NotificationRemoved += id => removed = id;
        await phone.SendAsync("notification", new { id = "n2", title = "T", body = "B", app = "A", package = "p" });
        await phone.SendAsync("notificationUpdate", new { id = "n2", dismissed = true, action = "dismiss" });
        Assert.True(await Eventually.TrueAsync(() => removed == "n2"));
    }

    [Fact]
    public async Task Status_updates_battery_and_now_playing_and_media_commands_use_phone_names()
    {
        await using var host = new TestHost();
        await using var phone = await host.ConnectPhoneAsync();
        var art = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        await phone.SendPlainAsync("{\"type\":\"status\",\"data\":{\"battery\":{\"level\":82,\"isCharging\":true},\"isPaired\":true,\"music\":{\"isPlaying\":true,\"title\":\"Song \"Live\"\",\"artist\":\"Artist\",\"volume\":60,\"isMuted\":false,\"albumArt\":\"" + art + "\",\"duration\":215000,\"position\":42000,\"positionTimestamp\":0,\"isBuffering\":false,\"likeStatus\":\"none\"}}}");
        Assert.True(await Eventually.TrueAsync(() => host.Core.Link.Device?.BatteryLevel == 82));
        Assert.True(host.Core.Link.Device!.IsCharging);
        var np = host.Core.Hub.NowPlaying!;
        Assert.Equal("Song \"Live\"", np.Title);
        Assert.Equal(215000, np.DurationMs);
        Assert.Equal(4, np.AlbumArt!.Length);

        await host.Core.Hub.MediaControlAsync("playPause");
        Assert.Equal("playPause", (await phone.WaitForAsync("mediaControl")).Data.GetProperty("action").GetString());
        await host.Core.Hub.VolumeUpAsync();
        var up = await phone.WaitForAsync("volumeControl");
        Assert.Equal("increaseVolume", up.Data.GetProperty("action").GetString());
        Assert.Equal(10, up.Data.GetProperty("increment").GetInt32());
        await host.Core.Hub.VolumeDownAsync();
        Assert.Equal("decreaseVolume", (await phone.WaitForAsync("volumeControl")).Data.GetProperty("action").GetString());
        await host.Core.Hub.ToggleMuteAsync();
        Assert.Equal("toggleMute", (await phone.WaitForAsync("volumeControl")).Data.GetProperty("action").GetString());
        await host.Core.Hub.SetVolumeAsync(35);
        var set = await phone.WaitForAsync("volumeControl");
        Assert.Equal("setVolume", set.Data.GetProperty("action").GetString());
        Assert.Equal(35, set.Data.GetProperty("volume").GetInt32());
    }

    [Fact]
    public async Task Clipboard_syncs_both_ways_without_echo()
    {
        await using var host = new TestHost();
        await using var phone = await host.ConnectPhoneAsync();
        await Task.Delay(500); // baseline poll

        // phone → desktop
        await phone.SendAsync("clipboardUpdate", new { text = "from phone C:\\Users" });
        Assert.True(await Eventually.TrueAsync(() => host.Clipboard.Text == "from phone C:\\Users"));
        Assert.False(await phone.ReceivesWithinAsync("clipboardUpdate", TimeSpan.FromSeconds(1))); // no echo

        // desktop → phone
        host.Clipboard.Text = "from desktop ✓";
        var msg = await phone.WaitForAsync("clipboardUpdate", TimeSpan.FromSeconds(3));
        Assert.Equal("from desktop ✓", msg.Data.GetProperty("text").GetString());
        Assert.False(await phone.ReceivesWithinAsync("clipboardUpdate", TimeSpan.FromSeconds(1))); // sent once

        // image phone → desktop, then desktop image → phone
        var png = RandomNumberGenerator.GetBytes(300);
        await phone.SendAsync("clipboardImageUpdate", new { base64 = Convert.ToBase64String(png), mime = "image/png", name = "HIB-Clipboard.png" });
        Assert.True(await Eventually.TrueAsync(() => host.Clipboard.Image?.SequenceEqual(png) == true));
        Assert.False(await phone.ReceivesWithinAsync("clipboardImageUpdate", TimeSpan.FromSeconds(1)));
        var png2 = RandomNumberGenerator.GetBytes(500);
        host.Clipboard.Image = png2;
        var img = await phone.WaitForAsync("clipboardImageUpdate", TimeSpan.FromSeconds(3));
        Assert.Equal(png2, Convert.FromBase64String(img.Data.GetProperty("base64").GetString()!));
        Assert.Equal("image/png", img.Data.GetProperty("mime").GetString());
    }

    [Fact]
    public async Task File_transfer_acks_every_chunk_and_saves_to_downloads()
    {
        await using var host = new TestHost();
        await using var phone = await host.ConnectPhoneAsync();
        ReceivedFile? received = null;
        host.Core.Files.Received += f => received = f;
        var content = RandomNumberGenerator.GetBytes(150_000);
        const int chunk = 65536;
        var id = Guid.NewGuid().ToString();
        var flickId = id;

        await phone.SendAsync("flickStart", new { id = flickId, kind = "file", name = "report.pdf", mime = "application/pdf", size = content.Length, exitXRatio = 0.4, velocity = 2100.0, angle = -3.5, heading = 271.5, elevation = 4.0 });
        await phone.SendAsync("fileTransferInit", new { id, name = "report.pdf", size = content.Length, mime = "application/pdf", chunkSize = chunk, isClipboard = false, flickId });
        var chunks = (content.Length + chunk - 1) / chunk;
        // Send out of order to check offsets.
        foreach (var i in Enumerable.Range(0, chunks).Reverse())
        {
            var part = content.AsSpan(i * chunk, Math.Min(chunk, content.Length - i * chunk)).ToArray();
            await phone.SendAsync("fileChunk", new { id, index = i, chunk = Convert.ToBase64String(part) });
        }
        var acked = new HashSet<int>();
        for (var i = 0; i < chunks; i++)
            acked.Add((await phone.WaitForAsync("fileChunkAck")).Data.GetProperty("index").GetInt32());
        Assert.Equal(Enumerable.Range(0, chunks).ToHashSet(), acked);

        await phone.SendAsync("fileTransferComplete", new { id, name = "report.pdf", size = content.Length });
        var landed = await phone.WaitForAsync("flickLanded");
        Assert.Equal(flickId, landed.Data.GetProperty("id").GetString());
        Assert.True(await Eventually.TrueAsync(() => received is not null));
        Assert.Equal(Path.Combine(host.Downloads, "report.pdf"), received!.Path);
        Assert.Equal(content, await File.ReadAllBytesAsync(received.Path));

        // Unknown ids are still acked.
        await phone.SendAsync("fileChunk", new { id = "nope", index = 7, chunk = "AAAA" });
        var ack = await phone.WaitForAsync("fileChunkAck");
        Assert.Equal("nope", ack.Data.GetProperty("id").GetString());

        // Same name again → de-duplicated as name(1).ext; checksum verified.
        var id2 = Guid.NewGuid().ToString();
        var small = Encoding.UTF8.GetBytes("hello");
        await phone.SendAsync("fileTransferInit", new { id = id2, name = "report.pdf", size = small.Length, chunkSize = chunk, checksum = Convert.ToHexString(SHA256.HashData(small)).ToLowerInvariant() });
        await phone.SendAsync("fileChunk", new { id = id2, index = 0, chunk = Convert.ToBase64String(small) });
        await phone.SendAsync("fileTransferComplete", new { id = id2, name = "report.pdf", size = small.Length });
        Assert.True(await Eventually.TrueAsync(() => received!.Id == id2));
        Assert.Equal("report(1).pdf", Path.GetFileName(received!.Path));

        // Cancel deletes the partial file.
        string? failed = null;
        host.Core.Files.Failed += (fid, _) => failed = fid;
        await phone.SendAsync("fileTransferInit", new { id = "c1", name = "x.bin", size = 10 });
        await phone.SendAsync("fileTransferCancel", new { id = "c1" });
        Assert.True(await Eventually.TrueAsync(() => failed == "c1"));
        Assert.False(File.Exists(Path.Combine(host.Downloads, "x.bin")));
    }

    [Fact]
    public async Task Flick_link_and_text_land_immediately_and_carry_aim()
    {
        await using var host = new TestHost();
        await using var phone = await host.ConnectPhoneAsync();
        FlickInfo? flick = null;
        host.Core.Hub.FlickStarted += f => flick = f;
        await phone.SendAsync("flickStart", new { id = "f1", kind = "link", name = "", text = "https://example.com", angle = 25.0, heading = 90.0, elevation = 10.0 });
        Assert.Equal("f1", (await phone.WaitForAsync("flickLanded")).Data.GetProperty("id").GetString());
        Assert.Equal("link", flick!.Kind);
        Assert.Equal(90.0, flick.Heading);
        Assert.Equal(25.0, flick.Angle);
    }

    [Fact]
    public async Task Call_event_without_normalizedNumber_and_call_control()
    {
        await using var host = new TestHost();
        await using var phone = await host.ConnectPhoneAsync();
        CallInfo? call = null;
        host.Core.Hub.CallChanged += c => call = c;
        await phone.SendAsync("call_event", new { eventId = "e1", deviceId = "d", timestamp = 1763600000000, direction = "incoming", state = "ringing", number = "+966500000000", simSlot = 0 });
        Assert.True(await Eventually.TrueAsync(() => call is not null));
        Assert.True(call!.IsRinging && call.IsIncoming);
        Assert.Equal("+966500000000", call.DisplayName);
        await host.Core.Hub.CallControlAsync("accept");
        Assert.Equal("accept", (await phone.WaitForAsync("callControl")).Data.GetProperty("action").GetString());
        await phone.SendAsync("call_event", new { eventId = "e1", direction = "incoming", state = "idle", number = "+966500000000" });
        Assert.True(await Eventually.TrueAsync(() => host.Core.Hub.CurrentCall is null));
    }

    [Fact]
    public async Task Ring_phone_and_ring_state()
    {
        await using var host = new TestHost();
        await using var phone = await host.ConnectPhoneAsync();
        await host.Core.Hub.RingAsync(true);
        Assert.Equal("start", (await phone.WaitForAsync("ringPhone")).Data.GetProperty("action").GetString());
        await phone.SendAsync("ringState", new { ringing = false });
        Assert.True(await Eventually.TrueAsync(() => !host.Core.Hub.IsRinging));
        await host.Core.Hub.RingAsync(false);
        Assert.Equal("stop", (await phone.WaitForAsync("ringPhone")).Data.GetProperty("action").GetString());
    }

    [Fact]
    public async Task App_icons_saved_and_reported_in_next_macInfo()
    {
        await using var host = new TestHost();
        await using var phone = await host.ConnectPhoneAsync();
        var icon = "data:image/png;base64," + Convert.ToBase64String(new byte[] { 1, 2, 3 });
        await phone.SendPlainAsync("{\"type\":\"appIcons\",\"data\":{ \"com.whatsapp\": {\n \"name\": \"WhatsApp\",\n \"icon\": \"" + icon + "\",\n \"listening\": true,\n \"systemApp\": false\n}, \"com.android.chrome\": {\"name\":\"Chrome\",\"icon\":\"\",\"listening\":false,\"systemApp\":true} }}");
        Assert.True(await Eventually.TrueAsync(() => host.Core.Hub.Apps.Count == 2));
        Assert.NotNull(host.Core.Hub.IconFor("com.whatsapp"));
        await phone.SendDeviceAsync();
        var info = await phone.WaitForAsync("macInfo");
        var saved = info.Data.GetProperty("savedAppPackages").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "com.android.chrome", "com.whatsapp" }, saved);
    }

    [Fact]
    public async Task Unknown_types_are_ignored_and_link_stays_up()
    {
        await using var host = new TestHost();
        await using var phone = await host.ConnectPhoneAsync();
        await phone.SendAsync("volumeControlResponse", new { action = "x", success = true });
        await phone.SendAsync("somethingFromTheFuture", new { a = 1 });
        await phone.SendPlainAsync("not json at all");
        await host.Core.Hub.RingAsync(true);
        await phone.WaitForAsync("ringPhone");
        Assert.True(host.Core.Link.IsConnected);
    }
}

public class FlickScreenPickerTests
{
    static readonly FlickScreen Left = new("DELL-L", -1920, 0, 1920, 1080);
    static readonly FlickScreen Main = new("BUILTIN", 0, 0, 1920, 1080);
    static readonly FlickScreen Right = new("LG-R", 1920, 0, 2560, 1440);
    static readonly FlickScreen[] Screens = { Main, Left, Right };

    [Fact]
    public void Swipe_angle_picks_neighbour_of_pointer_screen()
    {
        var p = new FlickScreenPicker(null);
        Assert.Equal(Left, p.Pick(Screens, null, null, -25, 100, 100));
        Assert.Equal(Right, p.Pick(Screens, null, null, 18, 100, 100));
        Assert.Equal(Main, p.Pick(Screens, null, null, 10, 100, 100));
        Assert.Equal(Right, p.Pick(Screens, null, null, 0, 2500, 500)); // pointer on the right screen
        Assert.Equal(Right, p.Pick(Screens, null, null, 30, 2500, 500)); // nothing further right
    }

    [Fact]
    public void Teaching_records_aims_then_nearest_aim_wins()
    {
        var store = Path.Combine(Path.GetTempPath(), "hib-tests", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        var p = new FlickScreenPicker(store);
        p.StartTeaching(Screens);
        Assert.Equal("BUILTIN", p.TeachingScreen);
        Assert.Equal(Main, p.Pick(Screens, 0, 0, 0, 0, 0));
        Assert.Equal(Left, p.Pick(Screens, 300, 0, 0, 0, 0));
        Assert.Equal(Right, p.Pick(Screens, 60, 10, 0, 0, 0));
        Assert.False(p.IsTeaching);

        var reloaded = new FlickScreenPicker(store);
        Assert.Equal(3, reloaded.TaughtCount(Screens));
        Assert.Equal(Main, reloaded.Pick(Screens, 350, 5, 0, 100, 100)); // 10° from Main, 50° from Left
        Assert.Equal(Left, reloaded.Pick(Screens, 310, 0, 0, 100, 100));
    }

    [Fact]
    public void Aim_wraps_around_north_and_falls_back_beyond_50_degrees()
    {
        var p = new FlickScreenPicker(null);
        p.StartTeaching(new[] { Main, Left });
        p.Pick(new[] { Main, Left }, 10, 0, 0, 0, 0);   // Main at 10°
        p.Pick(new[] { Main, Left }, 280, 0, 0, 0, 0);  // Left at 280°
        Assert.Equal(Main, p.Pick(new[] { Main, Left }, 355, 0, 0, 100, 100)); // 15° from Main across north
        Assert.Equal(Left, p.Pick(new[] { Main, Left }, 285, 0, 0, 100, 100));
        // 180° is > 50° from both: falls back to swipe direction (left).
        Assert.Equal(Left, p.Pick(new[] { Main, Left }, 180, 0, -30, 100, 100));
        p.ForgetAims();
        Assert.Equal(0, p.TaughtCount(Screens));
    }
}
