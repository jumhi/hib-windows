// H.I.B Room in the Roam engine (docs/SPACE.md §10.2–§10.3), the same model as the Mac's RoamEngine:
// the stored (winning) roomLayout places the computers it knows; auto-place places the rest around this
// PC; with nothing stored this PC stands by its own auto layout (rev 0). The layout goes to each linked
// computer on every hello and whenever it changes (once per version); a received one is adopted by the
// §10.2 rule. Computers that sent roomLayout are reached through the room's portals; older ones keep §9.2.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HIB.Core.Space;

namespace HIB.Core.Roam;

/// <summary>What the Room view draws: every computer with screens (this one first), where each group is, and this computer's id.</summary>
public sealed record RoomSnapshot(IReadOnlyList<RoomComputer> Computers, IReadOnlyList<RoomGroup> Groups, RoomLayout? Layout, string Self)
{
    public RoomMap Map => RoomGeometry.Build(Computers, Groups);
}

public sealed partial class RoamEngine
{
    readonly RoomStore _roomStore;
    RoomMap _roomMap = RoomMap.Empty;
    IReadOnlyList<RoomGroup> _roomGroups = Array.Empty<RoomGroup>();
    IReadOnlyList<RoomGroup>? _roomUndo;

    /// <summary>The room changed (layout, computers or screens; any thread).</summary>
    public event Action? RoomChanged;

    /// <summary>A computer that speaks the room appeared in it for the first time ever ("Arrange your desk" opens by itself).</summary>
    public event Action<RoomComputer>? ComputerJoined;

    /// <summary>The layout this PC stands by: the stored (winning) one, else its own auto layout with rev 0.</summary>
    public RoomLayout? RoomLayout { get { lock (_gate) return OutgoingRoom(); } }

    /// <summary>Every computer with screens and where the room places them.</summary>
    public RoomSnapshot Room { get { lock (_gate) return new RoomSnapshot(Computers(), _roomGroups, OutgoingRoom(), Own); } }

    /// <summary>True when Roam reaches this computer through the room's portals.</summary>
    public bool InRoom(string peerId) { lock (_gate) return PeerFor(peerId) is { } p && UsesRoom(p); }

    /// <summary>The portals the controller uses now.</summary>
    public RoomMap ActiveRoom { get { lock (_gate) return _controller.Room; } }

    /// <summary>The user arranged the desk (Room view): a new rev from this computer, sent to every linked computer.</summary>
    public void SetRoomGroups(IReadOnlyList<RoomGroup> groups)
    {
        Locked(() =>
        {
            var cur = OutgoingRoom();
            if (cur is not null && cur.Replacing(groups, cur.Rev, cur.By).SamePlaces(cur)) return; // nothing moved
            _roomUndo = null;
            WriteLog($"Room: arranged ({groups.Count} computers)");
            ApplyRoomEdit(groups);
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
            if (p.Link is not { IsOpen: true } || p.Remote is not { Screens.Count: > 0 } r || IsSelf(p.Id)) continue;
            list.Add(new RoomComputer(p.Id, p.Name, r.Platform, r.Screens.Select(RoomScreenSpec.From).ToList()));
        }
        return list;
    }

    /// <summary>§9.2's side for auto-place, seen from this PC, and whether Point setup or a manual choice says so.</summary>
    (RoamSide Side, bool Known) RoomSideOf(string peerId)
    {
        if (PeerFor(peerId) is not { } p) return (RoamSide.Right, false);
        var (side, source) = SideOf(p, _store.Get(p.Id));
        return (side, source is RoamSideSource.Manual or RoamSideSource.PointSetup);
    }

    /// <summary>The other computer sent roomLayout on this link and both are placed: crossings use portals.</summary>
    bool UsesRoom(Peer p) => _options.Room && p.SentRoom && _roomMap.Has(Own) && _roomMap.Has(p.Id);

    /// <summary>Where a placed computer's group sits relative to this PC's (by their centres).</summary>
    RoamSide RoomSide(string peerId) => RoomGeometry.SideOf(_roomMap.BoundsOf(Own), _roomMap.BoundsOf(peerId));

    RoomLayout? OutgoingRoom()
    {
        if (_roomStore.Layout is { } stored) return stored;
        if (_displays.Count == 0 || !_options.Room) return null;
        return RoomLayout.Make(_roomGroups, 0, Own);
    }

    /// <summary>Rebuilds the room (stored offsets, auto-place for the rest) and tells every linked computer when the layout changed. Called by Recompute.</summary>
    void RebuildRoom()
    {
        if (!_options.Room) return;
        var computers = Computers();
        var stored = _roomStore.Layout;
        var groups = RoomGeometry.AutoPlace(computers, Own, stored?.Groups ?? (IReadOnlyList<RoomGroup>)Array.Empty<RoomGroup>(), RoomSideOf);
        var changed = !SameGroups(groups, _roomGroups);
        _roomGroups = groups;
        _roomMap = RoomGeometry.Build(computers, groups);
        foreach (var p in _peers.Values) SendRoom(p);
        if (changed) After(() => RoomChanged?.Invoke());
        // The first time a computer that speaks the room joins it, "Arrange your desk" opens by itself.
        foreach (var p in _peers.Values)
        {
            if (!p.SentRoom || !_roomMap.Has(p.Id) || !_roomMap.Has(Own) || _roomStore.WasSeen(p.Id)) continue;
            _roomStore.MarkSeen(p.Id);
            if (computers.FirstOrDefault(c => SpaceIds.Same(c.Id, p.Id)) is { } joined) After(() => ComputerJoined?.Invoke(joined));
        }
    }

    static bool SameGroups(IReadOnlyList<RoomGroup> a, IReadOnlyList<RoomGroup> b)
        => a.Count == b.Count && a.All(g => b.Any(o => SpaceIds.Same(o.Computer, g.Computer) && o.X == g.X && o.Y == g.Y));

    /// <summary>Sends the layout this PC stands by, unless that computer has it already.</summary>
    void SendRoom(Peer p)
    {
        if (p.Link is not { IsOpen: true } || OutgoingRoom() is not { } layout) return;
        var key = layout.Key;
        if (key == p.LastSentRoom) return;
        p.LastSentRoom = key;
        Enqueue(p, RoomLayout.Type, layout.ToWire());
    }

    /// <summary>An edit (the Room view, learning, undo, the side picker): a new rev above the current one, by this PC.</summary>
    void ApplyRoomEdit(IEnumerable<RoomGroup> groups)
    {
        var base_ = OutgoingRoom() ?? new RoomLayout(0, Own, Array.Empty<RoomGroup>());
        _roomStore.SetLayout(base_.Replacing(groups, RoomLayout.NextRev(base_, _clock()), Own));
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
            p.LastSentRoom = incoming.Key; // they have this one: only a different one goes back
            if (RoomLayout.Adopt(incoming, OutgoingRoom(), Own))
            {
                _roomStore.SetLayout(incoming);
                WriteLog($"Room: {p.Name}'s arrangement (rev {incoming.Rev})");
                After(() => RoomChanged?.Invoke());
            }
            Recompute(); // ours goes back when it won
        });
    }

    /// <summary>The Devices tab's ← / → with a room: that computer's group goes beside this PC.</summary>
    void MoveGroupNextTo(string peerId, RoamSide side)
    {
        var c = Computers().FirstOrDefault(x => SpaceIds.Same(x.Id, peerId));
        var own = _roomMap.BoundsOf(Own);
        if (c is null || own.IsEmpty) return;
        var (gx, gy) = RoomGeometry.NextTo(c, own, side);
        ApplyRoomEdit(_roomGroups.Where(g => !SpaceIds.Same(g.Computer, c.Id)).Append(new RoomGroup(c.Id, gx, gy)));
    }

    /// <summary>§10.2 learning: the other computer's group moves so it touches this PC's group on the pushed side, centred on it.</summary>
    bool LearnInRoom(RoamSide side, RoamNeighbor peer)
    {
        var c = Computers().FirstOrDefault(x => SpaceIds.Same(x.Id, peer.PeerId));
        var mine = _roomMap.BoundsOf(Own);
        if (c is null || mine.IsEmpty) return false;
        var previous = _roomGroups;
        var (gx, gy) = RoomGeometry.Touching(c, side, mine);
        _undo = null;
        WriteLog($"Room: {peer.Name} is on the {RoamSides.EdgeWord(side)} now (learned)");
        ApplyRoomEdit(previous.Where(g => !SpaceIds.Same(g.Computer, c.Id)).Append(new RoomGroup(c.Id, gx, gy)).ToList());
        _roomUndo = previous;
        var notice = new RoamLearnedNotice(peer.PeerId, peer.Name, side);
        After(() => SideLearned?.Invoke(notice));
        return true;
    }
}
