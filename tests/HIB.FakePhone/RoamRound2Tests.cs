using System.Text.Json;
using HIB.Core.Roam;
using HIB.Core.Space;

namespace HIB.FakePhone;

/// <summary>Stands in for Windows' system services (SPACE.md §12): Mouse Keys, the window in front, keep awake, lock.</summary>
sealed class FakeRoamSystem : IRoamSystem
{
    readonly object _gate = new();
    public bool RealMouse { get; set; } = true;
    public MouseKeysState Keys { get; set; } = new(0x00000062, 80, 1000);
    public List<MouseKeysState> Sets { get; } = new();
    public bool? Awake { get; private set; }
    public int Locks;
    public RoamForeground? Front { get; set; }
    public int FrontReads;

    /// <summary>Like Windows: Mouse Keys on counts as a mouse.</summary>
    public bool MousePresent => RealMouse || Keys.IsOn;
    public MouseKeysState? GetMouseKeys() => Keys;
    public bool SetMouseKeys(MouseKeysState state)
    {
        lock (_gate) Sets.Add(state);
        Keys = state;
        return true;
    }
    public RoamForeground? Foreground()
    {
        Interlocked.Increment(ref FrontReads);
        return Front;
    }
    public void KeepAwake(bool on) => Awake = on;
    public void LockScreen() => Interlocked.Increment(ref Locks);
    public event Action? DevicesChanged;
    public event Action<bool>? SessionLockChanged;
    public void RaiseDevices() => DevicesChanged?.Invoke();
    public void RaiseLock(bool locked) => SessionLockChanged?.Invoke(locked);
    public void Dispose() { }
}

/// <summary>
/// Roam round 2 (docs/SPACE.md §12): Mouse Keys for a PC without a mouse, the UIPI guard and key counters, the send path and
/// the input thread, game mode (detection, gate and double push, ctlRel), hotkeys, lock together, speeds, clipboard images,
/// latency and "Get log".
/// </summary>
public class RoamRound2Tests
{
    static readonly RoamDisplay Pc = new(@"\\.\DISPLAY1", new RoamRect(0, 0, 1920, 1080), 1, true);
    static readonly RoamRect MacScreen = new(0, 0, 1512, 982);
    static readonly byte[] SpaceKey = RoamTests.SpaceKey;

    static RoamNeighbor Mac(RoamGameMode game = RoamGameMode.Normal, double speed = 1, double scroll = 1, bool natural = false)
        => new("B Mac", "B Mac", RoamSide.Right, new[] { MacScreen }, "mac", true, Game: game, PointerSpeed: speed, ScrollSpeed: scroll, NaturalScroll: natural);

    sealed class Clock
    {
        public DateTime Now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(double ms) => Now = Now.AddMilliseconds(ms);
    }

    static (RoamController c, RecordingHost h, Clock clock) Controller(params RoamNeighbor[] neighbors)
    {
        var clock = new Clock();
        var h = new RecordingHost();
        var c = new RoamController(h, () => clock.Now) { LocalName = "Office PC" };
        c.SetDisplays(new[] { Pc });
        c.SetNeighbors(neighbors);
        return (c, h, clock);
    }

    static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "hib-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    // ---------------- §12.1 Mouse Keys ----------------

    [Fact]
    public void Mouse_keys_turn_on_only_without_a_mouse_and_always_come_back_even_after_a_crash()
    {
        var dir = TempDir();
        var sys = new FakeRoamSystem { RealMouse = false };
        var before = sys.Keys;
        var keeper = new MouseKeysKeeper(sys, dir);

        // Driven with no mouse: on (Deskflow's flags), and the user's setting is on disk first.
        keeper.SetControlled(true);
        Assert.True(keeper.Active);
        Assert.Equal(MouseKeysState.Available | MouseKeysState.On | MouseKeysState.ReplaceNumbers, sys.Keys.Flags);
        Assert.Equal(before.MaxSpeed, sys.Keys.MaxSpeed);
        Assert.True(File.Exists(Path.Combine(dir, MouseKeysKeeper.FileName)));
        keeper.SetControlled(true); // again: nothing more
        Assert.Single(sys.Sets);

        // Released: the user's own setting, and the file is gone.
        keeper.SetControlled(false);
        Assert.False(keeper.Active);
        Assert.Equal(before, sys.Keys);
        Assert.False(File.Exists(Path.Combine(dir, MouseKeysKeeper.FileName)));

        // A crash while it was on: the next start puts it back.
        keeper.SetControlled(true);
        var afterCrash = new MouseKeysKeeper(sys, dir);
        Assert.True(afterCrash.RecoverAfterCrash());
        Assert.Equal(before, sys.Keys);
        Assert.False(File.Exists(Path.Combine(dir, MouseKeysKeeper.FileName)));
        Assert.False(afterCrash.RecoverAfterCrash()); // nothing left to recover

        // With a mouse: never touched.
        var withMouse = new FakeRoamSystem { RealMouse = true };
        var k2 = new MouseKeysKeeper(withMouse, TempDir());
        k2.SetControlled(true);
        Assert.Empty(withMouse.Sets);

        // The mouse is unplugged while driven (WM_DEVICECHANGE): on now; turned off behind our back: on again.
        withMouse.RealMouse = false;
        k2.DevicesChanged();
        Assert.True(k2.Active);
        withMouse.Keys = withMouse.Keys with { Flags = 0 };
        k2.DevicesChanged();
        Assert.True(withMouse.Keys.IsOn);
        k2.Restore();
        Assert.False(withMouse.Keys.IsOn);
        Assert.Equal(0x62u, withMouse.Keys.Flags);
    }

    [Fact]
    public async Task A_pc_without_a_mouse_shows_the_pointer_and_stays_awake_while_driven_and_recovers_after_a_crash()
    {
        var root = Path.Combine(Path.GetTempPath(), "hib-tests", Guid.NewGuid().ToString("N")[..8]);
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        // The last run died while it held Mouse Keys on.
        File.WriteAllText(Path.Combine(data, MouseKeysKeeper.FileName), """{"Flags":98,"MaxSpeed":80,"TimeToMaxSpeed":1000}""");
        var sys = new FakeRoamSystem { RealMouse = false, Keys = new MouseKeysState(0x83, 80, 1000) };
        var pc = new FakeRoamPlatform();
        await using var host = new TestHost(name: "Office PC", root: root, space: RoamTests.FastSpace(),
            roam: new RoamOptions { Platform = () => pc, System = () => sys });
        host.Core.SpaceEngine.SetSpaceKey(SpaceKey);
        host.Core.Roam.SetDisplays(new[] { Pc });
        Assert.True(host.Core.Roam.WaitChain(TimeSpan.FromSeconds(2)));
        Assert.Equal(0x62u, sys.Keys.Flags); // put back at start
        Assert.False(File.Exists(Path.Combine(data, MouseKeysKeeper.FileName)));

        await using var mac = await RawMac.DialAsync(host.Core.Link.Port, SpaceKey);
        await mac.SendRawAsync(RoamUiTests.MacHello);
        Assert.True(await Eventually.TrueAsync(() => host.Core.Roam.StatusFor("A Mac") is { Ready: true }));
        await mac.SendRawAsync("""{"type":"ctlEnter","data":{"x":0,"y":420,"clipboard":null}}""");
        Assert.True(await Eventually.TrueAsync(() => host.Core.Roam.MouseKeysHeld && sys.Awake == true));
        Assert.True(sys.Keys.IsOn);
        Assert.True(File.Exists(Path.Combine(data, MouseKeysKeeper.FileName)));

        await mac.SendRawAsync("""{"type":"ctlLeave","data":{"clipboard":null}}""");
        Assert.True(await Eventually.TrueAsync(() => !host.Core.Roam.MouseKeysHeld && sys.Awake == false));
        Assert.Equal(0x62u, sys.Keys.Flags);
        Assert.False(File.Exists(Path.Combine(data, MouseKeysKeeper.FileName)));
    }

    // ---------------- §12.2 keys reach the target, or say why ----------------

    [Fact]
    public void Uipi_guard_tells_once_per_admin_app_and_never_for_hib_or_normal_apps()
    {
        var g = new UipiGuard();
        var screen = new RoamRect(0, 0, 1920, 1080);
        RoamForeground App(string name, bool? elevated, bool own = false) => new(1, 42, name, own, screen, screen, Elevated: elevated);
        Assert.Null(g.Check(null));
        Assert.Null(g.Check(App("notepad.exe", false)));
        Assert.Null(g.Check(App("notepad.exe", null))); // unknown: no pill
        Assert.Null(g.Check(App("H.I.B", true, own: true)));
        Assert.Equal("powershell.exe", g.Check(App("powershell.exe", true)));
        Assert.Null(g.Check(App("powershell.exe", true))); // once per app
        Assert.Null(g.Check(App("PowerShell.EXE", true)));
        Assert.Equal("Taskmgr.exe", g.Check(App("Taskmgr.exe", true)));
        Assert.True(UipiGuard.Blocks(App("x.exe", true)));
        Assert.False(UipiGuard.Blocks(App("x.exe", false)));
    }

    [Fact]
    public void Key_counters_flush_every_10_seconds_with_codes_but_no_characters()
    {
        var stats = new RoamKeyStats();
        var t = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        Assert.Null(stats.Flush(t)); // starts the period
        for (var i = 0; i < 4; i++) stats.Captured();
        for (var i = 0; i < 4; i++) stats.Sent();
        stats.Received();
        stats.Injected();
        stats.Unmapped(RoamKeyStats.UsageCode(RoamKeys.BrightnessUp));
        stats.Unmapped(RoamKeyStats.UsageCode(RoamKeys.BrightnessUp));
        stats.Unmapped(RoamKeyStats.WindowsCode(0xFF, 0x00, false));
        stats.Failed();
        Assert.Null(stats.Flush(t.AddSeconds(9)));
        var line = stats.Flush(t.AddSeconds(10))!;
        Assert.Equal("Roam keys (10 s): captured 4 · sent 4 · received 1 · injected 1 · unmapped 3 · failed 1 · unmapped: usage 0xC006F, vk 0xFF sc 0x00", line);
        Assert.Null(stats.Flush(t.AddSeconds(25))); // nothing happened since
    }

    [Fact]
    public async Task Keys_from_a_mac_are_counted_typed_and_repeated_and_an_admin_app_in_front_says_so()
    {
        var pc = new FakeRoamPlatform();
        var sys = new FakeRoamSystem();
        await using var host = new TestHost(name: "Office PC", space: RoamTests.FastSpace(), roam: new RoamOptions { Platform = () => pc, System = () => sys });
        host.Core.SpaceEngine.SetSpaceKey(SpaceKey);
        host.Core.Roam.SetDisplays(new[] { Pc });
        var pills = new List<RoamPill?>();
        host.Core.Roam.PillChanged += p => { lock (pills) pills.Add(p); };
        await using var mac = await RawMac.DialAsync(host.Core.Link.Port, SpaceKey);
        await mac.SendRawAsync(RoamUiTests.MacHello);
        Assert.True(await Eventually.TrueAsync(() => host.Core.Roam.StatusFor("A Mac") is { Ready: true }));
        await mac.SendRawAsync("""{"type":"ctlEnter","data":{"x":0,"y":420,"clipboard":null}}""");

        // §12.5 key repeat: every repeated down is typed (Windows doesn't repeat injected keys itself).
        for (var i = 0; i < 3; i++) await mac.SendRawAsync("""{"type":"ctlKey","data":{"usage":4,"down":true}}""");
        await mac.SendRawAsync("""{"type":"ctlKey","data":{"usage":4,"down":false}}""");
        // Brightness has no key on Windows: counted as unmapped, never typed.
        await mac.SendRawAsync("""{"type":"ctlKey","data":{"usage":786543,"down":true}}""");
        Assert.True(await Eventually.TrueAsync(() => host.Core.Roam.KeyStats.Totals.Unmapped == 1));
        Assert.True(host.Core.Roam.DrainInput(TimeSpan.FromSeconds(2)));
        Assert.Equal(new[] { (4, true), (4, true), (4, true), (4, false) }, pc.Keys);
        var totals = host.Core.Roam.KeyStats.Totals;
        Assert.Equal(5, totals.Received);
        Assert.Equal(4, totals.Injected);
        Assert.Empty(pills);

        // An elevated PowerShell in front: Windows would drop it all (UIPI) — the pill says so, once.
        sys.Front = new RoamForeground(7, 900, "powershell.exe", false, new RoamRect(100, 100, 800, 600), new RoamRect(0, 0, 1920, 1080), Elevated: true);
        await mac.SendRawAsync("""{"type":"ctlKey","data":{"usage":5,"down":true}}""");
        await mac.SendRawAsync("""{"type":"ctlKey","data":{"usage":5,"down":false}}""");
        await mac.SendRawAsync("""{"type":"ctlKey","data":{"usage":6,"down":true}}""");
        Assert.True(await Eventually.TrueAsync(() => { lock (pills) return pills.Count == 1; }));
        var pill = pills[0]!;
        Assert.Equal(RoamPillKind.AdminApp, pill.Kind);
        Assert.Equal("The app in front runs as administrator — H.I.B can't type into it", pill.Text);
        Assert.Contains(host.Core.LogLines, l => l.Contains("powershell.exe runs as administrator"));
        Assert.True(host.Core.Roam.DrainInput(TimeSpan.FromSeconds(2)));
        lock (pills) Assert.Single(pills);
    }

    // ---------------- §12.3 smoother ----------------

    /// <summary>An injector whose first move blocks until released (a slow SendInput).</summary>
    sealed class SlowPlatform : IRoamPlatform
    {
        public readonly SemaphoreSlim Gate = new(0);
        public readonly ManualResetEventSlim Entered = new();
        public readonly List<string> Log = new();
        int _moves;
        public bool IsAvailable => true;
        public string UnavailableReason => "";
        public string Platform => "windows";
        public RoamInputMode Mode => RoamInputMode.Controlled;
        public void SetMode(RoamInputMode mode) { }
        public void Park(double px, double py, bool hide) { }
        public void Unpark(double px, double py) { }
        public void InjectMove(double px, double py)
        {
            if (Interlocked.Increment(ref _moves) == 1)
            {
                Entered.Set();
                Gate.Wait();
            }
            lock (Log) Log.Add($"move {px}");
        }
        public void InjectRelative(int dx, int dy) { lock (Log) Log.Add($"rel {dx},{dy}"); }
        public void InjectButton(RoamButton button, bool down) { lock (Log) Log.Add($"button {down}"); }
        public void InjectWheel(int dx, int dy) { lock (Log) Log.Add($"wheel {dx},{dy}"); }
        public bool InjectKey(int usage, bool down) { lock (Log) Log.Add($"key {usage} {down}"); return true; }
        public void FinishParkedDrag(bool cancel) { }
        public event Action<RoamInput>? Input { add { } remove { } }
        public void Dispose() { }
    }

    [Fact]
    public void The_input_thread_keeps_only_the_newest_move_while_busy_and_never_merges_keys_or_reorders()
    {
        var p = new SlowPlatform();
        using var inj = new RoamInjector(p, new RoamKeyStats(), () => null);
        inj.Move(1, 1);
        Assert.True(p.Entered.Wait(2000)); // the first move is being injected
        for (var i = 2; i <= 5; i++) inj.Move(i, i);
        inj.Key(4, true);
        inj.Key(4, true);
        inj.Move(6, 6);
        inj.Relative(3, 1);
        inj.Relative(2, -1);
        inj.Wheel(0, 7.5);
        inj.Wheel(0, 7.5);
        inj.Wheel(0, 0.4);
        inj.Wheel(0, 0.4);
        inj.Wheel(0, 0.4);
        Assert.Equal(10, inj.Pending); // 2→5 collapsed, rel added up
        p.Gate.Release();
        Assert.True(inj.Drain(TimeSpan.FromSeconds(2)));
        // Sub-notch scrolling: 7.5 + 7.5 → 7 then 8; three 0.4s → nothing, nothing, 1 (0.2 left over).
        Assert.Equal(new[] { "move 1", "move 5", "key 4 True", "key 4 True", "move 6", "rel 5,0", "wheel 0,7", "wheel 0,8", "wheel 0,1" }, p.Log);
    }

    [Fact]
    public void Latency_is_the_median_of_the_last_20_round_trips()
    {
        var m = new LatencyMeter();
        Assert.Null(m.MedianMs);
        m.Add(3);
        Assert.Equal(3, m.MedianMs);
        m.Add(5);
        Assert.Equal(4, m.MedianMs);
        for (var i = 0; i < 30; i++) m.Add(i < 25 ? 100 : 2);
        Assert.Equal(20, m.Count);
        Assert.Equal(100, m.MedianMs); // 15 × 100 ms, 5 × 2 ms
        Assert.Equal("Latency 3 ms", LatencyMeter.Text(3));
    }

    // ---------------- §12.4 game mode ----------------

    [Fact]
    public void Game_mode_comes_from_the_window_the_cursor_and_the_clip()
    {
        var mon = new RoamRect(1920, 0, 2560, 1440);
        RoamForeground W(RoamRect r, bool shown = true, RoamRect? clip = null, bool own = false) => new(9, 1, "VALORANT-Win64-Shipping.exe", own, r, mon, shown, clip);
        Assert.Equal(RoamGameMode.Normal, RoamGameModes.Classify(null));
        Assert.Equal(RoamGameMode.Normal, RoamGameModes.Classify(W(new RoamRect(2000, 100, 1200, 800))));
        Assert.Equal(RoamGameMode.Fullscreen, RoamGameModes.Classify(W(mon)));
        Assert.Equal(RoamGameMode.Fullscreen, RoamGameModes.Classify(W(new RoamRect(1912, -8, 2576, 1456)))); // a little past the monitor
        Assert.Equal(RoamGameMode.Locked, RoamGameModes.Classify(W(mon, shown: false)));
        Assert.Equal(RoamGameMode.Locked, RoamGameModes.Classify(W(mon, clip: new RoamRect(3190, 710, 20, 20))));
        Assert.Equal(RoamGameMode.Fullscreen, RoamGameModes.Classify(W(mon, clip: new RoamRect(0, 0, 4480, 1440)))); // the whole desktop: not clipped
        Assert.Equal(RoamGameMode.Normal, RoamGameModes.Classify(W(mon, shown: false, own: true))); // H.I.B's own windows
        Assert.Equal(RoamGameMode.Normal, RoamGameModes.Classify(W(default))); // a maximized window with a title bar
        Assert.Equal(RoamGameMode.Locked, RoamGameModes.Parse("locked"));
        Assert.Equal("fullscreen", RoamGameModes.Wire(RoamGameMode.Fullscreen));
        Assert.Equal(RoamGameMode.Normal, RoamGameModes.Parse("weird"));
    }

    [Fact]
    public void The_gate_is_the_top_15_percent_of_an_edge_and_takes_two_pushes_within_500_ms()
    {
        Assert.Equal((0.0, 162.0), RoamGate.Of(0, 1080));
        Assert.Equal((100.0, 160.0), RoamGate.Of(100, 300)); // at least 60
        Assert.Equal((0.0, 120.0), RoamGate.Of(0, 300, unit: 2)); // 60 points at 200 %
        Assert.Equal((0.0, 40.0), RoamGate.Of(0, 40)); // never more than the edge

        var g = new DoublePushGate();
        var t = new DateTime(2026, 1, 1);
        var e = "right";
        Assert.Equal(RoamPush.First, g.Feed(e, true, 0, t));
        Assert.Equal(RoamPush.None, g.Feed(e, true, 0, t.AddMilliseconds(50))); // still pressed: the same push
        Assert.Equal(RoamPush.None, g.Feed(e, false, 1, t.AddMilliseconds(80))); // a pixel off isn't leaving
        Assert.Equal(RoamPush.None, g.Feed(e, true, 0, t.AddMilliseconds(90)));
        Assert.Equal(RoamPush.None, g.Feed(e, false, 8, t.AddMilliseconds(150)));
        Assert.Equal(RoamPush.Cross, g.Feed(e, true, 0, t.AddMilliseconds(300)));
        // Too slow: the second push is a new first push.
        Assert.Equal(RoamPush.First, g.Feed(e, true, 0, t.AddSeconds(2)));
        g.Feed(e, false, 10, t.AddMilliseconds(2300));
        Assert.Equal(RoamPush.First, g.Feed(e, true, 0, t.AddMilliseconds(2600)));
        // §12.6: the 500 ms count from the last event of the first push, however long it lasted.
        g.Reset();
        var u = t.AddSeconds(10);
        Assert.Equal(RoamPush.First, g.Feed(e, true, 0, u));
        Assert.Equal(RoamPush.None, g.Feed(e, true, 0, u.AddMilliseconds(800))); // still pushing
        g.Feed(e, false, 12, u.AddMilliseconds(900));
        Assert.Equal(RoamPush.Cross, g.Feed(e, true, 0, u.AddMilliseconds(1100)));
    }

    [Fact]
    public void With_a_full_screen_app_here_only_the_gate_opens_and_only_on_a_double_push()
    {
        var (c, h, clock) = Controller(Mac());
        c.SetLocalGame(RoamGameMode.Fullscreen);
        c.Handle(RoamInput.Pointer(1925, 540, false)); // mid edge: closed; the pill points at the gate
        Assert.Null(h.Entered);
        Assert.Equal(RoamPillKind.Gate, Assert.Single(h.Pills)!.Kind);
        c.Handle(RoamInput.Pointer(1900, 540, false));
        c.Handle(RoamInput.Pointer(1925, 540, false)); // pushes outside the gate never count
        Assert.Null(h.Entered);
        c.Handle(RoamInput.Pointer(1925, 100, false)); // the gate: first push
        Assert.Null(h.Entered);
        Assert.Equal(2, h.Pills.Count);
        var pill = h.Pills.Last()!;
        Assert.Equal(RoamPillKind.Gate, pill.Kind);
        Assert.Equal("Game running — push again here to leave", pill.Text);
        Assert.Equal((0.0, 162.0), (pill.At!.SegStart, pill.At.SegEnd));
        c.Handle(RoamInput.Pointer(1930, 100, false)); // still pushing
        Assert.Null(h.Entered);
        clock.Advance(120);
        c.Handle(RoamInput.Pointer(1900, 100, false)); // away from the edge
        clock.Advance(150);
        c.Handle(RoamInput.Pointer(1925, 100, false)); // second push within 500 ms
        Assert.NotNull(h.Entered);
        Assert.Null(h.Pills.Last());

        // Back home (no game on the Mac: its edge is open — the gate guards leaving the game, not coming back), then a slow
        // second push doesn't cross.
        c.Handle(RoamInput.Motion(-2000, 0));
        Assert.Equal(RoamMode.Home, c.Mode);
        h.Entered = null;
        c.Handle(RoamInput.Pointer(1925, 100, false));
        clock.Advance(100);
        c.Handle(RoamInput.Pointer(1900, 100, false));
        clock.Advance(600);
        c.Handle(RoamInput.Pointer(1925, 100, false));
        Assert.Null(h.Entered);

        // The game closes: the edge is open again.
        c.SetLocalGame(RoamGameMode.Normal);
        c.Handle(RoamInput.Pointer(1925, 540, false));
        Assert.NotNull(h.Entered);
    }

    [Fact]
    public void Leaving_a_computer_with_a_full_screen_app_needs_the_gate_and_a_double_push()
    {
        var (c, h, clock) = Controller(Mac(RoamGameMode.Fullscreen));
        c.Handle(RoamInput.Pointer(1925, 540, false)); // this PC has no game: in at once
        Assert.Equal(RoamMode.Away, c.Mode);
        c.Handle(RoamInput.Motion(-100, 0)); // mid left edge of the Mac: closed
        Assert.Equal(RoamMode.Away, c.Mode);
        Assert.Equal(0, c.Virtual.X);
        c.Handle(RoamInput.Motion(0, -430)); // up to y = 61: inside the gate (top 147 points)
        c.Handle(RoamInput.Motion(-10, 0));
        Assert.Equal(RoamMode.Away, c.Mode);
        Assert.Equal(RoamPillKind.Gate, h.Pills.Last()!.Kind);
        Assert.Equal(RoamSide.Right, h.Pills.Last()!.At!.Side); // shown on this PC's edge, at its top
        c.Handle(RoamInput.Motion(20, 0));
        clock.Advance(200);
        c.Handle(RoamInput.Motion(-30, 0));
        Assert.Equal(RoamMode.Home, c.Mode);
    }

    [Fact]
    public void A_game_that_holds_the_cursor_gets_relative_motion_and_the_mouse_never_falls_out()
    {
        var (c, h, _) = Controller(Mac(RoamGameMode.Locked, speed: 1.5));
        c.Handle(RoamInput.Pointer(1925, 540, false));
        Assert.Equal(RoamMode.Away, c.Mode);
        var start = c.Virtual;
        var pill = h.Pills.Last()!;
        Assert.Equal(RoamPillKind.GameMode, pill.Kind);
        Assert.Equal("Game mode · the mouse stays on B Mac · Scroll Lock or Ctrl+Alt+Shift+L to leave", pill.Text);
        c.Handle(RoamInput.Motion(3, 1));   // 4.5, 1.5 → 4, 1 (0.5, 0.5 carried)
        c.Handle(RoamInput.Motion(1, 1));   // 2.0, 2.0 → 2, 2
        c.Handle(RoamInput.Motion(-5000, 0)); // a flick to the left: no edge leads home
        Assert.Equal(RoamMode.Away, c.Mode);
        Assert.Equal(new[] { (4, 1), (2, 2), (-7500, 0) }, h.Rels);
        Assert.Equal(start, c.Virtual); // frozen
        Assert.Empty(h.Moves);

        // The game shows its cursor again: back to absolute moves, from the centre of that screen (§12.6).
        c.SetNeighbors(new[] { Mac(RoamGameMode.Fullscreen) });
        Assert.Null(h.Pills.Last());
        Assert.Equal((756.0, 491.0), Assert.Single(h.Moves));
        c.Handle(RoamInput.Motion(10, 0));
        Assert.Equal((766.0, 491.0), h.Moves.Last());

        // Locked again: the lock hotkey leaves the game (the mouse comes home).
        c.SetNeighbors(new[] { Mac(RoamGameMode.Locked) });
        c.Handle(RoamInput.Hot(RoamHotkey.Lock));
        Assert.Equal(RoamMode.Home, c.Mode);
        Assert.Equal(1, h.Leaves);
    }

    [Fact]
    public async Task Game_state_travels_both_ways_and_a_locked_mac_gets_ctlRel()
    {
        var pc = new FakeRoamPlatform();
        var sys = new FakeRoamSystem();
        await using var host = new TestHost(name: "Office PC", space: RoamTests.FastSpace(), roam: new RoamOptions { Platform = () => pc, System = () => sys });
        host.Core.SpaceEngine.SetSpaceKey(SpaceKey);
        host.Core.Roam.SetDisplays(new[] { Pc });
        await using var mac = await RawMac.DialAsync(host.Core.Link.Port, SpaceKey);
        await mac.SendRawAsync(RoamUiTests.MacHello.Replace("\"manualSide\":null", "\"manualSide\":\"left\"")); // the PC is left of the Mac
        Assert.True(await Eventually.TrueAsync(() => host.Core.Roam.StatusFor("A Mac") is { Ready: true, Side: RoamSide.Right }));
        Assert.True(await Eventually.TrueAsync(() => mac.Of("ctlTargetState").Count == 1)); // said on link-up
        Assert.Equal("normal", mac.Of("ctlTargetState")[0].GetProperty("mode").GetString());

        // Valorant runs here, holding the cursor in the middle.
        var mon = new RoamRect(0, 0, 1920, 1080);
        sys.Front = new RoamForeground(5, 77, "VALORANT-Win64-Shipping.exe", false, mon, mon, CursorShown: false, Clip: new RoamRect(959, 539, 2, 2));
        host.Core.Roam.RefreshForeground();
        Assert.True(await Eventually.TrueAsync(() => mac.Of("ctlTargetState").Count == 2));
        var state = mac.Of("ctlTargetState")[1];
        Assert.Equal("locked", state.GetProperty("mode").GetString());
        Assert.Equal("VALORANT-Win64-Shipping.exe", state.GetProperty("app").GetString());
        Assert.Equal(RoamGameMode.Locked, host.Core.Roam.LocalGame);

        // The Mac drives this PC: its relative motion is injected without ABSOLUTE.
        await mac.SendRawAsync("""{"type":"ctlEnter","data":{"x":0,"y":420,"clipboard":null}}""");
        await mac.SendRawAsync("""{"type":"ctlRel","data":{"dx":12,"dy":-3}}""");
        Assert.True(await Eventually.TrueAsync(() => pc.Rels.Count == 1));
        Assert.Equal((12, -3), pc.Rels[0]);
        // Floats are accepted; the fractions add up (§12.6).
        await mac.SendRawAsync("""{"type":"ctlRel","data":{"dx":0.5,"dy":0}}""");
        await mac.SendRawAsync("""{"type":"ctlRel","data":{"dx":0.5,"dy":-0.25}}""");
        Assert.True(await Eventually.TrueAsync(() => pc.Rels.Count == 2));
        Assert.Equal((1, 0), pc.Rels[1]);
        await mac.SendRawAsync("""{"type":"ctlLeave","data":{"clipboard":null}}""");
        Assert.True(await Eventually.TrueAsync(() => host.Core.Roam.ControlledById is null));

        // Game mode off in Settings: this PC says normal.
        host.Core.Roam.SetGameMode(false);
        Assert.True(await Eventually.TrueAsync(() => mac.Of("ctlTargetState").Count == 3));
        Assert.Equal("normal", mac.Of("ctlTargetState")[2].GetProperty("mode").GetString());
        host.Core.Roam.SetGameMode(true);
        sys.Front = null;
        host.Core.Roam.RefreshForeground();

        // A game on the Mac holds its cursor: this PC sends relative motion and the mouse stays there.
        await mac.SendRawAsync("""{"type":"ctlTargetState","data":{"mode":"locked","app":"Cyberpunk2077"}}""");
        Assert.True(await Eventually.TrueAsync(() => host.Core.Roam.StatusFor("A Mac") is { RemoteGame: RoamGameMode.Locked, RemoteApp: "Cyberpunk2077" }));
        pc.Raise(RoamInput.Pointer(1925, 540, false));
        Assert.True(await Eventually.TrueAsync(() => mac.Of("ctlEnter").Count == 1));
        pc.Raise(RoamInput.Motion(6, 2));
        pc.Raise(RoamInput.Motion(-4000, 0));
        Assert.True(await Eventually.TrueAsync(() => mac.Of("ctlRel").Sum(r => r.GetProperty("dx").GetInt32()) == -3994));
        Assert.Equal(RoamMode.Away, host.Core.Roam.Mode);
        Assert.Contains("Game mode on A Mac", host.Core.Roam.StatusFor("A Mac")!.Status);
    }

    // ---------------- §12.5 hotkeys, lock, jump ----------------

    [Fact]
    public void Hotkeys_fire_on_ctrl_alt_shift_l_arrows_or_scroll_lock_and_eat_only_that_key()
    {
        var d = new RoamHotkeyDetector();
        Assert.Equal(RoamHotkeyResult.Pass, d.Feed(RoamHotkeyDetector.L, true)); // plain L
        d.Feed(RoamHotkeyDetector.L, false);
        Assert.Equal(RoamHotkeyResult.Pass, d.Feed(RoamKeys.LeftCtrl, true));
        Assert.Equal(RoamHotkeyResult.Pass, d.Feed(RoamKeys.RightAlt, true));
        Assert.Equal(RoamHotkeyResult.Pass, d.Feed(RoamHotkeyDetector.L, true)); // Ctrl+Alt+L: no
        d.Feed(RoamHotkeyDetector.L, false);
        d.Feed(RoamKeys.LeftShift, true);
        Assert.Equal(new RoamHotkeyResult(RoamHotkey.Lock, true), d.Feed(RoamHotkeyDetector.L, true));
        Assert.Equal(new RoamHotkeyResult(RoamHotkey.None, true), d.Feed(RoamHotkeyDetector.L, true)); // its auto-repeat
        Assert.Equal(new RoamHotkeyResult(RoamHotkey.None, true), d.Feed(RoamHotkeyDetector.L, false)); // its release
        Assert.Equal(RoamHotkey.JumpLeft, d.Feed(RoamHotkeyDetector.Left, true).Fire);
        d.Feed(RoamHotkeyDetector.Left, false);
        Assert.Equal(RoamHotkey.JumpUp, d.Feed(RoamHotkeyDetector.Up, true).Fire);
        d.Feed(RoamHotkeyDetector.Up, false);
        d.Feed(RoamKeys.LeftGui, true); // with the Windows key: not ours
        Assert.Equal(RoamHotkeyResult.Pass, d.Feed(RoamHotkeyDetector.Right, true));
        d.Feed(RoamHotkeyDetector.Right, false);
        Assert.Equal(RoamHotkeyResult.Pass, d.Feed(RoamKeys.LeftShift, false)); // modifiers always pass
        d.Reset();
        Assert.Equal(new RoamHotkeyResult(RoamHotkey.Lock, true), d.Feed(RoamHotkeyDetector.ScrollLock, true));
        Assert.Equal(new RoamHotkeyResult(RoamHotkey.None, true), d.Feed(RoamHotkeyDetector.ScrollLock, false));
    }

    [Fact]
    public void Lock_keeps_the_mouse_where_it_is_and_jump_moves_it_to_the_computer_on_that_side()
    {
        var (c, h, _) = Controller(Mac());
        c.Handle(RoamInput.Pointer(1500, 300, false));
        c.Handle(RoamInput.Hot(RoamHotkey.Lock));
        Assert.True(c.UserLock);
        Assert.Equal("Locked to Office PC", h.Pills.Last()!.Text);
        c.Handle(RoamInput.Pointer(1925, 540, false));
        Assert.Null(h.Entered); // locked here
        c.Handle(RoamInput.Hot(RoamHotkey.Lock));
        Assert.Equal(RoamPillKind.Unlocked, h.Pills.Last()!.Kind);
        c.Handle(RoamInput.Pointer(1925, 540, false));
        Assert.NotNull(h.Entered);

        // Locked to the Mac: its edges lead nowhere.
        c.Handle(RoamInput.Hot(RoamHotkey.Lock));
        Assert.Equal("Locked to B Mac", h.Pills.Last()!.Text);
        c.Handle(RoamInput.Motion(-3000, 0));
        Assert.Equal(RoamMode.Away, c.Mode);
        Assert.Equal(0, c.Virtual.X);

        // Jump left (towards this PC): home, at the centre of the main screen; the lock is gone.
        c.Handle(RoamInput.Hot(RoamHotkey.JumpLeft));
        Assert.Equal(RoamMode.Home, c.Mode);
        Assert.Equal((960.0, 540.0), h.Uncaptured!.Value);
        Assert.False(c.UserLock);

        // Jump right from home: the Mac, centre of its main screen. Up: nothing there.
        h.Entered = null;
        c.Handle(RoamInput.Hot(RoamHotkey.JumpUp));
        Assert.Null(h.Entered);
        c.Handle(RoamInput.Hot(RoamHotkey.JumpRight));
        Assert.Equal((756.0, 491.0), (h.Entered!.Value.X, h.Entered.Value.Y));
        Assert.Equal(RoamMode.Away, c.Mode);
    }

    // ---------------- §12.5 lock together ----------------

    [Fact]
    public void Lock_together_locks_the_others_once_and_never_echoes()
    {
        var a = new LockTogether();
        var b = new LockTogether();
        var t = new DateTime(2026, 1, 1);
        Assert.True(a.OnLocal(true, enabled: true, t));     // A locks: tell B
        Assert.True(b.OnRemote(enabled: true, t));           // B locks
        Assert.False(b.OnLocal(true, enabled: true, t.AddSeconds(1))); // B's own lock notice isn't sent back
        Assert.False(a.OnRemote(enabled: true, t.AddSeconds(1)));      // and A is locked anyway
        Assert.False(a.OnLocal(false, enabled: true, t.AddSeconds(30))); // unlocking unlocks nothing
        Assert.False(b.OnLocal(false, enabled: true, t.AddSeconds(31)));
        Assert.True(b.OnLocal(true, enabled: true, t.AddSeconds(60))); // later, B's own lock goes out
        Assert.False(new LockTogether().OnLocal(true, enabled: false, t)); // off in Settings
        Assert.False(new LockTogether().OnRemote(enabled: false, t));
    }

    [Fact]
    public async Task Locking_this_pc_locks_the_linked_mac_and_a_mac_lock_locks_this_pc()
    {
        var pc = new FakeRoamPlatform();
        var sys = new FakeRoamSystem();
        await using var host = new TestHost(name: "Office PC", space: RoamTests.FastSpace(), roam: new RoamOptions { Platform = () => pc, System = () => sys });
        host.Core.SpaceEngine.SetSpaceKey(SpaceKey);
        host.Core.Roam.SetDisplays(new[] { Pc });
        await using var mac = await RawMac.DialAsync(host.Core.Link.Port, SpaceKey);
        await mac.SendRawAsync(RoamUiTests.MacHello);
        Assert.True(await Eventually.TrueAsync(() => host.Core.Roam.StatusFor("A Mac") is { Ready: true }));

        sys.RaiseLock(true);
        Assert.True(await Eventually.TrueAsync(() => mac.Of("ctlScreenLock").Count == 1));
        sys.RaiseLock(false); // unlocking says nothing
        await mac.SendRawAsync("""{"type":"ctlScreenLock","data":{}}""");
        Assert.True(await Eventually.TrueAsync(() => host.Core.Roam.WaitChain(TimeSpan.FromSeconds(1)) && sys.Locks == 1));
        sys.RaiseLock(true); // the lock the Mac caused isn't sent back
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Single(mac.Of("ctlScreenLock"));

        host.Core.Roam.SetLockTogether(false);
        sys.RaiseLock(false);
        await mac.SendRawAsync("""{"type":"ctlScreenLock","data":{}}""");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(1, sys.Locks);
        Assert.False(host.Core.Roam.Options.LockTogether);
    }

    // ---------------- §12.5 speeds and natural scrolling ----------------

    [Fact]
    public void Pointer_and_scroll_speed_and_natural_scrolling_apply_per_computer()
    {
        var (c, h, _) = Controller(Mac(speed: 2, scroll: 0.5, natural: true));
        c.Handle(RoamInput.Pointer(1925, 540, false));
        var (x0, y0) = c.Virtual;
        c.Handle(RoamInput.Motion(10, 5));
        Assert.Equal((x0 + 20, y0 + 10), c.Virtual);
        c.Handle(RoamInput.Scroll(0, 120)); // half speed, reversed
        c.Handle(RoamInput.Scroll(0, 1));   // −0.5: nothing yet
        c.Handle(RoamInput.Scroll(0, 1));   // −0.5 more: −1
        var wheels = h.Sent.Where(m => m.Type == RoamWire.Wheel).Select(m => ((int)m.Data["dx"]!, (int)m.Data["dy"]!)).ToList();
        Assert.Equal(new[] { (0, -60), (0, -1) }, wheels);
        // §12.5 key repeat: every auto-repeated down from this keyboard goes across.
        for (var i = 0; i < 3; i++) c.Handle(RoamInput.KeyEvent(0x04, true));
        c.Handle(RoamInput.KeyEvent(0x04, false));
        Assert.Equal(new[] { true, true, true, false }, h.Sent.Where(m => m.Type == RoamWire.Key).Select(m => (bool)m.Data["down"]!));
        Assert.Equal(0.5, RoamSpeed.Clamp(0.1));
        Assert.Equal(2, RoamSpeed.Clamp(9));
        Assert.Equal(1, RoamSpeed.Clamp(double.NaN));
        Assert.Equal("1.5×", RoamSpeed.Label(1.5));
    }

    [Fact]
    public async Task Speeds_are_kept_per_computer_and_shown_with_the_latency()
    {
        var pc = new FakeRoamPlatform();
        var root = Path.Combine(Path.GetTempPath(), "hib-tests", Guid.NewGuid().ToString("N")[..8]);
        await using (var host = new TestHost(name: "Office PC", root: root, space: RoamTests.FastSpace(),
                         roam: new RoamOptions { Platform = () => pc, System = () => new FakeRoamSystem(), LatencyPing = TimeSpan.FromMilliseconds(150) }) { KeepRoot = true })
        {
            host.Core.SpaceEngine.SetSpaceKey(SpaceKey);
            host.Core.Roam.SetDisplays(new[] { Pc });
            await using var mac = await RawMac.DialAsync(host.Core.Link.Port, SpaceKey);
            await mac.SendRawAsync(RoamUiTests.MacHello);
            Assert.True(await Eventually.TrueAsync(() => host.Core.Roam.StatusFor("A Mac") is { Ready: true }));
            host.Core.Roam.SetPointerSpeed("A Mac", 1.5);
            host.Core.Roam.SetScrollSpeed("A Mac", 7); // clamped
            host.Core.Roam.SetNaturalScroll("A Mac", true);
            var s = host.Core.Roam.StatusFor("A Mac")!;
            Assert.Equal((1.5, 2.0, true), (s.PointerSpeed, s.ScrollSpeed, s.NaturalScroll));
            // §12.3 the link latency: pings while ready, the median of the round trips.
            Assert.True(await Eventually.TrueAsync(() => host.Core.Roam.StatusFor("A Mac")!.LatencyMs is not null));
            Assert.InRange(host.Core.Roam.StatusFor("A Mac")!.LatencyMs!.Value, 0, 1000);
        }
        await using var again = new TestHost(name: "Office PC", root: root, roam: new RoamOptions { Platform = () => new FakeRoamPlatform(), System = () => new FakeRoamSystem() });
        var json = File.ReadAllText(Path.Combine(root, "data", "roam.json"));
        Assert.Contains("\"PointerSpeed\": 1.5", json);
        Assert.Contains("\"NaturalScroll\": true", json);
    }

    // ---------------- §12.5 clipboard images ----------------

    [Fact]
    public void Clipboard_text_wins_an_image_goes_when_there_is_no_text_and_big_ones_stay()
    {
        var png = SpaceTests.PngBytes(64, 48);
        Assert.Equal("hi", RoamClip.Pick("hi", png)!.Text);
        Assert.Equal(png, RoamClip.Pick(null, png)!.Png);
        Assert.Equal(png, RoamClip.Pick("", png)!.Png);
        Assert.Null(RoamClip.Pick(null, null));
        Assert.Null(RoamClip.Pick(null, new byte[RoamClip.MaxPngBytes + 1]));
        Assert.NotEqual(RoamClip.Pick("a", null)!.Signature, RoamClip.Pick("b", null)!.Signature);

        var wire = RoamWire.EnterData(1, 2, RoamClip.Pick(null, png));
        using var doc = JsonDocument.Parse(wire.ToJsonString());
        Assert.Equal(Convert.ToBase64String(png), doc.RootElement.GetProperty("clipboard").GetProperty("png").GetString());
        Assert.Equal(png, RoamClip.Parse(doc.RootElement)!.Png);
        using var notPng = JsonDocument.Parse("""{"clipboard":{"png":"aGVsbG8gd29ybGQh"}}""");
        Assert.Null(RoamClip.Parse(notPng.RootElement));
        using var text = JsonDocument.Parse("""{"clipboard":{"text":"from the Mac"}}""");
        Assert.Equal("from the Mac", RoamClip.Parse(text.RootElement)!.Text);
    }

    [Fact]
    public async Task A_copied_image_crosses_with_the_mouse()
    {
        var pc = new FakeRoamPlatform();
        await using var host = new TestHost(name: "Office PC", space: RoamTests.FastSpace(), roam: new RoamOptions { Platform = () => pc, System = () => new FakeRoamSystem() });
        host.Core.SpaceEngine.SetSpaceKey(SpaceKey);
        host.Core.Roam.SetDisplays(new[] { Pc });
        await using var mac = await RawMac.DialAsync(host.Core.Link.Port, SpaceKey);
        await mac.SendRawAsync(RoamUiTests.MacHello);
        Assert.True(await Eventually.TrueAsync(() => host.Core.Roam.StatusFor("A Mac") is { Ready: true }));

        // The Mac brings a screenshot.
        var shot = SpaceTests.PngBytes(32, 32);
        await mac.SendRawAsync("""{"type":"ctlEnter","data":{"x":0,"y":420,"clipboard":{"png":"B64"}}}""".Replace("B64", Convert.ToBase64String(shot)));
        Assert.True(await Eventually.TrueAsync(() => host.Clipboard.Image is not null));
        Assert.Equal(shot, host.Clipboard.Image);
        await mac.SendRawAsync("""{"type":"ctlLeave","data":{"clipboard":null}}""");
        Assert.True(await Eventually.TrueAsync(() => host.Core.Roam.ControlledById is null));

        // This PC copies another image (no text) and goes over: it travels as PNG.
        var mine = SpaceTests.PngBytes(48, 20);
        host.Clipboard.Image = mine;
        pc.Raise(RoamInput.Pointer(1925, 540, false)); // the Mac is on the right (default)
        Assert.True(await Eventually.TrueAsync(() => mac.Of("ctlEnter").Count == 1));
        var clip = mac.Of("ctlEnter")[0].GetProperty("clipboard");
        Assert.Equal(Convert.ToBase64String(mine), clip.GetProperty("png").GetString());
    }

    // ---------------- §12.2 Get log ----------------

    [Fact]
    public async Task Get_log_asks_the_mac_over_the_peer_link_and_saves_it_by_name()
    {
        await using var host = new TestHost(name: "Office PC", space: RoamTests.FastSpace(), roam: new RoamOptions { Platform = () => new FakeRoamPlatform(), System = () => new FakeRoamSystem() });
        host.Core.SpaceEngine.SetSpaceKey(SpaceKey);
        await using var mac = await RawMac.DialAsync(host.Core.Link.Port, SpaceKey, "Haitham’s MacBook: Pro");
        Assert.True(await Eventually.TrueAsync(() => host.Core.SpaceEngine.Network.IsLinked("Haitham’s MacBook: Pro")));
        var dir = TempDir();
        var fetch = PeerDiagnostics.FetchAsync(host.Core.SpaceEngine.Network, "Haitham’s MacBook: Pro", "Haitham’s MacBook: Pro", dir);
        Assert.True(await Eventually.TrueAsync(() => mac.Of("peerDiag").Count == 1));
        await mac.SendAsync("peerDiagLog", new { version = "4.2.0", text = "12:00:01 [roam] Roam keys (10 s): captured 3 · sent 3\n" });
        var (path, problem) = await fetch;
        Assert.Null(problem);
        Assert.Equal(Path.Combine(dir, "Haitham’s MacBook- Pro.log"), path);
        var saved = File.ReadAllText(path!);
        Assert.StartsWith("# H.I.B log from Haitham’s MacBook: Pro (H.I.B 4.2.0)", saved);
        Assert.Contains("captured 3 · sent 3", saved);

        // Not linked: says so. Linked but silent: says so after the wait.
        Assert.Equal("Studio PC isn't linked directly right now", (await PeerDiagnostics.FetchAsync(host.Core.SpaceEngine.Network, "Studio PC", "Studio PC", dir)).Problem);
        var silent = await PeerDiagnostics.FetchAsync(host.Core.SpaceEngine.Network, "Haitham’s MacBook: Pro", "Haitham’s MacBook: Pro", dir, TimeSpan.FromMilliseconds(300));
        Assert.Equal("Haitham’s MacBook: Pro didn't send its log", silent.Problem);
        Assert.Equal("hib (peer).log", PeerDiagnostics.FileNameFor("hib"));
        Assert.Equal("a-b-c.log", PeerDiagnostics.FileNameFor("a/b\\c"));
    }
}
