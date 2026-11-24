// H.I.B Roam (docs/SPACE.md §12.5): hotkeys read on the controller's own keyboard. On a PC keyboard: Scroll Lock or
// Ctrl+Alt+Shift+L locks the mouse to the computer it is on (toggles); Ctrl+Alt+Shift+arrows jump to the computer on
// that side. The key that fires a hotkey (L, an arrow, Scroll Lock) is eaten with its repeats and its release, so the
// app in front never sees it; the modifiers pass as usual.
using System.Collections.Generic;

namespace HIB.Core.Roam;

public enum RoamHotkey { None, Lock, JumpLeft, JumpRight, JumpUp, JumpDown }

public readonly record struct RoamHotkeyResult(RoamHotkey Fire, bool Swallow)
{
    public static readonly RoamHotkeyResult Pass = new(RoamHotkey.None, false);
}

public sealed class RoamHotkeyDetector
{
    public const int L = 0x0F, ScrollLock = 0x47, Right = 0x4F, Left = 0x50, Down = 0x51, Up = 0x52;

    readonly HashSet<int> _down = new();
    readonly HashSet<int> _eaten = new();

    public static RoamSide? JumpSide(RoamHotkey k) => k switch
    {
        RoamHotkey.JumpLeft => RoamSide.Left,
        RoamHotkey.JumpRight => RoamSide.Right,
        RoamHotkey.JumpUp => RoamSide.Above,
        RoamHotkey.JumpDown => RoamSide.Below,
        _ => null,
    };

    bool Held(int left, int right) => _down.Contains(left) || _down.Contains(right);

    /// <summary>Ctrl, Alt and Shift held (either side), and no Windows / ⌘ key.</summary>
    bool Chord => Held(RoamKeys.LeftCtrl, RoamKeys.RightCtrl) && Held(RoamKeys.LeftAlt, RoamKeys.RightAlt)
                  && Held(RoamKeys.LeftShift, RoamKeys.RightShift) && !Held(RoamKeys.LeftGui, RoamKeys.RightGui);

    /// <summary>One physical key event (HID usage).</summary>
    public RoamHotkeyResult Feed(int usage, bool down)
    {
        if (!down)
        {
            _down.Remove(usage);
            return _eaten.Remove(usage) ? new RoamHotkeyResult(RoamHotkey.None, true) : RoamHotkeyResult.Pass;
        }
        var repeat = !_down.Add(usage);
        if (_eaten.Contains(usage)) return new RoamHotkeyResult(RoamHotkey.None, true); // its auto-repeat
        if (repeat) return RoamHotkeyResult.Pass;
        var fire = usage switch
        {
            ScrollLock => RoamHotkey.Lock,
            L when Chord => RoamHotkey.Lock,
            Left when Chord => RoamHotkey.JumpLeft,
            Right when Chord => RoamHotkey.JumpRight,
            Up when Chord => RoamHotkey.JumpUp,
            Down when Chord => RoamHotkey.JumpDown,
            _ => RoamHotkey.None,
        };
        if (fire == RoamHotkey.None) return RoamHotkeyResult.Pass;
        _eaten.Add(usage);
        return new RoamHotkeyResult(fire, true);
    }

    public void Reset()
    {
        _down.Clear();
        _eaten.Clear();
    }
}
