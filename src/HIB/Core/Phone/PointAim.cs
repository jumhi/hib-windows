// H.I.B — Point & Flick on the computer (SPACE.md §7): tells the phone which screens this computer has
// (screensInfo), and tracks which one the phone points at (flickAim → the aim light) and the screen being
// learned during Point setup (aimSetup → the point target). UI-free: the app pushes its monitors in with
// SetScreens and draws the overlays from the Lit / Setup events.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using HIB.Core.Link;

namespace HIB.Core.Phone;

/// <summary>
/// A monitor as the phone sees it (§7.2): bounds in DIPs, y down. §10.1 adds what the room needs: the physical size in
/// millimetres (0 = unknown), whether it is the laptop's own panel, its native pixels and its scale.
/// </summary>
public sealed record DisplayScreen(string Id, string Name, bool Primary, double X, double Y, double W, double H)
{
    public double MmW { get; init; }
    public double MmH { get; init; }
    public bool BuiltIn { get; init; }
    public int PxW { get; init; }
    public int PxH { get; init; }
    public double Scale { get; init; } = 1;
}

/// <summary>The screen being learned in Point setup (§7.5).</summary>
public sealed record AimSetupState(DisplayScreen Screen, int Step, int Total, double Progress)
{
    /// <summary>Progress reached 1: the ring turns lime with a check, then fades.</summary>
    public bool Complete => Progress >= 1;
}

/// <summary>
/// The pure state machine behind the aim light and the point target, driven by a clock the caller passes in
/// (tests use a manual one). Not thread-safe on its own: <see cref="PointAim"/> serialises calls.
/// </summary>
public sealed class AimTracker
{
    /// <summary>The aim light hides by itself this long after the last flickAim naming it (§7.4).</summary>
    public static readonly TimeSpan AimTimeout = TimeSpan.FromSeconds(4);
    /// <summary>The point target hides by itself this long after the last aimSetup (§7.5).</summary>
    public static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(6);
    /// <summary>How long the lime ring and check stay up once a screen is captured, before it fades.</summary>
    public static readonly TimeSpan CompleteHold = TimeSpan.FromMilliseconds(800);

    IReadOnlyList<DisplayScreen> _screens = Array.Empty<DisplayScreen>();
    DateTime _litAt;
    DateTime _setupAt;
    DateTime? _completeAt;

    public IReadOnlyList<DisplayScreen> Screens => _screens;
    public DisplayScreen? Lit { get; private set; }
    /// <summary>§11.2: what the aim-light pill says ("Desk Deck"); null = "Flick lands here".</summary>
    public string? Label { get; private set; }
    public AimSetupState? Setup { get; private set; }
    public bool IsActive => Lit is not null || Setup is not null;

    public event Action<DisplayScreen?>? LitChanged;
    public event Action<AimSetupState?>? SetupChanged;

    /// <summary>The screen with this id, else the main screen (§7.4: "an unknown id lights the main screen").</summary>
    public DisplayScreen? Resolve(string? id)
    {
        var screens = _screens;
        if (screens.Count == 0) return null;
        if (!string.IsNullOrEmpty(id) && screens.FirstOrDefault(s => s.Id == id) is { } exact) return exact;
        return screens.FirstOrDefault(s => s.Primary) ?? screens[0];
    }

    /// <summary>Only an exact match (flickStart.screen: otherwise the computer's own rule applies).</summary>
    public DisplayScreen? Find(string? id)
        => string.IsNullOrEmpty(id) ? null : _screens.FirstOrDefault(s => s.Id == id);

    public void SetScreens(IReadOnlyList<DisplayScreen> screens)
    {
        _screens = screens.ToList();
        // A lit screen that went away goes dark; one that moved keeps its light with the new bounds.
        if (Lit is { } lit)
        {
            var now = Find(lit.Id);
            if (now != lit) SetLit(now);
        }
        if (Setup is { } setup)
        {
            var now = Find(setup.Screen.Id);
            if (now is null) SetSetup(null);
            else if (now != setup.Screen) SetSetup(setup with { Screen = now });
        }
    }

    /// <summary>The longest label the pill shows (the phone keeps it short anyway).</summary>
    public const int MaxLabel = 40;

    /// <summary>flickAim: <paramref name="id"/> null = light nothing here. <paramref name="label"/> (§11.2) replaces "Flick lands here".</summary>
    public void ApplyAim(string? id, bool named, DateTime now, string? label = null)
    {
        if (!named)
        {
            SetLit(null);
            return;
        }
        var screen = Resolve(id);
        _litAt = now;
        label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (label is { Length: > MaxLabel }) label = label[..MaxLabel].TrimEnd() + "…";
        if (label != Label && screen is not null && screen == Lit)
        {
            Label = label;
            LitChanged?.Invoke(screen); // same screen, new words
            return;
        }
        Label = label;
        SetLit(screen);
    }

    /// <summary>aimSetup: <paramref name="id"/> null (not named) = hide.</summary>
    public void ApplySetup(string? id, bool named, int step, int total, double progress, DateTime now)
    {
        if (!named)
        {
            _completeAt = null;
            SetSetup(null);
            return;
        }
        var screen = Resolve(id);
        if (screen is null) return;
        progress = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        total = Math.Max(1, total);
        step = Math.Clamp(step, 1, total);
        var next = new AimSetupState(screen, step, total, progress);
        var prev = Setup;
        var sameTarget = prev is not null && prev.Screen.Id == screen.Id && prev.Step == step;
        if (next.Complete)
        {
            // Re-sends of a finished capture don't keep the lime ring up.
            if (!(sameTarget && prev!.Complete && _completeAt is not null)) _completeAt = now;
        }
        else _completeAt = null;
        _setupAt = now;
        SetSetup(next);
    }

    /// <summary>Hides what timed out. Call often (PointAim ticks every 100 ms while anything is shown).</summary>
    public void Tick(DateTime now)
    {
        if (Lit is not null && now - _litAt >= AimTimeout) SetLit(null);
        if (Setup is not null)
        {
            if (_completeAt is { } done && now - done >= CompleteHold)
            {
                _completeAt = null;
                SetSetup(null);
            }
            else if (now - _setupAt >= SetupTimeout)
            {
                _completeAt = null;
                SetSetup(null);
            }
        }
    }

    /// <summary>The phone went away: nothing stays lit.</summary>
    public void Clear()
    {
        _completeAt = null;
        SetLit(null);
        SetSetup(null);
    }

    void SetLit(DisplayScreen? screen)
    {
        if (Lit == screen) return;
        Lit = screen;
        LitChanged?.Invoke(screen);
    }

    void SetSetup(AimSetupState? state)
    {
        if (Setup == state) return;
        Setup = state;
        SetupChanged?.Invoke(state);
    }
}

/// <summary>
/// Wires <see cref="AimTracker"/> to the phone link: screensInfo right after macInfo, on display changes and on
/// screensRequest; flickAim and aimSetup into the tracker; a timer for the auto-hides. Events fire on a
/// background thread (the UI marshals).
/// </summary>
public sealed class PointAim : IDisposable
{
    readonly LinkServer _link;
    readonly Func<DateTime> _now;
    readonly object _gate = new();
    readonly Timer _timer;
    readonly AimTracker _tracker = new();
    readonly List<Action> _pending = new();
    string? _lastSentSignature;
    bool _ticking;

    public PointAim(LinkServer link, Func<DateTime>? clock = null)
    {
        _link = link;
        _now = clock ?? (() => DateTime.UtcNow);
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        _tracker.LitChanged += s => _pending.Add(() => LitChanged?.Invoke(s));
        _tracker.SetupChanged += s => _pending.Add(() => SetupChanged?.Invoke(s));
        link.AfterMacInfo = HandshakeScreensInfo;
        link.MessageReceived += OnMessage;
        link.ConnectionChanged += (_, connected) =>
        {
            if (connected) return;
            Run(() => _tracker.Clear());
            lock (_gate) _lastSentSignature = null;
        };
    }

    /// <summary>The screen the aim light is on (null = none).</summary>
    public DisplayScreen? Lit { get { lock (_gate) return _tracker.Lit; } }
    /// <summary>The aim-light pill's words from the phone (§11.2), or null for "Flick lands here".</summary>
    public string? LitLabel { get { lock (_gate) return _tracker.Label; } }
    /// <summary>The point target (null = hidden).</summary>
    public AimSetupState? Setup { get { lock (_gate) return _tracker.Setup; } }
    public IReadOnlyList<DisplayScreen> Screens { get { lock (_gate) return _tracker.Screens; } }

    public event Action<DisplayScreen?>? LitChanged;
    public event Action<AimSetupState?>? SetupChanged;
    public event Action<string>? Log;

    /// <summary>The app's monitors. Sends screensInfo when they changed and a phone is connected.</summary>
    public void SetScreens(IReadOnlyList<DisplayScreen> screens)
    {
        string before = "", after = "";
        Run(() =>
        {
            before = Signature(_tracker.Screens);
            _tracker.SetScreens(screens);
            after = Signature(_tracker.Screens);
        });
        bool send;
        lock (_gate) send = before != after && after.Length > 0 && after != _lastSentSignature && _link.IsConnected;
        if (send) _ = SendScreensInfoAsync();
    }

    /// <summary>The screen with exactly this id (flickStart.screen), or null.</summary>
    public DisplayScreen? Find(string? id) { lock (_gate) return _tracker.Find(id); }

    /// <summary>The <c>screensInfo</c> payload (§7.2).</summary>
    public object ScreensInfoData()
    {
        IReadOnlyList<DisplayScreen> screens;
        lock (_gate) screens = _tracker.Screens;
        return BuildScreensInfo(screens);
    }

    public static object BuildScreensInfo(IReadOnlyList<DisplayScreen> screens) => new Dictionary<string, object?>
    {
        ["screens"] = screens.Select(s => new Dictionary<string, object?>
        {
            ["id"] = s.Id,
            ["name"] = s.Name,
            ["primary"] = s.Primary,
            ["x"] = Math.Round(s.X, 2),
            ["y"] = Math.Round(s.Y, 2),
            ["w"] = Math.Round(s.W, 2),
            ["h"] = Math.Round(s.H, 2),
            // §10.1
            ["mmW"] = (int)Math.Round(s.MmW, MidpointRounding.AwayFromZero),
            ["mmH"] = (int)Math.Round(s.MmH, MidpointRounding.AwayFromZero),
            ["builtIn"] = s.BuiltIn,
            ["pxW"] = s.PxW > 0 ? s.PxW : (int)Math.Round(s.W * s.Scale),
            ["pxH"] = s.PxH > 0 ? s.PxH : (int)Math.Round(s.H * s.Scale),
            ["scale"] = Math.Round(s.Scale > 0 ? s.Scale : 1, 2),
        }).ToArray(),
    };

    public async System.Threading.Tasks.Task<bool> SendScreensInfoAsync()
    {
        IReadOnlyList<DisplayScreen> screens;
        lock (_gate)
        {
            screens = _tracker.Screens;
            if (screens.Count == 0) return false;
            _lastSentSignature = Signature(screens);
        }
        return await _link.SendAsync("screensInfo", BuildScreensInfo(screens)).ConfigureAwait(false);
    }

    (string Type, object Data)? HandshakeScreensInfo()
    {
        lock (_gate)
        {
            var screens = _tracker.Screens;
            if (screens.Count == 0) return null; // the app hasn't told us yet: SetScreens sends it then
            _lastSentSignature = Signature(screens);
            return ("screensInfo", BuildScreensInfo(screens));
        }
    }

    void OnMessage(object? sender, LinkMessageEventArgs e)
    {
        var d = e.Data;
        switch (e.Type)
        {
            case "screensRequest":
                _ = SendScreensInfoAsync();
                break;
            case "flickAim":
            {
                var (id, named) = ScreenField(d);
                var label = d.ValueKind == JsonValueKind.Object ? d.Str("label") : null;
                Run(() => _tracker.ApplyAim(id, named, _now(), label));
                break;
            }
            case "aimSetup":
            {
                var (id, named) = ScreenField(d);
                Run(() => _tracker.ApplySetup(id, named, d.Int("step") ?? 1, d.Int("total") ?? 1, d.Double("progress") ?? 0, _now()));
                break;
            }
        }
    }

    /// <summary>"screen": "&lt;id&gt;" names a screen (even ""); null or missing names none.</summary>
    static (string? id, bool named) ScreenField(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object || !d.TryGetProperty("screen", out var p)) return (null, false);
        return p.ValueKind switch
        {
            JsonValueKind.String => (p.GetString(), true),
            JsonValueKind.Number => (p.GetRawText(), true),
            _ => (null, false),
        };
    }

    /// <summary>Hides what timed out (the timer calls this; tests may too).</summary>
    public void Tick() => Run(() => _tracker.Tick(_now()));

    /// <summary>Runs a tracker change under the lock, then raises its events outside it and (re)arms the timer.</summary>
    void Run(Action change)
    {
        List<Action> fire;
        bool active;
        lock (_gate)
        {
            change();
            fire = _pending.ToList();
            _pending.Clear();
            active = _tracker.IsActive;
            if (active != _ticking)
            {
                _ticking = active;
                try { _timer.Change(active ? 100 : Timeout.Infinite, active ? 100 : Timeout.Infinite); } catch (ObjectDisposedException) { }
            }
        }
        foreach (var f in fire)
        {
            try { f(); }
            catch (Exception ex) { Log?.Invoke("Point & Flick handler threw: " + ex.Message); }
        }
    }

    static string Signature(IReadOnlyList<DisplayScreen> screens)
        => string.Join("|", screens.Select(s => $"{s.Id}/{s.Name}/{s.Primary}/{s.X:0.##},{s.Y:0.##},{s.W:0.##}x{s.H:0.##}/{s.MmW:0.#}x{s.MmH:0.#}/{s.BuiltIn}/{s.PxW}x{s.PxH}@{s.Scale:0.###}"));

    public void Dispose() => _timer.Dispose();
}
