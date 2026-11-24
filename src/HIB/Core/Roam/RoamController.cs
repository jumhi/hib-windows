// H.I.B Roam (docs/SPACE.md §9.1–§9.2, §9.5): the controller side as a pure state machine.
// Home: watch the physical pointer; at an outer edge that faces a ready neighbour (not in a corner, no
// button held unless a file drag is in progress) cross over. Away: local input is captured; motion
// moves a virtual cursor on the target's screens, clamped into real screens; crossing the target's
// edge that faces us comes home at the same proportional position. Modelled on Deskflow's server
// (onMouseMovePrimary / onMouseMoveSecondary / isSwitchOkay), written for H.I.B.
// With a room (SPACE.md §10.3), a computer placed in it is reached through portals instead: the edge segments
// where one of this PC's monitors faces one of its screens. Away, the virtual cursor leaves the target's
// screens the same way — home, or straight on to a third computer.
// Round 2 (SPACE.md §12.4–§12.5): a computer whose app in front locks the cursor (a game) gets relative motion and is
// never left by an edge; with a full-screen app on either side only the gate (top 15 % of the shared edge) opens, on a
// double push; the lock hotkey keeps the mouse where it is, the jump hotkeys move it to the computer on that side; each
// computer has its own pointer and scroll speed (and natural scrolling).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using HIB.Core.Space;

namespace HIB.Core.Roam;

/// <summary>
/// A linked computer Roam can move to, with everything the controller needs. <see cref="InRoom"/>: it is reached through the
/// room's portals (§10.3), and <see cref="Side"/> is only where its group sits (for words and learning).
/// </summary>
public sealed record RoamNeighbor(string PeerId, string Name, RoamSide Side, IReadOnlyList<RoamRect> Screens, string Platform, bool CmdAsCtrl,
    bool InRoom = false, RoamGameMode Game = RoamGameMode.Normal, double PointerSpeed = 1, double ScrollSpeed = 1, bool NaturalScroll = false);

public enum RoamPillKind { Gate, GameMode, Locked, Unlocked, AdminApp }

/// <summary>A short message pill (SPACE.md §12.2, §12.4, §12.5). <see cref="At"/>: beside that edge (pixels), else mid-screen.</summary>
public sealed record RoamPill(RoamPillKind Kind, string Text, RoamEdgeHint? At = null)
{
    public const string GateText = "Game running — push again here to leave";
    public const string UnlockedText = "Unlocked · the mouse moves between computers again";
    /// <summary>The hotkeys as a PC keyboard says them.</summary>
    public const string LeaveKeys = "Scroll Lock or Ctrl+Alt+Shift+L";
    public static string GameModeText(string name) => $"Game mode · the mouse stays on {name} · {LeaveKeys} to leave";
    public static string LockedText(string name) => $"Locked to {name}";
}

/// <summary>
/// The edge glow: which edge of which local monitor (pixels) and where along it the cursor is (pixels). With a room, the glow
/// covers only the portal: <see cref="SegStart"/>…<see cref="SegEnd"/> (pixels along the edge; both 0 = the whole edge).
/// </summary>
public sealed record RoamEdgeHint(RoamSide Side, string PeerId, string Name, RoamRect Screen, double Along, double SegStart = 0, double SegEnd = 0)
{
    public string PillText => RoamSides.PillText(Side, Name);
    public bool HasSegment => SegEnd > SegStart;
}

public enum RoamMode { Home, Away }

/// <summary>What the controller asks the engine to do (network, platform, UI).</summary>
public interface IRoamControllerHost
{
    /// <summary>Send ctlEnter (with the clipboard if it changed).</summary>
    void Enter(RoamNeighbor peer, double x, double y);
    void Send(string peerId, string type, JsonObject data);
    /// <summary>Queue a coalesced ctlMove.</summary>
    void Move(string peerId, double x, double y);
    /// <summary>Send ctlLeave (with the clipboard if it changed).</summary>
    void Leave(RoamNeighbor peer);
    void Capture(double parkX, double parkY, bool hide);
    void Uncapture(double x, double y);
    void Hint(RoamEdgeHint? hint);
    /// <summary>Show (or hide, null) the invisible drop catcher under the cursor at the edge to learn whether a file drag is on.</summary>
    void ProbeDrag(RoamEdgeHint? at);
    /// <summary>The files were dropped on the target at (x, y) (target points).</summary>
    void Drop(RoamNeighbor peer, double x, double y, IReadOnlyList<string> files);
    void FinishLocalDrag(bool cancel);
    /// <summary>A dead edge was pushed 3 times: <paramref name="peer"/> now sits on <paramref name="side"/> (of monitor <paramref name="screenId"/>).</summary>
    void Learned(RoamSide side, RoamNeighbor peer, string? screenId);
    void StateChanged();
    /// <summary>§12.4: queue a coalesced ctlRel (relative pixels) for a computer whose game locks the cursor.</summary>
    void Rel(string peerId, int dx, int dy) { }
    /// <summary>§12.4–§12.5: show a pill (null hides the one showing).</summary>
    void Pill(RoamPill? pill) { }
}

public sealed class RoamController
{
    /// <summary>A button held this long at an edge shows the drop catcher (so a click near the edge doesn't).</summary>
    public static readonly TimeSpan ProbeDelay = TimeSpan.FromMilliseconds(120);

    readonly IRoamControllerHost _host;
    readonly Func<DateTime> _clock;
    readonly DeadEdgeLearner _learner = new();
    readonly RoamWatchdog _watchdog = new();
    readonly HashSet<int> _heldKeys = new();
    readonly HashSet<RoamButton> _heldButtons = new();
    IReadOnlyList<RoamDisplay> _displays = Array.Empty<RoamDisplay>();
    List<RoamRect> _pixels = new();
    RoamRect _bounds;
    IReadOnlyList<RoamNeighbor> _neighbors = Array.Empty<RoamNeighbor>();
    RoamNeighbor? _target;
    double _vx, _vy;
    (double X, double Y) _exit;
    double _parkScale = 1;
    RoamEdgeHint? _hint;
    bool _anyButton;
    // file drag (§9.5)
    IReadOnlyList<string>? _dragFiles;
    string? _dragId;
    bool _dragAway;
    DateTime? _buttonAtEdgeSince;
    RoamEdgeHint? _probe;
    // room (§10.3)
    RoomMap _room = RoomMap.Empty;
    string _own = "";
    // round 2 (§12.4–§12.5)
    readonly DoublePushGate _push = new();
    (RoamSide Side, RoamRect Edge)? _pushEdge;
    readonly RoamDelta _rel = new();
    readonly RoamDelta _wheel = new();
    RoamGameMode _localGame;
    bool _userLock;
    RoamSide _exitSide;
    (double X, double Y) _lastPointer;
    RoamPill? _pill;

    static readonly RoamSide[] AllSides = { RoamSide.Left, RoamSide.Right, RoamSide.Above, RoamSide.Below };

    public RoamController(IRoamControllerHost host, Func<DateTime>? clock = null)
    {
        _host = host;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>This computer's platform ("windows"): the controller maps modifiers for the target.</summary>
    public string LocalPlatform { get; set; } = "windows";

    public RoamMode Mode => _target is null ? RoamMode.Home : RoamMode.Away;
    public RoamNeighbor? Target => _target;
    public (double X, double Y) Virtual => (_vx, _vy);
    public (double X, double Y) ExitPoint => _exit;
    public IReadOnlyCollection<int> HeldKeys => _heldKeys;
    public bool DraggingFiles => _dragAway;
    public IReadOnlyList<string>? DragFiles => _dragFiles;
    public RoamEdgeHint? CurrentHint => _hint;
    public IReadOnlyList<RoamNeighbor> Neighbors => _neighbors;

    /// <summary>This computer's name (the lock pill says "Locked to &lt;name&gt;" at home).</summary>
    public string LocalName { get; set; } = "this PC";

    /// <summary>The lock hotkey holds the mouse on the computer it is on (§12.5).</summary>
    public bool UserLock => _userLock;

    /// <summary>The state of this computer's own app in front (§12.4; Normal when game mode is off).</summary>
    public RoamGameMode LocalGame => _localGame;

    /// <summary>The pill showing now (tests).</summary>
    public RoamPill? CurrentPill => _pill;

    /// <summary>The target's game locks the cursor: motion goes as ctlRel and no edge leads away.</summary>
    public bool RelativeMode => _target is { Game: RoamGameMode.Locked };

    public void SetLocalGame(RoamGameMode mode)
    {
        if (mode == _localGame) return;
        _localGame = mode;
        _push.Reset();
        _pushEdge = null;
        if (_pill?.Kind == RoamPillKind.Gate) SetPill(null);
    }

    /// <summary>
    /// Leaving a computer with a full-screen (or locked) app in front — this one at home, the target while away — only the gate
    /// opens. Coming back into the game's computer is never gated.
    /// </summary>
    bool Gated(RoamNeighbor? away) => away is null ? _localGame != RoamGameMode.Normal : away.Game != RoamGameMode.Normal;

    public void SetDisplays(IReadOnlyList<RoamDisplay> displays)
    {
        _displays = displays.Where(d => !d.Pixels.IsEmpty).ToList();
        _pixels = _displays.Select(d => d.Pixels).ToList();
        _bounds = RoamGeometry.Bounds(_pixels);
    }

    /// <summary>The room (this computer and the computers placed with it) and this computer's id in it.</summary>
    public void SetRoom(RoomMap room, string own)
    {
        _room = room;
        _own = own;
    }

    public RoomMap Room => _room;

    bool IsOwn(string id) => SpaceIds.Same(id, _own);

    RoamNeighbor? RoomNeighbor(string id) => _neighbors.FirstOrDefault(n => n.InRoom && SpaceIds.Same(n.PeerId, id));

    bool IsRoomTarget(string id) => RoomNeighbor(id) is not null;

    public void SetNeighbors(IReadOnlyList<RoamNeighbor> neighbors)
    {
        _neighbors = neighbors.ToList();
        if (_target is { } t)
        {
            var now = _neighbors.FirstOrDefault(n => string.Equals(n.PeerId, t.PeerId, StringComparison.OrdinalIgnoreCase));
            if (now is null) ComeHome(_exit, sendLeave: true);
            else
            {
                _target = now;
                if (now.Game != t.Game) GameChanged(t.Game, now);
            }
        }
        if (_neighbors.Count == 0) SetHint(null);
    }

    // ---------------- input ----------------

    public void Handle(RoamInput input)
    {
        switch (input.Kind)
        {
            case RoamInputKind.Pointer:
                if (_target is null) OnPointer(input.X, input.Y, input.AnyButtonDown);
                break;
            case RoamInputKind.Motion:
                if (_target is not null) OnMotion(input.X, input.Y);
                break;
            case RoamInputKind.Button:
                OnButton(input.Button, input.Down);
                break;
            case RoamInputKind.Wheel:
                if (_target is { } t && (input.Dx != 0 || input.Dy != 0)) OnWheel(t, input.Dx, input.Dy);
                break;
            case RoamInputKind.Key:
                if (_target is not null) OnKey(input.Usage, input.Down);
                break;
            case RoamInputKind.Hotkey:
                Hotkey(input.Hotkey);
                break;
        }
    }

    void OnPointer(double px, double py, bool anyButton)
    {
        _anyButton = anyButton;
        if (!anyButton) EndDragWatch();
        if (_pixels.Count == 0) return;
        var now = _clock();
        var (cx, cy) = RoamGeometry.ClampIntoScreens(px, py, _pixels);
        _lastPointer = (cx, cy);
        var scale = RoamGeometry.ScaleAt(cx, cy, _displays);
        if (_userLock)
        {
            // §12.5 locked to this computer: no glow, no crossing, no learning.
            SetHint(null);
            return;
        }
        if (_pushEdge is { } pe && Gated(null))
            _push.Feed(pe, false, DistanceToScreenEdge(pe.Side, cx, cy, pe.Edge) / scale, now);
        var screenIndex = RoamGeometry.ScreenAt(cx, cy, _pixels);
        var screen = screenIndex >= 0 ? _pixels[screenIndex] : _bounds;
        var display = screenIndex >= 0 ? _displays[screenIndex] : null;

        // Learn a moved computer from pushes against an empty left/right edge.
        foreach (var side in new[] { RoamSide.Left, RoamSide.Right })
        {
            if (_neighbors.Any(n => n.Side == side) || OuterEdgeHasPortal(side)) continue;
            var atEdge = RoamGeometry.AtEdge(side, cx, cy, _bounds);
            var toward = RoamGeometry.Beyond(side, px, py, _bounds) / scale;
            var distance = RoamGeometry.DistanceToEdge(side, cx, cy, _bounds) / scale;
            if (_learner.Feed(side, atEdge, toward, distance, now))
            {
                var opposite = _neighbors.Where(n => n.Side == RoamSides.Mirror(side)).ToList();
                if (opposite.Count == 1)
                {
                    _host.Learned(side, opposite[0], display?.Id);
                    return; // the next push goes across; this one only taught us
                }
            }
        }

        // The edge glow: the nearest shared edge within 40 points, on a monitor that reaches it.
        RoamEdgeHint? hint = null;
        var best = double.MaxValue;
        foreach (var n in _neighbors)
        {
            if (n.InRoom || !ReachesEdge(n.Side, screen)) continue;
            var d = RoamGeometry.DistanceToEdge(n.Side, cx, cy, _bounds) / scale;
            if (d > RoamGeometry.GlowDistance || d >= best) continue;
            best = d;
            hint = new RoamEdgeHint(n.Side, n.PeerId, n.Name, screen, RoamSides.IsHorizontal(n.Side) ? cy : cx);
        }
        if (display is not null && RoomHint(display, screen, cx, cy, scale, best) is { } roomHint) hint = roomHint;
        SetHint(hint);

        // Cross over (Deskflow's jump zone: the outermost pixel of the desktop; with a room, a portal on this monitor's edge).
        if (display is not null && TryRoomCross(display, screen, cx, cy, scale, anyButton, now)) return;
        foreach (var n in _neighbors)
        {
            if (n.InRoom || !RoamGeometry.AtEdge(n.Side, cx, cy, _bounds)) continue;
            if (RoamGeometry.InCorner(n.Side, cx, cy, _pixels, RoamGeometry.CornerSize * scale)) continue;
            if (anyButton && _dragFiles is null)
            {
                WaitForDrag(new RoamEdgeHint(n.Side, n.PeerId, n.Name, screen, RoamSides.IsHorizontal(n.Side) ? cy : cx), now);
                return;
            }
            var horizontal = RoamSides.IsHorizontal(n.Side);
            if (!GateOpen(n.Side, screen, horizontal ? cy : cx, scale, null, n, screen, now)) return;
            Cross(n, cx, cy, scale);
            return;
        }
        // Not at a shared edge: no probing (a found drag stays known until the button comes up).
        _buttonAtEdgeSince = null;
        if (_dragFiles is null) SetProbe(null);
    }

    /// <summary>A button is held at a shared edge: only a file drag may cross. Ask the drop catcher whether this is one.</summary>
    void WaitForDrag(RoamEdgeHint at, DateTime now)
    {
        _buttonAtEdgeSince ??= now;
        if (now - _buttonAtEdgeSince >= ProbeDelay) SetProbe(at);
    }

    // ---------------- the room (§10.3) ----------------

    /// <summary>The cursor sits on a monitor's own edge on <paramref name="side"/>.</summary>
    static bool AtScreenEdge(RoamSide side, double x, double y, RoamRect s) => side switch
    {
        RoamSide.Left => x <= s.X,
        RoamSide.Right => x >= s.MaxX,
        RoamSide.Above => y <= s.Y,
        _ => y >= s.MaxY,
    };

    /// <summary>Another of this PC's monitors carries on right past that edge (an inner edge, not one the room can use).</summary>
    bool LocalContinues(RoamSide side, RoamRect s, double x, double y)
    {
        var (bx, by) = side switch
        {
            RoamSide.Left => (s.X - 1, y),
            RoamSide.Right => (s.Right, y),
            RoamSide.Above => (x, s.Y - 1),
            _ => (x, s.Bottom),
        };
        return _pixels.Any(p => p.Contains(bx, by));
    }

    static double DistanceToScreenEdge(RoamSide side, double x, double y, RoamRect s) => side switch
    {
        RoamSide.Left => Math.Max(0, x - s.X),
        RoamSide.Right => Math.Max(0, s.MaxX - x),
        RoamSide.Above => Math.Max(0, y - s.Y),
        _ => Math.Max(0, s.MaxY - y),
    };

    /// <summary>A portal on any monitor's outer edge on <paramref name="side"/> (that edge isn't dead).</summary>
    bool OuterEdgeHasPortal(RoamSide side)
    {
        if (_room.Portals.Count == 0) return false;
        foreach (var d in _displays)
        {
            if (!ReachesEdge(side, d.Pixels)) continue;
            if (_room.Portals.Any(p => p.Side == side && p.FromScreen == d.Id && IsOwn(p.FromComputer) && IsRoomTarget(p.ToComputer))) return true;
        }
        return false;
    }

    /// <summary>
    /// The glow for the nearest portal edge of this monitor within 40 points (if nearer than <paramref name="best"/>): the cursor within
    /// 40 points of the portal along the edge too (the Mac's RoomCursor.glow). It covers only the portal.
    /// </summary>
    RoamEdgeHint? RoomHint(RoamDisplay display, RoamRect screen, double cx, double cy, double scale, double best)
    {
        if (_room.Find(_own, display.Id) is null) return null;
        RoamEdgeHint? hint = null;
        var s = display.SafeScale;
        foreach (var side in AllSides)
        {
            var d = DistanceToScreenEdge(side, cx, cy, screen) / scale;
            if (d > RoamGeometry.GlowDistance || d >= best) continue;
            var along = (RoamSides.IsHorizontal(side) ? cy : cx) / s; // DIPs along the edge
            foreach (var portal in _room.Portals)
            {
                if (portal.Side != side || portal.FromScreen != display.Id || !IsOwn(portal.FromComputer)) continue;
                if (RoomNeighbor(portal.ToComputer) is not { } n || _room.Segment(portal) is not { } seg) continue;
                if (along < seg.Start - RoamGeometry.GlowDistance || along > seg.End + RoamGeometry.GlowDistance) continue;
                var clamped = Math.Clamp(along, seg.Start, seg.End) * s;
                var (ex, ey) = RoamSides.IsHorizontal(side) ? (side == RoamSide.Left ? screen.X : screen.MaxX, clamped) : (clamped, side == RoamSide.Above ? screen.Y : screen.MaxY);
                if (LocalContinues(side, screen, ex, ey)) continue;
                best = d;
                hint = new RoamEdgeHint(side, n.PeerId, n.Name, screen, RoamSides.IsHorizontal(side) ? cy : cx, seg.Start * s, seg.End * s);
                break;
            }
        }
        return hint;
    }

    bool TryRoomCross(RoamDisplay display, RoamRect screen, double cx, double cy, double scale, bool anyButton, DateTime now)
    {
        if (_room.Portals.Count == 0 || _room.Find(_own, display.Id) is null) return false;
        foreach (var side in AllSides)
        {
            if (!AtScreenEdge(side, cx, cy, screen) || LocalContinues(side, screen, cx, cy)) continue;
            var c = _room.Cross(_own, display.Id, side, cx / display.SafeScale, cy / display.SafeScale, IsRoomTarget);
            if (c is null || RoomNeighbor(c.Portal.ToComputer) is not { } n) continue;
            if (RoamGeometry.InCorner(side, cx, cy, new[] { screen }, RoamGeometry.CornerSize * scale)) continue;
            if (anyButton && _dragFiles is null)
            {
                WaitForDrag(new RoamEdgeHint(side, n.PeerId, n.Name, screen, RoamSides.IsHorizontal(side) ? cy : cx), now);
                return true;
            }
            if (!GateOpen(side, screen, RoamSides.IsHorizontal(side) ? cy : cx, scale, null, n, screen, now)) return true;
            Enter(n, c.X, c.Y, side, cx, cy, scale);
            return true;
        }
        return false;
    }

    bool ReachesEdge(RoamSide side, RoamRect screen) => side switch
    {
        RoamSide.Left => screen.X <= _bounds.X,
        RoamSide.Right => screen.MaxX >= _bounds.MaxX,
        RoamSide.Above => screen.Y <= _bounds.Y,
        _ => screen.MaxY >= _bounds.MaxY,
    };

    void Cross(RoamNeighbor n, double cx, double cy, double scale)
    {
        if (n.Screens.Count == 0) return;
        var ratio = RoamGeometry.EdgeRatio(n.Side, cx, cy, _bounds);
        var (vx, vy) = RoamGeometry.EnterAt(RoamSides.Mirror(n.Side), ratio, n.Screens);
        Enter(n, vx, vy, n.Side, cx, cy, scale);
    }

    /// <summary>Goes to <paramref name="n"/> at (<paramref name="vx"/>, <paramref name="vy"/>) (its DIPs), leaving this PC over <paramref name="side"/> at (cx, cy).</summary>
    void Enter(RoamNeighbor n, double vx, double vy, RoamSide side, double cx, double cy, double scale)
    {
        _target = n;
        _vx = vx;
        _vy = vy;
        _exit = Inside(side, cx, cy, RoamGeometry.ReturnInset * scale);
        _exitSide = side;
        _heldKeys.Clear();
        _heldButtons.Clear();
        _rel.Reset();
        _wheel.Reset();
        _push.Reset();
        _pushEdge = null;
        SetHint(null);
        SetPill(n.Game == RoamGameMode.Locked ? GamePill(n) : null);
        var drag = _dragFiles is { Count: > 0 };
        // A file drag stays parked where it left (on the drop catcher); otherwise park hidden mid-screen.
        var park = drag ? _exit : ParkPoint();
        _parkScale = RoamGeometry.ScaleAt(park.X, park.Y, _displays);
        _host.Capture(park.X, park.Y, hide: !drag);
        _host.Enter(n, vx, vy);
        if (drag)
        {
            _dragAway = true;
            _dragId = Guid.NewGuid().ToString();
            var files = _dragFiles!;
            var name = files.Count == 1 ? System.IO.Path.GetFileName(files[0]) : $"{files.Count} files";
            _host.Send(n.PeerId, RoamWire.DragBegin, RoamWire.DragBeginData(_dragId, files.Count, name));
            // The local drag stays parked on the drop catcher, so a local drop lands nowhere else.
            var screen = _pixels[Math.Max(0, RoamGeometry.ScreenAt(_exit.X, _exit.Y, _pixels))];
            SetProbe(new RoamEdgeHint(side, n.PeerId, n.Name, screen, RoamSides.IsHorizontal(side) ? _exit.Y : _exit.X), force: true);
        }
        else SetProbe(null);
        _watchdog.Arm(_clock());
        _host.StateChanged();
    }

    /// <summary>The primary monitor's centre (Deskflow parks the hidden cursor at the centre).</summary>
    (double X, double Y) ParkPoint()
    {
        var d = _displays.FirstOrDefault(x => x.Primary) ?? _displays.FirstOrDefault();
        if (d is null) return (0, 0);
        return (Math.Round(d.Pixels.X + d.Pixels.W / 2), Math.Round(d.Pixels.Y + d.Pixels.H / 2));
    }

    (double X, double Y) Inside(RoamSide side, double x, double y, double inset)
    {
        var p = side switch
        {
            RoamSide.Left => (x + inset, y),
            RoamSide.Right => (x - inset, y),
            RoamSide.Above => (x, y + inset),
            _ => (x, y - inset),
        };
        return RoamGeometry.ClampIntoScreens(p.Item1, p.Item2, _pixels);
    }

    void OnMotion(double dxPx, double dyPx)
    {
        var t = _target!;
        var speed = RoamSpeed.Clamp(t.PointerSpeed);
        if (t.Game == RoamGameMode.Locked)
        {
            // §12.4: the game steers with raw motion; the virtual cursor stays put and no edge leads away.
            var (rx, ry) = _rel.Add(dxPx * speed, dyPx * speed);
            if (rx != 0 || ry != 0) _host.Rel(t.PeerId, rx, ry);
            return;
        }
        var nx = _vx + dxPx * speed / _parkScale;
        var ny = _vy + dyPx * speed / _parkScale;
        if (_userLock)
        {
            // §12.5 locked to this computer: it stops at the edges.
            Stay(t, nx, ny);
            return;
        }
        if (t.InRoom && _room.Has(t.PeerId))
        {
            RoomMotion(t, nx, ny);
            return;
        }
        var home = RoamSides.Mirror(t.Side);
        var tb = RoamGeometry.Bounds(t.Screens);
        if (_pushEdge is { } pe && Gated(t))
            _push.Feed(pe, false, DistanceToScreenEdge(pe.Side, nx, ny, pe.Edge), _clock());
        if (RoamGeometry.Beyond(home, nx, ny, tb) > 0)
        {
            var (cx, cy) = RoamGeometry.ClampIntoScreens(nx, ny, t.Screens);
            var ok = RoamGeometry.AtEdge(home, cx, cy, tb)
                     && !RoamGeometry.InCorner(home, cx, cy, t.Screens, RoamGeometry.CornerSize)
                     && (_heldButtons.Count == 0 || _dragAway);
            if (ok && Gated(t))
            {
                // §12.6: the gate is measured on the screen the cursor is on.
                var on = t.Screens[Math.Max(0, RoamGeometry.ScreenAt(cx, cy, t.Screens))];
                ok = GateOpen(home, on, RoamSides.IsHorizontal(home) ? cy : cx, 1, t, t, null, _clock());
            }
            if (ok)
            {
                var ratio = RoamGeometry.EdgeRatio(home, cx, cy, tb);
                var scale = RoamGeometry.ScaleAt(_exit.X, _exit.Y, _displays);
                var (lx, ly) = RoamGeometry.PointOnEdge(t.Side, ratio, _bounds, RoamGeometry.ReturnInset * scale);
                ComeHome(RoamGeometry.ClampIntoScreens(lx, ly, _pixels), sendLeave: true);
                return;
            }
        }
        Stay(t, nx, ny);
    }

    /// <summary>The virtual cursor moves on the target's screens and stops at their edges.</summary>
    void Stay(RoamNeighbor t, double nx, double ny)
    {
        var rects = t.InRoom && _room.Has(t.PeerId) ? _room.Of(t.PeerId).Select(s => s.Dips).ToList() : t.Screens;
        var (x, y) = RoamGeometry.ClampIntoScreens(nx, ny, rects);
        if (x == _vx && y == _vy) return;
        _vx = x;
        _vy = y;
        _host.Move(t.PeerId, x, y);
    }

    /// <summary>
    /// Away with a room: the virtual cursor stays on the target's screens; leaving one where no other screen of it carries on, a
    /// portal takes it home or on to a third computer (§10.3). Otherwise it stops at the edge.
    /// </summary>
    void RoomMotion(RoamNeighbor t, double nx, double ny)
    {
        var screens = _room.Of(t.PeerId);
        var rects = screens.Select(s => s.Dips).ToList();
        if (_pushEdge is { } pe && Gated(t))
            _push.Feed(pe, false, DistanceToScreenEdge(pe.Side, nx, ny, pe.Edge), _clock());
        if (!rects.Any(r => r.Contains(nx, ny)))
        {
            var i = RoamGeometry.ScreenAt(_vx, _vy, rects);
            if (i >= 0 && ExitSide(rects[i], nx, ny) is { } side)
            {
                var cur = screens[i];
                var (cx, cy) = cur.Dips.Clamp(nx, ny);
                var ok = !RoamGeometry.InCorner(side, cx, cy, new[] { cur.Dips }, RoamGeometry.CornerSize) && (_heldButtons.Count == 0 || _dragAway);
                var c = ok ? _room.Cross(t.PeerId, cur.Id, side, cx, cy, id => IsOwn(id) || (!_dragAway && IsRoomTarget(id) && !SpaceIds.Same(id, t.PeerId))) : null;
                if (c is not null && !GateOpen(side, cur.Dips, RoamSides.IsHorizontal(side) ? cy : cx, 1, t, t, null, _clock())) c = null;
                if (c is not null)
                {
                    if (IsOwn(c.Portal.ToComputer))
                    {
                        ComeHome(LocalEntry(c), sendLeave: true);
                        return;
                    }
                    if (RoomNeighbor(c.Portal.ToComputer) is { } next)
                    {
                        SwitchTarget(next, c.X, c.Y);
                        return;
                    }
                }
            }
        }
        var (x, y) = RoamGeometry.ClampIntoScreens(nx, ny, rects);
        if (x == _vx && y == _vy) return;
        _vx = x;
        _vy = y;
        _host.Move(t.PeerId, x, y);
    }

    /// <summary>The edge of <paramref name="r"/> a point outside it went furthest past, or null when it is inside.</summary>
    static RoamSide? ExitSide(RoamRect r, double x, double y)
    {
        var over = new (RoamSide Side, double By)[]
        {
            (RoamSide.Left, r.X - x),
            (RoamSide.Right, x - r.MaxX),
            (RoamSide.Above, r.Y - y),
            (RoamSide.Below, y - r.MaxY),
        };
        var best = over.MaxBy(o => o.By);
        return best.By > 0 ? best.Side : null;
    }

    /// <summary>Coming home through a portal: the entry point (already just inside the edge) on this PC's monitor, in pixels.</summary>
    (double X, double Y) LocalEntry(RoomCrossing c)
    {
        var d = _displays.FirstOrDefault(x => x.Id == c.To.Id) ?? _displays[0];
        var s = d.SafeScale;
        var (px, py) = d.Pixels.Clamp(Math.Round(c.X * s), Math.Round(c.Y * s));
        return RoamGeometry.ClampIntoScreens(px, py, _pixels);
    }

    /// <summary>Straight on from one computer to another (3–4 computers): lift what's held there, leave it, enter the next.</summary>
    void SwitchTarget(RoamNeighbor next, double x, double y)
    {
        var old = _target!;
        foreach (var k in _heldKeys.ToList()) _host.Send(old.PeerId, RoamWire.Key, RoamWire.KeyData(k, false));
        foreach (var b in _heldButtons.ToList()) _host.Send(old.PeerId, RoamWire.Button, RoamWire.ButtonData(b, false));
        _host.Leave(old);
        _heldKeys.Clear();
        _heldButtons.Clear();
        _rel.Reset();
        _wheel.Reset();
        _push.Reset();
        _pushEdge = null;
        _target = next;
        _vx = x;
        _vy = y;
        SetPill(next.Game == RoamGameMode.Locked ? GamePill(next) : null);
        _host.Enter(next, x, y);
        _watchdog.Arm(_clock());
        _host.StateChanged();
    }

    void OnButton(RoamButton b, bool down)
    {
        if (_target is not { } t)
        {
            // Home: a button coming up ends any file drag we were watching.
            if (!down && b == RoamButton.Left) EndDragWatch();
            return;
        }
        if (_dragAway && b == RoamButton.Left && !down)
        {
            // §9.5: the drop lands on the target at the virtual cursor.
            _host.Send(t.PeerId, RoamWire.Button, RoamWire.ButtonData(RoamButton.Left, false));
            var files = _dragFiles ?? Array.Empty<string>();
            _dragAway = false;
            _dragId = null;
            _dragFiles = null;
            _host.FinishLocalDrag(cancel: false);
            _host.Drop(t, _vx, _vy, files);
            SetProbe(null);
            return;
        }
        if (down) _heldButtons.Add(b);
        else if (!_heldButtons.Remove(b)) return; // pressed before crossing: it stays local
        _host.Send(t.PeerId, RoamWire.Button, RoamWire.ButtonData(b, down));
    }

    void OnKey(int usage, bool down)
    {
        var t = _target!;
        if (_dragAway && usage == RoamKeys.Escape)
        {
            if (down) CancelDrag(t, local: true);
            return;
        }
        var mapped = RoamKeys.MapModifier(usage, LocalPlatform, t.Platform, t.CmdAsCtrl);
        if (down) _heldKeys.Add(mapped);
        else if (!_heldKeys.Remove(mapped)) return; // went down before crossing: it stays local
        _host.Send(t.PeerId, RoamWire.Key, RoamWire.KeyData(mapped, down));
    }

    void CancelDrag(RoamNeighbor t, bool local)
    {
        if (_dragId is { } id) _host.Send(t.PeerId, RoamWire.DragCancel, RoamWire.DragCancelData(id));
        _dragAway = false;
        _dragId = null;
        if (local)
        {
            _dragFiles = null;
            _host.FinishLocalDrag(cancel: true);
            SetProbe(null);
        }
    }

    void EndDragWatch()
    {
        _buttonAtEdgeSince = null;
        if (_target is null && (_dragFiles is not null || _probe is not null))
        {
            _dragFiles = null;
            SetProbe(null);
        }
    }

    /// <summary>The drop catcher saw a file drag (paths) or that it ended (null).</summary>
    public void SetFileDrag(IReadOnlyList<string>? files)
    {
        if (_target is not null && _dragAway) return;
        _dragFiles = files is { Count: > 0 } ? files.ToList() : null;
    }

    // ---------------- round 2: gate, game mode, wheel, hotkeys (§12.4–§12.5) ----------------

    /// <summary>
    /// Leaving a computer with a full-screen app in front (SPACE.md §12.4, §12.6): may the cursor cross this edge of
    /// <paramref name="screen"/> (the one it is on) now? Only inside the gate — the top 15 % of a left/right edge, the left-most
    /// 15 % of a top/bottom edge, at least 60 points (<paramref name="unit"/> = pixels per point) — and on the second push within
    /// 500 ms of the first. The first push shows "Game running — push again here to leave"; a push outside the gate only shows it
    /// (at the gate) and never counts.
    /// </summary>
    bool GateOpen(RoamSide side, RoamRect screen, double along, double unit, RoamNeighbor? away, RoamNeighbor n,
        RoamRect? homeScreen, DateTime now)
    {
        if (!Gated(away)) return true;
        var (s0, s1) = RoamSides.IsHorizontal(side) ? (screen.Y, screen.Bottom) : (screen.X, screen.Right);
        if (!RoamGate.Contains(along, s0, s1, unit))
        {
            if (_pill?.Kind != RoamPillKind.Gate) SetPill(new RoamPill(RoamPillKind.Gate, RoamPill.GateText, GateHint(side, n, homeScreen, s0, s1, unit)));
            return false;
        }
        var key = (side, screen);
        _pushEdge = key;
        switch (_push.Feed(key, true, 0, now))
        {
            case RoamPush.First:
                SetPill(new RoamPill(RoamPillKind.Gate, RoamPill.GateText, GateHint(side, n, homeScreen, s0, s1, unit)));
                return false;
            case RoamPush.Cross:
                if (_pill?.Kind == RoamPillKind.Gate) SetPill(null);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Where the gate pill shows: at the gate at home; away, at the top of the edge this PC was left by.</summary>
    RoamEdgeHint GateHint(RoamSide side, RoamNeighbor n, RoamRect? homeScreen, double s0, double s1, double unit)
    {
        if (homeScreen is { } screen)
        {
            var (g0, g1) = RoamGate.Of(s0, s1, unit);
            return new RoamEdgeHint(side, n.PeerId, n.Name, screen, (g0 + g1) / 2, g0, g1);
        }
        var exitScreen = ExitScreen();
        var scale = RoamGeometry.ScaleAt(_exit.X, _exit.Y, _displays);
        var horizontal = RoamSides.IsHorizontal(_exitSide);
        var (e0, e1) = horizontal ? (exitScreen.Y, exitScreen.Bottom) : (exitScreen.X, exitScreen.Right);
        var (h0, h1) = RoamGate.Of(e0, e1, scale);
        return new RoamEdgeHint(_exitSide, n.PeerId, n.Name, exitScreen, (h0 + h1) / 2, h0, h1);
    }

    RoamRect ExitScreen() => _pixels.Count == 0 ? _bounds : _pixels[Math.Max(0, RoamGeometry.ScreenAt(_exit.X, _exit.Y, _pixels))];

    RoamEdgeHint ExitHint(RoamNeighbor n)
        => new(_exitSide, n.PeerId, n.Name, ExitScreen(), RoamSides.IsHorizontal(_exitSide) ? _exit.Y : _exit.X);

    RoamPill GamePill(RoamNeighbor n) => new(RoamPillKind.GameMode, RoamPill.GameModeText(n.Name), ExitHint(n));

    /// <summary>The target's app in front changed state while we are on it.</summary>
    void GameChanged(RoamGameMode before, RoamNeighbor now)
    {
        _rel.Reset();
        _push.Reset();
        _pushEdge = null;
        if (now.Game == RoamGameMode.Locked) SetPill(GamePill(now));
        else if (before == RoamGameMode.Locked)
        {
            if (_pill?.Kind == RoamPillKind.GameMode) SetPill(null);
            // §12.6: the game let go of its cursor — the virtual cursor starts again from the centre of that screen.
            var rects = now.InRoom && _room.Has(now.PeerId) ? _room.Of(now.PeerId).Select(x => x.Dips).ToList() : now.Screens;
            if (rects.Count > 0)
            {
                var on = rects[Math.Max(0, RoamGeometry.ScreenAt(_vx, _vy, rects))];
                _vx = Math.Round(on.X + on.W / 2);
                _vy = Math.Round(on.Y + on.H / 2);
                _host.Move(now.PeerId, _vx, _vy);
            }
        }
    }

    /// <summary>§12.3 / §12.5: the wheel at the target's scroll speed (natural scrolling reverses it), fractions carried over.</summary>
    void OnWheel(RoamNeighbor t, int dx, int dy)
    {
        var (wx, wy) = RoamSpeed.Wheel(dx, dy, t.ScrollSpeed, t.NaturalScroll);
        var (ix, iy) = _wheel.Add(wx, wy);
        if (ix != 0 || iy != 0) _host.Send(t.PeerId, RoamWire.Wheel, RoamWire.WheelData(ix, iy));
    }

    /// <summary>
    /// §12.5 hotkeys from this computer's own keyboard. Lock: on a computer whose game holds the cursor it leaves the game (the
    /// mouse comes home); otherwise it toggles "Locked to &lt;name&gt;". Jump: to the computer on that side, centre of its main screen.
    /// </summary>
    public void Hotkey(RoamHotkey k)
    {
        if (k == RoamHotkey.Lock)
        {
            if (_target is { Game: RoamGameMode.Locked })
            {
                ComeHome(_exit, sendLeave: true);
                return;
            }
            _userLock = !_userLock;
            var at = _target is { } t ? ExitHint(t) : HereHint();
            SetPill(_userLock ? new RoamPill(RoamPillKind.Locked, RoamPill.LockedText(_target?.Name ?? LocalName), at)
                : new RoamPill(RoamPillKind.Unlocked, RoamPill.UnlockedText, at));
            if (_userLock) SetHint(null);
            _host.StateChanged();
            return;
        }
        if (RoamHotkeyDetector.JumpSide(k) is { } side) Jump(side);
    }

    /// <summary>The bottom of the monitor the cursor is on (pills at home).</summary>
    RoamEdgeHint? HereHint()
    {
        if (_pixels.Count == 0) return null;
        var screen = _pixels[Math.Max(0, RoamGeometry.ScreenAt(_lastPointer.X, _lastPointer.Y, _pixels))];
        return new RoamEdgeHint(RoamSide.Below, "", LocalName, screen, screen.X + screen.W / 2);
    }

    static (double X, double Y) Unit(RoamSide side) => side switch
    {
        RoamSide.Left => (-1, 0),
        RoamSide.Right => (1, 0),
        RoamSide.Above => (0, -1),
        _ => (0, 1),
    };

    static bool Toward(RoamSide side, double dx, double dy) => side switch
    {
        RoamSide.Left => dx < 0 && Math.Abs(dx) >= Math.Abs(dy),
        RoamSide.Right => dx > 0 && Math.Abs(dx) >= Math.Abs(dy),
        RoamSide.Above => dy < 0 && Math.Abs(dy) > Math.Abs(dx),
        _ => dy > 0 && Math.Abs(dy) > Math.Abs(dx),
    };

    /// <summary>The centre of a computer's main screen (the one at its origin 0,0, else the first listed), its points.</summary>
    static (double X, double Y) MainCentre(RoamNeighbor n)
    {
        var main = n.Screens.FirstOrDefault(r => r.X == 0 && r.Y == 0);
        if (main.IsEmpty) main = n.Screens[0];
        return (Math.Round(main.X + main.W / 2), Math.Round(main.Y + main.H / 2));
    }

    /// <summary>
    /// §12.5 jump: to the computer on that side of the one the cursor is on, landing at the centre of its main screen. With a room
    /// (§12.6) "on that side" compares group centres, nearest first; without one, the §9.2 sides (this PC at 0,0, each other computer
    /// one step towards its side).
    /// </summary>
    void Jump(RoamSide side)
    {
        _userLock = false;
        if (_pixels.Count == 0) return;
        var fromId = _target?.PeerId ?? _own;
        var room = _room.Screens.Count > 0 && _room.Has(fromId) && (_target is null || _target.InRoom);
        (double X, double Y)? Place(RoamNeighbor? c)
        {
            if (room)
            {
                var id = c?.PeerId ?? _own;
                if (c is { InRoom: false } || !_room.Has(id)) return null;
                var b = _room.BoundsOf(id);
                return b.IsEmpty ? null : (b.X + b.W / 2, b.Y + b.H / 2);
            }
            return c is null ? (0, 0) : c.InRoom ? null : Unit(c.Side);
        }
        if (Place(_target) is not { } from) return;
        (RoamNeighbor? To, double Distance)? best = null;
        var candidates = _neighbors.Where(x => x.Screens.Count > 0 && (_target is null || !SpaceIds.Same(x.PeerId, _target.PeerId))).Cast<RoamNeighbor?>();
        if (_target is not null) candidates = candidates.Append(null); // this PC
        foreach (var c in candidates)
        {
            if (Place(c) is not { } at) continue;
            var (dx, dy) = (at.X - from.X, at.Y - from.Y);
            if (!Toward(side, dx, dy)) continue;
            var d = dx * dx + dy * dy;
            if (best is null || d < best.Value.Distance) best = (c, d);
        }
        if (best is not { } pick) return;
        if (pick.To is null)
        {
            ComeHome(ParkPoint(), sendLeave: true);
            return;
        }
        var (vx, vy) = MainCentre(pick.To);
        if (_target is null)
        {
            var here = _lastPointer == default ? ParkPoint() : _lastPointer;
            Enter(pick.To, vx, vy, side, here.X, here.Y, RoamGeometry.ScaleAt(here.X, here.Y, _displays));
        }
        else SwitchTarget(pick.To, vx, vy);
    }

    void SetPill(RoamPill? pill)
    {
        if (pill is null && _pill is null) return;
        _pill = pill;
        _host.Pill(pill);
    }

    // ---------------- going home ----------------

    /// <summary>
    /// Back to this computer at <paramref name="at"/> (pixels): key-ups and button-ups for everything still held on the
    /// target, a drag cancel, ctlLeave — all best effort — then the cursor shows here at once (no waiting on the network).
    /// </summary>
    void ComeHome((double X, double Y) at, bool sendLeave)
    {
        if (_target is not { } t) return;
        if (sendLeave)
        {
            foreach (var k in _heldKeys.ToList()) _host.Send(t.PeerId, RoamWire.Key, RoamWire.KeyData(k, false));
            foreach (var b in _heldButtons.ToList()) _host.Send(t.PeerId, RoamWire.Button, RoamWire.ButtonData(b, false));
            if (_dragAway) CancelDrag(t, local: false); // the drag carries on here
            _host.Leave(t);
        }
        _heldKeys.Clear();
        _heldButtons.Clear();
        _dragAway = false;
        _dragId = null;
        _target = null;
        _userLock = false;
        _push.Reset();
        _pushEdge = null;
        if (_pill?.Kind is RoamPillKind.GameMode or RoamPillKind.Gate or RoamPillKind.Locked) SetPill(null);
        _watchdog.Disarm();
        SetProbe(null); // a drag still held carries on here, over whatever the user drops it on
        _host.Uncapture(at.X, at.Y);
        _host.StateChanged();
    }

    /// <summary>ctlRelease: someone touched the target's own mouse/keyboard — come home where we left.</summary>
    public void Released(string peerId)
    {
        if (_target is { } t && string.Equals(t.PeerId, peerId, StringComparison.OrdinalIgnoreCase)) ComeHome(_exit, sendLeave: true);
    }

    /// <summary>The link went away: release at once, nothing sent.</summary>
    public void LinkLost(string peerId)
    {
        if (_target is { } t && string.Equals(t.PeerId, peerId, StringComparison.OrdinalIgnoreCase)) ComeHome(_exit, sendLeave: false);
    }

    public void Pong(string peerId)
    {
        if (_target is { } t && string.Equals(t.PeerId, peerId, StringComparison.OrdinalIgnoreCase)) _watchdog.Pong(_clock());
    }

    /// <summary>Call often while away: no pong for 2 s releases at once.</summary>
    public bool Tick()
    {
        if (_target is null || !_watchdog.Expired(_clock())) return false;
        ComeHome(_exit, sendLeave: true); // best effort: if the link is just slow, the target still lets go
        return true;
    }

    /// <summary>Release without the network (quitting, Roam turned off).</summary>
    public void ReleaseNow() => ComeHome(_exit, sendLeave: false);

    /// <summary>Leave politely (Roam turned off for this pair while away).</summary>
    public void ReleaseWithLeave() => ComeHome(_exit, sendLeave: true);

    void SetHint(RoamEdgeHint? hint)
    {
        if (hint is null && _hint is null) return;
        if (hint is not null && _hint is not null && hint.Side == _hint.Side && hint.PeerId == _hint.PeerId && hint.Screen == _hint.Screen
            && Math.Abs(hint.Along - _hint.Along) < 4) return;
        _hint = hint;
        _host.Hint(hint);
    }

    void SetProbe(RoamEdgeHint? probe, bool force = false)
    {
        if (probe is null && _probe is null) return;
        if (!force && probe is not null && _probe is not null && probe.Side == _probe.Side && probe.Screen == _probe.Screen && Math.Abs(probe.Along - _probe.Along) < 8) return;
        _probe = probe;
        _host.ProbeDrag(probe);
    }
}
