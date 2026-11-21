// H.I.B Desk Deck (docs/SPACE.md §11.3): this PC's live state for the phone's deck, the Mac's fields —
// CPU (GetSystemTimes deltas), memory (GlobalMemoryStatusEx), battery (GetSystemPowerStatus), uptime and
// network rates (KB/s since the last sample, every up interface but loopback/tunnels).
using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace HIB.Core.Act;

public sealed partial class SystemStats : ISystemStats
{
    readonly Func<DateTime> _clock;
    (ulong Idle, ulong Kernel, ulong User)? _lastCpu;
    (long Up, long Down, DateTime At)? _lastNet;

    public SystemStats(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    public void Reset()
    {
        _lastCpu = null;
        _lastNet = null;
    }

    public DeckStats Sample()
    {
        var (mem, pressure) = Memory();
        var (battery, charging) = Battery();
        var (up, down) = Network();
        return new DeckStats(Cpu(), mem, pressure, Environment.TickCount64 / 1000, battery, charging, up, down);
    }

    /// <summary>Busy share of all cores since the previous sample (0 on the first, like the Mac).</summary>
    int Cpu()
    {
        if (!OperatingSystem.IsWindows() || !GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var now = (idle, kernel, user);
        var last = _lastCpu;
        _lastCpu = now;
        if (last is not { } l) return 0;
        var di = idle - l.Idle;
        var total = (kernel - l.Kernel) + (user - l.User); // kernel time includes idle time
        if (total == 0 || di > total) return 0;
        return (int)Math.Round((double)(total - di) / total * 100);
    }

    /// <summary>Memory in use (%) and a pressure word: "critical" at 95 %, "warning" at 85 %, else "normal".</summary>
    static (int Percent, string Pressure) Memory()
    {
        if (!OperatingSystem.IsWindows()) return (0, "normal");
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m) || m.ullTotalPhys == 0) return (0, "normal");
        var used = (int)Math.Clamp(Math.Round((double)(m.ullTotalPhys - m.ullAvailPhys) / m.ullTotalPhys * 100), 0, 100);
        return (used, PressureFor(used));
    }

    public static string PressureFor(int usedPercent) => usedPercent >= 95 ? "critical" : usedPercent >= 85 ? "warning" : "normal";

    /// <summary>Battery level (−1 without one) and charging (or on AC and full, like the Mac).</summary>
    static (int Level, bool Charging) Battery()
    {
        if (!OperatingSystem.IsWindows() || !GetSystemPowerStatus(out var p)) return (-1, false);
        return BatteryFrom(p.ACLineStatus, p.BatteryFlag, p.BatteryLifePercent);
    }

    public static (int Level, bool Charging) BatteryFrom(byte acLine, byte flag, byte percent)
    {
        if ((flag & 128) != 0 || flag == 255 || percent == 255) return (-1, false); // no battery / unknown
        var level = Math.Clamp((int)percent, 0, 100);
        var charging = (flag & 8) != 0 || (acLine == 1 && level >= 100);
        return (level, charging);
    }

    /// <summary>KB/s up and down since the previous sample.</summary>
    (int Up, int Down) Network()
    {
        long up = 0, down = 0;
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                try
                {
                    var s = n.GetIPStatistics();
                    up += s.BytesSent;
                    down += s.BytesReceived;
                }
                catch { /* some adapters don't say */ }
            }
        }
        catch { return (0, 0); }
        var now = _clock();
        var last = _lastNet;
        _lastNet = (up, down, now);
        if (last is not { } l) return (0, 0);
        var dt = (now - l.At).TotalSeconds;
        if (dt <= 0) return (0, 0);
        var du = up >= l.Up ? up - l.Up : 0;
        var dd = down >= l.Down ? down - l.Down : 0;
        return ((int)(du / dt / 1024), (int)(dd / dt / 1024));
    }

    // ---------------- Win32 ----------------

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
}
