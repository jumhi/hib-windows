using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HIB.Core.Link;
using HIB.Core.Phone;
using HIB.Core.Roam;
using HIB.Core.Space;

namespace HIB.FakePhone;

/// <summary>Stands in for Windows' hooks and SendInput: records what would be injected, raises input on demand.</summary>
sealed class FakeRoamPlatform : IRoamPlatform
{
    readonly object _gate = new();
    readonly List<(double X, double Y)> _moves = new();
    readonly List<(RoamButton B, bool Down)> _buttons = new();
    readonly List<(int Dx, int Dy)> _wheels = new();
    readonly List<(int Usage, bool Down)> _keys = new();
    readonly List<(int Dx, int Dy)> _rels = new();

    public bool IsAvailable { get; set; } = true;
    public string UnavailableReason => "fake";
    public string Platform { get; init; } = "windows";
    public RoamInputMode Mode { get; private set; }
    public (double X, double Y, bool Hide)? Parked { get; private set; }
    public (double X, double Y)? Unparked { get; private set; }
    public int DragCancels { get; private set; }
    public event Action<RoamInput>? Input;

    public void SetMode(RoamInputMode mode) => Mode = mode;
    public void Park(double px, double py, bool hide) { Parked = (px, py, hide); Unparked = null; }
    public void Unpark(double px, double py) => Unparked = (px, py);
    public void InjectMove(double px, double py) { lock (_gate) _moves.Add((px, py)); }
    public void InjectButton(RoamButton button, bool down) { lock (_gate) _buttons.Add((button, down)); }
    public void InjectWheel(int dx, int dy) { lock (_gate) _wheels.Add((dx, dy)); }
    public void InjectRelative(int dx, int dy) { lock (_gate) _rels.Add((dx, dy)); }
    public bool InjectKey(int usage, bool down) { lock (_gate) _keys.Add((usage, down)); return true; }
    public void FinishParkedDrag(bool cancel) { if (cancel) DragCancels++; }
    public void Dispose() { }

    public void Raise(RoamInput e) => Input?.Invoke(e);
    public List<(double X, double Y)> Moves { get { lock (_gate) return _moves.ToList(); } }
    public List<(RoamButton B, bool Down)> Buttons { get { lock (_gate) return _buttons.ToList(); } }
    public List<(int Dx, int Dy)> Wheels { get { lock (_gate) return _wheels.ToList(); } }
    public List<(int Usage, bool Down)> Keys { get { lock (_gate) return _keys.ToList(); } }
    public List<(int Dx, int Dy)> Rels { get { lock (_gate) return _rels.ToList(); } }
}

/// <summary>Records what the controller asks for.</summary>
sealed class RecordingHost : IRoamControllerHost
{
    public readonly List<(string Type, JsonObject Data)> Sent = new();
    public readonly List<(double X, double Y)> Moves = new();
    public (RoamNeighbor Peer, double X, double Y)? Entered;
    public int Leaves;
    public (double X, double Y, bool Hide)? Captured;
    public (double X, double Y)? Uncaptured;
    public RoamEdgeHint? Glow;
    public RoamEdgeHint? Probe;
    public (RoamNeighbor Peer, double X, double Y, IReadOnlyList<string> Files)? Dropped;
    public (RoamSide Side, RoamNeighbor Peer)? LearnedSide;
    public readonly List<(int Dx, int Dy)> Rels = new();
    public readonly List<RoamPill?> Pills = new();

    public void Enter(RoamNeighbor peer, double x, double y) => Entered = (peer, x, y);
    public void Send(string peerId, string type, JsonObject data) => Sent.Add((type, data));
    public void Move(string peerId, double x, double y) => Moves.Add((x, y));
    public void Leave(RoamNeighbor peer) => Leaves++;
    public void Capture(double parkX, double parkY, bool hide) { Captured = (parkX, parkY, hide); Uncaptured = null; }
    public void Uncapture(double x, double y) => Uncaptured = (x, y);
    public void Hint(RoamEdgeHint? hint) => Glow = hint;
    public void ProbeDrag(RoamEdgeHint? at) => Probe = at;
    public void Drop(RoamNeighbor peer, double x, double y, IReadOnlyList<string> files) => Dropped = (peer, x, y, files);
    public void FinishLocalDrag(bool cancel) { }
    public void Learned(RoamSide side, RoamNeighbor peer, string? screenId) => LearnedSide = (side, peer);
    public void StateChanged() { }
    public void Rel(string peerId, int dx, int dy) => Rels.Add((dx, dy));
    public void Pill(RoamPill? pill) => Pills.Add(pill);
}

/// <summary>
/// Shared mouse &amp; keyboard (docs/SPACE.md §9): the side function, edge mapping, switch rules, learning, the outbox,
/// key and modifier tables, the watchdog, the ctl* JSON and a full round trip between two in-process computers.
/// </summary>
public class RoamTests
{
    internal static readonly byte[] SpaceKey = Enumerable.Range(1, 32).Select(i => (byte)(i * 11)).ToArray();

    // A 1920×1080 PC at 100 % and a 1512×982-point Mac.
    static readonly RoamDisplay Pc = new(@"\\.\DISPLAY1", new RoamRect(0, 0, 1920, 1080), 1, true);
    static readonly RoamRect MacScreen = new(0, 0, 1512, 982);

    static RoamNeighbor Mac(RoamSide side = RoamSide.Right, bool cmdAsCtrl = true, params RoamRect[] screens)
        => new("B Mac", "B Mac", side, screens.Length > 0 ? screens : new[] { MacScreen }, "mac", cmdAsCtrl);

    sealed class Clock
    {
        public DateTime Now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(double ms) => Now = Now.AddMilliseconds(ms);
    }

    static (RoamController c, RecordingHost h, Clock clock) Controller(params RoamNeighbor[] neighbors)
    {
        var clock = new Clock();
        var h = new RecordingHost();
        var c = new RoamController(h, () => clock.Now);
        c.SetDisplays(new[] { Pc });
        c.SetNeighbors(neighbors);
        return (c, h, clock);
    }

    // ---------------- §9.2 the side function ----------------

    [Fact]
    public void Side_comes_from_the_mean_aims_and_both_computers_agree()
    {
        Assert.Equal(RoamSide.Right, RoamLayout.SideFrom((200, 5), (240, 8)));
        Assert.Equal(RoamSide.Left, RoamLayout.SideFrom((240, 8), (200, 5)));
        Assert.Equal(RoamSide.Above, RoamLayout.SideFrom((200, 0), (205, 30)));
        Assert.Equal(RoamSide.Below, RoamLayout.SideFrom((205, 30), (200, 0)));
        // Across north: 350° → 10° is 20° clockwise, so to the right.
        Assert.Equal(RoamSide.Right, RoamLayout.SideFrom((350, 0), (10, 0)));
        Assert.Equal(180, RoamLayout.SignedHeadingDelta(0, 180));
        Assert.Equal(-90, RoamLayout.SignedHeadingDelta(90, 0));
        // The unit-vector mean of 350° and 10° is 0°, not 180°.
        var mean = RoamLayout.MeanAim(new[] { new ScreenAim("A", "1", 350, 0), new ScreenAim("A", "2", 10, 0) })!.Value;
        Assert.True(Math.Abs(RoamLayout.SignedHeadingDelta(0, mean.H)) < 1e-6);
        // Symmetric: whatever A computes for B, B computes the mirror for A.
        var rnd = new Random(42);
        for (var i = 0; i < 500; i++)
        {
            var a = (rnd.NextDouble() * 360, rnd.NextDouble() * 60 - 30);
            var b = (rnd.NextDouble() * 360, rnd.NextDouble() * 60 - 30);
            Assert.Equal(RoamSides.Mirror(RoamLayout.SideFrom(a, b)), RoamLayout.SideFrom(b, a));
        }
    }

    [Fact]
    public void ScreenLayout_parses_and_groups_aims_per_computer()
    {
        using var doc = JsonDocument.Parse("""
            {"screens":[{"computer":"Office PC","screen":"\\\\.\\DISPLAY1","h":214.0,"e":8.5},
                        {"computer":"office pc","screen":"\\\\.\\DISPLAY2","h":224.0,"e":8.5},
                        {"computer":"Haitham’s MacBook","screen":"x","h":250.0,"e":2.0},
                        {"computer":"","h":1,"e":1},{"computer":"Bad","h":"x"}]}
            """);
        var layout = RoamLayout.Parse(doc.RootElement);
        Assert.Equal(3, layout.Count);
        Assert.Equal(RoamSide.Right, RoamLayout.SideOf(layout, "Office PC", "Haitham’s MacBook"));
        Assert.Equal(RoamSide.Left, RoamLayout.SideOf(layout, "Haitham’s MacBook", "Office PC"));
        Assert.Null(RoamLayout.SideOf(layout, "Office PC", "Studio PC"));
        Assert.Equal(RoamLayout.Signature(layout), RoamLayout.Signature(layout.Reverse().ToList()));
    }

    // ---------------- edge mapping, clamping, corners ----------------

    [Fact]
    public void Crossing_maps_the_edge_proportionally_and_clamps_into_a_real_screen()
    {
        var (c, h, _) = Controller(Mac());
        c.Handle(RoamInput.Pointer(1000, 540, false));
        Assert.Null(h.Entered);
        c.Handle(RoamInput.Pointer(1925, 540, false)); // the hook sees the push past the edge
        Assert.Equal(RoamMode.Away, c.Mode);
        var (_, x, y) = h.Entered!.Value;
        Assert.Equal(0, x);
        Assert.Equal(491, y, 1); // half way down the Mac's left edge
        Assert.Equal((960.0, 540.0, true), h.Captured!.Value); // parked, hidden, mid primary screen

        // Two Mac screens of different heights side by side: the gap under the short one is never a destination.
        var tall = new RoamRect(0, 0, 1000, 1000);
        var shortOne = new RoamRect(1000, 0, 800, 400);
        var (ex, ey) = RoamGeometry.EnterAt(RoamSide.Right, 0.9, new[] { tall, shortOne });
        Assert.True(shortOne.Contains(ex, ey));
        Assert.Equal(399, ey);
        var (lx, ly) = RoamGeometry.EnterAt(RoamSide.Left, 0.25, new[] { tall, shortOne });
        Assert.Equal((0.0, 250.0), (lx, ly));
        // Clamping keeps a point in the nearest real screen.
        Assert.Equal((1799.0, 399.0), RoamGeometry.ClampIntoScreens(5000, 900, new[] { tall, shortOne }));
    }

    [Fact]
    public void No_switch_in_a_corner_or_with_a_button_held()
    {
        var (c, h, _) = Controller(Mac());
        c.Handle(RoamInput.Pointer(1919, 10, false)); // top-right corner (within 24 points)
        c.Handle(RoamInput.Pointer(1919, 1070, false)); // bottom-right corner
        Assert.Null(h.Entered);
        c.Handle(RoamInput.Pointer(1919, 500, true)); // a button held: no switch…
        Assert.Null(h.Entered);
        c.Handle(RoamInput.Pointer(1919, 30, false)); // just outside the corner
        Assert.NotNull(h.Entered);
    }

    [Fact]
    public void Moving_away_follows_the_target_and_comes_home_at_the_same_proportion()
    {
        var (c, h, _) = Controller(Mac());
        c.Handle(RoamInput.Pointer(1919, 540, false));
        c.Handle(RoamInput.Motion(300, -91)); // to (300, 400) on the Mac
        Assert.Equal((300.0, 400.0), c.Virtual);
        Assert.Equal((300.0, 400.0), h.Moves.Last());
        c.Handle(RoamInput.Motion(5000, 0)); // clamped at the Mac's right edge, never off-screen
        Assert.Equal(1511, c.Virtual.X);
        c.Handle(RoamInput.KeyEvent(RoamKeys.LeftCtrl, true)); // held while we come home
        // Back across the Mac's left edge at 3/4 of its height: home on this PC's right edge at 3/4 of 1080.
        c.Handle(RoamInput.Motion(-1511, 736.5 - 400));
        c.Handle(RoamInput.Motion(-10, 0));
        Assert.Equal(RoamMode.Home, c.Mode);
        Assert.Equal(1, h.Leaves);
        var (ux, uy) = h.Uncaptured!.Value;
        Assert.Equal(1917, ux); // two pixels inside the edge
        Assert.Equal(810, uy, 1);
        // The key still down on the Mac was let go before leaving (mapped: ⌘).
        Assert.Contains(h.Sent, m => m.Type == RoamWire.Key && (int)m.Data["usage"]! == RoamKeys.LeftGui && !(bool)m.Data["down"]!);
    }

    [Fact]
    public void Edge_glow_shows_within_40_points_of_a_shared_edge_only()
    {
        var (c, h, _) = Controller(Mac(RoamSide.Left));
        c.Handle(RoamInput.Pointer(1900, 500, false)); // near the right edge: nothing there
        Assert.Null(h.Glow);
        c.Handle(RoamInput.Pointer(30, 500, false));
        Assert.NotNull(h.Glow);
        Assert.Equal(RoamSide.Left, h.Glow!.Side);
        Assert.Equal("← B Mac", h.Glow.PillText);
        Assert.Equal(500, h.Glow.Along);
        c.Handle(RoamInput.Pointer(80, 500, false));
        Assert.Null(h.Glow);
    }

    // ---------------- learning a moved computer ----------------

    [Fact]
    public void Three_pushes_on_an_empty_edge_within_two_and_a_half_seconds_move_the_computer_there()
    {
        var learner = new DeadEdgeLearner();
        var t = new DateTime(2026, 1, 1);
        bool Push(double toward) => learner.Feed(RoamSide.Left, true, toward, 0, t);
        void Away(double d) => learner.Feed(RoamSide.Left, false, 0, d, t);
        Assert.False(Push(31));           // push 1
        Assert.False(Push(50));           // still at the edge: no new push until it moves away
        Away(10);                         // not far enough
        Assert.False(Push(40));
        Away(25);
        t = t.AddSeconds(1);
        Assert.False(Push(15));           // not yet 30
        Assert.False(Push(15));           // push 2
        Away(25);
        t = t.AddSeconds(1);
        Assert.True(Push(30));            // push 3 within 2.5 s
        // Spread out over more than 2.5 s it doesn't learn.
        var slow = new DeadEdgeLearner();
        var s = new DateTime(2026, 1, 1);
        for (var i = 0; i < 3; i++)
        {
            Assert.False(slow.Feed(RoamSide.Right, true, 40, 0, s));
            slow.Feed(RoamSide.Right, false, 0, 30, s);
            s = s.AddSeconds(1.5);
        }
        Assert.False(new DeadEdgeLearner().Feed(RoamSide.Above, true, 100, 0, s)); // top/bottom never learn

        // The controller: the Mac sits right; pushing the empty left edge three times moves it left.
        var (c, h, clock) = Controller(Mac(RoamSide.Right));
        for (var i = 0; i < 3; i++)
        {
            c.Handle(RoamInput.Pointer(-20, 500, false));
            c.Handle(RoamInput.Pointer(-20, 500, false));
            c.Handle(RoamInput.Pointer(60, 500, false));
            clock.Advance(400);
        }
        Assert.Equal(RoamSide.Left, h.LearnedSide!.Value.Side);
        Assert.Equal("B Mac", h.LearnedSide.Value.Peer.PeerId);
    }

    // ---------------- the outbox ----------------

    [Fact]
    public void Moves_go_at_once_and_coalesce_only_while_a_frame_is_in_flight_never_jumping_a_key()
    {
        // SPACE.md §12.3: no fixed timer. The pump takes a move the moment it is offered…
        var box = new RoamOutbox();
        box.OfferMove(1, 1);
        Assert.True(box.TryTake(out var first));
        Assert.Equal(RoamWire.Move, first.Type);
        // …and while that frame is being written, newer positions replace the one waiting: only the newest goes next.
        for (var i = 2; i <= 6; i++) box.OfferMove(i, i);
        Assert.Equal(1, box.Count);
        box.Enqueue(RoamWire.Key, RoamWire.KeyData(4, true));
        box.OfferMove(7, 7); // after the key: its own slot
        Assert.Equal(3, box.Count);
        Assert.True(box.TryTake(out var move));
        Assert.Equal(6, (double)move.Data!["x"]!);
        Assert.True(box.TryTake(out var key));
        Assert.Equal(RoamWire.Key, key.Type);
        Assert.True(box.TryTake(out var last));
        Assert.Equal(7, (double)last.Data!["x"]!);
        Assert.False(box.TryTake(out _));

        // §12.4 relative motion waiting at the end adds up; a key between keeps them apart.
        box.OfferRel(3, -1);
        box.OfferRel(2, 2);
        box.Enqueue(RoamWire.Button, RoamWire.ButtonData(RoamButton.Left, true));
        box.OfferRel(-1, 0);
        Assert.True(box.TryTake(out var rel));
        Assert.Equal(RoamWire.Rel, rel.Type);
        Assert.Equal((5, 1), ((int)rel.Data!["dx"]!, (int)rel.Data!["dy"]!));
        Assert.True(box.TryTake(out _));
        Assert.True(box.TryTake(out var rel2));
        Assert.Equal(-1, (int)rel2.Data!["dx"]!);
    }

    // ---------------- keys ----------------

    [Fact]
    public void Modifiers_map_between_mac_and_pc_both_ways()
    {
        // Mac → PC, ⌘ as Ctrl (default): ⌘ → Ctrl, Control → Windows key, Option → Alt.
        Assert.Equal(RoamKeys.LeftCtrl, RoamKeys.MapModifier(RoamKeys.LeftGui, "mac", "windows", true));
        Assert.Equal(RoamKeys.LeftGui, RoamKeys.MapModifier(RoamKeys.LeftCtrl, "mac", "windows", true));
        Assert.Equal(RoamKeys.RightCtrl, RoamKeys.MapModifier(RoamKeys.RightGui, "mac", "windows", true));
        Assert.Equal(RoamKeys.LeftAlt, RoamKeys.MapModifier(RoamKeys.LeftAlt, "mac", "windows", true));
        // PC → Mac is the inverse: Ctrl → ⌘, Win → Control, Alt → Option.
        Assert.Equal(RoamKeys.LeftGui, RoamKeys.MapModifier(RoamKeys.LeftCtrl, "windows", "mac", true));
        Assert.Equal(RoamKeys.LeftCtrl, RoamKeys.MapModifier(RoamKeys.LeftGui, "windows", "mac", true));
        Assert.Equal(RoamKeys.RightAlt, RoamKeys.MapModifier(RoamKeys.RightAlt, "windows", "mac", true));
        // Off, or the same kind of computer: plain.
        Assert.Equal(RoamKeys.LeftGui, RoamKeys.MapModifier(RoamKeys.LeftGui, "windows", "mac", false));
        Assert.Equal(RoamKeys.LeftCtrl, RoamKeys.MapModifier(RoamKeys.LeftCtrl, "windows", "windows", true));
        Assert.Equal(0x04, RoamKeys.MapModifier(0x04, "windows", "mac", true));
        // The controller applies it: Ctrl+C on this PC reaches the Mac as ⌘C.
        var (c, h, _) = Controller(Mac());
        c.Handle(RoamInput.Pointer(1919, 540, false));
        c.Handle(RoamInput.KeyEvent(RoamKeys.LeftCtrl, true));
        c.Handle(RoamInput.KeyEvent(0x06, true));
        c.Handle(RoamInput.KeyEvent(0x06, false));
        c.Handle(RoamInput.KeyEvent(RoamKeys.LeftCtrl, false));
        c.Handle(RoamInput.KeyEvent(0x07, false)); // went down before crossing: stays local
        var keys = h.Sent.Where(m => m.Type == RoamWire.Key).Select(m => ((int)m.Data["usage"]!, (bool)m.Data["down"]!)).ToList();
        Assert.Equal(new[] { (RoamKeys.LeftGui, true), (0x06, true), (0x06, false), (RoamKeys.LeftGui, false) }, keys);
    }

    [Fact]
    public void Hid_usages_and_windows_scancodes_round_trip_including_extended_keys()
    {
        var aliases = new HashSet<int> { 0x32, RoamKeys.ConsumerMute, RoamKeys.ConsumerVolumeUp, RoamKeys.ConsumerVolumeDown };
        var checkedKeys = 0;
        foreach (var usage in RoamKeys.KnownUsages)
        {
            if (aliases.Contains(usage)) continue;
            var k = RoamKeys.ToWindows(usage)!.Value;
            var back = RoamKeys.FromWindows(k.Scan, k.Extended, k.Vk);
            Assert.True(back == usage, $"usage 0x{usage:X} → scan 0x{k.Scan:X}{(k.Extended ? " E0" : "")} vk 0x{k.Vk:X} → {back}");
            checkedKeys++;
        }
        Assert.True(checkedKeys > 120);
        // Extended pairs stay apart: keypad Enter vs Enter, arrows vs keypad, right Ctrl/Alt vs left.
        Assert.Equal(new WinKey(0x1C, true), RoamKeys.ToWindows(0x58));
        Assert.Equal(new WinKey(0x1C, false), RoamKeys.ToWindows(0x28));
        Assert.Equal(0x52, RoamKeys.FromWindows(0x48, true, 0x26)); // ↑
        Assert.Equal(0x60, RoamKeys.FromWindows(0x48, false, 0x68)); // keypad 8
        Assert.Equal(RoamKeys.RightAlt, RoamKeys.FromWindows(0x38, true, 0xA5));
        Assert.Equal(RoamKeys.LeftGui, RoamKeys.FromWindows(0x5B, true, 0x5B));
        Assert.Null(RoamKeys.FromWindows(0x21D, false, 0xA2)); // AltGr's fake Left Ctrl
        // Media keys: by virtual key on Windows, consumer usages on the wire.
        Assert.Equal(RoamKeys.PlayPause, RoamKeys.FromWindows(0x22, true, 0xB3));
        Assert.Equal(0x000C00CD, RoamKeys.PlayPause);
        Assert.Equal(RoamKeys.VolumeUp, RoamKeys.FromWindows(0x30, true, 0xAF));
        Assert.True(RoamKeys.ToWindows(RoamKeys.ConsumerVolumeUp)!.Value.ByVk);
        Assert.Null(RoamKeys.ToWindows(RoamKeys.BrightnessUp)); // Windows has no brightness key to type
    }

    [Fact]
    public void Wheel_travels_in_120ths_of_a_notch_with_up_positive()
    {
        Assert.Equal(120, RoamWheel.HighWord(0x0078_0000u));
        Assert.Equal(-120, RoamWheel.HighWord(0xFF88_0000u));
        Assert.Equal((0, -240), RoamWheel.FromWindows(-240, false));
        Assert.Equal((60, 0), RoamWheel.FromWindows(60, true)); // a fine-grained tilt to the right
        Assert.Equal((-120, 0), RoamWheel.ToWindows(0, -120)); // (vertical, horizontal)
        Assert.Equal((0, 30), RoamWheel.ToWindows(30, 0));
        Assert.Equal((12000, 0), RoamWheel.ToWindows(0, 1e9)); // clamped
        Assert.Equal((0, 0), RoamWheel.ToWindows(double.NaN, double.NaN));
    }

    // ---------------- safety ----------------

    [Fact]
    public void No_pong_for_two_seconds_releases_and_a_lost_link_releases_without_the_network()
    {
        var (c, h, clock) = Controller(Mac());
        c.Handle(RoamInput.Pointer(1919, 300, false));
        Assert.Equal(RoamMode.Away, c.Mode);
        clock.Advance(1500);
        Assert.False(c.Tick());
        c.Pong("B Mac");
        clock.Advance(1900);
        Assert.False(c.Tick());
        clock.Advance(200); // 2.1 s since the last pong
        Assert.True(c.Tick());
        Assert.Equal(RoamMode.Home, c.Mode);
        Assert.Equal((1917.0, 300.0), h.Uncaptured!.Value); // home at the edge it left from
        Assert.False(c.Tick());

        var (c2, h2, _) = Controller(Mac());
        c2.Handle(RoamInput.Pointer(1919, 700, false));
        c2.Handle(RoamInput.KeyEvent(0x04, true));
        var sentBefore = h2.Sent.Count;
        c2.LinkLost("B Mac");
        Assert.Equal(RoamMode.Home, c2.Mode);
        Assert.Equal(sentBefore, h2.Sent.Count); // nothing sent: releasing never needs the network
        Assert.Equal(0, h2.Leaves);
        Assert.Equal((1917.0, 700.0), h2.Uncaptured!.Value);

        // ctlRelease from the target: home where we left, with key-ups and ctlLeave.
        var (c3, h3, _) = Controller(Mac());
        c3.Handle(RoamInput.Pointer(1919, 200, false));
        c3.Handle(RoamInput.KeyEvent(0x04, true));
        c3.Released("B Mac");
        Assert.Equal(RoamMode.Home, c3.Mode);
        Assert.Equal(1, h3.Leaves);
        Assert.Contains(h3.Sent, m => m.Type == RoamWire.Key && (int)m.Data["usage"]! == 0x04 && !(bool)m.Data["down"]!);
    }

    [Fact]
    public void A_file_drag_crosses_with_the_button_held_and_drops_at_the_virtual_cursor()
    {
        var (c, h, clock) = Controller(Mac());
        c.Handle(RoamInput.Pointer(1919, 540, true));
        clock.Advance(150);
        c.Handle(RoamInput.Pointer(1919, 540, true));
        Assert.NotNull(h.Probe); // the drop catcher goes under the cursor
        Assert.Equal(RoamMode.Home, c.Mode);
        c.SetFileDrag(new[] { "/Users/h/report.pdf" });
        c.Handle(RoamInput.Pointer(1921, 540, true));
        Assert.Equal(RoamMode.Away, c.Mode);
        Assert.False(h.Captured!.Value.Hide); // the drag stays parked, visible, on the catcher
        var begin = h.Sent.Single(m => m.Type == RoamWire.DragBegin).Data;
        Assert.Equal("report.pdf", (string)begin["name"]!);
        Assert.Equal(1, (int)begin["count"]!);
        c.Handle(RoamInput.Motion(400, 100));
        c.Handle(RoamInput.Press(RoamButton.Left, false));
        Assert.Equal((400.0, 591.0), (h.Dropped!.Value.X, h.Dropped.Value.Y));
        Assert.Equal("B Mac", h.Dropped.Value.Peer.PeerId);
        Assert.Contains(h.Sent, m => m.Type == RoamWire.Button && (string)m.Data["b"]! == "left" && !(bool)m.Data["down"]!);
        Assert.Equal(RoamMode.Away, c.Mode); // still on the Mac after the drop
    }

    // ---------------- JSON ----------------

    [Fact]
    public void Ctl_messages_have_the_spec_shape()
    {
        var hello = new CtlHello(true, "windows", new[] { new RoamScreen(@"\\.\DISPLAY1", 0, 0, 1920, 1080) }, null, true);
        var json = LinkServer.BuildEnvelope(RoamWire.Hello, hello.ToWire());
        using (var doc = JsonDocument.Parse(json))
        {
            var d = doc.RootElement.GetProperty("data");
            Assert.Equal("ctlHello", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal(1, d.GetProperty("v").GetInt32());
            Assert.True(d.GetProperty("enabled").GetBoolean());
            Assert.Equal("windows", d.GetProperty("platform").GetString());
            Assert.Equal(JsonValueKind.Null, d.GetProperty("manualSide").ValueKind); // explicit null, as in the spec
            Assert.True(d.GetProperty("cmdAsCtrl").GetBoolean());
            Assert.Equal(1920, d.GetProperty("screens")[0].GetProperty("w").GetDouble());
        }
        // A Mac's hello parses.
        using (var mac = JsonDocument.Parse("""{"v":1,"enabled":true,"platform":"mac","screens":[{"id":"Apple-1-2-Built-in","x":0,"y":0,"w":1512,"h":982}],"manualSide":"left","cmdAsCtrl":false}"""))
        {
            var h = CtlHello.Parse(mac.RootElement);
            Assert.Equal("mac", h.Platform);
            Assert.Equal(RoamSide.Left, h.ManualSide);
            Assert.False(h.CmdAsCtrl);
            Assert.Equal(new RoamRect(0, 0, 1512, 982), Assert.Single(h.Screens).Rect);
        }
        Assert.Equal("""{"type":"ctlEnter","data":{"x":0,"y":420.5,"clipboard":null}}""", LinkServer.BuildEnvelope(RoamWire.Enter, RoamWire.EnterData(0, 420.5, null)));
        Assert.Equal("""{"type":"ctlEnter","data":{"x":0,"y":1,"clipboard":{"text":"hi"}}}""", LinkServer.BuildEnvelope(RoamWire.Enter, RoamWire.EnterData(0, 1, new RoamClip("hi", null))));
        Assert.Equal("""{"type":"ctlMove","data":{"x":12.5,"y":431}}""", LinkServer.BuildEnvelope(RoamWire.Move, RoamWire.MoveData(12.5, 431.0004)));
        Assert.Equal("""{"type":"ctlButton","data":{"b":"forward","down":true}}""", LinkServer.BuildEnvelope(RoamWire.Button, RoamWire.ButtonData(RoamButton.Forward, true)));
        Assert.Equal("""{"type":"ctlWheel","data":{"dx":0,"dy":-120}}""", LinkServer.BuildEnvelope(RoamWire.Wheel, RoamWire.WheelData(0, -120)));
        Assert.Equal("""{"type":"ctlKey","data":{"usage":4,"down":true}}""", LinkServer.BuildEnvelope(RoamWire.Key, RoamWire.KeyData(4, true)));
        Assert.Equal("""{"type":"ctlLeave","data":{"clipboard":null}}""", LinkServer.BuildEnvelope(RoamWire.Leave, RoamWire.LeaveData(null)));
        Assert.Equal("""{"type":"ctlRelease","data":{}}""", LinkServer.BuildEnvelope(RoamWire.Release, RoamWire.ReleaseData()));
        Assert.Equal("""{"type":"ctlDragBegin","data":{"id":"d1","count":2,"name":"2 files"}}""", LinkServer.BuildEnvelope(RoamWire.DragBegin, RoamWire.DragBeginData("d1", 2, "2 files")));
        Assert.Equal("""{"type":"ctlDragCancel","data":{"id":"d1"}}""", LinkServer.BuildEnvelope(RoamWire.DragCancel, RoamWire.DragCancelData("d1")));
        using var at = JsonDocument.Parse("""{"id":"f","at":{"x":640.5,"y":200}}""");
        Assert.Equal((640.5, 200.0), RoamWire.At(at.RootElement));
        using var big = JsonDocument.Parse(JsonSerializer.Serialize(new { clipboard = new { text = new string('x', RoamWire.MaxClipboardBytes + 1) } }));
        Assert.Null(RoamWire.ClipboardText(big.RootElement)); // over 1 MB: not applied
    }

    [Fact]
    public async Task Dialled_peer_sockets_have_nagle_off()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accept = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            await using var stream = await PeerNetwork.ConnectNoDelayAsync(new DnsEndPoint("127.0.0.1", port), TestContext.Current.CancellationToken);
            Assert.True(((NetworkStream)stream).Socket.NoDelay);
            using var server = await accept;
        }
        finally { listener.Stop(); }
    }

    // ---------------- two computers, in process ----------------

    internal static SpaceOptions FastSpace() => new()
    {
        ActivitySampler = () => new ActivitySample(false, 300),
        ActivityCheck = TimeSpan.FromMilliseconds(100),
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

    sealed class Pair : IAsyncDisposable
    {
        public required TestHost A, B;
        public required FakeRoamPlatform PA, PB;
        public async ValueTask DisposeAsync()
        {
            await A.DisposeAsync();
            await B.DisposeAsync();
        }
    }

    /// <summary>"A PC" (1920×1080) and "B PC" (2560×1440 at 200 % = 1280×720 points), linked directly, Roam ready.</summary>
    /// <remarks>Without <paramref name="room"/> both behave like an H.I.B before the room (§9.2 edges only).</remarks>
    static async Task<Pair> LinkedAsync(bool room = false)
    {
        var pa = new FakeRoamPlatform();
        var pb = new FakeRoamPlatform();
        var a = new TestHost(name: "A PC", space: FastSpace(), roam: new RoamOptions { Platform = () => pa, Room = room });
        var b = new TestHost(name: "B PC", space: FastSpace(), roam: new RoamOptions { Platform = () => pb, Room = room });
        a.Core.SpaceEngine.SetSpaceKey(SpaceKey);
        b.Core.SpaceEngine.SetSpaceKey(SpaceKey);
        a.Core.SpaceEngine.RememberPeer("B PC", "B PC", "windows", new[] { "127.0.0.1" }, b.Core.Link.Port);
        b.Core.SpaceEngine.RememberPeer("A PC", "A PC", "windows", new[] { "127.0.0.1" }, a.Core.Link.Port);
        a.Core.Roam.SetDisplays(new[] { Pc });
        b.Core.Roam.SetDisplays(new[] { new RoamDisplay(@"\\.\DISPLAY1", new RoamRect(0, 0, 2560, 1440), 2, true) });
        Assert.True(await Eventually.TrueAsync(() => a.Core.Roam.StatusFor("B PC") is { Ready: true } && b.Core.Roam.StatusFor("A PC") is { Ready: true },
            TimeSpan.FromSeconds(10)));
        return new Pair { A = a, B = b, PA = pa, PB = pb };
    }

    [Fact]
    public async Task Two_computers_share_one_mouse_and_keyboard_over_the_peer_link()
    {
        await using var p = await LinkedAsync();
        var (a, b, pa, pb) = (p.A, p.B, p.PA, p.PB);
        // No aims, no choice yet: the other computer is on the right, and the Devices tab asks.
        var status = a.Core.Roam.StatusFor("B PC")!;
        Assert.Equal(RoamSide.Right, status.Side);
        Assert.True(status.NeedsAsk);
        Assert.Equal("Move the cursor off the right edge to go to B PC", status.Status);
        Assert.Equal(RoamInputMode.Watch, pa.Mode);

        // Copy on A, cross: B's clipboard has it instantly.
        a.Clipboard.Text = "copied on A";
        pa.Raise(RoamInput.Pointer(1925, 540, false));
        Assert.Equal(RoamMode.Away, a.Core.Roam.Mode);
        Assert.Equal(RoamInputMode.Capture, pa.Mode);
        Assert.True(await Eventually.TrueAsync(() => b.Core.Roam.ControlledById == "A PC"));
        Assert.Equal(RoamInputMode.Controlled, pb.Mode);
        Assert.True(await Eventually.TrueAsync(() => pb.Moves.Count >= 1));
        Assert.Equal((0.0, 720.0), pb.Moves[0]); // B's left edge, half way down (360 points × 2)
        Assert.True(await Eventually.TrueAsync(() => b.Clipboard.Text == "copied on A"));
        Assert.Equal(RoamRole.Controlled, b.Core.Roam.StatusFor("A PC")!.Role);
        // In use follows the cursor.
        Assert.True(await Eventually.TrueAsync(() => b.Core.SpaceEngine.OwnActivity.InUse && !a.Core.SpaceEngine.OwnActivity.InUse));

        // Motion, buttons, wheel and keys arrive in order.
        for (var i = 0; i < 50; i++) pa.Raise(RoamInput.Motion(4, 2));
        pa.Raise(RoamInput.Press(RoamButton.Left, true));
        pa.Raise(RoamInput.Press(RoamButton.Left, false));
        pa.Raise(RoamInput.Scroll(0, -120));
        pa.Raise(RoamInput.KeyEvent(0x04, true));
        pa.Raise(RoamInput.KeyEvent(0x04, false));
        Assert.True(await Eventually.TrueAsync(() => pb.Keys.Count == 2));
        Assert.Equal(new[] { (0x04, true), (0x04, false) }, pb.Keys); // PC → PC: no mapping
        Assert.Equal(new[] { (RoamButton.Left, true), (RoamButton.Left, false) }, pb.Buttons);
        Assert.Equal(new[] { (0, -120) }, pb.Wheels);
        Assert.Equal((400.0, 920.0), pb.Moves.Last()); // (200, 460) points, coalesced but exact at the end
        Assert.True(pb.Moves.Count < 52);

        // Someone touches B's own mouse: B lets go, A comes home where it left.
        pa.Raise(RoamInput.KeyEvent(0x05, true)); // still held when released
        Assert.True(await Eventually.TrueAsync(() => pb.Keys.Count == 3));
        pb.Raise(RoamInput.Physical());
        Assert.True(await Eventually.TrueAsync(() => a.Core.Roam.Mode == RoamMode.Home));
        Assert.Equal((1917.0, 540.0), pa.Unparked!.Value);
        Assert.Null(b.Core.Roam.ControlledById);
        Assert.True(await Eventually.TrueAsync(() => pb.Keys.Contains((0x05, false)))); // lifted on B
        Assert.Equal(RoamInputMode.Watch, pa.Mode);
        Assert.Equal(RoamInputMode.Watch, pb.Mode);

        // Cross again and come back over B's left edge: ctlLeave carries B-side nothing new; A is home.
        pa.Raise(RoamInput.Pointer(1925, 100, false));
        Assert.True(await Eventually.TrueAsync(() => b.Core.Roam.ControlledById == "A PC"));
        pa.Raise(RoamInput.Motion(-50, 0));
        Assert.True(await Eventually.TrueAsync(() => b.Core.Roam.ControlledById is null));
        Assert.Equal(RoamMode.Home, a.Core.Roam.Mode);
    }

    [Fact]
    public async Task Side_choices_are_shared_mirrored_and_a_new_point_setup_clears_them()
    {
        await using var p = await LinkedAsync();
        var (a, b) = (p.A, p.B);
        a.Core.Roam.SetSide("B PC", RoamSide.Left);
        Assert.True(await Eventually.TrueAsync(() => b.Core.Roam.StatusFor("A PC") is { Side: RoamSide.Right, SideSource: RoamSideSource.Manual }));
        Assert.False(b.Core.Roam.StatusFor("A PC")!.NeedsAsk);
        b.Core.Roam.SetCmdAsCtrl("A PC", false);
        Assert.True(await Eventually.TrueAsync(() => a.Core.Roam.StatusFor("B PC") is { CmdAsCtrl: false }));
        b.Core.Roam.SetEnabled("A PC", false);
        Assert.True(await Eventually.TrueAsync(() => a.Core.Roam.StatusFor("B PC") is { Ready: false, Status: "Turned off on B PC" }));
        Assert.Equal(RoamInputMode.Off, p.PA.Mode);
        b.Core.Roam.SetEnabled("A PC", true);
        Assert.True(await Eventually.TrueAsync(() => a.Core.Roam.StatusFor("B PC") is { Ready: true }));

        // Point setup on the phone: B is to the left by the aims; the manual choice is cleared on both.
        var layout = new[] { new ScreenAim("A PC", "1", 200, 0), new ScreenAim("B PC", "1", 160, 0) };
        a.Core.Roam.ApplyLayout(layout);
        b.Core.Roam.ApplyLayout(layout);
        Assert.True(await Eventually.TrueAsync(() => a.Core.Roam.StatusFor("B PC") is { Side: RoamSide.Left, SideSource: RoamSideSource.PointSetup }
                                                 && b.Core.Roam.StatusFor("A PC") is { Side: RoamSide.Right, SideSource: RoamSideSource.PointSetup }));
        // The same layout again (the phone re-sends it on reconnect) changes nothing.
        await Task.Delay(500); // let both cleared hellos cross first
        a.Core.Roam.SetSide("B PC", RoamSide.Right);
        a.Core.Roam.ApplyLayout(layout);
        Assert.Equal(RoamSideSource.Manual, a.Core.Roam.StatusFor("B PC")!.SideSource);
    }

    [Fact]
    public async Task ScreenLayout_from_the_phone_sets_the_side()
    {
        await using var p = await LinkedAsync();
        await using var phone = await p.A.ConnectPhoneAsync();
        await phone.SendAsync("screenLayout", new { screens = new[] { new { computer = "A PC", screen = "1", h = 214.0, e = 8.5 }, new { computer = "B PC", screen = "1", h = 230.0, e = 9.0 } } });
        Assert.True(await Eventually.TrueAsync(() => p.A.Core.Roam.StatusFor("B PC") is { Side: RoamSide.Right, SideSource: RoamSideSource.PointSetup, NeedsAsk: false }));
        Assert.Equal(2, p.A.Core.Roam.Layout.Count);
    }

    [Fact]
    public async Task Pushing_an_empty_edge_moves_the_other_computer_there_and_undo_puts_it_back()
    {
        await using var p = await LinkedAsync();
        RoamLearnedNotice? notice = null;
        p.A.Core.Roam.SideLearned += n => notice = n;
        for (var i = 0; i < 3; i++)
        {
            p.PA.Raise(RoamInput.Pointer(-40, 500, false));
            p.PA.Raise(RoamInput.Pointer(80, 500, false));
        }
        Assert.Equal(new RoamLearnedNotice("B PC", "B PC", RoamSide.Left), notice);
        Assert.Equal(RoamSide.Left, p.A.Core.Roam.StatusFor("B PC")!.Side);
        Assert.True(await Eventually.TrueAsync(() => p.B.Core.Roam.StatusFor("A PC")!.Side == RoamSide.Right && p.B.Core.Roam.StatusFor("A PC")!.SideSource == RoamSideSource.Manual));
        p.A.Core.Roam.UndoLearned();
        Assert.Equal(RoamSideSource.Default, p.A.Core.Roam.StatusFor("B PC")!.SideSource);
        Assert.True(await Eventually.TrueAsync(() => p.B.Core.Roam.StatusFor("A PC")!.SideSource == RoamSideSource.Default));
    }

    [Fact]
    public async Task Files_dragged_across_the_edge_land_as_a_flick_card_at_the_drop_point()
    {
        await using var p = await LinkedAsync();
        var file = SpaceTests.MakePng(p.A.Root, "holiday.png", 320, 200);
        FlickInfo? flick = null;
        ReceivedFile? received = null;
        RoamGhost? ghost = null;
        p.B.Core.Space.FlickArrived += f => flick = f;
        p.B.Core.Files.Received += f => received = f;
        p.B.Core.Roam.GhostChanged += g => { if (g is not null) ghost = g; };

        p.PA.Raise(RoamInput.Pointer(1919, 540, true));
        await Task.Delay(150);
        p.PA.Raise(RoamInput.Pointer(1919, 540, true));
        p.A.Core.Roam.SetFileDrag(new[] { file });
        p.PA.Raise(RoamInput.Pointer(1921, 540, true));
        Assert.Equal(RoamMode.Away, p.A.Core.Roam.Mode);
        Assert.True(await Eventually.TrueAsync(() => ghost is not null));
        Assert.Equal("holiday.png", ghost!.Name);
        p.PA.Raise(RoamInput.Motion(300, -100));
        p.PA.Raise(RoamInput.Press(RoamButton.Left, false));
        Assert.True(await Eventually.TrueAsync(() => received is not null, TimeSpan.FromSeconds(10)));
        Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(received!.Path));
        Assert.Equal((300.0, 260.0), flick!.At);
        Assert.Equal("A PC", flick.From);
        Assert.True(await Eventually.TrueAsync(() => p.B.Core.Roam.Ghost is null));
        Assert.DoesNotContain(p.PB.Buttons, x => x.B == RoamButton.Left); // the drop isn't a click over there
    }

    [Fact]
    public async Task Link_loss_while_away_releases_at_once()
    {
        await using var p = await LinkedAsync();
        p.PA.Raise(RoamInput.Pointer(1919, 640, false));
        Assert.True(await Eventually.TrueAsync(() => p.B.Core.Roam.ControlledById == "A PC"));
        p.PA.Raise(RoamInput.KeyEvent(0x04, true));
        Assert.True(await Eventually.TrueAsync(() => p.PB.Keys.Count == 1));
        p.B.Core.SpaceEngine.Network.DropAll("gone"); // B's link vanishes
        Assert.True(await Eventually.TrueAsync(() => p.A.Core.Roam.Mode == RoamMode.Home, TimeSpan.FromSeconds(8)));
        Assert.Equal((1917.0, 640.0), p.PA.Unparked!.Value);
    }

    // ---------------- a Mac peer speaking the raw protocol ----------------

    [Fact]
    public async Task A_mac_drives_this_pc_and_this_pc_drives_the_mac_with_cmd_as_ctrl()
    {
        var pc = new FakeRoamPlatform();
        await using var host = new TestHost(name: "Office PC", space: FastSpace(), roam: new RoamOptions { Platform = () => pc });
        host.Core.SpaceEngine.SetSpaceKey(SpaceKey);
        host.Core.Roam.SetDisplays(new[] { Pc });
        await using var mac = await RawMac.DialAsync(host.Core.Link.Port, SpaceKey);
        Assert.True(await Eventually.TrueAsync(() => mac.Of("ctlHello").Count > 0));
        var ours = mac.Of("ctlHello")[0];
        Assert.Equal("windows", ours.GetProperty("platform").GetString());
        Assert.Equal(1920, ours.GetProperty("screens")[0].GetProperty("w").GetDouble());

        // The Mac says hello; it sits left of the PC (its manualSide says the PC is on its right).
        await mac.SendRawAsync("""{"type":"ctlHello","data":{"v":1,"enabled":true,"platform":"mac","screens":[{"id":"Built-in","x":0,"y":0,"w":1512,"h":982}],"manualSide":"right","cmdAsCtrl":true}}""");
        Assert.True(await Eventually.TrueAsync(() => host.Core.Roam.StatusFor("A Mac") is { Ready: true, Side: RoamSide.Left, IsMac: true }));

        // The Mac drives: keys arrive already mapped by the Mac (⌘C → Ctrl+C) and are typed by scancode here.
        await mac.SendRawAsync("""{"type":"ctlEnter","data":{"x":0,"y":420,"clipboard":{"text":"from the Mac"}}}""");
        await mac.SendRawAsync("""{"type":"ctlMove","data":{"x":12.5,"y":431}}""");
        await mac.SendRawAsync("""{"type":"ctlKey","data":{"usage":224,"down":true}}""");
        await mac.SendRawAsync("""{"type":"ctlKey","data":{"usage":6,"down":true}}""");
        await mac.SendRawAsync("""{"type":"ctlWheel","data":{"dx":0,"dy":-120}}""");
        Assert.True(await Eventually.TrueAsync(() => pc.Keys.Count == 2 && pc.Wheels.Count == 1));
        Assert.Equal((12.5, 431.0), pc.Moves.Last());
        Assert.Equal("from the Mac", host.Clipboard.Text);
        Assert.Equal(new[] { (0, -120) }, pc.Wheels);
        // It leaves without lifting its keys: this PC lifts them itself.
        await mac.SendRawAsync("""{"type":"ctlLeave","data":{"clipboard":null}}""");
        Assert.True(await Eventually.TrueAsync(() => host.Core.Roam.ControlledById is null));
        Assert.True(host.Core.Roam.DrainInput(TimeSpan.FromSeconds(2)));
        Assert.Contains((224, false), pc.Keys);
        Assert.Contains((6, false), pc.Keys);

        // This PC drives the Mac: Ctrl on the PC is ⌘ on the Mac; the Mac's left edge is where the PC's left goes in.
        pc.Raise(RoamInput.Pointer(-3, 540, false));
        Assert.True(await Eventually.TrueAsync(() => mac.Of("ctlEnter").Count == 1));
        var enter = mac.Of("ctlEnter")[0];
        Assert.Equal(1511, enter.GetProperty("x").GetDouble());
        Assert.Equal(491, enter.GetProperty("y").GetDouble(), 1);
        pc.Raise(RoamInput.KeyEvent(RoamKeys.LeftCtrl, true));
        pc.Raise(RoamInput.KeyEvent(0x19, true)); // V
        Assert.True(await Eventually.TrueAsync(() => mac.Of("ctlKey").Count == 2));
        Assert.Equal(RoamKeys.LeftGui, mac.Of("ctlKey")[0].GetProperty("usage").GetInt32());
        Assert.Equal(0x19, mac.Of("ctlKey")[1].GetProperty("usage").GetInt32());

        // The Mac's user touches its own trackpad.
        await mac.SendRawAsync("""{"type":"ctlRelease","data":{}}""");
        Assert.True(await Eventually.TrueAsync(() => host.Core.Roam.Mode == RoamMode.Home));
        Assert.True(await Eventually.TrueAsync(() => mac.Of("ctlLeave").Count == 1));
        var ups = mac.Of("ctlKey").Skip(2).Select(k => (k.GetProperty("usage").GetInt32(), k.GetProperty("down").GetBoolean())).ToList();
        Assert.Contains((RoamKeys.LeftGui, false), ups);
        Assert.Contains((0x19, false), ups);
    }
}

/// <summary>A Mac on the peer link (SPACE.md §3.3), speaking the JSON of SPACE.md §9.3 by hand.</summary>
sealed class RawMac : IAsyncDisposable
{
    readonly ClientWebSocket _ws = new();
    readonly List<JsonElement> _got = new();
    readonly CancellationTokenSource _cts = new();

    readonly byte[] _key;

    /// <summary>Stops answering pings (a computer gone quiet).</summary>
    public bool Mute { get; set; }

    RawMac(byte[] key) => _key = key;

    public static async Task<RawMac> DialAsync(int port, byte[] key, string id = "A Mac")
    {
        var m = new RawMac(key);
        await m._ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/peer"), CancellationToken.None);
        await m.SendAsync("peerHello", new { id, name = id, platform = "mac", type = "MacBook Pro", port = 1, ips = new[] { "127.0.0.1" }, v = 1 });
        _ = m.ReceiveAsync();
        return m;
    }

    readonly SemaphoreSlim _send = new(1, 1);

    public Task SendAsync(string type, object data) => SendRawAsync(LinkServer.BuildEnvelope(type, data));

    /// <summary>One send at a time (a WebSocket allows a single outstanding send; pongs go out from the receive loop).</summary>
    public async Task SendRawAsync(string json)
    {
        await _send.WaitAsync();
        try { await _ws.SendAsync(Encoding.UTF8.GetBytes(LinkCrypto.Encrypt(json, _key)), WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { _send.Release(); }
    }

    async Task ReceiveAsync()
    {
        var buf = new byte[256 * 1024];
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                var count = 0;
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf, count, buf.Length - count), _cts.Token);
                    count += r.Count;
                } while (!r.EndOfMessage && count < buf.Length);
                if (r.MessageType == WebSocketMessageType.Close) return;
                var plain = LinkCrypto.TryDecrypt(Encoding.UTF8.GetString(buf, 0, count), _key);
                if (plain is null) continue;
                using var doc = JsonDocument.Parse(plain);
                if (doc.RootElement.GetProperty("type").GetString() == "ping")
                {
                    if (Mute) continue;
                    await SendAsync("pong", new { });
                    continue;
                }
                lock (_got) _got.Add(doc.RootElement.Clone());
            }
        }
        catch { }
    }

    public List<JsonElement> Of(string type)
    {
        lock (_got) return _got.Where(e => e.GetProperty("type").GetString() == type).Select(e => e.GetProperty("data")).ToList();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _ws.Abort(); } catch { }
        _ws.Dispose();
        await Task.CompletedTask;
    }
}
