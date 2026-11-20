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
}
