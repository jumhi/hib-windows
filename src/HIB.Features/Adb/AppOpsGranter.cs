using System.ComponentModel;

namespace HIB.Features.Adb;

/// <summary>
/// Android 10+ only lets the focused app read the clipboard. Two seconds after every ADB connect
/// this grants H.I.B's phone app the READ_CLIPBOARD app-op
/// (<c>adb shell appops set com.sameerasw.airsync READ_CLIPBOARD allow</c>). Repeating it is harmless.
/// </summary>
public sealed class AppOpsGranter : IDisposable
{
    private readonly AdbService _adb;
    private bool _started;
    private bool _wasConnected;

    public AppOpsGranter(AdbService adb) { _adb = adb; }

    /// <summary>Raised after each grant attempt with success/failure.</summary>
    public event Action<bool>? Granted;

    public TimeSpan SettleDelay { get; set; } = TimeSpan.FromSeconds(2);

    public void Start()
    {
        if (_started) return;
        _started = true;
        _wasConnected = _adb.IsConnected;
        _adb.PropertyChanged += OnAdbChanged;
        if (_wasConnected) _ = GrantAfterDelayAsync();
    }

    private void OnAdbChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AdbService.IsConnected)) return;
        var now = _adb.IsConnected;
        if (now && !_wasConnected) _ = GrantAfterDelayAsync();
        _wasConnected = now;
    }

    private async Task GrantAfterDelayAsync()
    {
        await Task.Delay(SettleDelay).ConfigureAwait(false);
        await GrantAsync().ConfigureAwait(false);
    }

    /// <summary>Runs the grant now.</summary>
    public async Task<bool> GrantAsync()
    {
        if (_adb.CurrentSerial is null || _adb.AdbPath is null) return false;
        var r = await _adb.ShellAsync("appops", "set", AdbService.PhonePackage, "READ_CLIPBOARD", "allow").ConfigureAwait(false);
        var ok = r.ExitCode == 0;
        Granted?.Invoke(ok);
        return ok;
    }

    public void Dispose()
    {
        if (_started) _adb.PropertyChanged -= OnAdbChanged;
        _started = false;
    }
}
