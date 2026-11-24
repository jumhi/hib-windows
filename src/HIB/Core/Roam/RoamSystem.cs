// H.I.B Roam, round 2 (docs/SPACE.md §12): the operating-system services Roam needs beyond capture and injection —
// Mouse Keys (§12.1), the window in front and whether it runs as administrator (§12.2, §12.4), keeping the
// computer awake and locking it (§12.5), and the device / session-lock notifications. Windows implements them
// in Windows/WindowsRoamSystem.cs; everywhere else (the macOS dev box, tests) nothing happens.
using System;

namespace HIB.Core.Roam;

/// <summary>Windows' MOUSEKEYS settings (SystemParametersInfo SPI_GETMOUSEKEYS / SPI_SETMOUSEKEYS).</summary>
public readonly record struct MouseKeysState(uint Flags, uint MaxSpeed, uint TimeToMaxSpeed)
{
    public const uint On = 0x00000001;           // MKF_MOUSEKEYSON
    public const uint Available = 0x00000002;    // MKF_AVAILABLE
    public const uint ReplaceNumbers = 0x00000080; // MKF_REPLACENUMBERS

    /// <summary>Deskflow's trick for a computer with no mouse: Mouse Keys on (and the number pad still types numbers).</summary>
    public MouseKeysState Forced() => this with { Flags = Available | On | ReplaceNumbers };

    public bool IsOn => (Flags & On) != 0;
}

/// <summary>
/// The window in front, in physical pixels: its rectangle, the rectangle of the monitor it is on, the cursor (shown or
/// not) and the cursor clip (null = not clipped). <see cref="Own"/>: one of H.I.B's windows. <see cref="Elevated"/>:
/// its process runs at a higher integrity level than H.I.B (Windows' UIPI drops our input there); null = unknown.
/// </summary>
public sealed record RoamForeground(nint Window, int ProcessId, string App, bool Own, RoamRect WindowRect, RoamRect Monitor,
    bool CursorShown = true, RoamRect? Clip = null, bool? Elevated = null);

public interface IRoamSystem : IDisposable
{
    /// <summary>Windows: GetSystemMetrics(SM_MOUSEPRESENT) — true while Mouse Keys is on too.</summary>
    bool MousePresent { get; }
    MouseKeysState? GetMouseKeys();
    bool SetMouseKeys(MouseKeysState state);

    /// <summary>The window in front now (null when there is none we can read, e.g. the lock screen).</summary>
    RoamForeground? Foreground();

    /// <summary>Holds (or lets go of) an idle-sleep and display-sleep assertion.</summary>
    void KeepAwake(bool on);

    /// <summary>Locks this computer's screen (LockWorkStation).</summary>
    void LockScreen();

    /// <summary>A mouse (or any device) came or went (WM_DEVICECHANGE).</summary>
    event Action? DevicesChanged;

    /// <summary>This computer's session was locked (true) or unlocked (false).</summary>
    event Action<bool>? SessionLockChanged;
}

/// <summary>Nothing (non-Windows builds and tests that don't need it).</summary>
public sealed class NullRoamSystem : IRoamSystem
{
    public bool MousePresent => true;
    public MouseKeysState? GetMouseKeys() => null;
    public bool SetMouseKeys(MouseKeysState state) => false;
    public RoamForeground? Foreground() => null;
    public void KeepAwake(bool on) { }
    public void LockScreen() { }
    public event Action? DevicesChanged { add { } remove { } }
    public event Action<bool>? SessionLockChanged { add { } remove { } }
    public void Dispose() { }
}

public static class RoamSystems
{
    /// <summary>Windows: the real thing (HIB_ROAM=0 turns Roam off). Elsewhere: nothing.</summary>
    public static IRoamSystem CreateDefault()
    {
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("HIB_ROAM") is not ("0" or "false"))
        {
            try { return new Windows.WindowsRoamSystem(); }
            catch { /* fall through */ }
        }
        return new NullRoamSystem();
    }
}
