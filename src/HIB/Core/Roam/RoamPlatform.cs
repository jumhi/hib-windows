// H.I.B Roam: the seam between the engine and the operating system's input. Windows captures with
// low-level hooks and injects with SendInput (Windows/WindowsRoamPlatform.cs); everywhere else (the
// macOS dev box, tests) nothing is captured or injected.
using System;

namespace HIB.Core.Roam;

/// <summary>What the platform does with local input.</summary>
public enum RoamInputMode
{
    /// <summary>No hooks at all (Roam not ready).</summary>
    Off,
    /// <summary>Watch the physical pointer (edges, buttons held); nothing is swallowed.</summary>
    Watch,
    /// <summary>This computer controls the other one: physical input is swallowed and reported; the cursor is parked and hidden.</summary>
    Capture,
    /// <summary>The other computer controls this one: report any physical (not injected) input.</summary>
    Controlled,
}

public enum RoamInputKind { Pointer, Motion, Button, Wheel, Key, LocalPhysical, Hotkey, UnmappedKey }

/// <summary>
/// One input event from the platform. Pixels are this computer's physical pixels.
///  - Pointer (Watch): the physical pointer's position as the hook saw it (it may lie past the outer edge while pushing).
///  - Motion (Capture): relative motion in pixels.
///  - Button / Wheel / Key (Capture; Button also in Watch to track what is held).
///  - LocalPhysical (Controlled): someone touched this computer's own mouse or keyboard.
///  - Hotkey (Watch, Capture; SPACE.md §12.5): a Roam hotkey on this computer's own keyboard (<see cref="Usage"/> = a <see cref="RoamHotkey"/>).
///  - UnmappedKey (Capture; §12.2): a key with no HID usage — Dx = its virtual key, Dy = its scancode (counted, never sent).
/// </summary>
public readonly record struct RoamInput(RoamInputKind Kind, double X = 0, double Y = 0, RoamButton Button = RoamButton.Left,
    bool Down = false, int Dx = 0, int Dy = 0, int Usage = 0, bool AnyButtonDown = false)
{
    public static RoamInput Pointer(double x, double y, bool anyButtonDown) => new(RoamInputKind.Pointer, x, y, AnyButtonDown: anyButtonDown);
    public static RoamInput Motion(double dx, double dy) => new(RoamInputKind.Motion, dx, dy);
    public static RoamInput Press(RoamButton b, bool down) => new(RoamInputKind.Button, Button: b, Down: down);
    public static RoamInput Scroll(int dx, int dy) => new(RoamInputKind.Wheel, Dx: dx, Dy: dy);
    public static RoamInput KeyEvent(int usage, bool down) => new(RoamInputKind.Key, Usage: usage, Down: down);
    public static RoamInput Physical() => new(RoamInputKind.LocalPhysical);
    public static RoamInput Hot(RoamHotkey hotkey) => new(RoamInputKind.Hotkey, Usage: (int)hotkey);
    public static RoamInput Unmapped(int vk, int scan, bool extended) => new(RoamInputKind.UnmappedKey, Dx: vk, Dy: scan, Down: extended);
    public RoamHotkey Hotkey => Kind == RoamInputKind.Hotkey ? (RoamHotkey)Usage : RoamHotkey.None;
}

public interface IRoamPlatform : IDisposable
{
    /// <summary>True when this computer can capture and inject input.</summary>
    bool IsAvailable { get; }
    /// <summary>Why not, when <see cref="IsAvailable"/> is false.</summary>
    string UnavailableReason { get; }
    /// <summary>"windows" or "mac" (what ctlHello says).</summary>
    string Platform { get; }

    RoamInputMode Mode { get; }
    void SetMode(RoamInputMode mode);

    /// <summary>Capture starts: park the (hidden) cursor at this pixel. <paramref name="hide"/> false keeps it visible (a parked file drag).</summary>
    void Park(double px, double py, bool hide);
    /// <summary>Capture ends: show the cursor again at this pixel.</summary>
    void Unpark(double px, double py);

    void InjectMove(double px, double py);
    /// <summary>SPACE.md §12.4 ctlRel: relative motion in pixels, without MOUSEEVENTF_ABSOLUTE (games see raw motion).</summary>
    void InjectRelative(int dx, int dy);
    void InjectButton(RoamButton button, bool down);
    void InjectWheel(int dx, int dy);
    /// <summary>Types a key (HID usage). False when it has no mapping here or the system refused it (SendInput returned 0).</summary>
    bool InjectKey(int usage, bool down);
    /// <summary>Releases the local OLE drag parked on the drop catcher (an injected left-button up at the park point).</summary>
    void FinishParkedDrag(bool cancel);

    /// <summary>This computer's monitors (absolute injection spans their union).</summary>
    void SetDisplays(System.Collections.Generic.IReadOnlyList<RoamDisplay> displays) { }

    /// <summary>Raised in order on one background thread.</summary>
    event Action<RoamInput>? Input;

    /// <summary>Where the platform writes what went wrong (SendInput failures; rate-limited, never key contents).</summary>
    Action<string>? Log { get => null; set { } }
}

/// <summary>No capture, no injection (non-Windows builds and tests that don't need input).</summary>
public sealed class NullRoamPlatform : IRoamPlatform
{
    public bool IsAvailable => false;
    public string UnavailableReason => "Mouse & keyboard sharing needs Windows here";
    public string Platform => "windows";
    public RoamInputMode Mode { get; private set; }
    public void SetMode(RoamInputMode mode) => Mode = mode;
    public void Park(double px, double py, bool hide) { }
    public void Unpark(double px, double py) { }
    public void InjectMove(double px, double py) { }
    public void InjectRelative(int dx, int dy) { }
    public void InjectButton(RoamButton button, bool down) { }
    public void InjectWheel(int dx, int dy) { }
    public bool InjectKey(int usage, bool down) => false;
    public void FinishParkedDrag(bool cancel) { }
    public event Action<RoamInput>? Input { add { } remove { } }
    public void Dispose() { }
}
