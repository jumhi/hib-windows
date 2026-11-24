// H.I.B Roam (docs/SPACE.md §12.4): games and full-screen apps. Each computer watches the app in front of it and tells
// the others (ctlTargetState): "fullscreen" when the window covers its whole monitor, "locked" when it also hides or
// clips the cursor (a game steering with raw mouse motion). Controlling a locked computer sends relative motion and
// never leaves by an edge; with a full-screen app on either side the edges close except a gate — the top 15 % (at least
// 60 px) of each shared edge — where it takes two pushes within 500 ms to cross (Deskflow's switch double-tap).
using System;

namespace HIB.Core.Roam;

public enum RoamGameMode { Normal, Fullscreen, Locked }

public static class RoamGameModes
{
    public static string Wire(RoamGameMode m) => m switch
    {
        RoamGameMode.Fullscreen => "fullscreen",
        RoamGameMode.Locked => "locked",
        _ => "normal",
    };

    public static RoamGameMode Parse(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "fullscreen" => RoamGameMode.Fullscreen,
        "locked" => RoamGameMode.Locked,
        _ => RoamGameMode.Normal,
    };

    /// <summary>
    /// The state of the app in front. Windows: <c>fullscreen</c> = its window covers its whole monitor; <c>locked</c> =
    /// fullscreen and the cursor is hidden (GetCursorInfo flags 0) or clipped to less than that monitor (GetClipCursor).
    /// H.I.B's own windows (and no window) are normal.
    /// </summary>
    public static RoamGameMode Classify(RoamForeground? front)
    {
        if (front is null || front.Own || front.Monitor.IsEmpty || front.WindowRect.IsEmpty) return RoamGameMode.Normal;
        var w = front.WindowRect;
        var m = front.Monitor;
        var covers = w.X <= m.X && w.Y <= m.Y && w.Right >= m.Right && w.Bottom >= m.Bottom;
        if (!covers) return RoamGameMode.Normal;
        var clipped = front.Clip is { IsEmpty: false } c && (c.W < m.W || c.H < m.H);
        return !front.CursorShown || clipped ? RoamGameMode.Locked : RoamGameMode.Fullscreen;
    }
}

/// <summary>The gate: where a closed edge still lets the mouse through (two pushes).</summary>
public static class RoamGate
{
    public const double Share = 0.15;
    public const double MinLength = 60;

    /// <summary>
    /// The gate along an edge segment <paramref name="start"/>…<paramref name="end"/> (pixels or points along it): its first 15 %, at
    /// least 60 units (<paramref name="unit"/> = pixels per point), never more than the segment. For a left or right edge that is the top;
    /// for a top or bottom edge, the left end.
    /// </summary>
    public static (double Start, double End) Of(double start, double end, double unit = 1)
    {
        var len = Math.Max(0, end - start);
        var gate = Math.Min(len, Math.Max(len * Share, MinLength * (unit > 0 ? unit : 1)));
        return (start, start + gate);
    }

    public static bool Contains(double along, double start, double end, double unit = 1)
    {
        var (g0, g1) = Of(start, end, unit);
        return along >= g0 && along <= g1;
    }
}

public enum RoamPush { None, First, Cross }

/// <summary>
/// Two pushes at an edge (Deskflow's switch double-tap; SPACE.md §12.6): a push is a crossing attempt inside the gate. The second
/// must come within <see cref="Window"/> of the last event of the first, after the cursor moved back at least
/// <see cref="AwayDistance"/> from the edge. Staying pressed against the edge is one push.
/// </summary>
public sealed class DoublePushGate
{
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(500);
    public const double AwayDistance = 3;

    DateTime? _first;
    DateTime _last;
    bool _left;
    bool _atEdge;
    object? _edge;

    /// <summary>
    /// One pointer sample: at the edge (key: which edge), or away from it by <paramref name="distance"/>. Returns
    /// <see cref="RoamPush.First"/> on a first push (show "push again"), <see cref="RoamPush.Cross"/> on the second.
    /// </summary>
    public RoamPush Feed(object edge, bool atEdge, double distance, DateTime now)
    {
        if (!Equals(edge, _edge))
        {
            Reset();
            _edge = edge;
        }
        if (!atEdge)
        {
            if (_first is not null && distance >= AwayDistance) _left = true;
            if (distance >= AwayDistance) _atEdge = false;
            return RoamPush.None;
        }
        if (_atEdge)
        {
            if (_first is not null && !_left) _last = now; // still pressed against the edge: the same push goes on
            return RoamPush.None;
        }
        _atEdge = true;
        if (_first is not null && _left && now - _last <= Window)
        {
            Reset();
            _edge = edge;
            return RoamPush.Cross;
        }
        _first = now;
        _last = now;
        _left = false;
        return RoamPush.First;
    }

    public void Reset()
    {
        _first = null;
        _left = false;
        _atEdge = false;
        _edge = null;
    }
}
