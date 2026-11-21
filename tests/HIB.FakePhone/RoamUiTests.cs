using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Platform;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HIB.Core.Roam;
using HIB.Services;
using HIB.ViewModels;
using HIB.Views;
using HIB.Views.Roam;

namespace HIB.FakePhone;

/// <summary>Roam in the app (SPACE.md §9.6): the Devices row, the edge glow, the ghost card, the learned-side notice and drops.</summary>
public class RoamUiTests
{
    internal static readonly byte[] SpaceKey = Enumerable.Range(1, 32).Select(i => (byte)(i * 13)).ToArray();
    internal const string MacName = "Haitham’s MacBook";

    internal static async Task PumpUntil(Func<bool> cond, int ms = 4000)
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

    internal const string MacHello = """{"type":"ctlHello","data":{"v":1,"enabled":true,"platform":"mac","screens":[{"id":"Built-in","x":0,"y":0,"w":1512,"h":982}],"manualSide":null,"cmdAsCtrl":true}}""";

    /// <summary>"Office PC" (one 1920×1080 monitor) in the real main window, with a Mac on the direct link.</summary>
    internal sealed class Setup : IAsyncDisposable
    {
        public required TestHost Host;
        public required FakeRoamPlatform Input;
        public required MainWindow Window;
        public required AppController Controller;
        public required MainViewModel Main;
        public required RawMac Mac;
        public required Screen Screen;
        public required FakePhone Phone;

        public static async Task<Setup> StartAsync(double renderScale = 1, Func<FakePhone, Task>? beforeMac = null)
        {
            var input = new FakeRoamPlatform();
            var host = new TestHost(name: "Office PC", roam: new RoamOptions { Platform = () => input });
            host.Core.SpaceEngine.SetSpaceKey(SpaceKey);
            var window = new MainWindow { Width = 1100, Height = 760 };
            if (renderScale != 1) window.SetRenderScaling(renderScale);
            var controller = new AppController(host.Core, null) { WindowOverride = window };
            var screen = FakeScreen.Make(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), 1, true, new PixelRect(0, 0, 1920, 1032));
            controller.Screens.Override = () => new[] { screen };
            controller.Point.PublishScreens();
            var main = new MainViewModel(controller);
            window.DataContext = main;
            window.Show();
            // A paired phone that also knows the Mac (the main window shows the pairing view without one).
            var phone = new FakePhone(host.Core.BuildQr()) { Name = "Haitham's S26 Ultra" };
            await phone.ConnectAsync();
            await phone.SendDeviceAsync();
            await phone.WaitForAsync("macInfo");
            if (beforeMac is not null) await beforeMac(phone);
            await phone.SendAsync("desktopsList", new
            {
                you = "Office PC",
                phone = new { id = "phone", name = "Haitham's S26 Ultra", model = "SM-S948B" },
                desktops = new object[] { new { id = MacName, name = MacName, platform = "mac", type = "MacBook Pro", connected = true } },
            });
            var mac = await RawMac.DialAsync(host.Core.Link.Port, SpaceKey, MacName);
            await mac.SendRawAsync(MacHello);
            await PumpUntil(() => host.Core.Roam.StatusFor(MacName) is { Ready: true });
            Assert.True(host.Core.Roam.StatusFor(MacName) is { Ready: true });
            await PumpUntil(() => main.IsPaired);
            return new Setup { Host = host, Input = input, Window = window, Controller = controller, Main = main, Mac = mac, Screen = screen, Phone = phone };
        }

        public DeviceCardViewModel? MacCard => Main.Devices.Devices.FirstOrDefault(d => d.Id == MacName);

        public async ValueTask DisposeAsync()
        {
            Controller.Roam.Dispose();
            Window.AllowClose = true;
            Window.Close();
            await Mac.DisposeAsync();
            await Phone.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task Devices_row_asks_where_the_mac_is_and_shares_the_answer()
    {
        await using var s = await Setup.StartAsync();
        s.Main.Navigate("devices");
        await PumpUntil(() => s.MacCard?.HasRoam == true);
        var card = s.MacCard!;
        Assert.True(card.HasRoam);
        Assert.True(card.RoamEnabled);
        Assert.True(card.RoamIsMac);
        Assert.True(card.RoamAsks);
        Assert.Equal($"Where is {MacName}?", card.RoamSideLabel);
        Assert.True(card.RoamRightSelected); // default right
        Assert.False(card.RoamLeftSelected);
        Assert.True(card.RoamCmdAsCtrl);
        Assert.Equal($"Move the cursor off the right edge to go to {MacName}", card.RoamStatus);
        Assert.Contains("admin", card.RoamInfo);
        await PumpUntil(() => string.Join("\n", s.Window.GetVisualDescendantsText()).Contains("Mouse & keyboard"), 1500);
        var text = string.Join("\n", s.Window.GetVisualDescendantsText());
        Assert.True(text.Contains("Mouse & keyboard"), text);
        Assert.Contains("⌘ works as Ctrl on the PC", text);

        // ← Left: ctlHello carries this PC's view ("the Mac is on my left"); the Mac applies the mirror.
        card.RoamLeftCommand.Execute(null);
        await PumpUntil(() => s.MacCard!.RoamLeftSelected);
        card = s.MacCard!;
        Assert.Equal("Side · set by you", card.RoamSideLabel);
        Assert.False(card.RoamAsks);
        Assert.Equal($"Move the cursor off the left edge to go to {MacName}", card.RoamStatus);
        Assert.True(await Eventually.TrueAsync(() => s.Mac.Of("ctlHello").Any(h => h.GetProperty("manualSide").GetString() == "left")),
            string.Join("\n", s.Mac.Of("ctlHello").Select(h => h.GetRawText())));

        // ⌘ as Ctrl off, then the whole row off: the Mac hears each change.
        card.RoamCmdAsCtrl = false;
        Assert.True(await Eventually.TrueAsync(() => s.Mac.Of("ctlHello").Any(h => !h.GetProperty("cmdAsCtrl").GetBoolean())));
        card.RoamEnabled = false;
        Assert.True(await Eventually.TrueAsync(() => s.Mac.Of("ctlHello").Last().GetProperty("enabled").GetBoolean() == false));
        await PumpUntil(() => !s.MacCard!.RoamEnabled);
        Assert.Equal(RoamInputMode.Off, s.Input.Mode);

        // The other computer turning it off says so.
        card.RoamEnabled = true;
        await s.Mac.SendRawAsync(MacHello.Replace("\"enabled\":true", "\"enabled\":false"));
        await PumpUntil(() => s.MacCard!.RoamStatus == $"Turned off on {MacName}");
        Assert.Equal($"Turned off on {MacName}", s.MacCard!.RoamStatus);
    }

    [AvaloniaFact]
    public async Task Edge_glow_follows_the_cursor_at_the_shared_edge_and_fades_away()
    {
        await using var s = await Setup.StartAsync();
        s.Input.Raise(RoamInput.Pointer(1895, 400, false));
        await PumpUntil(() => s.Controller.Roam.EdgeGlow is not null);
        var glow = s.Controller.Roam.EdgeGlow!;
        Assert.Equal(RoamSide.Right, glow.Side);
        Assert.Equal($"{MacName} →", glow.PillText);
        Assert.Equal(400, glow.Along);
        Assert.Equal(1920, glow.Area.Right);
        Assert.Equal(1080, glow.Area.Height);
        Assert.Equal((int)RoamEdgeWindow.SideDepth, glow.Area.Width);
        s.Input.Raise(RoamInput.Pointer(1900, 700, false));
        await PumpUntil(() => s.Controller.Roam.EdgeGlow?.Along == 700);
        Assert.Same(glow, s.Controller.Roam.EdgeGlow); // the same window moves its pill
        s.Input.Raise(RoamInput.Pointer(1700, 700, false));
        await PumpUntil(() => s.Controller.Roam.EdgeGlow is null);
        Assert.Null(s.Controller.Roam.EdgeGlow);
        await PumpUntil(() => glow.IsClosed, 1000);
        Assert.True(glow.IsClosed);
    }

    [AvaloniaFact]
    public async Task A_learned_side_shows_a_notice_with_undo()
    {
        await using var s = await Setup.StartAsync();
        for (var i = 0; i < 3; i++)
        {
            s.Input.Raise(RoamInput.Pointer(-40, 500, false));
            s.Input.Raise(RoamInput.Pointer(80, 500, false));
        }
        await PumpUntil(() => s.Controller.Roam.Notice is not null);
        var notice = s.Controller.Roam.Notice!;
        Assert.Equal($"{MacName} is on this side now", notice.Text);
        Assert.Equal(RoamSide.Left, s.Host.Core.Roam.StatusFor(MacName)!.Side);
        var undo = notice.GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Content == "Undo");
        undo.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await PumpUntil(() => s.Controller.Roam.Notice is null);
        Assert.Equal(RoamSideSource.Default, s.Host.Core.Roam.StatusFor(MacName)!.SideSource);
    }

    [AvaloniaFact]
    public async Task A_file_dragged_in_shows_a_ghost_card_and_lands_where_it_was_dropped()
    {
        await using var s = await Setup.StartAsync();
        await s.Mac.SendRawAsync("""{"type":"ctlEnter","data":{"x":0,"y":500,"clipboard":null}}""");
        await s.Mac.SendRawAsync("""{"type":"ctlDragBegin","data":{"id":"d1","count":1,"name":"report.pdf"}}""");
        await s.Mac.SendRawAsync("""{"type":"ctlMove","data":{"x":700,"y":420}}""");
        await PumpUntil(() => s.Controller.Roam.Ghost is not null && s.Controller.Roam.Ghost.Position.X > 700);
        var ghost = s.Controller.Roam.Ghost!;
        Assert.Equal("report.pdf", ghost.NameText);
        Assert.Equal(new PixelPoint(706, 430), ghost.Position);

        // The drop: the Mac sends the file's flickStart with "at"; the card appears there and the ghost goes.
        await s.Mac.SendRawAsync("""{"type":"ctlButton","data":{"b":"left","down":false}}""");
        await s.Mac.SendRawAsync("""{"type":"flickStart","data":{"id":"t1","kind":"text","text":"Meet at 7","from":"Haitham’s MacBook","fromPlatform":"mac","at":{"x":700,"y":420}}}""");
        await PumpUntil(() => s.Controller.Cards.Windows.Count == 1 && s.Controller.Roam.Ghost is null);
        Assert.Null(s.Controller.Roam.Ghost);
        var card = Assert.Single(s.Controller.Cards.Windows);
        var centerX = card.Position.X + HIB.Views.Flick.FlickCardView.ContainerWidth / 2;
        var centerY = card.Position.Y + HIB.Views.Flick.FlickCardView.ContainerHeight / 2;
        Assert.InRange(centerX, 690, 710);
        Assert.InRange(centerY, 410, 430);
        Assert.Empty(s.Input.Buttons); // the drop isn't a click here
    }
}
