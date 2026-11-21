// H.I.B Roam (docs/SPACE.md §9): shared mouse & keyboard between this PC and another computer.
// The geometry, all pure and unit-agnostic: the same functions work on this PC's physical pixels and on
// the other computer's points/DIPs (screensInfo coordinates, y down). The mechanism follows Deskflow's
// server (jump zone at the outer edge, proportional mapping onto the neighbour, corner lock) — written
// here from scratch for H.I.B.
using System;
using System.Collections.Generic;
using System.Linq;

namespace HIB.Core.Roam;

/// <summary>Where the other computer sits, seen from this one.</summary>
public enum RoamSide { Left, Right, Above, Below }

public static class RoamSides
{
    /// <summary>The wire word (ctlHello.manualSide).</summary>
    public static string Wire(RoamSide side) => side switch
    {
        RoamSide.Left => "left",
        RoamSide.Right => "right",
        RoamSide.Above => "above",
        _ => "below",
    };

    public static RoamSide? Parse(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "left" => RoamSide.Left,
        "right" => RoamSide.Right,
        "above" or "top" => RoamSide.Above,
        "below" or "bottom" => RoamSide.Below,
        _ => null,
    };

    /// <summary>The same pair seen from the other computer (B right of A ⇔ A left of B).</summary>
    public static RoamSide Mirror(RoamSide side) => side switch
    {
        RoamSide.Left => RoamSide.Right,
        RoamSide.Right => RoamSide.Left,
        RoamSide.Above => RoamSide.Below,
        _ => RoamSide.Above,
    };

    public static bool IsHorizontal(RoamSide side) => side is RoamSide.Left or RoamSide.Right;

    /// <summary>"left", "right", "top", "bottom" — for "off the right edge".</summary>
    public static string EdgeWord(RoamSide side) => side switch
    {
        RoamSide.Left => "left",
        RoamSide.Right => "right",
        RoamSide.Above => "top",
        _ => "bottom",
    };

    public static string Arrow(RoamSide side) => side switch
    {
        RoamSide.Left => "←",
        RoamSide.Right => "→",
        RoamSide.Above => "↑",
        _ => "↓",
    };

    /// <summary>The edge pill: "PC-INTERN →" on the right edge, "← PC-INTERN" on the left.</summary>
    public static string PillText(RoamSide side, string name) => side switch
    {
        RoamSide.Left => $"← {name}",
        RoamSide.Above => $"↑ {name}",
        RoamSide.Below => $"↓ {name}",
        _ => $"{name} →",
    };
}

/// <summary>An axis-aligned rectangle (pixels on this PC, points/DIPs on the other computer).</summary>
public readonly record struct RoamRect(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public bool IsEmpty => W <= 0 || H <= 0;

    /// <summary>The last addressable unit inside (a cursor never sits on Right/Bottom themselves).</summary>
    public double MaxX => X + Math.Max(0, W - 1);
    public double MaxY => Y + Math.Max(0, H - 1);

    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;

    /// <summary>The nearest point inside.</summary>
    public (double X, double Y) Clamp(double x, double y) => (Math.Clamp(x, X, MaxX), Math.Clamp(y, Y, MaxY));

    public double DistanceSquared(double x, double y)
    {
        var (cx, cy) = Clamp(x, y);
        return (cx - x) * (cx - x) + (cy - y) * (cy - y);
    }

    public static RoamRect Union(IEnumerable<RoamRect> rects)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        var any = false;
        foreach (var x in rects)
        {
            if (x.IsEmpty) continue;
            any = true;
            l = Math.Min(l, x.X);
            t = Math.Min(t, x.Y);
            r = Math.Max(r, x.Right);
            b = Math.Max(b, x.Bottom);
        }
        return any ? new RoamRect(l, t, r - l, b - t) : default;
    }
}

/// <summary>One monitor of this PC: its Point &amp; Flick id, physical pixels and scale (DIPs = pixels / scale, like screensInfo).</summary>
public sealed record RoamDisplay(string Id, RoamRect Pixels, double Scale, bool Primary = false)
{
    /// <summary>§10.1: the monitor's friendly name, physical size (0 = unknown) and whether it is the laptop's panel.</summary>
    public string Name { get; init; } = "";
    public double MmW { get; init; }
    public double MmH { get; init; }
    public bool BuiltIn { get; init; }
    public double SafeScale => Scale > 0 ? Scale : 1;
    public RoamRect Dips => new(Pixels.X / SafeScale, Pixels.Y / SafeScale, Pixels.W / SafeScale, Pixels.H / SafeScale);
}

/// <summary>A screen as ctlHello describes it (points/DIPs, y down), with §10.1's name, millimetres, native pixels and scale.</summary>
public sealed record RoamScreen(string Id, double X, double Y, double W, double H)
{
    public string Name { get; init; } = "";
    public double MmW { get; init; }
    public double MmH { get; init; }
    public bool BuiltIn { get; init; }
    public int PxW { get; init; }
    public int PxH { get; init; }
    public double Scale { get; init; } = 1;

    public RoamRect Rect => new(X, Y, W, H);
}

public static class RoamGeometry
{
    /// <summary>No switch within this many points of a corner (keeps hot corners, Deskflow's corner lock).</summary>
    public const double CornerSize = 24;
    /// <summary>The edge glow shows within this many points of a shared edge.</summary>
    public const double GlowDistance = 40;
    /// <summary>Coming home, the cursor lands this far inside the edge so it doesn't bounce straight back.</summary>
    public const double ReturnInset = 2;

    public static RoamRect Bounds(IEnumerable<RoamRect> screens) => RoamRect.Union(screens);

    /// <summary>The cursor sits on the outer edge of the whole desktop on <paramref name="side"/>.</summary>
    public static bool AtEdge(RoamSide side, double x, double y, RoamRect bounds) => side switch
    {
        RoamSide.Left => x <= bounds.X,
        RoamSide.Right => x >= bounds.MaxX,
        RoamSide.Above => y <= bounds.Y,
        _ => y >= bounds.MaxY,
    };

    /// <summary>How far past the outer edge a point is (0 when inside): the virtual cursor going home.</summary>
    public static double Beyond(RoamSide side, double x, double y, RoamRect bounds) => side switch
    {
        RoamSide.Left => Math.Max(0, bounds.X - x),
        RoamSide.Right => Math.Max(0, x - bounds.MaxX),
        RoamSide.Above => Math.Max(0, bounds.Y - y),
        _ => Math.Max(0, y - bounds.MaxY),
    };

    /// <summary>Distance from a point inside to the outer edge on <paramref name="side"/> (0 at the edge).</summary>
    public static double DistanceToEdge(RoamSide side, double x, double y, RoamRect bounds) => side switch
    {
        RoamSide.Left => Math.Max(0, x - bounds.X),
        RoamSide.Right => Math.Max(0, bounds.MaxX - x),
        RoamSide.Above => Math.Max(0, y - bounds.Y),
        _ => Math.Max(0, bounds.MaxY - y),
    };

    /// <summary>Position along an edge, 0 (top/left end) … 1 (bottom/right end).</summary>
    public static double EdgeRatio(RoamSide side, double x, double y, RoamRect bounds)
    {
        var r = RoamSides.IsHorizontal(side)
            ? (bounds.H > 0 ? (y - bounds.Y) / bounds.H : 0.5)
            : (bounds.W > 0 ? (x - bounds.X) / bounds.W : 0.5);
        return double.IsFinite(r) ? Math.Clamp(r, 0, 1) : 0.5;
    }

    /// <summary>The point at <paramref name="ratio"/> along the outer edge <paramref name="side"/> of <paramref name="bounds"/>.</summary>
    public static (double X, double Y) PointOnEdge(RoamSide side, double ratio, RoamRect bounds, double inset = 0)
    {
        ratio = double.IsFinite(ratio) ? Math.Clamp(ratio, 0, 1) : 0.5;
        return side switch
        {
            RoamSide.Left => (bounds.X + inset, bounds.Y + ratio * bounds.H),
            RoamSide.Right => (bounds.MaxX - inset, bounds.Y + ratio * bounds.H),
            RoamSide.Above => (bounds.X + ratio * bounds.W, bounds.Y + inset),
            _ => (bounds.X + ratio * bounds.W, bounds.MaxY - inset),
        };
    }

    /// <summary>
    /// SPACE.md §9.2: crossing at <paramref name="ratio"/> along an edge enters the other desktop at the same
    /// proportional position along its opposite edge <paramref name="entrySide"/>, then clamps into a real screen.
    /// </summary>
    public static (double X, double Y) EnterAt(RoamSide entrySide, double ratio, IReadOnlyList<RoamRect> screens, double inset = 0)
    {
        var bounds = Bounds(screens);
        if (bounds.IsEmpty) return (0, 0);
        var (x, y) = PointOnEdge(entrySide, ratio, bounds, inset);
        return ClampIntoScreens(x, y, screens);
    }

    /// <summary>The point itself when a screen holds it, else the nearest point on any real screen (never a gap).</summary>
    public static (double X, double Y) ClampIntoScreens(double x, double y, IReadOnlyList<RoamRect> screens)
    {
        if (screens.Count == 0) return (x, y);
        foreach (var s in screens)
            if (s.Contains(x, y)) return (x, y);
        var best = screens[0];
        var bestD = double.MaxValue;
        foreach (var s in screens)
        {
            if (s.IsEmpty) continue;
            var d = s.DistanceSquared(x, y);
            if (d < bestD)
            {
                bestD = d;
                best = s;
            }
        }
        return best.Clamp(x, y);
    }

    /// <summary>The screen holding the point, else the nearest one.</summary>
    public static int ScreenAt(double x, double y, IReadOnlyList<RoamRect> screens)
    {
        var best = -1;
        var bestD = double.MaxValue;
        for (var i = 0; i < screens.Count; i++)
        {
            if (screens[i].Contains(x, y)) return i;
            var d = screens[i].DistanceSquared(x, y);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// Deskflow's corner lock: against a left/right edge, within <paramref name="size"/> of the top or bottom of the
    /// screen the cursor is on; against a top/bottom edge, within <paramref name="size"/> of its left or right end.
    /// </summary>
    public static bool InCorner(RoamSide side, double x, double y, IReadOnlyList<RoamRect> screens, double size)
    {
        var i = ScreenAt(x, y, screens);
        if (i < 0) return false;
        var s = screens[i];
        return RoamSides.IsHorizontal(side)
            ? y < s.Y + size || y > s.MaxY - size
            : x < s.X + size || x > s.MaxX - size;
    }

    // ---------------- this PC: pixels ↔ DIPs ----------------

    /// <summary>A physical pixel on this PC → its DIP (screensInfo) coordinates, by the monitor that holds it.</summary>
    public static (double X, double Y) PixelToDip(double px, double py, IReadOnlyList<RoamDisplay> displays)
    {
        if (displays.Count == 0) return (px, py);
        var i = ScreenAt(px, py, displays.Select(d => d.Pixels).ToList());
        var d = displays[Math.Max(0, i)];
        var (cx, cy) = d.Pixels.Clamp(px, py);
        return (cx / d.SafeScale, cy / d.SafeScale);
    }

    /// <summary>A DIP point (what the controller sends) → a physical pixel on the monitor whose DIP rectangle holds it.</summary>
    public static (double X, double Y) DipToPixel(double x, double y, IReadOnlyList<RoamDisplay> displays)
    {
        if (displays.Count == 0) return (x, y);
        var dips = displays.Select(d => d.Dips).ToList();
        var i = Math.Max(0, ScreenAt(x, y, dips));
        var (cx, cy) = dips[i].Clamp(x, y);
        var d = displays[i];
        var (px, py) = (cx * d.SafeScale, cy * d.SafeScale);
        return d.Pixels.Clamp(px, py);
    }

    /// <summary>The scale of the monitor holding a pixel (1 when unknown).</summary>
    public static double ScaleAt(double px, double py, IReadOnlyList<RoamDisplay> displays)
    {
        if (displays.Count == 0) return 1;
        var i = ScreenAt(px, py, displays.Select(d => d.Pixels).ToList());
        return i < 0 ? 1 : displays[i].SafeScale;
    }
}
