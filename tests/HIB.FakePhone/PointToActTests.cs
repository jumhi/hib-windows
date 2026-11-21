using System.Text.Json;
using HIB.Core.Act;
using HIB.Core.Phone;
using HIB.Core.Roam;
using HIB.Core.Space;

namespace HIB.FakePhone;

/// <summary>Stands in for Windows: records every action instead of doing it.</summary>
sealed class FakeDesktopControl : IDesktopControl
{
    readonly List<string> _log = new();
    public bool IsAvailable { get; set; } = true;
    public bool BrightnessWorks { get; set; } = true;
    public bool? MicMuted { get; private set; } = false;
    public int VolumeLevel { get; set; } = 40;
    public List<string> Log { get { lock (_log) return _log.ToList(); } }
    bool Did(string what) { lock (_log) _log.Add(what); return true; }

    public bool Lock() => Did("lock");
    public bool Screensaver() => Did("screensaver");
    public bool Media(MediaKey key) => Did("media " + key);
    public bool VolumeUp() => Did("volume up");
    public bool VolumeDown() => Did("volume down");
    public bool ToggleMute() => Did("mute");
    public bool SetVolume(int percent) { VolumeLevel = percent; return Did($"volume {percent}"); }
    public int? Volume => VolumeLevel;
    public bool? ToggleMicMute() { Did("mic"); MicMuted = !MicMuted; return MicMuted; }
    public bool Screenshot() => Did("screenshot");
    public bool TaskView() => Did("task view");
    public bool ShowDesktop() => Did("show desktop");
    public string? OpenApp(string name) { Did("open " + name); return name == "Nope" ? $"Couldn't find \"{name}\" on this PC." : null; }
    public bool Brightness(int direction) { Did($"brightness {direction}"); return BrightnessWorks; }
    public void Key(int usage, bool down) => Did($"key {usage:X2} {(down ? "down" : "up")}");
    public void Text(string text) => Did("text " + text);
    public void MouseMove(double dx, double dy) => Did($"move {dx},{dy}");
    public void MouseButton(RoamButton button, bool down) => Did($"button {button} {(down ? "down" : "up")}");
    public void Scroll(double dx, double dy) => Did($"scroll {dx},{dy}");
}

sealed class FakeStats : ISystemStats
{
    public int Samples, Resets;
    public DeckStats Sample() => new(37, 61, "normal", 86400 + Interlocked.Increment(ref Samples), 88, true, 120, 2048);
    public void Reset() => Interlocked.Increment(ref Resets);
}

/// <summary>
/// Point to act on Windows (docs/SPACE.md §11): Desk Deck (deckOpen → deckState, deckAction → deckActionResult), the Remote
/// (remoteControl, macOS keycodes) and macMediaControl answer with the Mac's payloads; flickAim.label reaches the aim light.
/// </summary>
public class PointToActTests
{
    static async Task<(TestHost Host, FakePhone Phone, FakeDesktopControl Pc, FakeStats Stats)> StartAsync(bool available = true)
    {
        var pc = new FakeDesktopControl { IsAvailable = available };
        var stats = new FakeStats();
        var host = new TestHost(name: "PC-INTERN", desktop: available ? () => pc : () => new NullDesktopControl(), stats: () => stats,
            deckInterval: TimeSpan.FromMilliseconds(200));
        var phone = await host.ConnectPhoneAsync();
        return (host, phone, pc, stats);
    }

    static async Task<JsonElement> Result(FakePhone phone, string action)
        => (await phone.WaitForAsync("deckActionResult", match: m => m.Data.GetProperty("action").GetString() == action)).Data;

    [Fact]
    public async Task Desk_deck_sends_the_live_state_every_two_seconds_while_open()
    {
        var (host, phone, _, stats) = await StartAsync();
        await using var _h = host;
        await using var _p = phone;
        await phone.SendAsync("deckOpen", new { open = true });
        var state = (await phone.WaitForAsync("deckState")).Data;
        // The Mac's fields, nothing else (Windows has no calendar to read: no nextEvent).
        Assert.Equal(new[] { "battery", "charging", "cpu", "mem", "memPressure", "netDown", "netUp", "uptime" },
            state.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(37, state.GetProperty("cpu").GetInt32());
        Assert.Equal(61, state.GetProperty("mem").GetInt32());
        Assert.Equal("normal", state.GetProperty("memPressure").GetString());
        Assert.Equal(88, state.GetProperty("battery").GetInt32());
        Assert.True(state.GetProperty("charging").GetBoolean());
        Assert.Equal(120, state.GetProperty("netUp").GetInt32());
        Assert.Equal(2048, state.GetProperty("netDown").GetInt32());
        Assert.True(state.GetProperty("uptime").GetInt64() > 86400);
        Assert.Equal(1, stats.Resets);
        await phone.WaitForAsync("deckState"); // and again
        Assert.True(host.Core.Deck.IsOpen);
        await phone.SendAsync("deckOpen", new { open = false });
        Assert.True(await Eventually.TrueAsync(() => !host.Core.Deck.IsOpen));
        await Task.Delay(100);
        var count = stats.Samples;
        await Task.Delay(500);
        Assert.Equal(count, stats.Samples);
        // The phone leaving closes it too.
        await phone.SendAsync("deckOpen", new { open = true });
        Assert.True(await Eventually.TrueAsync(() => host.Core.Deck.IsOpen));
        await phone.DisposeAsync();
        Assert.True(await Eventually.TrueAsync(() => !host.Core.Deck.IsOpen, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Deck_actions_happen_on_this_pc_and_answer_like_the_mac()
    {
        var (host, phone, pc, _) = await StartAsync();
        await using var _h = host;
        await using var _p = phone;
        async Task Ok(string action, string expect, object? arg = null)
        {
            await phone.SendAsync("deckAction", arg is null ? new { action } : new { action, arg });
            var r = await Result(phone, action);
            Assert.True(r.GetProperty("ok").GetBoolean(), r.GetRawText());
            Assert.False(r.TryGetProperty("error", out _)); // like the Mac: no error key when it worked
            Assert.Contains(expect, pc.Log);
        }
        async Task Fails(string action, string error, object? arg = null)
        {
            await phone.SendAsync("deckAction", arg is null ? new { action } : new { action, arg });
            var r = await Result(phone, action);
            Assert.False(r.GetProperty("ok").GetBoolean());
            Assert.Equal(error, r.GetProperty("error").GetString());
        }
        await Ok("lock", "lock");
        await Ok("screensaver", "screensaver");
        await Ok("play_pause", "media PlayPause");
        await Ok("next", "media Next");
        await Ok("previous", "media Previous");
        await Ok("mute", "mute");
        await Ok("mission_control", "task view");
        await Ok("show_desktop", "show desktop");
        await Ok("screenshot", "screenshot");
        await Ok("mic_mute", "mic");
        Assert.True(pc.MicMuted);
        await Ok("brightness_up", "brightness 1");
        await Ok("open_app", "open Spotify", "Spotify");
        // Volume: the new level follows as macVolume, like the Mac.
        await Ok("vol_up", "volume up");
        Assert.Equal(40, (await phone.WaitForAsync("macVolume")).Data.GetProperty("volume").GetInt32());

        pc.BrightnessWorks = false;
        await Fails("brightness_down", "This PC's display doesn't allow brightness control");
        await Fails("open_app", "No app chosen. Long-press the tile to pick one.");
        await Fails("open_app", "Couldn't find \"Nope\" on this PC.", "Nope");
        await Fails("phone_audio", "Connect the phone with ADB first.");
        await Fails("shortcut", DeskDeck.NotAvailable, "Focus");
        await Fails("dnd", DeskDeck.NotAvailable, "Focus");
        await Fails("frobnicate", DeskDeck.NotAvailable);

        // Phone audio with the scrcpy bridge attached: it toggles.
        var audio = new FakeAudio();
        host.Core.SpaceEngine.AttachAudio(audio);
        await phone.SendAsync("deckAction", new { action = "phone_audio" });
        Assert.True((await Result(phone, "phone_audio")).GetProperty("ok").GetBoolean());
        Assert.Equal(1, audio.Starts);
    }

    [Fact]
    public async Task Elsewhere_every_action_says_it_is_not_available_on_windows()
    {
        var (host, phone, _, _) = await StartAsync(available: false);
        await using var _h = host;
        await using var _p = phone;
        foreach (var action in new[] { "lock", "mic_mute", "brightness_up", "mission_control" })
        {
            await phone.SendAsync("deckAction", new { action });
            var r = await Result(phone, action);
            Assert.False(r.GetProperty("ok").GetBoolean());
            Assert.Equal("Not available on Windows", r.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task The_remote_types_on_this_pc_with_mac_keycodes_translated()
    {
        var (host, phone, pc, _) = await StartAsync();
        await using var _h = host;
        await using var _p = phone;
        // ⌘C from the phone's Mac-style keyboard: Ctrl+C here (keycode 8 = C).
        await phone.SendAsync("remoteControl", new { action = "keypress", keycode = 8, modifiers = new[] { "cmd" } });
        Assert.True(await Eventually.TrueAsync(() => pc.Log.Count >= 4));
        Assert.Equal(new[] { "key E0 down", "key 06 down", "key 06 up", "key E0 up" }, pc.Log.Take(4));
        await phone.SendAsync("remoteControl", new { action = "type", text = "Hi", modifiers = new[] { "shift" } });
        await phone.SendAsync("remoteControl", new { action = "arrow_left" });
        await phone.SendAsync("remoteControl", new { action = "enter" });
        await phone.SendAsync("remoteControl", new { action = "vol_set", value = 30 });
        await phone.SendAsync("remoteControl", new { action = "media_next" });
        await phone.SendAsync("remoteControl", new { action = "mouse_move", dx = 5.0, dy = -3.0 });
        await phone.SendAsync("remoteControl", new { action = "mouse_click", button = "right", isDown = true });
        await phone.SendAsync("remoteControl", new { action = "mouse_scroll", dx = 0.0, dy = 12.0 });
        await phone.SendAsync("remoteControl", new { action = "lock_screen" });
        Assert.True(await Eventually.TrueAsync(() => pc.Log.Contains("lock")));
        var log = pc.Log;
        Assert.Contains("text Hi", log);
        Assert.True(log.IndexOf("key E1 down") < log.IndexOf("text Hi") && log.IndexOf("text Hi") < log.IndexOf("key E1 up"));
        Assert.Contains("key 50 down", log);
        Assert.Contains("key 28 down", log);
        Assert.Contains("volume 30", log);
        Assert.Contains("media Next", log);
        Assert.Contains("move 5,-3", log);
        Assert.Contains("button Right down", log);
        Assert.Contains("scroll 0,12", log);
        Assert.Equal(30, (await phone.WaitForAsync("macVolume")).Data.GetProperty("volume").GetInt32());

        // The table: macOS virtual keycodes → USB HID usages (letters, digits, arrows, keypad, ⌘ as Ctrl).
        Assert.Equal(0x04, RoamKeys.FromMacKeycode(0x00)); // A
        Assert.Equal(0x1D, RoamKeys.FromMacKeycode(0x06)); // Z
        Assert.Equal(0x27, RoamKeys.FromMacKeycode(0x1D)); // 0
        Assert.Equal(0x2A, RoamKeys.FromMacKeycode(51));   // Delete = Backspace
        Assert.Equal(0x29, RoamKeys.FromMacKeycode(53));   // Esc
        Assert.Equal(0x50, RoamKeys.FromMacKeycode(123));  // ←
        Assert.Equal(0x52, RoamKeys.FromMacKeycode(126));  // ↑
        Assert.Equal(0x4C, RoamKeys.FromMacKeycode(0x75)); // forward delete
        Assert.Equal(0x62, RoamKeys.FromMacKeycode(0x52)); // keypad 0
        Assert.Equal(RoamKeys.LeftCtrl, RoamKeys.FromMacKeycode(0x37)); // ⌘
        Assert.Null(RoamKeys.FromMacKeycode(0x3F)); // fn
        foreach (var k in Enumerable.Range(0, 0x80))
            if (RoamKeys.FromMacKeycode(k) is { } usage) Assert.NotNull(RoamKeys.ToWindows(usage)); // every one can be typed here
    }

    [Fact]
    public async Task Media_controls_answer_with_macMediaControlResponse()
    {
        var (host, phone, pc, _) = await StartAsync();
        await using var _h = host;
        await using var _p = phone;
        foreach (var (action, expect) in new[] { ("playPause", "media PlayPause"), ("play", "media PlayPause"), ("next", "media Next"), ("previous", "media Previous"), ("stop", "media Stop") })
        {
            await phone.SendAsync("macMediaControl", new { action });
            var r = (await phone.WaitForAsync("macMediaControlResponse", match: m => m.Data.GetProperty("action").GetString() == action)).Data;
            Assert.True(r.GetProperty("success").GetBoolean());
            Assert.Contains(expect, pc.Log);
        }
        await phone.SendAsync("macMediaControl", new { action = "vol_up" });
        Assert.True((await phone.WaitForAsync("macMediaControlResponse", match: m => m.Data.GetProperty("action").GetString() == "vol_up")).Data.GetProperty("success").GetBoolean());
        await phone.SendAsync("macMediaControl", new { action = "warp" });
        Assert.False((await phone.WaitForAsync("macMediaControlResponse", match: m => m.Data.GetProperty("action").GetString() == "warp")).Data.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void System_stats_read_battery_and_memory_pressure_like_the_mac()
    {
        Assert.Equal((-1, false), SystemStats.BatteryFrom(1, 128, 255)); // a desktop: no battery
        Assert.Equal((-1, false), SystemStats.BatteryFrom(1, 255, 50));  // unknown
        Assert.Equal((64, true), SystemStats.BatteryFrom(1, 8, 64));     // charging
        Assert.Equal((100, true), SystemStats.BatteryFrom(1, 1, 100));   // on AC and full
        Assert.Equal((40, false), SystemStats.BatteryFrom(0, 0, 40));
        Assert.Equal("normal", SystemStats.PressureFor(60));
        Assert.Equal("warning", SystemStats.PressureFor(88));
        Assert.Equal("critical", SystemStats.PressureFor(97));
        var s = new SystemStats().Sample(); // works anywhere (zeros where the system can't say)
        Assert.InRange(s.Cpu, 0, 100);
        Assert.True(s.Uptime > 0);
    }

    [Fact]
    public async Task FlickAim_label_names_what_the_pointed_screen_will_get()
    {
        await using var host = new TestHost();
        var main = new DisplayScreen(@"\\.\DISPLAY1", "Odyssey OLED G9", true, 0, 0, 5120, 1440);
        host.Core.Aim.SetScreens(new[] { main });
        await using var phone = await host.ConnectPhoneAsync();
        var changes = 0;
        host.Core.Aim.LitChanged += _ => Interlocked.Increment(ref changes);
        await phone.SendAsync("flickAim", new { screen = main.Id, label = "Desk Deck" });
        Assert.True(await Eventually.TrueAsync(() => host.Core.Aim.LitLabel == "Desk Deck"));
        Assert.Equal(main, host.Core.Aim.Lit);
        // Same screen, other words: the pill changes; no label: "Flick lands here" again.
        await phone.SendAsync("flickAim", new { screen = main.Id, label = "Remote" });
        Assert.True(await Eventually.TrueAsync(() => host.Core.Aim.LitLabel == "Remote"));
        Assert.True(changes >= 2);
        await phone.SendAsync("flickAim", new { screen = main.Id });
        Assert.True(await Eventually.TrueAsync(() => host.Core.Aim.LitLabel is null));
        var t = new AimTracker();
        t.SetScreens(new[] { main });
        t.ApplyAim(main.Id, true, DateTime.UtcNow, "  " + new string('x', 60) + " ");
        Assert.Equal(AimTracker.MaxLabel + 1, t.Label!.Length); // trimmed and shortened with an ellipsis
    }
}
