using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HIB.Features.Abstractions;

namespace HIB.Features.FindMy;

/// <summary>
/// Find My over the internet. ntfy.sh (or a self-hosted ntfy) is a dumb mailbox: only AES-GCM
/// ciphertext leaves this PC. Publishes sealed messages and keeps one streaming subscription
/// (<c>GET {server}/{topic}/json?since=2m</c>) open with 2→300 s backoff.
/// </summary>
public sealed class FindMyRelay : ObservableObject, IDisposable
{
    private readonly ISettingsStore _store;
    private readonly Func<byte[]?> _pairingKey;
    private readonly HttpClient _http;
    private readonly FindMyReplayCache _replay;
    private readonly object _lock = new();
    private CancellationTokenSource? _streamCts;
    private string? _activeTopic;

    private bool _isSubscribed;
    private string? _lastError;

    /// <summary>Idle timeout on the stream (ntfy sends keepalives every ~45 s).</summary>
    public TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromSeconds(90);

    public FindMyRelay(ISettingsStore store, Func<byte[]?> pairingKey, HttpMessageHandler? handler = null)
    {
        _store = store;
        _pairingKey = pairingKey;
        Settings = new FindMySettings(store);
        _replay = new FindMyReplayCache(store);
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public FindMySettings Settings { get; }

    public bool IsSubscribed { get => _isSubscribed; private set => SetProperty(ref _isSubscribed, value); }
    public string? LastError { get => _lastError; private set => SetProperty(ref _lastError, value); }

    /// <summary>Every authentic, fresh, non-replayed message from the phone (from == "phone").</summary>
    public event Action<FindMyEnvelope>? MessageReceived;

    public FindMyRelayKeys? CurrentKeys => FindMyRelayKeys.Current(_store, _pairingKey());

    public bool IsAvailable => Settings.RelayEnabled && Settings.NormalizedServer != null && CurrentKeys != null;

    // MARK: Publish

    public async Task<bool> PublishAsync(string type, JsonObject data, CancellationToken ct = default)
    {
        var server = Settings.NormalizedServer;
        var keys = CurrentKeys;
        string? body = null;
        if (Settings.RelayEnabled && server != null && keys != null) body = FindMyCodec.Seal(type, data, keys);
        if (body is null || server is null || keys is null)
        {
            LastError = "Internet relay isn't set up (pair the phone once and enable it in Settings → Find My).";
            return false;
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{server}/{keys.Topic}")
            {
                Content = new StringContent(body, Encoding.UTF8),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (code < 200 || code >= 300)
            {
                LastError = $"Relay server answered {code}.";
                return false;
            }
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = "Couldn't reach the relay: " + ex.Message;
            return false;
        }
    }

    // MARK: Subscribe

    /// <summary>Start (or restart if the topic/server changed) the background subscription.</summary>
    public void EnsureSubscribed()
    {
        var server = Settings.NormalizedServer;
        var keys = CurrentKeys;
        if (!Settings.RelayEnabled || server is null || keys is null)
        {
            Stop();
            return;
        }
        var identity = $"{server}/{keys.Topic}";
        lock (_lock)
        {
            if (_streamCts != null && _activeTopic == identity) return;
        }
        Stop();
        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            _activeTopic = identity;
            _streamCts = cts;
        }
        _ = Task.Run(() => RunStreamAsync(server, keys, cts.Token));
    }

    public void Stop()
    {
        lock (_lock)
        {
            _streamCts?.Cancel();
            _streamCts = null;
            _activeTopic = null;
        }
        IsSubscribed = false;
    }

    private async Task RunStreamAsync(string server, FindMyRelayKeys keys, CancellationToken ct)
    {
        double backoff = 2;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{server}/{keys.Topic}/json?since=2m");
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(StreamIdleTimeout);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectCts.Token).ConfigureAwait(false);
                if ((int)response.StatusCode != 200) throw new HttpRequestException("bad server response");
                IsSubscribed = true;
                backoff = 2;
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (!ct.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(StreamIdleTimeout);
                    var line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
                    if (line is null) break;
                    HandleLine(line, keys);
                }
            }
            catch
            {
                if (ct.IsCancellationRequested) break;
            }
            IsSubscribed = false;
            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct).ConfigureAwait(false); } catch { break; }
            backoff = Math.Min(backoff * 2, 300);
        }
        IsSubscribed = false;
    }

    /// <summary>Handles one ntfy JSON line. Internal for tests.</summary>
    internal bool HandleLine(string line, FindMyRelayKeys keys)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(line).RootElement; } catch { return false; }
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.String || ev.GetString() != "message") return false;
        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.String) return false;
        var envelope = FindMyCodec.Open(msg.GetString()!, keys);
        if (envelope is null || envelope.From != "phone" || !_replay.Accept(envelope.Nonce)) return false;
        HibFeatures.Dispatch(() => MessageReceived?.Invoke(envelope));
        return true;
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}
