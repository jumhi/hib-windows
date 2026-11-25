// H.I.B Point to act (docs/SPACE.md §11.3): what the phone does lands on the computer it points at, so
// Windows answers the same messages the Mac does, with the Mac's payloads:
//  - Desk Deck: deckOpen → deckState every 2 s while open; deckAction → deckActionResult
//    (Mac: Core/DeskDeck/DeskDeckManager.swift).
//  - remoteControl: keys (macOS virtual keycodes, translated), text, arrows, volume, media, mouse
//    (Mac: WebSocketServer+Handlers.swift handleRemoteControl, MacRemoteManager).
//  - macMediaControl → media keys / system volume, answered with macMediaControlResponse.
// The operating system part sits behind IDesktopControl / ISystemStats (Windows: WindowsDesktopControl);
// tests use fakes and nothing here ever touches real input elsewhere.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HIB.Core.Link;
using HIB.Core.Roam;
using HIB.Core.Space;

namespace HIB.Core.Act;

public enum MediaKey { PlayPause, Next, Previous, Stop }

/// <summary>What the phone can make this computer do.</summary>
public interface IDesktopControl
{
    /// <summary>False where nothing can be done (other systems): every action then answers "Not available on Windows".</summary>
    bool IsAvailable { get; }
    bool Lock();
    bool Screensaver();
    bool Media(MediaKey key);
    bool VolumeUp();
    bool VolumeDown();
    bool ToggleMute();
    bool SetVolume(int percent);
    /// <summary>The output volume 0–100, or null when unknown.</summary>
    int? Volume { get; }
    /// <summary>Mutes / unmutes the default microphone; the new state (true = muted), or null when there is none.</summary>
    bool? ToggleMicMute();
    bool Screenshot();
    /// <summary>Task View (the Mac's Mission Control).</summary>
    bool TaskView();
    bool ShowDesktop();
    /// <summary>Starts an app by name; null when it started, else why not.</summary>
    string? OpenApp(string name);
    /// <summary>Brightness one step up (+1) or down (−1) on every display that allows it; false when none does.</summary>
    bool Brightness(int direction);
    /// <summary>A key by USB HID usage (as Roam types them: the layout of this PC applies).</summary>
    void Key(int usage, bool down);
    void Text(string text);
    void MouseMove(double dx, double dy);
    void MouseButton(RoamButton button, bool down);
    void Scroll(double dx, double dy);
}

/// <summary>Nothing (other systems).</summary>
public sealed class NullDesktopControl : IDesktopControl
{
    public bool IsAvailable => false;
    public bool Lock() => false;
    public bool Screensaver() => false;
    public bool Media(MediaKey key) => false;
    public bool VolumeUp() => false;
    public bool VolumeDown() => false;
    public bool ToggleMute() => false;
    public bool SetVolume(int percent) => false;
    public int? Volume => null;
    public bool? ToggleMicMute() => null;
    public bool Screenshot() => false;
    public bool TaskView() => false;
    public bool ShowDesktop() => false;
    public string? OpenApp(string name) => DeskDeck.NotAvailable;
    public bool Brightness(int direction) => false;
    public void Key(int usage, bool down) { }
    public void Text(string text) { }
    public void MouseMove(double dx, double dy) { }
    public void MouseButton(RoamButton button, bool down) { }
    public void Scroll(double dx, double dy) { }
}

/// <summary>One deckState sample (the Mac's fields).</summary>
public sealed record DeckStats(int Cpu, int Mem, string MemPressure, long Uptime, int Battery, bool Charging, int NetUp, int NetDown);

public interface ISystemStats
{
    /// <summary>CPU and network are rates since the previous sample (0 on the first).</summary>
    DeckStats Sample();
    /// <summary>Forget the previous sample (a deck just opened).</summary>
    void Reset();
}

public static class DesktopControls
{
    public static IDesktopControl CreateDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            try { return new Windows.WindowsDesktopControl(); }
            catch { /* fall through */ }
        }
        return new NullDesktopControl();
    }

    public static ISystemStats CreateStats() => new SystemStats();
}

public sealed class DeskDeck : IDisposable
{
    public const string NotAvailable = "Not available on Windows";
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    readonly ILinkSession _link;
    readonly IDesktopControl _control;
    readonly ISystemStats _stats;
    readonly TimeSpan _interval;
    readonly object _gate = new();
    readonly Timer _timer;
    readonly TimeSpan _volumeEcho;
    bool _open;
    bool _disposed;

    public DeskDeck(ILinkSession link, IDesktopControl control, ISystemStats stats, TimeSpan? interval = null, TimeSpan? volumeEcho = null)
    {
        _link = link;
        _control = control;
        _stats = stats;
        _interval = interval ?? DefaultInterval;
        _volumeEcho = volumeEcho ?? TimeSpan.FromMilliseconds(500);
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        link.MessageReceived += OnMessage;
        link.ConnectionChanged += (_, connected) =>
        {
            if (!connected) SetOpen(false);
        };
    }

    /// <summary>The phone's audio here ("phone_audio"): the app's scrcpy bridge.</summary>
    public Func<IPhoneAudioPlayer?> Audio { get; set; } = () => null;

    public IDesktopControl Control => _control;

    /// <summary>
    /// Phone Remote's "Allow viewing and control" (SPACE.md §15.1): when it is off, the phone's mouse, keys and typing
    /// are ignored (media and volume still work).
    /// </summary>
    public Func<bool> RemoteInputAllowed { get; set; } = () => true;

    public bool IsOpen { get { lock (_gate) return _open; } }

    public event Action<string>? Log;

    void OnMessage(object? sender, LinkMessageEventArgs e)
    {
        var d = e.Data;
        switch (e.Type)
        {
            case "deckOpen":
                SetOpen(d.ValueKind == JsonValueKind.Object && (d.Bool("open") ?? false));
                break;
            case "deckAction":
                if (d.ValueKind == JsonValueKind.Object && d.Str("action") is { Length: > 0 } action)
                {
                    var arg = d.Str("arg");
                    _ = Task.Run(() => RunAction(action, arg));
                }
                break;
            case "remoteControl":
                if (d.ValueKind == JsonValueKind.Object && d.Str("action") is { Length: > 0 } remote)
                {
                    var copy = d.Clone();
                    _ = Task.Run(() => Remote(remote, copy));
                }
                break;
            case "macMediaControl":
                if (d.ValueKind == JsonValueKind.Object && d.Str("action") is { Length: > 0 } media)
                {
                    var copy = d.Clone();
                    _ = Task.Run(() => MediaControl(media, copy));
                }
                break;
        }
    }

    // ---------------- deckOpen / deckState ----------------

    public void SetOpen(bool open)
    {
        lock (_gate)
        {
            if (_disposed || open == _open) return;
            _open = open;
            if (open) _stats.Reset();
            // Like the Mac: the first state right away, then every 2 s.
            try { _timer.Change(open ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, open ? _interval : Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }
        WriteLog(open ? "Desk Deck opened on the phone" : "Desk Deck closed");
    }

    void Tick()
    {
        if (!IsOpen) return;
        if (!_link.IsConnected)
        {
            SetOpen(false);
            return;
        }
        DeckStats s;
        try { s = _stats.Sample(); }
        catch (Exception ex)
        {
            WriteLog("Desk Deck state: " + ex.Message);
            return;
        }
        _ = _link.SendAsync("deckState", StateData(s));
    }

    /// <summary>The deckState payload (the Mac's keys; no nextEvent — Windows has no calendar to read).</summary>
    public static Dictionary<string, object?> StateData(DeckStats s) => new()
    {
        ["cpu"] = Math.Clamp(s.Cpu, 0, 100),
        ["mem"] = Math.Clamp(s.Mem, 0, 100),
        ["memPressure"] = s.MemPressure,
        ["uptime"] = s.Uptime,
        ["battery"] = s.Battery,
        ["charging"] = s.Charging,
        ["netUp"] = Math.Max(0, s.NetUp),
        ["netDown"] = Math.Max(0, s.NetDown),
    };

    // ---------------- deckAction ----------------

    /// <summary>Runs one deck action and answers deckActionResult {action, ok, error?}.</summary>
    public async Task RunAction(string action, string? arg)
    {
        (bool Ok, string? Error) r;
        try { r = await DoAction(action, arg).ConfigureAwait(false); }
        catch (Exception ex) { r = (false, ex.Message); }
        var data = new Dictionary<string, object?> { ["action"] = action, ["ok"] = r.Ok };
        if (!r.Ok) data["error"] = r.Error ?? "Action failed";
        await _link.SendAsync("deckActionResult", data).ConfigureAwait(false);
    }

    async Task<(bool Ok, string? Error)> DoAction(string action, string? arg)
    {
        static (bool, string?) Did(bool ok, string? why = null) => ok ? (true, null) : (false, why ?? NotAvailable);
        var c = _control;
        switch (action)
        {
            case "lock": return Did(c.Lock());
            case "screensaver": return Did(c.Screensaver());
            case "play_pause": return Did(c.Media(MediaKey.PlayPause));
            case "next": return Did(c.Media(MediaKey.Next));
            case "previous": return Did(c.Media(MediaKey.Previous));
            case "vol_up": return Did(EchoVolume(c.VolumeUp()));
            case "vol_down": return Did(EchoVolume(c.VolumeDown()));
            case "mute": return Did(c.ToggleMute());
            case "brightness_up":
            case "brightness_down":
                if (!c.IsAvailable) return (false, NotAvailable);
                return Did(c.Brightness(action == "brightness_up" ? 1 : -1), "This PC's display doesn't allow brightness control");
            case "screenshot": return Did(c.Screenshot()); // Win+PrintScreen: saved in Pictures › Screenshots, like ⌘⇧3
            case "mission_control": return Did(c.TaskView());
            case "show_desktop": return Did(c.ShowDesktop());
            case "mic_mute":
                if (!c.IsAvailable) return (false, NotAvailable);
                return c.ToggleMicMute() is null ? (false, "No microphone found on this PC") : (true, null);
            case "phone_audio":
            {
                var audio = Audio();
                if (audio is null || (!audio.CanStart && !audio.IsActive)) return (false, "Connect the phone with ADB first.");
                if (audio.IsActive || audio.IsStarting) audio.Stop();
                else audio.Start();
                return (true, null);
            }
            case "open_app":
            {
                var name = arg?.Trim();
                if (string.IsNullOrEmpty(name)) return (false, "No app chosen. Long-press the tile to pick one.");
                if (!c.IsAvailable) return (false, NotAvailable);
                var why = await Task.Run(() => c.OpenApp(name)).ConfigureAwait(false);
                return why is null ? (true, null) : (false, why);
            }
            default:
                // shortcut / dnd run macOS Shortcuts; anything else is unknown here.
                return (false, NotAvailable);
        }
    }

    /// <summary>After a volume change the Mac reports the new level (macVolume) half a second later; so does Windows.</summary>
    bool EchoVolume(bool ok)
    {
        if (!ok) return false;
        _ = Task.Run(async () =>
        {
            await Task.Delay(_volumeEcho).ConfigureAwait(false);
            if (_control.Volume is { } v) await _link.SendAsync("macVolume", new Dictionary<string, object?> { ["volume"] = v }).ConfigureAwait(false);
        });
        return true;
    }

    // ---------------- remoteControl ----------------

    /// <summary>The phone's Remote (PROTOCOL.md §6.7) — keycodes are macOS virtual keycodes.</summary>
    public void Remote(string action, JsonElement d)
    {
        var c = _control;
        if (action is "keypress" or "type" or "mouse_move" or "mouse_click" or "mouse_scroll"
                or "arrow_up" or "arrow_down" or "arrow_left" or "arrow_right" or "enter" or "space" or "escape"
            && !RemoteInputAllowed())
            return;
        switch (action)
        {
            case "keypress":
                if (d.Int("keycode") is { } code && RoamKeys.FromMacKeycode(code) is { } usage) Press(usage, Modifiers(d));
                break;
            case "type":
                if (d.Str("text") is { Length: > 0 } text)
                {
                    var mods = Modifiers(d);
                    foreach (var m in mods) c.Key(m, true);
                    c.Text(text);
                    foreach (var m in mods.AsEnumerable().Reverse()) c.Key(m, false);
                }
                break;
            case "arrow_up": Press(0x52); break;
            case "arrow_down": Press(0x51); break;
            case "arrow_left": Press(0x50); break;
            case "arrow_right": Press(0x4F); break;
            case "enter": Press(0x28); break;
            case "space": Press(0x2C); break;
            case "escape": Press(RoamKeys.Escape); break;
            case "vol_up": EchoVolume(c.VolumeUp()); break;
            case "vol_down": EchoVolume(c.VolumeDown()); break;
            case "vol_mute": c.ToggleMute(); break;
            case "vol_set":
                if (d.Int("value") is { } value) EchoVolume(c.SetVolume(Math.Clamp(value, 0, 100)));
                break;
            case "media_play_pause": c.Media(MediaKey.PlayPause); break;
            case "media_next": c.Media(MediaKey.Next); break;
            case "media_prev": c.Media(MediaKey.Previous); break;
            case "mouse_move":
                if (d.Double("dx") is { } mx && d.Double("dy") is { } my && double.IsFinite(mx) && double.IsFinite(my)) c.MouseMove(mx, my);
                break;
            case "mouse_click":
                if (d.Str("button") is { } b && d.Bool("isDown") is { } down)
                    c.MouseButton(b switch { "right" => RoamButton.Right, "center" or "middle" => RoamButton.Middle, _ => RoamButton.Left }, down);
                break;
            case "mouse_scroll":
                if (d.Double("dx") is { } sx && d.Double("dy") is { } sy && double.IsFinite(sx) && double.IsFinite(sy)) c.Scroll(sx, sy);
                break;
            case "lock_screen": c.Lock(); break;
            case "screensaver": c.Screensaver(); break;
            case "brightness_up": c.Brightness(1); break;
            case "brightness_down": c.Brightness(-1); break;
        }
    }

    void Press(int usage, IReadOnlyList<int>? modifiers = null)
    {
        var mods = modifiers ?? Array.Empty<int>();
        foreach (var m in mods) _control.Key(m, true);
        _control.Key(usage, true);
        _control.Key(usage, false);
        foreach (var m in mods.Reverse()) _control.Key(m, false);
    }

    /// <summary>
    /// The phone's modifier names (made for a Mac) as PC keys: ⌘/command works as Ctrl (Roam's default), Control is Ctrl,
    /// Option is Alt, Shift is Shift; "win"/"meta" is the Windows key; fn has no key of its own.
    /// </summary>
    public static IReadOnlyList<int> Modifiers(JsonElement d)
    {
        var list = new List<int>();
        if (d.ValueKind != JsonValueKind.Object || !d.TryGetProperty("modifiers", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var m in arr.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.String) continue;
            int? usage = (m.GetString() ?? "").Trim().ToLowerInvariant() switch
            {
                "shift" => RoamKeys.LeftShift,
                "ctrl" or "control" or "command" or "cmd" => RoamKeys.LeftCtrl,
                "option" or "alt" => RoamKeys.LeftAlt,
                "win" or "windows" or "meta" or "super" => RoamKeys.LeftGui,
                _ => null,
            };
            if (usage is { } u && !list.Contains(u)) list.Add(u);
        }
        return list;
    }

    // ---------------- macMediaControl ----------------

    public async Task MediaControl(string action, JsonElement d)
    {
        var c = _control;
        var ok = action switch
        {
            "play" or "pause" or "playPause" or "toggle" => c.Media(MediaKey.PlayPause),
            "next" => c.Media(MediaKey.Next),
            "previous" or "prev" => c.Media(MediaKey.Previous),
            "stop" => c.Media(MediaKey.Stop),
            "vol_up" or "volumeUp" or "up" => EchoVolume(c.VolumeUp()),
            "vol_down" or "volumeDown" or "down" => EchoVolume(c.VolumeDown()),
            "mute" or "vol_mute" or "toggleMute" => c.ToggleMute(),
            "vol_set" or "setVolume" => (d.Int("volume") ?? d.Int("value")) is { } v && EchoVolume(c.SetVolume(Math.Clamp(v, 0, 100))),
            _ => false,
        };
        await _link.SendAsync("macMediaControlResponse", new Dictionary<string, object?> { ["action"] = action, ["success"] = ok }).ConfigureAwait(false);
    }

    void WriteLog(string line)
    {
        try { Log?.Invoke(line); } catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _open = false;
        }
        _link.MessageReceived -= OnMessage;
        try { _timer.Dispose(); } catch { }
    }
}
