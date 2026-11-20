using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HIB.Core.Pairing;

namespace HIB.Core.Discovery;

/// <summary>
/// Asks a phone to connect to us: POST http://phone:8888/wakeup with a plaintext JSON body,
/// falling back to the BARE JSON over UDP 8889 (the Mac's "AIRSYNC_WAKEUP:" prefix is not understood by the phone).
/// The key is never sent; the phone looks it up by macName.
/// </summary>
public static class WakeUpClient
{
    public const int HttpPort = 8888;
    public const int UdpPort = 8889;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    public static string BuildPayload(string macIp, int macPort, string macName)
        => JsonSerializer.Serialize(new
        {
            type = "wakeUpRequest",
            data = new { macIP = macIp, macPort, macName, isPlus = true },
        });

    /// <summary>Returns true if any phone IP answered the HTTP wake-up (UDP is fire-and-forget).</summary>
    public static async Task<bool> WakeAsync(IEnumerable<string> phoneIps, int macPort, string macName, CancellationToken ct = default)
    {
        var locals = NetworkInfo.GetLocalIPv4();
        var any = false;
        foreach (var phoneIp in phoneIps.Distinct())
        {
            if (!IPAddress.TryParse(phoneIp, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork) continue;
            var macIp = PickLocalFor(phoneIp, locals) ?? locals.FirstOrDefault()?.Address ?? "127.0.0.1";
            var body = BuildPayload(macIp, macPort, macName);
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var resp = await Http.PostAsync($"http://{phoneIp}:{HttpPort}/wakeup", content, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) { any = true; continue; }
            }
            catch { }
            try
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                var bytes = Encoding.UTF8.GetBytes(body);
                await udp.SendAsync(bytes, new IPEndPoint(addr, UdpPort), ct).ConfigureAwait(false);
            }
            catch { }
        }
        return any;
    }

    /// <summary>Picks the local address on the same /24 as the phone (best effort).</summary>
    public static string? PickLocalFor(string phoneIp, IEnumerable<LocalAddress> locals)
    {
        var prefix = string.Join('.', phoneIp.Split('.').Take(3)) + ".";
        return locals.FirstOrDefault(l => l.Address.StartsWith(prefix, StringComparison.Ordinal))?.Address;
    }
}
