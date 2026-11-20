using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using HIB.Features.Abstractions;
using HIB.Features.Adb;

namespace HIB.Features.PhoneSearch;

public enum PhoneSearchKind { Photos, Files, Apps, Contacts }

public sealed record PhoneSearchResult(PhoneSearchKind Kind, string Title, string Subtitle, string Mime,
    string? Path, string? Uri, long Size, DateTimeOffset? Date, byte[]? Thumbnail)
{
    public string Id => $"{Raw(Kind)}:{Path ?? Uri ?? Title}:{Subtitle}";
    public string? FetchPath => Path ?? Uri;
    public bool IsVideo => Mime.StartsWith("video/", StringComparison.Ordinal);

    internal static string Raw(PhoneSearchKind k) => k.ToString().ToLowerInvariant();

    public static PhoneSearchResult? Parse(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object) return null;
        string? S(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Length > 0 ? v.GetString() : null;
        var kindRaw = S("kind");
        var title = d.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (kindRaw is null || title is null) return null;
        PhoneSearchKind kind;
        switch (kindRaw)
        {
            case "photos": kind = PhoneSearchKind.Photos; break;
            case "files": kind = PhoneSearchKind.Files; break;
            case "apps": kind = PhoneSearchKind.Apps; break;
            case "contacts": kind = PhoneSearchKind.Contacts; break;
            default: return null;
        }
        long size = d.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out var l) ? l : 0;
        DateTimeOffset? date = null;
        if (d.TryGetProperty("date", out var dt) && dt.ValueKind == JsonValueKind.Number && dt.TryGetDouble(out var ms) && ms > 0)
            date = DateTimeOffset.FromUnixTimeMilliseconds((long)(ms > 1e11 ? ms : ms * 1000));
        byte[]? thumb = null;
        if (S("thumbnail") is { } b64)
        {
            try { thumb = Convert.FromBase64String(b64); } catch { thumb = null; }
        }
        return new PhoneSearchResult(kind, title, S("subtitle") ?? "", S("mime") ?? "", S("path"), S("uri"), size, date, thumb);
    }
}

/// <summary>
/// "Search your phone": <c>phoneSearch {id,query,kinds,limit}</c> → <c>phoneSearchResults {id,results,done}</c>
/// batches. Fetching a result goes over ADB pull when connected, else <c>phoneFetchFile</c> + the
/// WebSocket file transfer (the app forwards transfer events to <see cref="PhoneFetchRegistry"/>).
/// </summary>
public sealed class PhoneSearchClient : ObservableObject, IDisposable
{
    public delegate void Handler(IReadOnlyList<PhoneSearchResult> results, bool done);

    private readonly IPhoneLink _link;
    private readonly AdbService? _adb;
    private readonly ConcurrentDictionary<string, Handler> _handlers = new();
    private readonly ConcurrentDictionary<string, List<Action<string?>>> _waiters = new();
    private readonly ConcurrentDictionary<string, string> _downloaded = new();

    public PhoneSearchClient(IPhoneLink link, AdbService? adb = null)
    {
        _link = link;
        _adb = adb;
        _link.MessageReceived += OnMessage;
    }

    public bool IsPhoneConnected => _link.IsConnected;

    /// <summary>Progress 0…1 (or -1 when unknown) keyed by result id while downloading.</summary>
    public ConcurrentDictionary<string, double> Progress { get; } = new();
    public ConcurrentDictionary<string, string> Errors { get; } = new();

    /// <summary>Fires when <see cref="Progress"/> changes (result id).</summary>
    public event Action<string>? ProgressChanged;

    /// <summary>Starts a search; returns the id, or null when the phone isn't connected. Gives up after 6 s.</summary>
    public string? Search(string query, Handler onUpdate, IEnumerable<PhoneSearchKind>? kinds = null, int limit = 30)
    {
        if (!IsPhoneConnected) return null;
        var id = Guid.NewGuid().ToString().ToUpperInvariant();
        _handlers[id] = onUpdate;
        var kindList = (kinds ?? [PhoneSearchKind.Files, PhoneSearchKind.Photos, PhoneSearchKind.Apps, PhoneSearchKind.Contacts])
            .Select(k => (JsonNode)PhoneSearchResult.Raw(k)!).ToArray();
        _ = _link.SendAsync("phoneSearch", new JsonObject
        {
            ["id"] = id, ["query"] = query, ["kinds"] = new JsonArray(kindList), ["limit"] = limit,
        });
        _ = Task.Delay(6000).ContinueWith(_ =>
        {
            if (_handlers.TryRemove(id, out var h)) HibFeatures.Dispatch(() => h([], true));
        }, TaskScheduler.Default);
        return id;
    }

    public void Cancel(string? id)
    {
        if (id != null) _handlers.TryRemove(id, out _);
    }

    private void OnMessage(string type, JsonElement data)
    {
        if (type != "phoneSearchResults" || data.ValueKind != JsonValueKind.Object) return;
        if (!data.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return;
        var id = idEl.GetString()!;
        var results = data.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(PhoneSearchResult.Parse).Where(x => x != null).Select(x => x!).ToList()
            : new List<PhoneSearchResult>();
        var done = !data.TryGetProperty("done", out var d) || d.ValueKind != JsonValueKind.False;
        Handler? handler;
        if (done) _handlers.TryRemove(id, out handler);
        else _handlers.TryGetValue(id, out handler);
        if (handler != null) HibFeatures.Dispatch(() => handler(results, done));
    }

    // MARK: - Fetching

    /// <summary>A unique path in ~/Downloads/H.I.B for <paramref name="name"/>.</summary>
    public static string UniqueDestination(string name)
    {
        var dir = HibPaths.DownloadsDirectory;
        Directory.CreateDirectory(dir);
        var safe = name.Replace("/", "_").Replace(":", "_").Replace("\\", "_");
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
        var ext = System.IO.Path.GetExtension(safe).TrimStart('.');
        var bare = System.IO.Path.GetFileNameWithoutExtension(safe);
        var candidate = System.IO.Path.Combine(dir, safe.Length == 0 ? "file" : safe);
        var n = 1;
        while (File.Exists(candidate))
        {
            candidate = System.IO.Path.Combine(dir, ext.Length == 0 ? $"{bare} ({n})" : $"{bare} ({n}).{ext}");
            n++;
        }
        return candidate;
    }

    public bool IsDownloading(PhoneSearchResult result) => Progress.ContainsKey(result.Id);

    /// <summary>Downloads a phone file (or returns the earlier download). Returns the local path or null.</summary>
    public Task<string?> FetchAsync(PhoneSearchResult result)
    {
        if (_downloaded.TryGetValue(result.Id, out var existing) && File.Exists(existing)) return Task.FromResult<string?>(existing);
        var remote = result.FetchPath;
        if (remote is null) return Task.FromResult<string?>(null);
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = false;
        _waiters.AddOrUpdate(result.Id, _ => { first = true; return [p => tcs.TrySetResult(p)]; },
            (_, list) => { lock (list) list.Add(p => tcs.TrySetResult(p)); return list; });
        if (!first) return tcs.Task;

        SetProgress(result.Id, result.Size > 0 ? 0 : -1);
        Errors.TryRemove(result.Id, out _);
        var name = result.Title.Length == 0 ? System.IO.Path.GetFileName(remote) : result.Title;
        if (remote.StartsWith('/') && _adb?.CurrentSerial != null && _adb.AdbPath != null)
            _ = PullOverAdbAsync(result, remote, name);
        else
            FetchOverLink(result, remote, name);
        return tcs.Task;
    }

    private void SetProgress(string id, double value)
    {
        Progress[id] = value;
        HibFeatures.Dispatch(() => ProgressChanged?.Invoke(id));
    }

    private void Finish(PhoneSearchResult result, string? path, string? error = null)
    {
        Progress.TryRemove(result.Id, out _);
        if (path != null) _downloaded[result.Id] = path; else Errors[result.Id] = error ?? "Download failed";
        HibFeatures.Dispatch(() => ProgressChanged?.Invoke(result.Id));
        if (_waiters.TryRemove(result.Id, out var list))
        {
            List<Action<string?>> copy;
            lock (list) copy = list.ToList();
            foreach (var w in copy) w(path);
        }
    }

    private async Task PullOverAdbAsync(PhoneSearchResult result, string remote, string name)
    {
        var dest = UniqueDestination(name);
        using var cts = new CancellationTokenSource();
        var expected = result.Size;
        var poll = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested && expected > 0)
            {
                try { await Task.Delay(250, cts.Token); } catch { break; }
                long size = 0;
                try { size = new FileInfo(dest).Exists ? new FileInfo(dest).Length : 0; } catch { }
                if (Progress.ContainsKey(result.Id)) SetProgress(result.Id, Math.Min(1, (double)size / expected));
            }
        });
        var ok = await _adb!.PullAsync(remote, dest).ConfigureAwait(false) && File.Exists(dest);
        cts.Cancel();
        try { await poll.ConfigureAwait(false); } catch { }
        if (ok) { Finish(result, dest); return; }
        try { File.Delete(dest); } catch { }
        // ADB couldn't read it (scoped storage, wrong serial…): ask the phone instead.
        FetchOverLink(result, result.FetchPath ?? remote, name);
    }

    private void FetchOverLink(PhoneSearchResult result, string remote, string name)
    {
        if (!_link.IsConnected)
        {
            Finish(result, null, "Phone not connected");
            return;
        }
        var transferId = "hibfetch-" + Guid.NewGuid().ToString().ToUpperInvariant();
        PhoneFetchRegistry.Register(transferId, name,
            fraction => { if (Progress.ContainsKey(result.Id)) SetProgress(result.Id, fraction); },
            path => Finish(result, path, path is null ? "Transfer failed" : null));
        _ = _link.SendAsync("phoneFetchFile", new JsonObject { ["id"] = transferId, ["path"] = remote });
        // If the phone never starts the transfer, fail instead of spinning forever.
        _ = Task.Delay(20_000).ContinueWith(_ =>
        {
            if (PhoneFetchRegistry.AbandonIfNotStarted(transferId)) Finish(result, null, "Phone didn't send the file");
        }, TaskScheduler.Default);
    }

    public void Dispose() => _link.MessageReceived -= OnMessage;
}

/// <summary>
/// Hooks for the app's WebSocket file-transfer handler: when a <c>fileTransferInit</c> carries
/// <c>searchFetchId</c> (or its id is tracked here), report chunks with <see cref="ReportProgress"/>,
/// finish with <see cref="Complete"/> (we move the temp file to ~/Downloads/H.I.B) or <see cref="Fail"/>.
/// </summary>
public static class PhoneFetchRegistry
{
    private sealed class Entry(string name, Action<double> onProgress, Action<string?> onDone)
    {
        public string Name { get; } = name;
        public Action<double> OnProgress { get; } = onProgress;
        public Action<string?> OnDone { get; } = onDone;
        public bool Started;
        public int LastPercent = -1;
    }

    private static readonly object Lock = new();
    private static readonly Dictionary<string, Entry> Entries = new();

    internal static void Register(string id, string name, Action<double> onProgress, Action<string?> onDone)
    {
        lock (Lock) Entries[id] = new Entry(name, onProgress, onDone);
    }

    public static bool IsTracked(string id) { lock (Lock) return Entries.ContainsKey(id); }

    public static void MarkStarted(string id) { lock (Lock) if (Entries.TryGetValue(id, out var e)) e.Started = true; }

    /// <summary>A chunk arrived for transfer <paramref name="id"/>.</summary>
    public static void ReportProgress(string id, long received, long total)
    {
        Entry? e;
        bool changed;
        int percent;
        lock (Lock)
        {
            if (!Entries.TryGetValue(id, out e)) return;
            e.Started = true;
            percent = total > 0 ? (int)Math.Min(100, received * 100 / total) : -1;
            changed = percent != e.LastPercent;
            e.LastPercent = percent;
        }
        if (changed && percent >= 0) e.OnProgress(percent / 100.0);
    }

    /// <summary>Transfer finished into <paramref name="tempPath"/>. Returns true when it was a search
    /// fetch (the file has been moved and the caller must not touch it).</summary>
    public static bool Complete(string id, string tempPath)
    {
        Entry? e;
        lock (Lock) { if (!Entries.Remove(id, out e)) return false; }
        var dest = PhoneSearchClient.UniqueDestination(e.Name);
        try
        {
            File.Move(tempPath, dest);
            e.OnDone(dest);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            e.OnDone(null);
        }
        return true;
    }

    public static void Fail(string id)
    {
        Entry? e;
        lock (Lock) Entries.Remove(id, out e);
        e?.OnDone(null);
    }

    internal static bool AbandonIfNotStarted(string id)
    {
        lock (Lock)
        {
            if (!Entries.TryGetValue(id, out var e) || e.Started) return false;
            Entries.Remove(id);
            return true;
        }
    }
}
