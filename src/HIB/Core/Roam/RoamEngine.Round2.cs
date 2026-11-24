// H.I.B Roam, round 2 in the engine (docs/SPACE.md §12): what real use showed.
//  - §12.1 a PC with no mouse shows the pointer while it is driven (Mouse Keys, restored on release / exit / next start).
//  - §12.2 keys reach the target or say why: the UIPI pill, SendInput failures and the key-path counters in the log.
//  - §12.3 smoother: the target injects on its own high-priority thread; the latency readout.
//  - §12.4 game mode: this PC's app in front goes out as ctlTargetState; the other computers' states steer the controller.
//  - §12.5 lock together, keep awake while driven, per-computer speeds, Roam options.
// System calls that may block (Mouse Keys broadcasts a settings change) run in order on a background chain, never under the
// engine's lock and never on the network or input threads.
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HIB.Core.Link;
using HIB.Core.Space;

namespace HIB.Core.Roam;

public sealed partial class RoamEngine
{
    readonly IRoamSystem _system;
    readonly RoamInjector _injector;
    readonly MouseKeysKeeper _mouseKeys;
    readonly RoamKeyStats _stats = new();
    readonly LockTogether _lockTogether = new();
    readonly object _chainGate = new();
    Task _systemChain = Task.CompletedTask;
    Timer? _housekeeping;
    RoamGameMode _localGame;
    string _localApp = "";
    RoamPill? _pill;
    int _housekeepingBusy;

    /// <summary>A pill to show (null hides it): gate, game mode, locked, admin app (§12.2, §12.4, §12.5). Any thread.</summary>
    public event Action<RoamPill?>? PillChanged;

    public RoamPill? CurrentPill { get { lock (_gate) return _pill; } }

    /// <summary>The key path so far (§12.2; tests and diagnostics).</summary>
    public RoamKeyStats KeyStats => _stats;

    public IRoamSystem System => _system;

    /// <summary>H.I.B holds Mouse Keys on to show the pointer here (§12.1).</summary>
    public bool MouseKeysHeld => _mouseKeys.Active;

    /// <summary>This PC's app in front, as the other computers hear it (§12.4).</summary>
    public RoamGameMode LocalGame { get { lock (_gate) return _localGame; } }

    /// <summary>The lock hotkey holds the mouse where it is (§12.5).</summary>
    public bool UserLock { get { lock (_gate) return _controller.UserLock; } }

    public RoamOptionsSettings Options => _store.Options;

    /// <summary>Waits until everything received so far was injected (tests).</summary>
    public bool DrainInput(TimeSpan timeout) => _injector.Drain(timeout);

    void StartRound2()
    {
        _injector.AdminAppInFront += OnAdminApp;
        _system.DevicesChanged += OnDevicesChanged;
        _system.SessionLockChanged += OnSessionLock;
        SystemDo(() => _mouseKeys.RecoverAfterCrash());
        _housekeeping = new Timer(_ => Housekeep(), null, _options.Housekeeping, _options.Housekeeping);
    }

    void StopRound2()
    {
        try { _housekeeping?.Dispose(); } catch { }
        _injector.Drain(TimeSpan.FromSeconds(1));
        _injector.Dispose();
        try { WaitChain(TimeSpan.FromSeconds(2)); } catch { }
        try { _mouseKeys.Restore(); } catch { }
        try { _system.KeepAwake(false); } catch { }
        _system.DevicesChanged -= OnDevicesChanged;
        _system.SessionLockChanged -= OnSessionLock;
        try { _system.Dispose(); } catch { }
    }

    /// <summary>Runs <paramref name="action"/> after the system calls queued before it, off every input and network thread.</summary>
    void SystemDo(Action action)
    {
        lock (_chainGate)
        {
            _systemChain = _systemChain.ContinueWith(_ =>
            {
                try { action(); }
                catch (Exception ex) { WriteLog("Roam: " + ex.Message); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// <summary>Waits for the system calls queued so far (tests, quitting).</summary>
    public bool WaitChain(TimeSpan timeout)
    {
        Task t;
        lock (_chainGate) t = _systemChain;
        return t.Wait(timeout);
    }

    // ---------------- driven by another computer (§12.1, §12.5) ----------------

    /// <summary>Another computer starts driving this PC (under the lock).</summary>
    void StartControlled()
    {
        SystemDo(() => _mouseKeys.SetControlled(true));
        SystemDo(() => _system.KeepAwake(true));
    }

    /// <summary>It stopped (under the lock).</summary>
    void EndControlled()
    {
        SystemDo(() => _mouseKeys.SetControlled(false));
        SystemDo(() => _system.KeepAwake(false));
    }

    void OnDevicesChanged() => SystemDo(() => _mouseKeys.DevicesChanged());

    // ---------------- §12.2 the app in front runs as administrator ----------------

    void OnAdminApp(string app)
    {
        Locked(() =>
        {
            WriteLog($"Roam: {app} runs as administrator — Windows drops H.I.B's typing and clicks there (UIPI)");
            ShowPill(new RoamPill(RoamPillKind.AdminApp, UipiGuard.PillText, CursorHint()));
        });
    }

    /// <summary>The bottom of the monitor the remote cursor is on (pills on this PC while it is driven).</summary>
    RoamEdgeHint? CursorHint()
    {
        if (_displays.Count == 0) return null;
        var (px, py) = RoamGeometry.DipToPixel(_remoteCursor.X, _remoteCursor.Y, _displays);
        var screens = _displays.Select(d => d.Pixels).ToList();
        var screen = screens[Math.Max(0, RoamGeometry.ScreenAt(px, py, screens))];
        return new RoamEdgeHint(RoamSide.Below, _controlledBy ?? "", Own, screen, screen.X + screen.W / 2);
    }

    void ShowPill(RoamPill? pill)
    {
        if (pill is null && _pill is null) return;
        _pill = pill;
        After(() => PillChanged?.Invoke(pill));
    }

    // ---------------- §12.4 game mode ----------------

    RoamGameMode GameOf(Peer p) => _store.Options.GameMode ? p.Game : RoamGameMode.Normal;

    void OnTargetState(Peer p, JsonElement d)
    {
        var mode = RoamGameModes.Parse(d.Str("mode"));
        var app = d.Str("app") ?? "";
        if (mode == p.Game && app == p.GameApp) return;
        p.Game = mode;
        p.GameApp = app;
        WriteLog(mode == RoamGameMode.Normal ? $"Roam: {p.Name} has no full-screen app in front now"
            : $"Roam: {p.Name} runs {(app.Length > 0 ? app : "an app")} {(mode == RoamGameMode.Locked ? "holding the cursor (game mode)" : "full screen")}");
        Recompute();
    }

    void SendTargetState(Peer p)
    {
        if (p.Link is not { IsOpen: true }) return;
        Enqueue(p, RoamWire.TargetState, RoamWire.TargetStateData(_localGame, _localApp));
    }

    /// <summary>This PC's app in front changed (under the lock): tell every linked computer; the controller closes its edges.</summary>
    void SetLocalGame(RoamGameMode mode, string app)
    {
        if (mode == _localGame && app == _localApp) return;
        _localGame = mode;
        _localApp = app;
        _controller.SetLocalGame(mode);
        WriteLog(mode == RoamGameMode.Normal ? "Roam: no full-screen app in front here now"
            : $"Roam: {app} is in front {(mode == RoamGameMode.Locked ? "holding the cursor" : "full screen")} — edges need a double push at the top");
        foreach (var p in _peers.Values) SendTargetState(p);
    }

    /// <summary>For tests and the app: what this PC's app in front is doing now (normally the housekeeping timer looks).</summary>
    public void RefreshForeground()
    {
        RoamForeground? front = null;
        try { front = _system.Foreground(); } catch { }
        var on = _store.Options.GameMode;
        var mode = on ? RoamGameModes.Classify(front) : RoamGameMode.Normal;
        var app = mode == RoamGameMode.Normal ? "" : front?.App ?? "";
        Locked(() => SetLocalGame(mode, app));
    }

    // ---------------- §12.5 lock together ----------------

    void OnSessionLock(bool locked)
    {
        Locked(() =>
        {
            var tell = _lockTogether.OnLocal(locked, _store.Options.LockTogether, _clock());
            if (!locked) return;
            if (_controller.Mode == RoamMode.Away) _controller.ReleaseWithLeave(); // the mouse comes home to the locked PC
            if (!tell) return;
            var sent = 0;
            foreach (var p in _peers.Values)
            {
                if (p.Link is not { IsOpen: true } || p.Remote is not { Enabled: true } || !_store.Get(p.Id).Enabled) continue;
                Enqueue(p, RoamWire.ScreenLock, RoamWire.ScreenLockData());
                sent++;
            }
            if (sent > 0) WriteLog($"Roam: this PC locked — locking {sent} other computer{(sent == 1 ? "" : "s")} too");
        });
    }

    /// <summary>For tests and the app: this PC's screen was locked (true) or unlocked.</summary>
    public void SessionLocked(bool locked) => OnSessionLock(locked);

    void OnScreenLock(Peer p)
    {
        if (!_store.Get(p.Id).Enabled) return;
        if (!_lockTogether.OnRemote(_store.Options.LockTogether, _clock())) return;
        WriteLog($"Roam: {p.Name} locked its screen — locking this PC too");
        if (_controller.Mode == RoamMode.Away) _controller.ReleaseWithLeave();
        SystemDo(() => _system.LockScreen());
    }

    // ---------------- options and per-computer speeds ----------------

    public void SetGameMode(bool on) => SetOptions(o => o with { GameMode = on });

    public void SetLockTogether(bool on) => SetOptions(o => o with { LockTogether = on });

    void SetOptions(Func<RoamOptionsSettings, RoamOptionsSettings> edit)
    {
        _store.SetOptions(edit(_store.Options));
        RefreshForeground();
        Locked(Recompute);
    }

    public void SetPointerSpeed(string peerId, double speed) => Tune(peerId, s => s with { PointerSpeed = RoamSpeed.Clamp(speed) });

    public void SetScrollSpeed(string peerId, double speed) => Tune(peerId, s => s with { ScrollSpeed = RoamSpeed.Clamp(speed) });

    public void SetNaturalScroll(string peerId, bool on) => Tune(peerId, s => s with { NaturalScroll = on });

    /// <summary>A choice only this PC uses (no ctlHello).</summary>
    void Tune(string peerId, Func<RoamPairSettings, RoamPairSettings> edit)
    {
        Locked(() =>
        {
            var before = _store.Get(peerId);
            var after = edit(before);
            if (after == before) return;
            _store.Set(peerId, after);
            Recompute();
        });
    }

    // ---------------- housekeeping ----------------

    void Housekeep()
    {
        if (_disposed || Interlocked.Exchange(ref _housekeepingBusy, 1) == 1) return;
        try
        {
            bool linked;
            lock (_gate) linked = _peers.Values.Any(p => p.Link is { IsOpen: true });
            if (linked) RefreshForeground();
            var now = _clock();
            Locked(() =>
            {
                var changed = false;
                foreach (var p in _peers.Values)
                {
                    if (p.Link is not { IsOpen: true } link) continue;
                    var away = _controller.Target is { } t && SpaceIds.Same(t.PeerId, p.Id); // the watchdog pings it already
                    if (!away && IsReady(p, _store.Get(p.Id)) && now - p.LastLatencyPing >= _options.LatencyPing)
                    {
                        p.LastLatencyPing = now;
                        After(() => _ = link.SendAsync("ping"));
                    }
                    var lat = link.Latency.MedianMs;
                    if (lat != p.ShownLatency)
                    {
                        p.ShownLatency = lat;
                        changed = true;
                    }
                }
                if (changed) After(() => Changed?.Invoke());
            });
            if (_stats.Flush(now) is { } line) WriteLog(line);
        }
        catch (Exception ex) { WriteLog("Roam: " + ex.Message); }
        finally { Volatile.Write(ref _housekeepingBusy, 0); }
    }
}
