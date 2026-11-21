// H.I.B Roam on Windows (docs/SPACE.md §9.1): the mechanism Deskflow uses on its Windows server and client,
// written for H.I.B. A dedicated thread with its own message loop owns WH_MOUSE_LL / WH_KEYBOARD_LL hooks and
// a 1×1 "hider" window. Hook callbacks only decide swallow-or-pass and queue an event; the engine runs
// elsewhere. While capturing, physical mouse moves are swallowed (so the cursor never budges from where it
// is parked, hidden under the hider) and the hook's would-be position minus the park point is the relative
// motion. Injection is SendInput with scancodes (E0 → KEYEVENTF_EXTENDEDKEY) and our dwExtraInfo marker, so
// our own events are never mistaken for the user's. No admin needed; Windows doesn't let a normal process
// drive elevated windows or the secure desktop (lock screen, UAC).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace HIB.Core.Roam.Windows;

[SupportedOSPlatform("windows")]
public sealed unsafe partial class WindowsRoamPlatform : IRoamPlatform
{
    /// <summary>Our dwExtraInfo on every injected event ("HIBR").</summary>
    public const uint Marker = 0x48494252;

    /// <summary>Physical motion while controlled below this many pixels (desk bumps) doesn't take the cursor back.</summary>
    const int PhysicalMotionThreshold = 6;

    static WindowsRoamPlatform? s_current;

    readonly Channel<RoamInput> _events = Channel.CreateUnbounded<RoamInput>(new UnboundedChannelOptions { SingleReader = true });
    readonly Thread _thread;
    readonly ManualResetEventSlim _ready = new();
    uint _threadId;
    nint _mouseHook, _keyHook, _hider;
    volatile int _mode;
    bool _disposed;

    // Hook-thread state (only touched on _thread).
    int _curX, _curY;
    bool _hidden;
    bool _captureInit;
    int _physMotion;
    readonly bool[] _buttons = new bool[5];
    readonly bool[] _buttonsBefore = new bool[5];
    readonly HashSet<uint> _keysDown = new();
    readonly HashSet<uint> _keepLocal = new();

    // Injection: the virtual desktop in pixels (set by the engine from Avalonia's screens).
    RoamRect _desktop;

    public WindowsRoamPlatform()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        s_current = this;
        _thread = new Thread(Run) { IsBackground = true, Name = "H.I.B Roam input" };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Roam input thread didn't start");
        _ = Task.Run(() => RoamInputPump.RunAsync(_events.Reader, Raise));
    }

    public bool IsAvailable => _threadId != 0 && !_disposed;
    public string UnavailableReason => "Mouse & keyboard sharing couldn't start on this PC";
    public string Platform => "windows";
    public RoamInputMode Mode => (RoamInputMode)_mode;
    public event Action<RoamInput>? Input;

    /// <summary>The virtual desktop in pixels (the monitors' union), for absolute SendInput moves.</summary>
    public void SetDisplays(IReadOnlyList<RoamDisplay> displays) => _desktop = RoamGeometry.Bounds(System.Linq.Enumerable.Select(displays, d => d.Pixels));

    public void SetMode(RoamInputMode mode)
    {
        _mode = (int)mode;
        Post(CmdHooks, mode == RoamInputMode.Off ? 0 : 1, 0);
    }

    public void Park(double px, double py, bool hide) => Post(hide ? CmdParkHidden : CmdParkVisible, (int)Math.Round(px), (int)Math.Round(py));

    public void Unpark(double px, double py) => Post(CmdUnpark, (int)Math.Round(px), (int)Math.Round(py));

    // ---------------- the input thread ----------------

    const uint WM_APP = 0x8000;
    const uint CmdHooks = WM_APP + 1, CmdParkHidden = WM_APP + 2, CmdParkVisible = WM_APP + 3, CmdUnpark = WM_APP + 4;
    const uint WM_QUIT = 0x0012, WM_SETCURSOR = 0x0020;

    void Post(uint cmd, int a, int b)
    {
        if (_threadId != 0) PostThreadMessageW(_threadId, cmd, a, b);
    }

    void Run()
    {
        try { SetThreadDpiAwarenessContext(-4); } catch { } // per-monitor v2: hook points and SetCursorPos in physical pixels
        _threadId = GetCurrentThreadId();
        PeekMessageW(out _, 0, 0, 0, 0); // create the message queue
        try { _hider = CreateHider(); } catch { _hider = 0; }
        _ready.Set();
        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            if (msg.hwnd == 0 && msg.message is >= CmdHooks and <= CmdUnpark)
            {
                try { Command(msg.message, (int)msg.wParam, (int)msg.lParam); } catch { }
                continue;
            }
            TranslateMessage(in msg);
            DispatchMessageW(in msg);
        }
        Unhook();
        if (_hidden) ShowCursorUntil(true);
        if (_hider != 0) DestroyWindow(_hider);
    }

    void Command(uint cmd, int a, int b)
    {
        switch (cmd)
        {
            case CmdHooks:
                if (a != 0) Hook();
                else Unhook();
                break;
            case CmdParkHidden:
            case CmdParkVisible:
                _curX = a;
                _curY = b;
                if (cmd == CmdParkHidden && _hider != 0)
                {
                    // Deskflow's hider: a tiny topmost window of ours under the parked cursor, whose cursor is none.
                    SetWindowPos(_hider, -1 /* HWND_TOPMOST */, a, b, 1, 1, 0x0010 /* NOACTIVATE */ | 0x0040 /* SHOWWINDOW */);
                    SetCursorPos(a, b);
                    SetCursor(0);
                    if (!_hidden) ShowCursorUntil(false);
                    _hidden = true;
                }
                else SetCursorPos(a, b);
                break;
            case CmdUnpark:
                if (_hidden)
                {
                    if (_hider != 0) ShowWindow(_hider, 0 /* SW_HIDE */);
                    ShowCursorUntil(true);
                    _hidden = false;
                }
                SetCursorPos(a, b);
                _curX = a;
                _curY = b;
                break;
        }
    }

    /// <summary>ShowCursor keeps a counter: call it until it is where we want it (Deskflow's setCursorVisibility).</summary>
    static void ShowCursorUntil(bool visible)
    {
        for (var i = 0; i < 10; i++)
        {
            var count = ShowCursor(visible ? 1 : 0);
            if (visible ? count >= 0 : count < 0) return;
        }
    }

    void Hook()
    {
        var module = GetModuleHandleW(null);
        if (_mouseHook == 0) _mouseHook = SetWindowsHookExW(14 /* WH_MOUSE_LL */, &MouseProc, module, 0);
        if (_keyHook == 0) _keyHook = SetWindowsHookExW(13 /* WH_KEYBOARD_LL */, &KeyboardProc, module, 0);
        if (GetCursorPos(out var p))
        {
            _curX = p.X;
            _curY = p.Y;
        }
    }

    void Unhook()
    {
        if (_mouseHook != 0) UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != 0) UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = 0;
        _keysDown.Clear();
        Array.Clear(_buttons);
    }

    nint CreateHider()
    {
        fixed (char* name = "HIB.RoamHider")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &HiderProc,
                hInstance = GetModuleHandleW(null),
                hCursor = 0, // no cursor over it
                lpszClassName = name,
            };
            RegisterClassExW(&wc);
            // Topmost tool window that never activates; not WS_EX_TRANSPARENT, so it owns the cursor above it.
            return CreateWindowExW(0x00000008 /* TOPMOST */ | 0x00000080 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */,
                name, name, 0x80000000 /* WS_POPUP */, 0, 0, 1, 1, 0, 0, wc.hInstance, 0);
        }
    }

    [UnmanagedCallersOnly]
    static nint HiderProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_SETCURSOR)
        {
            SetCursor(0);
            return 1;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // ---------------- hooks (fast: decide, queue, return) ----------------

    const nint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205,
        WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MOUSEWHEEL = 0x020A, WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C, WM_MOUSEHWHEEL = 0x020E;
    const nint WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;

    [UnmanagedCallersOnly]
    static nint MouseProc(int code, nint wParam, nint lParam)
    {
        var self = s_current;
        if (code >= 0 && self is not null)
        {
            try
            {
                if (self.OnMouse(wParam, (MSLLHOOKSTRUCT*)lParam)) return 1;
            }
            catch { /* never let an exception cross the hook */ }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    static nint KeyboardProc(int code, nint wParam, nint lParam)
    {
        var self = s_current;
        if (code >= 0 && self is not null)
        {
            try
            {
                if (self.OnKey(wParam, (KBDLLHOOKSTRUCT*)lParam)) return 1;
            }
            catch { }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    void Emit(RoamInput e) => _events.Writer.TryWrite(e);

    bool AnyButton => _buttons[0] || _buttons[1] || _buttons[2] || _buttons[3] || _buttons[4];

    void EnterCaptureIfNeeded(RoamInputMode mode)
    {
        if (mode != RoamInputMode.Capture)
        {
            _captureInit = false;
            return;
        }
        if (_captureInit) return;
        _captureInit = true;
        // Keys and buttons already down when we crossed stay this computer's (their releases pass through here).
        _keepLocal.Clear();
        foreach (var k in _keysDown) _keepLocal.Add(k);
        Array.Copy(_buttons, _buttonsBefore, _buttons.Length);
    }

    bool OnMouse(nint msg, MSLLHOOKSTRUCT* info)
    {
        // Anything injected (ours or another program's) is never captured nor taken as the user's hand.
        if ((info->flags & 0x3) != 0 || info->dwExtraInfo == Marker) return false;
        var mode = (RoamInputMode)_mode;
        EnterCaptureIfNeeded(mode);
        int x = info->pt.X, y = info->pt.Y;

        if (msg == WM_MOUSEMOVE)
        {
            switch (mode)
            {
                case RoamInputMode.Capture:
                {
                    // Swallowed, so the cursor stays parked: the would-be position minus the park point is the motion.
                    var dx = x - _curX;
                    var dy = y - _curY;
                    if (dx != 0 || dy != 0) Emit(RoamInput.Motion(dx, dy));
                    return true;
                }
                case RoamInputMode.Watch:
                    Emit(RoamInput.Pointer(x, y, AnyButton));
                    _curX = x;
                    _curY = y;
                    return false;
                case RoamInputMode.Controlled:
                    _physMotion += Math.Abs(x - _curX) + Math.Abs(y - _curY);
                    _curX = x;
                    _curY = y;
                    if (_physMotion >= PhysicalMotionThreshold)
                    {
                        _physMotion = 0;
                        Emit(RoamInput.Physical());
                    }
                    return false;
                default:
                    return false;
            }
        }

        if (msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL)
        {
            if (mode == RoamInputMode.Capture)
            {
                var (dx, dy) = RoamWheel.FromWindows(RoamWheel.HighWord(info->mouseData), msg == WM_MOUSEHWHEEL);
                Emit(RoamInput.Scroll(dx, dy));
                return true;
            }
            if (mode == RoamInputMode.Controlled) Emit(RoamInput.Physical());
            return false;
        }

        RoamButton b;
        bool down;
        switch (msg)
        {
            case WM_LBUTTONDOWN: b = RoamButton.Left; down = true; break;
            case WM_LBUTTONUP: b = RoamButton.Left; down = false; break;
            case WM_RBUTTONDOWN: b = RoamButton.Right; down = true; break;
            case WM_RBUTTONUP: b = RoamButton.Right; down = false; break;
            case WM_MBUTTONDOWN: b = RoamButton.Middle; down = true; break;
            case WM_MBUTTONUP: b = RoamButton.Middle; down = false; break;
            case WM_XBUTTONDOWN:
            case WM_XBUTTONUP:
                b = (info->mouseData >> 16) == 2 ? RoamButton.Forward : RoamButton.Back;
                down = msg == WM_XBUTTONDOWN;
                break;
            default:
                return false;
        }
        var i = (int)b;
        _buttons[i] = down;
        switch (mode)
        {
            case RoamInputMode.Capture:
                if (!down && _buttonsBefore[i])
                {
                    // Held since before we crossed (a file drag): its release belongs here — it ends the parked drag
                    // on the drop catcher — and the engine learns the drop happened.
                    _buttonsBefore[i] = false;
                    Emit(RoamInput.Press(b, false));
                    return false;
                }
                Emit(RoamInput.Press(b, down));
                return true;
            case RoamInputMode.Watch:
                Emit(RoamInput.Press(b, down));
                return false;
            case RoamInputMode.Controlled:
                Emit(RoamInput.Physical());
                return false;
            default:
                return false;
        }
    }

    bool OnKey(nint msg, KBDLLHOOKSTRUCT* info)
    {
        if ((info->flags & 0x12) != 0 || info->dwExtraInfo == Marker) return false; // LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED
        var mode = (RoamInputMode)_mode;
        EnterCaptureIfNeeded(mode);
        var down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        var vk = info->vkCode;
        if (mode == RoamInputMode.Capture)
        {
            if (_keepLocal.Contains(vk))
            {
                // Down before we crossed: repeats are dropped, the release passes so this computer's state is right.
                if (down) return true;
                _keepLocal.Remove(vk);
                _keysDown.Remove(vk);
                return false;
            }
            if (down) _keysDown.Add(vk);
            else _keysDown.Remove(vk);
            var scan = (int)info->scanCode;
            if (scan == 0) scan = (int)MapVirtualKeyW(vk, 4 /* MAPVK_VK_TO_VSC_EX */);
            var usage = RoamKeys.FromWindows(scan & 0x2FF, (info->flags & 0x1) != 0 || (scan & 0xE000) == 0xE000, (int)vk);
            if (usage is { } u) Emit(RoamInput.KeyEvent(u, down));
            return true;
        }
        if (down) _keysDown.Add(vk);
        else _keysDown.Remove(vk);
        if (mode == RoamInputMode.Controlled) Emit(RoamInput.Physical());
        return false;
    }

    void Raise(RoamInput e)
    {
        try { Input?.Invoke(e); } catch { }
    }

    // ---------------- injection (any thread) ----------------

    public void InjectMove(double px, double py)
    {
        var d = _desktop;
        if (d.IsEmpty)
        {
            d = new RoamRect(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
            if (d.IsEmpty) return;
        }
        var nx = (int)Math.Round((px - d.X) * 65535.0 / Math.Max(1, d.W - 1));
        var ny = (int)Math.Round((py - d.Y) * 65535.0 / Math.Max(1, d.H - 1));
        SendMouse(0x0001 /* MOVE */ | 0x8000 /* ABSOLUTE */ | 0x4000 /* VIRTUALDESK */, nx, ny, 0);
    }

    public void InjectButton(RoamButton button, bool down)
    {
        // Windows swaps left/right for a left-handed mouse; pre-swap so the controller's handedness wins (as Deskflow does).
        if (GetSystemMetrics(23 /* SM_SWAPBUTTON */) != 0)
            button = button switch { RoamButton.Left => RoamButton.Right, RoamButton.Right => RoamButton.Left, _ => button };
        var (flags, data) = button switch
        {
            RoamButton.Left => (down ? 0x0002u : 0x0004u, 0u),
            RoamButton.Right => (down ? 0x0008u : 0x0010u, 0u),
            RoamButton.Middle => (down ? 0x0020u : 0x0040u, 0u),
            RoamButton.Back => (down ? 0x0080u : 0x0100u, 1u),
            _ => (down ? 0x0080u : 0x0100u, 2u),
        };
        SendMouse(flags, 0, 0, data);
    }

    public void InjectWheel(int dx, int dy)
    {
        if (dy != 0) SendMouse(0x0800 /* WHEEL */, 0, 0, unchecked((uint)dy));
        if (dx != 0) SendMouse(0x1000 /* HWHEEL */, 0, 0, unchecked((uint)dx));
    }

    public void InjectKey(int usage, bool down)
    {
        if (RoamKeys.ToWindows(usage) is not { } k) return;
        uint flags = down ? 0u : 0x0002u; // KEYEVENTF_KEYUP
        if (k.Extended) flags |= 0x0001;  // KEYEVENTF_EXTENDEDKEY
        if (k.ByVk) SendKey(k.Vk, k.Scan, flags);
        else SendKey(0, k.Scan, flags | 0x0008 /* KEYEVENTF_SCANCODE: the target's own layout decides the character */);
    }

    public void FinishParkedDrag(bool cancel)
    {
        // A drop needs nothing: the physical button-up passed through onto the drop catcher. A cancel is Escape here.
        if (!cancel) return;
        SendKey(0, 0x01, 0x0008);
        SendKey(0, 0x01, 0x0008 | 0x0002);
    }

    static void SendMouse(uint flags, int dx, int dy, uint data)
    {
        var input = new INPUT { type = 0 };
        input.u.mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags, dwExtraInfo = Marker };
        SendInput(1, &input, sizeof(INPUT));
    }

    static void SendKey(ushort vk, ushort scan, uint flags)
    {
        var input = new INPUT { type = 1 };
        input.u.ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = Marker };
        SendInput(1, &input, sizeof(INPUT));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mode = (int)RoamInputMode.Off;
        if (_threadId != 0) PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
        _events.Writer.TryComplete();
        if (ReferenceEquals(s_current, this)) s_current = null;
    }

    // ---------------- Win32 ----------------

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

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
    struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public INPUTUNION u;
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

    [LibraryImport("user32.dll")] private static partial nint SetWindowsHookExW(int idHook, delegate* unmanaged<int, nint, nint, nint> lpfn, nint hmod, uint threadId);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool UnhookWindowsHookEx(nint hhk);
    [LibraryImport("user32.dll")] private static partial nint CallNextHookEx(nint hhk, int code, nint wParam, nint lParam);
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
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ShowWindow(nint hwnd, int cmd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetCursorPos(int x, int y);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetCursorPos(out POINT p);
    [LibraryImport("user32.dll")] private static partial nint SetCursor(nint cursor);
    [LibraryImport("user32.dll")] private static partial int ShowCursor(int show);
    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll")] private static partial uint SendInput(uint count, INPUT* inputs, int size);
    [LibraryImport("user32.dll")] private static partial uint MapVirtualKeyW(uint code, uint mapType);
    [LibraryImport("user32.dll")] private static partial nint SetThreadDpiAwarenessContext(nint context);
}

/// <summary>Hands the hook thread's queued events to the engine, in order, on one background task.</summary>
static class RoamInputPump
{
    public static async Task RunAsync(ChannelReader<RoamInput> reader, Action<RoamInput> raise)
    {
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var e)) raise(e);
        }
    }
}
