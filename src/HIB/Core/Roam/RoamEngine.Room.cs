// H.I.B Room in the Roam engine (docs/SPACE.md §10.2–§10.3): keeps the one room layout every computer
// shares (roomLayout over the peer link: highest rev, then lower `by`), places a computer the first time
// it joins (rev 0 auto-place, or beside an arrangement the user made), learns moves into the room, and
// gives the controller the portals among this PC and the ready computers placed with it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HIB.Core.Space;

namespace HIB.Core.Roam;

/// <summary>What the Room view draws: every computer with screens (this one first), the layout and this computer's id.</summary>
public sealed record RoomSnapshot(IReadOnlyList<RoomComputer> Computers, RoomLayout? Layout, string Self)
{
    public RoomMap Map => RoomGeometry.Build(Computers, Layout);
}

public sealed partial class RoamEngine
{
    readonly RoomStore _roomStore;
    RoomMap _roomMap = RoomMap.Empty;
    RoomLayout? _roomUndo;

    /// <summary>The room layout changed (any thread).</summary>
    public event Action? RoomChanged;

    /// <summary>A computer appeared in the room for the first time ever ("Arrange your desk" opens by itself).</summary>
    public event Action<RoomComputer>? ComputerJoined;

    /// <summary>The stored room layout (null before there are two computers).</summary>
    public RoomLayout? RoomLayout => _roomStore.Layout;

    /// <summary>Every computer with screens and where the room places them.</summary>
    public RoomSnapshot Room { get { lock (_gate) return new RoomSnapshot(Computers(), _roomStore.Layout, Own); } }

    /// <summary>True when Roam reaches this computer through the room's portals.</summary>
    public bool InRoom(string peerId) { lock (_gate) return PeerFor(peerId) is { } p && UsesRoom(p); }

    /// <summary>The portals the controller uses now.</summary>
    public RoomMap ActiveRoom { get { lock (_gate) return _controller.Room; } }

    /// <summary>The user arranged the desk (Room view): a new rev from this computer, sent to every linked computer.</summary>
    public void SetRoomGroups(IReadOnlyList<RoomGroup> groups)
    {
        Locked(() =>
        {
            var cur = _roomStore.Layout;
            var list = new List<RoomGroup>();
            foreach (var g in groups)
                if (!list.Any(x => SpaceIds.Same(x.Computer, g.Computer))) list.Add(new RoomGroup(g.Computer, RoomLayout.Round(g.X), RoomLayout.Round(g.Y)));
            // Computers not in this edit (offline ones) keep their places.
            if (cur is not null)
                foreach (var g in cur.Groups)
                    if (!list.Any(x => SpaceIds.Same(x.Computer, g.Computer))) list.Add(g);
            var next = new RoomLayout(RoomLayout.NextRev(cur, _clock()), Own, list);
            if (cur is not null && next.SamePlaces(cur)) return;
            _roomUndo = null;
            WriteLog($"Room: arranged ({list.Count} computers)");
            SetRoomLayoutLocked(next);
        });
    }

    // ---------------- under the lock ----------------

    /// <summary>This PC and every linked computer that told us its screens.</summary>
    List<RoomComputer> Computers()
    {
        var list = new List<RoomComputer>();
        if (_displays.Count > 0)
            list.Add(new RoomComputer(Own, Own, _platform.Platform, _displays.Select(RoomScreenSpec.From).ToList(), IsSelf: true));
        foreach (var p in _peers.Values.OrderBy(p => p.Id, RoomGeometry.IdOrder))
        {
            if (p.Link is not { IsOpen: true } || p.Remote is not { Screens.Count: > 0 } r) continue;
            list.Add(new RoomComputer(p.Id, p.Name, r.Platform, r.Screens.Select(RoomScreenSpec.From).ToList()));
        }
        return list;
    }

    /// <summary>§9.2's side function for auto-place: manualSide or Point setup aims; null when nothing says.</summary>
    RoamSide? SideBetween(string a, string b)
    {
        if (IsSelf(a) && PeerFor(b) is { } pb && SideOf(pb, _store.Get(pb.Id)) is var (sb, srcB) && srcB != RoamSideSource.Default) return sb;
        if (IsSelf(b) && PeerFor(a) is { } pa && SideOf(pa, _store.Get(pa.Id)) is var (sa, srcA) && srcA != RoamSideSource.Default) return RoamSides.Mirror(sa);
        return RoamLayout.SideOf(_store.Layout, a, b);
    }

    /// <summary>A peer is reached through the room when both are placed and the room is real: arranged (rev &gt; 0) or the peer knows rooms.</summary>
    bool UsesRoom(Peer p)
        => _options.Room && _roomStore.Layout is { } l && l.Has(Own) && l.Has(p.Id) && (l.Rev > 0 || p.SentRoom) && _roomMap.Has(Own) && _roomMap.Has(p.Id);

    /// <summary>Where a placed computer's group sits relative to this PC's.</summary>
    RoamSide RoomSide(string peerId) => RoomGeometry.SideOf(_roomMap.BoundsOf(Own), _roomMap.BoundsOf(peerId));

    /// <summary>Places computers the layout doesn't have yet, then rebuilds the room map. Called by Recompute.</summary>
    void RebuildRoom()
    {
        if (!_options.Room) return;
        var computers = Computers();
        var layout = _roomStore.Layout;
        var next = layout;
        if (computers.Count >= 2 && computers.Any(c => c.IsSelf))
        {
            var missing = layout is null || computers.Any(c => !layout.Has(c.Id));
            if (layout is null || layout.Rev == 0)
            {
                // No arrangement yet: auto-place (rev 0, any edit wins). Ours replaces another computer's only when it lacks someone.
                if (missing || SpaceIds.Same(layout!.By, Own))
                {
                    var auto = RoomGeometry.AutoPlace(computers, SideBetween, Own);
                    if (missing || !auto.SamePlaces(layout)) next = auto;
                }
            }
            else if (missing)
            {
                // The user arranged it already: the newcomer goes beside, as a small edit (rev + 1) a real one beats.
                next = RoomGeometry.AddMissing(layout, computers, Own, SideBetween) with { Rev = layout.Rev + 1, By = Own };
            }
        }
        if (next is not null && !ReferenceEquals(next, layout))
        {
            _roomStore.SetLayout(next);
            WriteLog($"Room: placed {next.Groups.Count} computers (rev {next.Rev})");
            SendRoomToAll(next);
            After(() => RoomChanged?.Invoke());
        }
        if (computers.Count >= 2)
        {
            foreach (var c in computers)
            {
                if (c.IsSelf || !_roomStore.MarkSeen(c.Id)) continue;
                var joined = c;
                After(() => ComputerJoined?.Invoke(joined));
            }
        }
        _roomMap = RoomGeometry.Build(computers, _roomStore.Layout);
    }

    void SendRoomToAll(RoomLayout layout)
    {
        foreach (var p in _peers.Values)
            if (p.Link is { IsOpen: true }) Enqueue(p, RoomLayout.Type, layout.ToWire());
    }

    /// <summary>Stores a layout this computer made, tells everyone, recomputes the portals.</summary>
    void SetRoomLayoutLocked(RoomLayout next)
    {
        _roomStore.SetLayout(next);
        SendRoomToAll(next);
        Recompute();
        After(() => RoomChanged?.Invoke());
    }

    void OnRoomLayout(PeerLink link, JsonElement data)
    {
        if (!_options.Room) return;
        Locked(() =>
        {
            if (!_peers.TryGetValue(link.PeerId, out var p) || !ReferenceEquals(p.Link, link)) return;
            if (RoomLayout.Parse(data) is not { } incoming) return;
            p.SentRoom = true;
            var cur = _roomStore.Layout;
            var same = cur is not null && incoming.Rev == cur.Rev && SpaceIds.Compare(incoming.By, cur.By) == 0 && incoming.SamePlaces(cur);
            if (!same)
            {
                if (RoomLayout.Wins(incoming, cur))
                {
                    _roomStore.SetLayout(incoming);
                    WriteLog($"Room: {p.Name}'s arrangement (rev {incoming.Rev})");
                    After(() => RoomChanged?.Invoke());
                }
                else if (cur is not null)
                {
                    Enqueue(p, RoomLayout.Type, cur.ToWire()); // ours wins: say so, so both end up with the same room
                }
            }
            Recompute();
        });
    }

    /// <summary>The Devices tab's ← / → with a room: that computer's group goes beside this PC.</summary>
    void MoveGroupNextTo(string peerId, RoamSide side)
    {
        var c = Computers().FirstOrDefault(x => SpaceIds.Same(x.Id, peerId));
        var own = _roomMap.BoundsOf(Own);
        if (c is null || own.IsEmpty || _roomStore.Layout is not { } cur) return;
        var (gx, gy) = RoomGeometry.NextTo(c, own, side);
        SetRoomLayoutLocked(cur.WithGroup(c.Id, gx, gy) with { Rev = RoomLayout.NextRev(cur, _clock()), By = Own });
    }

    /// <summary>§10.2 learning: the other computer's group moves so it touches the pushed edge, centred on it.</summary>
    bool LearnInRoom(RoamSide side, RoamNeighbor peer, string? screenId)
    {
        var c = Computers().FirstOrDefault(x => SpaceIds.Same(x.Id, peer.PeerId));
        if (c is null || _roomStore.Layout is not { } cur) return false;
        var edge = screenId is not null && _roomMap.Find(Own, screenId) is { } rs ? rs.Mm : _roomMap.BoundsOf(Own);
        if (edge.IsEmpty) return false;
        var (gx, gy) = RoomGeometry.Beside(c, edge, side);
        _roomUndo = cur;
        _undo = null;
        WriteLog($"Room: {peer.Name} is on the {RoamSides.EdgeWord(side)} now (learned)");
        SetRoomLayoutLocked(cur.WithGroup(c.Id, gx, gy) with { Rev = RoomLayout.NextRev(cur, _clock()), By = Own });
        var notice = new RoamLearnedNotice(peer.PeerId, peer.Name, side);
        After(() => SideLearned?.Invoke(notice));
        return true;
    }
}
