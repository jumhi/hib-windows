using System.Globalization;

namespace HIB.Features.Adb;

/// <summary>What made the keeper look again (docs/SPACE.md §13). Every trigger resets the backoff.</summary>
public enum AdbKeeperTrigger
{
    /// <summary>The phone link came up (the attempt waits <see cref="AdbKeeper.LinkUpGrace"/> for the phone's reports).</summary>
    LinkUp,
    /// <summary><c>adbSetupState.connectPort</c> changed (or Wireless debugging came on).</summary>
    ConnectPort,
    /// <summary>The device message brought other <c>adbPorts</c> or another address.</summary>
    Ports,
    /// <summary>This computer's network changed.</summary>
    Network,
    /// <summary>The user pressed Connect (a nudge; the keeper does the rest).</summary>
    Manual,
}

public enum AdbKeeperState
{
    /// <summary>Nothing to keep: no phone, never set up, switched off, or the user disconnected.</summary>
    Off,
    Connected,
    /// <summary>Wanted but down: trying quietly with backoff.</summary>
    Reconnecting,
}

/// <summary>What the keeper reads from the app. Every getter is quick and thread-safe.</summary>
public sealed class AdbKeeperContext
{
    /// <summary>The "keep ADB connected" preference.</summary>
    public required Func<bool> Enabled { get; init; }
    /// <summary>The phone answers on the app link.</summary>
    public required Func<bool> LinkUp { get; init; }
    /// <summary>Where adbd listens (the phone's own LAN address).</summary>
    public required Func<string?> PhoneIp { get; init; }
    /// <summary>Which phone this is, so "has ADB ever worked with it" and its ports are remembered per phone.</summary>
    public required Func<string?> PhoneKey { get; init; }
    /// <summary>The phone's last <c>adbSetupState</c> (SPACE.md §8.1), or null for an older phone app.</summary>
    public required Func<AdbSetupState?> Setup { get; init; }
    /// <summary>The device message's <c>adbPorts</c> (older phone apps).</summary>
    public Func<IReadOnlyList<string>> DevicePorts { get; init; } = () => [];
    /// <summary>Someone else is connecting right now (the setup card pairing): stand back.</summary>
    public Func<bool> Paused { get; init; } = () => false;
    /// <summary>Asks an older phone app to re-send its ports (<c>refreshAdbPorts</c>).</summary>
    public Func<Task>? AskForPorts { get; init; }
}

/// <summary>
/// ADB that stays connected (docs/SPACE.md §13): while the phone link is up and ADB has worked with this phone before
/// (or the phone says Wireless debugging is on), keep it connected without any button. Tries at link-up, when the
/// phone's <c>connectPort</c> changes, right after a network change, and every 10 s while down (doubling to 60 s; any
/// trigger resets it). Uses the phone's reported port first, then the ports that worked before. When adb answers
/// "no route to host" / "refused" while the phone answers on the app link, restarts the adb server from H.I.B once,
/// then retries. Never toasts: <see cref="Status"/> is the quiet "ADB · connected" / "ADB · reconnecting…".
/// UI-free and clock-injected, so the whole state machine is testable; the app calls <see cref="TickAsync"/> every
/// second (<see cref="Start"/>).
/// </summary>
public sealed class AdbKeeper : ObservableObject, IDisposable
{
    public const string SettingsKey = "adbKeeperPhones";
    public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(60);
    /// <summary>After link-up, the device message and <c>adbSetupState</c> arrive within a moment: wait for them.</summary>
    public static readonly TimeSpan LinkUpGrace = TimeSpan.FromSeconds(1.5);
    /// <summary>Another connect (Mirror, the setup card) was running: look again soon, without backing off.</summary>
    public static readonly TimeSpan BusyRetry = TimeSpan.FromSeconds(3);
    /// <summary>While connected, check now and then that the connection still answers.</summary>
    public static readonly TimeSpan HealthEvery = TimeSpan.FromSeconds(60);
    /// <summary>Network events come in bursts: act once things are quiet.</summary>
    public static readonly TimeSpan NetworkSettle = TimeSpan.FromSeconds(2);
    const int MaxRemembered = 4;

    readonly AdbService _adb;
    readonly AdbKeeperContext _ctx;
    readonly PhoneMemory _store;
    readonly Func<DateTime> _now;
    readonly object _gate = new();
    Timer? _timer;
    bool _networkHooked;

    TimeSpan _delay = FirstRetry;
    DateTime? _nextAt;
    DateTime _nextHealth = DateTime.MinValue;
    DateTime? _networkAt;
    bool _running;
    bool _again;
    bool _verifyNow;
    bool _restarted;
    bool _disposed;

    AdbKeeperState _state;
    string? _detail;

    public AdbKeeper(AdbService adb, AdbKeeperContext context, Abstractions.ISettingsStore settings, Func<DateTime>? clock = null)
    {
        _adb = adb;
        _ctx = context;
        _store = new PhoneMemory(settings);
        _now = clock ?? (() => DateTime.UtcNow);
        _adb.PropertyChanged += OnAdbChanged;
    }

    public AdbKeeperState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value)) OnPropertyChanged(nameof(Status));
        }
    }

    /// <summary>The quiet line for the UI: "ADB · connected", "ADB · reconnecting…", or empty when there's nothing to keep.</summary>
    public string Status => StatusFor(State);

    public static string StatusFor(AdbKeeperState state) => state switch
    {
        AdbKeeperState.Connected => "ADB · connected",
        AdbKeeperState.Reconnecting => "ADB · reconnecting…",
        _ => "",
    };

    /// <summary>The last reason a quiet attempt failed (for the ADB page; never a toast).</summary>
    public string? Detail { get => _detail; private set => SetProperty(ref _detail, value); }

    /// <summary>The next quiet attempt (null while connected, off, or about to try).</summary>
    public DateTime? NextAttemptAt { get { lock (_gate) return _nextAt; } }
    /// <summary>The wait after the next failure.</summary>
    public TimeSpan CurrentDelay { get { lock (_gate) return _delay; } }
    /// <summary>How many quiet <c>adb connect</c> rounds ran (tests, diagnostics).</summary>
    public int Attempts { get; private set; }
    /// <summary>How many times the keeper restarted the adb server.</summary>
    public int ServerRestarts { get; private set; }

    /// <summary>ADB is worth keeping now: link up, preference on, not disconnected on purpose, and this phone has had
    /// ADB working before (or says Wireless debugging is on).</summary>
    public bool Wanted
    {
        get
        {
            if (_disposed || !_ctx.Enabled() || !_ctx.LinkUp() || !_adb.HasAdb || _adb.UserRequestedDisconnect) return false;
            if (_adb.WiredAdbEnabled && !string.IsNullOrEmpty(_adb.SelectedWiredSerial)) return false; // the cable is in charge
            if (string.IsNullOrEmpty(_ctx.PhoneIp())) return false;
            var setup = _ctx.Setup();
            return EverWorked || setup?.WirelessDebugging == true || (setup is null && _ctx.DevicePorts().Count > 0);
        }
    }

    /// <summary>ADB has worked with this phone on this computer before.</summary>
    public bool EverWorked => _ctx.PhoneKey() is { Length: > 0 } key && _store.Has(key);

    /// <summary>The ports that worked with this phone, most recent first.</summary>
    public IReadOnlyList<int> Remembered => _ctx.PhoneKey() is { Length: > 0 } key ? _store.Ports(key) : [];

    /// <summary>
    /// Ports for <c>adb connect</c>: the phone's own <c>connectPort</c> first (else the device message's
    /// <c>adbPorts</c>), then the ports that worked before. None while the phone says Wireless debugging is off.
    /// </summary>
    public static IReadOnlyList<string> PortsFor(AdbSetupState? setup, IReadOnlyList<string> devicePorts, IReadOnlyList<int> remembered)
    {
        if (setup is { WirelessDebugging: false }) return [];
        var list = new List<string>();
        void Add(int p)
        {
            var s = p.ToString(CultureInfo.InvariantCulture);
            if (p is > 0 and <= 65535 && !list.Contains(s)) list.Add(s);
        }
        if (setup?.ConnectPort is int connect) Add(connect);
        else
            foreach (var p in devicePorts)
                if (int.TryParse(p.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) Add(v);
        foreach (var p in remembered) Add(p);
        return list;
    }

    /// <summary>adb couldn't reach the phone at all ("No route to host", refused — also Windows' WSA wording), the case a
    /// fresh adb server started by H.I.B can fix.</summary>
    public static bool MeansUnreachable(string? output)
    {
        if (string.IsNullOrEmpty(output)) return false;
        var o = output.ToLowerInvariant();
        return o.Contains("no route to host") || o.Contains("refused") || o.Contains("unreachable host")
               || o.Contains("network is unreachable");
    }

    /// <summary>Starts the once-a-second tick (the app; tests call <see cref="TickAsync"/> themselves).</summary>
    public void Start(TimeSpan? period = null)
    {
        var p = period ?? TimeSpan.FromSeconds(1);
        _timer ??= new Timer(_ => _ = SafeTickAsync(), null, p, p);
    }

    /// <summary>Follows this computer's network changes (Wi-Fi switched, cable in, VPN up…).</summary>
    public void WatchNetwork()
    {
        if (_networkHooked) return;
        try
        {
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
            _networkHooked = true;
        }
        catch (Exception ex) { Trace("network events unavailable: " + ex.Message); }
    }

    void OnNetworkAddressChanged(object? s, EventArgs e) => NetworkChanged();
    void OnNetworkAvailabilityChanged(object? s, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e) => NetworkChanged();

    /// <summary>The network changed: once it settles (<see cref="NetworkSettle"/>), a <see cref="AdbKeeperTrigger.Network"/> trigger.</summary>
    public void NetworkChanged()
    {
        lock (_gate) _networkAt = _now() + NetworkSettle;
    }

    async Task SafeTickAsync()
    {
        try { await TickAsync().ConfigureAwait(false); }
        catch (Exception ex) { Trace("tick failed: " + ex.Message); }
    }

    /// <summary>Something changed: reset the backoff and look now (link-up: after <see cref="LinkUpGrace"/>).</summary>
    public Task TriggerAsync(AdbKeeperTrigger why)
    {
        var now = _now();
        lock (_gate)
        {
            _delay = FirstRetry;
            // A new link or network is a new situation: the adb server may be restarted once more.
            if (why is AdbKeeperTrigger.LinkUp or AdbKeeperTrigger.Network or AdbKeeperTrigger.Manual) _restarted = false;
            _nextAt = why == AdbKeeperTrigger.LinkUp ? now + LinkUpGrace : now;
            // Connected, but to a port or network that may be gone: check it still answers.
            if (why is AdbKeeperTrigger.ConnectPort or AdbKeeperTrigger.Ports or AdbKeeperTrigger.Network) _verifyNow = true;
            if (_running) _again = true;
        }
        Trace($"trigger: {why}");
        return why == AdbKeeperTrigger.LinkUp ? Task.CompletedTask : TickAsync();
    }

    /// <summary>One look: keeps the state, runs a due attempt or health check. Never runs two at once.</summary>
    public async Task TickAsync()
    {
        var now = _now();
        bool verify;
        lock (_gate)
        {
            if (_running) return;
            if (_networkAt is { } settled && now >= settled)
            {
                _networkAt = null;
                _delay = FirstRetry;
                _restarted = false;
                _nextAt = now;
                _verifyNow = true;
                Trace("trigger: Network");
            }
            if (!Wanted)
            {
                _nextAt = null;
                _delay = FirstRetry;
                _verifyNow = false;
                State = AdbKeeperState.Off;
                return;
            }
            if (_adb.IsConnected)
            {
                State = AdbKeeperState.Connected;
                if (!_verifyNow && now < _nextHealth) return;
                verify = true;
            }
            else
            {
                State = AdbKeeperState.Reconnecting;
                if (_ctx.Paused() || _adb.IsConnecting) return; // someone else is connecting: wait for them
                _nextAt ??= now;
                if (now < _nextAt) return;
                verify = false;
            }
            _running = true;
            _verifyNow = false;
        }

        try
        {
            var attempt = !verify;
            if (verify)
            {
                _nextHealth = now + HealthEvery;
                if (!await _adb.VerifyConnectedAsync().ConfigureAwait(false))
                {
                    // Gone (a new port, another network): reconnect right away.
                    Trace("the connection stopped answering; reconnecting");
                    lock (_gate) _delay = FirstRetry;
                    attempt = true;
                }
            }
            if (attempt) await AttemptAsync(now).ConfigureAwait(false);
        }
        finally
        {
            bool again;
            lock (_gate)
            {
                _running = false;
                again = _again;
                _again = false;
            }
            if (again && !_disposed) await TickAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The "Connect" button and Mirror: one connect now, with the restart-once rule; the result says why not.
    /// Returns null when there's no phone or no adb.</summary>
    public async Task<AdbConnectResult?> ConnectNowAsync(bool notify = false)
    {
        var ip = _ctx.PhoneIp();
        if (string.IsNullOrEmpty(ip) || !_adb.HasAdb) return null;
        var setup = _ctx.Setup();
        var ports = PortsFor(setup, _ctx.DevicePorts(), Remembered);
        if (setup is not null && ports.Count == 0)
        {
            var why = !setup.DeveloperOptions ? "Developer options are off on the phone."
                : !setup.WirelessDebugging ? "Wireless debugging is off on the phone."
                : "The phone hasn't reported its Wireless debugging port yet.";
            return new AdbConnectResult(false, null, why, why, why);
        }
        lock (_gate)
        {
            _delay = FirstRetry;
            _restarted = false;
        }
        var now = _now();
        var r = await ConnectAsync(ip, ports, silent: false).ConfigureAwait(false);
        Settle(r, now);
        if (notify && !r.Success && r.Output != AdbService.BusyOutput) _adb.NotifyFailed(r);
        return r;
    }

    async Task AttemptAsync(DateTime now)
    {
        var ip = _ctx.PhoneIp();
        var setup = _ctx.Setup();
        if (string.IsNullOrEmpty(ip)) { Settle(null, now); return; }
        if (setup is { WirelessDebugging: false })
        {
            Detail = setup.DeveloperOptions ? "Wireless debugging is off on the phone." : "Developer options are off on the phone.";
            Settle(null, now);
            return;
        }
        var ports = PortsFor(setup, _ctx.DevicePorts(), Remembered);
        if (ports.Count == 0 && setup is not null)
        {
            // The phone will report its port as soon as it has one (a ConnectPort trigger).
            Detail = "Waiting for the phone's Wireless debugging port.";
            Settle(null, now);
            return;
        }
        Attempts++;
        Trace($"connecting quietly to {ip} ({(ports.Count == 0 ? "mDNS" : string.Join(", ", ports))})");
        var r = await ConnectAsync(ip, ports, silent: true).ConfigureAwait(false);
        Settle(r, now);
        if (!r.Success && r.Output != AdbService.BusyOutput && setup is null && _ctx.AskForPorts is { } ask && _ctx.LinkUp())
        {
            // An older phone app: its ports may be old. The answer comes back as a device message (a Ports trigger).
            try { await ask().ConfigureAwait(false); } catch { }
        }
    }

    /// <summary><c>adb connect</c> over the ports; on "no route"/refused while the phone answers on the app link, restarts
    /// the adb server once (per outage) and tries again.</summary>
    async Task<AdbConnectResult> ConnectAsync(string ip, IReadOnlyList<string> ports, bool silent)
    {
        var r = await _adb.ConnectAsync(ip, ports, silent: silent, notify: false).ConfigureAwait(false);
        if (r.Success || r.Output == AdbService.BusyOutput) return r;
        bool restart;
        lock (_gate)
        {
            restart = !_restarted && _ctx.LinkUp() && MeansUnreachable(r.Output);
            if (restart) _restarted = true;
        }
        if (restart)
        {
            Trace("adb can't reach the phone but the phone answers on the app link: restarting the adb server once");
            ServerRestarts++;
            await _adb.RestartServerAsync().ConfigureAwait(false);
            r = await _adb.ConnectAsync(ip, ports, silent: silent, notify: false).ConfigureAwait(false);
        }
        return r;
    }

    /// <summary>After an attempt: connected → remember the port and rest; busy → look again soon; failed → back off.</summary>
    void Settle(AdbConnectResult? r, DateTime now)
    {
        lock (_gate)
        {
            if (r is { Success: true } || _adb.IsConnected)
            {
                _delay = FirstRetry;
                _nextAt = null;
                _restarted = false;
                _nextHealth = now + HealthEvery;
            }
            else if (r?.Output == AdbService.BusyOutput)
            {
                _nextAt = now + BusyRetry;
            }
            else
            {
                _nextAt = now + _delay;
                _delay = _delay + _delay > MaxRetry ? MaxRetry : _delay + _delay;
            }
        }
        if (r is { Success: true } || _adb.IsConnected)
        {
            Detail = null;
            RememberCurrent();
            State = AdbKeeperState.Connected;
        }
        else if (r is { } failed && failed.Output != AdbService.BusyOutput)
        {
            Detail = AdbService.Explain(failed);
            Trace("not yet: " + (Detail ?? "").Split('\n')[0] + $" (next try in {(NextAttemptAt is { } at ? (at - now).TotalSeconds : 0):0}s)");
        }
    }

    void OnAdbChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AdbService.IsConnected) || _disposed) return;
        if (_adb.IsConnected)
        {
            // Connected by anyone (the keeper, Mirror, the setup card's pairing): this phone has ADB now.
            RememberCurrent();
            lock (_gate)
            {
                _delay = FirstRetry;
                _nextAt = null;
                _nextHealth = _now() + HealthEvery;
            }
            if (Wanted) State = AdbKeeperState.Connected;
        }
        else
        {
            // Lost: reconnect at the next tick.
            lock (_gate)
            {
                _delay = FirstRetry;
                _nextAt = _now();
            }
            State = Wanted ? AdbKeeperState.Reconnecting : AdbKeeperState.Off;
        }
    }

    void RememberCurrent()
    {
        if (_adb.Mode != AdbConnectionMode.Wireless || _adb.Port <= 0) return;
        if (_ctx.PhoneKey() is not { Length: > 0 } key) return;
        _store.Remember(key, _adb.Port, MaxRemembered);
    }

    static void Trace(string message) => HibFeatures.Trace("[adb-keeper] " + message);

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
        _adb.PropertyChanged -= OnAdbChanged;
        if (_networkHooked)
        {
            try
            {
                System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
                System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            }
            catch { }
            _networkHooked = false;
        }
    }

    /// <summary>Per-phone memory in the settings store: <c>{ "&lt;phone&gt;": [ports, most recent first] }</c>.</summary>
    sealed class PhoneMemory(Abstractions.ISettingsStore settings)
    {
        readonly object _lock = new();

        Dictionary<string, List<int>> Load()
            => settings.Get<Dictionary<string, List<int>>>(SettingsKey) ?? new Dictionary<string, List<int>>();

        public bool Has(string key)
        {
            lock (_lock) return Load().ContainsKey(key);
        }

        public IReadOnlyList<int> Ports(string key)
        {
            lock (_lock) return Load().TryGetValue(key, out var list) ? list.ToList() : [];
        }

        public void Remember(string key, int port, int max)
        {
            lock (_lock)
            {
                var all = Load();
                var list = all.TryGetValue(key, out var l) ? l : new List<int>();
                if (list.Count > 0 && list[0] == port) return;
                list.Remove(port);
                list.Insert(0, port);
                if (list.Count > max) list.RemoveRange(max, list.Count - max);
                all[key] = list;
                settings.Set(SettingsKey, all);
            }
        }
    }
}
