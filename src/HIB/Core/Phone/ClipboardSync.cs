using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HIB.Core.Link;
using HIB.Core.Pairing;

namespace HIB.Core.Phone;

/// <summary>Platform clipboard access (Avalonia in the app, a fake in tests).</summary>
public interface IClipboardBridge
{
    Task<string?> GetTextAsync();
    /// <summary>PNG bytes of the clipboard image, or null.</summary>
    Task<byte[]?> GetImagePngAsync();
    Task SetTextAsync(string text);
    Task SetImageAsync(byte[] imageBytes);
    /// <summary>Cheap change counter (Windows GetClipboardSequenceNumber); null if unsupported.</summary>
    uint? ChangeCount { get; }
}

public sealed record ClipboardEntry(bool FromPhone, string? Text, byte[]? Image, DateTimeOffset Time);

/// <summary>
/// Two-way clipboard sync with loop prevention. Polls the desktop every 2.5 s while connected;
/// text wins over images; images ≤ 8 MiB; each side remembers what it received last.
/// </summary>
public sealed class ClipboardSync : IDisposable
{
    public const int MaxImageBytes = 8 * 1024 * 1024;

    readonly ILinkSession _link;
    readonly PhoneHub _hub;
    readonly AppSettings _settings;
    readonly IClipboardBridge _clipboard;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly TimeSpan _interval;
    CancellationTokenSource? _cts;

    string? _lastLocalText;
    string? _lastLocalImageHash;
    string? _lastReceivedText;
    string? _lastReceivedImageHash;
    uint? _lastChangeCount;
    bool _baselined;

    public ClipboardSync(ILinkSession link, PhoneHub hub, AppSettings settings, IClipboardBridge clipboard, TimeSpan? interval = null)
    {
        _link = link;
        _hub = hub;
        _settings = settings;
        _clipboard = clipboard;
        _interval = interval ?? TimeSpan.FromSeconds(2.5);
        hub.ClipboardTextReceived += t => _ = ApplyTextFromPhoneAsync(t);
        hub.ClipboardImageReceived += (b, _, _) => _ = ApplyImageFromPhoneAsync(b);
        link.ConnectionChanged += (_, c) => { if (c) _baselined = false; };
    }

    long _localChangeCount;

    /// <summary>Increments whenever the desktop clipboard content changes (user copy or phone apply).</summary>
    public long LocalChangeCount => Interlocked.Read(ref _localChangeCount);

    /// <summary>The last text seen on (or written to) the desktop clipboard.</summary>
    public string? LastLocalText => _lastLocalText;

    public event Action<ClipboardEntry>? EntryAdded;
    public event Action<string>? Log;

    /// <summary>When set, the next local change is not sent (e.g. a login code copied locally).</summary>
    public void SuppressNextLocalChange(string text) => _lastLocalText = text;

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts = null;
    }

    async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!_link.IsConnected) continue;
                try { await PollOnceAsync().ConfigureAwait(false); }
                catch (Exception ex) { Log?.Invoke("Clipboard poll failed: " + ex.Message); }
            }
        }
        catch (OperationCanceledException) { }
    }

    static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    /// <summary>One poll of the desktop clipboard (public for tests).</summary>
    public async Task PollOnceAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var count = _clipboard.ChangeCount;
            if (count is not null && _baselined && count == _lastChangeCount) return;
            _lastChangeCount = count;

            var text = await _clipboard.GetTextAsync().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(text))
            {
                if (text == _lastLocalText) { _baselined = true; return; }
                _lastLocalText = text;
                Interlocked.Increment(ref _localChangeCount);
                if (!_baselined) { _baselined = true; return; } // don't push stale content on connect
                if (text == _lastReceivedText || !_settings.ClipboardSync) return;
                if (await _hub.SendClipboardTextAsync(text).ConfigureAwait(false))
                    EntryAdded?.Invoke(new ClipboardEntry(false, text, null, DateTimeOffset.Now));
                return;
            }

            if (!_settings.ClipboardImages || !_settings.ClipboardSync) { _baselined = true; return; }
            var png = await _clipboard.GetImagePngAsync().ConfigureAwait(false);
            if (png is null || png.Length == 0) { _baselined = true; return; }
            var hash = Hash(png);
            if (hash == _lastLocalImageHash) { _baselined = true; return; }
            _lastLocalImageHash = hash;
            Interlocked.Increment(ref _localChangeCount);
            if (!_baselined) { _baselined = true; return; }
            if (hash == _lastReceivedImageHash) return;
            if (png.Length > MaxImageBytes)
            {
                Log?.Invoke($"Clipboard image too large ({png.Length / 1024} KiB) — not sent");
                return;
            }
            if (await _hub.SendClipboardImageAsync(png).ConfigureAwait(false))
                EntryAdded?.Invoke(new ClipboardEntry(false, null, png, DateTimeOffset.Now));
        }
        finally { _gate.Release(); }
    }

    async Task ApplyTextFromPhoneAsync(string text)
    {
        EntryAdded?.Invoke(new ClipboardEntry(true, text, null, DateTimeOffset.Now));
        if (!_settings.ClipboardSync) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _lastReceivedText = text;
            _lastLocalText = text;
            Interlocked.Increment(ref _localChangeCount);
            await _clipboard.SetTextAsync(text).ConfigureAwait(false);
            _lastChangeCount = _clipboard.ChangeCount;
        }
        catch (Exception ex) { Log?.Invoke("Set clipboard failed: " + ex.Message); }
        finally { _gate.Release(); }
    }

    async Task ApplyImageFromPhoneAsync(byte[] bytes)
    {
        EntryAdded?.Invoke(new ClipboardEntry(true, null, bytes, DateTimeOffset.Now));
        if (!_settings.ClipboardSync || !_settings.ClipboardImages) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _lastReceivedImageHash = Hash(bytes);
            _lastLocalText = null;
            Interlocked.Increment(ref _localChangeCount);
            await _clipboard.SetImageAsync(bytes).ConfigureAwait(false);
            // The platform may re-encode the image: remember what it reads back as.
            var back = await _clipboard.GetImagePngAsync().ConfigureAwait(false);
            _lastLocalImageHash = back is null ? _lastReceivedImageHash : Hash(back);
            if (back is not null) _lastReceivedImageHash = _lastLocalImageHash;
            _lastChangeCount = _clipboard.ChangeCount;
        }
        catch (Exception ex) { Log?.Invoke("Set clipboard image failed: " + ex.Message); }
        finally { _gate.Release(); }
    }
}
