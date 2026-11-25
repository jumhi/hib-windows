// H.I.B Space (docs/SPACE.md §3.3): accepts and dials peer links, keeps one per computer.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace HIB.Core.Space;

public sealed class PeerNetworkOptions
{
    public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Drop a link after this long with no frames.</summary>
    public TimeSpan DropAfter { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Server side: a decryptable first frame must arrive within this (else close 4003).</summary>
    public TimeSpan FirstFrameTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan HelloTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Retry delays after failed dials: 5 s, 10 s, 20 s, … capped at 60 s.</summary>
    public TimeSpan[] Backoff { get; set; } = { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60) };
    /// <summary>How often the dialler looks for peers without a link.</summary>
    public TimeSpan DialTick { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>
    /// The computer whose id sorts higher waits this long before dialling, so usually only one link opens
    /// (the Mac does the same; the duplicate rule still settles it if both dial).
    /// </summary>
    public TimeSpan PoliteDelay { get; set; } = TimeSpan.FromSeconds(1.5);
    /// <summary>The wall clock the keepalive judges silence by (tests move it, e.g. to stand in for a sleep).</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
    /// <summary>What a linked computer's <c>peerDiag</c> gets (tests inject; default: the tail of hib.log).</summary>
    public Func<string>? DiagnosticsLog { get; set; }
}

/// <summary>
/// The set of live peer links. Dials every known peer that has no link (all its IPs in parallel,
/// the first to open wins), backs off on failure, answers the server side, and resolves duplicates:
/// both computers keep the link whose client id sorts lower (UTF-8 bytes) and close the other with 4009.
/// </summary>
public sealed class PeerNetwork : IDisposable
{
    sealed class DialState
    {
        public int Failures;
        public DateTime NextAttempt = DateTime.MinValue;
        public bool InFlight;
    }

    readonly Func<byte[]?> _key;
    readonly Func<PeerHello> _ownHello;
    readonly Func<IEnumerable<KnownPeer>> _known;
    readonly PeerNetworkOptions _options;
    readonly object _gate = new();
    readonly Dictionary<string, PeerLink> _active = new(StringComparer.Ordinal);
    readonly HashSet<PeerLink> _all = new();
    readonly Dictionary<string, DialState> _dial = new(StringComparer.Ordinal);
    CancellationTokenSource? _cts;

    public PeerNetwork(Func<byte[]?> key, Func<PeerHello> ownHello, Func<IEnumerable<KnownPeer>> known, PeerNetworkOptions? options = null)
    {
        _key = key;
        _ownHello = ownHello;
        _known = known;
        _options = options ?? new PeerNetworkOptions();
    }

    public PeerNetworkOptions Options => _options;

    /// <summary>A link to a computer came up (after the hello, duplicates already resolved).</summary>
    public event Action<PeerLink>? LinkUp;
    /// <summary>The active link to a computer went away (not raised for duplicates that lost the tie-break).</summary>
    public event Action<PeerLink>? LinkDown;
    /// <summary>Every completed hello, with the address it came from (to remember the peer).</summary>
    public event Action<PeerLink>? HelloReceived;
    /// <summary>peerActivity arrived on an active link.</summary>
    public event Action<PeerLink>? ActivityChanged;
    public event Action<string>? Log;

    public bool Running => _cts is not null;

    /// <summary>The live link per peer id.</summary>
    public IReadOnlyList<PeerLink> Links { get { lock (_gate) return _active.Values.Where(l => l.IsOpen).ToList(); } }

    public PeerLink? Get(string id)
    {
        lock (_gate) return _active.TryGetValue(id, out var l) && l.IsOpen ? l : null;
    }

    public bool IsLinked(string id) => Get(id) is not null;

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _ = DialLoopAsync(_cts.Token);
        _ = KeepAliveLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        cts?.Cancel();
        DropAll("Stopping");
    }

    public void Dispose() => Stop();

    /// <summary>Closes every link (the space key changed, or the app is quitting).</summary>
    public void DropAll(string reason)
    {
        List<PeerLink> all;
        lock (_gate) all = _all.ToList();
        foreach (var l in all) _ = l.CloseAsync(1000, reason);
    }

    /// <summary>Retry now: a desktopsList or peerHello came in (SPACE.md: resets the backoff).</summary>
    public void ResetBackoff(string? id = null)
    {
        var own = _ownHello().Id;
        lock (_gate)
        {
            foreach (var (peer, st) in _dial)
            {
                if (id is not null && !SpaceIds.Same(peer, id)) continue;
                st.Failures = 0;
                st.NextAttempt = FirstAttempt(own, peer);
            }
        }
    }

    /// <summary>Now for the lower id; the higher id waits <see cref="PeerNetworkOptions.PoliteDelay"/>.</summary>
    DateTime FirstAttempt(string own, string peer)
        => SpaceIds.Compare(own, peer) > 0 ? DateTime.UtcNow + _options.PoliteDelay : DateTime.MinValue;

    // ---------------- server side ----------------

    /// <summary>Runs one accepted <c>/peer</c> session (LinkServer.PeerHandler).</summary>
    public async Task HandleIncomingAsync(WebSocket ws, TcpClient client, string remoteIp, CancellationToken ct)
    {
        var key = _key();
        if (key is null || _cts is null)
        {
            try { await ws.CloseOutputAsync((WebSocketCloseStatus)PeerLink.CloseDecryptFailed, "No space key", CancellationToken.None).ConfigureAwait(false); } catch { }
            return;
        }
        var link = Create(ws, client, key, isClient: false, remoteIp);
        await link.RunAsync(_options.FirstFrameTimeout, _options.HelloTimeout, ct).ConfigureAwait(false);
    }

    PeerLink Create(WebSocket ws, IDisposable? owner, byte[] key, bool isClient, string remoteIp)
    {
        var link = new PeerLink(ws, owner, key, _ownHello(), isClient, remoteIp, _options.Clock) { Log = WriteLog, Admit = AdmitLink, DiagnosticsLog = _options.DiagnosticsLog };
        lock (_gate) _all.Add(link);
        link.HelloCompleted += OnHello;
        link.Closed += OnClosed;
        link.ActivityChanged += l =>
        {
            if (Get(l.PeerId) == l) ActivityChanged?.Invoke(l);
        };
        return link;
    }

    /// <summary>
    /// The first hello on a link: keep it or not (SPACE.md §3.3, the Mac's PeerDedup). Runs before a server
    /// replies with its own hello, so a losing duplicate is closed with 4009 and never hears from us.
    /// </summary>
    bool AdmitLink(PeerLink link)
    {
        try { HelloReceived?.Invoke(link); } catch { }
        lock (_gate)
        {
            if (_active.TryGetValue(link.PeerId, out var existing) && !ReferenceEquals(existing, link) && existing.IsAlive)
            {
                if (!KeepFirst(link, existing))
                {
                    WriteLog($"Peer {link.PeerName}: duplicate link closed (4009)");
                    return false;
                }
                link.Replaced = existing;
            }
            _active[link.PeerId] = link;
            if (_dial.TryGetValue(link.PeerId, out var st))
            {
                st.Failures = 0;
                st.NextAttempt = DateTime.MinValue;
            }
            return true;
        }
    }

    /// <summary>The kept link finished its hello: retire the one it replaced and announce it.</summary>
    void OnHello(PeerLink link)
    {
        if (Interlocked.Exchange(ref link.Replaced, null) is { } loser)
        {
            WriteLog($"Peer {link.PeerName}: duplicate link closed (4009)");
            _ = loser.CloseAsync(PeerLink.CloseDuplicate, "duplicate");
        }
        WriteLog($"Peer link up: {link}");
        try { LinkUp?.Invoke(link); } catch (Exception ex) { WriteLog("LinkUp handler: " + ex.Message); }
    }

    /// <summary>
    /// SPACE.md §3.3 duplicates, exactly like the Mac's PeerDedup.resolve: keep the link whose client id sorts
    /// lower (UTF-8 bytes); two links from the same client (parallel IP dials, a redial) keep the existing one.
    /// </summary>
    public static bool KeepFirst(PeerLink candidate, PeerLink existing)
        => SpaceIds.Compare(candidate.ClientId, existing.ClientId) < 0;

    /// <summary>The tie-break on ids alone: true when the link dialled by <paramref name="clientA"/> survives.</summary>
    public static bool KeepsLinkDialledBy(string clientA, string clientB) => SpaceIds.Compare(clientA, clientB) < 0;

    void OnClosed(PeerLink link)
    {
        var wasActive = false;
        lock (_gate)
        {
            _all.Remove(link);
            if (link.PeerId.Length > 0 && _active.TryGetValue(link.PeerId, out var cur) && ReferenceEquals(cur, link))
            {
                _active.Remove(link.PeerId);
                wasActive = link.WasUp;
                if (_dial.TryGetValue(link.PeerId, out var st))
                    st.NextAttempt = DateTime.UtcNow + TimeSpan.FromSeconds(1) + (SpaceIds.Compare(_ownHello().Id, link.PeerId) > 0 ? _options.PoliteDelay : TimeSpan.Zero);
            }
        }
        if (wasActive)
        {
            WriteLog($"Peer link down: {link} ({link.CloseCode})");
            try { LinkDown?.Invoke(link); } catch { }
        }
    }

    // ---------------- dialling ----------------

    async Task DialLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_options.DialTick, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (_key() is null) continue;
            var own = _ownHello().Id;
            foreach (var peer in _known())
            {
                if (SpaceIds.Same(peer.Id, own) || peer.Port <= 0 || peer.Ips.Count == 0) continue;
                DialState st;
                lock (_gate)
                {
                    if (_active.TryGetValue(peer.Id, out var l) && l.IsAlive) continue;
                    if (!_dial.TryGetValue(peer.Id, out st!)) _dial[peer.Id] = st = new DialState { NextAttempt = FirstAttempt(own, peer.Id) };
                    if (st.InFlight || DateTime.UtcNow < st.NextAttempt) continue;
                    st.InFlight = true;
                }
                _ = DialAsync(peer, st, ct);
            }
        }
    }

    async Task DialAsync(KnownPeer peer, DialState st, CancellationToken ct)
    {
        var ok = false;
        try
        {
            ok = await DialOnceAsync(peer, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { WriteLog($"Dial {peer.Name}: {ex.Message}"); }
        finally
        {
            lock (_gate)
            {
                st.InFlight = false;
                if (!ok)
                {
                    var delay = _options.Backoff[Math.Min(st.Failures, _options.Backoff.Length - 1)];
                    st.Failures++;
                    st.NextAttempt = DateTime.UtcNow + delay;
                }
            }
        }
    }

    /// <summary>Tries every IP in parallel; keeps the first socket that opens and says hello.</summary>
    async Task<bool> DialOnceAsync(KnownPeer peer, CancellationToken ct)
    {
        var key = _key();
        if (key is null) return false;
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        race.CancelAfter(_options.ConnectTimeout);
        var attempts = peer.Ips.Distinct().Select(ip => ConnectAsync(ip, peer.Port, race.Token)).ToList();
        ClientWebSocket? winner = null;
        string? winnerIp = null;
        while (attempts.Count > 0)
        {
            var done = await Task.WhenAny(attempts).ConfigureAwait(false);
            attempts.Remove(done);
            var (ws, ip) = await done.ConfigureAwait(false);
            if (ws is null) continue;
            if (winner is null)
            {
                winner = ws;
                winnerIp = ip;
                race.Cancel(); // the others give up
            }
            else
            {
                ws.Dispose();
            }
        }
        if (winner is null) return false;

        lock (_gate)
        {
            // A server-side link may have come up while we dialled: no need for a second one.
            if (_active.TryGetValue(peer.Id, out var l) && l.IsOpen)
            {
                winner.Dispose();
                return true;
            }
        }
        var link = Create(winner, winner, key, isClient: true, winnerIp!);
        var helloDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.HelloCompleted += _ => helloDone.TrySetResult(true);
        link.Closed += _ => helloDone.TrySetResult(false);
        _ = Task.Run(() => link.RunAsync(_options.FirstFrameTimeout, _options.HelloTimeout, _cts?.Token ?? CancellationToken.None), CancellationToken.None);
        if (!await link.SendHelloAsync().ConfigureAwait(false))
        {
            await link.CloseAsync(1000, "").ConfigureAwait(false);
            return false;
        }
        return await helloDone.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Dials with TCP_NODELAY on (SPACE.md §9.3: Roam's small ctl* frames must not wait for Nagle). The server side
    /// sets it on accept (LinkServer).
    /// </summary>
    static readonly HttpMessageInvoker Dialer = new(new SocketsHttpHandler
    {
        ConnectCallback = (context, ct) => ConnectNoDelayAsync(context.DnsEndPoint, ct),
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.Zero,
    });

    /// <summary>Opens the TCP connection for a peer dial with Nagle off.</summary>
    public static async ValueTask<Stream> ConnectNoDelayAsync(DnsEndPoint endPoint, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endPoint, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    async Task<(ClientWebSocket? ws, string ip)> ConnectAsync(string ip, int port, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        ws.Options.DangerousDeflateOptions = null; // SPACE.md §12.3: compression off on the peer link
        try
        {
            var host = IPAddress.TryParse(ip, out var addr) && addr.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip;
            await ws.ConnectAsync(new Uri($"ws://{host}:{port}/peer"), Dialer, ct).ConfigureAwait(false);
            return (ws, ip);
        }
        catch
        {
            ws.Dispose();
            return (null, ip);
        }
    }

    // ---------------- keepalive ----------------

    /// <summary>
    /// Pings every live link each <see cref="PeerNetworkOptions.PingInterval"/> and drops one silent for
    /// <see cref="PeerNetworkOptions.DropAfter"/>. A tick that comes far too late means this computer was asleep (or the
    /// process frozen): the time asleep isn't the peer's silence, so silence is counted from the wake, after a ping
    /// (the Mac's 2026-11-25 log: every "no frames … for 30 s" was logged within a second of that Mac waking).
    /// </summary>
    async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.PingInterval);
        var lastTick = _options.Clock();
        var wokeAt = DateTime.MinValue;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                List<PeerLink> links;
                lock (_gate) links = _all.ToList();
                var now = _options.Clock();
                var gap = now - lastTick;
                lastTick = now;
                if (gap > _options.PingInterval * 3)
                {
                    wokeAt = now;
                    WriteLog($"[peer] woke after {gap.TotalSeconds:0}s without a keepalive tick: pinging before judging silence");
                }
                foreach (var l in links)
                {
                    var heard = l.LastActivity > wokeAt ? l.LastActivity : wokeAt;
                    if (now - heard > _options.DropAfter)
                    {
                        WriteLog($"Peer {l.PeerName}: silent for {(now - l.LastActivity).TotalSeconds:0}s — dropping");
                        _ = l.CloseAsync(1001, "timeout");
                    }
                    else if (l.IsOpen)
                    {
                        _ = l.SendAsync("ping");
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    void WriteLog(string line)
    {
        try { Log?.Invoke(line); } catch { }
    }
}
