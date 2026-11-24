// H.I.B Roam on Windows, round 2 (docs/SPACE.md §12): the system services around capture and injection. One small
// thread owns a hidden top-level window (message-only windows miss broadcasts) that hears WM_DEVICECHANGE (a mouse
// plugged or unplugged, §12.1) and the session lock / unlock (WTS notifications, §12.5 lock together), and holds the
// keep-awake assertion (SetThreadExecutionState is per thread, so it lives here). Mouse Keys, the window in front
// (full screen? cursor hidden or clipped? elevated?) and LockWorkStation are plain calls from any thread.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace HIB.Core.Roam.Windows;

[SupportedOSPlatform("windows")]
public sealed unsafe partial class WindowsRoamSystem : IRoamSystem
{
    static WindowsRoamSystem? s_current;

    readonly Thread _thread;
    readonly ManualResetEventSlim _ready = new();
    readonly object _cache = new();
    readonly Dictionary<int, (string App, bool? Elevated)> _processes = new();
    readonly int _ownPid = Environment.ProcessId;
    readonly int _ownIntegrity;
    nint _window;
    uint _threadId;
    bool _disposed;

    public WindowsRoamSystem()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        s_current = this;
        _ownIntegrity = IntegrityOf(GetCurrentProcess(), ownHandle: true) ?? 0x2000;
        _thread = new Thread(Run) { IsBackground = true, Name = "H.I.B Roam system" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public event Action? DevicesChanged;
    public event Action<bool>? SessionLockChanged;

    // ---------------- Mouse Keys (§12.1) ----------------

    public bool MousePresent => GetSystemMetrics(19 /* SM_MOUSEPRESENT */) != 0;

    public MouseKeysState? GetMouseKeys()
    {
        var mk = new MOUSEKEYS { cbSize = (uint)sizeof(MOUSEKEYS) };
        if (!SystemParametersInfoW(0x0036 /* SPI_GETMOUSEKEYS */, mk.cbSize, &mk, 0)) return null;
        return new MouseKeysState(mk.dwFlags, mk.iMaxSpeed, mk.iTimeToMaxSpeed);
    }

    public bool SetMouseKeys(MouseKeysState state)
    {
        var mk = new MOUSEKEYS { cbSize = (uint)sizeof(MOUSEKEYS) };
        if (!SystemParametersInfoW(0x0036, mk.cbSize, &mk, 0)) return false; // keeps the fields we don't own (Ctrl speed)
        mk.dwFlags = state.Flags;
        if (state.MaxSpeed != 0) mk.iMaxSpeed = state.MaxSpeed;
        if (state.TimeToMaxSpeed != 0) mk.iTimeToMaxSpeed = state.TimeToMaxSpeed;
        return SystemParametersInfoW(0x0037 /* SPI_SETMOUSEKEYS */, mk.cbSize, &mk, 0x0002 /* SPIF_SENDCHANGE */);
    }

    // ---------------- the window in front (§12.2, §12.4) ----------------

    public RoamForeground? Foreground()
    {
        var old = SetThreadDpiAwarenessContext(-4); // physical pixels for windows, monitors, cursor and clip
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == 0 || hwnd == GetShellWindow() || IsShellClass(hwnd)) return null;
            GetWindowThreadProcessId(hwnd, out var pid);
            var own = (int)pid == _ownPid;
            if (!GetWindowRect(hwnd, out var wr)) return null;
            // A maximized window with a title bar isn't full screen, even when the taskbar hides itself.
            var framed = IsZoomed(hwnd) && (GetWindowLongPtrW(hwnd, -16 /* GWL_STYLE */) & 0x00C00000 /* WS_CAPTION */) == 0x00C00000;
            var monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            var mon = monitor != 0 && GetMonitorInfoW(monitor, &mi) ? Rect(mi.rcMonitor) : default;
            var ci = new CURSORINFO { cbSize = (uint)sizeof(CURSORINFO) };
            // A PC with no mouse hides the cursor for every app: then only a clip says "locked".
            var shown = !GetCursorInfo(&ci) || (ci.flags & 0x1 /* CURSOR_SHOWING */) != 0 || !MousePresent;
            RoamRect? clip = null;
            if (GetClipCursor(out var cr))
            {
                var c = Rect(cr);
                var desk = new RoamRect(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
                if (!c.IsEmpty && (c.W < desk.W || c.H < desk.H)) clip = c;
            }
            var (app, elevated) = own ? ("H.I.B", (bool?)false) : Process((int)pid);
            return new RoamForeground(hwnd, (int)pid, app, own, framed ? default : Rect(wr), mon, shown, clip, elevated);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (old != 0) SetThreadDpiAwarenessContext(old);
        }
    }

    static RoamRect Rect(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    static bool IsShellClass(nint hwnd)
    {
        var buf = stackalloc char[32];
        var n = GetClassNameW(hwnd, buf, 32);
        var name = new string(buf, 0, Math.Max(0, n));
        return name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    /// <summary>The exe name and whether it runs above H.I.B's integrity level (cached per process).</summary>
    (string App, bool? Elevated) Process(int pid)
    {
        lock (_cache)
        {
            if (_processes.TryGetValue(pid, out var known)) return known;
        }
        var app = $"process {pid}";
        bool? elevated = null;
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)pid);
        if (h != 0)
        {
            try
            {
                var buf = stackalloc char[1024];
                var len = 1024u;
                if (QueryFullProcessImageNameW(h, 0, buf, ref len)) app = Path.GetFileName(new string(buf, 0, (int)len));
                if (IntegrityOf(h, ownHandle: false) is { } il) elevated = il > _ownIntegrity;
                else if (Marshal.GetLastPInvokeError() == 5 /* ACCESS_DENIED */) elevated = true; // its token is above us
            }
            finally { CloseHandle(h); }
        }
        lock (_cache)
        {
            if (_processes.Count > 256) _processes.Clear();
            _processes[pid] = (app, elevated);
        }
        return (app, elevated);
    }

    /// <summary>The mandatory integrity level RID of a process (0x2000 medium, 0x3000 high, 0x4000 system), or null.</summary>
    static int? IntegrityOf(nint process, bool ownHandle)
    {
        if (!OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out var token)) return null;
        try
        {
            GetTokenInformation(token, 25 /* TokenIntegrityLevel */, null, 0, out var size);
            if (size == 0 || size > 4096) return null;
            var buf = stackalloc byte[(int)size];
            if (!GetTokenInformation(token, 25, buf, size, out _)) return null;
            var sid = *(nint*)buf; // TOKEN_MANDATORY_LABEL.Label.Sid
            var count = *GetSidSubAuthorityCount(sid);
            if (count == 0) return null;
            return (int)*GetSidSubAuthority(sid, (uint)(count - 1));
        }
        finally { CloseHandle(token); }
    }

    // ---------------- keep awake, lock (§12.5) ----------------

    public void KeepAwake(bool on)
    {
        if (_threadId != 0) PostThreadMessageW(_threadId, CmdAwake, on ? 1 : 0, 0);
    }

    public void LockScreen()
    {
        try { LockWorkStation(); } catch { }
    }

    // ---------------- the thread ----------------

    const uint WM_APP = 0x8000, CmdAwake = WM_APP + 21, WM_QUIT = 0x0012;
    const uint WM_DEVICECHANGE = 0x0219, WM_WTSSESSION_CHANGE = 0x02B1;

    void Run()
    {
        _threadId = GetCurrentThreadId();
        PeekMessageW(out _, 0, 0, 0, 0);
        try
        {
            _window = CreateListener();
            if (_window != 0) WTSRegisterSessionNotification(_window, 0 /* NOTIFY_FOR_THIS_SESSION */);
        }
        catch { _window = 0; }
        _ready.Set();
        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            if (msg.hwnd == 0 && msg.message == CmdAwake)
            {
                // ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED while another computer drives this one.
                SetThreadExecutionState(msg.wParam != 0 ? 0x80000003u : 0x80000000u);
                continue;
            }
            TranslateMessage(in msg);
            DispatchMessageW(in msg);
        }
        SetThreadExecutionState(0x80000000u);
        if (_window != 0)
        {
            try { WTSUnRegisterSessionNotification(_window); } catch { }
            DestroyWindow(_window);
        }
    }

    nint CreateListener()
    {
        fixed (char* name = "HIB.RoamSystem")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = GetModuleHandleW(null),
                lpszClassName = name,
            };
            RegisterClassExW(&wc);
            // Top-level (so broadcasts reach it), never shown.
            return CreateWindowExW(0x00000080 /* TOOLWINDOW */, name, name, 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, 0, 0, wc.hInstance, 0);
        }
    }

    [UnmanagedCallersOnly]
    static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var self = s_current;
        if (self is not null)
        {
            if (msg == WM_DEVICECHANGE)
            {
                // DBT_DEVNODES_CHANGED and friends: never block the window's thread.
                if (self.DevicesChanged is { } d) Task.Run(() => { try { d(); } catch { } });
            }
            else if (msg == WM_WTSSESSION_CHANGE && wParam is 0x7 or 0x8)
            {
                var locked = wParam == 0x7; // WTS_SESSION_LOCK / WTS_SESSION_UNLOCK
                if (self.SessionLockChanged is { } l) Task.Run(() => { try { l(locked); } catch { } });
            }
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0) PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
        if (ReferenceEquals(s_current, this)) s_current = null;
    }

    // ---------------- Win32 ----------------

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEKEYS
    {
        public uint cbSize, dwFlags, iMaxSpeed, iTimeToMaxSpeed, iCtrlSpeed, dwReserved1, dwReserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CURSORINFO
    {
        public uint cbSize, flags;
        public nint hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SystemParametersInfoW(uint action, uint param, void* pv, uint winIni);
    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] private static partial nint GetShellWindow();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsZoomed(nint hwnd);
    [LibraryImport("user32.dll")] private static partial nint GetWindowLongPtrW(nint hwnd, int index);
    [LibraryImport("user32.dll")] private static partial int GetClassNameW(nint hwnd, char* name, int max);
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetWindowRect(nint hwnd, out RECT rect);
    [LibraryImport("user32.dll")] private static partial nint MonitorFromWindow(nint hwnd, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetMonitorInfoW(nint monitor, MONITORINFO* info);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetCursorInfo(CURSORINFO* info);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetClipCursor(out RECT rect);
    [LibraryImport("user32.dll")] private static partial nint SetThreadDpiAwarenessContext(nint context);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool LockWorkStation();
    [LibraryImport("kernel32.dll")] private static partial nint GetCurrentProcess();
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseHandle(nint handle);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool QueryFullProcessImageNameW(nint process, uint flags, char* name, ref uint size);
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool OpenProcessToken(nint process, uint access, out nint token);
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetTokenInformation(nint token, int cls, void* info, uint length, out uint returned);
    [LibraryImport("advapi32.dll")] private static partial byte* GetSidSubAuthorityCount(nint sid);
    [LibraryImport("advapi32.dll")] private static partial uint* GetSidSubAuthority(nint sid, uint index);
    [LibraryImport("kernel32.dll")] private static partial uint SetThreadExecutionState(uint flags);
    [LibraryImport("wtsapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool WTSRegisterSessionNotification(nint hwnd, uint flags);
    [LibraryImport("wtsapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool WTSUnRegisterSessionNotification(nint hwnd);
    [LibraryImport("user32.dll")] private static partial int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PeekMessageW(out MSG msg, nint hwnd, uint min, uint max, uint remove);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool TranslateMessage(in MSG msg);
    [LibraryImport("user32.dll")] private static partial nint DispatchMessageW(in MSG msg);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PostThreadMessageW(uint thread, uint msg, nint wParam, nint lParam);
    [LibraryImport("kernel32.dll")] private static partial uint GetCurrentThreadId();
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandleW(string? name);
    [LibraryImport("user32.dll")] private static partial ushort RegisterClassExW(WNDCLASSEXW* wc);
    [LibraryImport("user32.dll")] private static partial nint CreateWindowExW(uint exStyle, char* cls, char* name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")] private static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
}
