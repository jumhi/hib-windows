// H.I.B Phone Remote, round 2 (docs/SPACE.md §15.5), the pure part:
//  - mouse_move_abs: a fraction of one display (§7.1 id, "" = main) → a desktop pixel → SendInput's normalized
//    virtual-desktop coordinates (multi-monitor, negative origins, physical pixels).
//  - key: macOS keycodes held down until isDown:false, tracked per phone link so every one can be let go when the
//    /screen session ends, the link drops or the phone goes quiet for 10 s; ⌘ held + Tab is Alt+Tab here (the switcher).
//  - keypress with modifiers (⌘S → Ctrl+S) through the same map, never touching a key the phone is holding.
// WindowsDesktopControl does the sending; tests drive all of this with fakes.
using System;
using System.Collections.Generic;
using System.Linq;
using HIB.Core.Remote;
using HIB.Core.Roam;

namespace HIB.Core.Act;

public static class RemotePointer
{
    /// <summary>
    /// The desktop pixel at (<paramref name="fx"/>, <paramref name="fy"/>) of the display with this §7.1 id (empty or unknown
    /// = the main display, as screenOpen does). Fractions are clamped to the display; null when they aren't numbers or there
    /// is no display.
    /// </summary>
    public static (int X, int Y)? Target(double fx, double fy, string? displayId, IReadOnlyList<RemoteDisplay> displays)
    {
        if (!double.IsFinite(fx) || !double.IsFinite(fy)) return null;
        var d = Resolve(displayId, displays);
        if (d is null || d.Bounds.IsEmpty) return null;
        var b = d.Bounds;
        var x = b.X + Math.Clamp((int)Math.Floor(Math.Clamp(fx, 0, 1) * b.W), 0, b.W - 1);
        var y = b.Y + Math.Clamp((int)Math.Floor(Math.Clamp(fy, 0, 1) * b.H), 0, b.H - 1);
        return (x, y);
    }

    /// <summary>The display with this id; else the main one; else the first.</summary>
    public static RemoteDisplay? Resolve(string? displayId, IReadOnlyList<RemoteDisplay> displays)
    {
        if (!string.IsNullOrEmpty(displayId))
            foreach (var x in displays) if (x.Id == displayId) return x;
        foreach (var x in displays) if (x.Main) return x;
        return displays.Count > 0 ? displays[0] : null;
    }

    /// <summary>
    /// A desktop pixel → SendInput's MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK coordinates (0…65535 across the whole
    /// virtual desktop <paramref name="desk"/>, whose origin may be negative). Aims at the pixel's centre so Windows'
    /// own rounding (n × width / 65536) lands on exactly that pixel.
    /// </summary>
    public static (int Nx, int Ny) Normalize(int x, int y, PixelBox desk)
    {
        if (desk.IsEmpty) return (0, 0);
        static int Axis(int v, int origin, int size)
        {
            var p = Math.Clamp(v - origin, 0, size - 1);
            return (int)Math.Clamp(Math.Floor((p + 0.5) * 65536.0 / size), 0, 65535);
        }
        return (Axis(x, desk.X, desk.W), Axis(y, desk.Y, desk.H));
    }

    /// <summary>Where Windows puts a normalized coordinate (the inverse of <see cref="Normalize"/>; for tests and checks).</summary>
    public static (int X, int Y) Denormalize(int nx, int ny, PixelBox desk)
        => (desk.X + (int)Math.Floor(nx * (double)desk.W / 65536), desk.Y + (int)Math.Floor(ny * (double)desk.H / 65536));

    /// <summary>The bounding box of every display (the virtual desktop).</summary>
    public static PixelBox VirtualDesk(IEnumerable<PixelBox> displays)
    {
        var list = displays.Where(b => !b.IsEmpty).ToList();
        if (list.Count == 0) return default;
        int l = list.Min(b => b.X), t = list.Min(b => b.Y), r = list.Max(b => b.Right), bo = list.Max(b => b.Bottom);
        return new PixelBox(l, t, r - l, bo - t);
    }
}

/// <summary>macOS keycodes and the phone's modifier names as keys on this PC (SPACE.md §9.3 / §15.5).</summary>
public static class RemoteKeyMap
{
    public const int MacTab = 0x30;
    public const int MacCommand = 0x37, MacRightCommand = 0x36, MacControl = 0x3B, MacRightControl = 0x3E;

    public static bool IsCommand(int macKeycode) => macKeycode is MacCommand or MacRightCommand;

    /// <summary>
    /// A macOS keycode → HID usage. With <paramref name="cmdAsCtrl"/> (the default) ⌘ is Ctrl and ⌃ is the Windows key (the
    /// phone's "⌃/Win" key); off, ⌘ is the Windows key and ⌃ is Ctrl. Everything else as <see cref="RoamKeys.FromMacKeycode"/>.
    /// </summary>
    public static int? Usage(int macKeycode, bool cmdAsCtrl) => macKeycode switch
    {
        MacCommand => cmdAsCtrl ? RoamKeys.LeftCtrl : RoamKeys.LeftGui,
        MacRightCommand => cmdAsCtrl ? RoamKeys.RightCtrl : RoamKeys.RightGui,
        MacControl => cmdAsCtrl ? RoamKeys.LeftGui : RoamKeys.LeftCtrl,
        MacRightControl => cmdAsCtrl ? RoamKeys.RightGui : RoamKeys.RightCtrl,
        _ => RoamKeys.FromMacKeycode(macKeycode),
    };

    /// <summary>
    /// The phone's modifier names (keypress / type) as keys, in order, without repeats. "command"/"cmd" is Ctrl with
    /// <paramref name="cmdAsCtrl"/>, else the Windows key; "ctrl"/"control" is Ctrl (a phone talking to Windows says "ctrl"
    /// for Ctrl); "option"/"alt" is Alt; "win"/"meta" is the Windows key; fn has no key. <c>Command</c> marks the ones that
    /// came from ⌘ (the ⌘Tab rule turns those into Alt).
    /// </summary>
    public static IReadOnlyList<(int Usage, bool Command)> Modifiers(IEnumerable<string?> names, bool cmdAsCtrl)
    {
        var list = new List<(int, bool)>();
        foreach (var raw in names)
        {
            var n = (raw ?? "").Trim().ToLowerInvariant();
            (int Usage, bool Command)? m = n switch
            {
                "shift" => (RoamKeys.LeftShift, false),
                "command" or "cmd" => (cmdAsCtrl ? RoamKeys.LeftCtrl : RoamKeys.LeftGui, true),
                "ctrl" or "control" => (RoamKeys.LeftCtrl, false),
                "option" or "alt" => (RoamKeys.LeftAlt, false),
                "win" or "windows" or "meta" or "super" => (RoamKeys.LeftGui, false),
                _ => null,
            };
            if (m is { } v && !list.Any(x => x.Item1 == v.Usage)) list.Add(v);
        }
        return list;
    }

    /// <summary>
    /// ⌘Tab on Windows: with ⌘-as-Ctrl, a ⌘ that stands for Ctrl becomes Alt when Tab goes with it, so Windows' app switcher
    /// opens (Ctrl+Tab would only change tabs). Off, ⌘ is already the Windows key (Win+Tab = Task View) and nothing changes.
    /// </summary>
    public static int SwitcherUsage(int usage, bool cmdAsCtrl) => !cmdAsCtrl ? usage : usage switch
    {
        RoamKeys.LeftCtrl => RoamKeys.LeftAlt,
        RoamKeys.RightCtrl => RoamKeys.RightAlt,
        _ => usage,
    };
}

/// <summary>
/// The keys and mouse buttons one phone is holding down on this PC (remoteControl key / mouse_click). Not thread-safe:
/// the caller serialises (DeskDeck does every remoteControl in order on one queue).
/// </summary>
public sealed class RemoteHeldInput
{
    /// <summary>Keys held with no remoteControl at all for this long are let go (§15.5).</summary>
    public static readonly TimeSpan IdleRelease = TimeSpan.FromSeconds(10);

    readonly Action<int, bool> _key;
    readonly Action<RoamButton, bool> _button;
    // macOS keycode → the usage pressed for it (⌘ may have become Alt for the switcher), in press order.
    readonly List<(int Mac, int Usage)> _keys = new();
    readonly List<RoamButton> _buttons = new();

    public RemoteHeldInput(Action<int, bool> key, Action<RoamButton, bool> button)
    {
        _key = key;
        _button = button;
    }

    public bool CmdAsCtrl { get; set; } = true;

    /// <summary>When the last remoteControl arrived (any action).</summary>
    public DateTime LastActivity { get; private set; }

    public bool AnyHeld => _keys.Count > 0 || _buttons.Count > 0;
    public IReadOnlyList<int> HeldUsages => _keys.Select(k => k.Usage).ToList();
    public IReadOnlyList<RoamButton> HeldButtons => _buttons.ToList();

    public void Touch(DateTime now) => LastActivity = now;

    /// <summary>Held things and nothing from the phone for <see cref="IdleRelease"/>: time to let go.</summary>
    public bool IdleExpired(DateTime now) => AnyHeld && now - LastActivity >= IdleRelease;

    /// <summary>remoteControl key {keycode, isDown}.</summary>
    public void Key(int macKeycode, bool down)
    {
        var i = _keys.FindIndex(k => k.Mac == macKeycode);
        if (!down)
        {
            if (i < 0) return; // never pressed (or already let go): nothing to release
            var usage = _keys[i].Usage;
            _keys.RemoveAt(i);
            _key(usage, false);
            return;
        }
        if (i >= 0)
        {
            // Held already: a repeat. Modifiers don't repeat; other keys do (like a held key's auto-repeat).
            if (!RoamKeys.IsModifier(_keys[i].Usage)) _key(_keys[i].Usage, true);
            return;
        }
        if (RemoteKeyMap.Usage(macKeycode, CmdAsCtrl) is not { } u) return;
        if (macKeycode == RemoteKeyMap.MacTab) OpenSwitcher();
        _keys.Add((macKeycode, u));
        _key(u, true);
    }

    /// <summary>
    /// remoteControl keypress {keycode, modifiers}: modifiers down, the key, modifiers up — skipping a modifier the phone is
    /// already holding (it stays down). ⌘+Tab (named or held) is Alt+Tab.
    /// </summary>
    public void Press(int macKeycode, IEnumerable<string?> modifierNames)
    {
        if (RemoteKeyMap.Usage(macKeycode, CmdAsCtrl) is not { } usage) return;
        var mods = RemoteKeyMap.Modifiers(modifierNames, CmdAsCtrl);
        // A ⌘ held on the key strip already stands for the named one (left or right, Ctrl or Alt by now).
        if (_keys.Any(k => RemoteKeyMap.IsCommand(k.Mac))) mods = mods.Where(m => !m.Command).ToList();
        if (macKeycode == RemoteKeyMap.MacTab)
        {
            OpenSwitcher();
            mods = mods.Select(m => m.Command ? (RemoteKeyMap.SwitcherUsage(m.Usage, CmdAsCtrl), true) : m).ToList();
        }
        PressUsage(usage, mods.Select(m => m.Usage));
    }

    /// <summary>A key by HID usage with modifier usages, leaving held modifiers alone.</summary>
    public void PressUsage(int usage, IEnumerable<int> modifiers)
    {
        var held = _keys.Select(k => k.Usage).ToHashSet();
        var down = modifiers.Distinct().Where(m => !held.Contains(m) && m != usage).ToList();
        foreach (var m in down) _key(m, true);
        _key(usage, true);
        _key(usage, false);
        for (var j = down.Count - 1; j >= 0; j--) _key(down[j], false);
    }

    /// <summary>remoteControl mouse_click {button, isDown}: remembered so a drag cut off by a lost link doesn't stay down.</summary>
    public void Button(RoamButton button, bool down)
    {
        if (down)
        {
            if (!_buttons.Contains(button)) _buttons.Add(button);
        }
        else _buttons.Remove(button);
        _button(button, down);
    }

    /// <summary>Lets go of everything held (reverse order); how many keys and buttons were released.</summary>
    public int ReleaseAll()
    {
        var n = _keys.Count + _buttons.Count;
        for (var j = _keys.Count - 1; j >= 0; j--) _key(_keys[j].Usage, false);
        for (var j = _buttons.Count - 1; j >= 0; j--) _button(_buttons[j], false);
        _keys.Clear();
        _buttons.Clear();
        return n;
    }

    /// <summary>Held ⌘ keys standing for Ctrl become Alt (Ctrl up, Alt down) before Tab goes down; they stay Alt until let go.</summary>
    void OpenSwitcher()
    {
        if (!CmdAsCtrl) return;
        for (var j = 0; j < _keys.Count; j++)
        {
            var (mac, usage) = _keys[j];
            if (!RemoteKeyMap.IsCommand(mac)) continue;
            var alt = RemoteKeyMap.SwitcherUsage(usage, true);
            if (alt == usage) continue;
            _key(usage, false);
            _key(alt, true);
            _keys[j] = (mac, alt);
        }
    }
}
