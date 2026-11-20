using System.Text.Json;
using System.Text.Json.Nodes;
using HIB.Features.Abstractions;
using HIB.Features.Adb;

namespace HIB.Features.FindMy;

/// <summary>The phone's last reported location (persisted).</summary>
public sealed record PhoneLocationReport
{
    public double Lat { get; init; }
    public double Lon { get; init; }
    public double AccuracyM { get; init; }
    public double? Altitude { get; init; }
    public double? Speed { get; init; }
    public double? Bearing { get; init; }
    public int? BatteryPct { get; init; }
    public bool? IsCharging { get; init; }
    public string? NetworkName { get; init; }
    public long Timestamp { get; init; }
    public string? Provider { get; init; }

    public DateTimeOffset Date => DateTimeOffset.FromUnixTimeMilliseconds(Timestamp);

    /// <summary>Parses a <c>findLocation</c> payload; lat and lon are required.</summary>
    public static PhoneLocationReport? FromJson(JsonObject d)
    {
        static double? Num(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out double x) ? x : null;
        var lat = Num(d["lat"]);
        var lon = Num(d["lon"]);
        if (lat is null || lon is null) return null;
        return new PhoneLocationReport
        {
            Lat = lat.Value,
            Lon = lon.Value,
            AccuracyM = Num(d["accuracyM"]) ?? 0,
            Altitude = Num(d["altitude"]),
            Speed = Num(d["speed"]),
            Bearing = Num(d["bearing"]),
            BatteryPct = Num(d["batteryPct"]) is { } b ? (int)b : null,
            IsCharging = d["isCharging"] is JsonValue c && c.TryGetValue(out bool cb) ? cb : null,
            NetworkName = d["networkName"] is JsonValue nn && nn.TryGetValue(out string? ns) ? ns : null,
            Timestamp = Num(d["timestamp"]) is { } t ? (long)t : FindMyCodec.NowMs(),
            Provider = d["provider"] is JsonValue p && p.TryGetValue(out string? ps) ? ps : null,
        };
    }
}

public enum FindMyRoute { Nearby, Adb, Internet, None }

/// <summary>
/// Find My, both directions (port of FindMyManager). Commands go over the phone link when it's up,
/// ADB for lock, otherwise over the encrypted internet relay. Also answers the phone's
/// "find my PC" requests through <see cref="DesktopRingRequested"/> / <see cref="DesktopLocationProvider"/>.
/// </summary>
public sealed class FindMyService : ObservableObject, IDisposable
{
    private const string LocationStoreKey = "hibFindMyLastPhoneLocation";
    private static readonly string[] HandledTypes =
        ["findLocation", "findLockResult", "findMessageResult", "findRingState", "findPong", "findRingMac", "findLocateMac", "findPing"];

    private readonly IPhoneLink _link;
    private readonly AdbService? _adb;
    private readonly ISettingsStore _store;
    private CancellationTokenSource? _locateTimeout;
    private CancellationTokenSource? _pingTimeout;
    private (string Id, DateTime SentAt)? _pendingPing;
    private bool _started;
    private bool _lastPhoneCommandViaRelay;

    private PhoneLocationReport? _lastLocation;
    private string _status = "";
    private FindMyRoute? _lastRoute;
    private bool _isLocating;
    private bool _isTestingRelay;
    private string? _relayTestResult;
    private bool _phoneRingingViaRelay;

    public FindMyService(IPhoneLink link, ISettingsStore store, AdbService? adb = null, FindMyRelay? relay = null)
    {
        _link = link;
        _store = store;
        _adb = adb;
        Relay = relay ?? new FindMyRelay(store, () => link.PairingKey);
        _lastLocation = store.Get<PhoneLocationReport>(LocationStoreKey);
    }

    public FindMyRelay Relay { get; }

    public PhoneLocationReport? LastLocation { get => _lastLocation; private set => SetProperty(ref _lastLocation, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public FindMyRoute? LastRoute { get => _lastRoute; private set => SetProperty(ref _lastRoute, value); }
    public bool IsLocating { get => _isLocating; private set => SetProperty(ref _isLocating, value); }
    public bool IsTestingRelay { get => _isTestingRelay; private set => SetProperty(ref _isTestingRelay, value); }
    public string? RelayTestResult { get => _relayTestResult; private set => SetProperty(ref _relayTestResult, value); }
    public bool PhoneRingingViaRelay { get => _phoneRingingViaRelay; private set => SetProperty(ref _phoneRingingViaRelay, value); }

    /// <summary>The phone reported its ring state (findRingState).</summary>
    public event Action<bool>? PhoneRingStateReported;

    /// <summary>The phone asked this PC to ring (true) or stop (false). Play an alarm, then call
    /// <see cref="ReportDesktopRingState"/>.</summary>
    public event Action<bool>? DesktopRingRequested;

    /// <summary>Supplies the <c>findMacLocation</c> payload: {macName,timestamp,networkName?,lat,lon,accuracyM,altitude,provider}
    /// or {provider:"none", error}. Default answers with the machine name and provider "none".</summary>
    public Func<Task<JsonObject>>? DesktopLocationProvider { get; set; }

    /// <summary>Call once at startup: wires link messages, relay subscription and pairing capture.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        Relay.MessageReceived += OnRelayMessage;
        _link.MessageReceived += OnLinkMessage;
        _link.ConnectionChanged += OnLinkConnectionChanged;
        if (_link.IsConnected) OnLinkConnectionChanged(true);
        Relay.EnsureSubscribed();
    }

    private void OnRelayMessage(FindMyEnvelope e) => HandleIncoming(e.Type, e.Data, viaRelay: true);

    private void OnLinkMessage(string type, JsonElement data)
    {
        if (Array.IndexOf(HandledTypes, type) < 0) return;
        var obj = data.ValueKind == JsonValueKind.Object ? JsonObject.Create(data) ?? new JsonObject() : new JsonObject();
        HibFeatures.Dispatch(() => HandleIncoming(type, (JsonObject)obj.DeepClone(), viaRelay: false));
    }

    private void OnLinkConnectionChanged(bool connected)
    {
        if (!connected) return;
        // When the phone connects, remember the pairing (enables the relay) and share relay config.
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000).ConfigureAwait(false);
            FindMyRelayKeys.MarkPaired(_store, _link.PairingKey);
            await PushRelayConfigAsync().ConfigureAwait(false);
            Relay.EnsureSubscribed();
        });
    }

    public async Task SettingsChangedAsync()
    {
        Relay.Stop();
        Relay.EnsureSubscribed();
        await PushRelayConfigAsync().ConfigureAwait(false);
    }

    private Task PushRelayConfigAsync()
    {
        if (!_link.IsConnected) return Task.CompletedTask;
        return _link.SendAsync("findRelayConfig", new JsonObject
        {
            ["enabled"] = Relay.Settings.RelayEnabled,
            ["server"] = Relay.Settings.NormalizedServer ?? FindMySettings.DefaultServer,
        });
    }

    // MARK: - Routing

    public FindMyRoute NearbyRoute
    {
        get
        {
            if (_link.IsConnected) return FindMyRoute.Nearby;
            if (_adb?.CurrentSerial != null) return FindMyRoute.Adb;
            if (Relay.IsAvailable) return FindMyRoute.Internet;
            return FindMyRoute.None;
        }
    }

    private FindMyRoute Send(string type, JsonObject? data = null)
    {
        data ??= new JsonObject();
        if (_link.IsConnected)
        {
            _ = _link.SendAsync(type, data);
            LastRoute = FindMyRoute.Nearby;
            return FindMyRoute.Nearby;
        }
        if (!Relay.IsAvailable)
        {
            LastRoute = FindMyRoute.None;
            Status = "The phone isn't nearby and the internet relay isn't set up. Connect the phone once, then try again.";
            return FindMyRoute.None;
        }
        Relay.EnsureSubscribed();
        LastRoute = FindMyRoute.Internet;
        _ = Task.Run(async () =>
        {
            var ok = await Relay.PublishAsync(type, data).ConfigureAwait(false);
            if (!ok) Status = Relay.LastError ?? "Couldn't send over the internet.";
        });
        return FindMyRoute.Internet;
    }

    // MARK: - PC → phone

    public void Locate()
    {
        IsLocating = true;
        Status = "Locating your phone…";
        var route = Send("findLocate");
        if (route == FindMyRoute.None) { IsLocating = false; return; }
        _locateTimeout?.Cancel();
        var cts = _locateTimeout = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(route == FindMyRoute.Internet ? 60 : 35), cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled || !IsLocating) return;
            IsLocating = false;
            Status = "The phone didn't answer. It may be offline; the last known location is shown.";
        }, TaskScheduler.Default);
    }

    /// <summary>Ring over the relay (RingService uses this when nothing nearby is available).</summary>
    public bool RingViaRelay(bool start)
    {
        if (!Relay.IsAvailable) return false;
        Relay.EnsureSubscribed();
        LastRoute = FindMyRoute.Internet;
        PhoneRingingViaRelay = start;
        Status = start ? "Ringing your phone over the internet…" : "Stopping…";
        _ = Task.Run(() => Relay.PublishAsync("findRing", new JsonObject { ["action"] = start ? "start" : "stop" }));
        return true;
    }

    public void Lock()
    {
        Status = "Locking your phone…";
        if (_link.IsConnected) Send("findLock");
        else if (_adb?.CurrentSerial != null) { LastRoute = FindMyRoute.Adb; _ = LockOverAdbAsync(); }
        else Send("findLock");
    }

    public void ShowMessage(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return;
        if (trimmed.Length > 500) trimmed = trimmed[..500];
        var route = Send("findMessage", new JsonObject { ["text"] = trimmed });
        if (route != FindMyRoute.None) Status = "Message sent to your phone.";
    }

    public void TestRelay()
    {
        if (!Relay.IsAvailable)
        {
            RelayTestResult = "Relay not set up: enable it and connect the phone once so both sides share the key.";
            return;
        }
        IsTestingRelay = true;
        RelayTestResult = "Testing…";
        Relay.EnsureSubscribed();
        // If the phone is connected nearby its relay stream is paused; ask it to listen briefly.
        if (_link.IsConnected) _ = _link.SendAsync("findRelayWake", new JsonObject { ["seconds"] = 90 });
        var id = FindMyCodec.RandomNonce();
        _pendingPing = (id, DateTime.UtcNow);
        _ = Task.Run(async () =>
        {
            var ok = await Relay.PublishAsync("findPing", new JsonObject { ["id"] = id }).ConfigureAwait(false);
            if (!ok)
            {
                IsTestingRelay = false;
                _pendingPing = null;
                RelayTestResult = Relay.LastError ?? "Couldn't publish to the relay.";
            }
        });
        _pingTimeout?.Cancel();
        var cts = _pingTimeout = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(45), cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled || _pendingPing?.Id != id) return;
            _pendingPing = null;
            IsTestingRelay = false;
            RelayTestResult = "Sent, but the phone didn't answer within 45 s. Check the phone has internet and H.I.B is allowed to run in the background.";
        }, TaskScheduler.Default);
    }

    private async Task LockOverAdbAsync()
    {
        if (_adb?.CurrentSerial is null || _adb.AdbPath is null)
        {
            Status = "Couldn't lock: no accessibility service on the phone and no ADB connection.";
            return;
        }
        var ok = await _adb.LockPhoneAsync().ConfigureAwait(false);
        Status = ok ? "Phone locked (via ADB)." : "ADB couldn't lock the phone.";
    }

    /// <summary>Tell the phone whether this PC's alarm is ringing (after a findRingMac request).</summary>
    public void ReportDesktopRingState(bool ringing)
        => Reply("findMacRingState", new JsonObject { ["ringing"] = ringing }, _lastPhoneCommandViaRelay);

    // MARK: - Incoming (link or relay)

    /// <summary>Entry point for every Find My message from the phone.</summary>
    public void HandleIncoming(string type, JsonObject data, bool viaRelay)
    {
        switch (type)
        {
            case "findLocation":
                _locateTimeout?.Cancel();
                IsLocating = false;
                if (PhoneLocationReport.FromJson(data) is { } report)
                {
                    LastLocation = report;
                    _store.Set(LocationStoreKey, report);
                    Status = $"Location updated {(viaRelay ? "over the internet" : "nearby")}.";
                }
                else
                {
                    Status = Str(data, "error") ?? "The phone couldn't get a location.";
                }
                break;
            case "findLockResult":
                var ok = Bool(data, "ok");
                var method = Str(data, "method") ?? "";
                if (ok) Status = $"Phone locked ({method}).";
                else if (_adb?.CurrentSerial != null) { LastRoute = FindMyRoute.Adb; _ = LockOverAdbAsync(); }
                else Status = "Couldn't lock: turn on H.I.B Attachment Automation (accessibility) on the phone.";
                break;
            case "findMessageResult":
                Status = Bool(data, "ok") ? "The message is showing on your phone." : "The phone couldn't show the message.";
                break;
            case "findRingState":
                var ringing = Bool(data, "ringing");
                PhoneRingingViaRelay = ringing;
                PhoneRingStateReported?.Invoke(ringing);
                break;
            case "findPong":
                if (_pendingPing is not { } ping || Str(data, "id") != ping.Id) return;
                var ms = (int)(DateTime.UtcNow - ping.SentAt).TotalMilliseconds;
                _pendingPing = null;
                _pingTimeout?.Cancel();
                IsTestingRelay = false;
                RelayTestResult = $"Round trip OK: {ms} ms via {Relay.Settings.NormalizedServer ?? "relay"}.";
                break;
            case "findRingMac":
                _lastPhoneCommandViaRelay = viaRelay;
                DesktopRingRequested?.Invoke(Str(data, "action") != "stop");
                break;
            case "findLocateMac":
                _ = Task.Run(async () =>
                {
                    JsonObject report;
                    try
                    {
                        report = DesktopLocationProvider is { } p ? await p().ConfigureAwait(false) : DefaultDesktopLocation();
                    }
                    catch (Exception ex)
                    {
                        report = new JsonObject { ["macName"] = Environment.MachineName, ["timestamp"] = FindMyCodec.NowMs(), ["provider"] = "none", ["error"] = ex.Message };
                    }
                    Reply("findMacLocation", report, viaRelay);
                });
                break;
            case "findPing":
                Reply("findPong", new JsonObject { ["id"] = Str(data, "id") ?? "" }, viaRelay: true);
                break;
        }
    }

    private static JsonObject DefaultDesktopLocation() => new()
    {
        ["macName"] = Environment.MachineName,
        ["timestamp"] = FindMyCodec.NowMs(),
        ["provider"] = "none",
        ["error"] = "Location isn't available on this PC.",
    };

    private void Reply(string type, JsonObject data, bool viaRelay)
    {
        if (!viaRelay && _link.IsConnected) _ = _link.SendAsync(type, data);
        else if (_link.IsConnected && type != "findPong") _ = _link.SendAsync(type, data);
        else _ = Task.Run(() => Relay.PublishAsync(type, data));
    }

    private static string? Str(JsonObject d, string key) => d[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    private static bool Bool(JsonObject d, string key) => d[key] is JsonValue v && v.TryGetValue(out bool b) && b;

    public void Dispose()
    {
        if (_started)
        {
            Relay.MessageReceived -= OnRelayMessage;
            _link.MessageReceived -= OnLinkMessage;
            _link.ConnectionChanged -= OnLinkConnectionChanged;
        }
        Relay.Dispose();
    }
}
