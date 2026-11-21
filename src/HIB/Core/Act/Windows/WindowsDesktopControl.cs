// H.I.B Point to act on Windows (docs/SPACE.md §11.3): the Mac's Desk Deck / Remote / media actions done the
// Windows way — LockWorkStation, Task View (Win+Tab), show desktop (Win+D), Win+PrintScreen, media and
// volume keys through SendInput (with Roam's marker, so Roam never takes them for the user's own hands),
// the volume and the microphone through Core Audio (IAudioEndpointVolume, called through its vtable),
// brightness over DDC/CI (dxva2) or, for a laptop panel, WMI. No admin needed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HIB.Core.Roam;
using HIB.Core.Roam.Windows;

namespace HIB.Core.Act.Windows;

[SupportedOSPlatform("windows")]
public sealed unsafe partial class WindowsDesktopControl : IDesktopControl
{
    const ushort VK_TAB = 0x09, VK_RETURN = 0x0D, VK_LWIN = 0x5B, VK_SNAPSHOT = 0x2C, VK_D = 0x44;
    const ushort VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;
    const ushort VK_MEDIA_NEXT_TRACK = 0xB0, VK_MEDIA_PREV_TRACK = 0xB1, VK_MEDIA_STOP = 0xB2, VK_MEDIA_PLAY_PAUSE = 0xB3;
    const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4, KEYEVENTF_SCANCODE = 0x8;

    public WindowsDesktopControl()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    }

    public bool IsAvailable => true;

    public bool Lock() => LockWorkStation();

    public bool Screensaver()
    {
        // The screensaver when one is set; otherwise the displays go to sleep (Windows has no screensaver by default).
        string? saver = null;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            saver = k?.GetValue("SCRNSAVE.EXE") as string;
        }
        catch { }
        if (!string.IsNullOrWhiteSpace(saver)) return PostMessageW(GetDesktopWindow(), 0x0112 /* WM_SYSCOMMAND */, 0xF140 /* SC_SCREENSAVE */, 0);
        return PostMessageW(0xFFFF /* HWND_BROADCAST */, 0x0112, 0xF170 /* SC_MONITORPOWER */, 2);
    }

    public bool Media(MediaKey key) => Tap(key switch
    {
        MediaKey.Next => VK_MEDIA_NEXT_TRACK,
        MediaKey.Previous => VK_MEDIA_PREV_TRACK,
        MediaKey.Stop => VK_MEDIA_STOP,
        _ => VK_MEDIA_PLAY_PAUSE,
    });

    // The volume keys show Windows' own volume flyout, like the Mac's media keys show its OSD.
    public bool VolumeUp() => Tap(VK_VOLUME_UP);
    public bool VolumeDown() => Tap(VK_VOLUME_DOWN);
    public bool ToggleMute() => Tap(VK_VOLUME_MUTE);

    public bool SetVolume(int percent)
    {
        var v = Endpoint(0 /* eRender */, 0 /* eConsole */);
        if (v == 0) return false;
        try
        {
            var set = (delegate* unmanaged[Stdcall]<nint, float, Guid*, int>)(*(nint**)v)[7]; // SetMasterVolumeLevelScalar
            return set(v, Math.Clamp(percent, 0, 100) / 100f, null) >= 0;
        }
        finally { Release(v); }
    }

    public int? Volume
    {
        get
        {
            var v = Endpoint(0, 0);
            if (v == 0) return null;
            try
            {
                float level;
                var get = (delegate* unmanaged[Stdcall]<nint, float*, int>)(*(nint**)v)[9]; // GetMasterVolumeLevelScalar
                return get(v, &level) >= 0 ? (int)Math.Round(level * 100) : null;
            }
            finally { Release(v); }
        }
    }

    public bool? ToggleMicMute()
    {
        // The default microphone for everything and the one calls use: both follow the first one's new state.
        bool? muted = null;
        foreach (var role in new[] { 0 /* eConsole */, 2 /* eCommunications */ })
        {
            var v = Endpoint(1 /* eCapture */, role);
            if (v == 0) continue;
            try
            {
                var vt = *(nint**)v;
                var getMute = (delegate* unmanaged[Stdcall]<nint, int*, int>)vt[15];
                var setMute = (delegate* unmanaged[Stdcall]<nint, int, Guid*, int>)vt[14];
                if (muted is null)
                {
                    int now;
                    if (getMute(v, &now) < 0) continue;
                    muted = now == 0;
                }
                setMute(v, muted.Value ? 1 : 0, null);
            }
            finally { Release(v); }
        }
        return muted;
    }

    public bool Screenshot() => Chord(VK_LWIN, VK_SNAPSHOT);
    public bool TaskView() => Chord(VK_LWIN, VK_TAB);
    public bool ShowDesktop() => Chord(VK_LWIN, VK_D);

    public string? OpenApp(string name)
    {
        name = name.Trim();
        try
        {
            // A Start menu shortcut by name: exact, then starting with, then containing.
            var target = FindShortcut(name);
            if (target is not null || File.Exists(name))
            {
                Process.Start(new ProcessStartInfo(target ?? name) { UseShellExecute = true });
                return null;
            }
            // Registered names (App Paths, PATH, URL schemes): notepad, calc, spotify:, ms-settings:…
            Process.Start(new ProcessStartInfo(name) { UseShellExecute = true });
            return null;
        }
        catch
        {
            return $"Couldn't find \"{name}\" on this PC.";
        }
    }

    static string? FindShortcut(string name)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        };
        var links = new List<string>();
        foreach (var r in roots)
        {
            if (string.IsNullOrEmpty(r) || !Directory.Exists(r)) continue;
            try { links.AddRange(Directory.EnumerateFiles(r, "*.lnk", SearchOption.AllDirectories)); } catch { }
        }
        string Base(string p) => Path.GetFileNameWithoutExtension(p);
        return links.FirstOrDefault(l => Base(l).Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? links.FirstOrDefault(l => Base(l).StartsWith(name, StringComparison.OrdinalIgnoreCase))
               ?? links.FirstOrDefault(l => Base(l).Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    public bool Brightness(int direction)
    {
        var any = DdcBrightness(direction);
        return any || WmiBrightness(direction);
    }

    public void Key(int usage, bool down)
    {
        if (RoamKeys.ToWindows(usage) is not { } k) return;
        var flags = down ? 0u : KEYEVENTF_KEYUP;
        if (k.Extended) flags |= KEYEVENTF_EXTENDEDKEY;
        if (k.ByVk) SendKey(k.Vk, k.Scan, flags);
        else SendKey(0, k.Scan, flags | KEYEVENTF_SCANCODE);
    }

    public void Text(string text)
    {
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            if (ch == '\n')
            {
                SendKey(VK_RETURN, 0, 0);
                SendKey(VK_RETURN, 0, KEYEVENTF_KEYUP);
                continue;
            }
            SendKey(0, ch, KEYEVENTF_UNICODE);
            SendKey(0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
        }
    }

    public void MouseMove(double dx, double dy)
    {
        if (!GetCursorPos(out var p)) return;
        // The phone moves in points: scale by the monitor under the cursor.
        var scale = 1.0;
        try
        {
            var mon = MonitorFromPoint(p, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (GetDpiForMonitor(mon, 0, out var dpi, out _) == 0 && dpi > 0) scale = dpi / 96.0;
        }
        catch { }
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        if (vw <= 1 || vh <= 1) return;
        var x = Math.Clamp(p.X + dx * scale, vx, vx + vw - 1);
        var y = Math.Clamp(p.Y + dy * scale, vy, vy + vh - 1);
        var nx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        var ny = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        SendMouse(0x0001 | 0x8000 | 0x4000 /* MOVE | ABSOLUTE | VIRTUALDESK */, nx, ny, 0);
    }

    public void MouseButton(RoamButton button, bool down)
    {
        var (flags, data) = button switch
        {
            RoamButton.Right => (down ? 0x0008u : 0x0010u, 0u),
            RoamButton.Middle => (down ? 0x0020u : 0x0040u, 0u),
            RoamButton.Back => (down ? 0x0080u : 0x0100u, 1u),
            RoamButton.Forward => (down ? 0x0080u : 0x0100u, 2u),
            _ => (down ? 0x0002u : 0x0004u, 0u),
        };
        SendMouse(flags, 0, 0, data);
    }

    public void Scroll(double dx, double dy)
    {
        // The phone scrolls in pixels (the Mac posts pixel wheel events); about 40 px is a notch here.
        var v = (int)Math.Round(dy * 3);
        var h = (int)Math.Round(dx * 3);
        if (v != 0) SendMouse(0x0800 /* WHEEL */, 0, 0, unchecked((uint)Math.Clamp(v, -12000, 12000)));
        if (h != 0) SendMouse(0x1000 /* HWHEEL */, 0, 0, unchecked((uint)Math.Clamp(h, -12000, 12000)));
    }

    // ---------------- keys ----------------

    static bool Tap(ushort vk)
    {
        var ok = SendKey(vk, 0, KEYEVENTF_EXTENDEDKEY);
        return SendKey(vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP) && ok;
    }

    static bool Chord(ushort modifier, ushort vk)
    {
        var ok = SendKey(modifier, 0, KEYEVENTF_EXTENDEDKEY);
        ok &= SendKey(vk, 0, 0);
        ok &= SendKey(vk, 0, KEYEVENTF_KEYUP);
        ok &= SendKey(modifier, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP);
        return ok;
    }

    static bool SendKey(ushort vk, ushort scan, uint flags)
    {
        var input = new INPUT { type = 1 };
        input.u.ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = WindowsRoamPlatform.Marker };
        return SendInput(1, &input, sizeof(INPUT)) == 1;
    }

    static void SendMouse(uint flags, int dx, int dy, uint data)
    {
        var input = new INPUT { type = 0 };
        input.u.mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags, dwExtraInfo = WindowsRoamPlatform.Marker };
        SendInput(1, &input, sizeof(INPUT));
    }

    // ---------------- Core Audio ----------------

    static readonly Guid ClsidEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static readonly Guid IidEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    static readonly Guid IidEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    /// <summary>IAudioEndpointVolume of the default device for this flow/role, or 0. The caller releases it.</summary>
    static nint Endpoint(int flow, int role)
    {
        try
        {
            CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */); // S_FALSE / RPC_E_CHANGED_MODE are fine: COM is usable either way
            var clsid = ClsidEnumerator;
            var iid = IidEnumerator;
            nint en;
            if (CoCreateInstance(&clsid, 0, 0x17 /* CLSCTX_ALL */, &iid, &en) < 0 || en == 0) return 0;
            try
            {
                nint dev;
                var getDefault = (delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)(*(nint**)en)[4]; // GetDefaultAudioEndpoint
                if (getDefault(en, flow, role, &dev) < 0 || dev == 0) return 0;
                try
                {
                    var vid = IidEndpointVolume;
                    nint vol;
                    var activate = (delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)(*(nint**)dev)[3]; // IMMDevice::Activate
                    return activate(dev, &vid, 0x17, 0, &vol) >= 0 ? vol : 0;
                }
                finally { Release(dev); }
            }
            finally { Release(en); }
        }
        catch { return 0; }
    }

    static void Release(nint p)
    {
        if (p != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)p)[2])(p);
    }

    // ---------------- brightness ----------------

    static readonly List<nint> s_monitors = new();

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    static int CollectMonitor(nint hmon, nint hdc, RECT* rect, nint data)
    {
        s_monitors.Add(hmon);
        return 1;
    }

    /// <summary>DDC/CI (external monitors that allow it): one step of 10 % of each monitor's range.</summary>
    static bool DdcBrightness(int direction)
    {
        List<nint> monitors;
        lock (s_monitors)
        {
            s_monitors.Clear();
            EnumDisplayMonitors(0, 0, &CollectMonitor, 0);
            monitors = s_monitors.ToList();
        }
        var any = false;
        foreach (var hmon in monitors)
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out var n) || n == 0) continue;
            var list = new PHYSICAL_MONITOR[n];
            fixed (PHYSICAL_MONITOR* p = list)
            {
                if (!GetPhysicalMonitorsFromHMONITOR(hmon, n, p)) continue;
                try
                {
                    for (var i = 0; i < n; i++)
                    {
                        if (!GetMonitorBrightness(p[i].hPhysicalMonitor, out var min, out var cur, out var max) || max <= min) continue;
                        var step = Math.Max(1u, (max - min) / 10);
                        var next = direction > 0 ? Math.Min(max, cur + step) : (cur > min + step ? cur - step : min);
                        any |= SetMonitorBrightness(p[i].hPhysicalMonitor, next);
                    }
                }
                finally { DestroyPhysicalMonitors(n, p); }
            }
        }
        return any;
    }

    /// <summary>A laptop's own panel (WMI, through PowerShell so no extra library is needed).</summary>
    static bool WmiBrightness(int direction)
    {
        var delta = direction > 0 ? 10 : -10;
        var script = "$m = Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightness -ErrorAction Stop | Select-Object -First 1; " +
                     $"$b = [Math]::Max(0, [Math]::Min(100, [int]$m.CurrentBrightness + ({delta}))); " +
                     "Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightnessMethods -ErrorAction Stop | Invoke-CimMethod -MethodName WmiSetBrightness -Arguments @{ Timeout = 0; Brightness = [byte]$b } -ErrorAction Stop | Out-Null";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
            if (p is null) return false;
            if (!p.WaitForExit(6000))
            {
                try { p.Kill(); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // ---------------- Win32 ----------------

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nuint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nuint dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public INPUTUNION u; }

    [StructLayout(LayoutKind.Sequential)]
    struct PHYSICAL_MONITOR { public nint hPhysicalMonitor; public fixed char szPhysicalMonitorDescription[128]; }

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool LockWorkStation();
    [LibraryImport("user32.dll")] private static partial nint GetDesktopWindow();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [LibraryImport("user32.dll")] private static partial uint SendInput(uint count, INPUT* inputs, int size);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetCursorPos(out POINT p);
    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll")] private static partial nint MonitorFromPoint(POINT pt, uint flags);
    [LibraryImport("shcore.dll")] private static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EnumDisplayMonitors(nint hdc, nint clip, delegate* unmanaged[Stdcall]<nint, nint, RECT*, nint, int> proc, nint data);
    [LibraryImport("dxva2.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint monitor, out uint count);
    [LibraryImport("dxva2.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetPhysicalMonitorsFromHMONITOR(nint monitor, uint count, PHYSICAL_MONITOR* monitors);
    [LibraryImport("dxva2.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR* monitors);
    [LibraryImport("dxva2.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetMonitorBrightness(nint monitor, out uint min, out uint current, out uint max);
    [LibraryImport("dxva2.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetMonitorBrightness(nint monitor, uint brightness);
    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint coinit);
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* ppv);
}
