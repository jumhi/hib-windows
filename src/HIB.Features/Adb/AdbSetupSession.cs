namespace HIB.Features.Adb;

/// <summary>
/// Drives the "Set up mirroring" card while it is open (docs/SPACE.md §8.3): asks the phone to report its ADB
/// setup quickly (<c>adbSetupRequest watch</c>), works out the step from each <c>adbSetupState</c>, connects to
/// <c>connectPort</c> as soon as Wireless debugging is on, shows the pairing QR when that connect says "not
/// paired" (or the phone opens a pairing screen), pairs the moment the phone reports our QR's service, and
/// connects right after pairing. UI-free so the whole flow is testable.
/// </summary>
public sealed class AdbSetupSession : ObservableObject, IDisposable
{
    private readonly AdbService _adb;
    private readonly AdbPairingService _pairing;
    private readonly Func<string?, bool, Task> _request;
    private readonly Func<bool> _phoneConnected;
    private readonly object _gate = new();
    private readonly DateTime _openedAt = DateTime.UtcNow;
    private Timer? _timer;
    private DateTime _lastWatch;
    private bool _disposed;

    private bool _connecting;
    private bool _needsPairing;
    private int? _triedPort;
    private bool _codeMode;
    private bool _startingQr;
    private AdbSetupStep _step = AdbSetupStep.Checking;
    private string _status = "";
    private bool _developerDone, _wirelessDone;
    private string? _openedOnPhone;
    private (bool, bool, bool) _lastFlags = (false, false, false);

    /// <param name="request">Sends <c>adbSetupRequest {open, watch}</c> to the phone.</param>
    /// <param name="phoneConnected">The phone is linked to H.I.B.</param>
    public AdbSetupSession(AdbService adb, AdbPairingService pairing, Func<string?, bool, Task> request, Func<bool> phoneConnected)
    {
        _adb = adb;
        _pairing = pairing;
        _request = request;
        _phoneConnected = phoneConnected;
        _pairing.SetupStateChanged += OnState;
        _pairing.PropertyChanged += OnPairingChanged;
        _adb.PropertyChanged += OnAdbChanged;
    }

    /// <summary>An older phone app never sends <c>adbSetupState</c>: after this long, show the QR (found by mDNS).</summary>
    public TimeSpan OldAppAfter { get; set; } = TimeSpan.FromSeconds(4);
    /// <summary><c>watch</c> lasts 5 minutes on the phone; renew it before that.</summary>
    public TimeSpan WatchRenewal { get; set; } = TimeSpan.FromMinutes(4);

    public AdbSetupStep Step { get => _step; private set => SetProperty(ref _step, value); }
    /// <summary>The 6-digit code instead of the QR.</summary>
    public bool CodeMode { get => _codeMode; private set => SetProperty(ref _codeMode, value); }
    /// <summary>A line under the step: progress or the real reason something failed.</summary>
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool DeveloperDone { get => _developerDone; private set => SetProperty(ref _developerDone, value); }
    public bool WirelessDone { get => _wirelessDone; private set => SetProperty(ref _wirelessDone, value); }
    public bool PairedDone => Step == AdbSetupStep.Ready;
    /// <summary>"developer" / "wireless" once we asked the phone to open that screen.</summary>
    public string? OpenedOnPhone { get => _openedOnPhone; private set => SetProperty(ref _openedOnPhone, value); }
    /// <summary>The phone has its pairing-code screen open (the code box can be used).</summary>
    public bool PhoneShowsPairingCode => _pairing.SetupState?.PairingPort is not null;
    /// <summary>The phone never reported its setup: an older H.I.B app on the phone.</summary>
    public bool OldPhoneApp => _pairing.SetupState is null && DateTime.UtcNow - _openedAt >= OldAppAfter;

    public AdbPairingService Pairing => _pairing;

    /// <summary>Tells the phone to report quickly and works out the first step.</summary>
    public async Task StartAsync()
    {
        await WatchAsync().ConfigureAwait(false);
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
        Refresh();
    }

    private async Task WatchAsync(string? open = null)
    {
        _lastWatch = DateTime.UtcNow;
        try { await _request(open, true).ConfigureAwait(false); } catch { }
    }

    private void Tick()
    {
        if (_disposed) return;
        if (DateTime.UtcNow - _lastWatch >= WatchRenewal) _ = WatchAsync();
        Refresh();
    }

    /// <summary>"Show me on the phone" (developer) / "Open it on the phone" (wireless).</summary>
    public async Task OpenOnPhoneAsync(string what)
    {
        OpenedOnPhone = what;
        Status = what == "developer"
            ? "Opened on your phone: tap Build number 7 times."
            : "Opened on your phone: switch on Wireless debugging.";
        await WatchAsync(what).ConfigureAwait(false);
    }

    /// <summary>Switches between the QR and the 6-digit code.</summary>
    public void UseCode(bool code)
    {
        if (CodeMode == code) return;
        CodeMode = code;
        if (!_pairing.IsBusy) _pairing.StopPairing();
        Status = "";
        Refresh();
    }

    /// <summary>The user typed the 6 digits.</summary>
    public async Task<bool> SubmitCodeAsync(string code)
    {
        var ok = await _pairing.PairWithCodeAsync(code).ConfigureAwait(false);
        if (!ok) Status = _pairing.Status;
        Refresh();
        return ok;
    }

    private void OnState(AdbSetupState? state) => Refresh();
    private void OnPairingChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AdbPairingService.Phase) or nameof(AdbPairingService.Status)) Refresh();
    }
    private void OnAdbChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AdbService.IsConnected)) Refresh();
    }

    /// <summary>Works out the step and starts whatever it needs (connect, QR pairing).</summary>
    public void Refresh()
    {
        if (_disposed) return;
        var state = _pairing.SetupState;
        bool connectNow = false, startQr = false, stopQr = false;
        int port = 0;
        AdbSetupStep step;
        lock (_gate)
        {
            var phone = _phoneConnected();
            var adbOk = _adb.IsConnected;
            if (adbOk) _needsPairing = false;
            var busy = _connecting || _pairing.IsBusy;
            // Wireless debugging restarted on a new port: pairings survive that, so try connecting again.
            if (state?.ConnectPort is int cp && _triedPort is int tp && cp != tp && !_pairing.IsPairing) _needsPairing = false;
            step = AdbSetupFlow.StepFor(phone, adbOk, state, OldPhoneApp, busy, _needsPairing);

            // Wireless debugging is on and we know its port: try it (once per port) — maybe it's paired already.
            if (step == AdbSetupStep.Connecting && !busy && state?.ConnectPort is int p && p != _triedPort && !string.IsNullOrEmpty(_pairing.PhoneIp))
            {
                _triedPort = p;
                _connecting = true;
                connectNow = true;
                port = p;
            }
            if (step == AdbSetupStep.Pair && !CodeMode && !_pairing.IsPairing && !_startingQr) { startQr = true; _startingQr = true; }
            if ((step is AdbSetupStep.Phone or AdbSetupStep.DeveloperOptions or AdbSetupStep.WirelessDebugging) && _pairing.IsPairing && !_pairing.IsBusy)
                stopQr = true;
            if (connectNow) step = AdbSetupStep.Connecting;
        }

        // A new step starts with a clean line (e.g. "Opened on your phone…" belongs to the previous step).
        if (step != _step) Status = "";
        Step = step;
        DeveloperDone = AdbSetupFlow.DeveloperDone(state, _adb.IsConnected);
        WirelessDone = AdbSetupFlow.WirelessDone(state, _adb.IsConnected);
        var flags = (step == AdbSetupStep.Ready, PhoneShowsPairingCode, OldPhoneApp);
        if (flags != _lastFlags)
        {
            _lastFlags = flags;
            OnPropertyChanged(nameof(PairedDone));
            OnPropertyChanged(nameof(PhoneShowsPairingCode));
            OnPropertyChanged(nameof(OldPhoneApp));
        }
        if (step == AdbSetupStep.Ready) Status = "";
        else if ((_pairing.Phase is AdbPairingPhase.Pairing or AdbPairingPhase.Connecting or AdbPairingPhase.Failed) && _pairing.Status.Length > 0)
            Status = _pairing.Status;

        if (stopQr) _pairing.StopPairing();
        if (startQr)
        {
            try { _ = _pairing.StartPairingAsync(); } // runs synchronously until IsPairing is true
            finally { lock (_gate) _startingQr = false; }
        }
        if (connectNow) _ = ConnectAsync(port);
    }

    private async Task ConnectAsync(int port)
    {
        var ip = _pairing.PhoneIp!;
        Status = "Connecting…";
        AdbConnectResult r;
        try { r = await _adb.ConnectAsync(ip, [port.ToString()], silent: true, notify: false).ConfigureAwait(false); }
        catch (Exception ex) { r = new AdbConnectResult(false, null, ex.Message, null, null); }
        if (r.Output == AdbService.BusyOutput)
        {
            // H.I.B's own automatic connect is running: let it finish, then look again.
            for (var i = 0; i < 120 && _adb.IsConnecting; i++) await Task.Delay(250).ConfigureAwait(false);
            lock (_gate) { _connecting = false; if (!_adb.IsConnected) _triedPort = null; }
            Refresh();
            return;
        }
        lock (_gate)
        {
            _connecting = false;
            if (!r.Success && !_adb.IsConnected) _needsPairing = true;
        }
        Refresh();
        // Not paired is simply the next step (the QR); anything else is worth saying.
        if (!r.Success && !_adb.IsConnected && !AdbSetupFlow.MeansNotPaired(r.Output)) Status = AdbService.Explain(r);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _pairing.SetupStateChanged -= OnState;
        _pairing.PropertyChanged -= OnPairingChanged;
        _adb.PropertyChanged -= OnAdbChanged;
        if (_pairing.IsPairing && !_pairing.IsBusy) _pairing.StopPairing();
        _ = _request(null, false); // stop the fast reporting
    }
}
