// H.I.B Roam on the desktop (docs/SPACE.md §9.5–§9.6): tells the engine this PC's monitors, and shows what
// the engine asks for — the edge glow, the drop catcher at the edge, the ghost card of a file dragged in
// from the other computer, the "is on this side now · Undo" notice, and the round-2 pills (SPACE.md §12: game
// mode, the gate, "Locked to …", the admin-app warning).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using HIB.Core.Roam;
using HIB.Views.Roam;

namespace HIB.Services;

public sealed partial class RoamHost : IDisposable
{
    readonly AppController _app;
    readonly object _gate = new();
    RoamEdgeHint? _hint;
    bool _hintPosted;
    RoamGhost? _ghostState;
    bool _ghostPosted;
    RoamEdgeWindow? _edge;
    RoamGhostWindow? _ghost;
    RoamNoticeWindow? _notice;
    RoamDropCatcherWindow? _catcher;
    RoamPillWindow? _pill;

    public RoamHost(AppController app)
    {
        _app = app;
        var roam = app.Core.Roam;
        roam.HintChanged += h =>
        {
            lock (_gate)
            {
                _hint = h;
                if (_hintPosted) return;
                _hintPosted = true;
            }
            Dispatcher.UIThread.Post(ApplyHint);
        };
        roam.GhostChanged += g =>
        {
            lock (_gate)
            {
                _ghostState = g;
                if (_ghostPosted) return;
                _ghostPosted = true;
            }
            Dispatcher.UIThread.Post(ApplyGhost);
        };
        roam.ProbeChanged += p => Dispatcher.UIThread.Post(() => ApplyProbe(p));
        roam.SideLearned += n => Dispatcher.UIThread.Post(() => ShowNotice(n));
        roam.PillChanged += p => Dispatcher.UIThread.Post(() => ShowPill(p));
    }

    /// <summary>The edge glow on screen (UI thread), or null.</summary>
    public RoamEdgeWindow? EdgeGlow => _edge is { IsFadingOut: false, IsClosed: false } e ? e : null;
    public RoamGhostWindow? Ghost => _ghost;
    public RoamNoticeWindow? Notice => _notice;
    public RoamDropCatcherWindow? Catcher => _catcher;
    public RoamPillWindow? Pill => _pill;

    /// <summary>Tells the engine this PC's monitors (UI thread; PointFlickHost calls it with screensInfo).</summary>
    public void PublishDisplays()
    {
        var list = _app.Screens.Current()
            .Select(x => new RoamDisplay(x.Info.Id, new RoamRect(x.Screen.Bounds.X, x.Screen.Bounds.Y, x.Screen.Bounds.Width, x.Screen.Bounds.Height),
                x.Screen.Scaling > 0 ? x.Screen.Scaling : 1, x.Info.Primary)
            {
                Name = x.Info.Name,
                MmW = x.Info.MmW,
                MmH = x.Info.MmH,
                BuiltIn = x.Info.BuiltIn,
            })
            .ToList();
        if (list.Count > 0) _app.Core.Roam.SetDisplays(list);
    }

    Screen? ScreenFor(RoamRect pixels)
    {
        var all = _app.Screens.All;
        return all.FirstOrDefault(s => s.Bounds.X == (int)pixels.X && s.Bounds.Y == (int)pixels.Y && s.Bounds.Width == (int)pixels.W && s.Bounds.Height == (int)pixels.H)
               ?? all.FirstOrDefault(s => s.Bounds.Contains(new PixelPoint((int)(pixels.X + pixels.W / 2), (int)(pixels.Y + pixels.H / 2))))
               ?? _app.Screens.Primary;
    }

    string IdFor(Screen s) => _app.Screens.Current().FirstOrDefault(x => ReferenceEquals(x.Screen, s)).Info?.Id ?? ScreenMap.BoundsId(s.Bounds);

    // ---------------- edge glow ----------------

    void ApplyHint()
    {
        RoamEdgeHint? hint;
        lock (_gate)
        {
            hint = _hint;
            _hintPosted = false;
        }
        ShowHint(hint);
    }

    /// <summary>Shows (or hides) the glow for <paramref name="hint"/> (UI thread; tests call it directly).</summary>
    public void ShowHint(RoamEdgeHint? hint)
    {
        if (hint is null || ScreenFor(hint.Screen) is not { } screen)
        {
            _edge?.FadeOutAndClose();
            _edge = null;
            return;
        }
        var scale = screen.Scaling > 0 ? screen.Scaling : 1;
        var origin = RoamSides.IsHorizontal(hint.Side) ? screen.Bounds.Y : screen.Bounds.X;
        var along = (hint.Along - origin) / scale;
        var (s0, s1) = hint.HasSegment ? ((hint.SegStart - origin) / scale, (hint.SegEnd - origin) / scale) : (0.0, 0.0);
        var id = IdFor(screen);
        if (_edge is { IsFadingOut: false, IsClosed: false } current && current.Side == hint.Side && current.ScreenId == id)
        {
            current.Update(hint.PillText, along, s0, s1);
            return;
        }
        _edge?.FadeOutAndClose();
        _edge = new RoamEdgeWindow(hint.Side, id);
        _edge.Update(hint.PillText, along, s0, s1);
        _edge.ShowOn(screen);
    }

    // ---------------- drop catcher (controller side) ----------------

    void ApplyProbe(RoamEdgeHint? at)
    {
        if (at is null || ScreenFor(at.Screen) is not { } screen)
        {
            _catcher?.Close();
            _catcher = null;
            return;
        }
        if (_catcher is null)
        {
            _catcher = new RoamDropCatcherWindow(paths => _app.Core.Roam.SetFileDrag(paths));
            _catcher.Place(at, screen.Scaling > 0 ? screen.Scaling : 1);
            _catcher.Show();
            // An OLE drag only looks for a new target when the mouse moves: nudge it once.
            Nudge(at.Side);
        }
        _catcher.Place(at, screen.Scaling > 0 ? screen.Scaling : 1);
    }

    static void Nudge(RoamSide side)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (!GetCursorPos(out var p)) return;
            var (dx, dy) = side switch { RoamSide.Left => (1, 0), RoamSide.Right => (-1, 0), RoamSide.Above => (0, 1), _ => (0, -1) };
            SetCursorPos(p.X + dx, p.Y + dy);
            SetCursorPos(p.X, p.Y);
        }
        catch { }
    }

    // ---------------- ghost card (target side) ----------------

    void ApplyGhost()
    {
        RoamGhost? g;
        lock (_gate)
        {
            g = _ghostState;
            _ghostPosted = false;
        }
        ShowGhost(g);
    }

    /// <summary>Shows, moves or hides the ghost card (UI thread).</summary>
    public void ShowGhost(RoamGhost? g)
    {
        if (g is null)
        {
            _ghost?.Close();
            _ghost = null;
            return;
        }
        var displays = _app.Core.Roam.Displays;
        var (px, py) = RoamGeometry.DipToPixel(g.X, g.Y, displays);
        var scale = RoamGeometry.ScaleAt(px, py, displays);
        if (_ghost is null)
        {
            _ghost = new RoamGhostWindow();
            _ghost.Update(g);
            _ghost.MoveTo(px, py, scale);
            _ghost.Show();
        }
        _ghost.Update(g);
        _ghost.MoveTo(px, py, scale);
    }

    // ---------------- learned side ----------------

    void ShowNotice(RoamLearnedNotice n)
    {
        _notice?.Close();
        var roam = _app.Core.Roam;
        var w = new RoamNoticeWindow(RoamNoticeWindow.TextFor(n), roam.UndoLearned);
        _notice = w;
        w.Closed += (_, _) =>
        {
            if (ReferenceEquals(_notice, w)) _notice = null;
        };
        // At the middle of the edge that now leads to the other computer, on the monitor that reaches it.
        var displays = roam.Displays;
        var edgeDisplay = n.Side == RoamSide.Left
            ? displays.OrderBy(d => d.Pixels.X).FirstOrDefault()
            : displays.OrderByDescending(d => d.Pixels.Right).FirstOrDefault();
        w.Opened += (_, _) =>
        {
            if (edgeDisplay is null) return;
            var s = edgeDisplay.SafeScale;
            var width = w.Bounds.Width * s;
            var height = w.Bounds.Height * s;
            var r = edgeDisplay.Pixels;
            var x = n.Side == RoamSide.Left ? r.X + 12 * s : r.Right - width - 12 * s;
            w.Position = new PixelPoint((int)Math.Round(x), (int)Math.Round(r.Y + r.H / 2 - height / 2));
        };
        w.Show();
    }

    // ---------------- round-2 pills (§12) ----------------

    /// <summary>Shows (or hides, null) a pill (UI thread).</summary>
    public void ShowPill(RoamPill? pill)
    {
        _pill?.Close();
        _pill = null;
        if (pill is null) return;
        var displays = _app.Core.Roam.Displays;
        var screenPx = pill.At?.Screen ?? displays.FirstOrDefault(d => d.Primary)?.Pixels ?? displays.FirstOrDefault()?.Pixels ?? new RoamRect(0, 0, 1920, 1080);
        var screen = ScreenFor(screenPx);
        var scale = screen?.Scaling > 0 ? screen.Scaling : 1;
        var w = new RoamPillWindow(pill);
        _pill = w;
        w.Closed += (_, _) =>
        {
            if (ReferenceEquals(_pill, w)) _pill = null;
        };
        // Placed by its measured size before it shows, and again whenever its real size is known.
        var size = w.MeasuredSize();
        w.Position = RoamPillWindow.PlaceFor(pill, screenPx, scale, size.Width * scale, size.Height * scale);
        w.SizeChanged += (_, e) => w.Position = RoamPillWindow.PlaceFor(pill, screenPx, scale, e.NewSize.Width * scale, e.NewSize.Height * scale);
        w.Show();
    }

    public void Dispose()
    {
        _pill?.Close();
        _edge?.FadeOutAndClose();
        _ghost?.Close();
        _notice?.Close();
        _catcher?.Close();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT p);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);
}
