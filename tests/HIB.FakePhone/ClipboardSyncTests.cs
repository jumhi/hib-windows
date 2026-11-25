using System.Text.Json;
using HIB.Core.Roam;

namespace HIB.FakePhone;

/// <summary>
/// SPACE.md §16 clipboard that just works, on Windows: every clip has an identity (id, ts, origin), seen ids kill echoes,
/// newest wins, applying is not copying, catch-up on link-up, failed reads and writes are retried, the setting holds on
/// every path, and Roam crossings carry the same identity (findings 2-6 and 8 of the 2026-11-25 clipboard findings).
/// </summary>
public class ClipboardSyncTests
{
    static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    static readonly RoamDisplay Pc = new(@"\\.\DISPLAY1", new RoamRect(0, 0, 1920, 1080), 1, true);

    static async Task<(TestHost Host, FakePhone Phone)> ConnectedAsync(bool counter = false)
    {
        var host = new TestHost();
        host.Clipboard.CountChanges = counter;
        var phone = await host.ConnectPhoneAsync();
        await Task.Delay(400); // the first look (what was there at start) is behind us
        return (host, phone);
    }

    static Task<DesktopMessage> NextClipAsync(FakePhone phone, string text, int seconds = 3)
        => phone.WaitForAsync("clipboardUpdate", TimeSpan.FromSeconds(seconds), m => m.Data.GetProperty("text").GetString() == text);

    // ---------------- §16.1 identity ----------------

    [Fact]
    public async Task A_copy_goes_to_the_phone_with_its_id_time_and_origin()
    {
        var (host, phone) = await ConnectedAsync();
        await using var _h = host;
        await using var _p = phone;
        var before = Now;
        host.Clipboard.Copy("copied on the PC");
        var msg = await NextClipAsync(phone, "copied on the PC");
        var id = msg.Data.GetProperty("id").GetString()!;
        Assert.Equal(22, id.Length); // 16 random bytes, base64url without padding
        Assert.DoesNotContain('=', id);
        var ts = msg.Data.GetProperty("ts").GetInt64();
        Assert.InRange(ts, before - 50, Now + 50);
        Assert.Equal(host.Core.Space.ThisDeviceName, msg.Data.GetProperty("origin").GetString());
        // The log says what happened, never the content (§16.4).
        Assert.Contains(host.Core.LogLines, l => l.Contains($"[clip] copied id={id[..6]} from={host.Core.Space.ThisDeviceName}"));
        Assert.Contains(host.Core.LogLines, l => l.Contains($"[clip] sent id={id[..6]}") && l.Contains("via=phone"));
        Assert.DoesNotContain(host.Core.LogLines, l => l.Contains("copied on the PC"));
    }

    [Fact]
    public async Task An_older_app_without_ids_is_still_understood()
    {
        var (host, phone) = await ConnectedAsync();
        await using var _h = host;
        await using var _p = phone;
        await phone.SendAsync("clipboardUpdate", new { text = "from an old phone" });
        Assert.True(await Eventually.TrueAsync(() => host.Clipboard.Text == "from an old phone"));
        Assert.Contains(host.Core.LogLines, l => l.Contains("[clip] applied id=") && l.Contains("from=phone via=phone"));
        Assert.False(await phone.ReceivesWithinAsync("clipboardUpdate", TimeSpan.FromSeconds(1))); // no echo
    }

    // ---------------- §16.2 seen ids, applying is not copying (finding 3) ----------------

    [Fact]
    public async Task A_clip_seen_once_is_never_applied_or_sent_again()
    {
        var (host, phone) = await ConnectedAsync(counter: true);
        await using var _h = host;
        await using var _p = phone;
        await phone.SendAsync("clipboardUpdate", new { text = "from the Mac", id = "macclip0000000000000001", ts = Now, origin = "A Mac" });
        Assert.True(await Eventually.TrueAsync(() => host.Clipboard.Text == "from the Mac"));
        Assert.False(await phone.ReceivesWithinAsync("clipboardUpdate", TimeSpan.FromSeconds(1))); // applied, not copied

        // The user copies something newer; the same clip relayed again (an echo) must not come back over it.
        host.Clipboard.Copy("newer on the PC");
        await NextClipAsync(phone, "newer on the PC");
        await phone.SendAsync("clipboardUpdate", new { text = "from the Mac", id = "macclip0000000000000001", ts = Now, origin = "A Mac" });
        await Task.Delay(600);
        Assert.Equal("newer on the PC", host.Clipboard.Text);
        Assert.Contains(host.Core.LogLines, l => l.Contains("[clip] ignored(seen) id=maccli"));
    }

    // ---------------- newest wins (finding 2) ----------------

    [Fact]
    public async Task A_late_older_clip_never_overwrites_a_newer_copy()
    {
        var (host, phone) = await ConnectedAsync();
        await using var _h = host;
        await using var _p = phone;
        var stale = Now - 5000; // copied on the Mac 5 s ago, relayed late through the phone
        host.Clipboard.Copy("newer on the PC");
        await NextClipAsync(phone, "newer on the PC");
        await phone.SendAsync("clipboardUpdate", new { text = "stale from the Mac", id = "staleclip0000000000001", ts = stale, origin = "A Mac" });
        await Task.Delay(600);
        Assert.Equal("newer on the PC", host.Clipboard.Text);
        Assert.Contains(host.Core.LogLines, l => l.Contains("[clip] ignored(older) id=stalec from=A Mac via=phone"));

        // Within the 1.5 s skew allowance it is still applied.
        await phone.SendAsync("clipboardUpdate", new { text = "just now on the Mac", id = "freshclip0000000000001", ts = Now - 500, origin = "A Mac" });
        Assert.True(await Eventually.TrueAsync(() => host.Clipboard.Text == "just now on the Mac"));
    }

    [Fact]
    public async Task A_copy_not_polled_yet_still_beats_a_late_clip()
    {
        var host = new TestHost();
        await using var _h = host;
        host.Core.Clipboard!.Dispose(); // no timer: only what an incoming clip makes it look at
        await using var phone = await host.ConnectPhoneAsync();
        await host.Core.Clipboard.PollOnceAsync(); // the start-up look
        host.Clipboard.Copy("copied a moment ago");
        await phone.SendAsync("clipboardUpdate", new { text = "older", id = "olderclip0000000000001", ts = Now - 4000, origin = "A Mac" });
        await Task.Delay(500);
        Assert.Equal("copied a moment ago", host.Clipboard.Text);
        await NextClipAsync(phone, "copied a moment ago"); // and the copy still goes out
    }

    // ---------------- catch-up, no silent baseline (finding 4) ----------------

    [Fact]
    public async Task A_copy_made_while_the_phone_was_away_goes_out_on_link_up()
    {
        var (host, phone) = await ConnectedAsync(counter: true);
        await using var _h = host;
        await phone.DisposeAsync();
        Assert.True(await Eventually.TrueAsync(() => !host.Core.Link.IsConnected));
        host.Clipboard.Copy("copied while away");
        await Task.Delay(500);
        await using var again = await host.ConnectPhoneAsync();
        var msg = await NextClipAsync(again, "copied while away");
        Assert.Equal(host.Core.Space.ThisDeviceName, msg.Data.GetProperty("origin").GetString());
        Assert.Contains(host.Core.LogLines, l => l.Contains("[clip] sent(catch-up) id="));
    }

    [Fact]
    public async Task A_copy_right_after_reconnecting_is_not_swallowed()
    {
        var (host, phone) = await ConnectedAsync();
        await using var _h = host;
        await phone.DisposeAsync();
        Assert.True(await Eventually.TrueAsync(() => !host.Core.Link.IsConnected));
        await using var again = await host.ConnectPhoneAsync();
        host.Clipboard.Copy("copied as it came back");
        await NextClipAsync(again, "copied as it came back");
    }

    [Fact]
    public async Task What_was_on_the_clipboard_at_start_is_not_pushed_and_a_phone_clip_is_not_sent_back_on_link_up()
    {
        var host = new TestHost();
        await using var _h = host;
        host.Clipboard.Copy("there before H.I.B started");
        await using (var phone = await host.ConnectPhoneAsync())
        {
            Assert.False(await phone.ReceivesWithinAsync("clipboardUpdate", TimeSpan.FromSeconds(1)));
            await phone.SendAsync("clipboardUpdate", new { text = "from the phone", id = "phoneclip0000000000001", ts = Now, origin = "phone" });
            Assert.True(await Eventually.TrueAsync(() => host.Clipboard.Text == "from the phone"));
        }
        Assert.True(await Eventually.TrueAsync(() => !host.Core.Link.IsConnected));
        await using var again = await host.ConnectPhoneAsync();
        Assert.False(await again.ReceivesWithinAsync("clipboardUpdate", TimeSpan.FromSeconds(1))); // never back to its origin
    }

    // ---------------- copying the same text again (finding 5) ----------------

    [Fact]
    public async Task Copying_text_the_phone_sent_earlier_is_a_new_clip()
    {
        var (host, phone) = await ConnectedAsync(counter: true);
        await using var _h = host;
        await using var _p = phone;
        await phone.SendAsync("clipboardUpdate", new { text = "same words", id = "phoneclip0000000000002", ts = Now, origin = "phone" });
        Assert.True(await Eventually.TrueAsync(() => host.Clipboard.Text == "same words"));
        await Task.Delay(2200); // past the applied-echo window
        host.Clipboard.Copy("same words"); // the user copies the same text again
        var msg = await NextClipAsync(phone, "same words");
        Assert.NotEqual("phoneclip0000000000002", msg.Data.GetProperty("id").GetString());
    }

    // ---------------- failed reads and writes (finding 6) ----------------

    [Fact]
    public async Task A_read_that_fails_is_retried_and_the_copy_still_goes_out()
    {
        var (host, phone) = await ConnectedAsync(counter: true);
        await using var _h = host;
        await using var _p = phone;
        host.Clipboard.FailReads = 3;
        host.Clipboard.Copy("copied while another app held the clipboard");
        await NextClipAsync(phone, "copied while another app held the clipboard");
        Assert.Equal(0, host.Clipboard.FailReads);
    }

    [Fact]
    public async Task A_read_that_keeps_failing_is_not_marked_handled()
    {
        var (host, phone) = await ConnectedAsync(counter: true);
        await using var _h = host;
        await using var _p = phone;
        host.Clipboard.FailReads = 12; // more than one look's retries
        host.Clipboard.Copy("held for a while");
        await NextClipAsync(phone, "held for a while", seconds: 6);
        Assert.Contains(host.Core.LogLines, l => l.Contains("[clip] read failed"));
    }

    [Fact]
    public async Task A_write_that_fails_is_retried()
    {
        var (host, phone) = await ConnectedAsync();
        await using var _h = host;
        await using var _p = phone;
        host.Clipboard.FailWrites = 2;
        await phone.SendAsync("clipboardUpdate", new { text = "write me", id = "writeclip0000000000001", ts = Now, origin = "phone" });
        Assert.True(await Eventually.TrueAsync(() => host.Clipboard.Text == "write me"));
    }

    // ---------------- the setting holds both ways, on every path ----------------

    [Fact]
    public async Task Sync_off_neither_sends_nor_applies_on_any_path()
    {
        var pc = new FakeRoamPlatform();
        await using var host = new TestHost(name: "Office PC", space: RoamTests.FastSpace(), roam: new RoamOptions { Platform = () => pc });
        host.Core.SpaceEngine.SetSpaceKey(RoamTests.SpaceKey);
        host.Core.Roam.SetDisplays(new[] { Pc });
        await using var phone = await host.ConnectPhoneAsync();
        await using var mac = await RawMac.DialAsync(host.Core.Link.Port, RoamTests.SpaceKey);
        await mac.SendRawAsync(RoamUiTests.MacHello);
        Assert.True(await Eventually.TrueAsync(() => host.Core.Roam.StatusFor("A Mac") is { Ready: true }));
        host.Core.Settings.ClipboardSync = false;
        host.Clipboard.Copy("mine");

        await phone.SendAsync("clipboardUpdate", new { text = "phone clip", id = "offclip00000000000001", ts = Now, origin = "phone" });
        await mac.SendRawAsync("""{"type":"ctlEnter","data":{"x":0,"y":420,"clipboard":{"text":"mac clip","id":"offclip00000000000002","ts":TS,"origin":"A Mac"}}}""".Replace("TS", Now.ToString()));
        Assert.True(await Eventually.TrueAsync(() => host.Core.Roam.ControlledById == "A Mac"));
        await mac.SendRawAsync("""{"type":"ctlLeave","data":{"clipboard":null}}""");
        Assert.True(await Eventually.TrueAsync(() => host.Core.Roam.ControlledById is null));
        await Task.Delay(500);
        Assert.Equal("mine", host.Clipboard.Text); // nothing applied
        Assert.False(await phone.ReceivesWithinAsync("clipboardUpdate", TimeSpan.FromMilliseconds(500))); // nothing sent

        pc.Raise(RoamInput.Pointer(1925, 540, false)); // cross to the Mac: no clipboard goes along
        Assert.True(await Eventually.TrueAsync(() => mac.Of("ctlEnter").Count == 1));
        Assert.Equal(JsonValueKind.Null, mac.Of("ctlEnter")[0].GetProperty("clipboard").ValueKind);
        Assert.Contains(host.Core.LogLines, l => l.Contains("[clip] ignored(off)") && l.Contains("via=phone"));
        Assert.Contains(host.Core.LogLines, l => l.Contains("[clip] ignored(off)") && l.Contains("via=roam"));
    }

    // ---------------- Roam (§9.4, finding 8, finding 3 for Roam) ----------------

    static async Task<(TestHost Host, FakeRoamPlatform Pc, RawMac Mac)> WithMacAsync(TimeSpan? readDelay = null)
    {
        var pc = new FakeRoamPlatform();
        var host = new TestHost(name: "Office PC", space: RoamTests.FastSpace(), roam: new RoamOptions { Platform = () => pc });
        host.Clipboard.CountChanges = true;
        host.Core.SpaceEngine.SetSpaceKey(RoamTests.SpaceKey);
        host.Core.Roam.SetDisplays(new[] { Pc });
        var mac = await RawMac.DialAsync(host.Core.Link.Port, RoamTests.SpaceKey);
        await mac.SendRawAsync(RoamUiTests.MacHello);
        Assert.True(await Eventually.TrueAsync(() => host.Core.Roam.StatusFor("A Mac") is { Ready: true }));
        await Task.Delay(300);
        return (host, pc, mac);
    }

    [Fact]
    public async Task A_clip_the_mac_brought_across_is_not_echoed_to_the_phone()
    {
        var (host, pc, mac) = await WithMacAsync();
        await using var _h = host;
        await using var _m = mac;
        await using var phone = await host.ConnectPhoneAsync();
        await Task.Delay(400);
        await mac.SendRawAsync("""{"type":"ctlEnter","data":{"x":0,"y":420,"clipboard":{"text":"from the Mac","id":"roamclip0000000000001","ts":TS,"origin":"A Mac"}}}""".Replace("TS", Now.ToString()));
        Assert.True(await Eventually.TrueAsync(() => host.Clipboard.Text == "from the Mac"));
        Assert.False(await phone.ReceivesWithinAsync("clipboardUpdate", TimeSpan.FromSeconds(1))); // the Mac sends its own copy
        Assert.Contains(host.Core.LogLines, l => l.Contains("[clip] applied id=roamcl from=A Mac via=roam"));

        // The same clip relayed by the phone later is an echo.
        await mac.SendRawAsync("""{"type":"ctlLeave","data":{"clipboard":null}}""");
        host.Clipboard.Copy("newer on the PC");
        await NextClipAsync(phone, "newer on the PC");
        await phone.SendAsync("clipboardUpdate", new { text = "from the Mac", id = "roamclip0000000000001", ts = Now, origin = "A Mac" });
        await Task.Delay(500);
        Assert.Equal("newer on the PC", host.Clipboard.Text);
    }

    [Fact]
    public async Task A_crossing_carries_the_clip_with_its_identity_and_an_older_one_coming_back_loses()
    {
        var (host, pc, mac) = await WithMacAsync();
        await using var _h = host;
        await using var _m = mac;
        host.Clipboard.Copy("copied on the PC");
        pc.Raise(RoamInput.Pointer(1925, 540, false));
        Assert.True(await Eventually.TrueAsync(() => mac.Of("ctlEnter").Count == 1));
        var clip = mac.Of("ctlEnter")[0].GetProperty("clipboard");
        Assert.Equal("copied on the PC", clip.GetProperty("text").GetString());
        Assert.Equal(22, clip.GetProperty("id").GetString()!.Length);
        Assert.Equal("Office PC", clip.GetProperty("origin").GetString());
        Assert.InRange(clip.GetProperty("ts").GetInt64(), Now - 5000, Now);

        // Someone uses the Mac's own mouse: the PC's cursor comes home. Then the Mac drives the PC, carrying an older clip
        // (copied there before ours): ours stays.
        await mac.SendRawAsync("""{"type":"ctlRelease","data":{}}""");
        Assert.True(await Eventually.TrueAsync(() => host.Core.Roam.Mode == RoamMode.Home));
        await mac.SendRawAsync("""{"type":"ctlEnter","data":{"x":0,"y":420,"clipboard":{"text":"older on the Mac","id":"oldmac000000000000001","ts":TS,"origin":"A Mac"}}}""".Replace("TS", (Now - 60_000).ToString()));
        Assert.True(await Eventually.TrueAsync(() => host.Core.Roam.ControlledById == "A Mac"));
        await Task.Delay(300);
        Assert.Equal("copied on the PC", host.Clipboard.Text);
    }

    [Fact]
    public async Task A_clip_the_other_computer_did_not_take_goes_again_next_crossing()
    {
        var (host, pc, mac) = await WithMacAsync();
        await using var _h = host;
        await using var _m = mac;
        host.Clipboard.Copy("carry me");
        pc.Raise(RoamInput.Pointer(1925, 540, false));
        Assert.True(await Eventually.TrueAsync(() => mac.Of("ctlEnter").Count == 1));
        Assert.True(mac.Of("ctlEnter")[0].GetProperty("clipboard").ValueKind == JsonValueKind.Object, string.Join("\n", host.Core.LogLines));
        Assert.Equal("carry me", mac.Of("ctlEnter")[0].GetProperty("clipboard").GetProperty("text").GetString());
        // The Mac is busy and answers ctlRelease: it never applied the clip.
        await mac.SendRawAsync("""{"type":"ctlRelease","data":{}}""");
        Assert.True(await Eventually.TrueAsync(() => host.Core.Roam.Mode == RoamMode.Home));
        await Task.Delay(300);
        pc.Raise(RoamInput.Pointer(1925, 540, false));
        Assert.True(await Eventually.TrueAsync(() => mac.Of("ctlEnter").Count == 2));
        Assert.Equal("carry me", mac.Of("ctlEnter")[1].GetProperty("clipboard").GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_slow_clipboard_read_still_rides_the_crossing()
    {
        var (host, pc, mac) = await WithMacAsync();
        await using var _h = host;
        await using var _m = mac;
        host.Core.Clipboard!.Dispose(); // only the crossing reads
        host.Clipboard.ReadDelay = TimeSpan.FromMilliseconds(400);
        host.Clipboard.Copy("read slowly");
        pc.Raise(RoamInput.Pointer(1925, 540, false));
        Assert.True(await Eventually.TrueAsync(() => mac.Of("ctlEnter").Count == 1));
        Assert.Equal("read slowly", mac.Of("ctlEnter")[0].GetProperty("clipboard").GetProperty("text").GetString());
    }
}
