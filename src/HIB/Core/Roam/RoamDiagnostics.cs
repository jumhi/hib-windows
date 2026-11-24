// H.I.B Roam (docs/SPACE.md §12.2): keys must reach the target, or say why. The key path is counted on both sides —
// captured / sent / received / injected / unmapped (and SendInput failures) — and written to the log once per 10 s,
// without contents: only counts, plus the codes of keys that have no mapping. Never characters.
// The UIPI guard decides when to tell the user that the app in front runs as administrator (Windows silently drops a
// normal app's input there): once per app.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace HIB.Core.Roam;

public sealed class RoamKeyStats
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(10);
    const int MaxCodes = 24;

    long _captured, _sent, _received, _injected, _unmapped, _failed;
    readonly object _gate = new();
    readonly List<string> _codes = new();
    DateTime _since = DateTime.MinValue;

    public void Captured() => Interlocked.Increment(ref _captured);
    public void Sent() => Interlocked.Increment(ref _sent);
    public void Received() => Interlocked.Increment(ref _received);
    public void Injected() => Interlocked.Increment(ref _injected);
    public void Failed() => Interlocked.Increment(ref _failed);

    /// <summary>A key with no mapping: counted, and its code (never a character) kept for the next line.</summary>
    public void Unmapped(string code)
    {
        Interlocked.Increment(ref _unmapped);
        lock (_gate)
        {
            if (_codes.Count < MaxCodes && !_codes.Contains(code)) _codes.Add(code);
        }
    }

    public static string UsageCode(int usage) => "usage 0x" + usage.ToString("X", CultureInfo.InvariantCulture);
    public static string WindowsCode(int vk, int scan, bool extended)
        => $"vk 0x{vk:X2} sc 0x{scan:X2}{(extended ? " ext" : "")}";

    public (long Captured, long Sent, long Received, long Injected, long Unmapped, long Failed) Totals
        => (Interlocked.Read(ref _captured), Interlocked.Read(ref _sent), Interlocked.Read(ref _received),
            Interlocked.Read(ref _injected), Interlocked.Read(ref _unmapped), Interlocked.Read(ref _failed));

    /// <summary>
    /// Once <see cref="Every"/> has passed since the last line: the counts of that period (and resets them), or null when
    /// nothing happened or it isn't time yet.
    /// </summary>
    public string? Flush(DateTime now)
    {
        if (_since == DateTime.MinValue) _since = now;
        if (now - _since < Every) return null;
        _since = now;
        var c = Interlocked.Exchange(ref _captured, 0);
        var s = Interlocked.Exchange(ref _sent, 0);
        var r = Interlocked.Exchange(ref _received, 0);
        var i = Interlocked.Exchange(ref _injected, 0);
        var u = Interlocked.Exchange(ref _unmapped, 0);
        var f = Interlocked.Exchange(ref _failed, 0);
        List<string> codes;
        lock (_gate)
        {
            codes = new List<string>(_codes);
            _codes.Clear();
        }
        if (c + s + r + i + u + f == 0) return null;
        var line = new StringBuilder("Roam keys (10 s): ")
            .Append(CultureInfo.InvariantCulture, $"captured {c} · sent {s} · received {r} · injected {i} · unmapped {u}");
        if (f > 0) line.Append(CultureInfo.InvariantCulture, $" · failed {f}");
        if (codes.Count > 0) line.Append(" · unmapped: ").Append(string.Join(", ", codes));
        return line.ToString();
    }
}

/// <summary>
/// SPACE.md §12.2 UIPI: before injecting a key or a click, look at the window in front. When its process runs above
/// H.I.B's integrity level (as administrator, and H.I.B isn't), Windows drops what we inject without a word: say so
/// once per app ("The app in front runs as administrator — H.I.B can't type into it") and log it.
/// </summary>
public sealed class UipiGuard
{
    public const string PillText = "The app in front runs as administrator — H.I.B can't type into it";

    readonly HashSet<string> _told = new(StringComparer.OrdinalIgnoreCase);
    readonly object _gate = new();

    /// <summary>Input to <paramref name="front"/> is dropped by Windows.</summary>
    public static bool Blocks(RoamForeground? front) => front is { Own: false, Elevated: true };

    /// <summary>The app to tell the user about now (the first time it blocks us), else null.</summary>
    public string? Check(RoamForeground? front)
    {
        if (!Blocks(front)) return null;
        var app = string.IsNullOrWhiteSpace(front!.App) ? $"process {front.ProcessId}" : front.App;
        lock (_gate) return _told.Add(app) ? app : null;
    }
}
