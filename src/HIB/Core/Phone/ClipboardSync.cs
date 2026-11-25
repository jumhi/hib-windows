// H.I.B clipboard sync (docs/SPACE.md §16): the one component every clipboard read and write goes through, so a clip
// keeps its identity (id, ts, origin) on every path — the phone link, Roam crossings (§9.4) and local writes (Flick
// text, login codes) — and the rules hold everywhere: seen ids, newest wins, applying is not copying, catch-up on
// link-up, failed reads and writes retried. Never logs clipboard contents.
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HIB.Core.Link;
using HIB.Core.Pairing;

namespace HIB.Core.Phone;

/// <summary>Platform clipboard access (Avalonia in the app, a fake in tests).</summary>
public interface IClipboardBridge
{
    /// <summary>The clipboard's text, or null when it holds none. Throws when the clipboard can't be read right now
    /// (another app holds it open, no window yet): the caller retries (SPACE.md §16.2).</summary>
    Task<string?> GetTextAsync();
    /// <summary>PNG bytes of the clipboard image, or null. Throws like <see cref="GetTextAsync"/>.</summary>
    Task<byte[]?> GetImagePngAsync();
    Task SetTextAsync(string text);
    Task SetImageAsync(byte[] imageBytes);
    /// <summary>Cheap change counter (Windows GetClipboardSequenceNumber); null if unsupported.</summary>
    uint? ChangeCount { get; }
}

public sealed record ClipboardEntry(bool FromPhone, string? Text, byte[]? Image, DateTimeOffset Time);

/// <summary>One clip and its identity (SPACE.md §16.1): text or PNG, a random id, the origin's copy time (Unix ms) and
/// the origin's §2 id (<c>"phone"</c> for the phone).</summary>
public sealed record Clip(string? Text, byte[]? Png, string Id, long Ts, string Origin)
{
    public bool IsEmpty => string.IsNullOrEmpty(Text) && Png is not { Length: > 0 };

    /// <summary>What it holds (kind + sha256), to tell content apart.</summary>
    public string Sha => ClipIdentity.Sha(Text, Png);

    /// <summary>The first 6 characters of the id, for the log (§16.4).</summary>
    public string ShortId => ClipIdentity.Short(Id);

    public bool TooBig => ClipIdentity.TooBig(Text, Png);
}

public static class ClipIdentity
{
    /// <summary>The phone's origin id (§16.1).</summary>
    public const string Phone = "phone";
    /// <summary>§16.2 limits: text ≤ 1 MB, PNG ≤ 8 000 000 bytes before base64.</summary>
    public const int MaxTextBytes = 1024 * 1024;
    public const int MaxPngBytes = 8_000_000;

    /// <summary>16 random bytes, base64url.</summary>
    public static string NewId() => Base64Url(RandomNumberGenerator.GetBytes(16));

    /// <summary>The id of a clip from an older app that sent none: sha256 of the content, first 16 bytes, base64url.</summary>
    public static string ContentId(string? text, byte[]? png) => Base64Url(SHA256.HashData(Content(text, png)).AsSpan(0, 16));

    public static string Sha(string? text, byte[]? png)
        => (text is not null ? "t:" : "p:") + Convert.ToHexString(SHA256.HashData(Content(text, png)));

    public static bool TooBig(string? text, byte[]? png)
        => text is not null ? Encoding.UTF8.GetByteCount(text) > MaxTextBytes : png is { Length: > MaxPngBytes };

    public static string Short(string id) => id.Length > 6 ? id[..6] : id;

    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// A received clip's identity (§16.1). Missing fields (older versions): id = the content id, ts = receive time,
    /// origin = the sending link's device.
    /// </summary>
    public static Clip FromWire(JsonElement d, string? text, byte[]? png, string fallbackOrigin, long now)
    {
        var id = d.Str("id") is { Length: > 0 and <= 128 } i ? i : ContentId(text, png);
        var ts = d.Long("ts") is { } t && t > 0 ? t : now;
        var origin = d.Str("origin") is { Length: > 0 } o ? o : fallbackOrigin;
        return new Clip(text, png, id, ts, origin);
    }

    static byte[] Content(string? text, byte[]? png) => text is not null ? Encoding.UTF8.GetBytes(text) : png ?? Array.Empty<byte>();

    static string Base64Url(ReadOnlySpan<byte> b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The last <see cref="Capacity"/> clip ids applied or sent (§16.2): a clip seen before is ignored.</summary>
public sealed class SeenIds
{
    public const int Capacity = 64;
    readonly LinkedList<string> _order = new();
    readonly HashSet<string> _set = new(StringComparer.Ordinal);

    public bool Contains(string id) => _set.Contains(id);

    public void Add(string id)
    {
        if (!_set.Add(id)) return;
        _order.AddLast(id);
        while (_order.Count > Capacity)
        {
            _set.Remove(_order.First!.Value);
            _order.RemoveFirst();
        }
    }

    public void Remove(string id)
    {
        if (_set.Remove(id)) _order.Remove(id);
    }

    public int Count => _set.Count;
}

/// <summary>
/// Two-way clipboard sync (SPACE.md §16). Watches the desktop clipboard every 2.5 s (connected or not; the Windows
/// sequence number makes an unchanged clipboard free), gives every user copy a new id, and is the only writer of the
/// clipboard: what it applies is never sent out again as a copy. Text wins over images; text ≤ 1 MB, PNG ≤ 8 000 000.
/// </summary>
public sealed class ClipboardSync : IDisposable
{
    public const int MaxImageBytes = ClipIdentity.MaxPngBytes;
    /// <summary>§16.2 newest wins: an incoming clip is applied only if ts &gt; localTs − this.</summary>
    public const long SkewAllowanceMs = 1500;
    /// <summary>§16.2 catch-up: the current clip goes out on link-up when younger than this.</summary>
    public static readonly TimeSpan CatchUpMaxAge = TimeSpan.FromMinutes(10);
    /// <summary>§16.2: a failed read is retried 5× over ~1 s.</summary>
    public const int ReadAttempts = 5;
    public static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(200);
    /// <summary>The longest one read may take before it counts as failed (a busy UI thread).</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);
    public const int WriteAttempts = 3;
    public static readonly TimeSpan WriteRetryDelay = TimeSpan.FromMilliseconds(150);
    /// <summary>A change that reads back as what we just applied, this soon after, is still ours (the platform may
    /// move its counter again); later it is the user copying the same thing again (§16.2: a new clip).</summary>
    public const long AppliedEchoMs = 2000;

    readonly ILinkSession _link;
    readonly PhoneHub _hub;
    readonly AppSettings _settings;
    readonly IClipboardBridge _clipboard;
    readonly Func<string> _ownId;
    readonly Func<long> _clock;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly TimeSpan _interval;
    readonly SeenIds _seen = new();
    CancellationTokenSource? _cts;

    // All below under _gate.
    Clip? _current;
    string? _currentSha;
    long _localTs;
    string? _appliedSha;
    string? _appliedReadbackSha;
    uint? _appliedMarker;
    long _appliedAt;
    uint? _lastChangeCount;
    bool _baselined;

    public ClipboardSync(ILinkSession link, PhoneHub hub, AppSettings settings, IClipboardBridge clipboard, TimeSpan? interval = null,
        Func<string>? ownId = null, Func<long>? clock = null)
    {
        _link = link;
        _hub = hub;
        _settings = settings;
        _clipboard = clipboard;
        _ownId = ownId ?? (() => link.DesktopName);
        _clock = clock ?? ClipIdentity.NowMs;
        _interval = interval ?? TimeSpan.FromSeconds(2.5);
        hub.ClipReceived += OnPhoneClip;
        link.ConnectionChanged += OnConnectionChanged;
    }

    void OnPhoneClip(Clip c) => _ = ApplyAsync(c, "phone");

    void OnConnectionChanged(object? sender, bool connected)
    {
        if (connected) _ = CatchUpAsync();
    }

    long _localChangeCount;

    /// <summary>Increments whenever the desktop clipboard content changes by a user copy or an applied clip (not by
    /// <see cref="WriteTextAsync"/>: the login-code copier counts its own writes).</summary>
    public long LocalChangeCount => Interlocked.Read(ref _localChangeCount);

    /// <summary>The text on the desktop clipboard as last seen or written.</summary>
    public string? LastLocalText => Volatile.Read(ref _current)?.Text;

    /// <summary>The clip on the desktop clipboard now, as last seen or written (null before the first read).</summary>
    public Clip? Current => Volatile.Read(ref _current);

    public event Action<ClipboardEntry>? EntryAdded;
    public event Action<string>? Log;

    string Own => _ownId();

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    /// <summary>Stops watching (an incoming clip, a crossing or <see cref="PollOnceAsync"/> still look).</summary>
    public void Dispose()
    {
        _cts?.Cancel();
        _cts = null;
    }

    /// <summary>Stops watching and listening for good (the core attached another clipboard).</summary>
    public void Detach()
    {
        Dispose();
        _hub.ClipReceived -= OnPhoneClip;
        _link.ConnectionChanged -= OnConnectionChanged;
    }

    async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            // Always watching (§16.2, no silent baseline): a copy made while the phone is away goes out on link-up.
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { await PollOnceAsync().ConfigureAwait(false); }
                catch (Exception ex) { Say("[clip] poll failed: " + ex.GetType().Name); }
            }
        }
        catch (OperationCanceledException) { }
    }

    void Say(string line)
    {
        try { Log?.Invoke(line); } catch { }
    }

    /// <summary>§16.4: one line per clip event, never the content.</summary>
    void Event(string what, Clip c, string via) => Say($"[clip] {what} id={c.ShortId} from={c.Origin} via={via}");

    // ---------------- watching (user copies) ----------------

    /// <summary>One look at the desktop clipboard (public for tests): a user copy gets a new id and goes to the phone.</summary>
    public async Task PollOnceAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var (_, copied) = await ObserveLockedAsync(ReadAttempts, ReadRetryDelay).ConfigureAwait(false);
            if (copied is not null) await SendLockedAsync(copied).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Reads the clipboard if it may have changed (always when the platform has no counter). A failed read is retried
    /// and never marks the change as handled. Returns whether the read worked, and the new clip when the user copied.
    /// </summary>
    async Task<(bool Ok, Clip? Copied)> ObserveLockedAsync(int attempts, TimeSpan retryDelay, long? deadline = null)
    {
        var count = _clipboard.ChangeCount;
        if (count is not null && _baselined && count == _lastChangeCount) return (true, null);

        string? text = null;
        byte[]? png = null;
        string? failure = null;
        var ok = false;
        for (var i = 0; i < attempts; i++)
        {
            var budget = ReadTimeout;
            if (deadline is { } dl)
            {
                var left = dl - _clock();
                if (left <= 0) break;
                budget = TimeSpan.FromMilliseconds(Math.Min(left, ReadTimeout.TotalMilliseconds));
            }
            try
            {
                text = await _clipboard.GetTextAsync().WaitAsync(budget).ConfigureAwait(false);
                png = null;
                if (string.IsNullOrEmpty(text))
                {
                    text = null;
                    if (_settings.ClipboardImages && _settings.ClipboardSync)
                        png = await _clipboard.GetImagePngAsync().WaitAsync(budget * 3).ConfigureAwait(false);
                }
                ok = true;
                break;
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name;
                if (i < attempts - 1)
                {
                    if (deadline is { } d2 && d2 - _clock() <= retryDelay.TotalMilliseconds) break;
                    try { await Task.Delay(retryDelay).ConfigureAwait(false); } catch { }
                }
            }
        }
        if (!ok)
        {
            // Not handled: the counter stays uncommitted, so the next look reads again.
            Say($"[clip] read failed ({failure ?? "timeout"}), will retry");
            return (false, null);
        }
        if (png is { Length: 0 }) png = null;

        if (text is null && png is null)
        {
            _lastChangeCount = count;
            _baselined = true;
            return (true, null);
        }

        var sha = ClipIdentity.Sha(text, png);
        var now = _clock();
        if (!_baselined)
        {
            // What was already there when H.I.B started: of unknown age, so it is never "newer" and never caught up.
            _baselined = true;
            _lastChangeCount = count;
            SetCurrent(new Clip(text, png, ClipIdentity.NewId(), 0, Own), sha, 0);
            return (true, null);
        }
        _lastChangeCount = count;

        // Applying is not copying (§16.2): what we wrote (or what it reads back as) isn't sent out again.
        var ours = sha == _appliedSha || sha == _appliedReadbackSha;
        if (ours && (count is null || count == _appliedMarker || now - _appliedAt < AppliedEchoMs)) return (true, null);
        // Without a counter only a content change can be seen; with one, copying the same thing again is a new clip.
        if (count is null && sha == _currentSha) return (true, null);

        var clip = new Clip(text, png, ClipIdentity.NewId(), now, Own);
        SetCurrent(clip, sha, now);
        _appliedSha = _appliedReadbackSha = null;
        Event("copied", clip, "local");
        return (true, clip);
    }

    void SetCurrent(Clip clip, string sha, long localTs, bool count = true)
    {
        Volatile.Write(ref _current, clip);
        _currentSha = sha;
        _localTs = localTs;
        if (count) Interlocked.Increment(ref _localChangeCount);
    }

    /// <summary>Sends a clip to the phone (sync on, within the limits, link up). Under the gate.</summary>
    async Task<bool> SendLockedAsync(Clip clip, bool catchUp = false)
    {
        if (!_settings.ClipboardSync) return false;
        if (clip.Png is not null && !_settings.ClipboardImages) return false;
        if (clip.TooBig)
        {
            Event("ignored(size)", clip, "phone");
            return false;
        }
        if (!_link.IsConnected) return false;
        var ok = clip.Text is { } text
            ? await _hub.SendClipboardTextAsync(text, clip).ConfigureAwait(false)
            : await _hub.SendClipboardImageAsync(clip.Png!, clip).ConfigureAwait(false);
        if (!ok) return false;
        _seen.Add(clip.Id);
        Event(catchUp ? "sent(catch-up)" : "sent", clip, "phone");
        if (!catchUp) EntryAdded?.Invoke(new ClipboardEntry(false, clip.Text, clip.Png, DateTimeOffset.Now));
        return true;
    }

    /// <summary>§16.2 catch-up: the link came up; send the current clip if it is real and younger than 10 minutes.</summary>
    public async Task CatchUpAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ObserveLockedAsync(ReadAttempts, ReadRetryDelay).ConfigureAwait(false);
            // Never back to where it came from (a clip from the phone), never one of unknown age.
            if (_current is not { IsEmpty: false } c || string.Equals(c.Origin, ClipIdentity.Phone, StringComparison.OrdinalIgnoreCase) || c.Ts <= 0) return;
            if (_clock() - c.Ts > (long)CatchUpMaxAge.TotalMilliseconds) return;
            await SendLockedAsync(c, catchUp: true).ConfigureAwait(false);
        }
        catch (Exception ex) { Say("[clip] catch-up failed: " + ex.GetType().Name); }
        finally { _gate.Release(); }
    }

    // ---------------- applying (phone, Roam) ----------------

    /// <summary>
    /// An incoming clip (§16.2): ignored when sync is off, too big, already seen or older than what is here; otherwise
    /// written to the clipboard (retried) and remembered so the watcher doesn't send it out again. <paramref name="via"/>
    /// is <c>phone</c> or <c>roam</c> (the log). True when it was applied.
    /// </summary>
    public async Task<bool> ApplyAsync(Clip clip, string via)
    {
        if (clip.IsEmpty) return false;
        if (!_settings.ClipboardSync || (clip.Png is not null && !_settings.ClipboardImages))
        {
            Event("ignored(off)", clip, via);
            return false;
        }
        if (clip.TooBig)
        {
            Event("ignored(size)", clip, via);
            return false;
        }
        bool applied;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_seen.Contains(clip.Id))
            {
                Event("ignored(seen)", clip, via);
                return false;
            }
            // A copy made here a moment ago (not polled yet) is newer than a late clip: look first.
            var (_, copied) = await ObserveLockedAsync(2, TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
            _seen.Add(clip.Id);
            if (clip.Ts <= _localTs - SkewAllowanceMs)
            {
                Event("ignored(older)", clip, via);
                if (copied is not null) await SendLockedAsync(copied).ConfigureAwait(false);
                return false;
            }
            applied = await WriteLockedAsync(clip).ConfigureAwait(false);
            if (!applied)
            {
                _seen.Remove(clip.Id); // a resend may try again
                Say($"[clip] apply failed id={clip.ShortId} from={clip.Origin} via={via}");
                return false;
            }
            Event("applied", clip, via);
        }
        finally { _gate.Release(); }
        if (via == "phone") EntryAdded?.Invoke(new ClipboardEntry(true, clip.Text, clip.Png, DateTimeOffset.Now));
        return applied;
    }

    /// <summary>Writes a clip to the clipboard (retried), then records it as applied. Under the gate.</summary>
    async Task<bool> WriteLockedAsync(Clip clip, bool local = false)
    {
        for (var i = 0; i < WriteAttempts; i++)
        {
            try
            {
                if (clip.Text is { } text) await _clipboard.SetTextAsync(text).ConfigureAwait(false);
                else await _clipboard.SetImageAsync(clip.Png!).ConfigureAwait(false);
                string? readback = null;
                if (clip.Png is not null)
                {
                    // The platform may re-encode the image: remember what it reads back as.
                    try
                    {
                        if (await _clipboard.GetImagePngAsync().WaitAsync(ReadTimeout).ConfigureAwait(false) is { Length: > 0 } back)
                            readback = ClipIdentity.Sha(null, back);
                    }
                    catch { }
                }
                var now = _clock();
                var sha = clip.Sha;
                _appliedSha = sha;
                _appliedReadbackSha = readback;
                _appliedMarker = _clipboard.ChangeCount;
                _appliedAt = now;
                if (_appliedMarker is not null) _lastChangeCount = _appliedMarker;
                _baselined = true;
                // localTs never runs ahead of this clock: a device whose clock is fast can't block newer clips.
                SetCurrent(clip, readback ?? sha, Math.Min(clip.Ts, now), count: !local);
                return true;
            }
            catch (Exception ex)
            {
                if (i == WriteAttempts - 1)
                {
                    Say("[clip] write failed: " + ex.GetType().Name);
                    return false;
                }
                try { await Task.Delay(WriteRetryDelay).ConfigureAwait(false); } catch { }
            }
        }
        return false;
    }

    /// <summary>
    /// A local write that isn't a user copy (Flick text, a login code): applied with a new id from
    /// <paramref name="origin"/>, so the watcher never sends it out, and catch-up skips it when it came from the phone.
    /// </summary>
    public async Task<bool> WriteTextAsync(string text, string origin = ClipIdentity.Phone)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var clip = new Clip(text, null, ClipIdentity.NewId(), _clock(), origin);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _seen.Add(clip.Id);
            var ok = await WriteLockedAsync(clip, local: true).ConfigureAwait(false);
            if (ok) Event("applied", clip, "local");
            return ok;
        }
        finally { _gate.Release(); }
    }

    // ---------------- Roam crossings (§9.4) ----------------

    /// <summary>
    /// The clip a crossing carries: the clipboard is read now (within <paramref name="timeout"/>) so a copy made a moment
    /// ago goes along with its new id; null when sync is off, the clipboard is empty or unreadable, or the clip is over
    /// the limits. A copy found here also goes to the phone.
    /// </summary>
    public async Task<Clip?> ForCrossingAsync(TimeSpan timeout)
    {
        if (!_settings.ClipboardSync) return null;
        var deadline = _clock() + (long)timeout.TotalMilliseconds;
        if (!await _gate.WaitAsync(timeout).ConfigureAwait(false)) return null;
        Clip? copied;
        Clip? result = null;
        try
        {
            (var ok, copied) = await ObserveLockedAsync(ReadAttempts, TimeSpan.FromMilliseconds(100), deadline).ConfigureAwait(false);
            if (ok && _current is { IsEmpty: false, TooBig: false } c && (c.Png is null || _settings.ClipboardImages)) result = c;
        }
        finally { _gate.Release(); }
        if (copied is not null) _ = SendIfCurrentAsync(copied);
        return result;
    }

    async Task SendIfCurrentAsync(Clip clip)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_current, clip)) await SendLockedAsync(clip).ConfigureAwait(false);
        }
        catch (Exception ex) { Say("[clip] send failed: " + ex.GetType().Name); }
        finally { _gate.Release(); }
    }
}
