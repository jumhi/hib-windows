// H.I.B Phone Remote (docs/SPACE.md §15.3): one open /screen socket. Encrypted text control frames both ways, encrypted
// raw binary video frames to the phone, at most 2 frames waiting, pings, the adaptive ladder, the view crop, display
// switches, and locked → paused → resumed. Capture + encode is an IScreenPipeline (Windows: DXGI + Media Foundation).
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HIB.Core.Link;

namespace HIB.Core.Remote;

/// <summary>A display as the remote names it (§7.1 id), in desktop pixels.</summary>
public sealed record RemoteDisplay(string Id, string Name, PixelBox Bounds, bool Main);

/// <summary>What the phone asked for in screenOpen.</summary>
public sealed record ScreenOpenRequest(string Display, int MaxW, int MaxH, int Fps, ScreenCodec Codec, RemoteView View, bool Tailscale)
{
    public static ScreenOpenRequest? Parse(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object) return null;
        var view = d.Obj("view") is { } v ? ParseView(v) : RemoteView.Whole;
        return new ScreenOpenRequest(
            d.Str("display") ?? "",
            d.Int("maxW") ?? 0,
            d.Int("maxH") ?? 0,
            d.Int("fps") ?? 60,
            string.Equals(d.Str("codec"), "jpeg", StringComparison.OrdinalIgnoreCase) ? ScreenCodec.Jpeg : ScreenCodec.H264,
            view,
            string.Equals(d.Str("route"), "tailscale", StringComparison.OrdinalIgnoreCase));
    }

    public static RemoteView ParseView(JsonElement v)
        => RemoteViewMath.Clamp(new RemoteView(v.Double("x") ?? 0, v.Double("y") ?? 0, v.Double("w") ?? 1, v.Double("h") ?? 1));
}

public sealed class RemoteScreenSession
{
    static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan ViewApplyEvery = TimeSpan.FromMilliseconds(100);

    readonly WebSocket _ws;
    readonly byte[] _key;
    readonly IScreenPipeline _pipeline;
    readonly Func<IReadOnlyList<RemoteDisplay>> _displays;
    readonly Func<DateTime> _now;
    readonly Action<string> _log;
    readonly object _gate = new();
    readonly ScreenSendQueue _queue = new();
    readonly Queue<string> _text = new();
    readonly SemaphoreSlim _signal = new(0);
    readonly CancellationTokenSource _cts = new();
    readonly ScreenConfigTracker _configs = new();
    readonly ScreenLockState _lock = new();
    readonly PingWatch _ping;
    readonly KeyframeClock _keyClock;
    readonly int _maxW, _maxH, _requestedFps;
    readonly bool _tailscale;

    AdaptiveLadder _ladder;
    ScreenCodec _codec;
    RemoteDisplay _display;
    RemoteView _view;
    RemoteView? _pendingView;
    DateTime _lastViewApply = DateTime.MinValue;
    ScreenPipelineConfig? _config;
    bool _opened;
    uint _seq;
    (int Code, string Reason)? _close;

    public RemoteScreenSession(WebSocket ws, byte[] key, string remoteIp, string phoneName, ScreenOpenRequest request,
        IScreenPipeline pipeline, Func<IReadOnlyList<RemoteDisplay>> displays, bool locked, TimeSpan pingTimeout,
        Func<DateTime> clock, Action<string> log)
    {
        _ws = ws;
        _key = key;
        RemoteIp = remoteIp;
        PhoneName = phoneName;
        _pipeline = pipeline;
        _displays = displays;
        _now = clock;
        _log = log;
        _maxW = request.MaxW;
        _maxH = request.MaxH;
        _requestedFps = request.Fps;
        _tailscale = request.Tailscale;
        _codec = request.Codec;
        _view = request.View;
        _display = Resolve(request.Display);
        var now = clock();
        _ping = new PingWatch(now, pingTimeout);
        _keyClock = new KeyframeClock(now);
        _ladder = new AdaptiveLadder(_tailscale, _requestedFps, now, _codec == ScreenCodec.Jpeg);
        _lock.SetOsLocked(locked);
        pipeline.Frame += OnFrame;
        pipeline.Event += OnPipelineEvent;
    }

    public string RemoteIp { get; }
    public string PhoneName { get; }
    public string DisplayName { get { lock (_gate) return _display.Name; } }
    /// <summary>Why it ended (for the log), once it did.</summary>
    public string? EndReason { get; private set; }

    // ---------------- lifetime ----------------

    public async Task RunAsync(CancellationToken serverCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverCt, _cts.Token);
        var ct = linked.Token;
        lock (_gate)
        {
            StartPipeline(restartEncoder: true); // screenOpened first (when the display size is known)
            if (_lock.Paused) Text("screenPaused", new { reason = "locked" });
        }
        _signal.Release();
        var sender = Task.Run(() => SendLoopAsync(ct), CancellationToken.None);
        var ticker = Task.Run(() => TickLoopAsync(ct), CancellationToken.None);
        try
        {
            await ReceiveLoopAsync(ct).ConfigureAwait(false);
            EndReason ??= "phone closed";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException) { EndReason ??= "connection lost"; }
        finally
        {
            _pipeline.Frame -= OnFrame;
            _pipeline.Event -= OnPipelineEvent;
            try { _pipeline.Dispose(); } catch { }
            _signal.Release();
            try { await Task.WhenAny(sender, Task.Delay(1500, CancellationToken.None)).ConfigureAwait(false); } catch { }
            _cts.Cancel();
            try { _ws.Abort(); } catch { }
            try { await ticker.ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>Ends the session: sends the close frame (after anything already waiting) and stops.</summary>
    public void Close(int code, string reason, string? pausedReason = null)
    {
        lock (_gate)
        {
            if (_close is not null) return;
            if (pausedReason is not null) Text("screenPaused", new { reason = pausedReason });
            _close = (code, reason);
            EndReason ??= reason;
            _queue.Clear();
        }
        _signal.Release();
    }

    // ---------------- from the computer ----------------

    /// <summary>The computer locked or unlocked (WTS).</summary>
    public void SetOsLocked(bool locked)
    {
        lock (_gate)
        {
            if (!locked) _lock.SetCaptureBlocked(false); // a fresh start re-reports it if the secure desktop is still up
            Transition(_lock.SetOsLocked(locked), restart: !locked);
        }
        _signal.Release();
    }

    void Transition(ScreenTransition t, bool restart)
    {
        switch (t)
        {
            case ScreenTransition.Paused:
                _queue.Clear();
                Text("screenPaused", new { reason = "locked" });
                if (_lock.OsLocked) _pipeline.Pause();
                break;
            case ScreenTransition.Resumed:
                _queue.Clear();
                Text("screenResumed", new { });
                if (restart) StartPipeline(restartEncoder: true);
                else _pipeline.RequestKeyframe();
                break;
        }
    }

    void OnPipelineEvent(ScreenPipelineEvent e)
    {
        lock (_gate)
        {
            if (_close is not null) return;
            switch (e.Kind)
            {
                case ScreenPipelineEventKind.Blocked:
                    Transition(_lock.SetCaptureBlocked(true), restart: false);
                    break;
                case ScreenPipelineEventKind.Recovered:
                    Transition(_lock.SetCaptureBlocked(false), restart: false);
                    break;
                case ScreenPipelineEventKind.SourceChanged:
                    _display = _display with
                    {
                        Id = string.IsNullOrEmpty(_display.Id) ? e.DisplayId ?? "" : _display.Id,
                        Bounds = e.Display,
                    };
                    _log($"[remote] display {_display.Name} is now {e.Display.W}x{e.Display.H}");
                    StartPipeline(restartEncoder: true);
                    break;
                case ScreenPipelineEventKind.EncoderFailed when _codec == ScreenCodec.H264:
                    _log("[remote] no H.264 encoder here: sending JPEG");
                    _codec = ScreenCodec.Jpeg;
                    _ladder = new AdaptiveLadder(_tailscale, _requestedFps, _now(), jpeg: true);
                    _opened = false; // screenOpened says jpeg now
                    StartPipeline(restartEncoder: true);
                    break;
                case ScreenPipelineEventKind.EncoderFailed:
                case ScreenPipelineEventKind.Failed:
                    _log($"[remote] capture failed{(e.Detail is { Length: > 0 } d ? ": " + d : "")}");
                    _close = (1011, "capture failed");
                    EndReason ??= "capture failed";
                    break;
            }
        }
        _signal.Release();
    }

    /// <summary>(Re)starts capture with the current display, view, size and rate; sends screenOpened when the size is new.</summary>
    void StartPipeline(bool restartEncoder)
    {
        var crop = _display.Bounds.IsEmpty ? default : RemoteViewMath.Crop(_view, _display.Bounds.W, _display.Bounds.H);
        var (w, h) = crop.IsEmpty ? (0, 0) : RemoteViewMath.OutputSize(crop.W, crop.H, _maxW, _maxH);
        var config = new ScreenPipelineConfig(_display.Id, _display.Bounds, crop, w, h, _ladder.Fps, _ladder.Bitrate, _codec);
        var sizeChanged = _config is null || _config.OutW != w || _config.OutH != h || _config.Codec != _codec || _config.DisplayId != _display.Id
                          || _config.Display != _display.Bounds;
        _config = config;
        if (sizeChanged) _configs.Reset();
        if (sizeChanged || restartEncoder) _queue.Clear();
        if (w > 0 && (sizeChanged || !_opened))
        {
            _opened = true;
            Text("screenOpened", new Dictionary<string, object?>
            {
                ["display"] = _display.Id,
                ["w"] = w,
                ["h"] = h,
                ["codec"] = _codec == ScreenCodec.Jpeg ? "jpeg" : "h264",
            });
            if (!_openedLogged)
            {
                _openedLogged = true;
                _log($"[remote] opened by {PhoneName} display {_display.Name} {w}x{h} {(_codec == ScreenCodec.Jpeg ? "jpeg" : "h264")}");
            }
        }
        if (_lock.OsLocked) return; // resumes on unlock
        _pipeline.Start(config);
        _keyClock.Emitted(_now());
    }

    bool _openedLogged;

    void OnFrame(PipelineFrame f)
    {
        var now = _now();
        lock (_gate)
        {
            if (_close is not null || _lock.Paused || _config is not { } current) return;
            // A picture made for an older size or codec can't be decoded with the current config.
            if (f.Config.OutW != current.OutW || f.Config.OutH != current.OutH || f.Config.Codec != current.Codec || f.Config.DisplayId != current.DisplayId)
                return;
            var (cx, cy) = RemoteViewMath.CursorFraction(f.CursorX, f.CursorY, f.Config.Display, f.Config.Crop);
            var pts = (ulong)Math.Max(0, f.PtsMicros);
            if (f.IsJpeg)
            {
                if (_queue.Offer(new QueuedScreenFrame(ScreenFrameKind.Jpeg, f.Data, pts, cx, cy, f.CursorVisible)) == ScreenQueueResult.Replaced)
                    StepDown(now);
            }
            else
            {
                var (config, rest, isKey) = AnnexB.Split(f.Data);
                if (rest.Length == 0) return;
                if (isKey || f.IsKey)
                {
                    // Every keyframe goes out right behind its config (SPS + PPS), §15.3.
                    if (_configs.ForKeyframe(config) is not { } cfg) return; // no SPS/PPS yet: the phone couldn't decode it
                    _queue.Offer(new QueuedScreenFrame(ScreenFrameKind.Config, cfg, pts, cx, cy, f.CursorVisible));
                    _queue.Offer(new QueuedScreenFrame(ScreenFrameKind.Keyframe, rest, pts, cx, cy, f.CursorVisible));
                    _keyClock.Emitted(now);
                }
                else
                {
                    var r = _queue.Offer(new QueuedScreenFrame(ScreenFrameKind.Frame, rest, pts, cx, cy, f.CursorVisible));
                    if (r == ScreenQueueResult.Dropped)
                    {
                        _pipeline.RequestKeyframe();
                        StepDown(now);
                    }
                }
            }
        }
        _signal.Release();
    }

    void StepDown(DateTime now)
    {
        if (_ladder.OnDrop(now)) _pipeline.SetRate(_ladder.Bitrate, _ladder.Fps);
    }

    // ---------------- from the phone ----------------

    void HandleText(string type, JsonElement d)
    {
        var now = _now();
        lock (_gate)
        {
            switch (type)
            {
                case "screenPing":
                    _ping.Seen(now);
                    Text("screenPong", new Dictionary<string, object?> { ["t"] = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("t", out var t) ? t.Clone() : null });
                    break;
                case "screenView":
                    if (d.ValueKind == JsonValueKind.Object) _pendingView = ScreenOpenRequest.ParseView(d.Obj("view") ?? d);
                    break;
                case "screenDisplay":
                {
                    var next = Resolve(d.Str("display") ?? "");
                    _display = next;
                    _opened = false; // always a new screenOpened (+ config + keyframe) for a display switch
                    _view = RemoteView.Whole;
                    _pendingView = null;
                    StartPipeline(restartEncoder: true);
                    break;
                }
                case "screenKeyframe":
                    _pipeline.RequestKeyframe();
                    break;
                case "screenStats":
                    break; // the phone's own fps / rtt / decode time: the queue already tells us when it's behind
            }
        }
        _signal.Release();
    }

    RemoteDisplay Resolve(string id)
    {
        IReadOnlyList<RemoteDisplay> all;
        try { all = _displays(); }
        catch { all = Array.Empty<RemoteDisplay>(); }
        RemoteDisplay? hit = null;
        if (!string.IsNullOrEmpty(id)) foreach (var x in all) if (x.Id == id) { hit = x; break; }
        if (hit is null) foreach (var x in all) if (x.Main) { hit = x; break; }
        if (hit is null && all.Count > 0) hit = all[0];
        return hit ?? new RemoteDisplay("", "main display", default, true);
    }

    // ---------------- loops ----------------

    async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, result.Count);
                if (message.Length > 1024 * 1024) throw new IOException("Message too large");
            } while (!result.EndOfMessage);
            if (result.MessageType != WebSocketMessageType.Text) continue; // the phone sends no binary
            var plain = LinkCrypto.TryDecrypt(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length), _key);
            if (plain is null) continue;
            using var doc = LenientJson.TryParse(plain);
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) continue;
            var type = doc.RootElement.Str("type");
            if (string.IsNullOrEmpty(type)) continue;
            var data = doc.RootElement.TryGetProperty("data", out var dd) ? dd.Clone() : default;
            HandleText(type, data);
        }
    }

    async Task TickLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                var now = _now();
                lock (_gate)
                {
                    if (_close is not null) continue;
                    if (_ping.IsSilent(now))
                    {
                        _close = (1000, "no ping");
                        EndReason ??= "no ping for 5 s";
                        _signal.Release();
                        continue;
                    }
                    if (_lock.Paused) continue;
                    if (_pendingView is { } v && now - _lastViewApply >= ViewApplyEvery)
                    {
                        _pendingView = null;
                        _lastViewApply = now;
                        if (v != _view)
                        {
                            _view = v;
                            StartPipeline(restartEncoder: false);
                        }
                    }
                    if (_keyClock.Due(now))
                    {
                        _keyClock.Emitted(now);
                        _pipeline.RequestKeyframe();
                    }
                    if (_ladder.Tick(now)) _pipeline.SetRate(_ladder.Bitrate, _ladder.Fps);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
                while (true)
                {
                    string? text = null;
                    QueuedScreenFrame? frame = null;
                    (int Code, string Reason)? close = null;
                    uint seq = 0;
                    lock (_gate)
                    {
                        if (_text.Count > 0) text = _text.Dequeue();
                        else if (_close is null && _queue.TryDequeue(out var f)) { frame = f; seq = _seq++; }
                        else if (_close is { } c) close = c;
                    }
                    if (text is not null)
                    {
                        if (!await SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, ct).ConfigureAwait(false)) return;
                        continue;
                    }
                    if (frame is not null)
                    {
                        var header = new ScreenFrameHeader(frame.Kind, seq, frame.PtsMicros, frame.CursorX, frame.CursorY, frame.CursorVisible);
                        if (!await SendAsync(ScreenFraming.Seal(header, frame.Payload, _key), WebSocketMessageType.Binary, ct).ConfigureAwait(false)) return;
                        continue;
                    }
                    if (close is { } cl)
                    {
                        using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        try { await _ws.CloseOutputAsync((WebSocketCloseStatus)cl.Code, cl.Reason, t.Token).ConfigureAwait(false); } catch { }
                        _cts.CancelAfter(TimeSpan.FromSeconds(1)); // the phone answers the close, or we stop anyway
                        return;
                    }
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task<bool> SendAsync(byte[] bytes, WebSocketMessageType type, CancellationToken ct)
    {
        if (_ws.State != WebSocketState.Open) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SendTimeout);
        try
        {
            await _ws.SendAsync(bytes, type, true, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            EndReason ??= "send failed";
            _cts.Cancel();
            return false;
        }
    }

    /// <summary>Queues an encrypted text frame (caller holds the gate).</summary>
    void Text(string type, object? data)
    {
        try { _text.Enqueue(LinkCrypto.Encrypt(LinkServer.BuildEnvelope(type, data), _key)); }
        catch { /* never with a valid key */ }
    }
}
