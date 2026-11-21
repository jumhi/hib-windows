// H.I.B Room (docs/SPACE.md §10.2–§10.3), all pure: computers' screens placed in room millimetres, the
// automatic arrangement, snapping while dragging, and the portals Roam's cursor follows — wherever a
// screen's edge and another computer's opposite edge are within 30 mm and overlap. Both computers run the
// same rules on the same layout (the Mac: Core/Roam/RoomLogic.swift), so they find identical portals.
//
// Settled details (both apps):
//  - The primary screen is the one whose top-left is the desktop origin (0, 0), else the first listed.
//    mm per DIP = its mmW / its width (unknown → 0.25). A screen's room size is its own mmW × mmH (each
//    unknown axis: 0.25 mm per DIP).
//  - A group's offset (x, y) is the room position of its desktop origin, in whole millimetres.
//  - Inside a screen, desktop ↔ room is linear over its two rectangles (desktop bounds ↔ room rect).
//  - The cursor crosses only inside a portal's overlap; the clamp only keeps the ends inside. Several
//    portals at one spot: the smallest |gap|, then the longest overlap, then the lower computer id (UTF-8),
//    then the lower screen index. The entry point is 1 DIP inside the other screen's edge.
using System;
using System.Collections.Generic;
using System.Linq;
using HIB.Core.Space;

namespace HIB.Core.Roam;

/// <summary>A computer's screen as the room needs it: DIP bounds (y down) and §10.1's physical size, name and pixels.</summary>
public sealed record RoomScreenSpec(string Id, string Name, double X, double Y, double W, double H, double MmW = 0, double MmH = 0,
    bool BuiltIn = false, int PxW = 0, int PxH = 0, double Scale = 1, bool Primary = false)
{
    public RoamRect Dips => new(X, Y, W, H);

    public static RoomScreenSpec From(RoamScreen s) => new(s.Id, s.Name, s.X, s.Y, s.W, s.H, s.MmW, s.MmH, s.BuiltIn, s.PxW, s.PxH, s.Scale);

    public static RoomScreenSpec From(RoamDisplay d)
    {
        var r = d.Dips;
        return new RoomScreenSpec(d.Id, d.Name, r.X, r.Y, r.W, r.H, d.MmW, d.MmH, d.BuiltIn,
            (int)Math.Round(d.Pixels.W), (int)Math.Round(d.Pixels.H), d.SafeScale, d.Primary);
    }
}

/// <summary>One computer in the room: its id (desktop name), platform ("mac"/"windows") and screens.</summary>
public sealed record RoomComputer(string Id, string Name, string Platform, IReadOnlyList<RoomScreenSpec> Screens, bool IsSelf = false)
{
    public bool IsMac => string.Equals(Platform, "mac", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A screen placed in the room: its computer's DIPs and its rectangle in room millimetres.</summary>
public sealed record RoomScreen(string Computer, string Id, int Index, RoamRect Dips, RoamRect Mm, RoomScreenSpec Spec);

/// <summary>
/// Where the cursor can cross: <see cref="FromScreen"/>'s <see cref="Side"/> edge faces <see cref="ToScreen"/>'s opposite edge,
/// overlapping from <see cref="Start"/> to <see cref="End"/> (room mm along the edge: y for left/right, x for top/bottom).
/// <see cref="Gap"/> is the distance between the edges (negative when they overlap); <see cref="At"/> is half way between them.
/// </summary>
public sealed record RoomPortal(string FromComputer, string FromScreen, int FromIndex, RoamSide Side, string ToComputer, string ToScreen, int ToIndex,
    double Start, double End, double Gap, double At)
{
    public double Length => End - Start;
}

/// <summary>A crossing through a portal: the target screen and the entry point in the target computer's DIPs.</summary>
public sealed record RoomCrossing(RoomPortal Portal, RoomScreen To, double X, double Y, double Along);

/// <summary>Every placed screen and the portals between computers.</summary>
public sealed class RoomMap
{
    public static readonly RoomMap Empty = new(Array.Empty<RoomScreen>());

    public RoomMap(IEnumerable<RoomScreen> screens)
    {
        Screens = screens.ToList();
        Portals = RoomGeometry.Portals(Screens);
    }

    public IReadOnlyList<RoomScreen> Screens { get; }
    public IReadOnlyList<RoomPortal> Portals { get; }

    public IEnumerable<string> Computers => Screens.Select(s => s.Computer).Distinct(StringComparer.OrdinalIgnoreCase);

    public bool Has(string computer) => Screens.Any(s => SpaceIds.Same(s.Computer, computer));

    public IReadOnlyList<RoomScreen> Of(string computer) => Screens.Where(s => SpaceIds.Same(s.Computer, computer)).ToList();

    public RoomScreen? Find(string computer, string screenId)
        => Screens.FirstOrDefault(s => SpaceIds.Same(s.Computer, computer) && s.Id == screenId);

    /// <summary>The bounding box of a computer's screens (mm).</summary>
    public RoamRect BoundsOf(string computer) => RoamRect.Union(Of(computer).Select(s => s.Mm));

    /// <summary>True when any screen of <paramref name="a"/> has a portal to <paramref name="b"/>.</summary>
    public bool Touches(string a, string b) => Portals.Any(p => SpaceIds.Same(p.FromComputer, a) && SpaceIds.Same(p.ToComputer, b));

    /// <summary>True when the computer has a portal to any other computer.</summary>
    public bool Connected(string computer) => Portals.Any(p => SpaceIds.Same(p.FromComputer, computer));

    /// <summary>The edges of <paramref name="from"/>'s screens that lead to <paramref name="to"/>.</summary>
    public IReadOnlyList<RoamSide> SidesTo(string from, string to)
        => Portals.Where(p => SpaceIds.Same(p.FromComputer, from) && SpaceIds.Same(p.ToComputer, to)).Select(p => p.Side).Distinct().ToList();

    /// <summary>
    /// The portal on that edge at <paramref name="along"/> (mm along it) whose target <paramref name="allowed"/> accepts: only inside
    /// an overlap. Several: the smallest |gap|, then the longest overlap, then the lower computer id, then the lower screen index.
    /// </summary>
    public RoomPortal? PortalAt(string computer, string screenId, RoamSide side, double along, Func<string, bool>? allowed = null)
    {
        RoomPortal? best = null;
        foreach (var p in Portals)
        {
            if (p.Side != side || p.FromScreen != screenId || !SpaceIds.Same(p.FromComputer, computer)) continue;
            if (along < p.Start || along > p.End) continue;
            if (allowed is not null && !allowed(p.ToComputer)) continue;
            if (best is null || Better(p, best)) best = p;
        }
        return best;

        static bool Better(RoomPortal p, RoomPortal best)
        {
            if (Math.Abs(p.Gap) != Math.Abs(best.Gap)) return Math.Abs(p.Gap) < Math.Abs(best.Gap);
            if (p.Length != best.Length) return p.Length > best.Length;
            var c = SpaceIds.Compare(p.ToComputer, best.ToComputer);
            if (c != 0) return c < 0;
            return p.ToIndex < best.ToIndex;
        }
    }

    /// <summary>
    /// §10.3: the cursor at (<paramref name="x"/>, <paramref name="y"/>) (this computer's DIPs) leaves <paramref name="screenId"/>
    /// over its <paramref name="side"/> edge. Crossing at room position p enters the other screen at the same p, clamped into the
    /// overlap, converted to that computer's DIPs, <paramref name="inset"/> inside its edge. Null when no portal is there.
    /// </summary>
    public RoomCrossing? Cross(string computer, string screenId, RoamSide side, double x, double y, Func<string, bool>? allowed = null,
        double inset = RoomGeometry.EntryInset)
    {
        if (Find(computer, screenId) is not { } s) return null;
        var along = RoomGeometry.Along(s, side, x, y);
        if (PortalAt(computer, screenId, side, along, allowed) is not { } portal) return null;
        if (Find(portal.ToComputer, portal.ToScreen) is not { } to) return null;
        var clamped = Math.Clamp(along, portal.Start, portal.End);
        var (ex, ey) = RoomGeometry.EnterPoint(to, RoamSides.Mirror(side), clamped, inset);
        return new RoomCrossing(portal, to, ex, ey, clamped);
    }

    /// <summary>A portal's overlap in its <c>from</c> screen's DIPs along the edge (for the glow).</summary>
    public (double Start, double End)? Segment(RoomPortal p)
    {
        if (Find(p.FromComputer, p.FromScreen) is not { } a) return null;
        var horizontal = RoamSides.IsHorizontal(p.Side);
        var lo = horizontal ? RoomGeometry.ToDips(a, a.Mm.X, p.Start).Y : RoomGeometry.ToDips(a, p.Start, a.Mm.Y).X;
        var hi = horizontal ? RoomGeometry.ToDips(a, a.Mm.X, p.End).Y : RoomGeometry.ToDips(a, p.End, a.Mm.Y).X;
        return (Math.Min(lo, hi), Math.Max(lo, hi));
    }

    /// <summary>One portal per touching pair of screens (a→b and b→a are the same segment), for drawing.</summary>
    public IReadOnlyList<RoomPortal> Segments()
        => Portals.Where(p => SpaceIds.Compare(p.FromComputer, p.ToComputer) < 0
                              || !Portals.Any(q => SpaceIds.Same(q.FromComputer, p.ToComputer) && SpaceIds.Same(q.ToComputer, p.FromComputer)
                                                   && q.FromIndex == p.ToIndex && q.ToIndex == p.FromIndex && q.Side == RoamSides.Mirror(p.Side)))
            .ToList();

    /// <summary>Computers that touch nothing (with at least two computers in the room): "Not connected to anything".</summary>
    public IReadOnlyList<string> Lonely()
    {
        var ids = Computers.ToList();
        if (ids.Count < 2) return Array.Empty<string>();
        return ids.Where(id => !Connected(id)).ToList();
    }
}

public static class RoomGeometry
{
    /// <summary>Edges within this many mm (and overlapping) make a portal.</summary>
    public const double PortalReach = 30;
    /// <summary>Dragging snaps edges within this many mm.</summary>
    public const double SnapDistance = 40;
    /// <summary>Unknown physical size: this many mm per point/DIP.</summary>
    public const double DefaultMmPerDip = 0.25;
    /// <summary>A crossing enters this many DIPs inside the other screen's edge (the Mac's RoamGeometry.inset).</summary>
    public const double EntryInset = 1;

    public static readonly IComparer<string> IdOrder = Comparer<string>.Create(SpaceIds.Compare);

    /// <summary>Whole millimetres, half away from zero (Swift's rounded()).</summary>
    public static double Round(double v) => RoomLayout.Round(v);

    /// <summary>The primary screen: its top-left is the desktop origin, else the first listed.</summary>
    public static int PrimaryIndex(IReadOnlyList<RoomScreenSpec> screens)
    {
        if (screens.Count == 0) return -1;
        for (var i = 0; i < screens.Count; i++)
            if (screens[i].X == 0 && screens[i].Y == 0) return i;
        return 0;
    }

    public static RoomScreenSpec? PrimaryOf(IReadOnlyList<RoomScreenSpec> screens) => PrimaryIndex(screens) is var i and >= 0 ? screens[i] : null;

    /// <summary>A computer's mm per DIP: its primary screen's mmW / width (0.25 when unknown).</summary>
    public static double MmPerDip(IReadOnlyList<RoomScreenSpec> screens)
        => PrimaryOf(screens) is { MmW: > 0, W: > 0 } p ? p.MmW / p.W : DefaultMmPerDip;

    /// <summary>A screen's physical size (mm): its own, each unknown axis 0.25 mm per DIP.</summary>
    public static (double W, double H) SizeMm(RoomScreenSpec s)
        => (s.MmW > 0 ? s.MmW : s.W * DefaultMmPerDip, s.MmH > 0 ? s.MmH : s.H * DefaultMmPerDip);

    /// <summary>
    /// §10.1 at the source: what a display says about its size, cleaned so both computers use the same numbers — turned like its
    /// bounds, whole millimetres, and 0 (unknown) when it can't be right (an EDID that only gives the aspect ratio, a TV that claims
    /// metres): each axis 0.05–1.5 mm per DIP and the two within 1.5× of each other. (The Mac's PointPhysical.millimetres.)
    /// </summary>
    public static (double W, double H) Millimetres(double mmW, double mmH, double dipW, double dipH)
    {
        if (!(mmW > 0 && mmH > 0 && dipW > 0 && dipH > 0)) return (0, 0);
        double w = mmW, h = mmH;
        if ((w > h) != (dipW > dipH) && w != h) (w, h) = (h, w);
        var kx = w / dipW;
        var ky = h / dipH;
        if (kx < 0.05 || kx > 1.5 || ky < 0.05 || ky > 1.5 || Math.Max(kx, ky) / Math.Min(kx, ky) > 1.5) return (0, 0);
        return (Round(w), Round(h));
    }

    /// <summary>Each screen in mm relative to the computer's desktop origin (OS arrangement × mm per DIP; sizes their own).</summary>
    public static IReadOnlyList<RoamRect> LocalMm(IReadOnlyList<RoomScreenSpec> screens)
    {
        var k = MmPerDip(screens);
        return screens.Select(s =>
        {
            var (w, h) = SizeMm(s);
            return new RoamRect(s.X * k, s.Y * k, w, h);
        }).ToList();
    }

    public static RoamRect LocalBounds(IReadOnlyList<RoomScreenSpec> screens) => RoamRect.Union(LocalMm(screens));

    /// <summary>A computer's screens placed at its group offset.</summary>
    public static IReadOnlyList<RoomScreen> Place(RoomComputer c, double gx, double gy)
    {
        var local = LocalMm(c.Screens);
        var list = new List<RoomScreen>(c.Screens.Count);
        for (var i = 0; i < c.Screens.Count; i++)
        {
            var s = c.Screens[i];
            var m = local[i];
            list.Add(new RoomScreen(c.Id, s.Id, i, s.Dips, new RoamRect(gx + m.X, gy + m.Y, m.W, m.H), s));
        }
        return list;
    }

    /// <summary>The room of every computer these groups place.</summary>
    public static RoomMap Build(IEnumerable<RoomComputer> computers, IEnumerable<RoomGroup>? groups)
    {
        if (groups is null) return RoomMap.Empty;
        var list = groups.ToList();
        var screens = new List<RoomScreen>();
        foreach (var c in computers)
        {
            if (c.Screens.Count == 0) continue;
            var g = list.FirstOrDefault(x => x.Computer == c.Id) ?? list.FirstOrDefault(x => SpaceIds.Same(x.Computer, c.Id));
            if (g is null) continue;
            screens.AddRange(Place(c, g.X, g.Y));
        }
        return new RoomMap(screens);
    }

    public static RoomMap Build(IEnumerable<RoomComputer> computers, RoomLayout? layout) => Build(computers, layout?.Groups);

    // ---------------- portals (§10.3) ----------------

    /// <summary>Every portal between screens of different computers (right↔left, left↔right, bottom↔top, top↔bottom).</summary>
    public static IReadOnlyList<RoomPortal> Portals(IReadOnlyList<RoomScreen> screens)
    {
        var list = new List<RoomPortal>();
        foreach (var a in screens)
        {
            foreach (var b in screens)
            {
                if (ReferenceEquals(a, b) || SpaceIds.Same(a.Computer, b.Computer)) continue;
                Try(a, b, RoamSide.Left, a.Mm.X - b.Mm.Right, (a.Mm.X + b.Mm.Right) / 2, a.Mm.Y, a.Mm.Bottom, b.Mm.Y, b.Mm.Bottom);
                Try(a, b, RoamSide.Right, b.Mm.X - a.Mm.Right, (a.Mm.Right + b.Mm.X) / 2, a.Mm.Y, a.Mm.Bottom, b.Mm.Y, b.Mm.Bottom);
                Try(a, b, RoamSide.Above, a.Mm.Y - b.Mm.Bottom, (a.Mm.Y + b.Mm.Bottom) / 2, a.Mm.X, a.Mm.Right, b.Mm.X, b.Mm.Right);
                Try(a, b, RoamSide.Below, b.Mm.Y - a.Mm.Bottom, (a.Mm.Bottom + b.Mm.Y) / 2, a.Mm.X, a.Mm.Right, b.Mm.X, b.Mm.Right);
            }
        }
        return list;

        void Try(RoomScreen a, RoomScreen b, RoamSide side, double gap, double at, double a0, double a1, double b0, double b1)
        {
            if (Math.Abs(gap) > PortalReach) return;
            var lo = Math.Max(a0, b0);
            var hi = Math.Min(a1, b1);
            if (!(hi > lo)) return;
            list.Add(new RoomPortal(a.Computer, a.Id, a.Index, side, b.Computer, b.Id, b.Index, lo, hi, gap, at));
        }
    }

    /// <summary>A DIP point on a screen's edge → its position along that edge in room mm.</summary>
    public static double Along(RoomScreen s, RoamSide side, double x, double y)
    {
        var (mx, my) = ToMm(s, x, y);
        return RoamSides.IsHorizontal(side) ? my : mx;
    }

    /// <summary>Room mm → the screen's DIPs (linear over that screen's two rectangles).</summary>
    public static (double X, double Y) ToDips(RoomScreen s, double mmX, double mmY)
    {
        if (!(s.Mm.W > 0 && s.Mm.H > 0)) return (s.Dips.X + s.Dips.W / 2, s.Dips.Y + s.Dips.H / 2);
        return (s.Dips.X + (mmX - s.Mm.X) / s.Mm.W * s.Dips.W, s.Dips.Y + (mmY - s.Mm.Y) / s.Mm.H * s.Dips.H);
    }

    /// <summary>The screen's DIPs → room mm.</summary>
    public static (double X, double Y) ToMm(RoomScreen s, double x, double y)
    {
        if (!(s.Dips.W > 0 && s.Dips.H > 0)) return (s.Mm.X + s.Mm.W / 2, s.Mm.Y + s.Mm.H / 2);
        return (s.Mm.X + (x - s.Dips.X) / s.Dips.W * s.Mm.W, s.Mm.Y + (y - s.Dips.Y) / s.Dips.H * s.Mm.H);
    }

    /// <summary>The point <paramref name="along"/> mm along <paramref name="to"/>'s <paramref name="entrySide"/> edge, <paramref name="inset"/> inside, in its DIPs.</summary>
    public static (double X, double Y) EnterPoint(RoomScreen to, RoamSide entrySide, double along, double inset = EntryInset)
    {
        var d = to.Dips;
        var lastX = d.MaxX;
        var lastY = d.MaxY;
        if (RoamSides.IsHorizontal(entrySide))
        {
            var y = Math.Min(Math.Max(ToDips(to, to.Mm.X, along).Y, d.Y), lastY);
            var x = entrySide == RoamSide.Left ? Math.Min(d.X + inset, lastX) : Math.Max(lastX - inset, d.X);
            return (x, y);
        }
        var ex = Math.Min(Math.Max(ToDips(to, along, to.Mm.Y).X, d.X), lastX);
        var ey = entrySide == RoamSide.Above ? Math.Min(d.Y + inset, lastY) : Math.Max(lastY - inset, d.Y);
        return (ex, ey);
    }

    // ---------------- arranging ----------------

    /// <summary>Where group <paramref name="b"/> sits seen from group <paramref name="a"/>, by their centres.</summary>
    public static RoamSide SideOf(RoamRect a, RoamRect b)
    {
        var dx = (b.X + b.W / 2) - (a.X + a.W / 2);
        var dy = (b.Y + b.H / 2) - (a.Y + a.H / 2);
        if (Math.Abs(dx) >= Math.Abs(dy)) return dx >= 0 ? RoamSide.Right : RoamSide.Left;
        return dy > 0 ? RoamSide.Below : RoamSide.Above;
    }

    /// <summary>The offset that puts a group's bounding box (<paramref name="local"/>) at <paramref name="x"/>, <paramref name="y"/>, in whole mm.</summary>
    public static (double X, double Y) Offset(RoamRect local, double x, double y) => (Round(x - local.X), Round(y - local.Y));

    /// <summary>
    /// §10.2 auto-place (the Mac's RoomAutoPlace.place): offsets for every computer with screens. <paramref name="fixedOffsets"/> stay;
    /// the others go around <paramref name="me"/> by <paramref name="sideOf"/> (the §9.2 side seen from me, and whether Point setup or a
    /// manual choice says so). Left/right ones sit with their bottoms on my bottom (one desk line); above/below ones are centred on me.
    /// With nothing fixed and no side known: the Mac first and the others to its right, bottoms on y = 0. Whole millimetres.
    /// </summary>
    public static IReadOnlyList<RoomGroup> AutoPlace(IReadOnlyList<RoomComputer> computers, string me, IReadOnlyList<RoomGroup> fixedOffsets,
        Func<string, (RoamSide Side, bool Known)> sideOf)
    {
        var list = computers.Where(c => c.Screens.Count > 0).ToList();
        var placed = new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in list)
        {
            var f = fixedOffsets.FirstOrDefault(g => g.Computer == c.Id) ?? fixedOffsets.FirstOrDefault(g => SpaceIds.Same(g.Computer, c.Id));
            if (f is not null) placed[c.Id] = (Round(f.X), Round(f.Y));
        }
        RoamRect? Box(RoomComputer c)
        {
            if (!placed.TryGetValue(c.Id, out var o)) return null;
            var l = LocalBounds(c.Screens);
            return new RoamRect(l.X + o.X, l.Y + o.Y, l.W, l.H);
        }
        RoamRect RoomBox() => RoamRect.Union(list.Select(Box).Where(b => b is not null).Select(b => b!.Value));
        IReadOnlyList<RoomGroup> Result() => list.Where(c => placed.ContainsKey(c.Id)).Select(c => new RoomGroup(c.Id, placed[c.Id].X, placed[c.Id].Y)).ToList();

        var missing = list.Where(c => !placed.ContainsKey(c.Id)).ToList();
        if (missing.Count == 0) return Result();

        var mine = list.FirstOrDefault(c => SpaceIds.Same(c.Id, me));
        if (placed.Count == 0)
        {
            var others = list.Where(c => !SpaceIds.Same(c.Id, me));
            if (others.All(c => !sideOf(c.Id).Known))
            {
                // The Mac first, the others to its right, all on one desk line (y = 0).
                double x = 0;
                foreach (var c in list.OrderBy(c => c.IsMac ? 0 : 1).ThenBy(c => c.Id, IdOrder))
                {
                    var l = LocalBounds(c.Screens);
                    placed[c.Id] = Offset(l, x, -l.H);
                    x += l.W;
                }
                return Result();
            }
            if (mine is not null)
            {
                var l = LocalBounds(mine.Screens);
                placed[mine.Id] = Offset(l, 0, -l.H);
                missing.Remove(mine);
            }
        }

        // Known sides first, then guesses (to the right); each side in id order.
        var order = new[] { RoamSide.Right, RoamSide.Left, RoamSide.Above, RoamSide.Below };
        missing = missing
            .OrderBy(c => sideOf(c.Id).Known ? 0 : 1)
            .ThenBy(c => sideOf(c.Id) is { Known: true } s ? Array.IndexOf(order, s.Side) : 0)
            .ThenBy(c => c.Id, IdOrder)
            .ToList();
        foreach (var c in missing)
        {
            var s = sideOf(c.Id);
            var side = s.Known ? s.Side : RoamSide.Right;
            var room = RoomBox();
            var anchor = (mine is not null ? Box(mine) : null) ?? room;
            var l = LocalBounds(c.Screens);
            if (room.IsEmpty)
            {
                placed[c.Id] = Offset(l, 0, -l.H);
                continue;
            }
            placed[c.Id] = side switch
            {
                RoamSide.Left => Offset(l, room.X - l.W, anchor.Bottom - l.H),
                RoamSide.Above => Offset(l, anchor.X + anchor.W / 2 - l.W / 2, room.Y - l.H),
                RoamSide.Below => Offset(l, anchor.X + anchor.W / 2 - l.W / 2, room.Bottom),
                _ => Offset(l, room.Right, anchor.Bottom - l.H),
            };
        }
        return Result();
    }

    /// <summary>Learning (§10.2): the offset that makes <paramref name="c"/>'s group touch <paramref name="anchor"/> (my group's box) on <paramref name="side"/>, centred on that edge.</summary>
    public static (double X, double Y) Touching(RoomComputer c, RoamSide side, RoamRect anchor)
    {
        var l = LocalBounds(c.Screens);
        return side switch
        {
            RoamSide.Right => Offset(l, anchor.Right, anchor.Y + anchor.H / 2 - l.H / 2),
            RoamSide.Left => Offset(l, anchor.X - l.W, anchor.Y + anchor.H / 2 - l.H / 2),
            RoamSide.Above => Offset(l, anchor.X + anchor.W / 2 - l.W / 2, anchor.Y - l.H),
            _ => Offset(l, anchor.X + anchor.W / 2 - l.W / 2, anchor.Bottom),
        };
    }

    /// <summary>
    /// The offset that puts <paramref name="c"/> on <paramref name="side"/> of the whole <paramref name="anchor"/> box: touching, bottoms
    /// aligned for left/right, centred for above/below (the Devices tab's ← / → picker with a room).
    /// </summary>
    public static (double X, double Y) NextTo(RoomComputer c, RoamRect anchor, RoamSide side)
    {
        var l = LocalBounds(c.Screens);
        return side switch
        {
            RoamSide.Left => Offset(l, anchor.X - l.W, anchor.Bottom - l.H),
            RoamSide.Right => Offset(l, anchor.Right, anchor.Bottom - l.H),
            RoamSide.Above => Offset(l, anchor.X + anchor.W / 2 - l.W / 2, anchor.Y - l.H),
            _ => Offset(l, anchor.X + anchor.W / 2 - l.W / 2, anchor.Bottom),
        };
    }

    /// <summary>
    /// §10.4 snapping (the Mac's RoomSnap.snap): the correction that snaps a dragged group's screens to the other groups' screen edges
    /// within <paramref name="reach"/> mm, each axis on its own — side by side (touching, tops/bottoms aligned) or stacked (touching,
    /// lefts/rights/centres aligned). The nearest candidate wins; on a tie, the first found.
    /// </summary>
    public static (double Dx, double Dy) Snap(IReadOnlyList<RoamRect> moving, IReadOnlyList<RoamRect> others, double reach = SnapDistance)
    {
        double? bestX = null, bestY = null;
        void Consider(double d, ref double? best)
        {
            if (Math.Abs(d) > reach) return;
            if (best is { } b && Math.Abs(b) <= Math.Abs(d)) return;
            best = d;
        }
        foreach (var m in moving)
        {
            foreach (var o in others)
            {
                var sideBySide = m.Bottom >= o.Y - reach && m.Y <= o.Bottom + reach;
                var stacked = m.Right >= o.X - reach && m.X <= o.Right + reach;
                if (sideBySide)
                {
                    Consider(o.Right - m.X, ref bestX);   // my left edge on its right edge
                    Consider(o.X - m.Right, ref bestX);   // my right edge on its left edge
                    Consider(o.Bottom - m.Bottom, ref bestY); // bottoms aligned
                    Consider(o.Y - m.Y, ref bestY);       // tops aligned
                }
                if (stacked)
                {
                    Consider(o.Bottom - m.Y, ref bestY);  // under it
                    Consider(o.Y - m.Bottom, ref bestY);  // on top of it
                    Consider(o.X + o.W / 2 - (m.X + m.W / 2), ref bestX); // centred
                    Consider(o.X - m.X, ref bestX);       // lefts aligned
                    Consider(o.Right - m.Right, ref bestX); // rights aligned
                }
            }
        }
        return (bestX ?? 0, bestY ?? 0);
    }
}
