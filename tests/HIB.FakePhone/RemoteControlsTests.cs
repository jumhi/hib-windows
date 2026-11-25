using HIB.Core.Act;
using HIB.Core.Phone;
using HIB.Core.Remote;
using HIB.Core.Roam;

namespace HIB.FakePhone;

/// <summary>
/// Phone Remote, round 2 on Windows (docs/SPACE.md §15.5), the pure rules: fractions of a display → desktop pixels →
/// SendInput's virtual-desktop coordinates, held keys and when they are let go, ⌘Tab → Alt+Tab, and ⌘ combos.
/// </summary>
public class RemoteControlsLogicTests
{
    // A G9 in the middle (main), a 1080p panel on the left lower down, a 1440p one above: negative origins on both axes.
    static readonly RemoteDisplay G9 = new(@"\\.\DISPLAY1", "Odyssey G9", new PixelBox(0, 0, 5120, 1440), true);
    static readonly RemoteDisplay Left = new(@"\\.\DISPLAY2", "Dell", new PixelBox(-1920, 200, 1920, 1080), false);
    static readonly RemoteDisplay Above = new(@"\\.\DISPLAY3", "LG", new PixelBox(800, -1440, 2560, 1440), false);
    static readonly RemoteDisplay[] All = { Left, G9, Above };

    [Fact]
    public void Fractions_land_on_the_named_display_and_empty_means_main()
    {
        Assert.Equal((2560, 720), RemotePointer.Target(0.5, 0.5, "", All));
        Assert.Equal((2560, 720), RemotePointer.Target(0.5, 0.5, null, All));
        Assert.Equal((-1920, 200), RemotePointer.Target(0, 0, @"\\.\DISPLAY2", All));
        Assert.Equal((-960, 740), RemotePointer.Target(0.5, 0.5, @"\\.\DISPLAY2", All));
        Assert.Equal((-1, 1279), RemotePointer.Target(1, 1, @"\\.\DISPLAY2", All)); // the last pixel, not the next display's first
        Assert.Equal((800 + 1075, -1440 + 244), RemotePointer.Target(0.42, 0.17, @"\\.\DISPLAY3", All));
        // Outside 0…1 is clamped to the display; an unknown display is the main one (as screenOpen); NaN does nothing.
        Assert.Equal((-1920, 1279), RemotePointer.Target(-0.3, 7, @"\\.\DISPLAY2", All));
        Assert.Equal((0, 0), RemotePointer.Target(0, 0, "gone", All));
        Assert.Null(RemotePointer.Target(double.NaN, 0.5, "", All));
        Assert.Null(RemotePointer.Target(0.5, double.PositiveInfinity, "", All));
        Assert.Null(RemotePointer.Target(0.5, 0.5, "", Array.Empty<RemoteDisplay>()));
        // No display marked main: the first one.
        Assert.Equal((-1920, 200), RemotePointer.Target(0, 0, "", new[] { Left with { Main = false }, Above }));
    }

    [Fact]
    public void Pixels_become_virtual_desktop_coordinates_that_land_on_the_same_pixel()
    {
        var desk = RemotePointer.VirtualDesk(All.Select(d => d.Bounds));
        Assert.Equal(new PixelBox(-1920, -1440, 7040, 2880), desk);
        // Corners of the virtual desktop and of every display, plus a sweep: Windows' own rounding gives the pixel back.
        var points = new List<(int, int)> { (-1920, -1440), (5119, 1439), (0, 0), (-1, 1279), (800, -1440), (3359, -1) };
        foreach (var d in All)
            for (var fx = 0.0; fx <= 1.0; fx += 0.05)
            for (var fy = 0.0; fy <= 1.0; fy += 0.25)
                points.Add(RemotePointer.Target(fx, fy, d.Id, All)!.Value);
        foreach (var (x, y) in points)
        {
            var (nx, ny) = RemotePointer.Normalize(x, y, desk);
            Assert.InRange(nx, 0, 65535);
            Assert.InRange(ny, 0, 65535);
            Assert.Equal((x, y), RemotePointer.Denormalize(nx, ny, desk));
        }
        var (ox, oy) = RemotePointer.Normalize(-5000, -5000, desk); // off the desktop: its edge
        Assert.Equal((-1920, -1440), RemotePointer.Denormalize(ox, oy, desk));
        var (ex, ey) = RemotePointer.Normalize(99999, 99999, desk);
        Assert.Equal((5119, 1439), RemotePointer.Denormalize(ex, ey, desk));
        // One 4K display at the origin (the common case).
        var one = new PixelBox(0, 0, 3840, 2160);
        Assert.Equal((1920, 1080), RemotePointer.Denormalize(RemotePointer.Normalize(1920, 1080, one).Nx, RemotePointer.Normalize(1920, 1080, one).Ny, one));
    }

    sealed class Keys
    {
        public readonly List<string> Log = new();
        public readonly RemoteHeldInput Held;
        public Keys(bool cmdAsCtrl = true)
        {
            Held = new RemoteHeldInput((u, d) => Log.Add($"{u:X2}{(d ? "↓" : "↑")}"), (b, d) => Log.Add($"{b}{(d ? "↓" : "↑")}")) { CmdAsCtrl = cmdAsCtrl };
        }
        public List<string> Take() { var l = Log.ToList(); Log.Clear(); return l; }
    }

    const int Cmd = 0x37, Shift = 0x38, Option = 0x3A, Control = 0x3B, Tab = 0x30, S = 0x01, F = 0x03, Z = 0x06, Q = 0x0C, A = 0x00;

    [Fact]
    public void Held_keys_go_down_and_up_once_and_release_all_lets_go_in_reverse()
    {
        var k = new Keys();
        k.Held.Key(Cmd, true);
        k.Held.Key(Shift, true);
        k.Held.Key(Cmd, true); // the phone said it twice: a modifier doesn't repeat
        k.Held.Key(A, true);
        k.Held.Key(A, true);   // an ordinary key does (auto-repeat)
        Assert.Equal(new[] { "E0↓", "E1↓", "04↓", "04↓" }, k.Take());
        k.Held.Key(A, false);
        k.Held.Key(A, false);  // already up: nothing
        k.Held.Key(Q, false);  // never down: nothing
        Assert.Equal(new[] { "04↑" }, k.Take());
        Assert.Equal(new[] { RoamKeys.LeftCtrl, RoamKeys.LeftShift }, k.Held.HeldUsages);
        k.Held.Button(RoamButton.Left, true); // a drag in progress
        Assert.Equal(3, k.Held.ReleaseAll());
        Assert.Equal(new[] { "Left↓", "E1↑", "E0↑", "Left↑" }, k.Take());
        Assert.False(k.Held.AnyHeld);
        Assert.Equal(0, k.Held.ReleaseAll());
        Assert.Empty(k.Take());
        k.Held.Key(0x3F, true); // fn: no key here
        Assert.Empty(k.Take());
        Assert.False(k.Held.AnyHeld);
    }

    [Fact]
    public void Held_keys_are_let_go_after_ten_seconds_without_any_remote_control()
    {
        var k = new Keys();
        var t0 = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        k.Held.Touch(t0);
        Assert.False(k.Held.IdleExpired(t0.AddMinutes(5))); // nothing held: nothing to do
        k.Held.Key(Option, true);
        Assert.False(k.Held.IdleExpired(t0.AddSeconds(9.9)));
        k.Held.Touch(t0.AddSeconds(9)); // any remoteControl (a mouse move too) counts
        Assert.False(k.Held.IdleExpired(t0.AddSeconds(18)));
        Assert.True(k.Held.IdleExpired(t0.AddSeconds(19)));
        Assert.Equal(TimeSpan.FromSeconds(10), RemoteHeldInput.IdleRelease);
        k.Held.Key(Option, false);
        Assert.False(k.Held.IdleExpired(t0.AddHours(1)));
    }

    [Fact]
    public void Held_command_and_tab_is_alt_tab_so_the_switcher_stays_open()
    {
        var k = new Keys();
        k.Held.Key(Cmd, true);
        Assert.Equal(new[] { "E0↓" }, k.Take()); // alone, ⌘ is Ctrl (⌘C, ⌘V… while held)
        k.Held.Key(Tab, true);
        k.Held.Key(Tab, false);
        Assert.Equal(new[] { "E0↑", "E2↓", "2B↓", "2B↑" }, k.Take()); // Ctrl becomes Alt before Tab
        k.Held.Key(Tab, true);
        k.Held.Key(Tab, false);
        Assert.Equal(new[] { "2B↓", "2B↑" }, k.Take()); // next app: Alt stays down
        k.Held.Key(Shift, true);
        k.Held.Key(Tab, true);
        k.Held.Key(Tab, false);
        k.Held.Key(Shift, false);
        Assert.Equal(new[] { "E1↓", "2B↓", "2B↑", "E1↑" }, k.Take()); // ⇧⌘Tab = Shift+Alt+Tab: back one
        k.Held.Key(Cmd, false);
        Assert.Equal(new[] { "E2↑" }, k.Take()); // letting go of ⌘ lets go of Alt: the switcher picks
        Assert.False(k.Held.AnyHeld);

        // Right ⌘ too; a keypress Tab while ⌘ is held does the same, without pressing Alt twice.
        k.Held.Key(0x36, true);
        k.Held.Press(Tab, new[] { "command" });
        k.Held.ReleaseAll();
        Assert.Equal(new[] { "E4↓", "E4↑", "E6↓", "2B↓", "2B↑", "E6↑" }, k.Take());

        // A one-shot ⌘Tab keypress (the three-finger swipe): Alt+Tab.
        k.Held.Press(Tab, new[] { "command" });
        Assert.Equal(new[] { "E2↓", "2B↓", "2B↑", "E2↑" }, k.Take());
        // Ctrl+Tab named as Ctrl stays Ctrl+Tab (browser tabs).
        k.Held.Press(Tab, new[] { "ctrl" });
        Assert.Equal(new[] { "E0↓", "2B↓", "2B↑", "E0↑" }, k.Take());
    }

    [Fact]
    public void With_command_as_ctrl_off_command_is_the_windows_key_and_tab_is_left_alone()
    {
        var k = new Keys(cmdAsCtrl: false);
        k.Held.Key(Cmd, true);
        k.Held.Key(Tab, true);
        k.Held.Key(Tab, false);
        k.Held.Key(Cmd, false);
        Assert.Equal(new[] { "E3↓", "2B↓", "2B↑", "E3↑" }, k.Take()); // Win+Tab: Task View
        k.Held.Key(Control, true);
        k.Held.Key(Control, false);
        Assert.Equal(new[] { "E0↓", "E0↑" }, k.Take()); // ⌃ is Ctrl
        k.Held.Press(S, new[] { "command" });
        Assert.Equal(new[] { "E3↓", "16↓", "16↑", "E3↑" }, k.Take());
    }

    [Fact]
    public void Command_combos_are_ctrl_combos_and_held_modifiers_are_not_pressed_twice()
    {
        var k = new Keys();
        k.Held.Press(S, new[] { "command" });
        Assert.Equal(new[] { "E0↓", "16↓", "16↑", "E0↑" }, k.Take()); // ⌘S → Ctrl+S
        k.Held.Press(F, new[] { "cmd" });
        Assert.Equal(new[] { "E0↓", "09↓", "09↑", "E0↑" }, k.Take()); // ⌘F → Ctrl+F
        k.Held.Press(Z, new[] { "shift", "command" });
        Assert.Equal(new[] { "E1↓", "E0↓", "1D↓", "1D↑", "E0↑", "E1↑" }, k.Take()); // ⇧⌘Z
        k.Held.Press(Q, new[] { "alt", "command", "COMMAND", "fn" });
        Assert.Equal(new[] { "E2↓", "E0↓", "14↓", "14↑", "E0↑", "E2↑" }, k.Take()); // repeats and fn dropped
        k.Held.Press(0x76, new[] { "option" }); // Alt+F4 (the phone's "⌘Q / Alt+F4" on Windows)
        Assert.Equal(new[] { "E2↓", "3D↓", "3D↑", "E2↑" }, k.Take());

        // ⌘ held on the key strip, then S as a keypress with ⌘ named too: Ctrl stays down, S taps.
        k.Held.Key(Cmd, true);
        k.Held.Press(S, new[] { "command" });
        k.Held.Press(F, Array.Empty<string>());
        Assert.Equal(new[] { "E0↓", "16↓", "16↑", "09↓", "09↑" }, k.Take());
        Assert.Equal(new[] { RoamKeys.LeftCtrl }, k.Held.HeldUsages);
        k.Held.Key(Cmd, false);
        Assert.Equal(new[] { "E0↑" }, k.Take());

        // ⌃ (the phone's ⌃/Win key) is the Windows key: ⌃ tapped alone opens Start, ⌃+D shows the desktop.
        k.Held.Key(Control, true);
        k.Held.Key(0x02, true);
        k.Held.Key(0x02, false);
        k.Held.Key(Control, false);
        Assert.Equal(new[] { "E3↓", "07↓", "07↑", "E3↑" }, k.Take());
        Assert.Equal(RoamKeys.LeftGui, RemoteKeyMap.Usage(Control, cmdAsCtrl: true));
        Assert.Equal(RoamKeys.LeftCtrl, RemoteKeyMap.Usage(Cmd, cmdAsCtrl: true));
        Assert.Null(RemoteKeyMap.Usage(0x3F, cmdAsCtrl: true));
        foreach (var code in Enumerable.Range(0, 0x80))
            if (RemoteKeyMap.Usage(code, true) is { } u) Assert.NotNull(RoamKeys.ToWindows(u));
    }
}

/// <summary>§15.5 against a running core: absolute moves, held keys in order, and every way they get let go.</summary>
public class RemoteControlsTests
{
    // Mixed DPI: the main 4K panel at 150 % and a 1080p panel to its left at 100 % (DIPs here, pixels in the hello).
    static readonly DisplayScreen Main = new(@"\\.\DISPLAY1", "Main", true, 0, 0, 2560, 1440) { PxW = 3840, PxH = 2160, Scale = 1.5 };
    static readonly DisplayScreen LeftPanel = new(@"\\.\DISPLAY2", "Left", false, -1920, 0, 1920, 1080) { PxW = 1920, PxH = 1080, Scale = 1 };

    static async Task<(TestHost Host, FakePhone Phone, FakeDesktopControl Pc)> StartAsync()
    {
        var pc = new FakeDesktopControl();
        var host = new TestHost(name: "PC-INTERN", desktop: () => pc);
        host.Core.Aim.SetScreens(new[] { Main, LeftPanel });
        var phone = await host.ConnectPhoneAsync();
        return (host, phone, pc);
    }

    [Fact]
    public async Task Mouse_move_abs_puts_the_pointer_on_that_displays_pixel()
    {
        var (host, phone, pc) = await StartAsync();
        await using var _h = host;
        await using var _p = phone;
        await phone.SendAsync("remoteControl", new { action = "mouse_move_abs", x = 0.5, y = 0.5, display = "" });
        await phone.SendAsync("remoteControl", new { action = "mouse_move_abs", x = 0.25, y = 1.0, display = @"\\.\DISPLAY2" });
        await phone.SendAsync("remoteControl", new { action = "mouse_move_abs", x = 0.1, y = 0.1, display = "unplugged" });
        Assert.True(await Eventually.TrueAsync(() => pc.Log.Count(l => l.StartsWith("moveTo")) >= 3));
        Assert.Equal(new[] { "moveTo 1920,1080", "moveTo -1440,1079", "moveTo 384,216" }, pc.Log.Where(l => l.StartsWith("moveTo")));
    }

    [Fact]
    public async Task Keys_arrive_in_order_and_command_s_is_ctrl_s()
    {
        var (host, phone, pc) = await StartAsync();
        await using var _h = host;
        await using var _p = phone;
        await phone.SendAsync("remoteControl", new { action = "keypress", keycode = 1, modifiers = new[] { "command" } });
        await phone.SendAsync("remoteControl", new { action = "keypress", keycode = 3, modifiers = new[] { "command" } });
        for (var i = 0; i < 60; i++)
        {
            await phone.SendAsync("remoteControl", new { action = "key", keycode = 0x37, isDown = true });
            await phone.SendAsync("remoteControl", new { action = "key", keycode = 0x30, isDown = true });
            await phone.SendAsync("remoteControl", new { action = "key", keycode = 0x30, isDown = false });
            await phone.SendAsync("remoteControl", new { action = "key", keycode = 0x37, isDown = false });
        }
        Assert.True(await Eventually.TrueAsync(() => pc.Log.Count >= 8 + 60 * 6));
        var log = pc.Log;
        Assert.Equal(new[] { "key E0 down", "key 16 down", "key 16 up", "key E0 up", "key E0 down", "key 09 down", "key 09 up", "key E0 up" }, log.Take(8));
        var cycle = new[] { "key E0 down", "key E0 up", "key E2 down", "key 2B down", "key 2B up", "key E2 up" };
        for (var i = 0; i < 60; i++) Assert.Equal(cycle, log.Skip(8 + i * 6).Take(6));
        Assert.Empty(host.Core.Deck.Held.Keys);
    }

    [Fact]
    public async Task Held_keys_are_let_go_when_the_phone_link_drops()
    {
        var (host, phone, pc) = await StartAsync();
        await using var _h = host;
        await phone.SendAsync("remoteControl", new { action = "key", keycode = 0x37, isDown = true });
        await phone.SendAsync("remoteControl", new { action = "key", keycode = 0x38, isDown = true });
        await phone.SendAsync("remoteControl", new { action = "mouse_click", button = "left", isDown = true });
        Assert.True(await Eventually.TrueAsync(() => host.Core.Deck.Held.Keys.Count == 2 && host.Core.Deck.Held.Buttons.Count == 1));
        await phone.DisposeAsync();
        Assert.True(await Eventually.TrueAsync(() => host.Core.Deck.Held.Keys.Count == 0));
        var log = pc.Log;
        Assert.Equal(new[] { "key E1 up", "key E0 up", "button Left up" }, log.Skip(log.IndexOf("button Left down") + 1));
    }

    [Fact]
    public async Task Held_keys_are_let_go_after_ten_quiet_seconds_and_when_control_is_turned_off()
    {
        var (host, phone, pc) = await StartAsync();
        await using var _h = host;
        await using var _p = phone;
        var now = DateTime.UtcNow;
        host.Core.Deck.Clock = () => now;
        await phone.SendAsync("remoteControl", new { action = "key", keycode = 0x3A, isDown = true });
        Assert.True(await Eventually.TrueAsync(() => host.Core.Deck.Held.Keys.Count == 1));
        now += TimeSpan.FromSeconds(6);
        await phone.SendAsync("remoteControl", new { action = "mouse_move", dx = 1.0, dy = 0.0 }); // still busy on the phone
        Assert.True(await Eventually.TrueAsync(() => pc.Log.Contains("move 1,0")));
        now += TimeSpan.FromSeconds(9);
        host.Core.Deck.CheckHeld();
        Assert.Single(host.Core.Deck.Held.Keys);
        now += TimeSpan.FromSeconds(1.5);
        host.Core.Deck.CheckHeld();
        Assert.Empty(host.Core.Deck.Held.Keys);
        Assert.Equal("key E2 up", pc.Log[^1]);

        // Turning "Allow viewing and control" off: the next key from the phone does nothing and lets go of what was held.
        await phone.SendAsync("remoteControl", new { action = "key", keycode = 0x38, isDown = true });
        Assert.True(await Eventually.TrueAsync(() => host.Core.Deck.Held.Keys.Count == 1));
        host.Core.Remote.Allowed = false;
        await phone.SendAsync("remoteControl", new { action = "key", keycode = 0x01, isDown = true });
        Assert.True(await Eventually.TrueAsync(() => host.Core.Deck.Held.Keys.Count == 0));
        Assert.Equal("key E1 up", pc.Log[^1]);
        Assert.DoesNotContain("key 16 down", pc.Log);
        host.Core.Remote.Allowed = true;
    }
}
