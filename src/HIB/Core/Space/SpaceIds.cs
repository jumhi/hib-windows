// H.I.B Space (docs/SPACE.md §2): identity helpers shared by the directory and the peer link.
using System;
using System.Linq;
using System.Text;

namespace HIB.Core.Space;

public static class SpaceIds
{
    /// <summary>The phone's id in every list.</summary>
    public const string Phone = "phone";

    /// <summary>This app's platform on the wire.</summary>
    public const string ThisPlatform = "windows";

    /// <summary>This app's <c>type</c> on the wire (also macInfo.type).</summary>
    public const string ThisType = "Windows PC";

    /// <summary>
    /// SPACE.md §2: ids compare lexicographically over their UTF-8 bytes (not UTF-16 code units,
    /// which order surrogate pairs differently). Both computers must compute the same answer.
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        var x = Encoding.UTF8.GetBytes(a ?? "");
        var y = Encoding.UTF8.GetBytes(b ?? "");
        var n = Math.Min(x.Length, y.Length);
        for (var i = 0; i < n; i++)
        {
            if (x[i] != y[i]) return x[i] < y[i] ? -1 : 1;
        }
        return x.Length.CompareTo(y.Length);
    }

    /// <summary>The same computer: names compare case-insensitively, like the phone and the Mac do.</summary>
    public static bool Same(string? a, string? b)
        => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static SpaceDeviceKind KindFor(string? platform)
        => (platform ?? "").Trim().ToLowerInvariant() switch
        {
            "windows" => SpaceDeviceKind.Windows,
            "android" => SpaceDeviceKind.Phone,
            _ => SpaceDeviceKind.Mac,
        };

    public static string DefaultModel(SpaceDeviceKind kind) => kind switch
    {
        SpaceDeviceKind.Windows => "Windows PC",
        SpaceDeviceKind.Phone => "Android phone",
        _ => "Mac",
    };

    /// <summary>Flick kind from a MIME type: image | video | file.</summary>
    public static string KindForMime(string? mime)
    {
        mime ??= "";
        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return "image";
        if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return "video";
        return "file";
    }

    /// <summary>"link" when the whole text is one http(s) URL (like the Mac's FlickPayload.textKind), else "text".</summary>
    public static string TextKind(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Any(char.IsWhiteSpace)) return "text";
        if (Uri.TryCreate(t, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && u.Host.Length > 0) return "link";
        return "text"; // like the Mac: only a whole http(s) URL is a link
    }
}
