// H.I.B Phone Remote on Windows (docs/SPACE.md §15): the main-link side (remoteHello, remoteLockState, remoteFocus and
// the unlock replies of phase 1), the /screen sessions (one at a time), Stop, keep-awake and the "Phone remote" settings.
// Input stays the existing remoteControl → DeskDeck → WindowsDesktopControl.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HIB.Core.Link;
using HIB.Core.Pairing;
using HIB.Core.Phone;

namespace HIB.Core.Remote;

public sealed class PhoneRemote : IDisposable
{
    readonly LinkServer _link;
    readonly AppSettings _settings;
    readonly Func<bool> _controlAvailable;
    readonly Func<IReadOnlyList<DisplayScreen>> _screens;
    readonly PhoneRemoteOptions _options;
    readonly IScreenPipelineFactory _pipelines;
    readonly IRemoteSystem _system;
    readonly object _gate = new();
    readonly RemoteStopGate _stopGate = new();
    readonly FocusDebouncer _focus = new();
    readonly Timer _poll;
    readonly Timer _focusTimer;
    readonly Lazy<string> _unlock;
    RemoteScreenSession? _active;
    IRemoteFocusSource? _focusSource;
    string? _lastHello;
    bool _locked;
    RemoteAwake _awake;
    bool _disposed;

    public PhoneRemote(LinkServer link, AppSettings settings, Func<bool> controlAvailable, Func<IReadOnlyList<DisplayScreen>> screens,
        PhoneRemoteOptions? options = null)
    {
        _link = link;
        _settings = settings;
        _controlAvailable = controlAvailable;
        _screens = screens;
        _options = options ?? new PhoneRemoteOptions();
        _pipelines = _options.Pipelines();
        _system = _options.System();
        _unlock = new Lazy<string>(() =>
        {
            try
            {
                var (domain, entra) = _system.JoinState();
                return RemoteUnlockAvailability.ForWindows(domain, entra);
            }
            catch { return RemoteUnlockAvailability.Unsupported; }
        });
        try { _locked = _system.IsLocked() ?? false; } catch { }
        link.ScreenHandler = HandleScreenSocketAsync;
        link.MessageReceived += OnMessage;
        link.ConnectionChanged += (_, connected) =>
        {
            if (!connected) lock (_gate) _lastHello = null;
        };
        _poll = new Timer(_ => Poll(), null, _options.PollInterval, _options.PollInterval);
        _focusTimer = new Timer(_ => PollFocus(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>The phone name while a /screen session is open, null when nobody is viewing. Background thread.</summary>
    public event Action<string?>? SessionChanged;
    /// <summary>A setting or the state shown in Settings changed.</summary>
    public event Action? StateChanged;
    public event Action<string>? Log;

    /// <summary>"Allow viewing and control" (default on).</summary>
    public bool Allowed
    {
        get => _settings.RemoteAllowed;
        set
        {
            if (_settings.RemoteAllowed == value) return;
            _settings.RemoteAllowed = value;
            _settings.Save();
            if (!value) CloseActive(RemoteCloseCodes.Stopped, "viewing turned off", "stopped");
            _ = SendHelloIfChangedAsync();
            StateChanged?.Invoke();
        }
    }

    /// <summary>"Stay reachable when locked" (default on once Unlock from phone is set up — never on Windows in phase 1).</summary>
    public bool StayReachable
    {
        get => _settings.RemoteStayReachable ?? false;
        set
        {
            _settings.RemoteStayReachable = value;
            _settings.Save();
            UpdateAwake();
            StateChanged?.Invoke();
        }
    }

    /// <summary>remoteHello.unlock: "managed" (domain / Entra joined) or "unsupported".</summary>
    public string UnlockState => _unlock.Value;

    public bool CanView => _pipelines.IsAvailable;
    public bool IsLocked { get { lock (_gate) return _locked; } }

    /// <summary>The phone viewing this computer now, or null.</summary>
    public string? ViewingPhone { get { lock (_gate) return _active?.PhoneName; } }

    // ---------------- remoteHello ----------------

    public Dictionary<string, object?> BuildHello()
    {
        var allowed = Allowed;
        IReadOnlyList<string> addrs;
        try { addrs = _options.TailscaleAddrs(); }
        catch { addrs = Array.Empty<string>(); }
        return new Dictionary<string, object?>
        {
            ["v"] = 1,
            ["id"] = _link.DesktopName,
            ["platform"] = "windows",
            ["screen"] = allowed && _pipelines.IsAvailable,
            ["control"] = allowed && _controlAvailable(),
            ["unlock"] = UnlockState,
            ["locked"] = IsLocked,
            ["displays"] = Displays().Select(d => new Dictionary<string, object?>
            {
                ["id"] = d.Id,
                ["name"] = d.Name,
                ["w"] = d.Bounds.W,
                ["h"] = d.Bounds.H,
                ["main"] = d.Main,
            }).ToArray(),
            ["addrs"] = addrs.ToArray(),
        };
    }

    /// <summary>This computer's displays (§7.1 ids) in desktop pixels.</summary>
    public IReadOnlyList<RemoteDisplay> Displays()
    {
        IReadOnlyList<DisplayScreen> screens;
        try { screens = _screens(); }
        catch { screens = Array.Empty<DisplayScreen>(); }
        return screens.Select(s =>
        {
            var scale = s.Scale > 0 ? s.Scale : 1;
            var w = s.PxW > 0 ? s.PxW : (int)Math.Round(s.W * scale);
            var h = s.PxH > 0 ? s.PxH : (int)Math.Round(s.H * scale);
            return new RemoteDisplay(s.Id, s.Name, new PixelBox((int)Math.Round(s.X * scale), (int)Math.Round(s.Y * scale), w, h), s.Primary);
        }).ToList();
    }

    async Task SendHelloAsync()
    {
        var hello = BuildHello();
        var sig = LinkServer.BuildEnvelope("remoteHello", hello);
        lock (_gate) _lastHello = sig;
        await _link.SendAsync("remoteHello", hello).ConfigureAwait(false);
    }

    /// <summary>"remoteHello is sent on request and whenever any field changes" (once the phone asked for one).</summary>
    async Task SendHelloIfChangedAsync()
    {
        if (!_link.IsConnected) return;
        var sig = LinkServer.BuildEnvelope("remoteHello", BuildHello());
        lock (_gate)
        {
            if (_lastHello is null || _lastHello == sig) return;
        }
        await SendHelloAsync().ConfigureAwait(false);
    }

    void OnMessage(object? sender, LinkMessageEventArgs e)
    {
        switch (e.Type)
        {
            case "remoteHelloRequest":
                _ = SendHelloAsync();
                break;
            // Phase 1: Windows has no sign-in add-on, so unlock is never set up here (remoteHello says managed / unsupported).
            case "unlockRequest":
            case "unlockApprove":
                _ = _link.SendAsync("unlockResult", new Dictionary<string, object?> { ["ok"] = false, ["error"] = "notSetUp" });
                Write("[remote] unlock failed notSetUp");
                break;
            case "unlockEnroll":
                _ = _link.SendAsync("unlockEnrollResult", new Dictionary<string, object?> { ["ok"] = false });
                break;
        }
    }

    // ---------------- lock state, power, displays ----------------

    /// <summary>The session locked (true) or unlocked (WTS notification, or the poll).</summary>
    public void ReportLock(bool locked)
    {
        RemoteScreenSession? active;
        lock (_gate)
        {
            if (_disposed || _locked == locked) return;
            _locked = locked;
            active = _active;
        }
        Write(locked ? "[remote] computer locked" : "[remote] computer unlocked");
        if (_link.IsConnected) _ = _link.SendAsync("remoteLockState", new Dictionary<string, object?> { ["locked"] = locked });
        active?.SetOsLocked(locked);
        UpdateAwake();
        _ = SendHelloIfChangedAsync();
        StateChanged?.Invoke();
    }

    void Poll()
    {
        if (_disposed) return;
        try
        {
            if (_system.IsLocked() is { } locked) ReportLock(locked);
            UpdateAwake();
            _ = SendHelloIfChangedAsync();
        }
        catch (Exception ex) { Write("[remote] poll: " + ex.Message); }
    }

    void UpdateAwake()
    {
        RemoteAwake need;
        lock (_gate)
        {
            bool onMains;
            try { onMains = _system.OnMainsPower; }
            catch { onMains = true; }
            need = RemoteAwakePolicy.Need(_active is not null, StayReachable, _locked, onMains);
            if (need == _awake) return;
            _awake = need;
        }
        try { _system.SetAwake(need); } catch { }
    }

    // ---------------- /screen ----------------

    async Task HandleScreenSocketAsync(WebSocket ws, TcpClient client, string remote, CancellationToken ct)
    {
        var key = _settings.LinkKeyBytes();
        ScreenOpenRequest? request = null;
        if (key is not null)
        {
            // Never cancel a WebSocket receive (that aborts the socket and the 4003 couldn't be sent): race it instead.
            var read = ReadOpenAsync(ws, key, ct);
            if (await Task.WhenAny(read, Task.Delay(_options.OpenTimeout, ct)).ConfigureAwait(false) == read)
            {
                try { request = await read.ConfigureAwait(false); }
                catch { request = null; }
            }
            if (request is null)
            {
                await CloseAsync(ws, RemoteCloseCodes.BadKey, "bad key", read.IsCompleted ? null : read).ConfigureAwait(false);
                return;
            }
        }
        if (request is null || key is null)
        {
            await CloseAsync(ws, RemoteCloseCodes.BadKey, "bad key").ConfigureAwait(false);
            return;
        }
        if (!_pipelines.IsAvailable)
        {
            await CloseAsync(ws, RemoteCloseCodes.NotPermitted, "no screen capture here").ConfigureAwait(false);
            return;
        }

        var phone = _link.Device?.Name is { Length: > 0 } n ? n : "your phone";
        RemoteScreenSession session;
        RemoteScreenSession? replaced = null;
        lock (_gate)
        {
            var admit = RemoteAdmission.Decide(Allowed, _stopGate.IsRefusing(_options.Clock()), _active?.RemoteIp, remote);
            if (admit is ScreenAdmission.RefuseOff or ScreenAdmission.RefuseStopped or ScreenAdmission.RefuseBusy)
            {
                session = null!;
                _ = CloseAsync(ws, RemoteAdmission.CloseCode(admit),
                    admit == ScreenAdmission.RefuseBusy ? "another phone is viewing" : admit == ScreenAdmission.RefuseOff ? "viewing is off" : "stopped");
                Write($"[remote] refused {remote} ({admit})");
                return;
            }
            if (admit == ScreenAdmission.ReplaceSamePhone) replaced = _active;
            IScreenPipeline pipeline;
            try { pipeline = _pipelines.Create(); }
            catch (Exception ex)
            {
                Write("[remote] capture unavailable: " + ex.Message);
                _ = CloseAsync(ws, 1011, "capture unavailable");
                return;
            }
            session = new RemoteScreenSession(ws, key, remote, phone, request, pipeline, Displays, _locked, _options.PingTimeout, _options.Clock, Write);
            _active = session;
        }
        replaced?.Close(1000, "replaced");
        OnSessionStarted(session);
        try { await session.RunAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { Write("[remote] session error: " + ex.Message); }
        finally
        {
            var ended = false;
            lock (_gate)
            {
                if (ReferenceEquals(_active, session))
                {
                    _active = null;
                    ended = true;
                }
            }
            Write($"[remote] closed ({session.EndReason ?? "closed"})");
            if (ended) OnSessionEnded();
        }
    }

    static async Task<ScreenOpenRequest?> ReadOpenAsync(WebSocket ws, byte[] key, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var total = new List<byte>();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            total.AddRange(buffer.AsSpan(0, r.Count).ToArray());
            if (total.Count > 64 * 1024) return null;
        } while (!r.EndOfMessage);
        if (r.MessageType != WebSocketMessageType.Text) return null;
        var plain = LinkCrypto.TryDecrypt(Encoding.UTF8.GetString(total.ToArray()), key);
        if (plain is null) return null;
        using var doc = LenientJson.TryParse(plain);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object || doc.RootElement.Str("type") != "screenOpen") return null;
        return doc.RootElement.TryGetProperty("data", out var d) ? ScreenOpenRequest.Parse(d) : ScreenOpenRequest.Parse(JsonDocument.Parse("{}").RootElement);
    }

    /// <summary>Sends a close frame and waits (≤ 2 s) for the phone's answer before dropping the connection.</summary>
    static async Task CloseAsync(WebSocket ws, int code, string reason, Task? pendingReceive = null)
    {
        try
        {
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.CloseOutputAsync((WebSocketCloseStatus)code, reason, t.Token).ConfigureAwait(false);
            if (pendingReceive is not null)
            {
                // A receive is already running (it will see the phone's close); only one may run at a time.
                await Task.WhenAny(pendingReceive, Task.Delay(2000, CancellationToken.None)).ConfigureAwait(false);
            }
            else
            {
                var buf = new byte[256];
                while (ws.State is WebSocketState.CloseSent or WebSocketState.Open)
                {
                    var r = await Task.WhenAny(ws.ReceiveAsync(buf, CancellationToken.None), Task.Delay(2000, CancellationToken.None)).ConfigureAwait(false);
                    if (r is not Task<WebSocketReceiveResult> done || done.Result.MessageType == WebSocketMessageType.Close) break;
                }
            }
        }
        catch { }
        try { ws.Abort(); } catch { }
    }

    void OnSessionStarted(RemoteScreenSession session)
    {
        UpdateAwake();
        StartFocus();
        SessionChanged?.Invoke(session.PhoneName);
        StateChanged?.Invoke();
    }

    void OnSessionEnded()
    {
        StopFocus();
        UpdateAwake();
        SessionChanged?.Invoke(null);
        StateChanged?.Invoke();
    }

    /// <summary>The pill's Stop: ends the stream now and refuses new ones for 60 s (4012).</summary>
    public void Stop()
    {
        lock (_gate) _stopGate.Stop(_options.Clock());
        CloseActive(RemoteCloseCodes.Stopped, "stopped", "stopped");
        Write("[remote] Stop pressed");
    }

    void CloseActive(int code, string reason, string? pausedReason)
    {
        RemoteScreenSession? active;
        lock (_gate) active = _active;
        active?.Close(code, reason, pausedReason);
    }

    // ---------------- focus → keyboard ----------------

    void StartFocus()
    {
        IRemoteFocusSource? source;
        try { source = _system.CreateFocusSource(); }
        catch { source = null; }
        lock (_gate)
        {
            _focusSource?.Dispose();
            _focusSource = source;
            _focus.Reset();
        }
        _focusTimer.Change(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
        if (source is null) return;
        source.Changed += facts =>
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_focusSource, source)) return;
                _focus.Offer(RemoteFocusRules.Classify(facts), _options.Clock());
            }
        };
        try { source.Start(); } catch (Exception ex) { Write("[remote] focus watcher: " + ex.Message); }
    }

    void StopFocus()
    {
        IRemoteFocusSource? source;
        lock (_gate)
        {
            source = _focusSource;
            _focusSource = null;
        }
        try { _focusTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        try { source?.Dispose(); } catch { }
    }

    void PollFocus()
    {
        RemoteFocusState? send;
        lock (_gate)
        {
            if (_active is null) return;
            send = _focus.Poll(_options.Clock());
        }
        if (send is { } s) _ = _link.SendAsync("remoteFocus", new Dictionary<string, object?> { ["text"] = s.Text, ["secure"] = s.Secure });
    }

    /// <summary>Tests: what a focus watcher would report.</summary>
    internal void OfferFocusForTest(FocusFacts? facts)
    {
        lock (_gate) _focus.Offer(RemoteFocusRules.Classify(facts), _options.Clock());
    }

    void Write(string line)
    {
        try { Log?.Invoke(line); } catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _poll.Dispose();
        _focusTimer.Dispose();
        CloseActive(1001, "shutting down", null);
        StopFocus();
        try { _system.SetAwake(RemoteAwake.None); } catch { }
        try { _system.Dispose(); } catch { }
    }
}
