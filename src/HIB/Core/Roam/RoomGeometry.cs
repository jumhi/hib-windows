// H.I.B Room (docs/SPACE.md §10.2–§10.3), all pure: computers' screens placed in room millimetres, the
// automatic first arrangement, snapping while dragging, and the portals Roam's cursor follows — wherever a
// screen's edge and another computer's opposite edge are within 30 mm and overlap. Both computers run the
// same functions on the same layout, so they agree on every portal.
//
// Settled details (to match the Mac exactly):
//  - A computer's primary screen is the one whose origin is (0, 0), else the one flagged primary, else the first.
//  - mm per DIP = primary.mmW / primary.w (0.25 when its mm are unknown).
//  - A screen's room rect = (gx + x·k, gy + y·k, mmW, mmH) with k = that computer's mm per DIP; a screen with
//    unknown mm (either 0) is w·0.25 × h·0.25.
//  - Inside a screen, room mm ↔ DIPs map linearly over that screen's own rect (each axis on its own).
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
    public bool IsMac => !string.Equals(Platform, "windows", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A screen placed in the room: its computer's DIPs and its rectangle in room millimetres.</summary>
public sealed record RoomScreen(string Computer, string Id, RoamRect Dips, RoamRect Mm, RoomScreenSpec Spec);

/// <summary>
/// Where the cursor can cross: <see cref="FromScreen"/>'s <see cref="Side"/> edge faces <see cref="ToScreen"/>'s opposite edge,
/// overlapping from <see cref="Start"/> to <see cref="End"/> (room mm along the edge: y for left/right, x for top/bottom).
/// </summary>
public sealed record RoomPortal(string FromComputer, string FromScreen, RoamSide Side, string ToComputer, string ToScreen,
    double Start, double End, double Gap);

/// <summary>A crossing through a portal: the target screen and the entry point in the target computer's DIPs.</summary>
public sealed record RoomCrossing(RoomPortal Portal, RoomScreen To, double X, double Y, double At);

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

    /// <summary>
    /// The portal on that edge at <paramref name="at"/> (mm along it) whose target <paramref name="allowed"/> accepts. Two
    /// candidates: the nearer one, then the lower computer id, then the lower screen id.
    /// </summary>
    public RoomPortal? PortalAt(string computer, string screenId, RoamSide side, double at, Func<string, bool>? allowed = null)
    {
        const double eps = 1e-6;
        RoomPortal? best = null;
        foreach (var p in Portals)
        {
            if (p.Side != side || p.FromScreen != screenId || !SpaceIds.Same(p.FromComputer, computer)) continue;
            if (at < p.Start - eps || at > p.End + eps) continue;
            if (allowed is not null && !allowed(p.ToComputer)) continue;
            if (best is null || Better(p, best)) best = p;
        }
        return best;

        static bool Better(RoomPortal p, RoomPortal best)
        {
            if (p.Gap < best.Gap - eps) return true;
            if (p.Gap > best.Gap + eps) return false;
            var c = SpaceIds.Compare(p.ToComputer, best.ToComputer);
            return c < 0 || (c == 0 && string.CompareOrdinal(p.ToScreen, best.ToScreen) < 0);
        }
    }

    /// <summary>
    /// §10.3: the cursor at (<paramref name="x"/>, <paramref name="y"/>) (this computer's DIPs) leaves <paramref name="screenId"/>
    /// over its <paramref name="side"/> edge. Crossing at room position p enters the other screen at the same p, clamped into the
    /// overlap, converted to that computer's DIPs. Null when no portal is there.
    /// </summary>
    public RoomCrossing? Cross(string computer, string screenId, RoamSide side, double x, double y, Func<string, bool>? allowed = null, double inset = 0)
    {
        if (Find(computer, screenId) is not { } s) return null;
        var at = RoomGeometry.Along(s, side, x, y);
        if (PortalAt(computer, screenId, side, at, allowed) is not { } portal) return null;
        if (Find(portal.ToComputer, portal.ToScreen) is not { } to) return null;
        var clamped = Math.Clamp(at, portal.Start, portal.End);
        var (ex, ey) = RoomGeometry.EnterPoint(to, RoamSides.Mirror(side), clamped, inset);
        return new RoomCrossing(portal, to, ex, ey, clamped);
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

    public static readonly IComparer<string> IdOrder = Comparer<string>.Create(SpaceIds.Compare);

    /// <summary>The primary screen: origin (0, 0), else the one flagged primary, else the first.</summary>
    public static RoomScreenSpec? PrimaryOf(IReadOnlyList<RoomScreenSpec> screens)
        => screens.FirstOrDefault(s => Math.Abs(s.X) < 0.5 && Math.Abs(s.Y) < 0.5) ?? screens.FirstOrDefault(s => s.Primary) ?? screens.FirstOrDefault();

    /// <summary>A computer's mm per DIP: its primary screen's mmW / w (0.25 when unknown).</summary>
    public static double MmPerDip(IReadOnlyList<RoomScreenSpec> screens)
        => PrimaryOf(screens) is { MmW: > 0, MmH: > 0, W: > 0 } p ? p.MmW / p.W : DefaultMmPerDip;

    public static bool KnowsSize(RoomScreenSpec s) => s.MmW > 0 && s.MmH > 0;

    /// <summary>A screen's physical size (mm): its own, else 0.25 mm per DIP.</summary>
    public static (double W, double H) SizeMm(RoomScreenSpec s)
        => KnowsSize(s) ? (s.MmW, s.MmH) : (s.W * DefaultMmPerDip, s.H * DefaultMmPerDip);

    /// <summary>Each screen in mm relative to the computer's group origin (OS arrangement × mm per DIP; sizes their own).</summary>
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
            list.Add(new RoomScreen(c.Id, s.Id, s.Dips, new RoamRect(gx + m.X, gy + m.Y, m.W, m.H), s));
        }
        return list;
    }

    /// <summary>The room of every computer the layout places.</summary>
    public static RoomMap Build(IEnumerable<RoomComputer> computers, RoomLayout? layout)
    {
        if (layout is null) return RoomMap.Empty;
        var screens = new List<RoomScreen>();
        foreach (var c in computers)
        {
            if (c.Screens.Count == 0 || layout.Find(c.Id) is not { } g) continue;
            screens.AddRange(Place(c, g.X, g.Y));
        }
        return new RoomMap(screens);
    }

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
                Try(a, b, RoamSide.Right, b.Mm.X - a.Mm.Right, a.Mm.Y, a.Mm.Bottom, b.Mm.Y, b.Mm.Bottom);
                Try(a, b, RoamSide.Left, a.Mm.X - b.Mm.Right, a.Mm.Y, a.Mm.Bottom, b.Mm.Y, b.Mm.Bottom);
                Try(a, b, RoamSide.Below, b.Mm.Y - a.Mm.Bottom, a.Mm.X, a.Mm.Right, b.Mm.X, b.Mm.Right);
                Try(a, b, RoamSide.Above, a.Mm.Y - b.Mm.Bottom, a.Mm.X, a.Mm.Right, b.Mm.X, b.Mm.Right);
            }
        }
        return list;

        void Try(RoomScreen a, RoomScreen b, RoamSide side, double gap, double a0, double a1, double b0, double b1)
        {
            if (Math.Abs(gap) > PortalReach) return;
            var lo = Math.Max(a0, b0);
            var hi = Math.Min(a1, b1);
            if (hi - lo <= 0) return;
            list.Add(new RoomPortal(a.Computer, a.Id, side, b.Computer, b.Id, lo, hi, Math.Abs(gap)));
        }
    }

    /// <summary>A DIP point on a screen's edge → its position along that edge in room mm.</summary>
    public static double Along(RoomScreen s, RoamSide side, double x, double y)
        => RoamSides.IsHorizontal(side)
            ? s.Mm.Y + (s.Dips.H > 0 ? (y - s.Dips.Y) / s.Dips.H : 0.5) * s.Mm.H
            : s.Mm.X + (s.Dips.W > 0 ? (x - s.Dips.X) / s.Dips.W : 0.5) * s.Mm.W;

    /// <summary>Room mm → the screen's DIPs (linear over that screen).</summary>
    public static (double X, double Y) ToDips(RoomScreen s, double mmX, double mmY)
        => (s.Dips.X + (s.Mm.W > 0 ? (mmX - s.Mm.X) / s.Mm.W : 0) * s.Dips.W,
            s.Dips.Y + (s.Mm.H > 0 ? (mmY - s.Mm.Y) / s.Mm.H : 0) * s.Dips.H);

    /// <summary>The screen's DIPs → room mm.</summary>
    public static (double X, double Y) ToMm(RoomScreen s, double x, double y)
        => (s.Mm.X + (s.Dips.W > 0 ? (x - s.Dips.X) / s.Dips.W : 0) * s.Mm.W,
            s.Mm.Y + (s.Dips.H > 0 ? (y - s.Dips.Y) / s.Dips.H : 0) * s.Mm.H);

    /// <summary>The point <paramref name="at"/> mm along <paramref name="to"/>'s <paramref name="entrySide"/> edge, in its DIPs, inside the screen.</summary>
    public static (double X, double Y) EnterPoint(RoomScreen to, RoamSide entrySide, double at, double inset = 0)
    {
        var d = to.Dips;
        double x, y;
        if (RoamSides.IsHorizontal(entrySide))
        {
            y = d.Y + (to.Mm.H > 0 ? (at - to.Mm.Y) / to.Mm.H : 0.5) * d.H;
            x = entrySide == RoamSide.Left ? d.X + inset : d.MaxX - inset;
        }
        else
        {
            x = d.X + (to.Mm.W > 0 ? (at - to.Mm.X) / to.Mm.W : 0.5) * d.W;
            y = entrySide == RoamSide.Above ? d.Y + inset : d.MaxY - inset;
        }
        return d.Clamp(x, y);
    }

    // ---------------- arranging ----------------

    /// <summary>Where <paramref name="b"/> sits seen from <paramref name="a"/> (group bounding boxes): the axis they're more apart on.</summary>
    public static RoamSide SideOf(RoamRect a, RoamRect b)
    {
        var sepX = Math.Max(b.X - a.Right, a.X - b.Right);
        var sepY = Math.Max(b.Y - a.Bottom, a.Y - b.Bottom);
        if (sepX >= sepY) return b.X + b.W / 2 >= a.X + a.W / 2 ? RoamSide.Right : RoamSide.Left;
        return b.Y + b.H / 2 >= a.Y + a.H / 2 ? RoamSide.Below : RoamSide.Above;
    }

    /// <summary>
    /// §10.2 with no layout yet (rev 0): the anchor is the Mac (else the lowest id) at (0, 0); every other computer goes to its side
    /// from <paramref name="sideOf"/>(anchor, computer) (Point setup aims or manualSide; right when unknown): left/right chains touch,
    /// bottoms aligned on the anchor's desk line; above/below stack centred on the anchor.
    /// </summary>
    public static RoomLayout AutoPlace(IReadOnlyList<RoomComputer> computers, Func<string, string, RoamSide?> sideOf, string by)
    {
        var list = computers.Where(c => c.Screens.Count > 0).ToList();
        if (list.Count == 0) return new RoomLayout(0, by, Array.Empty<RoomGroup>());
        var anchor = list.Where(c => c.IsMac).OrderBy(c => c.Id, IdOrder).FirstOrDefault() ?? list.OrderBy(c => c.Id, IdOrder).First();
        var a = LocalBounds(anchor.Screens);
        var groups = new List<RoomGroup> { new(anchor.Id, 0, 0) };
        double right = a.Right, left = a.X, top = a.Y, bottom = a.Bottom, desk = a.Bottom, cx = a.X + a.W / 2;
        foreach (var c in list.Where(c => !ReferenceEquals(c, anchor)).OrderBy(c => c.Id, IdOrder))
        {
            var b = LocalBounds(c.Screens);
            double gx, gy;
            switch (sideOf(anchor.Id, c.Id) ?? RoamSide.Right)
            {
                case RoamSide.Left:
                    gx = left - b.Right;
                    gy = desk - b.Bottom;
                    left -= b.W;
                    break;
                case RoamSide.Above:
                    gx = cx - (b.X + b.W / 2);
                    gy = top - b.Bottom;
                    top -= b.H;
                    break;
                case RoamSide.Below:
                    gx = cx - (b.X + b.W / 2);
                    gy = bottom - b.Y;
                    bottom += b.H;
                    break;
                default:
                    gx = right - b.X;
                    gy = desk - b.Bottom;
                    right += b.W;
                    break;
            }
            groups.Add(new RoomGroup(c.Id, RoomLayout.Round(gx), RoomLayout.Round(gy)));
        }
        return new RoomLayout(0, by, groups);
    }

    /// <summary>
    /// A layout the user already arranged, plus computers it doesn't place yet: each beside <paramref name="self"/>'s side from
    /// <paramref name="sideOf"/>(self, computer) (right when unknown), past everything already there, bottoms on the room's desk line.
    /// </summary>
    public static RoomLayout AddMissing(RoomLayout layout, IReadOnlyList<RoomComputer> computers, string self, Func<string, string, RoamSide?> sideOf)
    {
        var missing = computers.Where(c => c.Screens.Count > 0 && !layout.Has(c.Id)).OrderBy(c => c.Id, IdOrder).ToList();
        if (missing.Count == 0) return layout;
        var result = layout;
        foreach (var c in missing)
        {
            var room = Build(computers, result);
            var all = RoamRect.Union(room.Screens.Select(s => s.Mm));
            var b = LocalBounds(c.Screens);
            if (all.IsEmpty)
            {
                result = result.WithGroup(c.Id, -b.X, -b.Y);
                continue;
            }
            var mine = room.BoundsOf(self);
            if (mine.IsEmpty) mine = all;
            var (gx, gy) = (sideOf(self, c.Id) ?? RoamSide.Right) switch
            {
                RoamSide.Left => (all.X - b.Right, all.Bottom - b.Bottom),
                RoamSide.Above => (mine.X + mine.W / 2 - (b.X + b.W / 2), all.Y - b.Bottom),
                RoamSide.Below => (mine.X + mine.W / 2 - (b.X + b.W / 2), all.Bottom - b.Y),
                _ => (all.Right - b.X, all.Bottom - b.Bottom),
            };
            result = result.WithGroup(c.Id, gx, gy);
        }
        return result;
    }

    /// <summary>
    /// The group offset that puts <paramref name="c"/> against <paramref name="edge"/>'s <paramref name="side"/> (touching), centred
    /// along it — what learning a moved computer does (§10.2).
    /// </summary>
    public static (double X, double Y) Beside(RoomComputer c, RoamRect edge, RoamSide side)
    {
        var b = LocalBounds(c.Screens);
        var (gx, gy) = side switch
        {
            RoamSide.Left => (edge.X - b.Right, edge.Y + edge.H / 2 - (b.Y + b.H / 2)),
            RoamSide.Right => (edge.Right - b.X, edge.Y + edge.H / 2 - (b.Y + b.H / 2)),
            RoamSide.Above => (edge.X + edge.W / 2 - (b.X + b.W / 2), edge.Y - b.Bottom),
            _ => (edge.X + edge.W / 2 - (b.X + b.W / 2), edge.Bottom - b.Y),
        };
        return (RoomLayout.Round(gx), RoomLayout.Round(gy));
    }

    /// <summary>
    /// The group offset that puts <paramref name="c"/> on <paramref name="side"/> of the whole <paramref name="anchor"/> box: touching,
    /// bottoms aligned for left/right, centred for above/below (the Devices tab's ← / → picker with a room).
    /// </summary>
    public static (double X, double Y) NextTo(RoomComputer c, RoamRect anchor, RoamSide side)
    {
        var b = LocalBounds(c.Screens);
        var (gx, gy) = side switch
        {
            RoamSide.Left => (anchor.X - b.Right, anchor.Bottom - b.Bottom),
            RoamSide.Right => (anchor.Right - b.X, anchor.Bottom - b.Bottom),
            RoamSide.Above => (anchor.X + anchor.W / 2 - (b.X + b.W / 2), anchor.Y - b.Bottom),
            _ => (anchor.X + anchor.W / 2 - (b.X + b.W / 2), anchor.Bottom - b.Y),
        };
        return (RoomLayout.Round(gx), RoomLayout.Round(gy));
    }

    /// <summary>
    /// §10.4 snapping: the smallest move (each axis on its own, within <paramref name="reach"/> mm) that lines an edge of a moving
    /// screen up with an edge of another computer's screen — touching (right↔left, bottom↔top) or aligned (left↔left, bottom↔bottom…).
    /// </summary>
    public static (double Dx, double Dy) Snap(IReadOnlyList<RoamRect> moving, IReadOnlyList<RoamRect> others, double reach = SnapDistance)
    {
        double? bx = null, by = null;
        foreach (var m in moving)
        {
            foreach (var o in others)
            {
                foreach (var c in new[] { o.Right - m.X, o.X - m.Right, o.X - m.X, o.Right - m.Right })
                    if (Math.Abs(c) <= reach && (bx is null || Math.Abs(c) < Math.Abs(bx.Value))) bx = c;
                foreach (var c in new[] { o.Bottom - m.Y, o.Y - m.Bottom, o.Y - m.Y, o.Bottom - m.Bottom })
                    if (Math.Abs(c) <= reach && (by is null || Math.Abs(c) < Math.Abs(by.Value))) by = c;
            }
        }
        return (bx ?? 0, by ?? 0);
    }
}
