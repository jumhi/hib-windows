// H.I.B Space (docs/SPACE.md §3.1): is somebody using this computer right now?
using System;
using System.Runtime.InteropServices;

namespace HIB.Core.Space;

/// <summary>One sample of keyboard/mouse activity.</summary>
public readonly record struct ActivitySample(bool InUse, int IdleSeconds);

public static partial class DesktopActivity
{
    /// <summary>Input in the last 90 s (and the screen not locked) counts as "in use".</summary>
    public const int InUseWindowSeconds = 90;

    /// <summary>
    /// Windows: <c>GetLastInputInfo</c> (+ the input desktop must be openable, i.e. not locked).
    /// Other systems: always <c>inUse:false, idleSeconds:-1</c> as the spec asks.
    /// </summary>
    public static ActivitySample Sample()
    {
        if (!OperatingSystem.IsWindows()) return new ActivitySample(false, -1);
        try
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return new ActivitySample(false, -1);
            // Both are 32-bit millisecond tick counts; unsigned subtraction survives the 49.7-day wrap.
            var idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
            var idle = (int)Math.Min(int.MaxValue, idleMs / 1000);
            var inUse = idle < InUseWindowSeconds && !IsLocked();
            return new ActivitySample(inUse, idle);
        }
        catch
        {
            return new ActivitySample(false, -1);
        }
    }

    /// <summary>The secure desktop (lock screen / UAC) can't be opened by a normal process.</summary>
    static bool IsLocked()
    {
        const uint DESKTOP_SWITCHDESKTOP = 0x0100;
        // While locked the input desktop is Winlogon's, which a normal process can't open.
        var h = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
        if (h == IntPtr.Zero) return true;
        CloseDesktop(h);
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(IntPtr hDesktop);
}
