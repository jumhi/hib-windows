using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using HIB.Core;
using HIB.Core.Link;
using HIB.Core.Phone;
using HIB.Features;
using HIB.Features.Abstractions;
using HIB.Features.Adb;
using HIB.Features.FindMy;
using HIB.Features.LoginCodes;
using HIB.Features.Mirror;
using HIB.Features.PhoneAgent;
using HIB.Features.PhoneSearch;
using HIB.Features.Ring;
using HIB.Features.CallRelay;
using HIB.Features.Camera;
using HIB.Features.Scenes;

namespace HIB.Services;

/// <summary>Adapts the app's <see cref="ILinkSession"/> to HIB.Features' <see cref="IPhoneLink"/>.</summary>
public sealed class SessionPhoneLink : IPhoneLink, IDisposable
{
    readonly ILinkSession _session;

    public SessionPhoneLink(ILinkSession session)
    {
        _session = session;
        _session.MessageReceived += OnMessage;
        _session.ConnectionChanged += OnConnection;
    }

    public bool IsConnected => _session.IsConnected;
    public string? DeviceName => _session.Device?.Name;

    public byte[]? PairingKey
    {
        get
        {
            try { return _session.GetPairingKey(); } catch { return null; }
        }
    }

    public Task SendAsync(string type, object data) => _session.SendAsync(type, data);

    public event Action<string, JsonElement>? MessageReceived;
    public event Action<bool>? ConnectionChanged;

    void OnMessage(object? sender, LinkMessageEventArgs e) => MessageReceived?.Invoke(e.Type, e.Data);
    void OnConnection(object? sender, bool connected) => ConnectionChanged?.Invoke(connected);

    public void Dispose()
    {
        _session.MessageReceived -= OnMessage;
        _session.ConnectionChanged -= OnConnection;
    }
}

/// <summary>
/// Login codes go to the desktop clipboard without syncing back to the phone. Runs on the UI thread
/// (LoginCodeAutoCopier dispatches through HibFeatures.UiDispatcher). Text only.
/// </summary>
public sealed class AppLoginCodeClipboard : ILoginCodeClipboard
{
    readonly HibCore _core;
    readonly Func<Avalonia.Controls.TopLevel?> _top;
    long _ownWrites;

    public AppLoginCodeClipboard(HibCore core, Func<Avalonia.Controls.TopLevel?> top)
    {
        _core = core;
        _top = top;
    }

    public long ChangeCount => (_core.Clipboard?.LocalChangeCount ?? 0) + Interlocked.Read(ref _ownWrites);

    public object? Snapshot() => _core.Clipboard?.LastLocalText;

    public void WriteCode(string code) => Write(code);

    public void Restore(object snapshot)
    {
        if (snapshot is string text) Write(text);
    }

    void Write(string text)
    {
        Interlocked.Increment(ref _ownWrites);
        // Through the clipboard sync (SPACE.md §16.2): a code from the phone is applied, never sent back to it.
        if (_core.Clipboard is { } sync)
        {
            _ = sync.WriteTextAsync(text);
            return;
        }
        Dispatcher.UIThread.Post(async () =>
        {
            var cb = _top()?.Clipboard;
            if (cb is not null) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, text);
        });
    }
}

/// <summary>Hands Phone Search fetch transfers to HIB.Features' PhoneFetchRegistry.</summary>
public sealed class PhoneFetchSink : IExternalTransferSink
{
    public bool Owns(string id) => PhoneFetchRegistry.IsTracked(id);
    public void Progress(string id, long received, long total) => PhoneFetchRegistry.ReportProgress(id, received, total);
    public bool Complete(string id, string tempPath) => PhoneFetchRegistry.Complete(id, tempPath);
    public void Fail(string id) => PhoneFetchRegistry.Fail(id);
}

/// <summary>
/// Creates and wires every HIB.Features service (ADB, mirroring, audio, ring, Find My, login codes,
/// phone search, scenes, Phone Agent) on top of the core link.
/// </summary>
public sealed class FeatureHost : IDisposable
{
    const string AdbAutoKey = "hib.win.adbAutoConnect";
    readonly HibCore _core;
    readonly AppController _app;
    string? _lastDevicePorts;
    int? _lastConnectPort;
    CancellationTokenSource? _alarm;

    public FeatureHost(HibCore core, AppController app)
    {
        _core = core;
        _app = app;

        // One data folder for everything (%APPDATA%\HIB): features.json, keys\, …
        HibPaths.DataDirectory = core.Options.DataDir;
        HibFeatures.UiDispatcher = a => Dispatcher.UIThread.Post(a);
        HibFeatures.Log = core.AddLog;

        Settings = new JsonFileSettingsStore(Path.Combine(core.Options.DataDir, "features.json"));
        Link = new SessionPhoneLink(core.Link);
        Adb = new AdbService(AdbLocator.Shared, Settings);
        Pairing = new AdbPairingService(Adb);
        AppOps = new AppOpsGranter(Adb);
        AppOps.Log += core.AddLog;
        AppOps.Start();
        Mirror = new MirrorService(Adb, Settings);
        Audio = new PhoneAudioBridge(Adb, Link, Settings);
        FindMy = new FindMyService(Link, Settings, Adb);
        FindMy.Start();
        Ring = new RingService(Link, Adb, FindMy);
        Agent = new PhoneAgentController(Adb, Link, Settings)
        {
            AppNamesProvider = () => core.Hub.Apps.Values.ToDictionary(a => a.Package, a => a.Name),
        };
        Search = new PhoneSearchClient(Link, Adb);
        Camera = new PhoneCameraService(Adb, Settings);
        Calls = new CallRelayService(Adb, Link, Search, Settings);
        Scenes = new ScenesBridge(Link);
        Codes = new LoginCodeAutoCopier(Settings, new AppLoginCodeClipboard(core, () => app.MainWindow), Link);

        core.Files.ExternalSink = new PhoneFetchSink();
        // "Play here" (SPACE.md §3.5) drives the same scrcpy audio bridge.
        core.SpaceEngine.AttachAudio(new PhoneAudioPlayer(Audio));

        // SPACE.md §13: ADB that stays connected — the keeper tries at link-up, when the phone's connectPort changes,
        // after a network change and every 10 s → 60 s while down; quietly (no toasts on retries).
        Keeper = new AdbKeeper(Adb, new AdbKeeperContext
        {
            Enabled = () => AdbAutoConnect,
            LinkUp = () => core.Link.IsConnected,
            PhoneIp = () => PhoneIp,
            PhoneKey = () => core.Link.Device is { } d ? (d.Id is { Length: > 0 } id ? id : d.Name) : null,
            Setup = () => core.Hub.AdbSetup,
            DevicePorts = () => core.Link.Device?.AdbPorts ?? Array.Empty<string>(),
            // The "Set up mirroring" card drives its own connects (and pairing) while it is open.
            Paused = () => Pairing.IsBusy || (Setup?.IsOpen ?? false),
            AskForPorts = () => core.Link.SendAsync("refreshAdbPorts"),
        }, Settings);
        // The device message: a new address or other adbPorts (an older phone app, or the answer to refreshAdbPorts).
        core.Link.MessageReceived += (_, e) =>
        {
            if (e.Type != "device" || core.Link.Device is not { } d) return;
            var seen = PhoneAdbIp(d) + "|" + string.Join(",", d.AdbPorts);
            if (seen == _lastDevicePorts) return;
            _lastDevicePorts = seen;
            _ = Keeper.TriggerAsync(AdbKeeperTrigger.Ports);
        };
        // SPACE.md §8: the phone reports its own ADB setup (ports, pairing service). Pairing and the setup card use it.
        core.Link.DeviceUpdated += (_, d) => Pairing.PhoneIp = PhoneAdbIp(d);
        if (core.Link.Device is { } known) Pairing.PhoneIp = PhoneAdbIp(known);
        core.Hub.AdbSetupChanged += state =>
        {
            if (core.Link.Device is { } dev) Pairing.PhoneIp = PhoneAdbIp(dev);
            Pairing.UpdateSetupState(state);
            // Wireless debugging came on, or its port moved: the keeper connects (or re-checks) at once.
            var key = state is { WirelessDebugging: true } ? state.ConnectPort : null;
            if (state is not null && key != _lastConnectPort)
            {
                _lastConnectPort = key;
                if (key is not null) _ = Keeper.TriggerAsync(AdbKeeperTrigger.ConnectPort);
            }
        };
        core.Link.ConnectionChanged += (_, connected) =>
        {
            if (!connected)
            {
                _lastDevicePorts = null;
                _lastConnectPort = null;
                if (Adb.IsConnected) _ = Adb.DisconnectAsync();
            }
            else
            {
                _ = Keeper.TriggerAsync(AdbKeeperTrigger.LinkUp);
                _ = Scenes.PublishScenesAsync(Array.Empty<PhoneSceneInfo>());
            }
        };
        Keeper.WatchNetwork();
        Keeper.Start();

        // The "Set up mirroring" card (SPACE.md §8.3).
        Setup = new AdbSetupPresenter(app, this);
        Adb.ConnectionFailed += r => Ui.Post(() =>
        {
            var vm = new ToastViewModel
            {
                Caption = "ADB",
                Title = "Couldn't connect to the phone over ADB",
                Body = AdbService.Explain(r) is { Length: > 0 } why ? why : "Turn on Wireless debugging on the phone.",
                Icon = ImageUtil.AppIcon(),
                Seconds = 12,
            };
            vm.Buttons.Add(new ToastButton("Set up mirroring", () => { Setup.Show(); vm.Close(); }, "primary"));
            app.Toasts.Show(vm);
        });
        // Mirror/Desktop/app launcher: progress, real reasons, and the setup card when ADB isn't ready.
        Launcher = new MirrorLauncher(app, this);
        Codes.CodeCopied += (code, source) => Ui.Post(() => app.Toasts.Show(new ToastViewModel
        {
            Caption = "Login code",
            Title = $"{code} copied",
            Body = $"From {source}. Paste it with Ctrl+V.",
            Icon = ImageUtil.AppIcon(),
            Seconds = 8,
        }));
        Agent.AttentionRequested += critical => Ui.Post(() =>
        {
            app.ShowMainWindow("agent");
            if (critical)
                app.Toasts.Show(new ToastViewModel { Caption = "Phone Agent", Title = "Phone Agent needs you", Body = Agent.PendingConfirmation ?? Agent.PendingQuestion ?? Agent.Status, Icon = ImageUtil.AppIcon(), Seconds = 15, OnClick = () => app.ShowMainWindow("agent") });
        });
        Scenes.RunSceneRequested += id => _ = Scenes.ReportSceneResultAsync(id, false, "Scenes aren't available on Windows yet");
        FindMy.DesktopRingRequested += ring => Ui.Post(() => RingThisPc(ring));
    }

    public ISettingsStore Settings { get; }
    public SessionPhoneLink Link { get; }
    public AdbService Adb { get; }
    /// <summary>Keeps ADB connected while the phone is linked (SPACE.md §13); <see cref="AdbKeeper.Status"/> is the quiet line.</summary>
    public AdbKeeper Keeper { get; }
    public AdbPairingService Pairing { get; }
    public AppOpsGranter AppOps { get; }
    public MirrorService Mirror { get; }
    public PhoneAudioBridge Audio { get; }
    public FindMyService FindMy { get; }
    public RingService Ring { get; }
    public PhoneAgentController Agent { get; }
    public PhoneSearchClient Search { get; }
    public PhoneCameraService Camera { get; }
    public CallRelayService Calls { get; }
    public ScenesBridge Scenes { get; }
    public LoginCodeAutoCopier Codes { get; }
    /// <summary>Starts mirror / desktop / app windows with visible progress and real failure reasons.</summary>
    public MirrorLauncher Launcher { get; }
    /// <summary>"Set up mirroring": Developer options → Wireless debugging → pair → ready, no terminal.</summary>
    public AdbSetupPresenter Setup { get; }

    public bool AdbAutoConnect
    {
        get => Settings.Get<bool?>(AdbAutoKey) ?? true;
        set => Settings.Set(AdbAutoKey, value);
    }

    public bool ToolsAvailable => Adb.AdbPath is not null && Adb.Locator.ScrcpyPath is not null;

    public string DeviceNameForWindows => _core.Link.Device?.Name ?? _core.Settings.LastPhoneName ?? "Phone";

    /// <summary>
    /// Connects ADB to the current phone: the phone's own <c>connectPort</c> first, then the ports that worked before
    /// (SPACE.md §13), restarting the adb server once when adb can't reach a phone that answers on the app link.
    /// <paramref name="silent"/> only nudges the keeper (it connects quietly and never toasts).
    /// </summary>
    /// <param name="notify">Show the "Couldn't connect" toast on failure (default: when not silent).</param>
    public async Task<AdbConnectResult?> AutoConnectAdbAsync(bool silent, bool killServer = false, bool? notify = null)
    {
        if (_core.Link.Device is null || !Adb.HasAdb) return null;
        if (silent)
        {
            await Keeper.TriggerAsync(AdbKeeperTrigger.Ports);
            return null;
        }
        if (killServer) await Adb.RestartServerAsync();
        return await Keeper.ConnectNowAsync(notify ?? true);
    }

    /// <summary>The phone's address for adb, when a phone is known.</summary>
    public string? PhoneIp => _core.Link.Device is { } d ? PhoneAdbIp(d) : null;

    /// <summary>The phone's own LAN address (what adbd listens on), else where its link came from.</summary>
    static string PhoneAdbIp(PhoneDeviceInfo d)
        => !string.IsNullOrEmpty(d.IpAddress) && d.IpAddress != "Unknown" ? d.IpAddress : d.RemoteAddress;

    void RingThisPc(bool ring)
    {
        _alarm?.Cancel();
        _alarm = null;
        _app.Toasts.Close("pc-ring");
        if (ring)
        {
            var cts = _alarm = new CancellationTokenSource();
            if (OperatingSystem.IsWindows())
            {
                _ = Task.Run(async () =>
                {
                    var end = DateTime.UtcNow.AddMinutes(2);
                    while (!cts.IsCancellationRequested && DateTime.UtcNow < end)
                    {
                        try { Console.Beep(1760, 250); Console.Beep(1320, 250); } catch { }
                        await Task.Delay(400);
                    }
                    Ui.Post(() => { if (_alarm == cts) RingThisPc(false); });
                });
            }
            var vm = new ToastViewModel
            {
                Key = "pc-ring",
                Persistent = true,
                Caption = "Find My",
                Title = "Your phone is ringing this PC",
                Body = "Found it?",
                Icon = ImageUtil.AppIcon(),
            };
            vm.Buttons.Add(new ToastButton("Stop", () => RingThisPc(false), "primary"));
            _app.Toasts.Show(vm);
        }
        FindMy.ReportDesktopRingState(ring);
    }

    public void Dispose()
    {
        Keeper.Dispose();
        _alarm?.Cancel();
        try { Mirror.StopAll(); } catch { }
        try { Audio.Dispose(); } catch { }
        try { FindMy.Dispose(); } catch { }
        try { Ring.Dispose(); } catch { }
        try { Search.Dispose(); } catch { }
        try { Camera.Dispose(); } catch { }
        try { Calls.Dispose(); } catch { }
        try { Scenes.Dispose(); } catch { }
        try { Codes.Dispose(); } catch { }
        try { AppOps.Dispose(); } catch { }
        Link.Dispose();
    }
}
