using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using HIB.Core.Link;

namespace HIB.Core.Phone;

/// <summary>
/// Phone → desktop file transfer (fileTransferInit / fileChunk / fileTransferComplete / fileTransferCancel),
/// used by Flick and Phone Search fetch. Every chunk is acked, even for unknown ids.
/// </summary>
public interface IExternalTransferSink
{
    bool Owns(string id);
    void Progress(string id, long received, long total);
    /// <summary>Returns true if the sink took the file (it moved/deleted tempPath).</summary>
    bool Complete(string id, string tempPath);
    void Fail(string id);
}

public sealed class FileReceiver
{
    readonly ILinkSession _link;
    readonly Func<string> _downloadsDir;
    readonly string _tempDir;
    readonly ConcurrentDictionary<string, Incoming> _incoming = new();

    sealed class Incoming
    {
        public required string Id;
        public required string Name;
        public long Size;
        public int ChunkSize;
        public string? Checksum;
        public string? FlickId;
        public string? SearchFetchId;
        public string? RelayFrom;
        /// <summary>The session the file arrives on (the phone, or a peer link): acks go back there.</summary>
        public required ILinkSession Link;
        public required string TempPath;
        public required FileStream Stream;
        public long Received;
        public readonly object Gate = new();
    }

    public FileReceiver(ILinkSession link, Func<string> downloadsDir, string tempDir)
    {
        _link = link;
        _downloadsDir = downloadsDir;
        _tempDir = tempDir;
        Directory.CreateDirectory(tempDir);
        link.MessageReceived += OnMessage;
        link.ConnectionChanged += (_, connected) => { if (!connected) CancelAll(link); };
    }

    /// <summary>
    /// Also receives files on another session (a direct peer link, SPACE.md §3.3), exactly like a
    /// relayed Flick: chunks are acked and transferVerified/flickLanded answered on that session.
    /// </summary>
    public void Attach(ILinkSession session)
    {
        if (ReferenceEquals(session, _link)) return;
        session.MessageReceived += OnMessage;
        session.ConnectionChanged += OnAttachedConnectionChanged;
    }

    public void Detach(ILinkSession session)
    {
        if (ReferenceEquals(session, _link)) return;
        session.MessageReceived -= OnMessage;
        session.ConnectionChanged -= OnAttachedConnectionChanged;
        CancelAll(session);
    }

    void OnAttachedConnectionChanged(object? sender, bool connected)
    {
        if (!connected && sender is ILinkSession s) CancelAll(s);
    }

    /// <summary>
    /// Optional external owner of some transfers (Phone Search fetches tracked by HIB.Features'
    /// PhoneFetchRegistry). When it claims an id, progress/complete/fail go to it instead of Downloads.
    /// </summary>
    public IExternalTransferSink? ExternalSink { get; set; }

    public event Action<TransferProgress>? Progress;
    public event Action<ReceivedFile>? Received;
    public event Action<string, string>? Failed; // id, reason
    public event Action<string>? Log;

    void OnMessage(object? sender, LinkMessageEventArgs e)
    {
        var link = sender as ILinkSession ?? _link;
        switch (e.Type)
        {
            case "fileTransferInit": OnInit(e.Data, link); break;
            case "fileChunk": OnChunk(e.Data, link); break;
            case "fileTransferComplete": OnComplete(e.Data); break;
            case "fileTransferCancel": OnCancel(e.Data); break;
        }
    }

    static string SafeName(string name)
    {
        name = name.Replace('/', '_').Replace('\\', '_');
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(name) ? "file" : name;
    }

    void OnInit(JsonElement d, ILinkSession link)
    {
        var id = d.Str("id");
        if (string.IsNullOrEmpty(id)) return;
        var name = SafeName(d.Str("name") ?? "file");
        var temp = Path.Combine(_tempDir, $"incoming_{SafeName(id)}_{name}");
        if (_incoming.TryRemove(id, out var old)) Dispose(old, delete: true);
        FileStream fs;
        try { fs = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.Read); }
        catch (Exception ex)
        {
            Log?.Invoke($"Cannot create temp file: {ex.Message}");
            return;
        }
        var inc = new Incoming
        {
            Id = id,
            Name = name,
            Size = d.Long("size") ?? 0,
            ChunkSize = d.Int("chunkSize") is int cs && cs > 0 ? cs : 65536,
            Checksum = d.Str("checksum"),
            FlickId = d.Str("flickId"),
            SearchFetchId = d.Str("searchFetchId"),
            RelayFrom = d.Str("relayFrom"),
            Link = link,
            TempPath = temp,
            Stream = fs,
        };
        _incoming[id] = inc;
        Log?.Invoke($"Receiving {name} ({inc.Size} bytes)");
        Progress?.Invoke(new TransferProgress(id, name, inc.Size, 0, false, false, inc.FlickId));
    }

    void OnChunk(JsonElement d, ILinkSession link)
    {
        var id = d.Str("id") ?? "";
        var index = d.Int("index") ?? -1;
        if (_incoming.TryGetValue(id, out var inc) && index >= 0)
        {
            var bytes = LenientJson.Base64(d.Str("chunk"));
            if (bytes is not null)
            {
                lock (inc.Gate)
                {
                    try
                    {
                        inc.Stream.Position = (long)index * inc.ChunkSize;
                        inc.Stream.Write(bytes);
                        inc.Received = Math.Max(inc.Received, inc.Stream.Position);
                    }
                    catch (Exception ex) { Log?.Invoke($"Chunk write failed: {ex.Message}"); }
                }
                Progress?.Invoke(new TransferProgress(id, inc.Name, inc.Size, inc.Received, false, false, inc.FlickId));
                if (ExternalSink?.Owns(id) == true) ExternalSink.Progress(id, inc.Received, inc.Size);
            }
        }
        // Always ack, even for unknown ids (the sender's window depends on it).
        _ = link.SendAsync("fileChunkAck", new { id, index });
    }

    void OnComplete(JsonElement d)
    {
        var id = d.Str("id") ?? "";
        if (!_incoming.TryRemove(id, out var inc)) return;
        var size = d.Long("size") ?? inc.Size;
        lock (inc.Gate)
        {
            try { inc.Stream.Flush(); } catch { }
            var actual = inc.Stream.Length;
            if (size > 0 && actual != size)
            {
                Dispose(inc, delete: true);
                Log?.Invoke($"{inc.Name}: size mismatch ({actual} != {size})");
                Failed?.Invoke(id, "Size mismatch");
                _ = inc.Link.SendAsync("transferVerified", new { id, verified = false });
                Progress?.Invoke(new TransferProgress(id, inc.Name, size, actual, true, true, inc.FlickId));
                return;
            }
            var checksum = d.Str("checksum") ?? inc.Checksum;
            if (!string.IsNullOrEmpty(checksum))
            {
                inc.Stream.Position = 0;
                var hex = Convert.ToHexString(SHA256.HashData(inc.Stream)).ToLowerInvariant();
                if (!hex.Equals(checksum, StringComparison.OrdinalIgnoreCase))
                    Log?.Invoke($"{inc.Name}: checksum mismatch (kept anyway)");
            }
            inc.Stream.Dispose();
        }

        if (ExternalSink?.Owns(id) == true && ExternalSink.Complete(id, inc.TempPath))
        {
            Log?.Invoke($"{inc.Name}: handed to phone search");
            Progress?.Invoke(new TransferProgress(id, inc.Name, size, size, true, false, inc.FlickId));
            return;
        }

        var destDir = inc.SearchFetchId is not null || id.StartsWith("hibfetch-", StringComparison.Ordinal)
            ? Path.Combine(_downloadsDir(), "H.I.B")
            : _downloadsDir();
        string dest;
        try
        {
            Directory.CreateDirectory(destDir);
            dest = UniquePath(destDir, inc.Name);
            File.Move(inc.TempPath, dest);
        }
        catch (Exception ex)
        {
            try { File.Delete(inc.TempPath); } catch { }
            Failed?.Invoke(id, ex.Message);
            return;
        }
        Log?.Invoke($"Saved {dest}");
        Progress?.Invoke(new TransferProgress(id, inc.Name, size, size, true, false, inc.FlickId));
        // Lets a sending computer (relayed through the phone) know the file is safely on disk.
        _ = inc.Link.SendAsync("transferVerified", new { id, verified = true });
        Received?.Invoke(new ReceivedFile(id, Path.GetFileName(dest), dest, size, inc.FlickId,
            inc.SearchFetchId is not null, DateTimeOffset.Now) { From = inc.RelayFrom });
        if (inc.FlickId is not null) _ = inc.Link.SendAsync("flickLanded", new { id = inc.FlickId });
    }

    void OnCancel(JsonElement d)
    {
        var id = d.Str("id") ?? "";
        if (!_incoming.TryRemove(id, out var inc))
        {
            // The phone could not read the file at all (no init was sent).
            if (ExternalSink?.Owns(id) == true) ExternalSink.Fail(id);
            return;
        }
        Dispose(inc, delete: true);
        if (ExternalSink?.Owns(id) == true) ExternalSink.Fail(id);
        Failed?.Invoke(id, ReferenceEquals(inc.Link, _link) ? "Cancelled by phone" : "Cancelled by the sender");
        Progress?.Invoke(new TransferProgress(id, inc.Name, inc.Size, inc.Received, true, true, inc.FlickId));
    }

    void CancelAll(ILinkSession link)
    {
        foreach (var (id, candidate) in _incoming.ToList())
        {
            if (!ReferenceEquals(candidate.Link, link)) continue;
            if (_incoming.TryRemove(id, out var inc))
            {
                Dispose(inc, delete: true);
                Failed?.Invoke(id, "Disconnected");
            }
        }
    }

    static void Dispose(Incoming inc, bool delete)
    {
        lock (inc.Gate)
        {
            try { inc.Stream.Dispose(); } catch { }
            if (delete) try { File.Delete(inc.TempPath); } catch { }
        }
    }

    public static string UniquePath(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 1; ; i++)
        {
            path = Path.Combine(dir, $"{stem}({i}){ext}");
            if (!File.Exists(path)) return path;
        }
    }
}
