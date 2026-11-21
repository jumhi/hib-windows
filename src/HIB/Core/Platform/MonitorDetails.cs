// H.I.B Room (docs/SPACE.md §10.1): what each monitor really is — its friendly name ("Odyssey OLED G9"),
// its physical size in millimetres and whether it is the laptop's own panel. Windows: QueryDisplayConfig
// gives, per active path, the GDI name (\\.\DISPLAY1, Avalonia's Screen.DisplayName), the target's
// monitorFriendlyDeviceName, its output technology (internal = built in) and monitorDevicePath, which
// leads to the monitor's EDID under HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\<id>\<instance>\Device Parameters.
// Everything here is best effort: anything unknown stays 0 / empty and the room assumes 0.25 mm per DIP.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace HIB.Core.Platform;

/// <summary>One active monitor as Windows describes it. Millimetres are 0 when unknown.</summary>
public sealed record MonitorDetails(string GdiName, string FriendlyName, double MmW, double MmH, bool BuiltIn);

/// <summary>The parts of an EDID block (VESA E-EDID 1.3/1.4) the room needs.</summary>
public static class Edid
{
    static readonly byte[] Header = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };

    public static bool IsValid(byte[]? edid)
    {
        if (edid is null || edid.Length < 128) return false;
        for (var i = 0; i < Header.Length; i++)
            if (edid[i] != Header[i]) return false;
        return true;
    }

    /// <summary>
    /// The image size in millimetres: the first detailed timing descriptor's mm (bytes 66–68) when it agrees with the
    /// basic block's centimetres (bytes 21/22) — some monitors put cm or nonsense there — else the cm × 10. (0, 0) when unknown.
    /// </summary>
    public static (double W, double H) PhysicalSizeMm(byte[]? edid)
    {
        if (!IsValid(edid)) return (0, 0);
        var e = edid!;
        double cmW = e[21] * 10, cmH = e[22] * 10;
        var hasCm = cmW > 0 && cmH > 0;
        double dtdW = 0, dtdH = 0;
        // Descriptor 1 at 54: a detailed timing when its pixel clock (bytes 54–55) isn't 0.
        if (e[54] != 0 || e[55] != 0)
        {
            dtdW = e[66] | ((e[68] & 0xF0) << 4);
            dtdH = e[67] | ((e[68] & 0x0F) << 8);
        }
        var hasDtd = dtdW > 0 && dtdH > 0 && dtdW < 5000 && dtdH < 5000;
        if (hasDtd && (!hasCm || Math.Abs(dtdW - cmW) <= Math.Max(20, cmW * 0.1))) return (dtdW, dtdH);
        if (hasCm) return (cmW, cmH);
        return hasDtd ? (dtdW, dtdH) : (0, 0);
    }

    /// <summary>The monitor name descriptor (tag 0xFC), e.g. "Odyssey G95SC", or null.</summary>
    public static string? MonitorName(byte[]? edid)
    {
        if (!IsValid(edid)) return null;
        var e = edid!;
        for (var d = 54; d + 18 <= 126; d += 18)
        {
            if (e[d] != 0 || e[d + 1] != 0 || e[d + 3] != 0xFC) continue;
            var sb = new StringBuilder();
            for (var i = d + 5; i < d + 18; i++)
            {
                var c = e[i];
                if (c == 0x0A || c == 0) break;
                sb.Append(c is >= 0x20 and < 0x7F ? (char)c : ' ');
            }
            var name = sb.ToString().Trim();
            return name.Length > 0 ? name : null;
        }
        return null;
    }
}

public static partial class MonitorDetailsProvider
{
    /// <summary>
    /// The registry location of a monitor from its <c>monitorDevicePath</c>
    /// (<c>\\?\DISPLAY#SAM7179#5&amp;2a5bd0b2&amp;0&amp;UID4352#{e6f07b5f-…}</c> → ("SAM7179", "5&amp;2a5bd0b2&amp;0&amp;UID4352")), or null.
    /// </summary>
    public static (string Model, string Instance)? RegistryKeyFor(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return null;
        var p = devicePath.Trim();
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        var parts = p.Split('#');
        if (parts.Length < 3 || !parts[0].Equals("DISPLAY", StringComparison.OrdinalIgnoreCase)) return null;
        if (parts[1].Length == 0 || parts[2].Length == 0) return null;
        return (parts[1], parts[2]);
    }

    /// <summary>Every active monitor (Windows); empty elsewhere or when Windows won't say.</summary>
    public static IReadOnlyList<MonitorDetails> Query()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<MonitorDetails>();
        try { return QueryWindows(); }
        catch { return Array.Empty<MonitorDetails>(); }
    }

    // ---------------- Windows ----------------

    const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    const int GET_SOURCE_NAME = 1, GET_TARGET_NAME = 2;
    const uint OUTPUT_INTERNAL = 0x80000000, OUTPUT_DISPLAYPORT_EMBEDDED = 11, OUTPUT_UDI_EMBEDDED = 13;

    [StructLayout(LayoutKind.Sequential)]
    struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    struct PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    struct PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshNum, refreshDen;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PATH_INFO { public PATH_SOURCE_INFO sourceInfo; public PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    struct MODE_INFO { public uint infoType; public uint id; public LUID adapterId; }

    [StructLayout(LayoutKind.Sequential)]
    struct DEVICE_INFO_HEADER { public int type; public uint size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SOURCE_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TARGET_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PATH_INFO[] paths, ref uint numModes, [Out] MODE_INFO[] modes, IntPtr topology);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    static extern int GetSourceName(ref SOURCE_DEVICE_NAME info);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    static extern int GetTargetName(ref TARGET_DEVICE_NAME info);

    [SupportedOSPlatform("windows")]
    static IReadOnlyList<MonitorDetails> QueryWindows()
    {
        var result = new List<MonitorDetails>();
        PATH_INFO[] paths;
        uint numPaths = 0;
        for (var attempt = 0; ; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out numPaths, out var numModes) != 0) return result;
            paths = new PATH_INFO[numPaths];
            var modes = new MODE_INFO[numModes];
            var rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
            if (rc == 0) break;
            if (rc != 122 /* ERROR_INSUFFICIENT_BUFFER: the setup changed in between */ || attempt >= 3) return result;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < numPaths; i++)
        {
            var path = paths[i];
            var source = new SOURCE_DEVICE_NAME
            {
                header = new DEVICE_INFO_HEADER { type = GET_SOURCE_NAME, size = (uint)Marshal.SizeOf<SOURCE_DEVICE_NAME>(), adapterId = path.sourceInfo.adapterId, id = path.sourceInfo.id },
                viewGdiDeviceName = "",
            };
            if (GetSourceName(ref source) != 0 || string.IsNullOrEmpty(source.viewGdiDeviceName)) continue;
            if (!seen.Add(source.viewGdiDeviceName)) continue; // a cloned source: the first target names it
            var target = new TARGET_DEVICE_NAME
            {
                header = new DEVICE_INFO_HEADER { type = GET_TARGET_NAME, size = (uint)Marshal.SizeOf<TARGET_DEVICE_NAME>(), adapterId = path.targetInfo.adapterId, id = path.targetInfo.id },
                monitorFriendlyDeviceName = "",
                monitorDevicePath = "",
            };
            var gotTarget = GetTargetName(ref target) == 0;
            var tech = gotTarget ? target.outputTechnology : path.targetInfo.outputTechnology;
            var builtIn = tech is OUTPUT_INTERNAL or OUTPUT_DISPLAYPORT_EMBEDDED or OUTPUT_UDI_EMBEDDED;
            var edid = gotTarget ? ReadEdid(target.monitorDevicePath) : null;
            var (mmW, mmH) = Edid.PhysicalSizeMm(edid);
            var name = gotTarget && !string.IsNullOrWhiteSpace(target.monitorFriendlyDeviceName) ? target.monitorFriendlyDeviceName.Trim()
                : Edid.MonitorName(edid) ?? (builtIn ? "Built-in display" : "");
            result.Add(new MonitorDetails(source.viewGdiDeviceName, name, mmW, mmH, builtIn));
        }
        return result;
    }

    [SupportedOSPlatform("windows")]
    static byte[]? ReadEdid(string devicePath)
    {
        if (RegistryKeyFor(devicePath) is not { } k) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{k.Model}\{k.Instance}\Device Parameters");
            return key?.GetValue("EDID") as byte[];
        }
        catch { return null; }
    }
}
