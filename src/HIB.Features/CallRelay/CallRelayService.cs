using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HIB.Features.Abstractions;
using HIB.Features.Adb;
using HIB.Features.PhoneSearch;

namespace HIB.Features.CallRelay;

/// <summary>
/// Call relay: place, answer, decline and end phone calls from the PC.
/// <list type="bullet">
/// <item>Dialing: ADB <c>am start -a android.intent.action.CALL -d tel:…</c>; the number is validated
/// and encoded by <see cref="DialNumber"/>.</item>
/// <item>Answer/decline/end: the H.I.B link's <c>callControl</c> when connected, else ADB
/// KEYCODE_CALL / KEYCODE_ENDCALL after checking <c>dumpsys telephony.registry</c>.</item>
/// <item>Contacts: Phone Search over the link, else the ADB contacts provider (cached).</item>
/// </list>
/// </summary>
public sealed class CallRelayService : ObservableObject, IDisposable
{
    readonly AdbService _adb;
    readonly IPhoneLink _link;
    readonly PhoneSearchClient? _search;
    List<PhoneContact> _adbContacts = new();
    DateTime _adbContactsLoaded = DateTime.MinValue;
    string? _searchId;
    int _searchGen;

    string? _status;
    bool _isDialing;
    bool _isLoadingContacts;
    IReadOnlyList<PhoneContact> _contacts = Array.Empty<PhoneContact>();
    PhoneCallState? _phoneCallState;

    public CallRelayService(AdbService adb, IPhoneLink link, PhoneSearchClient? search, ISettingsStore settings)
    {
        _adb = adb;
        _link = link;
        _search = search;
        Audio = new CallAudioRelay(adb, link, settings, this);
    }

    public CallAudioRelay Audio { get; }
    public string? Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsDialing { get => _isDialing; private set => SetProperty(ref _isDialing, value); }
    public bool IsLoadingContacts { get => _isLoadingContacts; private set => SetProperty(ref _isLoadingContacts, value); }
    public IReadOnlyList<PhoneContact> Contacts { get => _contacts; private set => SetProperty(ref _contacts, value); }
    public PhoneCallState? PhoneCallState { get => _phoneCallState; private set => SetProperty(ref _phoneCallState, value); }

    public bool LinkUp => _link.IsConnected;
    public bool AdbUp => _adb.CurrentSerial is not null;

    public async Task DialAsync(string? raw, bool hearHere)
    {
        var number = DialNumber.Sanitize(raw);
        if (number is null) { Status = "That doesn't look like a phone number."; return; }
        var serial = _adb.CurrentSerial;
        var args = serial is null ? null : DialNumber.CallArguments(serial, number);
        if (args is null) { Status = "Placing calls needs ADB (Wireless debugging) connected."; return; }
        IsDialing = true;
        try
        {
            if (hearHere) Audio.ArmForNextCall();
            var r = await _adb.RunAsync(args, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (r.Success && !r.Combined.Contains("Error", StringComparison.Ordinal))
                Status = $"Calling {number} on the phone…";
            else
            {
                Status = "The phone didn't start the call. " + r.Combined.Trim();
                Audio.Disarm();
            }
        }
        finally { IsDialing = false; }
    }

    public async Task PerformAsync(CallKeyAction action)
    {
        if (_link.IsConnected)
        {
            await _link.SendAsync("callControl", new { action = CallKeys.ProtocolAction(action) }).ConfigureAwait(false);
            Status = null;
            return;
        }
        var serial = _adb.CurrentSerial;
        if (serial is null) { Status = "The phone isn't connected (H.I.B link or ADB)."; return; }
        var state = await RefreshCallStateAsync().ConfigureAwait(false);
        if (state is not { } s || !CallKeys.IsAllowed(action, s))
        {
            Status = state is null ? "Couldn't read the phone's call state." : $"There's no call to {action.ToString().ToLowerInvariant()}.";
            return;
        }
        await _adb.RunAsync(CallKeys.AdbArguments(action, serial), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Status = null;
    }

    /// <summary>Answer and play the call on this PC.</summary>
    public Task AnswerHereAsync()
    {
        Audio.ArmForNextCall();
        return PerformAsync(CallKeyAction.Answer);
    }

    public async Task<PhoneCallState?> RefreshCallStateAsync()
    {
        if (_adb.CurrentSerial is null) { PhoneCallState = null; return null; }
        var r = await _adb.RunOnDeviceAsync(PhoneCallStates.DumpsysArguments, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        PhoneCallState = r.Success ? PhoneCallStates.Parse(r.StdOutText) : null;
        return PhoneCallState;
    }

    /// <summary>Phone Search over the link when connected, else the ADB contacts provider.</summary>
    public void SearchContacts(string? query)
    {
        var q = (query ?? "").Trim();
        var gen = Interlocked.Increment(ref _searchGen);
        _search?.Cancel(_searchId);
        _searchId = null;
        if (q.Length == 0) { Contacts = Array.Empty<PhoneContact>(); return; }
        if (_search is { IsPhoneConnected: true })
        {
            IsLoadingContacts = true;
            var collected = new List<PhoneContact>();
            _searchId = _search.Search(q, (results, done) =>
            {
                if (gen != Volatile.Read(ref _searchGen)) return;
                lock (collected)
                {
                    collected.AddRange(results.Where(r => r.Kind == PhoneSearchKind.Contacts && r.Subtitle.Length > 0)
                        .Select(r => new PhoneContact(r.Title, r.Subtitle)));
                    Contacts = collected.DistinctBy(c => c.Id).ToList();
                }
                if (done)
                {
                    IsLoadingContacts = false;
                    if (collected.Count == 0 && AdbUp) _ = SearchAdbContactsAsync(q, gen);
                }
            }, [PhoneSearchKind.Contacts], 30);
            if (_searchId is not null) return;
            IsLoadingContacts = false;
        }
        _ = SearchAdbContactsAsync(q, gen);
    }

    async Task SearchAdbContactsAsync(string q, int gen)
    {
        if (DateTime.UtcNow - _adbContactsLoaded > TimeSpan.FromMinutes(10))
        {
            if (_adb.CurrentSerial is null) { Status = "Contacts need the H.I.B link or ADB."; return; }
            IsLoadingContacts = true;
            var r = await _adb.RunOnDeviceAsync(ContactsQuery.AdbArguments, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            if (r.Success)
            {
                _adbContacts = ContactsQuery.Parse(r.StdOutText);
                _adbContactsLoaded = DateTime.UtcNow;
            }
            else Status = "Couldn't read contacts over ADB.";
            IsLoadingContacts = false;
        }
        if (gen == Volatile.Read(ref _searchGen)) Contacts = ContactsQuery.Filter(_adbContacts, q);
    }

    public void Dispose()
    {
        _search?.Cancel(_searchId);
        Audio.Dispose();
    }
}

/// <summary>
/// Hear phone calls on this PC: scrcpy audio-only with <c>--audio-source=voice-call</c> (falls back to
/// <c>voice-call-downlink</c>), started and stopped from <c>call_event</c> (or ADB call-state polling
/// when the link is down). Talking still uses the phone's microphone.
/// </summary>
public sealed class CallAudioRelay : ObservableObject, IDisposable
{
    public const string AutoHearKey = "hibHearCallsOnPc";
    readonly AdbService _adb;
    readonly IPhoneLink _link;
    readonly ISettingsStore _settings;
    readonly CallRelayService _calls;
    readonly object _lock = new();
    readonly StringBuilder _tail = new();
    readonly Timer _poll;
    Process? _process;
    bool _stopRequested;
    DateTime _startedAt;
    DateTime? _armedUntil;
    volatile bool _inCall;
    int _polling;

    bool _isActive, _isStarting;
    string? _activeSource, _lastError;

    public CallAudioRelay(AdbService adb, IPhoneLink link, ISettingsStore settings, CallRelayService calls)
    {
        _adb = adb;
        _link = link;
        _settings = settings;
        _calls = calls;
        _link.MessageReceived += OnMessage;
        _adb.PropertyChanged += OnAdbChanged;
        _poll = new Timer(_ => _ = PollAsync(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    public bool IsActive { get => _isActive; private set => SetProperty(ref _isActive, value); }
    public bool IsStarting { get => _isStarting; private set => SetProperty(ref _isStarting, value); }
    public string? ActiveSource { get => _activeSource; private set => SetProperty(ref _activeSource, value); }
    public string? LastError { get => _lastError; private set => SetProperty(ref _lastError, value); }
    public bool InCall => _inCall;

    /// <summary>"Hear calls on this PC": start automatically for every answered/placed call.</summary>
    public bool AutoHearCalls
    {
        get => _settings.Get<bool?>(AutoHearKey) ?? false;
        set { _settings.Set(AutoHearKey, value); OnPropertyChanged(); Evaluate(); }
    }

    bool Wanted => AutoHearCalls || (_armedUntil is { } t && t > DateTime.UtcNow);

    public void ArmForNextCall()
    {
        _armedUntil = DateTime.UtcNow.AddMinutes(2);
        Evaluate();
    }

    public void Disarm() => _armedUntil = null;

    public void Toggle()
    {
        if (IsActive || IsStarting) { _armedUntil = null; Stop(); }
        else { _armedUntil = DateTime.UtcNow.AddHours(4); Start(); }
    }

    /// <summary>Applies a call_event state (exposed for tests).</summary>
    public void OnCallState(string state)
    {
        switch (state.ToLowerInvariant())
        {
            case "offhook":
            case "accepted":
                _inCall = true;
                Evaluate();
                break;
            case "idle":
            case "ended":
            case "rejected":
            case "missed":
                if (_inCall || IsActive) CallEnded();
                break;
        }
    }

    void OnMessage(string type, JsonElement data)
    {
        if (type != "call_event" || data.ValueKind != JsonValueKind.Object) return;
        if (data.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String)
            OnCallState(s.GetString() ?? "");
    }

    void OnAdbChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AdbService.IsConnected) && !_adb.IsConnected) Stop();
    }

    void CallEnded()
    {
        _inCall = false;
        _armedUntil = null;
        Stop();
    }

    void Evaluate()
    {
        bool running;
        lock (_lock) running = _process != null;
        if (_inCall && Wanted && !running) Start();
    }

    async Task PollAsync()
    {
        // Poll the phone's call state when the link can't tell us (or to catch a missed hang-up).
        if (!_adb.IsConnected && _adb.CurrentSerial is null) return;
        var need = (Wanted && !_link.IsConnected) || IsActive;
        if (!need || Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            var state = await _calls.RefreshCallStateAsync().ConfigureAwait(false);
            if (state == PhoneCallState.OffHook) { _inCall = true; Evaluate(); }
            // Idle → the call is over. A manual "play now" (armed for hours) isn't stopped by this.
            else if (state == PhoneCallState.Idle && (_inCall || IsActive) && DateTime.UtcNow - _startedAt > TimeSpan.FromSeconds(6)
                     && !(_armedUntil is { } t && t > DateTime.UtcNow.AddHours(1))) CallEnded();
        }
        catch { }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    public void Start() => Launch(0);

    void Launch(int attempt)
    {
        lock (_lock) { if (_process != null || attempt >= CallAudioArgs.Sources.Length) return; }
        LastError = null;
        var serial = _adb.CurrentSerial;
        if (serial is null) { LastError = "Call audio needs ADB (Wireless debugging) connected."; return; }
        var scrcpy = _adb.Locator.ScrcpyPath;
        if (scrcpy is null) { LastError = "scrcpy isn't installed (it ships in the tools folder)."; return; }
        var adbPath = _adb.AdbPath;
        if (adbPath is null) { LastError = "adb isn't installed."; return; }

        var source = CallAudioArgs.Sources[attempt];
        var psi = ProcessRunner.CreateStartInfo(scrcpy, CallAudioArgs.Build(serial, source), AdbLocator.AdbEnvironment(adbPath));
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        lock (_tail) _tail.Clear();
        void OnLine(object? _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (_tail)
            {
                _tail.Append(e.Data).Append('\n');
                if (_tail.Length > 2000) _tail.Remove(0, _tail.Length - 2000);
            }
        }
        process.OutputDataReceived += OnLine;
        process.ErrorDataReceived += OnLine;
        process.Exited += (_, _) => OnExited(process, attempt);
        try
        {
            _stopRequested = false;
            IsStarting = true;
            _startedAt = DateTime.UtcNow;
            process.Start();
            try { process.StandardInput.Close(); } catch { }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            lock (_lock) _process = process;
            ActiveSource = source;
            _ = Task.Delay(2500).ContinueWith(_ =>
            {
                bool running;
                try { running = !process.HasExited; } catch { running = false; }
                lock (_lock) if (!ReferenceEquals(_process, process)) return;
                if (running) { IsStarting = false; IsActive = true; }
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            IsStarting = false;
            process.Dispose();
            LastError = ex.Message;
        }
    }

    void OnExited(Process finished, int attempt)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_process, finished)) return;
            _process = null;
        }
        var userStop = _stopRequested;
        _stopRequested = false;
        IsStarting = false;
        IsActive = false;
        ActiveSource = null;
        int code;
        try { code = finished.ExitCode; } catch { code = -1; }
        finished.Dispose();
        if (userStop || code == 0) return;
        if (DateTime.UtcNow - _startedAt < TimeSpan.FromSeconds(8) && attempt + 1 < CallAudioArgs.Sources.Length && (_inCall || Wanted))
        {
            Launch(attempt + 1);
            return;
        }
        string tail;
        lock (_tail) tail = _tail.ToString();
        LastError = tail.Split('\n').LastOrDefault(l => l.Contains("ERROR")) ??
                    $"Call audio stopped (code {code}). This phone may not allow capturing call audio.";
    }

    public void Stop()
    {
        Process? p;
        lock (_lock) p = _process;
        if (p is null) { IsActive = false; IsStarting = false; return; }
        _stopRequested = true;
        ProcessRunner.KillQuietly(p);
    }

    public void Dispose()
    {
        _poll.Dispose();
        _link.MessageReceived -= OnMessage;
        _adb.PropertyChanged -= OnAdbChanged;
        _stopRequested = true;
        Process? p;
        lock (_lock) { p = _process; _process = null; }
        if (p != null) ProcessRunner.KillQuietly(p);
    }
}
