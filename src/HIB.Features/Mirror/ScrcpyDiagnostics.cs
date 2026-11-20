using System.Text.RegularExpressions;

namespace HIB.Features.Mirror;

/// <summary>A monitor's usable area in screen pixels (left/top inclusive, right/bottom exclusive).</summary>
public readonly record struct ScreenArea(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

/// <summary>Reads scrcpy's console output (scrcpy 5: <c>INFO: …</c>, <c>ERROR: …</c>, <c>[server] ERROR: …</c>).</summary>
public static partial class ScrcpyOutput
{
    // Follow-up lines scrcpy prints after the real error; only used when nothing better exists.
    private static readonly string[] GenericErrors =
    [
        "server connection failed",
        "exception on thread",
        "demuxer error",
        "controller error",
        "decoder error",
        "recorder error",
        "could not start server",
        "failed to execute",
    ];

    [GeneratedRegex(@"^(?:\[server\]\s*)?ERROR:\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLine();

    // adb's own complaints, which scrcpy passes through (e.g. "adb: device unauthorized.", "error: device offline").
    [GeneratedRegex(@"^(?:adb(?:\.exe)?:\s*)?(?:error:\s*)(.+)$|^adb(?:\.exe)?:\s*(.*(?:unauthorized|not found|offline|failed|refused|cannot|unable).*)$", RegexOptions.IgnoreCase)]
    private static partial Regex AdbErrorLine();

    // "java.lang.IllegalStateException: message" / "Caused by: x.y.FooException: message"
    [GeneratedRegex(@"^(?:Caused by:\s*)?[a-z][\w.$]*(?:Exception|Error):\s*(.+)$")]
    private static partial Regex JavaException();

    /// <summary>
    /// The real reason scrcpy failed: the last specific <c>ERROR:</c> line (or adb error / Java exception
    /// message), skipping generic follow-ups like "Server connection failed" when something better exists.
    /// Null when the output has no error at all.
    /// </summary>
    public static string? ExtractError(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var lines = Lines(output);
        string? lastSpecific = null, lastGeneric = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            string? message = null;
            var m = ErrorLine().Match(line);
            if (m.Success) message = m.Groups[1].Value.Trim();
            else if (AdbErrorLine().Match(line) is { Success: true } a)
                message = (a.Groups[1].Success ? a.Groups[1].Value : a.Groups[2].Value).Trim();
            else if (JavaException().Match(line) is { Success: true } j && i > 0 && LooksLikeServerError(lines, i))
                message = j.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(message)) continue;
            if (IsGeneric(message)) lastGeneric = message;
            else lastSpecific = message;
        }
        return lastSpecific ?? lastGeneric;
    }

    private static bool LooksLikeServerError(List<string> lines, int i)
    {
        // Only trust a Java exception that follows a server error (scrcpy prints the stack trace after it).
        for (var k = i - 1; k >= 0 && k >= i - 6; k--)
            if (lines[k].Contains("ERROR", StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool IsGeneric(string message)
    {
        var lower = message.ToLowerInvariant();
        return GenericErrors.Any(g => lower.Contains(g, StringComparison.Ordinal));
    }

    /// <summary>The last non-empty line (stack-trace frames skipped), for "taking long" messages.</summary>
    public static string? LastMeaningfulLine(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var lines = Lines(output);
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var l = lines[i];
            if (l.StartsWith("at ", StringComparison.Ordinal) || l.StartsWith("...", StringComparison.Ordinal)) continue;
            return l;
        }
        return null;
    }

    /// <summary>
    /// scrcpy 5 logs <c>INFO: Texture: WxH</c> (or <c>Texture (D3D11VA): WxH</c>) when the first frame reaches
    /// the window, which is when the window is shown. (<c>INFO: Renderer: …</c> comes earlier, while hidden.)
    /// </summary>
    public static bool IsWindowShownLine(string? line)
    {
        if (line is null) return false;
        var t = line.Trim();
        return t.StartsWith("INFO: Texture:", StringComparison.Ordinal)
            || t.StartsWith("INFO: Texture (", StringComparison.Ordinal);
    }

    /// <summary><c>INFO: Renderer: …</c>: the (still hidden) window and its renderer exist.</summary>
    public static bool IsWindowCreatedLine(string? line)
        => line is not null && line.Trim().StartsWith("INFO: Renderer:", StringComparison.Ordinal);

    /// <summary>adb said this computer isn't authorized / paired for debugging.</summary>
    public static bool IndicatesPairing(string? output)
    {
        if (string.IsNullOrEmpty(output)) return false;
        var lower = output.ToLowerInvariant();
        return lower.Contains("unauthorized") || lower.Contains("failed to authenticate")
            || lower.Contains("not authorized") || lower.Contains("adb pair");
    }

    private static readonly string[] ConnectionLost =
    [
        "could not connect to",
        "failed to connect",
        "cannot connect to",
        "connection refused",
        "no devices/emulators found",
        "could not find any adb device",
        "device offline",
    ];

    /// <summary>scrcpy couldn't reach the phone through adb (stale Wireless debugging port, phone gone).</summary>
    public static bool IndicatesAdbConnectionLost(string? output)
    {
        if (string.IsNullOrEmpty(output)) return false;
        var lower = output.ToLowerInvariant();
        if (ConnectionLost.Any(p => lower.Contains(p, StringComparison.Ordinal))) return true;
        // adb: "error: device '10.107.0.205:41234' not found"
        return Regex.IsMatch(lower, @"device '[^']*' not found");
    }

    private static List<string> Lines(string output)
        => output.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                 .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
}

/// <summary>Where the scrcpy window may go.</summary>
public static class MirrorWindowPlacement
{
    /// <summary>Room kept on screen so the title bar can still be grabbed.</summary>
    public const int MinVisible = 160;
    /// <summary>SDL positions the client area; the title bar sits above it.</summary>
    public const int TitleBar = 40;

    /// <summary>
    /// Clamps a saved <c>--window-x/--window-y</c> to a monitor that exists now (monitors get unplugged and
    /// rearranged; an off-screen scrcpy window looks like "nothing opened"). Keeps the point when it is
    /// already comfortably on a screen, otherwise moves it onto the nearest one. With no screen
    /// information it returns the point unchanged.
    /// </summary>
    public static (int X, int Y) Clamp(int x, int y, IReadOnlyList<ScreenArea>? screens)
    {
        if (screens is null || screens.Count == 0) return (x, y);
        var valid = screens.Where(s => s.Width > 0 && s.Height > 0).ToList();
        if (valid.Count == 0) return (x, y);

        foreach (var s in valid)
            if (Fits(x, y, s)) return (x, y);

        // Nearest screen by distance from the point to the rectangle.
        var best = valid.OrderBy(s => DistanceSquared(x, y, s)).First();
        var cx = Math.Clamp(x, best.Left, Math.Max(best.Left, best.Right - MinVisible));
        var cy = Math.Clamp(y, best.Top + TitleBar, Math.Max(best.Top + TitleBar, best.Bottom - MinVisible));
        return (cx, cy);
    }

    private static bool Fits(int x, int y, ScreenArea s)
        => x >= s.Left && x <= s.Right - MinVisible && y >= s.Top + TitleBar && y <= s.Bottom - MinVisible;

    private static long DistanceSquared(int x, int y, ScreenArea s)
    {
        long dx = x < s.Left ? s.Left - x : x >= s.Right ? x - (s.Right - 1) : 0;
        long dy = y < s.Top ? s.Top - y : y >= s.Bottom ? y - (s.Bottom - 1) : 0;
        return dx * dx + dy * dy;
    }
}

/// <summary>When to say "Mirroring is taking long".</summary>
public static class MirrorWatchdog
{
    public static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(12);

    /// <summary>True once: scrcpy is still running <see cref="SlowAfter"/> after start and its window never showed.</summary>
    public static bool ShouldWarn(TimeSpan elapsed, bool running, bool windowShown, bool alreadyWarned, TimeSpan? slowAfter = null)
        => running && !windowShown && !alreadyWarned && elapsed >= (slowAfter ?? SlowAfter);
}
