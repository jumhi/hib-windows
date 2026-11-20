using System.Text.Json;
using HIB.Features.Abstractions;

namespace HIB.Features.LoginCodes;

/// <summary>Clipboard access the app provides (Windows: Clipboard API + GetClipboardSequenceNumber).</summary>
public interface ILoginCodeClipboard
{
    /// <summary>Monotonic change counter (GetClipboardSequenceNumber on Windows).</summary>
    long ChangeCount { get; }
    /// <summary>Captures the current clipboard so it can be restored (null if too big / empty).</summary>
    object? Snapshot();
    /// <summary>Writes the code WITHOUT syncing it to the phone; mark it as excluded from clipboard
    /// history (ExcludeClipboardContentFromMonitorProcessing / CanIncludeInClipboardHistory=0).</summary>
    void WriteCode(string code);
    void Restore(object snapshot);
}

/// <summary>A phone notification as the copier needs it.</summary>
public sealed record PhoneNotificationInfo(string Nid, string Title, string Body, string App, string Package, bool Ongoing);

/// <summary>
/// When a phone notification carries a one-time login code, copy it to the PC clipboard (without
/// syncing it back), raise <see cref="CodeCopied"/> for the HUD, and optionally restore the previous
/// clipboard after 90 s. Port of LoginCodeAutoCopier.swift (desktop-only logic, §6.13).
/// </summary>
public sealed class LoginCodeAutoCopier : ObservableObject, IDisposable
{
    public const string AutoCopyKey = "hib.loginCodes.autoCopy";
    public const string RestoreKey = "hib.loginCodes.restoreClipboard";
    public static readonly TimeSpan RestoreDelay = TimeSpan.FromSeconds(90);

    private readonly ISettingsStore _settings;
    private readonly ILoginCodeClipboard _clipboard;
    private readonly IPhoneLink? _link;
    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTime> _handled = new();
    private DateTime _deviceConnectedAt = DateTime.MinValue;
    private CancellationTokenSource? _restoreCts;
    private object? _savedClipboard;
    private long _codeChangeCount = -1;

    public LoginCodeAutoCopier(ISettingsStore settings, ILoginCodeClipboard clipboard, IPhoneLink? link = null,
                               Func<DateTime>? clock = null)
    {
        _settings = settings;
        _clipboard = clipboard;
        _link = link;
        _now = clock ?? (() => DateTime.UtcNow);
        if (_link != null)
        {
            _link.ConnectionChanged += OnConnectionChanged;
            _link.MessageReceived += OnMessage;
            if (_link.IsConnected) _deviceConnectedAt = _now();
        }
    }

    public bool IsAutoCopyEnabled
    {
        get => _settings.Get<bool?>(AutoCopyKey) ?? true;
        set { _settings.Set(AutoCopyKey, value); OnPropertyChanged(); }
    }

    public bool IsRestoreEnabled
    {
        get => _settings.Get<bool?>(RestoreKey) ?? true;
        set { _settings.Set(RestoreKey, value); OnPropertyChanged(); }
    }

    /// <summary>(code, source app) after a code was copied — show the HUD / toast.</summary>
    public event Action<string, string>? CodeCopied;

    /// <summary>Raised after the previous clipboard was put back.</summary>
    public event Action? ClipboardRestored;

    private void OnConnectionChanged(bool connected)
    {
        // The phone re-sends its active notifications right after connecting; old codes in that
        // burst must not overwrite the clipboard.
        if (connected) _deviceConnectedAt = _now();
    }

    private void OnMessage(string type, JsonElement data)
    {
        if (type != "notification" || data.ValueKind != JsonValueKind.Object) return;
        string? S(string k) => data.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var id = S("id"); var title = S("title"); var body = S("body"); var app = S("app"); var pkg = S("package");
        if (id is null || title is null || body is null || app is null || pkg is null) return;
        var ongoing = data.TryGetProperty("ongoing", out var o) && o.ValueKind == JsonValueKind.True;
        HibFeatures.Dispatch(() => Handle(new PhoneNotificationInfo(id, title, body, app, pkg, ongoing)));
    }

    /// <summary>Returns the code it copied, or null.</summary>
    public string? Handle(PhoneNotificationInfo notif)
    {
        if (!IsAutoCopyEnabled || notif.Ongoing) return null;
        var now = _now();
        if ((now - _deviceConnectedAt).TotalSeconds <= 6) return null;
        var result = OneTimeCodeDetector.Detect(notif.Title, notif.Body);
        if (result is null) return null;

        lock (_lock)
        {
            foreach (var k in _handled.Where(kv => (now - kv.Value).TotalSeconds >= 15 * 60).Select(kv => kv.Key).ToList())
                _handled.Remove(k);
            var key = $"{notif.Nid}|{result.Code}";
            if (_handled.ContainsKey(key)) return null;
            // Same code arriving again from a different notification (e.g. SMS + email) within 2 min.
            if (_handled.Any(kv => kv.Key.EndsWith("|" + result.Code, StringComparison.Ordinal) && (now - kv.Value).TotalSeconds < 120))
            {
                _handled[key] = now;
                return null;
            }
            _handled[key] = now;
        }

        Copy(result.Code);
        var source = notif.App.Length == 0 ? "your phone" : notif.App;
        CodeCopied?.Invoke(result.Code, source);
        HibFeatures.Trace($"[login-code] copied code from {notif.Package}");
        return result.Code;
    }

    private void Copy(string code)
    {
        // Only snapshot the user's clipboard when it isn't one of our own codes already.
        if (_savedClipboard is null || _clipboard.ChangeCount != _codeChangeCount)
            _savedClipboard = _clipboard.Snapshot();
        _clipboard.WriteCode(code);
        _codeChangeCount = _clipboard.ChangeCount;
        ScheduleRestore();
    }

    private void ScheduleRestore()
    {
        _restoreCts?.Cancel();
        if (!IsRestoreEnabled)
        {
            _savedClipboard = null;
            return;
        }
        var cts = _restoreCts = new CancellationTokenSource();
        _ = Task.Delay(RestoreDelay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) HibFeatures.Dispatch(RestoreIfUnchanged);
        }, TaskScheduler.Default);
    }

    /// <summary>Puts the previous clipboard back unless the user copied something else since.</summary>
    internal void RestoreIfUnchanged()
    {
        _restoreCts = null;
        var saved = _savedClipboard;
        _savedClipboard = null;
        if (_clipboard.ChangeCount != _codeChangeCount || saved is null) return;
        _clipboard.Restore(saved);
        _codeChangeCount = -1;
        ClipboardRestored?.Invoke();
    }

    public void Dispose()
    {
        _restoreCts?.Cancel();
        if (_link != null)
        {
            _link.ConnectionChanged -= OnConnectionChanged;
            _link.MessageReceived -= OnMessage;
        }
    }
}
