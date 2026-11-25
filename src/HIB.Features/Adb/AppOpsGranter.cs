using System.ComponentModel;

namespace HIB.Features.Adb;

/// <summary>
/// Android 10+ only lets the focused app read the clipboard. Two seconds after every ADB connect
/// this grants H.I.B's phone app the READ_CLIPBOARD app-op
/// (<c>adb shell appops set com.sameerasw.airsync READ_CLIPBOARD allow</c>), and for copies made while H.I.B is in the
/// background (SPACE.md §16.3) READ_LOGS and SYSTEM_ALERT_WINDOW. Each runs best-effort; repeating them is harmless.
/// </summary>
public sealed class AppOpsGranter : IDisposable
{
    private readonly AdbService _adb;
    private bool _started;
    private bool _wasConnected;

    public AppOpsGranter(AdbService adb) { _adb = adb; }

    /// <summary>The grants, in order (adb shell …). The first (READ_CLIPBOARD) decides <see cref="Granted"/>.</summary>
    public static readonly IReadOnlyList<string[]> Commands = new[]
    {
        new[] { "appops", "set", AdbService.PhonePackage, "READ_CLIPBOARD", "allow" },
        // §16.3: the phone reads its own "Denying clipboard access" log line…
        new[] { "pm", "grant", AdbService.PhonePackage, "android.permission.READ_LOGS" },
        // …and briefly takes focus with a 1×1 overlay to read the clip.
        new[] { "appops", "set", AdbService.PhonePackage, "SYSTEM_ALERT_WINDOW", "allow" },
    };

    /// <summary>Raised after each grant attempt with success/failure (READ_CLIPBOARD).</summary>
    public event Action<bool>? Granted;

    /// <summary>One <c>[clip]</c> line per grant.</summary>
    public event Action<string>? Log;

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
        if (_adb.CurrentSerial is null || !_adb.HasAdb) return false;
        var first = false;
        for (var i = 0; i < Commands.Count; i++)
        {
            var cmd = Commands[i];
            bool ok;
            try { ok = (await _adb.ShellAsync(cmd).ConfigureAwait(false)).ExitCode == 0; }
            catch { ok = false; }
            if (i == 0) first = ok;
            var what = cmd[0] == "pm" ? cmd[^1].Replace("android.permission.", "") : cmd[^2];
            try { Log?.Invoke($"[clip] grant {what} {(ok ? "ok" : "failed")}"); } catch { }
        }
        Granted?.Invoke(first);
        return first;
    }

    public void Dispose()
    {
        if (_started) _adb.PropertyChanged -= OnAdbChanged;
        _started = false;
    }
}
