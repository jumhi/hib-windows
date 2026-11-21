using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Avalonia.Threading;
using HIB.Core.Phone;
using HIB.Services;
using HIB.Views;
using HIB.Views.Flick;

namespace HIB.FakePhone;

/// <summary>
/// Stand-in monitors. Avalonia's Screen can't be subclassed outside Avalonia, so these are the headless
/// platform's own screen objects with their (protected) properties set by reflection.
/// </summary>
static class FakeScreen
{
    static readonly Type Stub = typeof(AvaloniaHeadlessPlatform).Assembly.GetTypes().First(t => t.Name == "PlatformScreenStub");
    static int _next = 1000;

    public static Screen Make(string? name, PixelRect bounds, double scaling = 1, bool primary = false, PixelRect? work = null)
    {
        var s = (Screen)Activator.CreateInstance(Stub, Interlocked.Increment(ref _next))!;
        Set(s, nameof(Screen.DisplayName), name);
        Set(s, nameof(Screen.Bounds), bounds);
        Set(s, nameof(Screen.WorkingArea), work ?? bounds);
        Set(s, nameof(Screen.Scaling), scaling);
        Set(s, nameof(Screen.IsPrimary), primary);
        return s;
    }

    static void Set(Screen s, string property, object? value) => typeof(Screen).GetProperty(property)!.SetValue(s, value);
}

/// <summary>Point &amp; Flick (SPACE.md §7): screensInfo, flickAim / aimSetup and flickStart.screen.</summary>
public class PointFlickTests
{
    static readonly DisplayScreen Main = new(@"\\.\DISPLAY1", "Display 1", true, 0, 0, 1920, 1080);
    static readonly DisplayScreen Side = new(@"\\.\DISPLAY2", "Display 2", false, 1920, 0, 2048, 1152);

    static Screen ScreenA() => FakeScreen.Make(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), 1, true, new PixelRect(0, 0, 1920, 1032));
    static Screen ScreenB() => FakeScreen.Make(@"\\.\DISPLAY2", new PixelRect(1920, 0, 2560, 1440), 1.25);

    static async Task PumpUntil(Func<bool> cond, int ms = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until && !cond())
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    static List<JsonElement> ScreensOf(DesktopMessage m) => m.Data.GetProperty("screens").EnumerateArray().ToList();

    // ---------------- screensInfo (§7.2, §7.3) ----------------

    [Fact]
    public async Task Screens_info_follows_macInfo_on_connect_and_answers_requests()
    {
        await using var host = new TestHost();
        host.Core.Aim.SetScreens(new[] { Main, Side });
        await using var phone = await host.ConnectPhoneAsync();
        var info = await phone.WaitForAsync("screensInfo");

        // Right after macInfo, on the same connection.
        var history = phone.History.Where(m => m.Type != "ping").ToList();
        var mac = history.FindIndex(m => m.Type == "macInfo");
        Assert.True(mac >= 0);
        Assert.Equal("screensInfo", history[mac + 1].Type);

        var screens = ScreensOf(info);
        Assert.Equal(2, screens.Count);
        Assert.Equal(@"\\.\DISPLAY1", screens[0].GetProperty("id").GetString());
        Assert.Equal("Display 1", screens[0].GetProperty("name").GetString());
        Assert.True(screens[0].GetProperty("primary").GetBoolean());
        Assert.False(screens[1].GetProperty("primary").GetBoolean());
        Assert.Equal(1920, screens[1].GetProperty("x").GetDouble());
        Assert.Equal(0, screens[1].GetProperty("y").GetDouble());
        Assert.Equal(2048, screens[1].GetProperty("w").GetDouble());
        Assert.Equal(1152, screens[1].GetProperty("h").GetDouble());

        // screensRequest → screensInfo.
        await phone.SendAsync("screensRequest", new { });
        Assert.Equal(2, ScreensOf(await phone.WaitForAsync("screensInfo")).Count);

        // Displays change → screensInfo; the same list again → nothing.
        var third = new DisplayScreen("2560,-1440,2560x1440", "Display 3", false, 0, -1152, 2048, 1152);
        host.Core.Aim.SetScreens(new[] { Main, Side, third });
        Assert.Equal(3, ScreensOf(await phone.WaitForAsync("screensInfo")).Count);
        host.Core.Aim.SetScreens(new[] { Main, Side, third });
        Assert.False(await phone.ReceivesWithinAsync("screensInfo", TimeSpan.FromMilliseconds(400)));

        // Every new phone connection gets it again after macInfo.
        await phone.DisposeAsync();
        await using var again = await host.ConnectPhoneAsync();
        Assert.Equal(3, ScreensOf(await again.WaitForAsync("screensInfo")).Count);
    }

    [Fact]
    public async Task Screens_info_is_sent_once_the_app_knows_its_screens()
    {
        await using var host = new TestHost();
        await using var phone = await host.ConnectPhoneAsync();
        Assert.False(await phone.ReceivesWithinAsync("screensInfo", TimeSpan.FromMilliseconds(300)));
        host.Core.Aim.SetScreens(new[] { Main });
        var screens = ScreensOf(await phone.WaitForAsync("screensInfo"));
        Assert.Equal(@"\\.\DISPLAY1", Assert.Single(screens).GetProperty("id").GetString());
    }

    // ---------------- the aim light and point target logic (§7.4, §7.5) ----------------

    [Fact]
    public void Aim_light_moves_hides_on_null_and_by_itself_after_4_seconds()
    {
        var t = new AimTracker();
        t.SetScreens(new[] { Main, Side });
        var changes = new List<DisplayScreen?>();
        t.LitChanged += changes.Add;
        var t0 = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

        t.ApplyAim(Side.Id, true, t0);
        Assert.Equal(Side, t.Lit);
        t.ApplyAim(Side.Id, true, t0.AddSeconds(1.5)); // the phone's 1.5 s re-send: no flicker
        Assert.Single(changes);
        t.Tick(t0.AddSeconds(5.4));
        Assert.Equal(Side, t.Lit); // 3.9 s since the last one
        t.Tick(t0.AddSeconds(5.5));
        Assert.Null(t.Lit);        // 4 s since the last one
        Assert.Equal(2, changes.Count);

        // Unknown id → the main screen; another screen moves the light; null hides at once.
        t.ApplyAim("gone", true, t0.AddSeconds(10));
        Assert.Equal(Main, t.Lit);
        t.ApplyAim(Side.Id, true, t0.AddSeconds(11));
        Assert.Equal(Side, t.Lit);
        t.ApplyAim(null, false, t0.AddSeconds(12));
        Assert.Null(t.Lit);

        // A lit screen that is unplugged goes dark.
        t.ApplyAim(Side.Id, true, t0.AddSeconds(13));
        t.SetScreens(new[] { Main });
        Assert.Null(t.Lit);
    }

    [Fact]
    public void Point_target_updates_completes_and_hides()
    {
        var t = new AimTracker();
        t.SetScreens(new[] { Main, Side });
        var t0 = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

        t.ApplySetup(Main.Id, true, 1, 2, 0, t0);
        Assert.Equal(new AimSetupState(Main, 1, 2, 0), t.Setup);
        t.ApplySetup(Main.Id, true, 1, 2, 0.4, t0.AddSeconds(1));
        Assert.Equal(0.4, t.Setup!.Progress);
        Assert.False(t.Setup.Complete);
        t.Tick(t0.AddSeconds(6.9));
        Assert.NotNull(t.Setup);   // 5.9 s since the last update
        t.Tick(t0.AddSeconds(7));
        Assert.Null(t.Setup);      // 6 s

        // progress 1 → lime + check, then it fades by itself; re-sends don't keep it up.
        t.ApplySetup(Side.Id, true, 2, 2, 1, t0.AddSeconds(20));
        Assert.True(t.Setup!.Complete);
        Assert.Equal(Side, t.Setup.Screen);
        t.ApplySetup(Side.Id, true, 2, 2, 1, t0.AddSeconds(20.5));
        t.Tick(t0.AddSeconds(20.7));
        Assert.NotNull(t.Setup);
        t.Tick(t0.AddSeconds(20) + AimTracker.CompleteHold);
        Assert.Null(t.Setup);

        // Next screen, then screen:null hides at once; out-of-range values are clamped.
        t.ApplySetup(Main.Id, true, 9, 3, 1.7, t0.AddSeconds(30));
        Assert.Equal(new AimSetupState(Main, 3, 3, 1), t.Setup);
        t.ApplySetup(Main.Id, true, 1, 3, 0.2, t0.AddSeconds(30.2));
        Assert.False(t.Setup!.Complete);
        t.ApplySetup(null, false, 0, 0, 0, t0.AddSeconds(31));
        Assert.Null(t.Setup);

        // The phone going away clears both.
        t.ApplySetup(Main.Id, true, 1, 1, 0, t0.AddSeconds(40));
        t.ApplyAim(Side.Id, true, t0.AddSeconds(40));
        t.Clear();
        Assert.Null(t.Setup);
        Assert.Null(t.Lit);
    }

    [Fact]
    public async Task Flick_aim_and_aim_setup_over_the_link()
    {
        await using var host = new TestHost();
        host.Core.Aim.SetScreens(new[] { Main, Side });
        await using var phone = await host.ConnectPhoneAsync();
        var aim = host.Core.Aim;

        await phone.SendAsync("flickAim", new Dictionary<string, object?> { ["screen"] = Side.Id });
        Assert.True(await Eventually.TrueAsync(() => aim.Lit == Side));
        await phone.SendAsync("flickAim", new Dictionary<string, object?> { ["screen"] = null });
        Assert.True(await Eventually.TrueAsync(() => aim.Lit is null));

        await phone.SendAsync("aimSetup", new { screen = Main.Id, step = 1, total = 2, progress = 0.4 });
        Assert.True(await Eventually.TrueAsync(() => aim.Setup is { Step: 1, Total: 2, Progress: 0.4 } s && s.Screen == Main));
        // Complete: the core's own timer hides it after the lime hold.
        await phone.SendAsync("aimSetup", new { screen = Main.Id, step = 1, total = 2, progress = 1.0 });
        Assert.True(await Eventually.TrueAsync(() => aim.Setup is { Complete: true }));
        Assert.True(await Eventually.TrueAsync(() => aim.Setup is null, TimeSpan.FromSeconds(3)));

        await phone.SendAsync("aimSetup", new { screen = Side.Id, step = 2, total = 2, progress = 0.1 });
        Assert.True(await Eventually.TrueAsync(() => aim.Setup?.Screen == Side));
        await phone.SendAsync("aimSetup", new Dictionary<string, object?> { ["screen"] = null });
        Assert.True(await Eventually.TrueAsync(() => aim.Setup is null));

        // The phone disconnecting turns everything off.
        await phone.SendAsync("flickAim", new { screen = Main.Id });
        await phone.SendAsync("aimSetup", new { screen = Side.Id, step = 2, total = 2, progress = 0.5 });
        Assert.True(await Eventually.TrueAsync(() => aim.Lit is not null && aim.Setup is not null));
        await phone.DisposeAsync();
        Assert.True(await Eventually.TrueAsync(() => aim.Lit is null && aim.Setup is null));
    }

    [Fact]
    public void Screen_ids_are_the_display_name_else_the_bounds()
    {
        var screens = new Screen[]
        {
            FakeScreen.Make(@"\\.\DISPLAY1", new PixelRect(0, 0, 3024, 1964), 2, true),
            FakeScreen.Make("", new PixelRect(3024, 0, 1920, 1080)),
            FakeScreen.Make("DELL U2720Q", new PixelRect(-3840, 0, 3840, 2160), 1.5),
            FakeScreen.Make("DELL U2720Q", new PixelRect(4944, 0, 3840, 2160), 1.5), // identical twin
        };
        var d = ScreenMap.Describe(screens).Select(x => x.Info).ToList();
        Assert.Equal(new[] { @"\\.\DISPLAY1", "3024,0,1920x1080", "DELL U2720Q", "4944,0,3840x2160" }, d.Select(s => s.Id));
        Assert.Equal(new[] { "Display 1", "Display 2", "DELL U2720Q", "DELL U2720Q" }, d.Select(s => s.Name));
        Assert.Equal(new DisplayScreen(@"\\.\DISPLAY1", "Display 1", true, 0, 0, 1512, 982) { PxW = 3024, PxH = 1964, Scale = 2 }, d[0]); // DIPs
        Assert.Equal(-2560, d[2].X);
        Assert.Equal(2560, d[2].W);
        Assert.Equal(1440, d[2].H);
    }

    // ---------------- on screen (headless) ----------------

    [AvaloniaFact]
    public async Task Aim_light_and_point_target_show_on_the_named_screen()
    {
        await using var host = new TestHost(name: "Office PC");
        var main = new MainWindow { Width = 900, Height = 600 };
        var controller = new AppController(host.Core, null) { WindowOverride = main };
        Screen a = ScreenA(), b = ScreenB();
        controller.Screens.Override = () => new Screen[] { a, b };
        controller.Point.PublishScreens();
        Assert.Equal(new[] { @"\\.\DISPLAY1", @"\\.\DISPLAY2" }, host.Core.Aim.Screens.Select(s => s.Id));

        await using var phone = await host.ConnectPhoneAsync();
        var info = ScreensOf(await phone.WaitForAsync("screensInfo"));
        Assert.Equal(2, info.Count);
        Assert.Equal(2048, info[1].GetProperty("w").GetDouble()); // 2560 px at 125%

        // flickAim → the aim light on that screen, along its bottom edge.
        await phone.SendAsync("flickAim", new { screen = @"\\.\DISPLAY2" });
        await PumpUntil(() => controller.Point.AimLight is not null);
        var light = controller.Point.AimLight;
        Assert.NotNull(light);
        Assert.Equal(@"\\.\DISPLAY2", light!.ScreenId);
        Assert.True(light.IsVisible);
        Assert.Equal("Flick lands here", light.LabelText);
        Assert.Equal(b.Bounds.X, light.Area.X);
        Assert.Equal(b.Bounds.Width, light.Area.Width);
        Assert.Equal(b.Bounds.Bottom, light.Area.Bottom);
        Assert.InRange(light.Area.Height, 1440 * 0.13, 1440 * 0.15);
        Assert.Equal(light.Area.TopLeft, light.Position);

        // Another screen moves it; null fades it out and closes the window.
        await phone.SendAsync("flickAim", new { screen = "not a screen here" });
        await PumpUntil(() => controller.Point.AimLight?.ScreenId == @"\\.\DISPLAY1");
        Assert.Equal(@"\\.\DISPLAY1", controller.Point.AimLight?.ScreenId); // unknown → the main screen
        await PumpUntil(() => light.IsClosed, 1000);
        Assert.True(light.IsClosed);
        var onMain = controller.Point.AimLight!;
        Assert.True(onMain.Area.Bottom == a.Bounds.Bottom);
        await phone.SendAsync("flickAim", new Dictionary<string, object?> { ["screen"] = null });
        await PumpUntil(() => onMain.IsClosed, 1500);
        Assert.Null(controller.Point.AimLight);
        Assert.True(onMain.IsClosed);

        // aimSetup → the point target, full screen, filling with progress.
        await phone.SendAsync("aimSetup", new { screen = @"\\.\DISPLAY2", step = 2, total = 2, progress = 0.5 });
        await PumpUntil(() => controller.Point.PointTarget is not null);
        var target = controller.Point.PointTarget!;
        Assert.Equal(@"\\.\DISPLAY2", target.ScreenId);
        Assert.Equal(b.Bounds, target.Area);
        Assert.Equal("Point your phone here", target.TitleText);
        Assert.Equal("Screen 2 of 2", target.StepText);
        Assert.Equal(0.5, target.Ring.Progress);
        await PumpUntil(() => target.Ring.ShownProgress > 0.45, 1500);
        Assert.InRange(target.Ring.ShownProgress, 0.45, 0.5);

        // Complete → lime + check, then it fades away by itself.
        await phone.SendAsync("aimSetup", new { screen = @"\\.\DISPLAY2", step = 2, total = 2, progress = 1.0 });
        await PumpUntil(() => target.Ring.Complete);
        Assert.True(target.Ring.Complete);
        Assert.Same(target, controller.Point.PointTarget);
        await PumpUntil(() => target.IsClosed, 2500);
        Assert.True(target.IsClosed);
        Assert.Null(controller.Point.PointTarget);

        // screen:null hides at once.
        await phone.SendAsync("aimSetup", new { screen = @"\\.\DISPLAY1", step = 1, total = 2, progress = 0.0 });
        await PumpUntil(() => controller.Point.PointTarget is not null);
        var first = controller.Point.PointTarget!;
        Assert.Equal(a.Bounds, first.Area);
        await phone.SendAsync("aimSetup", new Dictionary<string, object?> { ["screen"] = null });
        await PumpUntil(() => first.IsClosed, 1500);
        Assert.True(first.IsClosed);
        main.Close();
    }

    [AvaloniaFact]
    public async Task Flick_naming_a_screen_lands_there()
    {
        await using var host = new TestHost(name: "Office PC");
        var main = new MainWindow { Width = 900, Height = 600 };
        var controller = new AppController(host.Core, null) { WindowOverride = main };
        Screen a = ScreenA(), b = ScreenB();
        controller.Screens.Override = () => new Screen[] { a, b };
        await using var phone = await host.ConnectPhoneAsync();

        // A left swipe would pick nothing (no screen left of the main one): the named screen wins.
        await phone.SendAsync("flickStart", new { id = "p1", kind = "text", text = "On the side screen", angle = -30.0, heading = 120.0, elevation = 5.0, screen = @"\\.\DISPLAY2" });
        await PumpUntil(() => controller.Cards.Windows.Count == 1);
        Assert.Same(b, controller.Flick.LandingScreen("p1"));
        var card = Assert.Single(controller.Cards.Windows);
        Assert.InRange(card.Position.X, b.Bounds.X - 200, b.Bounds.Right);

        // A screen this computer doesn't have → its own rule (the pointer's / main screen here).
        await phone.SendAsync("flickStart", new { id = "p2", kind = "text", text = "Somewhere", angle = 0.0, screen = "the Mac's screen" });
        await PumpUntil(() => controller.Cards.Windows.Count == 2);
        Assert.NotNull(controller.Flick.LandingScreen("p2"));
        Assert.NotSame(b, controller.Flick.LandingScreen("p2"));

        // flickStart.screen is parsed for core listeners too.
        FlickInfo? seen = null;
        host.Core.Hub.FlickStarted += f => seen = f;
        await phone.SendAsync("flickStart", new { id = "p3", kind = "link", text = "https://example.com", screen = @"\\.\DISPLAY1" });
        Assert.True(await Eventually.TrueAsync(() => seen is not null));
        Assert.Equal(@"\\.\DISPLAY1", seen!.Screen);
        main.Close();
    }
}
