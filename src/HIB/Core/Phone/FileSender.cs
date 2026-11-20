// H.I.B for Windows — desktop → phone (and desktop → desktop via the phone) file sending.
// Made by Haitham Hamid.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using HIB.Core.Link;

namespace HIB.Core.Phone;

/// <summary>Result of one outgoing transfer.</summary>
public sealed record SendResult(string Id, string Name, bool Ok, string? Error);

/// <summary>Progress of one outgoing transfer.</summary>
public sealed record SendProgress(string Id, string Name, string TargetName, long Size, long Sent, bool Done, bool Failed);

/// <summary>
/// Sends a file with the WebSocket file-transfer protocol (PROTOCOL.md §6.9): fileTransferInit (with a
/// SHA-256 checksum) → 64 KiB fileChunk messages with a sliding window of 8 acked by fileChunkAck →
/// fileTransferComplete. With <c>relayTo</c> set, the phone forwards the stream to another computer
/// (§6.22) and relays that computer's acks back, so the same window paces the whole path.
/// </summary>
public sealed class FileSender
{
    public const int ChunkSize = 64 * 1024;
    public const int Window = 8;
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(2);
    const int MaxAttempts = 5;

    readonly ILinkSession _link;
    readonly ConcurrentDictionary<string, Channel<int>> _acks = new();
    readonly ConcurrentDictionary<string, string> _cancelled = new();
    readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _verified = new();

    public FileSender(ILinkSession link)
    {
        _link = link;
        link.MessageReceived += OnMessage;
    }

    public event Action<SendProgress>? Progress;
    public event Action<string>? Log;

    /// <summary>Error text when the session isn't up (the phone by default; a peer link says its own).</summary>
    public string NotConnectedText { get; init; } = "Phone not connected";
    /// <summary>Error text when the session drops mid-transfer.</summary>
    public string LostText { get; init; } = "Phone disconnected";

    void OnMessage(object? sender, LinkMessageEventArgs e)
    {
        var id = e.Data.Str("id");
        if (string.IsNullOrEmpty(id)) return;
        switch (e.Type)
        {
            case "fileChunkAck":
                if (_acks.TryGetValue(id, out var ch) && e.Data.Int("index") is int index) ch.Writer.TryWrite(index);
                break;
            case "fileTransferCancel":
                if (_acks.ContainsKey(id)) _cancelled[id] = e.Data.Str("reason") ?? "cancelled";
                break;
            case "transferVerified":
                if (_verified.TryGetValue(id, out var tcs)) tcs.TrySetResult(e.Data.Bool("verified") ?? false);
                break;
        }
    }

    /// <summary>Sends <paramref name="path"/> to the phone (<paramref name="relayTo"/> null/"phone") or to another computer.</summary>
    public Task<SendResult> SendAsync(string path, string? relayTo, string targetName, CancellationToken ct = default)
        => SendAsync(path, relayTo, targetName, null, null, ct);

    /// <summary>
    /// Like <see cref="SendAsync(string, string?, string, CancellationToken)"/> with a chosen transfer
    /// <paramref name="id"/> and extra <c>fileTransferInit</c> fields (e.g. <c>flickId</c>, <c>relayFrom</c>).
    /// </summary>
    public async Task<SendResult> SendAsync(string path, string? relayTo, string targetName, string? id,
        IReadOnlyDictionary<string, object?>? extraInit, CancellationToken ct = default)
    {
        id ??= Guid.NewGuid().ToString();
        var name = Path.GetFileName(path);
        if (!File.Exists(path)) return new SendResult(id, name, false, "File not found");
        if (!_link.IsConnected) return new SendResult(id, name, false, NotConnectedText);

        FileStream fs;
        try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 256 * 1024); }
        catch (Exception ex) { return new SendResult(id, name, false, ex.Message); }

        var acks = Channel.CreateUnbounded<int>();
        _acks[id] = acks;
        var verified = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _verified[id] = verified;
        try
        {
            await using var _ = fs;
            var size = fs.Length;
            string checksum;
            using (var sha = SHA256.Create())
            {
                checksum = Convert.ToHexString(await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false)).ToLowerInvariant();
            }
            fs.Position = 0;

            var init = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["name"] = name,
                ["size"] = size,
                ["mime"] = MimeFor(name),
                ["chunkSize"] = ChunkSize,
                ["isClipboard"] = false,
                ["checksum"] = checksum,
            };
            var phone = string.IsNullOrEmpty(relayTo) || relayTo == "phone";
            if (!phone) init["relayTo"] = relayTo;
            if (extraInit is not null)
                foreach (var (k, v) in extraInit) init[k] = v;
            if (!await _link.SendAsync("fileTransferInit", init, ct).ConfigureAwait(false))
                return Fail(id, name, targetName, size, 0, NotConnectedText);
            Log?.Invoke($"Sending {name} ({size} bytes) to {targetName}");
            Progress?.Invoke(new SendProgress(id, name, targetName, size, 0, false, false));

            var total = (int)((size + ChunkSize - 1) / ChunkSize);
            var inFlight = new Dictionary<int, (string b64, int attempts, DateTime sentAt)>();
            var buf = new byte[ChunkSize];
            var next = 0;
            var acked = 0;
            long sentBytes = 0;
            var lastReport = DateTime.MinValue;

            while (acked < total)
            {
                ct.ThrowIfCancellationRequested();
                if (_cancelled.TryRemove(id, out var why)) return Fail(id, name, targetName, size, sentBytes, Reason(why));
                if (!_link.IsConnected) return Fail(id, name, targetName, size, sentBytes, LostText);

                while (inFlight.Count < Window && next < total)
                {
                    var read = await ReadFullyAsync(fs, buf, ct).ConfigureAwait(false);
                    if (read <= 0) return Fail(id, name, targetName, size, sentBytes, "File changed while sending");
                    var b64 = Convert.ToBase64String(buf, 0, read);
                    if (!await _link.SendAsync("fileChunk", new { id, index = next, chunk = b64 }, ct).ConfigureAwait(false))
                        return Fail(id, name, targetName, size, sentBytes, LostText);
                    inFlight[next] = (b64, 1, DateTime.UtcNow);
                    next++;
                }

                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(AckTimeout);
                try
                {
                    var index = await acks.Reader.ReadAsync(wait.Token).ConfigureAwait(false);
                    if (inFlight.Remove(index))
                    {
                        acked++;
                        sentBytes = Math.Min(size, (long)acked * ChunkSize);
                        if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(250))
                        {
                            lastReport = DateTime.UtcNow;
                            Progress?.Invoke(new SendProgress(id, name, targetName, size, sentBytes, false, false));
                        }
                    }
                    continue;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

                var now = DateTime.UtcNow;
                foreach (var (idx, entry) in inFlight.ToList())
                {
                    if (now - entry.sentAt < AckTimeout) continue;
                    if (entry.attempts >= MaxAttempts) return Fail(id, name, targetName, size, sentBytes, "No answer from " + targetName);
                    await _link.SendAsync("fileChunk", new { id, index = idx, chunk = entry.b64 }, ct).ConfigureAwait(false);
                    inFlight[idx] = (entry.b64, entry.attempts + 1, now);
                }
            }

            await _link.SendAsync("fileTransferComplete", new { id, name, size, checksum }, ct).ConfigureAwait(false);
            // The phone (or relayed computer) answers transferVerified; older ones never do.
            var done = await Task.WhenAny(verified.Task, Task.Delay(TimeSpan.FromSeconds(10), ct)).ConfigureAwait(false);
            if (done == verified.Task && !verified.Task.Result)
                return Fail(id, name, targetName, size, size, "The file arrived damaged");
            Progress?.Invoke(new SendProgress(id, name, targetName, size, size, true, false));
            Log?.Invoke($"Sent {name} to {targetName}");
            return new SendResult(id, name, true, null);
        }
        catch (OperationCanceledException)
        {
            _ = _link.SendAsync("fileTransferCancel", new { id });
            return Fail(id, name, targetName, 0, 0, "Cancelled");
        }
        catch (Exception ex)
        {
            _ = _link.SendAsync("fileTransferCancel", new { id });
            return Fail(id, name, targetName, 0, 0, ex.Message);
        }
        finally
        {
            _acks.TryRemove(id, out _);
            _verified.TryRemove(id, out _);
            _cancelled.TryRemove(id, out _);
        }
    }

    SendResult Fail(string id, string name, string target, long size, long sent, string error)
    {
        Progress?.Invoke(new SendProgress(id, name, target, size, sent, true, true));
        Log?.Invoke($"Sending {name} to {target} failed: {error}");
        return new SendResult(id, name, false, error);
    }

    static string Reason(string code) => code switch
    {
        "targetOffline" => "That computer isn't connected to the phone",
        "targetLost" or "peerLost" => "The other computer disconnected",
        "cancelledOnPhone" => "Cancelled on the phone",
        "storage" => "The phone couldn't save it",
        _ => "Cancelled",
    };

    static async Task<int> ReadFullyAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        var total = 0;
        while (total < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    public static string MimeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        ".bmp" => "image/bmp",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".wav" => "audio/wav",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".txt" => "text/plain",
        ".apk" => "application/vnd.android.package-archive",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => "application/octet-stream",
    };
}

/// <summary>A place a file can go: the phone itself or another computer connected to it.</summary>
public sealed record SendTarget(string Id, string Name, string Platform, bool Connected)
{
    public bool IsPhone => Id == "phone";
    public string Label => IsPhone ? $"{Name} (phone)" : Platform == "windows" ? $"{Name} (Windows)" : $"{Name} (Mac)";
}

/// <summary>
/// The phone's view of the ecosystem (PROTOCOL.md §6.22): asks with <c>desktopsRequest</c>, and the
/// phone answers/pushes <c>desktopsList</c> with the other computers it is connected to.
/// </summary>
public sealed class PhoneEcosystem
{
    readonly ILinkSession _link;
    readonly object _gate = new();
    List<SendTarget> _desktops = new();
    string? _phoneName;

    public PhoneEcosystem(ILinkSession link)
    {
        _link = link;
        link.MessageReceived += OnMessage;
        link.ConnectionChanged += (_, connected) =>
        {
            if (connected) _ = RequestAsync();
            else
            {
                lock (_gate) _desktops = new();
                Changed?.Invoke();
            }
        };
    }

    public event Action? Changed;

    /// <summary>True once a phone that understands multi-desktop has answered.</summary>
    public bool Supported { get; private set; }

    /// <summary>The phone first, then the other connected computers.</summary>
    public IReadOnlyList<SendTarget> Targets
    {
        get
        {
            var list = new List<SendTarget>();
            if (_link.IsConnected)
                list.Add(new SendTarget("phone", _phoneName ?? _link.Device?.Name ?? "Phone", "android", true));
            lock (_gate) list.AddRange(_desktops.Where(d => d.Connected));
            return list;
        }
    }

    /// <summary>Other computers paired with the phone, including offline ones.</summary>
    public IReadOnlyList<SendTarget> AllDesktops { get { lock (_gate) return _desktops.ToList(); } }

    public Task<bool> RequestAsync() => _link.SendAsync("desktopsRequest");

    void OnMessage(object? sender, LinkMessageEventArgs e)
    {
        if (e.Type != "desktopsList") return;
        var list = new List<SendTarget>();
        if (e.Data.ValueKind == JsonValueKind.Object && e.Data.TryGetProperty("desktops", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in arr.EnumerateArray())
            {
                if (d.ValueKind != JsonValueKind.Object) continue;
                var id = d.Str("id") ?? d.Str("name");
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (string.Equals(id, _link.DesktopName, StringComparison.OrdinalIgnoreCase)) continue; // never ourselves
                list.Add(new SendTarget(id, d.Str("name") ?? id, (d.Str("platform") ?? "mac").ToLowerInvariant(), d.Bool("connected") ?? false));
            }
        }
        _phoneName = e.Data.Obj("phone") is { } p ? p.Str("name") ?? _phoneName : _phoneName;
        lock (_gate) _desktops = list;
        Supported = true;
        Changed?.Invoke();
    }
}
