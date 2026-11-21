// H.I.B Room view (docs/SPACE.md §10.4): the editing state behind "Your desk" — every computer with its
// screens, where each group is (mm), the phones at the side, which computers already follow the room,
// hover and drag. Dragging snaps a group's edges to the others' within 40 mm; a drop applies at once
// (roomLayout goes out); Cancel puts back what was there when the view opened. The Mac's RoomEditor.
using System;
using System.Collections.Generic;
using System.Linq;
using HIB.Core.Roam;
using HIB.Core.Space;

namespace HIB.ViewModels;

/// <summary>A phone or tablet in the space, shown as a chip at the side (not draggable).</summary>
public sealed record RoomPhone(string Id, string Name, int? Battery, bool Charging, bool Online);

public sealed class RoomEditor
{
    readonly Dictionary<string, (double X, double Y)> _offsets = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (double X, double Y)> _original = new(StringComparer.OrdinalIgnoreCase);

    public RoomEditor(string me, IReadOnlyList<RoomComputer> computers, IReadOnlyList<RoomGroup> groups, IReadOnlyList<RoomPhone>? phones = null,
        IReadOnlyCollection<string>? speaking = null, string? joined = null)
    {
        Me = me;
        Computers = computers;
        foreach (var g in groups) _offsets[g.Computer] = (g.X, g.Y);
        foreach (var (k, v) in _offsets) _original[k] = v;
        Phones = phones ?? Array.Empty<RoomPhone>();
        Speaking = new HashSet<string>(speaking ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        Joined = joined;
    }

    public string Me { get; }
    public IReadOnlyList<RoomComputer> Computers { get; private set; }
    public IReadOnlyList<RoomPhone> Phones { get; private set; }
    /// <summary>Computers whose Roam follows the room (the others run an older H.I.B).</summary>
    public HashSet<string> Speaking { get; private set; }
    /// <summary>Opened by itself because this computer joined ("PC-INTERN just joined").</summary>
    public string? Joined { get; set; }
    /// <summary>The screen under the pointer ("computer#index").</summary>
    public string? Hovered { get; set; }
    /// <summary>The computer whose group is in the hand.</summary>
    public string? Dragging { get; private set; }
    public bool Edited { get; private set; }

    /// <summary>Where every group is now (whole mm once dropped).</summary>
    public IReadOnlyList<RoomGroup> Groups => _offsets.Select(kv => new RoomGroup(kv.Key, kv.Value.X, kv.Value.Y)).ToList();

    public (double X, double Y)? OffsetOf(string computer) => _offsets.TryGetValue(computer, out var o) ? o : null;

    public RoomMap World => RoomGeometry.Build(Computers, Groups);

    public RoomComputer? Computer(string id) => Computers.FirstOrDefault(c => SpaceIds.Same(c.Id, id));

    /// <summary>Something changed (redraw).</summary>
    public event Action? Changed;
    /// <summary>A drop or a cancel: these groups apply now.</summary>
    public Action<IReadOnlyList<RoomGroup>> OnApply { get; set; } = _ => { };
    public Action OnClose { get; set; } = () => { };

    /// <summary>The room changed elsewhere (the other computer moved something, a computer joined or left).</summary>
    public void Refresh(IReadOnlyList<RoomComputer> computers, IReadOnlyList<RoomGroup> groups, IReadOnlyCollection<string> speaking, IReadOnlyList<RoomPhone>? phones = null)
    {
        Computers = computers;
        var held = Dragging is { } d && _offsets.TryGetValue(d, out var hand) ? (d, hand) : default;
        _offsets.Clear();
        foreach (var g in groups) _offsets[g.Computer] = (g.X, g.Y);
        if (held.d is not null) _offsets[held.d] = held.hand; // the group in the hand stays put
        foreach (var (k, v) in _offsets) _original.TryAdd(k, v); // newcomers: Cancel leaves them where they came
        Speaking = new HashSet<string>(speaking, StringComparer.OrdinalIgnoreCase);
        if (phones is not null) Phones = phones;
        Changed?.Invoke();
    }

    public void BeginDrag(string id)
    {
        Dragging = id;
        Changed?.Invoke();
    }

    /// <summary>Moves a group to <paramref name="x"/>, <paramref name="y"/> (mm), snapped to the other groups' edges.</summary>
    public void Drag(string id, double x, double y)
    {
        if (Computer(id) is not { } c) return;
        var moving = RoomGeometry.LocalMm(c.Screens).Select(r => new RoamRect(r.X + x, r.Y + y, r.W, r.H)).ToList();
        var others = World.Screens.Where(s => !SpaceIds.Same(s.Computer, id)).Select(s => s.Mm).ToList();
        var (dx, dy) = RoomGeometry.Snap(moving, others);
        _offsets[c.Id] = (x + dx, y + dy);
        Changed?.Invoke();
    }

    /// <summary>Dropped: whole millimetres, applied live (roomLayout goes out).</summary>
    public void EndDrag()
    {
        if (Dragging is not { } id) return;
        Dragging = null;
        if (_offsets.TryGetValue(id, out var o)) _offsets[id] = (RoomGeometry.Round(o.X), RoomGeometry.Round(o.Y));
        Edited = true;
        OnApply(Groups);
        Changed?.Invoke();
    }

    /// <summary>Cancel / Esc: everything back where it was when the view opened.</summary>
    public void Cancel()
    {
        Dragging = null;
        if (Edited)
        {
            _offsets.Clear();
            foreach (var (k, v) in _original) _offsets[k] = v;
            OnApply(Groups);
            Edited = false;
        }
        Changed?.Invoke();
        OnClose();
    }

    public void Done()
    {
        if (Dragging is not null) EndDrag();
        OnClose();
    }
}
