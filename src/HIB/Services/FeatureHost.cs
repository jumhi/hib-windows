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
        _core.Clipboard?.SuppressNextLocalChange(text);
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
    readonly DispatcherTimer _guardian;
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

        // ADB follows the link: connect on every device message, disconnect when the phone drops.
        core.Link.MessageReceived += (_, e) =>
        {
            if (e.Type == "device") _ = AutoConnectAdbAsync(silent: true);
        };
        core.Link.ConnectionChanged += (_, connected) =>
        {
            if (!connected && Adb.IsConnected) _ = Adb.DisconnectAsync();
            if (connected) _ = Scenes.PublishScenesAsync(Array.Empty<PhoneSceneInfo>());
        };

        Adb.ConnectionFailed += r => Ui.Post(() =>
        {
            var vm = new ToastViewModel
            {
                Caption = "ADB",
                Title = "Couldn't connect to the phone over ADB",
                Body = r.Diagnosis ?? r.FailureText ?? "Turn on Wireless debugging on the phone.",
                Icon = ImageUtil.AppIcon(),
                Seconds = 12,
            };
            vm.Buttons.Add(new ToastButton("Pair with ADB QR", () => { app.ShowMainWindow("phone"); vm.Close(); }, "primary"));
            app.Toasts.Show(vm);
        });
        Mirror.MirrorExited += e => Ui.Post(() =>
        {
            if (e.ExitCode == 0 && !e.NeedsPairing) return;
            var vm = new ToastViewModel
            {
                Caption = "Mirroring",
                Title = e.NeedsPairing ? "The phone needs to be paired for ADB" : "Mirroring stopped",
                Body = Mirror.LastError ?? "",
                Icon = ImageUtil.AppIcon(),
                Seconds = 12,
            };
            if (e.NeedsPairing) vm.Buttons.Add(new ToastButton("Pair with ADB QR", () => { app.ShowMainWindow("phone"); vm.Close(); }, "primary"));
            app.Toasts.Show(vm);
        });
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

        // Guardian (PROTOCOL §7.1): ADB wanted but down → ask for fresh ports, then retry quietly.
        _guardian = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _guardian.Tick += async (_, _) =>
        {
            if (!AdbAutoConnect || !core.Link.IsConnected || Adb.IsConnected || Adb.IsConnecting || Adb.UserRequestedDisconnect || Adb.AdbPath is null) return;
            await core.Link.SendAsync("refreshAdbPorts");
            await Task.Delay(1500);
            await AutoConnectAdbAsync(silent: true);
        };
        _guardian.Start();
    }

    public ISettingsStore Settings { get; }
    public SessionPhoneLink Link { get; }
    public AdbService Adb { get; }
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

    public bool AdbAutoConnect
    {
        get => Settings.Get<bool?>(AdbAutoKey) ?? true;
        set => Settings.Set(AdbAutoKey, value);
    }

    public bool ToolsAvailable => Adb.AdbPath is not null && Adb.Locator.ScrcpyPath is not null;

    public string DeviceNameForWindows => _core.Link.Device?.Name ?? _core.Settings.LastPhoneName ?? "Phone";

    /// <summary>Connects ADB to the current phone using the ports it reported.</summary>
    public async Task<AdbConnectResult?> AutoConnectAdbAsync(bool silent, bool killServer = false)
    {
        var d = _core.Link.Device;
        if (d is null || Adb.AdbPath is null) return null;
        if (silent && (!AdbAutoConnect || Adb.IsConnected || Adb.UserRequestedDisconnect)) return null;
        var ip = !string.IsNullOrEmpty(d.IpAddress) && d.IpAddress != "Unknown" ? d.IpAddress : d.RemoteAddress;
        return await Adb.ConnectAsync(ip, d.AdbPorts, killServer, silent);
    }

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
        _guardian.Stop();
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
