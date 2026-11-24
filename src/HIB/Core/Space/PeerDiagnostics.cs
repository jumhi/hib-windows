// H.I.B Space (docs/SPACE.md §12.2): "Get log" — read another computer's log over the peer link (peerDiag →
// peerDiagLog, §3.3) and keep it next to this PC's own: %LOCALAPPDATA%\H.I.B\logs\<computer name>.log.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HIB.Core.Space;

public static class PeerDiagnostics
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Where this PC's logs live (hib.log's folder; tests point it elsewhere).</summary>
    public static string LogsDirectory => DirectoryOverride ?? Path.GetDirectoryName(HIB.Features.HibLog.DefaultPath())!;

    /// <summary>Tests: save fetched logs here instead of the user's logs folder.</summary>
    public static string? DirectoryOverride { get; set; }

    /// <summary>A file name for a computer's log: its name with anything a file name can't hold replaced.</summary>
    public static string FileNameFor(string computer)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        var clean = new string((computer ?? "").Select(c => bad.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().Trim('.');
        if (clean.Length == 0) clean = "computer";
        if (clean.Length > 80) clean = clean[..80];
        if (string.Equals(clean, "hib", StringComparison.OrdinalIgnoreCase)) clean += " (peer)"; // never this PC's own hib.log
        return clean + ".log";
    }

    /// <summary>Saves <paramref name="text"/> as &lt;computer&gt;.log in <paramref name="dir"/> (default: the logs folder); returns the path.</summary>
    public static string Save(string computer, string version, string text, string? dir = null)
    {
        dir ??= LogsDirectory;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileNameFor(computer));
        var header = $"# H.I.B log from {computer}{(version.Length > 0 ? $" (H.I.B {version})" : "")}, fetched {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}";
        File.WriteAllText(path, header + text, new UTF8Encoding(false));
        return path;
    }

    /// <summary>
    /// Asks <paramref name="peerId"/> for its log over the direct link and saves it. Null with a reason when it isn't linked or
    /// doesn't answer.
    /// </summary>
    public static async Task<(string? Path, string? Problem)> FetchAsync(PeerNetwork network, string peerId, string name, string? dir = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (network.Get(peerId) is not { } link) return (null, $"{name} isn't linked directly right now");
        var got = await link.RequestDiagnosticsAsync(timeout ?? Timeout, ct).ConfigureAwait(false);
        if (got is not { } log) return (null, $"{name} didn't send its log");
        try { return (Save(name, log.Version, log.Text, dir), null); }
        catch (Exception ex) { return (null, "Couldn't save the log: " + ex.Message); }
    }
}
