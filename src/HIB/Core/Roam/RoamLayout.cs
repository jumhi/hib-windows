// H.I.B Roam (docs/SPACE.md §9.2): where the other computer is. From the phone's Point & Flick aims
// (screenLayout), a manual ← / → choice, or learned when the user keeps pushing against an empty edge.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HIB.Core.Link;
using HIB.Core.Space;

namespace HIB.Core.Roam;

/// <summary>One saved screen aim from <c>screenLayout</c>: compass heading and elevation in degrees.</summary>
public sealed record ScreenAim(string Computer, string Screen, double H, double E);

public static class RoamLayout
{
    /// <summary>Parses P→D <c>screenLayout</c> <c>{"screens":[{"computer","screen","h","e"}]}</c>.</summary>
    public static IReadOnlyList<ScreenAim> Parse(JsonElement data)
    {
        var list = new List<ScreenAim>();
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("screens", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var s in arr.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object) continue;
            var computer = s.Str("computer");
            if (string.IsNullOrWhiteSpace(computer)) continue;
            if (s.Double("h") is not { } h || s.Double("e") is not { } e || !double.IsFinite(h) || !double.IsFinite(e)) continue;
            list.Add(new ScreenAim(computer, s.Str("screen") ?? "", h, e));
        }
        return list;
    }

    /// <summary>
    /// The mean aim of a computer's screens as the mean of their unit direction vectors (so 359° and 1° average
    /// to 0°, not 180°). Null when there are no aims or they cancel out.
    /// </summary>
    public static (double H, double E)? MeanAim(IEnumerable<ScreenAim> aims)
    {
        double x = 0, y = 0, z = 0;
        var n = 0;
        foreach (var a in aims)
        {
            var h = a.H * Math.PI / 180;
            var e = a.E * Math.PI / 180;
            x += Math.Cos(e) * Math.Cos(h);
            y += Math.Cos(e) * Math.Sin(h);
            z += Math.Sin(e);
            n++;
        }
        if (n == 0) return null;
        var flat = Math.Sqrt(x * x + y * y);
        if (flat < 1e-9 && Math.Abs(z) < 1e-9) return null;
        var heading = Math.Atan2(y, x) * 180 / Math.PI;
        if (heading < 0) heading += 360;
        var elevation = Math.Atan2(z, flat) * 180 / Math.PI;
        return (heading, elevation);
    }

    /// <summary>The signed heading difference <paramref name="to"/> − <paramref name="from"/> in (−180, 180].</summary>
    public static double SignedHeadingDelta(double from, double to)
    {
        var d = (to - from) % 360;
        if (d < 0) d += 360;
        if (d > 180) d -= 360;
        return d;
    }

    /// <summary>
    /// SPACE.md §9.2: B's side relative to A. If |dh| ≥ |de|, B is right when dh &gt; 0, else left; otherwise B is above
    /// when de &gt; 0, else below. Both computers run this same function, so they agree.
    /// </summary>
    public static RoamSide SideFrom((double H, double E) a, (double H, double E) b)
    {
        var dh = SignedHeadingDelta(a.H, b.H);
        var de = b.E - a.E;
        if (Math.Abs(dh) >= Math.Abs(de)) return dh > 0 ? RoamSide.Right : RoamSide.Left;
        return de > 0 ? RoamSide.Above : RoamSide.Below;
    }

    /// <summary>Where <paramref name="other"/> sits relative to <paramref name="self"/> by the aims, or null when either has none.</summary>
    public static RoamSide? SideOf(IReadOnlyList<ScreenAim> layout, string self, string other)
    {
        var a = MeanAim(layout.Where(s => SpaceIds.Same(s.Computer, self)));
        var b = MeanAim(layout.Where(s => SpaceIds.Same(s.Computer, other)));
        return a is { } ma && b is { } mb ? SideFrom(ma, mb) : null;
    }

    /// <summary>True when both computers have at least one aim.</summary>
    public static bool HasAims(IReadOnlyList<ScreenAim> layout, string computer)
        => layout.Any(s => SpaceIds.Same(s.Computer, computer));

    /// <summary>A stable text of the layout, to tell a new Point setup from a re-send of the same one.</summary>
    public static string Signature(IReadOnlyList<ScreenAim> layout)
        => string.Join(";", layout.Select(s => $"{s.Computer.ToLowerInvariant()}|{s.Screen}|{s.H:0.###}|{s.E:0.###}").OrderBy(x => x, StringComparer.Ordinal));
}

/// <summary>
/// SPACE.md §9.2 "learns when a computer moves": pushing the physical cursor against a left or right outer edge that
/// has no neighbour 3 times within 2.5 s (a push = at the edge plus ≥ 30 more points of motion toward it; pushes are
/// separated by moving ≥ 20 points away). Top and bottom never learn. Feed it every physical pointer sample.
/// </summary>
public sealed class DeadEdgeLearner
{
    public const int PushesNeeded = 3;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(2.5);
    public const double PushMotion = 30;
    public const double AwayDistance = 20;

    sealed class EdgeState
    {
        public bool Armed = true;
        public double Motion;
        public readonly List<DateTime> Pushes = new();
    }

    readonly EdgeState _left = new();
    readonly EdgeState _right = new();

    /// <summary>
    /// One sample for a dead edge: <paramref name="atEdge"/> when the cursor is on it, <paramref name="toward"/> the
    /// motion toward it this sample (how far the pointer tried to go past the edge), <paramref name="distance"/> how far
    /// the cursor is from it. Returns true when this sample completes the third push (the state then resets).
    /// </summary>
    public bool Feed(RoamSide side, bool atEdge, double toward, double distance, DateTime now)
    {
        if (!RoamSides.IsHorizontal(side)) return false;
        var s = side == RoamSide.Left ? _left : _right;
        if (!atEdge)
        {
            s.Motion = 0;
            if (distance >= AwayDistance) s.Armed = true;
            return false;
        }
        if (!s.Armed) return false;
        if (toward > 0 && double.IsFinite(toward)) s.Motion += toward;
        if (s.Motion < PushMotion) return false;
        s.Armed = false;
        s.Motion = 0;
        s.Pushes.Add(now);
        s.Pushes.RemoveAll(t => now - t > Window);
        if (s.Pushes.Count < PushesNeeded) return false;
        s.Pushes.Clear();
        return true;
    }

    public void Reset()
    {
        foreach (var s in new[] { _left, _right })
        {
            s.Armed = true;
            s.Motion = 0;
            s.Pushes.Clear();
        }
    }
}
