using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HIB.Core;
using HIB.Services;
using HIB.ViewModels;
using HIB.Views;

namespace HIB;

public partial class App : Application
{
    /// <summary>The running app (null in tests / designer).</summary>
    public static AppController? Controller { get; private set; }

    /// <summary>The main view model — feature modules register sidebar pages via <c>App.Main.RegisterPage(...)</c>.</summary>
    public static MainViewModel? Main { get; private set; }

    TrayIcon? _tray;
    NativeMenuItem? _ringItem;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>Set by Program for H.I.B-Setup.exe / --install / --uninstall: shows only the Setup window.</summary>
    public static HIB.Core.Install.SetupRequest Setup { get; set; } = HIB.Core.Install.SetupRequest.None;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime setupDesktop && Setup.Action != HIB.Core.Install.SetupAction.None)
        {
            setupDesktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var exe = HIB.Core.Platform.PlatformShell.ExecutablePath;
            var vm = new SetupViewModel(Setup.Action, HIB.Core.Install.Installer.ForThisPc(),
                HIB.Core.Install.InstallSource.FromRunningApp(exe), exe);
            var setupWindow = new SetupWindow(vm);
            setupDesktop.MainWindow = setupWindow;
            setupWindow.Show();
            base.OnFrameworkInitializationCompleted();
            return;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            StartLogFile();
            var core = new HibCore();
            // Never let the pairing key reach hib.log (the scrubber also blanks anything key-shaped).
            HIB.Features.LogScrubber.AddSecret(core.Settings.EncryptionKey);
            var controller = new AppController(core, desktop);
            Controller = controller;

            var window = new MainWindow();
            desktop.MainWindow = window;
            window.HideOnClose = () => core.Settings.CloseToTray;
            window.Closed += (_, _) => controller.Quit();

            core.AttachClipboard(new AvaloniaClipboardBridge(() => desktop.MainWindow));
            core.Clipboard!.EntryAdded += ClipboardHistory.Add;
            core.Files.Received += ReceivedFiles.Add;

            try
            {
                core.Start();
            }
            catch (Exception ex)
            {
                core.AddLog($"Could not start the server on port {core.Link.Port}: {ex.Message}");
                controller.Toasts.Show(new ToastViewModel
                {
                    Caption = "H.I.B",
                    Title = $"Port {core.Link.Port} is busy",
                    Body = "Another app (or another copy of H.I.B) is using it. Close it and restart H.I.B.",
                    Icon = ImageUtil.AppIcon(),
                    Seconds = 20,
                });
            }

            try { controller.Features = new FeatureHost(core, controller); }
            catch (Exception ex) { core.AddLog("Features failed to start: " + ex.Message); }

            Main = new MainViewModel(controller);
            window.DataContext = Main;

            var args = desktop.Args ?? Array.Empty<string>();
            var minimized = args.Contains("--minimized") || core.Settings.StartMinimized;
            if (!minimized) window.Show();

            SetupTray(controller);
            desktop.Exit += (_, _) =>
            {
                _tray?.Dispose();
                core.Dispose();
                HIB.Features.HibLog.Stop();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>%LOCALAPPDATA%\H.I.B\logs\hib.log (≤ 2 MB, one rotation): ADB, scrcpy and app diagnostics.</summary>
    static void StartLogFile()
    {
        try
        {
            var log = HIB.Features.HibLog.Start();
            var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?";
            log.Write("app", $"H.I.B {version} started on {System.Runtime.InteropServices.RuntimeInformation.OSDescription} "
                             + $"({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}), .NET {Environment.Version}");
        }
        catch { }
    }

    void SetupTray(AppController controller)
    {
        var menu = new NativeMenu();
        var open = new NativeMenuItem("Open H.I.B");
        open.Click += (_, _) => controller.ShowMainWindow();
        _ringItem = new NativeMenuItem("Ring my phone");
        _ringItem.Click += async (_, _) =>
        {
            if (controller.Features is { } f) f.Ring.Toggle();
            else await controller.Core.Hub.RingAsync(!controller.Core.Hub.IsRinging);
        };
        var mirror = new NativeMenuItem("Mirror phone");
        mirror.Click += async (_, _) =>
        {
            // The launcher connects ADB if needed and shows progress / the real reason it failed.
            if (controller.Features is not { } f) return;
            await f.Launcher.MirrorAsync();
        };
        var camera = new NativeMenuItem("Open H.I.B Camera");
        camera.Click += (_, _) =>
        {
            if (controller.Features is not { } f) return;
            if (!f.Adb.IsConnected && !f.Camera.IsRunning) { controller.ShowMainWindow("webcam"); return; }
            f.Camera.Toggle();
        };
        var callAudio = new NativeMenuItem("Hear call on this PC");
        callAudio.Click += (_, _) =>
        {
            if (controller.Features is not { } f) return;
            if (!f.Adb.IsConnected) { controller.ShowMainWindow("calls"); return; }
            f.Calls.Audio.Toggle();
        };
        var dial = new NativeMenuItem("Calls…");
        dial.Click += (_, _) => controller.ShowMainWindow("calls");
        var agent = new NativeMenuItem("Phone Agent…");
        agent.Click += (_, _) => controller.ShowMainWindow("agent");
        var pair = new NativeMenuItem("Pair a phone…");
        pair.Click += (_, _) => controller.ShowMainWindow("pair");
        var quit = new NativeMenuItem("Quit H.I.B");
        quit.Click += (_, _) =>
        {
            if (controller.MainWindow is MainWindow mw) mw.AllowClose = true;
            controller.Quit();
        };
        menu.Items.Add(open);
        menu.Items.Add(_ringItem);
        if (controller.Features is not null)
        {
            menu.Items.Add(mirror);
            menu.Items.Add(camera);
            menu.Items.Add(callAudio);
            menu.Items.Add(dial);
            menu.Items.Add(agent);
        }
        menu.Items.Add(pair);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);

        _tray = new TrayIcon
        {
            Icon = ImageUtil.WindowIcon(),
            ToolTipText = "H.I.B",
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => controller.ShowMainWindow();

        void Refresh()
        {
            var hub = controller.Core.Hub;
            var link = controller.Core.Link;
            var ringing = controller.Features?.Ring.IsRinging ?? hub.IsRinging;
            _ringItem.Header = ringing ? "Stop ringing" : "Ring my phone";
            _ringItem.IsEnabled = controller.Features?.Ring.CanRing ?? link.IsConnected;
            if (controller.Features is { } ff)
            {
                camera.Header = ff.Camera.IsRunning ? "Close H.I.B Camera" : "Open H.I.B Camera";
                callAudio.Header = ff.Calls.Audio.IsActive || ff.Calls.Audio.IsStarting ? "Stop call audio" : "Hear call on this PC";
            }
            _tray.ToolTipText = link.IsConnected && link.Device is { } d ? $"H.I.B — {d.Name}{(d.BatteryLevel >= 0 ? $" · {d.BatteryLevel}%" : "")}" : "H.I.B — waiting for phone";
        }
        controller.Core.Hub.RingStateChanged += _ => Ui.Post(Refresh);
        if (controller.Features is { } fh) fh.Ring.PropertyChanged += (_, _) => Ui.Post(Refresh);
        if (controller.Features is { } fc)
        {
            fc.Camera.PropertyChanged += (_, _) => Ui.Post(Refresh);
            fc.Calls.Audio.PropertyChanged += (_, _) => Ui.Post(Refresh);
        }
        controller.Core.Link.ConnectionChanged += (_, _) => Ui.Post(Refresh);
        controller.Core.Link.DeviceUpdated += (_, _) => Ui.Post(Refresh);
        Refresh();
    }
}
