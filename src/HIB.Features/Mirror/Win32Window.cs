using System.Runtime.InteropServices;

namespace HIB.Features.Mirror;

/// <summary>
/// Windows window plumbing for the scrcpy window: foreground rights, bringing it to the front, keeping it
/// on a monitor that exists. Every call is a no-op (or empty) off Windows and never throws.
/// </summary>
public static class Win32Window
{
    /// <summary>
    /// Lets any process take the foreground (ASFW_ANY). Call while H.I.B is the foreground app (right after
    /// the user's click), so scrcpy's window — which only appears seconds later — may come to the front.
    /// </summary>
    public static bool AllowAnyForeground()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return Native.AllowSetForegroundWindow(Native.ASFW_ANY); } catch { return false; }
    }

    /// <summary>The work areas of the monitors attached now (empty off Windows).</summary>
    public static IReadOnlyList<ScreenArea> MonitorWorkAreas()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var list = new List<ScreenArea>();
        try
        {
            Native.MonitorEnumProc proc = (IntPtr hMonitor, IntPtr hdc, ref Native.RECT r, IntPtr data) =>
            {
                var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
                if (Native.GetMonitorInfo(hMonitor, ref info))
                    list.Add(new ScreenArea(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom));
                return true;
            };
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
            GC.KeepAlive(proc);
        }
        catch { }
        return list;
    }

    /// <summary>
    /// Brings <paramref name="hwnd"/> in front of every normal window: restore if minimized, move it onto a
    /// monitor if it is off-screen, raise it (topmost toggle, which needs no foreground rights) and try to
    /// make it the foreground window. Returns true when it ended up as the foreground window.
    /// </summary>
    public static bool BringToFront(IntPtr hwnd, bool keepTopmost)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return false;
        try
        {
            if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
            EnsureOnScreen(hwnd);
            const uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE;
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
            if (!keepTopmost) Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
            Native.BringWindowToTop(hwnd);
            if (Native.SetForegroundWindow(hwnd)) return true;

            // Foreground lock: borrow the current foreground thread's input state for one call.
            var fg = Native.GetForegroundWindow();
            var fgThread = fg == IntPtr.Zero ? 0u : Native.GetWindowThreadProcessId(fg, out _);
            var me = Native.GetCurrentThreadId();
            if (fgThread == 0 || fgThread == me) return false;
            var attached = Native.AttachThreadInput(me, fgThread, true);
            try { return Native.SetForegroundWindow(hwnd); }
            finally { if (attached) Native.AttachThreadInput(me, fgThread, false); }
        }
        catch { return false; }
    }

    /// <summary>Moves the window onto the nearest monitor's work area when no monitor shows it.</summary>
    public static void EnsureOnScreen(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return;
        try
        {
            if (!Native.GetWindowRect(hwnd, out var r)) return;
            // Visible enough when a monitor holds the title-bar strip.
            var strip = new Native.RECT { Left = r.Left + 40, Top = r.Top, Right = Math.Max(r.Left + 41, r.Right - 40), Bottom = r.Top + 30 };
            if (Native.MonitorFromRect(ref strip, Native.MONITOR_DEFAULTTONULL) != IntPtr.Zero) return;
            var mon = Native.MonitorFromRect(ref r, Native.MONITOR_DEFAULTTONEAREST);
            var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
            if (mon == IntPtr.Zero || !Native.GetMonitorInfo(mon, ref info)) return;
            var w = r.Right - r.Left;
            var h = r.Bottom - r.Top;
            var area = info.rcWork;
            var x = area.Left + Math.Max(0, (area.Right - area.Left - w) / 2);
            var y = area.Top + Math.Max(0, (area.Bottom - area.Top - h) / 2);
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        catch { }
    }

    // Only reached behind OperatingSystem.IsWindows().
    private static class Native
    {
        public const int ASFW_ANY = -1;
        public const int SW_RESTORE = 9;
        public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        public const uint MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTONEAREST = 2;
        public static readonly IntPtr HWND_TOPMOST = new(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new(-2);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int dwProcessId);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    }
}
