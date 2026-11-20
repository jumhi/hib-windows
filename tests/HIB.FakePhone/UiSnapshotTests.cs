using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HIB.Services;
using HIB.ViewModels;
using HIB.Views;

[assembly: AvaloniaTestApplication(typeof(HIB.FakePhone.TestAppBuilder))]

namespace HIB.FakePhone;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Renders the real windows/pages headlessly (Skia) while a fake phone feeds the core, so XAML,
/// bindings and the UI glue are exercised end to end. PNGs go to HIB_SCREENSHOT_DIR if set.
/// </summary>
public class UiSnapshotTests
{
    static readonly string? ShotDir = Environment.GetEnvironmentVariable("HIB_SCREENSHOT_DIR");

    static void Save(Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (ShotDir is null) return;
        Directory.CreateDirectory(ShotDir);
        frame!.Save(Path.Combine(ShotDir, name + ".png"));
    }

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

    [AvaloniaFact]
    public async Task Pages_and_toasts_render_with_live_phone_data()
    {
        await using var host = new TestHost();
        var window = new MainWindow { Width = 1080, Height = 720 };
        var controller = new AppController(host.Core, null) { WindowOverride = window };
        var main = new MainViewModel(controller);
        window.DataContext = main;
        window.Show();
        Save(window, "01-pair");

        await using var phone = new FakePhone(host.Core.BuildQr()) { Name = "Pixel 9 Pro" };
        await phone.ConnectAsync();
        await phone.SendDeviceAsync();
        await phone.WaitForAsync("macInfo");
        await phone.SendAsync("status", new
        {
            battery = new { level = 76, isCharging = true },
            isPaired = true,
            music = new { isPlaying = true, title = "Blinding Lights", artist = "The Weeknd", volume = 60, isMuted = false, duration = 200000, position = 64000, positionTimestamp = 0, isBuffering = false, likeStatus = "none" },
        });
        await Pump();
        main.Navigate("home");
        await Pump();
        Assert.True(main.Home.IsConnected);
        Assert.Equal("Pixel 9 Pro", main.Home.DeviceName);
        Assert.Equal(76, main.Home.Battery);
        Assert.True(main.Home.HasMedia);
        Save(window, "02-home");

        await phone.SendAsync("notification", new { id = "n1", title = "Ali", body = "Are we still on for 7?", app = "WhatsApp", package = "com.whatsapp", priority = "alerting",
            actions = new[] { new { name = "Reply", type = "reply" }, new { name = "Mark as read", type = "button" } } });
        await phone.SendAsync("notification", new { id = "n2", title = "Your boarding pass", body = "Flight SV 1020 · Gate B12", app = "Gmail", package = "com.google.android.gm", priority = "alerting" });
        await Pump();
        Assert.Equal(2, main.Notifications.Items.Count);
        Assert.Equal("2", main.NavItems.First(n => n.Key == "notifications").Badge);
        main.Navigate("notifications");
        await Pump();
        Assert.Null(main.NavItems.First(n => n.Key == "notifications").Badge);
        Save(window, "03-notifications");

        // Reply from the list goes to the phone.
        var item = main.Notifications.Items.First(i => i.Id == "n1");
        item.ReplyText = "on my way";
        await item.SendReplyCommand.ExecuteAsync(null);
        var reply = await phone.WaitForAsync("notificationAction");
        Assert.Equal("on my way", reply.Data.GetProperty("text").GetString());

        await phone.SendAsync("clipboardUpdate", new { text = "https://hib.example/invite/7Q2X" });
        await Pump();
        main.Navigate("clipboard");
        await Pump();
        Save(window, "04-clipboard");

        main.Navigate("files");
        await Pump();
        Save(window, "05-files");
        main.Navigate("settings");
        await Pump();
        Save(window, "06-settings");
        main.Navigate("about");
        await Pump();
        Save(window, "07-about");

        // Call popup (no normalizedNumber) with Answer → callControl accept.
        await phone.SendAsync("call_event", new { eventId = "e1", direction = "incoming", state = "ringing", number = "+966 50 123 4567", contactName = "Mom" });
        await Pump(500);
        var toast = controller.Toasts.OpenToasts.OfType<ToastWindow>().FirstOrDefault(t => t.DataContext is ToastViewModel { IsCall: true });
        Assert.NotNull(toast);
        var vm = (ToastViewModel)toast!.DataContext!;
        Assert.True(vm.IsCall);
        Save(toast, "08-call-toast");
        vm.Buttons.First(b => b.Label == "Answer").Command.Execute(null);
        Assert.Equal("accept", (await phone.WaitForAsync("callControl")).Data.GetProperty("action").GetString());

        // Ring from the home quick action.
        await main.Home.RingCommand.ExecuteAsync(null);
        Assert.Equal("start", (await phone.WaitForAsync("ringPhone")).Data.GetProperty("action").GetString());

        window.AllowClose = true;
        window.Close();
    }
}
