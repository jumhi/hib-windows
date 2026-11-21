// H.I.B Roam (docs/SPACE.md §9.1–§9.2, §9.5): the controller side as a pure state machine.
// Home: watch the physical pointer; at an outer edge that faces a ready neighbour (not in a corner, no
// button held unless a file drag is in progress) cross over. Away: local input is captured; motion
// moves a virtual cursor on the target's screens, clamped into real screens; crossing the target's
// edge that faces us comes home at the same proportional position. Modelled on Deskflow's server
// (onMouseMovePrimary / onMouseMoveSecondary / isSwitchOkay), written for H.I.B.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace HIB.Core.Roam;

/// <summary>A linked computer Roam can move to, with everything the controller needs.</summary>
public sealed record RoamNeighbor(string PeerId, string Name, RoamSide Side, IReadOnlyList<RoamRect> Screens, string Platform, bool CmdAsCtrl);

/// <summary>The edge glow: which edge of which local monitor (pixels) and where along it the cursor is (pixels).</summary>
public sealed record RoamEdgeHint(RoamSide Side, string PeerId, string Name, RoamRect Screen, double Along)
{
    public string PillText => RoamSides.PillText(Side, Name);
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
    /// <summary>A dead edge was pushed 3 times: <paramref name="peer"/> now sits on <paramref name="side"/>.</summary>
    void Learned(RoamSide side, RoamNeighbor peer);
    void StateChanged();
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

    public void SetDisplays(IReadOnlyList<RoamDisplay> displays)
    {
        _displays = displays.Where(d => !d.Pixels.IsEmpty).ToList();
        _pixels = _displays.Select(d => d.Pixels).ToList();
        _bounds = RoamGeometry.Bounds(_pixels);
    }

    public void SetNeighbors(IReadOnlyList<RoamNeighbor> neighbors)
    {
        _neighbors = neighbors.ToList();
        if (_target is { } t)
        {
            var now = _neighbors.FirstOrDefault(n => string.Equals(n.PeerId, t.PeerId, StringComparison.OrdinalIgnoreCase));
            if (now is null) ComeHome(_exit, sendLeave: true);
            else _target = now;
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
                if (_target is { } t && (input.Dx != 0 || input.Dy != 0)) _host.Send(t.PeerId, RoamWire.Wheel, RoamWire.WheelData(input.Dx, input.Dy));
                break;
            case RoamInputKind.Key:
                if (_target is not null) OnKey(input.Usage, input.Down);
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
        var scale = RoamGeometry.ScaleAt(cx, cy, _displays);

        // Learn a moved computer from pushes against an empty left/right edge.
        foreach (var side in new[] { RoamSide.Left, RoamSide.Right })
        {
            if (_neighbors.Any(n => n.Side == side)) continue;
            var atEdge = RoamGeometry.AtEdge(side, cx, cy, _bounds);
            var toward = RoamGeometry.Beyond(side, px, py, _bounds) / scale;
            var distance = RoamGeometry.DistanceToEdge(side, cx, cy, _bounds) / scale;
            if (_learner.Feed(side, atEdge, toward, distance, now))
            {
                var opposite = _neighbors.Where(n => n.Side == RoamSides.Mirror(side)).ToList();
                if (opposite.Count == 1)
                {
                    _host.Learned(side, opposite[0]);
                    return; // the next push goes across; this one only taught us
                }
            }
        }

        // The edge glow: the nearest shared edge within 40 points, on a monitor that reaches it.
        RoamEdgeHint? hint = null;
        var best = double.MaxValue;
        var screenIndex = RoamGeometry.ScreenAt(cx, cy, _pixels);
        var screen = screenIndex >= 0 ? _pixels[screenIndex] : _bounds;
        foreach (var n in _neighbors)
        {
            if (!ReachesEdge(n.Side, screen)) continue;
            var d = RoamGeometry.DistanceToEdge(n.Side, cx, cy, _bounds) / scale;
            if (d > RoamGeometry.GlowDistance || d >= best) continue;
            best = d;
            hint = new RoamEdgeHint(n.Side, n.PeerId, n.Name, screen, RoamSides.IsHorizontal(n.Side) ? cy : cx);
        }
        SetHint(hint);

        // Cross over (Deskflow's jump zone: the outermost pixel of the desktop).
        foreach (var n in _neighbors)
        {
            if (!RoamGeometry.AtEdge(n.Side, cx, cy, _bounds)) continue;
            if (RoamGeometry.InCorner(n.Side, cx, cy, _pixels, RoamGeometry.CornerSize * scale)) continue;
            if (anyButton && _dragFiles is null)
            {
                // A button is held: only a file drag may cross. Ask the drop catcher whether this is one.
                _buttonAtEdgeSince ??= now;
                if (now - _buttonAtEdgeSince >= ProbeDelay) SetProbe(new RoamEdgeHint(n.Side, n.PeerId, n.Name, screen, RoamSides.IsHorizontal(n.Side) ? cy : cx));
                return;
            }
            Cross(n, cx, cy, scale);
            return;
        }
        // Not at a shared edge: no probing (a found drag stays known until the button comes up).
        _buttonAtEdgeSince = null;
        if (_dragFiles is null) SetProbe(null);
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
        _target = n;
        _vx = vx;
        _vy = vy;
        _exit = Inside(n.Side, cx, cy, RoamGeometry.ReturnInset * scale);
        _heldKeys.Clear();
        _heldButtons.Clear();
        SetHint(null);
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
            SetProbe(new RoamEdgeHint(n.Side, n.PeerId, n.Name, screen, RoamSides.IsHorizontal(n.Side) ? _exit.Y : _exit.X), force: true);
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
        var nx = _vx + dxPx / _parkScale;
        var ny = _vy + dyPx / _parkScale;
        var home = RoamSides.Mirror(t.Side);
        var tb = RoamGeometry.Bounds(t.Screens);
        if (RoamGeometry.Beyond(home, nx, ny, tb) > 0)
        {
            var (cx, cy) = RoamGeometry.ClampIntoScreens(nx, ny, t.Screens);
            var ok = RoamGeometry.AtEdge(home, cx, cy, tb)
                     && !RoamGeometry.InCorner(home, cx, cy, t.Screens, RoamGeometry.CornerSize)
                     && (_heldButtons.Count == 0 || _dragAway);
            if (ok)
            {
                var ratio = RoamGeometry.EdgeRatio(home, cx, cy, tb);
                var scale = RoamGeometry.ScaleAt(_exit.X, _exit.Y, _displays);
                var (lx, ly) = RoamGeometry.PointOnEdge(t.Side, ratio, _bounds, RoamGeometry.ReturnInset * scale);
                ComeHome(RoamGeometry.ClampIntoScreens(lx, ly, _pixels), sendLeave: true);
                return;
            }
        }
        var (x, y) = RoamGeometry.ClampIntoScreens(nx, ny, t.Screens);
        if (x == _vx && y == _vy) return;
        _vx = x;
        _vy = y;
        _host.Move(t.PeerId, x, y);
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
