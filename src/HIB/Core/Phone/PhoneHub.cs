using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using HIB.Core.Link;
using HIB.Core.Pairing;

namespace HIB.Core.Phone;

/// <summary>
/// Turns raw link messages into typed phone state (notifications, media, calls, ring, flick, apps)
/// and offers typed commands back to the phone. UI-independent; events fire on socket threads.
/// </summary>
public sealed class PhoneHub
{
    readonly ILinkSession _link;
    readonly AppSettings _settings;
    readonly string _iconDir;
    readonly ConcurrentDictionary<string, PhoneNotification> _notifications = new();
    readonly ConcurrentDictionary<string, PhoneApp> _apps = new();
    string? _lastAlbumArtB64;
    byte[]? _lastAlbumArt;

    public PhoneHub(ILinkSession link, AppSettings settings, string dataDir)
    {
        _link = link;
        _settings = settings;
        _iconDir = Path.Combine(dataDir, "AppIcons");
        Directory.CreateDirectory(_iconDir);
        LoadApps();
        link.MessageReceived += OnMessage;
        link.ConnectionChanged += (_, connected) =>
        {
            if (connected) return;
            NowPlaying = null;
            NowPlayingChanged?.Invoke(null);
            if (IsRinging) { IsRinging = false; RingStateChanged?.Invoke(false); }
        };
    }

    public ILinkSession Link => _link;

    // ---------------- state ----------------
    public NowPlaying? NowPlaying { get; private set; }
    public CallInfo? CurrentCall { get; private set; }
    public bool IsRinging { get; private set; }
    public IReadOnlyCollection<PhoneNotification> Notifications => _notifications.Values.OrderByDescending(n => n.Received).ToList();
    public IReadOnlyDictionary<string, PhoneApp> Apps => _apps;

    // ---------------- events ----------------
    public event Action<PhoneNotification>? NotificationPosted;
    public event Action<string>? NotificationRemoved;
    public event Action<NowPlaying?>? NowPlayingChanged;
    public event Action<CallInfo>? CallChanged;
    public event Action<string>? CallProgress;
    public event Action<string>? ClipboardTextReceived;
    public event Action<byte[], string, string>? ClipboardImageReceived; // bytes, mime, name
    public event Action<bool>? RingStateChanged;
    public event Action<FlickInfo>? FlickStarted;
    public event Action? AppsChanged;
    public event Action<string>? Log;

    void OnMessage(object? sender, LinkMessageEventArgs e)
    {
        var d = e.Data;
        switch (e.Type)
        {
            case "notification":
                if (PhoneNotification.Parse(d) is { } n)
                {
                    _notifications[n.Id] = n;
                    NotificationPosted?.Invoke(n);
                }
                break;

            case "notificationUpdate":
                var nid = d.Str("id");
                var action = d.Str("action");
                if (nid is not null && ((d.Bool("dismissed") ?? false) || string.Equals(action, "dismiss", StringComparison.OrdinalIgnoreCase)))
                    RemoveNotificationLocal(nid);
                break;

            case "status":
                HandleStatus(d);
                break;

            case "clipboardUpdate":
                if (d.Str("text") is { Length: > 0 } text) ClipboardTextReceived?.Invoke(text);
                break;

            case "clipboardImageUpdate":
                if (LenientJson.Base64(d.Str("base64")) is { Length: > 0 } img)
                    ClipboardImageReceived?.Invoke(img, d.Str("mime") ?? "image/png", d.Str("name") ?? "HIB-Clipboard.png");
                break;

            case "call_event":
                var call = CallInfo.Parse(d);
                CurrentCall = call.IsOver ? null : call;
                CallChanged?.Invoke(call);
                break;

            case "call_progress":
                CallProgress?.Invoke(d.Str("eventId") ?? "");
                break;

            case "ringState":
                IsRinging = d.Bool("ringing") ?? false;
                RingStateChanged?.Invoke(IsRinging);
                break;

            case "flickStart":
                HandleFlickStart(d);
                break;

            case "appIcons":
                HandleAppIcons(d);
                break;
        }
    }

    void HandleStatus(JsonElement d)
    {
        var music = d.Obj("music");
        if (music is null) return;
        var m = music.Value;
        var b64 = m.Str("albumArt");
        if (string.IsNullOrEmpty(b64)) b64 = m.Str("albumArtLite");
        if (b64 != _lastAlbumArtB64)
        {
            _lastAlbumArtB64 = b64;
            _lastAlbumArt = LenientJson.Base64(b64);
        }
        var np = new NowPlaying
        {
            IsPlaying = m.Bool("isPlaying") ?? false,
            Title = m.Str("title") ?? "",
            Artist = m.Str("artist") ?? "",
            Volume = m.Int("volume") ?? 0,
            IsMuted = m.Bool("isMuted") ?? false,
            AlbumArt = _lastAlbumArt,
            DurationMs = m.Long("duration") ?? 0,
            PositionMs = m.Long("position") ?? 0,
            PositionTimestamp = m.Long("positionTimestamp") ?? 0,
            IsBuffering = m.Bool("isBuffering") ?? false,
            LikeStatus = m.Str("likeStatus") ?? "none",
        };
        NowPlaying = np;
        NowPlayingChanged?.Invoke(np);
    }

    void HandleFlickStart(JsonElement d)
    {
        var id = d.Str("id");
        if (string.IsNullOrEmpty(id)) return;
        var info = new FlickInfo(id, (d.Str("kind") ?? "file").ToLowerInvariant(), d.Str("name") ?? "", d.Str("mime"),
            d.Long("size") ?? 0, LenientJson.Base64(d.Str("thumbnail")), d.Str("text"))
        {
            Angle = d.Double("angle") ?? 0,
            ExitXRatio = d.Double("exitXRatio") ?? 0.5,
            Velocity = d.Double("velocity") ?? 1800,
            Heading = d.Double("heading"),
            Elevation = d.Double("elevation"),
            From = d.Str("from"),
            Screen = d.Str("screen"),
        };
        FlickStarted?.Invoke(info);
        // link/text have no file transfer: the card has "landed" as soon as we handled it.
        if (info.IsTransferless) _ = _link.SendAsync("flickLanded", new { id });
    }

    void HandleAppIcons(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object) return;
        var seen = new HashSet<string>();
        foreach (var prop in d.EnumerateObject())
        {
            var pkg = prop.Name;
            var v = prop.Value;
            if (v.ValueKind != JsonValueKind.Object) continue;
            seen.Add(pkg);
            var iconPath = Path.Combine(_iconDir, SafeFile(pkg) + ".png");
            var bytes = LenientJson.Base64(v.Str("icon"));
            if (bytes is { Length: > 0 })
            {
                try { File.WriteAllBytes(iconPath, bytes); } catch { }
            }
            _apps[pkg] = new PhoneApp(pkg, v.Str("name") ?? pkg, File.Exists(iconPath) ? iconPath : null,
                v.Bool("listening") ?? true, v.Bool("systemApp") ?? false);
        }
        // The message is the full list: drop anything not in it.
        foreach (var pkg in _apps.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _apps.TryRemove(pkg, out _);
            try { File.Delete(Path.Combine(_iconDir, SafeFile(pkg) + ".png")); } catch { }
        }
        SaveApps();
        Log?.Invoke($"App list: {_apps.Count} apps");
        AppsChanged?.Invoke();
    }

    static string SafeFile(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    void LoadApps()
    {
        try
        {
            var path = Path.Combine(_iconDir, "apps.json");
            if (!File.Exists(path)) return;
            var list = JsonSerializer.Deserialize<List<PhoneApp>>(File.ReadAllText(path)) ?? new();
            foreach (var a in list)
                _apps[a.Package] = a with { IconPath = a.IconPath is not null && File.Exists(a.IconPath) ? a.IconPath : null };
        }
        catch { }
    }

    void SaveApps()
    {
        try
        {
            File.WriteAllText(Path.Combine(_iconDir, "apps.json"), JsonSerializer.Serialize(_apps.Values.ToList()));
            _settings.SavedAppPackages = _apps.Keys.OrderBy(k => k).ToList();
            _settings.Save();
        }
        catch { }
    }

    public string? IconFor(string package) => _apps.TryGetValue(package, out var a) ? a.IconPath : null;

    void RemoveNotificationLocal(string id)
    {
        if (_notifications.TryRemove(id, out _)) NotificationRemoved?.Invoke(id);
    }

    public void ClearNotificationsLocal()
    {
        foreach (var id in _notifications.Keys.ToList()) RemoveNotificationLocal(id);
    }

    // ---------------- commands ----------------

    /// <summary>Dismiss on the phone and locally.</summary>
    public Task<bool> DismissNotificationAsync(string id)
    {
        RemoveNotificationLocal(id);
        return _link.SendAsync("dismissNotification", new { id });
    }

    /// <summary>Button action (text = null) or inline reply (text set). "text" is omitted for buttons.</summary>
    public Task<bool> SendNotificationActionAsync(string id, string actionName, string? replyText = null)
        => _link.SendAsync("notificationAction", new { id, name = actionName, text = replyText });

    public Task<bool> ToggleAppNotificationsAsync(string package, bool enabled)
        => _link.SendAsync("toggleAppNotif", new { package, state = enabled ? "true" : "false" });

    /// <summary>playPause | play | pause | next | previous | stop | toggleLike | like | unlike</summary>
    public Task<bool> MediaControlAsync(string action) => _link.SendAsync("mediaControl", new { action });

    public Task<bool> SeekAsync(long positionMs) => _link.SendAsync("mediaControl", new { action = "seekTo", positionMs });

    // Phone volume: the phone understands increaseVolume/decreaseVolume/toggleMute/setVolume (not the Mac's volumeUp/volumeDown/mute).
    public Task<bool> VolumeUpAsync(int step = 10) => _link.SendAsync("volumeControl", new { action = "increaseVolume", increment = step });
    public Task<bool> VolumeDownAsync(int step = 10) => _link.SendAsync("volumeControl", new { action = "decreaseVolume", decrement = step });
    public Task<bool> ToggleMuteAsync() => _link.SendAsync("volumeControl", new { action = "toggleMute" });
    public Task<bool> SetVolumeAsync(int volume) => _link.SendAsync("volumeControl", new { action = "setVolume", volume = Math.Clamp(volume, 0, 100) });

    /// <summary>accept | decline | end</summary>
    public Task<bool> CallControlAsync(string action) => _link.SendAsync("callControl", new { action });

    public async Task<bool> RingAsync(bool start)
    {
        var ok = await _link.SendAsync("ringPhone", new { action = start ? "start" : "stop" }).ConfigureAwait(false);
        if (ok && IsRinging != start)
        {
            // Optimistic; the phone confirms with ringState.
            IsRinging = start;
            RingStateChanged?.Invoke(start);
        }
        return ok;
    }

    public Task<bool> SendClipboardTextAsync(string text) => _link.SendAsync("clipboardUpdate", new { text });

    public Task<bool> SendClipboardImageAsync(byte[] png)
        => _link.SendAsync("clipboardImageUpdate", new { base64 = Convert.ToBase64String(png), mime = "image/png", name = "HIB-Clipboard.png" });

    public Task<bool> DisconnectPhoneAsync() => _link.SendAsync("disconnectRequest");
}
