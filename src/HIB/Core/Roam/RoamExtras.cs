// H.I.B Roam (docs/SPACE.md §12.3, §12.5): the small rules round 2 adds — fractional motion and wheel deltas carried
// over (smooth sub-notch scrolling), per-computer pointer and scroll speed with natural scrolling, clipboard images on
// crossing, "lock together" without echoes, and the link latency readout.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HIB.Core.Link;

namespace HIB.Core.Roam;

/// <summary>Adds fractional deltas and hands out the whole part, carrying the rest to the next one (nothing is lost, nothing stepped).</summary>
public sealed class RoamDelta
{
    double _x, _y;

    public (int X, int Y) Add(double dx, double dy)
    {
        if (!double.IsFinite(dx)) dx = 0;
        if (!double.IsFinite(dy)) dy = 0;
        _x += dx;
        _y += dy;
        var ix = (int)Math.Clamp(Math.Truncate(_x), -100_000, 100_000);
        var iy = (int)Math.Clamp(Math.Truncate(_y), -100_000, 100_000);
        _x -= ix;
        _y -= iy;
        return (ix, iy);
    }

    public void Reset() => _x = _y = 0;
}

/// <summary>§12.5 per-computer speeds (Devices row sliders, 0.5×–2×, default 1×), applied by the controller.</summary>
public static class RoamSpeed
{
    public const double Min = 0.5, Max = 2, Default = 1;

    public static double Clamp(double v) => double.IsFinite(v) && v > 0 ? Math.Clamp(Math.Round(v, 2), Min, Max) : Default;

    /// <summary>ctlWheel deltas for a target: scaled by its scroll speed, both axes reversed with natural scrolling.</summary>
    public static (double Dx, double Dy) Wheel(double dx, double dy, double speed, bool natural)
    {
        var k = Clamp(speed) * (natural ? -1 : 1);
        return (dx * k, dy * k);
    }

    /// <summary>"1×", "1.5×", "0.75×".</summary>
    public static string Label(double v) => Clamp(v).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×";
}

/// <summary>
/// §9.4 / §12.5 what a crossing carries: <c>{"text":…}</c> or <c>{"png":"&lt;base64&gt;"}</c>. Text wins when there is any
/// (as with the phone's clipboard sync); otherwise an image, PNG ≤ 8 MB.
/// </summary>
public sealed record RoamClip(string? Text, byte[]? Png)
{
    public const int MaxTextBytes = RoamWire.MaxClipboardBytes;
    public const int MaxPngBytes = 8 * 1024 * 1024;

    public static RoamClip? Pick(string? text, byte[]? png)
    {
        if (!string.IsNullOrEmpty(text)) return Encoding.UTF8.GetByteCount(text) <= MaxTextBytes ? new RoamClip(text, null) : null;
        if (png is { Length: > 0 } && png.Length <= MaxPngBytes) return new RoamClip(null, png);
        return null;
    }

    /// <summary>What it is, to send it only when it changed since the last crossing.</summary>
    public string Signature => Text is { } t ? "t:" + Hash(Encoding.UTF8.GetBytes(t)) : "p:" + Hash(Png ?? Array.Empty<byte>());

    static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    public JsonObject ToWire() => Text is { } t ? new JsonObject { ["text"] = t } : new JsonObject { ["png"] = Convert.ToBase64String(Png ?? Array.Empty<byte>()) };

    /// <summary>A crossing's <c>clipboard</c>, or null (none, unreadable, or over the limits).</summary>
    public static RoamClip? Parse(JsonElement data)
    {
        if (data.Obj("clipboard") is not { } c) return null;
        if (c.Str("text") is { } text) return Encoding.UTF8.GetByteCount(text) <= MaxTextBytes ? new RoamClip(text, null) : null;
        if (c.Str("png") is not { Length: > 0 } b64 || b64.Length > MaxPngBytes / 3 * 4 + 8) return null;
        try
        {
            var png = Convert.FromBase64String(b64);
            return png.Length is > 8 and <= MaxPngBytes && png[0] == 0x89 && png[1] == (byte)'P' && png[2] == (byte)'N' && png[3] == (byte)'G'
                ? new RoamClip(null, png) : null;
        }
        catch (FormatException) { return null; }
    }
}

/// <summary>
/// §12.5 lock together: locking one computer's screen locks the others (ctlScreenLock); unlocking unlocks nothing. A lock that
/// came from another computer isn't sent back (5 s), and a computer already locked doesn't lock again.
/// </summary>
public sealed class LockTogether
{
    public static readonly TimeSpan Echo = TimeSpan.FromSeconds(5);
    DateTime _remoteAt = DateTime.MinValue;

    public bool Locked { get; private set; }

    /// <summary>This computer's session locked (true) or unlocked: true when the others should be told to lock.</summary>
    public bool OnLocal(bool locked, bool enabled, DateTime now)
    {
        Locked = locked;
        return locked && enabled && now - _remoteAt > Echo;
    }

    /// <summary>ctlScreenLock arrived: true when this computer should lock now.</summary>
    public bool OnRemote(bool enabled, DateTime now)
    {
        if (!enabled || Locked) return false;
        _remoteAt = now;
        return true;
    }
}

/// <summary>§12.3 link latency: the median of the last 20 ping round trips.</summary>
public sealed class LatencyMeter
{
    public const int Samples = 20;
    /// <summary>Above this the Devices row adds the Wi-Fi tip.</summary>
    public const double TipAboveMs = 25;
    public const string Tip = "Wi-Fi is adding delay — 5 GHz or a cable to one computer helps";

    readonly Queue<double> _rtts = new();
    readonly object _gate = new();

    public void Add(double ms)
    {
        if (!double.IsFinite(ms) || ms < 0) return;
        lock (_gate)
        {
            _rtts.Enqueue(ms);
            while (_rtts.Count > Samples) _rtts.Dequeue();
        }
    }

    public int Count { get { lock (_gate) return _rtts.Count; } }

    /// <summary>The median in whole milliseconds, or null before the first round trip.</summary>
    public int? MedianMs
    {
        get
        {
            double[] all;
            lock (_gate) all = _rtts.ToArray();
            if (all.Length == 0) return null;
            Array.Sort(all);
            var m = all.Length % 2 == 1 ? all[all.Length / 2] : (all[all.Length / 2 - 1] + all[all.Length / 2]) / 2;
            return (int)Math.Round(m, MidpointRounding.AwayFromZero);
        }
    }

    public static string Text(int ms) => $"Latency {ms} ms";
}
