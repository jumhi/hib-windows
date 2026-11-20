using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace HIB.Core.Pairing;

/// <summary>
/// The pairing QR text: airsync://ip[,ip…]:port?name=&lt;pct-enc&gt;?plus=true?key=&lt;raw base64&gt;
/// Separators are '?', the key is NOT url-encoded, the name is strictly percent-encoded (%20 for space).
/// </summary>
public static class QrPayload
{
    public static string Build(IEnumerable<string> ips, int port, string name, string keyBase64, bool plus = true)
    {
        var ipList = string.Join(",", ips.Where(i => !string.IsNullOrWhiteSpace(i)));
        if (ipList.Length == 0) ipList = "127.0.0.1";
        return $"airsync://{ipList}:{port}?name={EncodeName(name)}?plus={(plus ? "true" : "false")}?key={keyBase64}";
    }

    /// <summary>RFC 3986 unreserved characters stay; everything else (UTF-8) becomes %XX.</summary>
    public static string EncodeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(name))
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_' or '~')
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    public sealed record Parsed(IReadOnlyList<string> Ips, int Port, string Name, bool? Plus, string Key);

    /// <summary>Parses like the Android app does (used by the fake-phone harness).</summary>
    public static Parsed Parse(string qr)
    {
        var rest = qr.Substring(qr.IndexOf("://", StringComparison.Ordinal) + 3);
        var hostPort = rest.Split('?')[0];
        var colon = hostPort.LastIndexOf(':');
        var host = hostPort[..colon];
        var port = int.Parse(hostPort[(colon + 1)..]);
        var query = rest.Contains('?') ? rest[(rest.IndexOf('?') + 1)..] : "";
        var map = new Dictionary<string, string>();
        foreach (var part in query.Split('?'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            map[part[..eq]] = part[(eq + 1)..];
        }
        // Android uses URLDecoder.decode which maps '+' to space; we never emit '+'.
        var name = map.TryGetValue("name", out var n) ? WebUtility.UrlDecode(n) : "";
        bool? plus = map.TryGetValue("plus", out var p) ? p switch { "true" => true, "false" => false, _ => null } : null;
        var key = map.TryGetValue("key", out var k) ? k : "";
        return new Parsed(host.Split(',', StringSplitOptions.RemoveEmptyEntries), port, name, plus, key);
    }
}
