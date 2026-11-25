using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HIB.Core.Link;
using HIB.Core.Phone;
using HIB.Core.Remote;
using SkiaSharp;

namespace HIB.FakePhone;

// =====================================================================================================================
// Pure logic (SPACE.md §15): the same rules the Mac app has, tested without a screen.
// =====================================================================================================================

public class PhoneRemoteLogicTests
{
    static readonly DateTime T0 = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
    static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    // ---------------- framing and crypto ----------------

    [Fact]
    public void Header_is_22_little_endian_bytes_in_the_contract_order()
    {
        var h = new ScreenFrameHeader(ScreenFrameKind.Keyframe, 0x01020304, 0x0A0B0C0D0E0F1011, 0.25f, -0.5f, true);
        var buf = new byte[ScreenFrameHeader.Size];
        h.Write(buf);
        Assert.Equal(22, ScreenFrameHeader.Size);
        Assert.Equal(2, buf[0]);
        Assert.Equal(new byte[] { 4, 3, 2, 1 }, buf[1..5]);
        Assert.Equal(new byte[] { 0x11, 0x10, 0x0F, 0x0E, 0x0D, 0x0C, 0x0B, 0x0A }, buf[5..13]);
        Assert.Equal(BitConverter.GetBytes(0.25f), buf[13..17]);
        Assert.Equal(BitConverter.GetBytes(-0.5f), buf[17..21]);
        Assert.Equal(1, buf[21]);
        Assert.True(ScreenFrameHeader.TryRead(buf, out var back));
        Assert.Equal(h, back);
        Assert.False(ScreenFrameHeader.TryRead(buf[..21], out _));
        buf[0] = 9;
        Assert.False(ScreenFrameHeader.TryRead(buf, out _));
    }

    [Fact]
    public void Header_carries_NaN_and_off_view_cursor_values()
    {
        var h = new ScreenFrameHeader(ScreenFrameKind.Frame, 7, 1, float.NaN, 1.7f, false);
        var buf = new byte[22];
        h.Write(buf);
        Assert.True(ScreenFrameHeader.TryRead(buf, out var back));
        Assert.True(float.IsNaN(back.CursorX));
        Assert.Equal(1.7f, back.CursorY);
        Assert.False(back.CursorVisible);
    }

    [Fact]
    public void Binary_frames_are_raw_nonce_ciphertext_tag_and_round_trip()
    {
        var payload = new byte[] { 0, 0, 0, 1, 0x65, 0xAA, 0xBB };
        var h = new ScreenFrameHeader(ScreenFrameKind.Keyframe, 3, 99, 0.5f, 0.5f, true);
        var sealedFrame = ScreenFraming.Seal(h, payload, Key);
        Assert.Equal(12 + 22 + payload.Length + 16, sealedFrame.Length); // nonce12 ‖ ct ‖ tag16, no base64
        Assert.True(ScreenFraming.TryOpen(sealedFrame, Key, out var back, out var body));
        Assert.Equal(h, back);
        Assert.Equal(payload, body);
        var wrong = (byte[])Key.Clone();
        wrong[0] ^= 1;
        Assert.False(ScreenFraming.TryOpen(sealedFrame, wrong, out _, out _));
        sealedFrame[20] ^= 1; // tampered
        Assert.False(ScreenFraming.TryOpen(sealedFrame, Key, out _, out _));
    }

    [Fact]
    public void Text_and_binary_encryption_are_the_same_gcm_output_one_base64_one_raw()
    {
        var plain = Encoding.UTF8.GetBytes("{\"type\":\"screenPong\",\"data\":{\"t\":5}}");
        var text = LinkCrypto.Encrypt(plain, Key);
        var raw = Convert.FromBase64String(text);
        Assert.Equal(plain, LinkCrypto.TryDecryptBytes(raw, Key));
        var binary = LinkCrypto.EncryptBytes(plain, Key);
        Assert.Equal(Encoding.UTF8.GetString(plain), LinkCrypto.TryDecrypt(Convert.ToBase64String(binary), Key));
        Assert.Null(LinkCrypto.TryDecryptBytes(new byte[10], Key));
    }

    [Fact]
    public void Close_codes_match_the_contract()
    {
        Assert.Equal(4003, RemoteCloseCodes.BadKey);
        Assert.Equal(4010, RemoteCloseCodes.NotPermitted);
        Assert.Equal(4012, RemoteCloseCodes.Stopped);
        Assert.Equal(4013, RemoteCloseCodes.Busy);
    }

    // ---------------- admission, stop, pings, keyframes ----------------

    [Fact]
    public void Admission_one_session_per_phone_others_get_busy()
    {
        Assert.Equal(ScreenAdmission.Accept, RemoteAdmission.Decide(true, false, null, "10.0.0.5"));
        Assert.Equal(ScreenAdmission.ReplaceSamePhone, RemoteAdmission.Decide(true, false, "10.0.0.5", "10.0.0.5"));
        Assert.Equal(ScreenAdmission.RefuseBusy, RemoteAdmission.Decide(true, false, "10.0.0.5", "10.0.0.9"));
        Assert.Equal(ScreenAdmission.RefuseOff, RemoteAdmission.Decide(false, false, null, "10.0.0.5"));
        Assert.Equal(ScreenAdmission.RefuseStopped, RemoteAdmission.Decide(true, true, null, "10.0.0.5"));
        Assert.Equal(4013, RemoteAdmission.CloseCode(ScreenAdmission.RefuseBusy));
        Assert.Equal(4012, RemoteAdmission.CloseCode(ScreenAdmission.RefuseOff));
        Assert.Equal(4012, RemoteAdmission.CloseCode(ScreenAdmission.RefuseStopped));
    }

    [Fact]
    public void Stop_refuses_new_sessions_for_60_seconds()
    {
        var gate = new RemoteStopGate();
        Assert.False(gate.IsRefusing(T0));
        gate.Stop(T0);
        Assert.True(gate.IsRefusing(T0.AddSeconds(59.9)));
        Assert.False(gate.IsRefusing(T0.AddSeconds(60)));
    }

    [Fact]
    public void Five_seconds_without_screenPing_ends_the_session()
    {
        var ping = new PingWatch(T0);
        Assert.False(ping.IsSilent(T0.AddSeconds(4.9)));
        ping.Seen(T0.AddSeconds(4));
        Assert.False(ping.IsSilent(T0.AddSeconds(8.9)));
        Assert.True(ping.IsSilent(T0.AddSeconds(9)));
    }

    [Fact]
    public void Keyframe_every_two_seconds()
    {
        var k = new KeyframeClock(T0);
        Assert.False(k.Due(T0.AddSeconds(1.9)));
        Assert.True(k.Due(T0.AddSeconds(2)));
        k.Emitted(T0.AddSeconds(2));
        Assert.False(k.Due(T0.AddSeconds(3)));
    }

    // ---------------- adaptive ladder ----------------

    [Fact]
    public void Ladder_starts_at_8_Mbps_60_fps_on_lan_and_3_Mbps_30_fps_on_tailscale()
    {
        var lan = new AdaptiveLadder(false, 60, T0);
        Assert.Equal((8_000_000, 60), (lan.Bitrate, lan.Fps));
        var ts = new AdaptiveLadder(true, 60, T0);
        Assert.Equal((3_000_000, 30), (ts.Bitrate, ts.Fps));
        var asked30 = new AdaptiveLadder(false, 30, T0);
        Assert.Equal(30, asked30.Fps);
        var jpeg = new AdaptiveLadder(false, 60, T0, jpeg: true);
        Assert.Equal(10, jpeg.Fps);
    }

    [Fact]
    public void Ladder_steps_bitrate_by_0_7_to_the_1_Mbps_floor_then_fps_60_30_15()
    {
        var l = new AdaptiveLadder(false, 60, T0);
        var t = T0;
        var seen = new List<(int, int)>();
        for (var i = 0; i < 12; i++)
        {
            t = t.AddSeconds(1);
            if (l.OnDrop(t)) seen.Add((l.Bitrate, l.Fps));
        }
        Assert.Equal(new[]
        {
            (5_600_000, 60), (3_920_000, 60), (2_744_000, 60), (1_920_800, 60), (1_344_560, 60), (1_000_000, 60),
            (1_000_000, 30), (1_000_000, 15),
        }, seen);
        Assert.False(l.OnDrop(t.AddSeconds(1))); // nothing lower
    }

    [Fact]
    public void Ladder_takes_one_step_per_burst_of_drops()
    {
        var l = new AdaptiveLadder(false, 60, T0);
        Assert.True(l.OnDrop(T0.AddSeconds(1)));
        Assert.False(l.OnDrop(T0.AddSeconds(1.1)));
        Assert.False(l.OnDrop(T0.AddSeconds(1.4)));
        Assert.Equal(5_600_000, l.Bitrate);
        Assert.True(l.OnDrop(T0.AddSeconds(1.5)));
    }

    [Fact]
    public void Ladder_steps_back_up_after_five_clean_seconds_fps_first()
    {
        var l = new AdaptiveLadder(false, 60, T0);
        var t = T0;
        for (var i = 0; i < 7; i++) { t = t.AddSeconds(1); l.OnDrop(t); } // 1 Mbps, 30 fps
        Assert.Equal((1_000_000, 30), (l.Bitrate, l.Fps));
        Assert.False(l.Tick(t.AddSeconds(4.9)));
        Assert.True(l.Tick(t.AddSeconds(5)));
        Assert.Equal((1_000_000, 60), (l.Bitrate, l.Fps));
        Assert.False(l.Tick(t.AddSeconds(6))); // one step per 5 s
        Assert.True(l.Tick(t.AddSeconds(10)));
        Assert.Equal(1_428_571, l.Bitrate);
        // A drop restarts the clean clock.
        l.OnDrop(t.AddSeconds(11));
        Assert.False(l.Tick(t.AddSeconds(15)));
        Assert.True(l.Tick(t.AddSeconds(16)));
        // Never above the start.
        var top = new AdaptiveLadder(false, 60, T0);
        Assert.False(top.Tick(T0.AddMinutes(1)));
        Assert.Equal(8_000_000, top.Bitrate);
    }

    [Fact]
    public void Jpeg_ladder_only_steps_fps()
    {
        var l = new AdaptiveLadder(false, 60, T0, jpeg: true);
        Assert.True(l.OnDrop(T0.AddSeconds(1)));
        Assert.Equal(5, l.Fps);
        Assert.False(l.OnDrop(T0.AddSeconds(2)));
        Assert.True(l.Tick(T0.AddSeconds(7)));
        Assert.Equal(10, l.Fps);
    }

    // ---------------- send queue ----------------

    static QueuedScreenFrame F(ScreenFrameKind k, int id = 0) => new(k, new[] { (byte)id }, (ulong)id, 0, 0, true);

    [Fact]
    public void Queue_never_holds_more_than_two_pictures_and_skips_to_the_next_keyframe_after_a_drop()
    {
        var q = new ScreenSendQueue();
        Assert.Equal(ScreenQueueResult.Queued, q.Offer(F(ScreenFrameKind.Config)));
        Assert.Equal(ScreenQueueResult.Queued, q.Offer(F(ScreenFrameKind.Keyframe, 1)));
        Assert.Equal(ScreenQueueResult.Queued, q.Offer(F(ScreenFrameKind.Frame, 2)));
        Assert.Equal(ScreenQueueResult.Dropped, q.Offer(F(ScreenFrameKind.Frame, 3)));
        Assert.True(q.WaitingForKeyframe);
        Assert.Equal(ScreenQueueResult.Skipped, q.Offer(F(ScreenFrameKind.Frame, 4)));
        Assert.Equal(3, q.Count);
        // The keyframe supersedes every waiting picture (the config stays in front of it).
        Assert.Equal(ScreenQueueResult.Queued, q.Offer(F(ScreenFrameKind.Keyframe, 5)));
        Assert.False(q.WaitingForKeyframe);
        Assert.True(q.TryDequeue(out var a));
        Assert.True(q.TryDequeue(out var b));
        Assert.False(q.TryDequeue(out _));
        Assert.Equal(ScreenFrameKind.Config, a.Kind);
        Assert.Equal((ScreenFrameKind.Keyframe, 5), (b.Kind, (int)b.Payload[0]));
    }

    [Fact]
    public void Queue_drains_normally_when_the_socket_keeps_up()
    {
        var q = new ScreenSendQueue();
        q.Offer(F(ScreenFrameKind.Config));
        q.Offer(F(ScreenFrameKind.Keyframe, 1));
        for (var i = 2; i < 50; i++)
        {
            q.TryDequeue(out _);
            Assert.Equal(ScreenQueueResult.Queued, q.Offer(F(ScreenFrameKind.Frame, i)));
        }
    }

    [Fact]
    public void A_new_config_clears_the_queue_and_waits_for_its_keyframe()
    {
        var q = new ScreenSendQueue();
        q.Offer(F(ScreenFrameKind.Keyframe, 1));
        q.Offer(F(ScreenFrameKind.Frame, 2));
        q.Offer(F(ScreenFrameKind.Config, 3));
        Assert.Equal(1, q.Count);
        Assert.Equal(ScreenQueueResult.Skipped, q.Offer(F(ScreenFrameKind.Frame, 4)));
        q.Offer(F(ScreenFrameKind.Keyframe, 5));
        Assert.Equal(2, q.Count);
    }

    [Fact]
    public void Jpeg_queue_keeps_the_newest_two()
    {
        var q = new ScreenSendQueue();
        q.Offer(F(ScreenFrameKind.Jpeg, 1));
        q.Offer(F(ScreenFrameKind.Jpeg, 2));
        Assert.Equal(ScreenQueueResult.Replaced, q.Offer(F(ScreenFrameKind.Jpeg, 3)));
        q.TryDequeue(out var a);
        q.TryDequeue(out var b);
        Assert.Equal(new[] { 2, 3 }, new[] { (int)a.Payload[0], (int)b.Payload[0] });
    }

    // ---------------- locked → paused → resumed ----------------

    [Fact]
    public void Lock_and_secure_desktop_drive_paused_and_resumed()
    {
        var s = new ScreenLockState();
        Assert.Equal(ScreenTransition.Paused, s.SetOsLocked(true));
        Assert.Equal(ScreenTransition.None, s.SetCaptureBlocked(true));
        Assert.Equal(ScreenTransition.None, s.SetOsLocked(false)); // still blocked
        Assert.Equal(ScreenTransition.Resumed, s.SetCaptureBlocked(false));
        Assert.Equal(ScreenTransition.None, s.SetOsLocked(false));
        Assert.Equal(ScreenTransition.Paused, s.SetCaptureBlocked(true)); // a UAC prompt
        Assert.Equal(ScreenTransition.Resumed, s.SetCaptureBlocked(false));
    }

    // ---------------- view, crop, cursor, rotation ----------------

    [Fact]
    public void View_is_clamped_inside_the_display()
    {
        Assert.Equal(new RemoteView(0, 0, 1, 1), RemoteViewMath.Clamp(new RemoteView(-1, -1, 3, 3)));
        Assert.Equal(new RemoteView(0.5, 0.5, 0.5, 0.5), RemoteViewMath.Clamp(new RemoteView(0.9, 0.9, 0.5, 0.5)));
        Assert.Equal(1.0 / 64, RemoteViewMath.Clamp(new RemoteView(0, 0, 0, 0)).W);
        Assert.Equal(new RemoteView(0, 0, 1, 1), RemoteViewMath.Clamp(new RemoteView(double.NaN, 0, double.PositiveInfinity, 1)));
    }

    [Fact]
    public void Crop_and_encoded_size_for_the_G9()
    {
        // Whole G9 into a 2400×1080 phone box: keeps 32:9, even.
        var crop = RemoteViewMath.Crop(RemoteView.Whole, 5120, 1440);
        Assert.Equal(new PixelBox(0, 0, 5120, 1440), crop);
        Assert.Equal((2400, 674), RemoteViewMath.OutputSize(crop.W, crop.H, 2400, 1080));
        // Zoomed to a quarter: the crop is encoded at the phone's size, never upscaled.
        var zoom = RemoteViewMath.Crop(new RemoteView(0.25, 0.25, 0.25, 0.5), 5120, 1440);
        Assert.Equal(new PixelBox(1280, 360, 1280, 720), zoom);
        Assert.Equal((1280, 720), RemoteViewMath.OutputSize(zoom.W, zoom.H, 2400, 1080));
        // Odd sizes become even; nothing above 4096 a side.
        Assert.Equal((1918, 1078), RemoteViewMath.OutputSize(1919, 1079, 0, 0));
        Assert.Equal((4096, 1152), RemoteViewMath.OutputSize(5120, 1440, 0, 0));
    }

    [Fact]
    public void Cursor_is_a_fraction_of_the_encoded_view_and_may_be_outside()
    {
        var display = new PixelBox(1920, 0, 5120, 1440);
        var crop = new PixelBox(1280, 360, 1280, 720);
        Assert.Equal((0.5f, 0.5f), RemoteViewMath.CursorFraction(1920 + 1280 + 640, 360 + 360, display, crop));
        var (x, y) = RemoteViewMath.CursorFraction(100, 100, display, crop); // on another monitor
        Assert.True(x < 0 && y < 0);
        Assert.True(float.IsNaN(RemoteViewMath.CursorFraction(0, 0, display, default).X));
    }

    [Fact]
    public void Rotated_displays_map_the_crop_into_the_unrotated_texture()
    {
        // A 1080×1920 portrait display (rotated 90°): its texture is 1920×1080.
        var topLeft = new PixelBox(0, 0, 100, 200);
        Assert.Equal(new PixelBox(0, 980, 200, 100), RemoteRotationMath.ToSource(topLeft, 1080, 1920, RemoteRotation.Cw90));
        Assert.Equal(new PixelBox(1720, 0, 200, 100), RemoteRotationMath.ToSource(topLeft, 1080, 1920, RemoteRotation.Cw270));
        Assert.Equal(new PixelBox(980, 1720, 100, 200), RemoteRotationMath.ToSource(topLeft, 1080, 1920, RemoteRotation.Cw180));
        Assert.Equal(topLeft, RemoteRotationMath.ToSource(topLeft, 1080, 1920, RemoteRotation.None));
        // The points agree with the rectangles.
        Assert.Equal((0.0, 1080.0), RemoteRotationMath.PointToSource(0, 0, 1080, 1920, RemoteRotation.Cw90));
        Assert.Equal((1920.0, 0.0), RemoteRotationMath.PointToSource(0, 0, 1080, 1920, RemoteRotation.Cw270));
    }

    // ---------------- H.264 Annex-B ----------------

    static readonly byte[] Sps = { 0x67, 0x4D, 0x40, 0x28 };
    static readonly byte[] Pps = { 0x68, 0xEE, 0x3C, 0x80 };
    static readonly byte[] Idr = { 0x65, 0x88, 0x84, 0x00 };
    static readonly byte[] Slice = { 0x41, 0x9A, 0x02 };

    static byte[] Au(params byte[][] nals) => nals.SelectMany((n, i) => (i % 2 == 0 ? new byte[] { 0, 0, 0, 1 } : new byte[] { 0, 0, 1 }).Concat(n)).ToArray();

    [Fact]
    public void Annex_b_split_puts_sps_pps_in_the_config_and_the_slices_in_the_keyframe()
    {
        var (config, body, key) = AnnexB.Split(Au(Sps, Pps, Idr));
        Assert.True(key);
        Assert.Equal(new byte[] { 0, 0, 0, 1 }.Concat(Sps).Concat(new byte[] { 0, 0, 0, 1 }).Concat(Pps), config);
        Assert.Equal(new byte[] { 0, 0, 0, 1 }.Concat(Idr), body);
        var (none, p, isKey) = AnnexB.Split(Au(Slice));
        Assert.Null(none);
        Assert.False(isKey);
        Assert.Equal(new byte[] { 0, 0, 0, 1 }.Concat(Slice), p);
        Assert.Equal(new[] { 7, 8, 5 }, AnnexB.Nals(Au(Sps, Pps, Idr)).Select(n => n.Type));
    }

    [Fact]
    public void Config_goes_before_every_keyframe()
    {
        var t = new ScreenConfigTracker();
        Assert.Null(t.ForKeyframe(null)); // the encoder never gave SPS/PPS: nothing decodable yet
        var c1 = new byte[] { 1 };
        Assert.Equal(c1, t.ForKeyframe(c1));
        Assert.Equal(c1, t.ForKeyframe(c1));   // periodic keyframe: config again
        Assert.Equal(c1, t.ForKeyframe(null)); // a keyframe without inline SPS/PPS: the remembered one
        var c2 = new byte[] { 2 };
        Assert.Equal(c2, t.ForKeyframe(c2));   // new size
        t.Reset();                             // new display: the old config would be wrong
        Assert.Null(t.ForKeyframe(null));
    }

    // ---------------- Tailscale ----------------

    [Fact]
    public void Tailscale_filter_keeps_100_64_slash_10_and_fd7a_115c_a1e0_slash_48()
    {
        var all = new[]
        {
            "192.168.1.20", "100.64.0.1", "100.127.255.254", "100.128.0.1", "100.63.255.255", "10.0.0.1",
            "fd7a:115c:a1e0::1", "fd7a:115c:a1e1::1", "fe80::1", "::ffff:100.101.102.103", "100.101.102.103",
        }.Select(IPAddress.Parse);
        Assert.Equal(new[] { "100.64.0.1", "100.127.255.254", "100.101.102.103", "fd7a:115c:a1e0::1" }, TailscaleAddresses.Filter(all));
        Assert.Equal(new[] { "fd7a:115c:a1e0::5" }, TailscaleAddresses.Filter(new[] { IPAddress.Parse("fd7a:115c:a1e0::5%12") }));
    }

    // ---------------- focus → keyboard ----------------

    [Theory]
    [InlineData(UiaControlType.Edit, false, false, false, false, true, false)]
    [InlineData(UiaControlType.Edit, true, true, false, false, true, true)]       // password box
    [InlineData(UiaControlType.Edit, false, true, true, false, false, false)]     // read-only text box
    [InlineData(UiaControlType.Document, false, false, false, true, true, false)] // Word, Notepad
    [InlineData(UiaControlType.Document, false, true, true, true, false, false)]  // a read-only document
    [InlineData(UiaControlType.ComboBox, false, true, false, false, true, false)] // editable combo
    [InlineData(UiaControlType.ComboBox, false, false, false, false, false, false)] // drop-down list only
    [InlineData(UiaControlType.Custom, false, false, false, true, true, false)]   // web / Electron editor
    [InlineData(50000, false, false, false, false, false, false)]                  // a button
    [InlineData(UiaControlType.Spinner, false, true, false, false, true, false)]  // a writable number box
    [InlineData(50033, false, false, false, true, false, false)]                   // a pane with text, not editable
    public void Focus_rules_open_the_keyboard_for_text_fields(int type, bool password, bool value, bool readOnly, bool textPattern, bool text, bool secure)
        => Assert.Equal(new RemoteFocusState(text, secure), RemoteFocusRules.Classify(new FocusFacts(type, password, value, readOnly, textPattern)));

    [Fact]
    public void No_focus_means_no_keyboard()
        => Assert.Equal(RemoteFocusState.None, RemoteFocusRules.Classify(null));

    [Fact]
    public void Focus_is_debounced_150_ms_and_sent_on_change_only()
    {
        var d = new FocusDebouncer();
        var text = new RemoteFocusState(true, false);
        d.Offer(text, T0);
        Assert.Null(d.Poll(T0.AddMilliseconds(100)));
        d.Offer(RemoteFocusState.None, T0.AddMilliseconds(120)); // hopped away
        d.Offer(text, T0.AddMilliseconds(130));                  // and back
        Assert.Null(d.Poll(T0.AddMilliseconds(250)));
        Assert.Equal(text, d.Poll(T0.AddMilliseconds(280)));
        d.Offer(text, T0.AddMilliseconds(400));
        Assert.Null(d.Poll(T0.AddMilliseconds(600))); // unchanged
        d.Reset();
        d.Offer(text, T0.AddSeconds(1));
        Assert.Equal(text, d.Poll(T0.AddSeconds(1.2))); // a new session hears it again
    }

    // ---------------- managed PCs and staying awake ----------------

    [Fact]
    public void Windows_unlock_is_managed_when_domain_or_entra_joined_else_unsupported()
    {
        Assert.True(RemoteUnlockAvailability.IsDomainJoined(3));  // NetSetupDomainName
        Assert.False(RemoteUnlockAvailability.IsDomainJoined(2)); // NetSetupWorkgroupName
        Assert.False(RemoteUnlockAvailability.IsDomainJoined(1)); // NetSetupUnjoined
        Assert.True(RemoteUnlockAvailability.IsEntraJoined(1));   // DSREG_DEVICE_JOIN
        Assert.False(RemoteUnlockAvailability.IsEntraJoined(2));  // DSREG_WORKPLACE_JOIN (a work account added)
        Assert.False(RemoteUnlockAvailability.IsEntraJoined(null));
        Assert.Equal("managed", RemoteUnlockAvailability.ForWindows(true, false));
        Assert.Equal("managed", RemoteUnlockAvailability.ForWindows(false, true));
        Assert.Equal("unsupported", RemoteUnlockAvailability.ForWindows(false, false));
    }

    [Fact]
    public void Keep_awake_while_viewed_and_while_locked_on_mains_when_asked()
    {
        Assert.Equal(RemoteAwake.System | RemoteAwake.Display, RemoteAwakePolicy.Need(true, false, false, false));
        Assert.Equal(RemoteAwake.System, RemoteAwakePolicy.Need(true, false, true, false));
        Assert.Equal(RemoteAwake.System, RemoteAwakePolicy.Need(false, true, true, true));
        Assert.Equal(RemoteAwake.None, RemoteAwakePolicy.Need(false, true, true, false)); // on battery
        Assert.Equal(RemoteAwake.None, RemoteAwakePolicy.Need(false, true, false, true)); // not locked
        Assert.Equal(RemoteAwake.None, RemoteAwakePolicy.Need(false, false, true, true));
        Assert.Equal(0x80000003u, RemoteAwakePolicy.ExecutionState(RemoteAwake.System | RemoteAwake.Display));
        Assert.Equal(0x80000001u, RemoteAwakePolicy.ExecutionState(RemoteAwake.System));
        Assert.Equal(0x80000000u, RemoteAwakePolicy.ExecutionState(RemoteAwake.None));
        Assert.True(RemoteAwakePolicy.OnMains(1, 0));
        Assert.False(RemoteAwakePolicy.OnMains(0, 0));
        Assert.True(RemoteAwakePolicy.OnMains(255, 128)); // a desktop with no battery
        Assert.False(RemoteAwakePolicy.OnMains(255, 1));
    }

    // ---------------- pixels ----------------

    [Fact]
    public void Nv12_of_white_black_and_red()
    {
        static byte[] Solid(byte b, byte g, byte r, int n) => Enumerable.Range(0, n).SelectMany(_ => new[] { b, g, r, (byte)255 }).ToArray();
        var nv12 = new byte[4 * 2 * 3 / 2];
        PixelConvert.BgraToNv12(Solid(255, 255, 255, 8), 4, 2, nv12);
        Assert.All(nv12[..8], y => Assert.Equal(235, y));
        Assert.All(nv12[8..], c => Assert.Equal(128, c));
        PixelConvert.BgraToNv12(Solid(0, 0, 0, 8), 4, 2, nv12);
        Assert.All(nv12[..8], y => Assert.Equal(16, y));
        PixelConvert.BgraToNv12(Solid(0, 0, 255, 8), 4, 2, nv12);
        Assert.Equal(82, nv12[0]);
        Assert.Equal(90, nv12[8]);  // U
        Assert.Equal(240, nv12[9]); // V
    }

    [Fact]
    public void Cpu_crop_scale_reads_the_right_corner_of_a_rotated_display()
    {
        // A 2×4 portrait display (rotated 90°) whose texture is 4×2; its top-left as seen is red.
        const int tw = 4, th = 2;
        var tex = new byte[tw * th * 4];
        var (sx, sy) = RemoteRotationMath.PointToSource(0.5, 0.5, 2, 4, RemoteRotation.Cw90);
        var px = ((int)sy * tw + (int)sx) * 4;
        tex[px + 2] = 255;
        tex[px + 3] = 255;
        var dst = new byte[1 * 1 * 4];
        PixelConvert.CropScaleBgra(tex, tw, th, tw * 4, new PixelBox(0, 0, 1, 1), 2, 4, RemoteRotation.Cw90, dst, 1, 1);
        Assert.Equal(255, dst[2]);
        var other = new byte[4];
        PixelConvert.CropScaleBgra(tex, tw, th, tw * 4, new PixelBox(1, 3, 1, 1), 2, 4, RemoteRotation.Cw90, other, 1, 1);
        Assert.Equal(0, other[2]);
    }

    [Fact]
    public void Jpeg_fallback_is_a_real_jpeg_of_the_picture()
    {
        var bgra = Enumerable.Range(0, 16 * 8).SelectMany(_ => new byte[] { 255, 0, 0, 255 }).ToArray(); // blue
        var jpeg = JpegFrameEncoder.Encode(bgra, 16, 8, 16 * 4);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpeg[..2]);
        using var bmp = SKBitmap.Decode(jpeg);
        Assert.Equal((16, 8), (bmp.Width, bmp.Height));
        var c = bmp.GetPixel(8, 4);
        Assert.True(c.Blue > 200 && c.Red < 60);
    }
}

// =====================================================================================================================
// The /screen socket and the main-link messages against a running core, with a fake capture pipeline.
// =====================================================================================================================

/// <summary>Stands in for DXGI + Media Foundation: H.264-shaped access units on a timer (SPS+PPS+IDR, then P slices).</summary>
sealed class FakeScreenPipeline : IScreenPipeline
{
    readonly Timer _timer;
    volatile ScreenPipelineConfig? _config;
    volatile bool _paused = true;
    int _key = 1;
    long _pts;

    public FakeScreenPipeline() => _timer = new Timer(_ => Tick(), null, 15, 15);

    public List<ScreenPipelineConfig> Starts { get; } = new();
    public int KeyRequests;
    public int Pauses;
    public (int Bitrate, int Fps) Rate;
    public bool Disposed;
    public (double X, double Y) Cursor = (0, 0);
    public event Action<PipelineFrame>? Frame;
    public event Action<ScreenPipelineEvent>? Event;
    public string? EncoderName => "fake";

    public void Start(ScreenPipelineConfig config)
    {
        lock (Starts) Starts.Add(config);
        Rate = (config.Bitrate, config.Fps);
        _config = config;
        _key = 1;
        _paused = false;
    }

    public void SetRate(int bitrate, int fps) => Rate = (bitrate, fps);
    public void RequestKeyframe() { Interlocked.Increment(ref KeyRequests); _key = 1; }
    public void Pause() { Interlocked.Increment(ref Pauses); _paused = true; }
    public void Raise(ScreenPipelineEvent e) => Event?.Invoke(e);

    void Tick()
    {
        if (_paused || Disposed || _config is not { OutW: > 0 } c) return;
        var key = Interlocked.Exchange(ref _key, 0) == 1;
        byte[] data;
        if (c.Codec == ScreenCodec.Jpeg) data = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        else if (key) data = new byte[] { 0, 0, 0, 1, 0x67, 0x42, (byte)c.OutW, 0, 0, 0, 1, 0x68, 0xCE, 0, 0, 0, 1, 0x65, 0x88 };
        else data = new byte[] { 0, 0, 0, 1, 0x41, 0x9A };
        Frame?.Invoke(new PipelineFrame(data, key, Interlocked.Add(ref _pts, 16_000), Cursor.X, Cursor.Y, true, c));
    }

    public void Dispose()
    {
        Disposed = true;
        _timer.Dispose();
    }
}

sealed class FakeScreenFactory : IScreenPipelineFactory
{
    public List<FakeScreenPipeline> Made { get; } = new();
    public FakeScreenPipeline Last { get { lock (Made) return Made[^1]; } }
    public bool IsAvailable => true;
    public IScreenPipeline Create()
    {
        var p = new FakeScreenPipeline();
        lock (Made) Made.Add(p);
        return p;
    }
}

sealed class FakeRemoteSystem : IRemoteSystem
{
    public bool? Locked;
    public (bool, bool) Join = (false, false);
    public List<RemoteAwake> Awake { get; } = new();
    public bool? IsLocked() => Locked;
    public bool OnMainsPower => true;
    public void SetAwake(RemoteAwake need) { lock (Awake) Awake.Add(need); }
    public (bool Domain, bool Entra) JoinState() => Join;
    public IRemoteFocusSource? CreateFocusSource() => null;
    public void Dispose() { }
}

/// <summary>Plays the phone's /screen client: encrypted screenOpen first, then reads text and binary frames.</summary>
sealed class FakeScreenClient : IAsyncDisposable
{
    readonly ClientWebSocket _ws = new();
    readonly byte[] _key;
    readonly Channel<(string? Type, JsonElement Data, ScreenFrameHeader? Header, byte[]? Payload)> _inbox = Channel.CreateUnbounded<(string?, JsonElement, ScreenFrameHeader?, byte[]?)>();
    Task? _loop;

    public FakeScreenClient(byte[] key) => _key = key;

    public WebSocketCloseStatus? CloseStatus => _ws.CloseStatus;
    public List<string> Texts { get; } = new();

    public async Task ConnectAsync(int port)
    {
        _ws.Options.KeepAliveInterval = TimeSpan.Zero;
        await _ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/screen"), CancellationToken.None);
        _loop = Task.Run(ReceiveAsync);
    }

    public Task SendAsync(string type, object? data = null, byte[]? key = null)
        => _ws.SendAsync(Encoding.UTF8.GetBytes(LinkCrypto.Encrypt(LinkServer.BuildEnvelope(type, data), key ?? _key)), WebSocketMessageType.Text, true, CancellationToken.None);

    public Task OpenAsync(string display = "", string codec = "h264", int maxW = 2400, int maxH = 1080)
        => SendAsync("screenOpen", new { display, maxW, maxH, fps = 60, codec, view = new { x = 0, y = 0, w = 1, h = 1 } });

    async Task ReceiveAsync()
    {
        var buf = new byte[1 << 16];
        using var ms = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buf, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
                        return;
                    }
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Binary)
                {
                    Assert.True(ScreenFraming.TryOpen(ms.ToArray(), _key, out var h, out var p));
                    await _inbox.Writer.WriteAsync((null, default, h, p));
                }
                else
                {
                    var plain = LinkCrypto.TryDecrypt(Encoding.UTF8.GetString(ms.ToArray()), _key);
                    Assert.NotNull(plain);
                    using var doc = JsonDocument.Parse(plain!);
                    var type = doc.RootElement.GetProperty("type").GetString()!;
                    lock (Texts) Texts.Add(type);
                    await _inbox.Writer.WriteAsync((type, doc.RootElement.GetProperty("data").Clone(), null, null));
                }
            }
        }
        catch { }
        finally { _inbox.Writer.TryComplete(); }
    }

    public async Task<JsonElement> WaitTextAsync(string type, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var m = await _inbox.Reader.ReadAsync(cts.Token);
            if (m.Type == type) return m.Data;
        }
    }

    /// <summary>The next binary frame (text frames before it are skipped and returned).</summary>
    public async Task<(ScreenFrameHeader Header, byte[] Payload)> NextFrameAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var m = await _inbox.Reader.ReadAsync(cts.Token);
            if (m.Header is { } h) return (h, m.Payload!);
        }
    }

    /// <summary>Reads until the next text frame of this type, collecting the order of everything before it.</summary>
    public async Task<List<string>> SequenceUntilAsync(Func<List<string>, bool> done, TimeSpan? timeout = null)
    {
        var seen = new List<string>();
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (!done(seen))
        {
            var m = await _inbox.Reader.ReadAsync(cts.Token);
            seen.Add(m.Type ?? "bin:" + m.Header!.Value.Kind);
        }
        return seen;
    }

    public async Task<bool> WaitClosedAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (_loop is { IsCompleted: true }) return true;
            await Task.Delay(50);
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_ws.State == WebSocketState.Open) await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", new CancellationTokenSource(1000).Token); } catch { }
        _ws.Dispose();
    }
}

public class PhoneRemoteScreenTests
{
    static readonly DisplayScreen G9 = new(@"\\.\DISPLAY1", "Odyssey G9", true, 0, 0, 5120, 1440) { PxW = 5120, PxH = 1440, Scale = 1 };
    static readonly DisplayScreen Side = new(@"\\.\DISPLAY2", "Dell U2720Q", false, 5120, 0, 1920, 1080) { PxW = 1920, PxH = 1080, Scale = 1 };

    sealed record Rig(TestHost Host, FakePhone Phone, FakeScreenFactory Screens, FakeRemoteSystem System, FakeDesktopControl Pc) : IAsyncDisposable
    {
        public byte[] Key => Phone.Key;
        public PhoneRemote Remote => Host.Core.Remote;

        public async Task<FakeScreenClient> OpenAsync(string display = "", string codec = "h264")
        {
            var c = new FakeScreenClient(Key);
            await c.ConnectAsync(Host.Core.Link.Port);
            await c.OpenAsync(display, codec);
            return c;
        }

        public async ValueTask DisposeAsync()
        {
            await Phone.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    static async Task<Rig> StartAsync(TimeSpan? pingTimeout = null, (bool, bool)? join = null, bool? locked = null)
    {
        var screens = new FakeScreenFactory();
        var system = new FakeRemoteSystem { Join = join ?? (false, false), Locked = locked };
        var pc = new FakeDesktopControl();
        var host = new TestHost(name: "PC-INTERN", desktop: () => pc, remote: new PhoneRemoteOptions
        {
            Pipelines = () => screens,
            System = () => system,
            TailscaleAddrs = () => new[] { "100.101.102.103", "fd7a:115c:a1e0::1" },
            PingTimeout = pingTimeout ?? TimeSpan.FromSeconds(5),
            OpenTimeout = TimeSpan.FromSeconds(1),
            PollInterval = TimeSpan.FromMilliseconds(100),
        });
        host.Core.Aim.SetScreens(new[] { G9, Side });
        var phone = await host.ConnectPhoneAsync();
        return new Rig(host, phone, screens, system, pc);
    }

    // ---------------- main link ----------------

    [Fact]
    public async Task Remote_hello_describes_this_pc()
    {
        await using var rig = await StartAsync();
        await rig.Phone.SendAsync("remoteHelloRequest", new { });
        var hello = (await rig.Phone.WaitForAsync("remoteHello")).Data;
        Assert.Equal(1, hello.GetProperty("v").GetInt32());
        Assert.Equal("PC-INTERN", hello.GetProperty("id").GetString());
        Assert.Equal("windows", hello.GetProperty("platform").GetString());
        Assert.True(hello.GetProperty("screen").GetBoolean());
        Assert.True(hello.GetProperty("control").GetBoolean());
        Assert.Equal("unsupported", hello.GetProperty("unlock").GetString());
        Assert.False(hello.GetProperty("locked").GetBoolean());
        var displays = hello.GetProperty("displays").EnumerateArray().ToList();
        Assert.Equal(2, displays.Count);
        Assert.Equal(@"\\.\DISPLAY1", displays[0].GetProperty("id").GetString());
        Assert.Equal("Odyssey G9", displays[0].GetProperty("name").GetString());
        Assert.Equal((5120, 1440, true), (displays[0].GetProperty("w").GetInt32(), displays[0].GetProperty("h").GetInt32(), displays[0].GetProperty("main").GetBoolean()));
        Assert.False(displays[1].GetProperty("main").GetBoolean());
        Assert.Equal(new[] { "100.101.102.103", "fd7a:115c:a1e0::1" }, hello.GetProperty("addrs").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task A_managed_pc_says_managed_and_unlock_is_never_set_up()
    {
        await using var rig = await StartAsync(join: (false, true));
        await rig.Phone.SendAsync("remoteHelloRequest", new { });
        Assert.Equal("managed", (await rig.Phone.WaitForAsync("remoteHello")).Data.GetProperty("unlock").GetString());
        await rig.Phone.SendAsync("unlockRequest", new { });
        var r = (await rig.Phone.WaitForAsync("unlockResult")).Data;
        Assert.False(r.GetProperty("ok").GetBoolean());
        Assert.Equal("notSetUp", r.GetProperty("error").GetString());
        await rig.Phone.SendAsync("unlockEnroll", new { phoneId = "p", publicKey = "AAAA" });
        Assert.False((await rig.Phone.WaitForAsync("unlockEnrollResult")).Data.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Lock_changes_reach_the_phone_and_resend_the_hello()
    {
        await using var rig = await StartAsync();
        await rig.Phone.SendAsync("remoteHelloRequest", new { });
        await rig.Phone.WaitForAsync("remoteHello");
        rig.System.Locked = true; // the poll sees it
        Assert.True((await rig.Phone.WaitForAsync("remoteLockState")).Data.GetProperty("locked").GetBoolean());
        Assert.True((await rig.Phone.WaitForAsync("remoteHello")).Data.GetProperty("locked").GetBoolean());
        rig.System.Locked = null;
        rig.Remote.ReportLock(false); // the WTS notification
        Assert.False((await rig.Phone.WaitForAsync("remoteLockState")).Data.GetProperty("locked").GetBoolean());
    }

    [Fact]
    public async Task Turning_viewing_off_says_so_and_ignores_the_phones_mouse_and_keys()
    {
        await using var rig = await StartAsync();
        await rig.Phone.SendAsync("remoteHelloRequest", new { });
        await rig.Phone.WaitForAsync("remoteHello");
        rig.Remote.Allowed = false;
        var hello = (await rig.Phone.WaitForAsync("remoteHello")).Data;
        Assert.False(hello.GetProperty("screen").GetBoolean());
        Assert.False(hello.GetProperty("control").GetBoolean());
        await rig.Phone.SendAsync("remoteControl", new { action = "mouse_move", dx = 5, dy = 5 });
        await rig.Phone.SendAsync("remoteControl", new { action = "type", text = "hi" });
        await rig.Phone.SendAsync("remoteControl", new { action = "vol_mute" });
        Assert.True(await Eventually.TrueAsync(() => rig.Pc.Log.Contains("mute")));
        Assert.DoesNotContain(rig.Pc.Log, l => l.StartsWith("move") || l.StartsWith("text"));

        await using var c = await rig.OpenAsync();
        Assert.True(await c.WaitClosedAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(4012, (int?)c.CloseStatus);
        rig.Remote.Allowed = true;
    }

    // ---------------- /screen ----------------

    [Fact]
    public async Task Screen_open_sends_opened_then_config_keyframe_and_frames_with_the_cursor()
    {
        await using var rig = await StartAsync();
        string? viewer = null;
        rig.Remote.SessionChanged += p => viewer = p;
        await using var c = await rig.OpenAsync();
        var opened = await c.WaitTextAsync("screenOpened");
        Assert.Equal(@"\\.\DISPLAY1", opened.GetProperty("display").GetString());
        Assert.Equal((2400, 674), (opened.GetProperty("w").GetInt32(), opened.GetProperty("h").GetInt32()));
        Assert.Equal("h264", opened.GetProperty("codec").GetString());
        rig.Screens.Last.Cursor = (2560, 720); // the middle of the G9

        var config = await c.NextFrameAsync();
        Assert.Equal(ScreenFrameKind.Config, config.Header.Kind);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x67 }, config.Payload[..5]);
        var key = await c.NextFrameAsync();
        Assert.Equal(ScreenFrameKind.Keyframe, key.Header.Kind);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x65, 0x88 }, key.Payload);
        Assert.Equal(config.Header.Seq + 1, key.Header.Seq);
        var frame = await c.NextFrameAsync();
        Assert.Equal(ScreenFrameKind.Frame, frame.Header.Kind);
        Assert.True(frame.Header.CursorVisible);
        Assert.Equal(0.5f, frame.Header.CursorX, 3);
        Assert.Equal(0.5f, frame.Header.CursorY, 3);
        Assert.Equal("Fake Pixel", viewer);
        Assert.Equal("Fake Pixel", rig.Remote.ViewingPhone);
        Assert.Contains(rig.Host.Core.LogLines, l => l.Contains(@"[remote] opened by Fake Pixel display Odyssey G9 2400x674 h264"));
        lock (rig.System.Awake) Assert.Equal(RemoteAwake.System | RemoteAwake.Display, rig.System.Awake[^1]);
    }

    [Fact]
    public async Task Every_keyframe_comes_right_after_a_config_frame()
    {
        await using var rig = await StartAsync();
        await using var c = await rig.OpenAsync();
        await c.WaitTextAsync("screenOpened");
        var kinds = new List<ScreenFrameKind>();
        var keyframes = 0;
        while (keyframes < 3)
        {
            var (h, _) = await c.NextFrameAsync();
            kinds.Add(h.Kind);
            if (h.Kind == ScreenFrameKind.Keyframe)
            {
                keyframes++;
                // The phone resets its decoder on screenKeyframe: ask for the next one.
                await c.SendAsync("screenKeyframe", new { });
            }
        }
        for (var i = 0; i < kinds.Count; i++)
        {
            if (kinds[i] == ScreenFrameKind.Keyframe) Assert.Equal(ScreenFrameKind.Config, kinds[i - 1]);
            if (kinds[i] == ScreenFrameKind.Config) Assert.Equal(ScreenFrameKind.Keyframe, kinds[i + 1]);
        }
    }

    [Fact]
    public async Task Ping_is_answered_and_silence_ends_the_session()
    {
        await using var rig = await StartAsync(pingTimeout: TimeSpan.FromSeconds(1.5));
        var ended = new TaskCompletionSource();
        rig.Remote.SessionChanged += p => { if (p is null) ended.TrySetResult(); };
        await using var c = await rig.OpenAsync();
        await c.WaitTextAsync("screenOpened");
        await c.SendAsync("screenPing", new { t = 123456789012L });
        Assert.Equal(123456789012L, (await c.WaitTextAsync("screenPong")).GetProperty("t").GetInt64());
        // Then nothing: the phone went to the background.
        Assert.True(await c.WaitClosedAsync(TimeSpan.FromSeconds(5)));
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(rig.Remote.ViewingPhone);
        Assert.True(rig.Screens.Last.Disposed);
        Assert.True(await Eventually.TrueAsync(() => { lock (rig.System.Awake) return rig.System.Awake[^1] == RemoteAwake.None; }));
        Assert.Contains(rig.Host.Core.LogLines, l => l.Contains("[remote] closed (no ping for 5 s)"));
    }

    [Fact]
    public async Task A_wrong_key_or_a_late_or_wrong_first_frame_closes_4003()
    {
        await using var rig = await StartAsync();
        await using (var bad = new FakeScreenClient(rig.Key))
        {
            await bad.ConnectAsync(rig.Host.Core.Link.Port);
            await bad.SendAsync("screenOpen", new { display = "" }, key: new byte[32]);
            Assert.True(await bad.WaitClosedAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(4003, (int?)bad.CloseStatus);
        }
        await using (var wrongType = new FakeScreenClient(rig.Key))
        {
            await wrongType.ConnectAsync(rig.Host.Core.Link.Port);
            await wrongType.SendAsync("screenPing", new { t = 1 });
            Assert.True(await wrongType.WaitClosedAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(4003, (int?)wrongType.CloseStatus);
        }
        await using (var silent = new FakeScreenClient(rig.Key))
        {
            await silent.ConnectAsync(rig.Host.Core.Link.Port);
            Assert.True(await silent.WaitClosedAsync(TimeSpan.FromSeconds(5))); // OpenTimeout is 1 s here
            Assert.Equal(4003, (int?)silent.CloseStatus);
        }
        Assert.Empty(rig.Screens.Made);
    }

    [Fact]
    public async Task Locking_pauses_and_unlocking_resumes_with_config_and_keyframe()
    {
        await using var rig = await StartAsync();
        await using var c = await rig.OpenAsync();
        await c.WaitTextAsync("screenOpened");
        await c.NextFrameAsync();
        rig.Remote.ReportLock(true);
        Assert.Equal("locked", (await c.WaitTextAsync("screenPaused")).GetProperty("reason").GetString());
        Assert.Equal(1, rig.Screens.Last.Pauses);
        lock (rig.System.Awake) Assert.Equal(RemoteAwake.System, rig.System.Awake[^1]);
        rig.Remote.ReportLock(false);
        var seq = await c.SequenceUntilAsync(s => s.IndexOf("screenResumed") is var i && i >= 0 && s.Skip(i).Contains("bin:Keyframe"));
        var resumed = seq.IndexOf("screenResumed");
        Assert.True(resumed >= 0);
        Assert.Equal("bin:Keyframe", seq[^1]);
        Assert.Equal("bin:Config", seq[^2]); // the config right before the keyframe
        Assert.True(seq.Count - 2 > resumed);
        Assert.DoesNotContain("bin:Frame", seq.Skip(resumed)); // nothing before the keyframe after a resume
    }

    [Fact]
    public async Task The_secure_desktop_pauses_and_recovery_resumes()
    {
        await using var rig = await StartAsync();
        await using var c = await rig.OpenAsync();
        await c.WaitTextAsync("screenOpened");
        rig.Screens.Last.Raise(new ScreenPipelineEvent(ScreenPipelineEventKind.Blocked));
        Assert.Equal("locked", (await c.WaitTextAsync("screenPaused")).GetProperty("reason").GetString());
        var keyRequests = rig.Screens.Last.KeyRequests;
        rig.Screens.Last.Raise(new ScreenPipelineEvent(ScreenPipelineEventKind.Recovered));
        await c.WaitTextAsync("screenResumed");
        Assert.True(rig.Screens.Last.KeyRequests > keyRequests);
    }

    [Fact]
    public async Task Opening_while_locked_says_paused_right_away()
    {
        await using var rig = await StartAsync(locked: true);
        await using var c = await rig.OpenAsync();
        await c.WaitTextAsync("screenOpened");
        Assert.Equal("locked", (await c.WaitTextAsync("screenPaused")).GetProperty("reason").GetString());
        Assert.Empty(rig.Screens.Last.Starts);
    }

    [Fact]
    public async Task A_resolution_change_sends_a_new_opened_then_config_and_keyframe()
    {
        await using var rig = await StartAsync();
        await using var c = await rig.OpenAsync();
        await c.WaitTextAsync("screenOpened");
        await c.NextFrameAsync();
        rig.Screens.Last.Raise(new ScreenPipelineEvent(ScreenPipelineEventKind.SourceChanged, @"\\.\DISPLAY1", new PixelBox(0, 0, 2560, 1440)));
        var seq = await c.SequenceUntilAsync(s => s.IndexOf("screenOpened") is var i && i >= 0 && s.Skip(i).Contains("bin:Keyframe"));
        Assert.Equal("bin:Keyframe", seq[^1]);
        Assert.Equal("bin:Config", seq[^2]);
        Assert.True(seq.LastIndexOf("screenOpened") < seq.Count - 2);
        Assert.Contains(rig.Screens.Last.Starts, s => s.Display == new PixelBox(0, 0, 2560, 1440) && s.OutW == 1920 && s.OutH == 1080);
    }

    [Fact]
    public async Task Switching_display_and_zooming_the_view()
    {
        await using var rig = await StartAsync();
        await using var c = await rig.OpenAsync();
        await c.WaitTextAsync("screenOpened");
        await c.SendAsync("screenDisplay", new { display = @"\\.\DISPLAY2" });
        var opened = await c.WaitTextAsync("screenOpened");
        Assert.Equal(@"\\.\DISPLAY2", opened.GetProperty("display").GetString());
        Assert.Equal((1920, 1080), (opened.GetProperty("w").GetInt32(), opened.GetProperty("h").GetInt32()));
        Assert.Equal(new PixelBox(5120, 0, 1920, 1080), rig.Screens.Last.Starts[^1].Display);

        await c.SendAsync("screenView", new { x = 0.5, y = 0.5, w = 0.5, h = 0.5 });
        Assert.True(await Eventually.TrueAsync(() => { lock (rig.Screens.Last.Starts) return rig.Screens.Last.Starts[^1].Crop == new PixelBox(960, 540, 960, 540); }));
        var requests = rig.Screens.Last.KeyRequests;
        await c.SendAsync("screenKeyframe", new { });
        Assert.True(await Eventually.TrueAsync(() => rig.Screens.Last.KeyRequests > requests));
    }

    [Fact]
    public async Task No_h264_encoder_falls_back_to_jpeg()
    {
        await using var rig = await StartAsync();
        await using var c = await rig.OpenAsync();
        await c.WaitTextAsync("screenOpened");
        rig.Screens.Last.Raise(new ScreenPipelineEvent(ScreenPipelineEventKind.EncoderFailed));
        Assert.Equal("jpeg", (await c.WaitTextAsync("screenOpened")).GetProperty("codec").GetString());
        (ScreenFrameHeader Header, byte[] Payload) f;
        do f = await c.NextFrameAsync(); while (f.Header.Kind != ScreenFrameKind.Jpeg);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, f.Payload[..2]);
        Assert.Equal(10, rig.Screens.Last.Starts[^1].Fps);
    }

    [Fact]
    public async Task Stop_closes_4012_and_refuses_for_a_minute()
    {
        await using var rig = await StartAsync();
        await using var c = await rig.OpenAsync();
        await c.WaitTextAsync("screenOpened");
        rig.Remote.Stop();
        Assert.Equal("stopped", (await c.WaitTextAsync("screenPaused")).GetProperty("reason").GetString());
        Assert.True(await c.WaitClosedAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(4012, (int?)c.CloseStatus);
        await using var again = await rig.OpenAsync();
        Assert.True(await again.WaitClosedAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(4012, (int?)again.CloseStatus);
    }

    [Fact]
    public async Task The_same_phone_reopening_replaces_its_old_session()
    {
        await using var rig = await StartAsync();
        await using var first = await rig.OpenAsync();
        await first.WaitTextAsync("screenOpened");
        await using var second = await rig.OpenAsync();
        await second.WaitTextAsync("screenOpened");
        Assert.True(await first.WaitClosedAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("Fake Pixel", rig.Remote.ViewingPhone);
    }

    [Fact]
    public async Task Focus_changes_reach_the_phone_while_viewing()
    {
        await using var rig = await StartAsync();
        await using var c = await rig.OpenAsync();
        await c.WaitTextAsync("screenOpened");
        rig.Remote.OfferFocusForTest(new FocusFacts(UiaControlType.Edit, true, true, false, false));
        var f = (await rig.Phone.WaitForAsync("remoteFocus")).Data;
        Assert.True(f.GetProperty("text").GetBoolean());
        Assert.True(f.GetProperty("secure").GetBoolean());
        rig.Remote.OfferFocusForTest(new FocusFacts(50000, false, false, false, false));
        Assert.False((await rig.Phone.WaitForAsync("remoteFocus")).Data.GetProperty("text").GetBoolean());
    }
}
