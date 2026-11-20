using System.Text.Json;
using System.Text.Json.Nodes;
using HIB.Features.Abstractions;
using HIB.Features.Adb;
using HIB.Features.FindMy;

namespace HIB.Features.Ring;

/// <summary>
/// Ring My Phone (port of PhoneRingController). Rings at full volume, even on silent, until stopped.
/// Route: the phone link when connected; ADB broadcast when the link is down; the Find My relay when
/// there is neither.
/// </summary>
public sealed class RingService : ObservableObject, IDisposable
{
    private readonly IPhoneLink _link;
    private readonly AdbService _adb;
    private readonly FindMyService? _findMy;
    private CancellationTokenSource? _safety;
    private bool _ringingViaRelay;

    private bool _isRinging;
    private string? _lastError;

    /// <summary>The phone stops itself after 120 s; we clear the button after 125 s.</summary>
    public static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(125);

    public RingService(IPhoneLink link, AdbService adb, FindMyService? findMy = null)
    {
        _link = link;
        _adb = adb;
        _findMy = findMy;
        _link.MessageReceived += OnMessage;
        if (_findMy != null) _findMy.PhoneRingStateReported += PhoneReported;
    }

    public bool IsRinging { get => _isRinging; private set => SetProperty(ref _isRinging, value); }
    public string? LastError { get => _lastError; private set => SetProperty(ref _lastError, value); }

    public bool CanRing => _link.IsConnected || _adb.IsConnected || (_findMy?.Relay.IsAvailable ?? false);

    public void Toggle()
    {
        if (IsRinging) Stop(); else Start();
    }

    public void Start()
    {
        LastError = null;
        if (!_link.IsConnected && _adb.CurrentSerial is null && _findMy != null && _findMy.RingViaRelay(true))
        {
            _ringingViaRelay = true;
            SetRinging(true);
            return;
        }
        _ringingViaRelay = false;
        var sentOverLink = _link.IsConnected;
        if (!sentOverLink && (_adb.CurrentSerial is null || _adb.AdbPath is null))
        {
            // (The Mac sets this error and then flips the button on anyway; we don't.)
            LastError = "Phone isn't connected over Wi-Fi, Bluetooth or ADB.";
            SetRinging(false);
            return;
        }
        if (sentOverLink) _ = _link.SendAsync("ringPhone", new JsonObject { ["action"] = "start" });
        else _ = RingOverAdbAsync(true);
        SetRinging(true);
    }

    public void Stop()
    {
        if (_ringingViaRelay)
        {
            _ringingViaRelay = false;
            _findMy?.RingViaRelay(false);
            SetRinging(false);
            return;
        }
        if (_link.IsConnected) _ = _link.SendAsync("ringPhone", new JsonObject { ["action"] = "stop" });
        if (_adb.IsConnected) _ = RingOverAdbAsync(false);
        SetRinging(false);
    }

    /// <summary>The phone reported its ringing state (ringState / findRingState).</summary>
    public void PhoneReported(bool ringing) => SetRinging(ringing);

    private void OnMessage(string type, JsonElement data)
    {
        if (type != "ringState") return;
        var ringing = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("ringing", out var r) && r.ValueKind == JsonValueKind.True;
        HibFeatures.Dispatch(() => PhoneReported(ringing));
    }

    private void SetRinging(bool ringing)
    {
        IsRinging = ringing;
        _safety?.Cancel();
        _safety = null;
        if (!ringing) return;
        var cts = _safety = new CancellationTokenSource();
        _ = Task.Delay(SafetyTimeout, cts.Token).ContinueWith(t => { if (!t.IsCanceled) IsRinging = false; }, TaskScheduler.Default);
    }

    private async Task RingOverAdbAsync(bool start)
    {
        if (_adb.CurrentSerial is null || _adb.AdbPath is null)
        {
            if (start && !_link.IsConnected)
            {
                LastError = "Phone isn't connected over Wi-Fi, Bluetooth or ADB.";
                IsRinging = false;
            }
            return;
        }
        await _adb.SendRingBroadcastAsync(start).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _link.MessageReceived -= OnMessage;
        if (_findMy != null) _findMy.PhoneRingStateReported -= PhoneReported;
        _safety?.Cancel();
    }
}
