using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HIB.Core.Pairing;

public sealed record LocalAddress(string Address, string InterfaceName, string? Broadcast, int InterfaceIndex);

public static class NetworkInfo
{
    /// <summary>All up, non-loopback IPv4 addresses (private ones first, Wi-Fi/Ethernet before virtual adapters).</summary>
    public static List<LocalAddress> GetLocalIPv4()
    {
        var list = new List<(LocalAddress addr, int rank)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); } catch { continue; }
                int index = 0;
                try { index = props.GetIPv4Properties()?.Index ?? 0; } catch { }
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;
                    var s = ua.Address.ToString();
                    if (s.StartsWith("169.254.", StringComparison.Ordinal)) continue; // link-local / APIPA
                    string? bcast = null;
                    try
                    {
                        var mask = ua.IPv4Mask;
                        if (mask is not null && !mask.Equals(IPAddress.Any))
                        {
                            var a = ua.Address.GetAddressBytes();
                            var m = mask.GetAddressBytes();
                            var b = new byte[4];
                            for (var i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
                            bcast = new IPAddress(b).ToString();
                        }
                    }
                    catch { }
                    var name = ni.Name + " " + ni.Description;
                    var rank = IsPrivate(s) ? 0 : 2;
                    if (LooksVirtual(name)) rank += 1;
                    list.Add((new LocalAddress(s, ni.Name, bcast, index), rank));
                }
            }
        }
        catch { }
        return list.OrderBy(x => x.rank).Select(x => x.addr).DistinctBy(a => a.Address).ToList();
    }

    static bool LooksVirtual(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("vethernet") || n.Contains("virtual") || n.Contains("vmware") || n.Contains("vbox")
               || n.Contains("hyper-v") || n.Contains("wsl") || n.Contains("docker") || n.Contains("bridge")
               || n.StartsWith("utun") || n.StartsWith("awdl") || n.StartsWith("llw");
    }

    /// <summary>The phone accepts 192.168.*, 10.*, 100.* (CGNAT/Tailscale), 172.16-31.*, 127.0.0.1.</summary>
    public static bool IsPrivate(string ip)
    {
        if (ip.StartsWith("192.168.", StringComparison.Ordinal) || ip.StartsWith("10.", StringComparison.Ordinal)
            || ip.StartsWith("100.", StringComparison.Ordinal) || ip == "127.0.0.1") return true;
        if (ip.StartsWith("172.", StringComparison.Ordinal))
        {
            var parts = ip.Split('.');
            if (parts.Length > 1 && int.TryParse(parts[1], out var second)) return second is >= 16 and <= 31;
        }
        return false;
    }

    /// <summary>The Mac's takeover rule (192.168., 10., 172.16-31.).</summary>
    public static bool IsTakeoverPrivate(string? ip)
        => !string.IsNullOrEmpty(ip) && IsPrivate(ip) && !ip.StartsWith("100.", StringComparison.Ordinal) && ip != "127.0.0.1";
}
