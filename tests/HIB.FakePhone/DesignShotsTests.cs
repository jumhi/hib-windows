using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HIB.Services;
using HIB.ViewModels;
using HIB.Views;

namespace HIB.FakePhone;

/// <summary>
/// Drives the real main window with a fake phone (wallpaper, battery, music, apps, notifications, other
/// computers) and renders every screen at 2× — the design screenshots in docs/design come from here.
/// Set HIB_DESIGN_DIR to save the PNGs; without it the test still renders and checks every page.
/// </summary>
public class DesignShotsTests
{
    static readonly string? ShotDir = Environment.GetEnvironmentVariable("HIB_DESIGN_DIR");

    static async Task Pump(int ms = 300)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    static void Shot(TopLevel w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (w is Window win)
        {
            var text = string.Join("\n", win.GetVisualDescendantsText());
            Assert.DoesNotContain("Not Found", text);
        }
        if (ShotDir is null) return;
        Directory.CreateDirectory(ShotDir);
        frame!.Save(Path.Combine(ShotDir, name + ".png"));
    }

    static byte[] Png(int w, int h, Action<DrawingContext> draw)
    {
        using var rtb = new RenderTargetBitmap(new PixelSize(w, h));
        using (var ctx = rtb.CreateDrawingContext()) draw(ctx);
        using var ms = new MemoryStream();
        rtb.Save(ms);
        return ms.ToArray();
    }

    static string B64(byte[] png) => "data:image/png;base64," + Convert.ToBase64String(png);

    static byte[] Wallpaper() => Png(540, 1170, ctx =>
    {
        ctx.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#0E3F63"), 0), new GradientStop(Color.Parse("#1D7F98"), 0.5), new GradientStop(Color.Parse("#0A2240"), 1) },
        }, new Rect(0, 0, 540, 1170));
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#3348E0D0")), null, new Point(420, 300), 300, 420);
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#26A8F0FF")), null, new Point(80, 860), 260, 360);
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#1AFFFFFF")), null, new Point(300, 640), 120, 520);
    });

    static byte[] AlbumArt() => Png(300, 300, ctx =>
    {
        ctx.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#0F4C5C"), 0), new GradientStop(Color.Parse("#0B1D33"), 1) },
        }, new Rect(0, 0, 300, 300));
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#5539D5C8")), null, new Point(190, 110), 110, 110);
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#40F2B84B")), null, new Point(90, 210), 70, 70);
    });

    static byte[] AppIcon(string color, string letter, string fg = "#FFFFFF") => Png(96, 96, ctx =>
    {
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse(color)), null, new RoundedRect(new Rect(0, 0, 96, 96), 22));
        var ft = new FormattedText(letter, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("fonts:Inter#Inter", FontStyle.Normal, FontWeight.Bold), 46, new SolidColorBrush(Color.Parse(fg)));
        ctx.DrawText(ft, new Point((96 - ft.Width) / 2, (96 - ft.Height) / 2));
    });

    [AvaloniaFact]
    public async Task Every_screen_renders_with_a_live_phone()
    {
        await using var host = new TestHost();
        var window = new MainWindow { Width = 1100, Height = 760 };
        window.SetRenderScaling(2);
        var controller = new AppController(host.Core, null) { WindowOverride = window };
        using var features = new FeatureHost(host.Core, controller);
        controller.Features = features;
        features.AdbAutoConnect = false; // the fake phone's address isn't real: don't run adb against it
        var main = new MainViewModel(controller);
        window.DataContext = main;
        window.Show();
        await Pump();
        Assert.False(main.IsPaired);
        Shot(window, "windows-pair");

        await using var phone = new FakePhone(host.Core.BuildQr()) { Name = "Haitham's S26 Ultra" };
        await phone.ConnectAsync();
        await phone.SendDeviceAsync(Convert.ToBase64String(Wallpaper()));
        await phone.WaitForAsync("macInfo");
        var icons = new Dictionary<string, object>
        {
            ["com.whatsapp"] = new { name = "WhatsApp", icon = B64(AppIcon("#25D366", "W")), listening = true, systemApp = false },
            ["com.google.android.gm"] = new { name = "Gmail", icon = B64(AppIcon("#FFFFFF", "M", "#EA4335")), listening = true, systemApp = false },
            ["com.google.android.calendar"] = new { name = "Calendar", icon = B64(AppIcon("#1A73E8", "31")), listening = true, systemApp = false },
            ["org.telegram.messenger"] = new { name = "Telegram", icon = B64(AppIcon("#2AABEE", "T")), listening = true, systemApp = false },
            ["com.spotify.music"] = new { name = "Spotify", icon = B64(AppIcon("#191414", "S", "#1DB954")), listening = true, systemApp = false },
            ["com.google.android.youtube"] = new { name = "YouTube", icon = B64(AppIcon("#FF0000", "▶")), listening = false, systemApp = false },
            ["com.google.android.apps.maps"] = new { name = "Maps", icon = B64(AppIcon("#34A853", "M")), listening = true, systemApp = false },
            ["com.android.chrome"] = new { name = "Chrome", icon = B64(AppIcon("#FBBC04", "C", "#1F1F1F")), listening = true, systemApp = false },
            ["com.instagram.android"] = new { name = "Instagram", icon = B64(AppIcon("#E1306C", "I")), listening = true, systemApp = false },
            ["com.ubercab"] = new { name = "Uber", icon = B64(AppIcon("#000000", "U")), listening = true, systemApp = false },
            ["com.netflix.mediaclient"] = new { name = "Netflix", icon = B64(AppIcon("#141414", "N", "#E50914")), listening = true, systemApp = false },
            ["com.slack"] = new { name = "Slack", icon = B64(AppIcon("#ECB22E", "S", "#1F1F1F")), listening = true, systemApp = false },
            ["com.google.android.keep"] = new { name = "Keep Notes", icon = B64(AppIcon("#FBBC04", "K", "#1F1F1F")), listening = true, systemApp = false },
            ["com.sec.android.app.camera"] = new { name = "Camera", icon = B64(AppIcon("#3C4043", "◉")), listening = true, systemApp = true },
            ["com.careem.acma"] = new { name = "Careem", icon = B64(AppIcon("#3EB34A", "C")), listening = true, systemApp = false },
            ["com.linkedin.android"] = new { name = "LinkedIn", icon = B64(AppIcon("#0A66C2", "in")), listening = true, systemApp = false },
        };
        await phone.SendAsync("appIcons", icons);
        await phone.SendAsync("status", new
        {
            battery = new { level = 76, isCharging = false },
            isPaired = true,
            music = new
            {
                isPlaying = true, title = "Midnight City", artist = "M83", volume = 60, isMuted = false,
                albumArt = Convert.ToBase64String(AlbumArt()), duration = 243000, position = 64000, positionTimestamp = 0, isBuffering = false, likeStatus = "none",
            },
        });
        await phone.SendAsync("desktopsList", new
        {
            phone = new { id = "phone", name = "Haitham's S26 Ultra" },
            desktops = new object[]
            {
                new { id = "Haitham’s MacBook", name = "Haitham’s MacBook", platform = "mac", type = "MacBook Pro", connected = true },
                new { id = "Office PC", name = "Office PC", platform = "windows", type = "Windows PC", connected = false },
            },
        });
        await phone.SendAsync("notification", new { id = "n0", title = "Standup in 10 minutes", body = "Daily sync · Meet", app = "Calendar", package = "com.google.android.calendar", priority = "alerting" });
        await phone.SendAsync("notification", new { id = "n1", title = "Your boarding pass", body = "Flight SV 1020 · Gate B12 · Boarding 18:40", app = "Gmail", package = "com.google.android.gm", priority = "alerting",
            actions = new[] { new { name = "Archive", type = "button" } } });
        await phone.SendAsync("notification", new { id = "n2", title = "Mom", body = "Call me when you land ❤️", app = "WhatsApp", package = "com.whatsapp", priority = "alerting",
            actions = new[] { new { name = "Reply", type = "reply" }, new { name = "Mark as read", type = "button" } } });
        await phone.SendAsync("notification", new { id = "n3", title = "Ali", body = "Are we still on for 7?", app = "WhatsApp", package = "com.whatsapp", priority = "alerting",
            actions = new[] { new { name = "Reply", type = "reply" }, new { name = "Mark as read", type = "button" } } });
        await Pump(600);
        Assert.True(main.IsPaired);
        Assert.True(main.Home.IsConnected);
        Assert.Equal(4, main.Notifications.Items.Count);
        Assert.Equal(3, main.Notifications.Groups.Count);
        Assert.True(main.Apps.Apps.Count >= 10);
        Assert.Equal(3, main.Devices.Devices.Count);

        main.Navigate("notifications");
        await Pump();
        Shot(window, "windows-home");

        main.Notifications.IsStacked = false;
        await Pump();
        Shot(window, "windows-notifications-flat");
        main.Notifications.IsStacked = true;

        main.Navigate("apps");
        await Pump();
        Shot(window, "windows-apps");
        main.Apps.SearchText = "ma";
        await Pump();
        Assert.Equal("Maps", main.Apps.Apps[0].Name);
        Shot(window, "windows-apps-search");
        main.Apps.SearchText = "";

        main.Navigate("devices");
        await Pump();
        Shot(window, "windows-devices");

        main.Navigate("settings");
        await Pump();
        Assert.True(main.IsSettings);
        Shot(window, "windows-settings");
        foreach (var key in new[] { "alerts", "clipboard", "files", "flick", "phone", "webcam", "calls", "findmy", "search", "agent", "pair", "about" })
        {
            main.Navigate(key);
            await Pump(150);
            Shot(window, "windows-settings-" + key);
        }

        // A notification pop-up and a landed file, like the Mac's cards.
        var toast = new ToastViewModel
        {
            Caption = "WhatsApp", Title = "Ali", Body = "Are we still on for 7?", HasReply = true, ReplyPlaceholder = "Reply…",
            Icon = ImageUtil.FromBytes(AppIcon("#25D366", "W")), Persistent = true,
        };
        toast.Buttons.Add(new ToastButton("Mark as read", () => { }));
        controller.Toasts.Show(toast);
        await Pump(400);
        var tw = controller.Toasts.OpenToasts.OfType<ToastWindow>().First();
        tw.SetRenderScaling(2);
        await Pump(100);
        Shot(tw, "windows-toast");

        // The smallest window, and the phone going away (the column shows "Looking for…").
        foreach (var t in controller.Toasts.OpenToasts.ToList()) t.Close();
        main.Navigate("notifications");
        window.Width = 860;
        window.Height = 620;
        await Pump();
        Shot(window, "windows-min-size");
        window.Width = 1100;
        window.Height = 760;
        await phone.DisposeAsync();
        Assert.True(await Eventually.TrueAsync(() => { Dispatcher.UIThread.RunJobs(); return !main.Home.IsConnected; }, TimeSpan.FromSeconds(10)));
        main.Navigate("devices");
        await Pump();
        Assert.True(main.IsPaired);
        Shot(window, "windows-phone-away");

        window.AllowClose = true;
        window.Close();
    }

    /// <summary>A 1440×900 Windows desktop (wallpaper, an app window, a 48 px taskbar) at 2×, to show the overlays on.</summary>
    static void Desktop(DrawingContext ctx)
    {
        ctx.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#0C1733"), 0), new GradientStop(Color.Parse("#173A6E"), 0.55), new GradientStop(Color.Parse("#0A1328"), 1) },
        }, new Rect(0, 0, 2880, 1800));
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#332A6FDB")), null, new Point(2200, 500), 900, 620);
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#2219B4E0")), null, new Point(600, 1300), 820, 520);
        // An app window.
        var win = new Rect(360, 220, 1560, 980);
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#F01B2030")), new Pen(new SolidColorBrush(Color.Parse("#26FFFFFF")), 2), new RoundedRect(win, 16));
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#FF242A3E")), null, new RoundedRect(new Rect(win.X, win.Y, win.Width, 72), 16, 16, 0, 0));
        for (var i = 0; i < 7; i++)
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse(i == 0 ? "#40FFFFFF" : "#1FFFFFFF")), null,
                new RoundedRect(new Rect(win.X + 64, win.Y + 140 + i * 92, i == 0 ? 640 : 1100 - i * 70, i == 0 ? 36 : 24), 8));
        // The taskbar.
        ctx.FillRectangle(new SolidColorBrush(Color.Parse("#F2101422")), new Rect(0, 1704, 2880, 96));
        ctx.FillRectangle(new SolidColorBrush(Color.Parse("#14FFFFFF")), new Rect(0, 1704, 2880, 2));
        var colors = new[] { "#3B82F6", "#F59E0B", "#10B981", "#E5E7EB", "#6366F1", "#EF4444" };
        for (var i = 0; i < colors.Length; i++)
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse(colors[i])), null, new RoundedRect(new Rect(1440 - colors.Length * 44 + i * 88 + 16, 1724, 56, 56), 12));
    }

    /// <summary>
    /// A 1440×900 window showing <see cref="Desktop"/> with the overlay's content moved onto it at its place (headless
    /// frames are opaque, so the overlay can't be captured on its own and blended).
    /// </summary>
    static async Task<Window> OnDesktop(Window overlay, PixelRect area)
    {
        await Pump(50);
        Assert.NotNull(overlay.CaptureRenderedFrame()); // the overlay window itself renders
        var content = (Control)overlay.Content!;
        overlay.Content = null;
        content.Width = area.Width / 2.0;
        content.Height = area.Height / 2.0;
        content.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        content.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        content.Margin = new Thickness(area.X / 2.0, area.Y / 2.0, 0, 0);
        var desktop = new Bitmap(new MemoryStream(Png(2880, 1800, Desktop)));
        var host = new Window
        {
            Width = 1440,
            Height = 900,
            Content = new Panel { Children = { new Image { Source = desktop, Stretch = Stretch.Fill }, content } },
        };
        host.SetRenderScaling(2);
        host.Show();
        await Pump(250);
        return host;
    }

    static byte[] Capture(TopLevel w)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var ms = new MemoryStream();
        frame!.Save(ms);
        return ms.ToArray();
    }

    static void SaveShot(byte[] png, string name)
    {
        if (ShotDir is null) return;
        Directory.CreateDirectory(ShotDir);
        File.WriteAllBytes(Path.Combine(ShotDir, name + ".png"), png);
    }

    [AvaloniaFact]
    public async Task Point_and_flick_overlays()
    {
        // A 1440×900-point screen at 200% with a 48-point taskbar.
        var screen = FakeScreen.Make(@"\\.\DISPLAY1", new PixelRect(0, 0, 2880, 1800), 2, true, new PixelRect(0, 0, 2880, 1704));

        // The aim light: the bottom of the screen glows, the pill sits above the taskbar.
        var light = new HIB.Views.Flick.AimLightWindow(@"\\.\DISPLAY1");
        light.SetRenderScaling(2);
        light.ShowOn(screen);
        await Pump(300);
        Assert.Equal(1800, light.Area.Bottom);
        var host = await OnDesktop(light, light.Area);
        SaveShot(Capture(host), "windows-aim-light");
        host.Close();
        light.FadeOutAndClose();

        // SPACE.md §11.2: pointing with Desk Deck open, the pill says what the phone does.
        var deckLight = new HIB.Views.Flick.AimLightWindow(@"\\.\DISPLAY1");
        deckLight.SetRenderScaling(2);
        deckLight.SetLabel("Desk Deck");
        deckLight.ShowOn(screen);
        await Pump(300);
        Assert.Equal("Desk Deck", deckLight.LabelText);
        host = await OnDesktop(deckLight, deckLight.Area);
        SaveShot(Capture(host), "windows-aim-light-deck");
        host.Close();
        deckLight.FadeOutAndClose();

        // Point setup's target, part filled, then captured (lime ring and check).
        var display = new HIB.Core.Phone.DisplayScreen(@"\\.\DISPLAY1", "Display 1", true, 0, 0, 1440, 900);
        var target = new HIB.Views.Flick.PointTargetWindow(@"\\.\DISPLAY1");
        target.SetRenderScaling(2);
        target.Update(new HIB.Core.Phone.AimSetupState(display, 1, 2, 0.62));
        target.ShowOn(screen);
        await Pump(300);
        host = await OnDesktop(target, target.Area);
        await Pump(400);
        SaveShot(Capture(host), "windows-point-target");
        target.Update(new HIB.Core.Phone.AimSetupState(display, 1, 2, 1));
        await Pump(500);
        SaveShot(Capture(host), "windows-point-target-done");
        host.Close();
        target.FadeOutAndClose();
        await Pump(300);
        Assert.True(light.IsClosed && target.IsClosed);
    }

    [AvaloniaFact]
    public async Task Roam_devices_row_and_overlays()
    {
        // The Devices tab with a Mac on the direct link: the Mouse & keyboard row asks where the Mac is.
        await using (var setup = await RoamUiTests.Setup.StartAsync(renderScale: 2))
        {
            setup.Main.Navigate("devices");
            await RoamUiTests.PumpUntil(() => setup.MacCard?.HasRoam == true);
            await Pump(400);
            Assert.True(setup.MacCard!.RoamAsks);
            Shot(setup.Window, "windows-roam-devices");

            // While the Mac drives this PC.
            await setup.Mac.SendRawAsync("""{"type":"ctlEnter","data":{"x":0,"y":500,"clipboard":null}}""");
            await RoamUiTests.PumpUntil(() => setup.MacCard?.RoamActive == true);
            await Pump(200);
            Shot(setup.Window, "windows-roam-devices-in-use");
        }

        // The edge glow on a 1440×900-point screen at 200 %, the cursor two thirds up the right edge.
        var screen = FakeScreen.Make(@"\\.\DISPLAY1", new PixelRect(0, 0, 2880, 1800), 2, true, new PixelRect(0, 0, 2880, 1704));
        var glow = new HIB.Views.Roam.RoamEdgeWindow(HIB.Core.Roam.RoamSide.Right, @"\\.\DISPLAY1");
        glow.SetRenderScaling(2);
        glow.Update(HIB.Core.Roam.RoamSides.PillText(HIB.Core.Roam.RoamSide.Right, RoamUiTests.MacName), 330);
        glow.ShowOn(screen);
        await Pump(300);
        Assert.Equal(2880, glow.Area.Right);
        var host = await OnDesktop(glow, glow.Area);
        SaveShot(Capture(host), "windows-roam-edge-glow");
        host.Close();
        glow.FadeOutAndClose();

        // A file dragged in from the Mac: the ghost card under the cursor.
        var ghost = new HIB.Views.Roam.RoamGhostWindow();
        ghost.SetRenderScaling(2);
        ghost.Update(new HIB.Core.Roam.RoamGhost("d1", "Quarterly report.pdf", 1, 0, 0));
        ghost.Show();
        await Pump(100);
        host = await OnDesktop(ghost, new PixelRect(1500, 760, (int)((HIB.Views.Roam.RoamGhostWindow.CardWidth + 24) * 2), (int)((HIB.Views.Roam.RoamGhostWindow.CardHeight + 24) * 2)));
        SaveShot(Capture(host), "windows-roam-ghost");
        host.Close();
        ghost.Close();

        // A computer that moved: "… is on this side now · Undo" at the edge it was learned on.
        var pill = HIB.Views.Roam.RoamNoticeWindow.Pill($"{RoamUiTests.MacName} is on this side now", () => { });
        pill.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        pill.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        var desk = new Bitmap(new MemoryStream(Png(2880, 1800, Desktop)));
        var noticeHost = new Window { Width = 1440, Height = 900, Content = new Panel { Children = { new Image { Source = desk, Stretch = Stretch.Fill }, pill } } };
        noticeHost.SetRenderScaling(2);
        noticeHost.Show();
        await Pump(300);
        SaveShot(Capture(noticeHost), "windows-roam-notice");
        noticeHost.Close();
    }
    [AvaloniaFact]
    public async Task Roam_round2_row_settings_and_pills()
    {
        // SPACE.md §12: the Mouse & keyboard row with speeds, natural scrolling, latency (a slow Wi-Fi link) and Get log.
        await using (var setup = await RoamUiTests.Setup.StartAsync(renderScale: 2))
        {
            setup.Window.Height = 1040;
            setup.Main.Navigate("devices");
            await RoamUiTests.PumpUntil(() => setup.MacCard?.HasRoam == true);
            setup.Host.Core.Roam.SetSide(RoamUiTests.MacName, HIB.Core.Roam.RoamSide.Left);
            setup.Host.Core.Roam.SetPointerSpeed(RoamUiTests.MacName, 1.25);
            setup.Host.Core.Roam.SetNaturalScroll(RoamUiTests.MacName, true);
            var link = setup.Host.Core.SpaceEngine.Network.Get(RoamUiTests.MacName)!;
            for (var i = 0; i < HIB.Core.Roam.LatencyMeter.Samples; i++) link.Latency.Add(31);
            await RoamUiTests.PumpUntil(() => setup.MacCard?.RoamSlowLink == true && setup.MacCard.RoamPointerSpeedText == "1.25×");
            await Pump(400);
            Shot(setup.Window, "windows-roam2-devices");

            // Settings › This PC: game mode and lock together.
            setup.Main.Navigate("general");
            await Pump(400);
            Shot(setup.Window, "windows-roam2-settings");
        }

        // The pills on a 1440×900 desktop: the gate at the top of the right edge, then game mode, locked and the admin app.
        Border Place(HIB.Core.Roam.RoamPill pill, double left, double top, bool right = false)
        {
            var b = HIB.Views.Roam.RoamPillWindow.Capsule(pill);
            b.HorizontalAlignment = right ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left;
            b.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
            b.Margin = new Thickness(left, top, right ? 8 : 0, 0);
            return b;
        }
        Window Desk(params Control[] items)
        {
            var desk = new Bitmap(new MemoryStream(Png(2880, 1800, Desktop)));
            var panel = new Panel { Children = { new Image { Source = desk, Stretch = Stretch.Fill } } };
            foreach (var c in items) panel.Children.Add(c);
            var w = new Window { Width = 1440, Height = 900, Content = panel };
            w.SetRenderScaling(2);
            w.Show();
            return w;
        }
        var mac = RoamUiTests.MacName;
        var gate = Desk(
            new Border { Width = 4, Height = 135, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Background = HIB.Views.Flick.FlickTheme.Solid(HIB.Views.Flick.FlickTheme.Cyan),
                BoxShadow = new BoxShadows(new BoxShadow { Blur = 18, Spread = 1, Color = HIB.Views.Flick.FlickTheme.A(HIB.Views.Flick.FlickTheme.Cyan, 0.95) }) },
            Place(new HIB.Core.Roam.RoamPill(HIB.Core.Roam.RoamPillKind.Gate, HIB.Core.Roam.RoamPill.GateText), 0, 30, right: true));
        await Pump(300);
        SaveShot(Capture(gate), "windows-roam2-gate");
        gate.Close();

        var pills = Desk(
            Place(new HIB.Core.Roam.RoamPill(HIB.Core.Roam.RoamPillKind.GameMode, HIB.Core.Roam.RoamPill.GameModeText(mac)), 0, 380, right: true),
            Place(new HIB.Core.Roam.RoamPill(HIB.Core.Roam.RoamPillKind.Locked, HIB.Core.Roam.RoamPill.LockedText("Office PC")), 600, 640),
            Place(new HIB.Core.Roam.RoamPill(HIB.Core.Roam.RoamPillKind.AdminApp, HIB.Core.Roam.UipiGuard.PillText), 430, 720));
        await Pump(300);
        SaveShot(Capture(pills), "windows-roam2-pills");
        pills.Close();
    }
}
