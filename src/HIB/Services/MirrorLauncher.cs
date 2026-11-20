using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using HIB.Core.Platform;
using HIB.Features;
using HIB.Features.Adb;
using HIB.Features.Mirror;

namespace HIB.Services;

/// <summary>
/// Every Mirror / Desktop / app-window click goes through here, so none of them ends silently: an immediate
/// "Starting…" card, ADB connected on the spot (waiting out an automatic connect instead of failing "busy"),
/// then either the window opens (card closes), it is slow ("Mirroring is taking long" + scrcpy's last line),
/// or it fails with the real reason (scrcpy's ERROR line or adb's connect error) — never a quiet page switch.
/// </summary>
public sealed partial class MirrorLauncher : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public const string ToastKey = "mirror-status";
    static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(30);

    readonly AppController _app;
    readonly FeatureHost _f;
    ToastViewModel? _card;
    DateTime _cardAt;
    Guid? _waitingFor;

    public MirrorLauncher(AppController app, FeatureHost f)
    {
        _app = app;
        _f = f;
        f.Mirror.MirrorWindowOpened += s => Ui.Post(() => OnWindowOpened(s));
        f.Mirror.MirrorSlowToOpen += e => Ui.Post(() => OnSlow(e));
        f.Mirror.MirrorExited += e => Ui.Post(() => OnExited(e));
    }

    /// <summary>A start is in progress (ADB connect + launching scrcpy).</summary>
    [ObservableProperty] public partial bool IsStarting { get; private set; }
    /// <summary>What is starting (for the button that was pressed).</summary>
    [ObservableProperty] public partial MirrorKind? StartingKind { get; private set; }
    /// <summary>The last problem, for pages that show a status line.</summary>
    [ObservableProperty] public partial string LastProblem { get; private set; } = "";

    public Task<MirrorSession?> MirrorAsync() => StartAsync(new MirrorRequest(MirrorKind.Mirror, _f.DeviceNameForWindows));
    public Task<MirrorSession?> DesktopAsync() => StartAsync(new MirrorRequest(MirrorKind.Desktop, _f.DeviceNameForWindows));
    public Task<MirrorSession?> AppAsync(string package, string? label)
        => StartAsync(new MirrorRequest(MirrorKind.App, _f.DeviceNameForWindows, package, label));

    static string What(MirrorRequest r) => r.Kind switch
    {
        MirrorKind.Desktop => "desktop mode",
        MirrorKind.App => r.WindowTitle ?? "the app",
        MirrorKind.Camera => "the camera",
        _ => "mirroring",
    };

    static string Capitalized(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    public async Task<MirrorSession?> StartAsync(MirrorRequest request)
    {
        var what = What(request);
        if (IsStarting)
        {
            // A second click while starting: keep the card in view rather than doing nothing.
            Progress($"Starting {what}…", "Still starting the previous window — one moment.");
            return null;
        }
        IsStarting = true;
        StartingKind = request.Kind;
        LastProblem = "";
        _waitingFor = null;
        var phone = _f.DeviceNameForWindows;
        HibFeatures.Log?.Invoke($"[mirror] {Capitalized(what)} requested for {phone}");
        try
        {
            Progress($"Starting {what}…", _f.Adb.IsConnected ? "Opening the phone screen…" : $"Connecting to {phone} over ADB…");
            var adbProblem = await EnsureAdbAsync();
            if (adbProblem is not null)
            {
                Problem("Couldn't connect to the phone over ADB", adbProblem.Value.Reason, adbButton: true, pairButton: adbProblem.Value.Pairing);
                return null;
            }

            Progress($"Starting {what}…", "Opening the phone screen. This takes a few seconds.");
            var session = await _f.Mirror.StartAsync(request);
            if (session is null)
            {
                Problem($"Couldn't start {what}", _f.Mirror.LastError ?? "scrcpy didn't start.", adbButton: !_f.Adb.IsConnected);
                return null;
            }
            _waitingFor = session.Id;
            if (session.WindowShown) CloseCard();
            return session;
        }
        catch (Exception ex)
        {
            Problem($"Couldn't start {what}", ex.Message);
            return null;
        }
        finally
        {
            IsStarting = false;
            StartingKind = null;
        }
    }

    /// <summary>Null when ADB is ready; otherwise the real reason it isn't.</summary>
    async Task<(string Reason, bool Pairing)?> EnsureAdbAsync()
    {
        var adb = _f.Adb;
        if (adb.AdbPath is null)
            return (OperatingSystem.IsWindows()
                ? "H.I.B couldn't find adb.exe. Reinstall H.I.B (adb ships in its tools folder)."
                : "adb isn't installed (brew install android-platform-tools).", false);

        // "Connected" may be stale: Wireless debugging gets a new port each time it restarts.
        var stale = false;
        if (adb.IsConnected)
        {
            if (await adb.VerifyConnectedAsync()) return null;
            stale = true;
            Progress("Reconnecting ADB…", "The phone's Wireless debugging port changed. Getting the new one…");
        }
        if (adb.WiredAdbEnabled && await adb.ResolveWiredSerialAsync() is not null) return null;

        var link = _app.Core.Link;
        if (link.Device is null) return ("Connect your phone to H.I.B first (it tells H.I.B its Wireless debugging port).", false);

        // H.I.B connects ADB by itself after every phone update; wait for that instead of failing as "busy".
        await WaitWhileConnectingAsync();
        if (adb.IsConnected) return null;

        // No port, or a dead one: ask the phone for fresh ports. (mDNS can't find a phone on another subnet,
        // so the port the phone reports is the reliable way.)
        var refreshed = false;
        if (stale || link.Device.AdbPorts.Count == 0)
        {
            await RefreshPortsAsync();
            refreshed = true;
            if (adb.IsConnected) return null;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var r = await _f.AutoConnectAdbAsync(silent: false, notify: false);
            if (r is null) return ("ADB isn't available on this PC.", false);
            if (r.Success || adb.IsConnected) return null;
            if (r.Output == AdbService.BusyOutput)
            {
                await WaitWhileConnectingAsync();
                if (adb.IsConnected) return null;
                continue;
            }
            if (!refreshed && link.IsConnected)
            {
                // The reported port may be old; one retry with fresh ones.
                await RefreshPortsAsync();
                refreshed = true;
                if (adb.IsConnected) return null;
                continue;
            }
            var reason = AdbService.Explain(r);
            var pairing = reason.Contains("authorized", StringComparison.OrdinalIgnoreCase)
                          || reason.Contains("paired", StringComparison.OrdinalIgnoreCase)
                          || reason.Contains("refused", StringComparison.OrdinalIgnoreCase);
            return (reason, pairing);
        }
        return adb.IsConnected ? null : ("ADB is still busy connecting to the phone. Try again in a moment.", false);
    }

    /// <summary>Asks the phone to re-send its ADB ports and waits (≤ 3 s) for the updated device message.</summary>
    async Task RefreshPortsAsync()
    {
        var link = _app.Core.Link;
        if (!link.IsConnected) return;
        var before = link.Device;
        await link.SendAsync("refreshAdbPorts");
        for (var i = 0; i < 15 && ReferenceEquals(link.Device, before); i++) await Task.Delay(200);
        await WaitWhileConnectingAsync(); // the device message starts H.I.B's own connect
    }

    async Task WaitWhileConnectingAsync()
    {
        var until = DateTime.UtcNow + ConnectWait;
        while (_f.Adb.IsConnecting && DateTime.UtcNow < until) await Task.Delay(250);
    }

    // ---------------- events from MirrorService ----------------

    void OnWindowOpened(MirrorSession s)
    {
        if (_waitingFor == s.Id || _waitingFor is null) CloseCard();
    }

    void OnSlow(MirrorSlow e)
    {
        var last = string.IsNullOrWhiteSpace(e.LastLine) ? "scrcpy hasn't printed anything yet." : "Last message: " + e.LastLine;
        var vm = NewCard("Mirroring is taking long", $"The window hasn't opened yet. {last}", seconds: 25);
        var session = e.Session;
        vm.Buttons.Add(new ToastButton("Stop", () => { _f.Mirror.Stop(session); vm.Close(); }, "danger"));
        vm.Buttons.Add(new ToastButton("Keep waiting", vm.Close));
        Show(vm);
    }

    void OnExited(MirrorExit e)
    {
        if (!e.IsProblem)
        {
            if (_waitingFor == e.Session.Id) CloseCard();
            return;
        }
        Problem(e.Title, e.Reason, pairButton: e.NeedsPairing);
    }

    // ---------------- the status card ----------------

    void Progress(string title, string body)
    {
        // Update the card in place (no flicker); it may still be on its way to the screen.
        if (_card is { } open && (IsOpen(open) || DateTime.UtcNow - _cardAt < TimeSpan.FromSeconds(1)))
        {
            open.Title = title;
            open.Body = body;
            return;
        }
        Show(NewCard(title, body, seconds: 30));
    }

    void Problem(string title, string reason, bool adbButton = false, bool pairButton = false)
    {
        LastProblem = reason;
        HibFeatures.Log?.Invoke($"[mirror] {title}: {reason}");
        var vm = NewCard(title, reason, seconds: 20);
        if (pairButton) vm.Buttons.Add(new ToastButton("Pair with ADB QR", () => { _app.ShowMainWindow("phone"); vm.Close(); }, "primary"));
        else if (adbButton) vm.Buttons.Add(new ToastButton("ADB settings", () => { _app.ShowMainWindow("phone"); vm.Close(); }, "primary"));
        if (HibLog.Current is { } log)
            vm.Buttons.Add(new ToastButton("Show log", () => { PlatformShell.RevealInFolder(log.FilePath); vm.Close(); }));
        Show(vm);
    }

    ToastViewModel NewCard(string title, string body, int seconds) => new()
    {
        Key = ToastKey,
        Caption = "Mirroring",
        Title = title,
        Body = body,
        Icon = ImageUtil.AppIcon(),
        Seconds = seconds,
    };

    void Show(ToastViewModel vm)
    {
        _card = vm;
        _cardAt = DateTime.UtcNow;
        _app.Toasts.Show(vm);
    }

    void CloseCard()
    {
        _card = null;
        _app.Toasts.Close(ToastKey);
    }

    bool IsOpen(ToastViewModel vm)
    {
        try { return _app.Toasts.OpenToasts.Any(w => ReferenceEquals(w.DataContext, vm)); }
        catch { return false; }
    }
}
