// H.I.B Phone Remote (docs/SPACE.md §15): every decision of the screen stream as small pure pieces, so they are tested
// on any computer — the 22-byte frame header and its encryption, close codes, who may open /screen, the adaptive
// ladder, the 2-frame send queue, ping silence, the keyframe clock, locked → paused → resumed, the view crop and the
// cursor's place in it, H.264 Annex-B config splitting, Tailscale addresses, focus → keyboard, managed PCs and the
// keep-awake rule. The Windows parts (capture, encode, UI Automation, WTS) stay thin and only feed these.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using HIB.Core.Link;

namespace HIB.Core.Remote;

// ---------------------------------------------------------------- framing (§15.3)

/// <summary>What a binary frame carries.</summary>
public enum ScreenFrameKind : byte
{
    /// <summary>H.264 SPS + PPS, Annex-B.</summary>
    Config = 1,
    Keyframe = 2,
    Frame = 3,
    Jpeg = 4,
}

/// <summary>
/// The 22-byte little-endian header in front of every binary payload: <c>u8 kind · u32 seq · u64 ptsMicros ·
/// f32 cursorX · f32 cursorY · u8 cursorVisible</c>. Cursor fractions are of the encoded view (may be outside 0…1).
/// </summary>
public readonly record struct ScreenFrameHeader(ScreenFrameKind Kind, uint Seq, ulong PtsMicros, float CursorX, float CursorY, bool CursorVisible)
{
    public const int Size = 22;

    public void Write(Span<byte> dst)
    {
        if (dst.Length < Size) throw new ArgumentException("Header needs 22 bytes", nameof(dst));
        dst[0] = (byte)Kind;
        BinaryPrimitives.WriteUInt32LittleEndian(dst[1..], Seq);
        BinaryPrimitives.WriteUInt64LittleEndian(dst[5..], PtsMicros);
        BinaryPrimitives.WriteSingleLittleEndian(dst[13..], CursorX);
        BinaryPrimitives.WriteSingleLittleEndian(dst[17..], CursorY);
        dst[21] = CursorVisible ? (byte)1 : (byte)0;
    }

    public static bool TryRead(ReadOnlySpan<byte> src, out ScreenFrameHeader header)
    {
        header = default;
        if (src.Length < Size || src[0] is < 1 or > 4 || src[21] > 1) return false;
        header = new ScreenFrameHeader((ScreenFrameKind)src[0],
            BinaryPrimitives.ReadUInt32LittleEndian(src[1..]),
            BinaryPrimitives.ReadUInt64LittleEndian(src[5..]),
            BinaryPrimitives.ReadSingleLittleEndian(src[13..]),
            BinaryPrimitives.ReadSingleLittleEndian(src[17..]),
            src[21] == 1);
        return true;
    }
}

public static class ScreenFraming
{
    /// <summary>header ‖ payload (the plaintext of one binary frame).</summary>
    public static byte[] Plaintext(in ScreenFrameHeader header, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[ScreenFrameHeader.Size + payload.Length];
        header.Write(buf);
        payload.CopyTo(buf.AsSpan(ScreenFrameHeader.Size));
        return buf;
    }

    /// <summary>The binary WebSocket frame: raw AES-256-GCM <c>nonce12‖ct‖tag16</c> of header ‖ payload.</summary>
    public static byte[] Seal(in ScreenFrameHeader header, ReadOnlySpan<byte> payload, byte[] key)
        => LinkCrypto.EncryptBytes(Plaintext(header, payload), key);

    /// <summary>The phone's side (and tests): decrypts and splits a binary frame.</summary>
    public static bool TryOpen(ReadOnlySpan<byte> frame, byte[] key, out ScreenFrameHeader header, out byte[] payload)
    {
        header = default;
        payload = Array.Empty<byte>();
        var plain = LinkCrypto.TryDecryptBytes(frame, key);
        if (plain is null || !ScreenFrameHeader.TryRead(plain, out header)) return false;
        payload = plain.AsSpan(ScreenFrameHeader.Size).ToArray();
        return true;
    }
}

/// <summary>§15.3 close codes.</summary>
public static class RemoteCloseCodes
{
    public const int BadKey = 4003;
    public const int NotPermitted = 4010;
    /// <summary>Viewing turned off, or Stop pressed (refused for 60 s after Stop).</summary>
    public const int Stopped = 4012;
    public const int Busy = 4013;
}

// ---------------------------------------------------------------- who may open /screen

public enum ScreenAdmission { Accept, ReplaceSamePhone, RefuseOff, RefuseStopped, RefuseBusy }

public static class RemoteAdmission
{
    /// <summary>
    /// One session per phone per computer. A second open from the address already viewing is that phone coming back
    /// (its old socket is dead or about to be), so it replaces the old one; from another address it is another phone (4013).
    /// </summary>
    public static ScreenAdmission Decide(bool allowed, bool stopRefusing, string? activeRemote, string remote)
    {
        if (!allowed) return ScreenAdmission.RefuseOff;
        if (stopRefusing) return ScreenAdmission.RefuseStopped;
        if (activeRemote is null) return ScreenAdmission.Accept;
        return string.Equals(activeRemote, remote, StringComparison.Ordinal) ? ScreenAdmission.ReplaceSamePhone : ScreenAdmission.RefuseBusy;
    }

    public static int CloseCode(ScreenAdmission a) => a switch
    {
        ScreenAdmission.RefuseBusy => RemoteCloseCodes.Busy,
        _ => RemoteCloseCodes.Stopped,
    };
}

/// <summary>After Stop, new /screen opens are refused for 60 s (4012).</summary>
public sealed class RemoteStopGate
{
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(60);
    DateTime? _stoppedAt;

    public void Stop(DateTime now) => _stoppedAt = now;
    public void Clear() => _stoppedAt = null;
    public bool IsRefusing(DateTime now) => _stoppedAt is { } t && now - t < Hold;
}

// ---------------------------------------------------------------- timing

/// <summary>"Socket closed or no screenPing for 5 s ends the session."</summary>
public sealed class PingWatch
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    readonly TimeSpan _timeout;
    DateTime _last;

    public PingWatch(DateTime now, TimeSpan? timeout = null)
    {
        _last = now;
        _timeout = timeout ?? DefaultTimeout;
    }

    public void Seen(DateTime now) => _last = now;
    public bool IsSilent(DateTime now) => now - _last >= _timeout;
}

/// <summary>A keyframe every 2 s (and on request).</summary>
public sealed class KeyframeClock
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    DateTime _last;

    public KeyframeClock(DateTime now) => _last = now;
    public void Emitted(DateTime now) => _last = now;
    public bool Due(DateTime now) => now - _last >= Interval;
}

// ---------------------------------------------------------------- adaptive quality (§15.3)

/// <summary>
/// Start 8 Mbps / 60 fps on the LAN, 3 Mbps / 30 fps over Tailscale. When the socket falls behind (a dropped frame),
/// step the bitrate down ×0.7 to a 1 Mbps floor, then the fps 60 → 30 → 15; step back up (fps first, then bitrate ÷0.7)
/// after 5 s without drops, one step per 5 s. Steps down are at most one per 500 ms so a burst of drops is one step.
/// JPEG (≤ 10 fps) only steps its fps: 10 → 5.
/// </summary>
public sealed class AdaptiveLadder
{
    public const int LanBitrate = 8_000_000;
    public const int TailscaleBitrate = 3_000_000;
    public const int FloorBitrate = 1_000_000;
    public const double DownFactor = 0.7;
    public static readonly TimeSpan UpAfter = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DownCooldown = TimeSpan.FromMilliseconds(500);
    static readonly int[] H264Fps = { 60, 30, 15 };
    static readonly int[] JpegFps = { 10, 5 };

    readonly int[] _steps;
    DateTime _lastDrop;
    DateTime _lastDown = DateTime.MinValue;
    DateTime _lastUp;

    public AdaptiveLadder(bool tailscale, int requestedFps, DateTime now, bool jpeg = false)
    {
        Jpeg = jpeg;
        MaxBitrate = tailscale ? TailscaleBitrate : LanBitrate;
        var cap = jpeg ? 10 : tailscale ? 30 : 60;
        if (requestedFps > 0) cap = Math.Min(cap, requestedFps);
        _steps = (jpeg ? JpegFps : H264Fps).Where(f => f <= cap).ToArray();
        if (_steps.Length == 0) _steps = new[] { Math.Max(1, cap) };
        MaxFps = _steps[0];
        Bitrate = MaxBitrate;
        Fps = MaxFps;
        _lastDrop = now;
        _lastUp = now;
    }

    public bool Jpeg { get; }
    public int Bitrate { get; private set; }
    public int Fps { get; private set; }
    public int MaxBitrate { get; }
    public int MaxFps { get; }

    /// <summary>A frame was dropped because the socket is behind. True when the rate changed.</summary>
    public bool OnDrop(DateTime now)
    {
        _lastDrop = now;
        if (now - _lastDown < DownCooldown) return false;
        if (!Jpeg && Bitrate > FloorBitrate)
            Bitrate = Math.Max(FloorBitrate, (int)Math.Round(Bitrate * DownFactor));
        else if (Array.IndexOf(_steps, Fps) is var i && i >= 0 && i < _steps.Length - 1)
            Fps = _steps[i + 1];
        else
            return false;
        _lastDown = now;
        return true;
    }

    /// <summary>Called often (every 100 ms is fine). True when the rate stepped back up.</summary>
    public bool Tick(DateTime now)
    {
        var quietSince = _lastDrop > _lastUp ? _lastDrop : _lastUp;
        if (now - quietSince < UpAfter) return false;
        var i = Array.IndexOf(_steps, Fps);
        if (i > 0)
            Fps = _steps[i - 1];
        else if (!Jpeg && Bitrate < MaxBitrate)
            Bitrate = Math.Min(MaxBitrate, (int)Math.Round(Bitrate / DownFactor));
        else
            return false;
        _lastUp = now;
        return true;
    }
}

// ---------------------------------------------------------------- the send queue (never more than 2 frames)

public enum ScreenQueueResult
{
    Queued,
    /// <summary>The socket is behind: an H.264 frame was dropped. Ask for a keyframe and step the ladder down.</summary>
    Dropped,
    /// <summary>Skipped while waiting for the keyframe that follows a drop (nothing more to do).</summary>
    Skipped,
    /// <summary>JPEG: the oldest waiting image was replaced by the newest (step the ladder down).</summary>
    Replaced,
}

/// <summary>One frame waiting to go out: its kind, payload and cursor (fractions of the encoded view).</summary>
public sealed record QueuedScreenFrame(ScreenFrameKind Kind, byte[] Payload, ulong PtsMicros, float CursorX, float CursorY, bool CursorVisible);

/// <summary>
/// §15.3: the server never holds more than 2 frames. H.264 frames depend on the ones before them, so a dropped frame
/// means skipping every frame up to the next keyframe (which the caller asks for at once); a keyframe supersedes every
/// frame still waiting; a config (new size) supersedes everything. JPEG images are independent: the newest wins.
/// Not thread-safe on its own.
/// </summary>
public sealed class ScreenSendQueue
{
    public const int MaxFrames = 2;
    readonly List<QueuedScreenFrame> _items = new();
    bool _waitKey;

    public int Count => _items.Count;
    public bool WaitingForKeyframe => _waitKey;

    int Pictures => _items.Count(f => f.Kind != ScreenFrameKind.Config);

    public ScreenQueueResult Offer(QueuedScreenFrame f)
    {
        switch (f.Kind)
        {
            case ScreenFrameKind.Config:
                _items.Clear();
                _items.Add(f);
                _waitKey = true; // frames between a config and its keyframe are useless
                return ScreenQueueResult.Queued;
            case ScreenFrameKind.Keyframe:
                _items.RemoveAll(x => x.Kind != ScreenFrameKind.Config);
                _items.Add(f);
                _waitKey = false;
                return ScreenQueueResult.Queued;
            case ScreenFrameKind.Frame:
                if (_waitKey) return ScreenQueueResult.Skipped;
                if (Pictures >= MaxFrames)
                {
                    _waitKey = true;
                    return ScreenQueueResult.Dropped;
                }
                _items.Add(f);
                return ScreenQueueResult.Queued;
            default: // Jpeg
                if (Pictures >= MaxFrames)
                {
                    var oldest = _items.FindIndex(x => x.Kind == ScreenFrameKind.Jpeg);
                    if (oldest >= 0) _items.RemoveAt(oldest);
                    _items.Add(f);
                    return ScreenQueueResult.Replaced;
                }
                _items.Add(f);
                return ScreenQueueResult.Queued;
        }
    }

    public bool TryDequeue(out QueuedScreenFrame frame)
    {
        if (_items.Count == 0)
        {
            frame = null!;
            return false;
        }
        frame = _items[0];
        _items.RemoveAt(0);
        return true;
    }

    public void Clear()
    {
        _items.Clear();
        _waitKey = false;
    }
}

// ---------------------------------------------------------------- locked → paused → resumed

public enum ScreenTransition { None, Paused, Resumed }

/// <summary>
/// §15.3 "Locked": no video while locked. Paused when the computer reports itself locked or capture is refused (the
/// secure desktop: lock screen, a UAC prompt); resumed when neither holds any more (then config + keyframe follow).
/// </summary>
public sealed class ScreenLockState
{
    bool _osLocked;
    bool _captureBlocked;

    public bool Paused => _osLocked || _captureBlocked;
    public bool OsLocked => _osLocked;

    public ScreenTransition SetOsLocked(bool locked) => Apply(() => _osLocked = locked);
    public ScreenTransition SetCaptureBlocked(bool blocked) => Apply(() => _captureBlocked = blocked);

    ScreenTransition Apply(Action change)
    {
        var before = Paused;
        change();
        if (before == Paused) return ScreenTransition.None;
        return Paused ? ScreenTransition.Paused : ScreenTransition.Resumed;
    }
}

// ---------------------------------------------------------------- view, crop and cursor

/// <summary>A pixel rectangle.</summary>
public readonly record struct PixelBox(int X, int Y, int W, int H)
{
    public bool IsEmpty => W <= 0 || H <= 0;
    public int Right => X + W;
    public int Bottom => Y + H;
}

/// <summary>The visible region as fractions of the display (screenOpen.view / screenView).</summary>
public readonly record struct RemoteView(double X, double Y, double W, double H)
{
    public static readonly RemoteView Whole = new(0, 0, 1, 1);
}

public static class RemoteViewMath
{
    /// <summary>The smallest view: 1/64 of the display (zoom never goes further than the phone's 6× anyway).</summary>
    public const double MinFraction = 1.0 / 64;
    public const int MaxEncodeSide = 4096;
    public const int MinEncodeSide = 16;

    public static RemoteView Clamp(RemoteView v)
    {
        static double Fin(double d, double fallback) => double.IsFinite(d) ? d : fallback;
        var w = Math.Clamp(Fin(v.W, 1), MinFraction, 1);
        var h = Math.Clamp(Fin(v.H, 1), MinFraction, 1);
        var x = Math.Clamp(Fin(v.X, 0), 0, 1 - w);
        var y = Math.Clamp(Fin(v.Y, 0), 0, 1 - h);
        return new RemoteView(x, y, w, h);
    }

    /// <summary>The view as pixels of a display of this size (relative to the display; even sizes).</summary>
    public static PixelBox Crop(RemoteView view, int displayW, int displayH)
    {
        var v = Clamp(view);
        if (displayW <= 0 || displayH <= 0) return default;
        var w = Even(Math.Max(Math.Min(MinEncodeSide, displayW), (int)Math.Round(v.W * displayW)));
        var h = Even(Math.Max(Math.Min(MinEncodeSide, displayH), (int)Math.Round(v.H * displayH)));
        w = Math.Min(w, displayW - displayW % 2);
        h = Math.Min(h, displayH - displayH % 2);
        var x = Math.Clamp((int)Math.Round(v.X * displayW), 0, displayW - w);
        var y = Math.Clamp((int)Math.Round(v.Y * displayH), 0, displayH - h);
        return new PixelBox(x, y, w, h);
    }

    /// <summary>
    /// The encoded size: the crop fitted inside maxW × maxH keeping its shape, never larger than the crop itself (no
    /// upscaling), even, at least 16 and at most 4096 a side. maxW/maxH ≤ 0 mean "no limit".
    /// </summary>
    public static (int W, int H) OutputSize(int cropW, int cropH, int maxW, int maxH)
    {
        if (cropW <= 0 || cropH <= 0) return (0, 0);
        double scale = 1;
        if (maxW > 0) scale = Math.Min(scale, (double)maxW / cropW);
        if (maxH > 0) scale = Math.Min(scale, (double)maxH / cropH);
        scale = Math.Min(scale, Math.Min((double)MaxEncodeSide / cropW, (double)MaxEncodeSide / cropH));
        var w = Even(Math.Max(MinEncodeSide, (int)Math.Floor(cropW * scale)));
        var h = Even(Math.Max(MinEncodeSide, (int)Math.Floor(cropH * scale)));
        return (Math.Min(w, MaxEncodeSide), Math.Min(h, MaxEncodeSide));
    }

    /// <summary>The cursor (desktop pixels) as fractions of the encoded view: may be outside 0…1 when it is off the view.</summary>
    public static (float X, float Y) CursorFraction(double cursorX, double cursorY, PixelBox display, PixelBox crop)
    {
        if (crop.IsEmpty) return (float.NaN, float.NaN);
        return ((float)((cursorX - display.X - crop.X) / crop.W), (float)((cursorY - display.Y - crop.Y) / crop.H));
    }

    static int Even(int v) => v - (v & 1);
}

/// <summary>DXGI output rotation (the desktop image must be turned this far clockwise to look right).</summary>
public enum RemoteRotation { None = 0, Cw90 = 90, Cw180 = 180, Cw270 = 270 }

public static class RemoteRotationMath
{
    /// <summary>
    /// A rectangle of the display as the user sees it (desktop pixels relative to the display, size dw × dh) in the
    /// coordinates of the duplicated texture, which Windows keeps unrotated.
    /// </summary>
    public static PixelBox ToSource(PixelBox r, int dw, int dh, RemoteRotation rotation) => rotation switch
    {
        RemoteRotation.Cw90 => new PixelBox(r.Y, dw - r.Right, r.H, r.W),
        RemoteRotation.Cw180 => new PixelBox(dw - r.Right, dh - r.Bottom, r.W, r.H),
        RemoteRotation.Cw270 => new PixelBox(dh - r.Bottom, r.X, r.H, r.W),
        _ => r,
    };

    /// <summary>A point of the display (as seen) to a point of the unrotated texture.</summary>
    public static (double X, double Y) PointToSource(double x, double y, int dw, int dh, RemoteRotation rotation) => rotation switch
    {
        RemoteRotation.Cw90 => (y, dw - x),
        RemoteRotation.Cw180 => (dw - x, dh - y),
        RemoteRotation.Cw270 => (dh - y, x),
        _ => (x, y),
    };
}

// ---------------------------------------------------------------- H.264 Annex-B

public static class AnnexB
{
    public const int NalIdr = 5, NalSps = 7, NalPps = 8;
    static readonly byte[] StartCode = { 0, 0, 0, 1 };

    /// <summary>The NAL units of an Annex-B access unit (payload without its start code) and their types.</summary>
    public static List<(int Offset, int Length, int Type)> Nals(ReadOnlySpan<byte> au)
    {
        var starts = new List<(int CodeAt, int DataAt)>();
        for (var i = 0; i + 2 < au.Length; i++)
        {
            if (au[i] != 0 || au[i + 1] != 0) continue;
            if (au[i + 2] == 1)
            {
                starts.Add((i > 0 && au[i - 1] == 0 ? i - 1 : i, i + 3));
                i += 2;
            }
        }
        var nals = new List<(int, int, int)>(starts.Count);
        for (var n = 0; n < starts.Count; n++)
        {
            var at = starts[n].DataAt;
            var end = n + 1 < starts.Count ? starts[n + 1].CodeAt : au.Length;
            if (end > at) nals.Add((at, end - at, au[at] & 0x1F));
        }
        return nals;
    }

    /// <summary>
    /// Splits an access unit into its SPS + PPS (the §15.3 config frame, null when it has none) and the rest, with
    /// 4-byte start codes; IsKey when it holds an IDR slice.
    /// </summary>
    public static (byte[]? Config, byte[] Body, bool IsKey) Split(ReadOnlySpan<byte> au)
    {
        var nals = Nals(au);
        if (nals.Count == 0) return (null, au.ToArray(), false);
        var config = new List<byte>();
        var rest = new List<byte>(au.Length);
        var key = false;
        foreach (var (offset, length, type) in nals)
        {
            var target = type is NalSps or NalPps ? config : rest;
            target.AddRange(StartCode);
            target.AddRange(au.Slice(offset, length).ToArray());
            if (type == NalIdr) key = true;
        }
        return (config.Count > 0 ? config.ToArray() : null, rest.ToArray(), key);
    }
}

/// <summary>
/// A config frame goes out before the first keyframe of a session, after a resume or a new size, and whenever the
/// encoder's SPS/PPS change; not before every periodic keyframe.
/// </summary>
public sealed class ScreenConfigTracker
{
    byte[]? _sent;
    bool _force = true;

    public byte[]? Latest { get; private set; }

    /// <summary>Next keyframe must carry a config (start, resume, display or size change).</summary>
    public void Reset() => _force = true;

    /// <summary>The config to send before this keyframe, or null.</summary>
    public byte[]? ForKeyframe(byte[]? configInFrame)
    {
        if (configInFrame is not null) Latest = configInFrame;
        if (Latest is null) return null;
        if (!_force && _sent is not null && _sent.AsSpan().SequenceEqual(Latest)) return null;
        _force = false;
        _sent = Latest;
        return Latest;
    }
}

// ---------------------------------------------------------------- Tailscale (§15.1 addrs)

public static class TailscaleAddresses
{
    /// <summary>Inside 100.64.0.0/10 (CGNAT, Tailscale IPv4) or fd7a:115c:a1e0::/48 (Tailscale IPv6).</summary>
    public static bool IsTailscale(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] == 100 && (b[1] & 0xC0) == 64;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return b[0] == 0xFD && b[1] == 0x7A && b[2] == 0x11 && b[3] == 0x5C && b[4] == 0xA1 && b[5] == 0xE0;
        return false;
    }

    /// <summary>The Tailscale ones, IPv4 first, each once, without a scope id.</summary>
    public static IReadOnlyList<string> Filter(IEnumerable<IPAddress> addresses)
        => addresses.Where(IsTailscale)
            .Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a)
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .Select(a => (a.AddressFamily == AddressFamily.InterNetworkV6 ? new IPAddress(a.GetAddressBytes()) : a).ToString())
            .Distinct()
            .ToList();

    /// <summary>This computer's Tailscale addresses (interfaces that are up), or none.</summary>
    public static IReadOnlyList<string> Local()
    {
        try
        {
            var all = new List<IPAddress>();
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses) all.Add(ua.Address);
            }
            return Filter(all);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}

// ---------------------------------------------------------------- focus → keyboard (§15.1 remoteFocus)

/// <summary>UI Automation control type ids used by the focus rules.</summary>
public static class UiaControlType
{
    public const int ComboBox = 50003;
    public const int Edit = 50004;
    public const int Spinner = 50016;
    public const int Custom = 50025;
    public const int Document = 50030;
}

/// <summary>What UI Automation says about the focused element.</summary>
public readonly record struct FocusFacts(int ControlType, bool IsPassword, bool ValuePattern, bool ValueReadOnly, bool TextPattern);

/// <summary>A <c>remoteFocus</c> payload.</summary>
public readonly record struct RemoteFocusState(bool Text, bool Secure)
{
    public static readonly RemoteFocusState None = new(false, false);
}

public static class RemoteFocusRules
{
    /// <summary>
    /// Text when the focused element is an Edit or a Document, an editable ComboBox, has a writable Value, or is a custom
    /// control with a Text pattern (web and Electron editors); never when its Value is read-only. Secure for passwords.
    /// </summary>
    public static RemoteFocusState Classify(FocusFacts? facts)
    {
        if (facts is not { } f) return RemoteFocusState.None;
        if (f.IsPassword) return new RemoteFocusState(true, true);
        var readOnly = f.ValuePattern && f.ValueReadOnly;
        var text = !readOnly && (f.ControlType switch
        {
            UiaControlType.Edit or UiaControlType.Document => true,
            UiaControlType.ComboBox => f.ValuePattern,
            UiaControlType.Custom => f.ValuePattern || f.TextPattern,
            _ => f.ValuePattern,
        });
        return new RemoteFocusState(text, false);
    }
}

/// <summary>remoteFocus goes out on change, debounced 150 ms (focus often hops through a few elements).</summary>
public sealed class FocusDebouncer
{
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(150);
    RemoteFocusState? _pending;
    DateTime _pendingAt;
    RemoteFocusState? _sent;

    public void Offer(RemoteFocusState state, DateTime now)
    {
        if (_pending == state) return;
        _pending = state;
        _pendingAt = now;
    }

    /// <summary>The state to send now, or null.</summary>
    public RemoteFocusState? Poll(DateTime now)
    {
        if (_pending is not { } p || now - _pendingAt < Delay) return null;
        _pending = null;
        if (_sent == p) return null;
        _sent = p;
        return p;
    }

    /// <summary>A new session: the next state is sent even if it equals the last one.</summary>
    public void Reset()
    {
        _sent = null;
        _pending = null;
    }
}

// ---------------------------------------------------------------- unlock on Windows (phase 1) and staying awake

public static class RemoteUnlockAvailability
{
    public const string Managed = "managed";
    public const string Unsupported = "unsupported";

    /// <summary>NetGetJoinInformation's NETSETUP_JOIN_STATUS: 3 = NetSetupDomainName (joined to a domain).</summary>
    public static bool IsDomainJoined(int netSetupJoinStatus) => netSetupJoinStatus == 3;

    /// <summary>NetGetAadJoinInformation's DSREG_JOIN_TYPE: 1 = DSREG_DEVICE_JOIN (Entra joined); 2 (a work account added) is not.</summary>
    public static bool IsEntraJoined(int? dsregJoinType) => dsregJoinType == 1;

    /// <summary>Phase 1: no sign-in add-on, so Windows is "managed" when domain- or Entra-joined, else "unsupported".</summary>
    public static string ForWindows(bool domainJoined, bool entraJoined) => domainJoined || entraJoined ? Managed : Unsupported;
}

[Flags]
public enum RemoteAwake
{
    None = 0,
    /// <summary>No idle system sleep (the display may sleep).</summary>
    System = 1,
    Display = 2,
}

public static class RemoteAwakePolicy
{
    const uint EsContinuous = 0x80000000, EsSystemRequired = 0x1, EsDisplayRequired = 0x2;

    /// <summary>
    /// While a /screen session is open: system + display (just system while locked: there's nothing to show). "Stay
    /// reachable when locked": system only, while locked and on mains power.
    /// </summary>
    public static RemoteAwake Need(bool sessionOpen, bool stayReachable, bool locked, bool onMains)
    {
        if (sessionOpen) return locked ? RemoteAwake.System : RemoteAwake.System | RemoteAwake.Display;
        return stayReachable && locked && onMains ? RemoteAwake.System : RemoteAwake.None;
    }

    /// <summary>SetThreadExecutionState flags (ES_CONTINUOUS alone clears it).</summary>
    public static uint ExecutionState(RemoteAwake need)
        => EsContinuous | ((need & RemoteAwake.System) != 0 ? EsSystemRequired : 0) | ((need & RemoteAwake.Display) != 0 ? EsDisplayRequired : 0);

    /// <summary>GetSystemPowerStatus: AC online (1), or no battery at all (flag 128) — a desktop PC — counts as mains; unknown (255) with a battery does not.</summary>
    public static bool OnMains(byte acLineStatus, byte batteryFlag) => acLineStatus == 1 || (acLineStatus != 0 && (batteryFlag & 128) != 0);
}
