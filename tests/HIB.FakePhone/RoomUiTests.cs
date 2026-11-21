using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using HIB.Core.Platform;
using HIB.Core.Roam;
using HIB.Services;
using HIB.ViewModels;
using HIB.Views;
using HIB.Views.Room;

namespace HIB.FakePhone;

/// <summary>
/// The Room view (docs/SPACE.md §10.4) in the app: "PC-INTERN" with an Odyssey OLED G9 (5120×1440, 1196×337 mm) and a
/// MacBook Pro 14" on the peer link, a phone at 82 %. Arrange from the Devices tab, drag with snapping, the glowing segment,
/// amber "Not connected to anything", Esc restores, and "Arrange your desk" by itself when a computer joins. Set HIB_DESIGN_DIR
/// to save docs/design/windows-room*.png.
/// </summary>
public class RoomUiTests
{
    static readonly string? ShotDir = Environment.GetEnvironmentVariable("HIB_DESIGN_DIR");
    internal const string PcName = "PC-INTERN";
    internal const string MacName = "Haitham’s MacBook Pro";

    internal const string MacHello = """{"type":"ctlHello","data":{"v":1,"enabled":true,"platform":"mac","screens":[{"id":"37D8832A","x":0,"y":0,"w":1512,"h":982,"name":"Built-in Retina Display","mmW":302,"mmH":196,"builtIn":true,"pxW":3024,"pxH":1964,"scale":2}],"manualSide":null,"cmdAsCtrl":true}}""";
    /// <summary>The Mac's own auto layout (rev 0): the MacBook first, the PC to its right, bottoms on y = 0.</summary>
    internal const string MacRoom = """{"type":"roomLayout","data":{"v":1,"rev":0,"by":"Haitham’s MacBook Pro","groups":[{"computer":"Haitham’s MacBook Pro","x":0,"y":-196},{"computer":"PC-INTERN","x":302,"y":-337}]}}""";

    internal sealed class Desk : IAsyncDisposable
    {
        public required TestHost Host;
        public required MainWindow Window;
        public required AppController Controller;
        public required MainViewModel Main;
        public required RawMac Mac;
        public required FakePhone Phone;

        public async ValueTask DisposeAsync()
        {
            Controller.Room.Close();
            Controller.Roam.Dispose();
            Window.AllowClose = true;
            Window.Close();
            await Mac.DisposeAsync();
            await Phone.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    internal static async Task<Desk> StartAsync(bool autoOpen = false)
    {
        var input = new FakeRoamPlatform();
        var host = new TestHost(name: PcName, roam: new RoamOptions { Platform = () => input });
        host.Core.SpaceEngine.SetSpaceKey(RoamUiTests.SpaceKey);
        var window = new MainWindow { Width = 1100, Height = 760 };
        var controller = new AppController(host.Core, null) { WindowOverride = window };
        controller.Room.AutoOpen = autoOpen;
        var g9 = FakeScreen.Make(@"\\.\DISPLAY1", new PixelRect(0, 0, 5120, 1440), 1, true, new PixelRect(0, 0, 5120, 1392));
        controller.Screens.Override = () => new[] { g9 };
        controller.Screens.DetailsOverride = () => new[] { new MonitorDetails(@"\\.\DISPLAY1", "Odyssey OLED G9", 1196, 337, false) };
        controller.Point.PublishScreens();
        var main = new MainViewModel(controller);
        window.DataContext = main;
        window.Show();
        var phone = new FakePhone(host.Core.BuildQr()) { Name = "Galaxy S26 Ultra" };
        await phone.ConnectAsync();
        await phone.SendDeviceAsync();
        await phone.WaitForAsync("macInfo");
        await phone.SendAsync("desktopsList", new
        {
            you = PcName,
            phone = new { id = "phone", name = "Galaxy S26 Ultra", model = "SM-S948B", battery = 82, charging = false },
            desktops = new object[] { new { id = MacName, name = MacName, platform = "mac", type = "MacBook Pro", connected = true } },
        });
        var mac = await RawMac.DialAsync(host.Core.Link.Port, RoamUiTests.SpaceKey, MacName);
        await mac.SendRawAsync(MacHello);
        await mac.SendRawAsync(MacRoom);
        await RoamUiTests.PumpUntil(() => host.Core.Roam.InRoom(MacName) && main.IsPaired);
        Assert.True(host.Core.Roam.InRoom(MacName));
        return new Desk { Host = host, Window = window, Controller = controller, Main = main, Mac = mac, Phone = phone };
    }

    static void Save(TopLevel w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (ShotDir is null) return;
        Directory.CreateDirectory(ShotDir);
        frame!.Save(Path.Combine(ShotDir, name + ".png"));
    }

    static Point Center(RoomCanvas canvas, string computer) => canvas.ScreenRects.First(r => r.Computer == computer).Rect.Center;

    /// <summary>Drags <paramref name="computer"/>'s group so its offset moves by (dx, dy) mm, as a pointer would.</summary>
    static async Task DragBy(RoomCanvas canvas, string computer, double dx, double dy)
    {
        var s = canvas.CurrentFit.Scale;
        var from = Center(canvas, computer);
        canvas.BeginPress(computer, from);
        for (var i = 1; i <= 4; i++)
        {
            canvas.MoveTo(new Point(from.X + dx * s * i / 4, from.Y + dy * s * i / 4));
            await RoamUiTests.PumpUntil(() => false, 20);
        }
        canvas.EndPress();
        await RoamUiTests.PumpUntil(() => false, 60);
    }

    [AvaloniaFact]
    public async Task Arrange_opens_your_desk_with_true_proportions_and_the_crossing_glows()
    {
        await using var d = await StartAsync();
        d.Main.Navigate("devices");
        await RoamUiTests.PumpUntil(() => d.Main.Devices.CanArrange);
        Assert.True(d.Main.Devices.CanArrange);
        var text = string.Join("\n", d.Window.GetVisualDescendantsText());
        Assert.Contains("Arrange", text);
        var card = d.Main.Devices.Devices.First(c => c.Id == MacName);
        await RoamUiTests.PumpUntil(() => card.HasRoam);
        Assert.True(card.RoamInRoom);
        Assert.Equal("Left of this PC · from your desk", card.RoamSideLabel);

        d.Main.Devices.ArrangeCommand.Execute(null);
        await RoamUiTests.PumpUntil(() => d.Controller.Room.Window is { IsVisible: true });
        var w = d.Controller.Room.Window!;
        w.SetRenderScaling(2);
        await RoamUiTests.PumpUntil(() => w.DeskCanvas.ScreenRects.Count == 2, 1500);
        var canvas = w.DeskCanvas;
        var editor = d.Controller.Room.Editor!;
        Assert.Equal(new[] { PcName, MacName }, editor.Computers.Select(c => c.Id));
        Assert.Contains(MacName, editor.Speaking);
        Assert.Equal("Galaxy S26 Ultra", Assert.Single(editor.Phones).Name);
        Assert.Equal(82, editor.Phones[0].Battery);

        // True proportions: the G9 is a 32:9 bar (1196×337 mm), the MacBook 302×196, both on one desk line.
        var g9 = canvas.ScreenRects.First(r => r.Computer == PcName).Rect;
        var mbp = canvas.ScreenRects.First(r => r.Computer == MacName).Rect;
        Assert.Equal(1196.0 / 337, g9.Width / g9.Height, 2);
        Assert.Equal(302.0 / 196, mbp.Width / mbp.Height, 2);
        Assert.Equal(1196.0 / 302, g9.Width / mbp.Width, 2);
        Assert.Equal(g9.Bottom, mbp.Bottom, 1);
        Assert.True(mbp.Right <= g9.X + 0.5); // the MacBook on the left, touching
        Assert.Equal(1, editor.World.Segments().Count);
        var me = canvas.Labels.Single(l => l.Computer == PcName);
        Assert.True(me.IsMe);
        Assert.Null(me.Note);
        Assert.False(canvas.Labels.Single(l => l.Computer == MacName).IsMe);
        Assert.Equal("Odyssey OLED G9", editor.Computers[0].Screens[0].Name);
        Assert.Equal("5120×1440", RoomCanvas.Resolution(editor.Computers[0].Screens[0]));
        Assert.Equal("3024×1964", RoomCanvas.Resolution(editor.Computers[1].Screens[0]));
        var wtext = string.Join("\n", w.GetVisualDescendantsText());
        Assert.Contains("Your desk", wtext);
        Assert.Contains("Drag your computers to where they really are", wtext);
        Assert.Contains("ALSO IN YOUR SPACE", wtext);
        Assert.Contains("82%", wtext);
        await RoamUiTests.PumpUntil(() => false, 200);
        Save(w, "windows-room");

        // The MacBook goes on top of the G9: dropped near the middle of its top edge it snaps onto it, centred.
        var before = d.Mac.Of("roomLayout").Count;
        var off = editor.OffsetOf(MacName)!.Value;
        await DragBy(canvas, MacName, 302 + 1196 / 2.0 - 151 - 12 - off.X, -337 - 196 - 25 - off.Y);
        var now = editor.OffsetOf(MacName)!.Value;
        Assert.Equal((302 + 598 - 151.0, -337 - 196.0), now);
        Assert.True(await Eventually.TrueAsync(() => d.Mac.Of("roomLayout").Count > before));
        var sent = d.Mac.Of("roomLayout").Last();
        Assert.Equal(PcName, sent.GetProperty("by").GetString());
        Assert.True(sent.GetProperty("rev").GetInt64() > 0);
        Assert.True(await Eventually.TrueAsync(() => d.Host.Core.Roam.StatusFor(MacName)?.Side == RoamSide.Above));
        var seg = Assert.Single(editor.World.Segments());
        Assert.True(seg.Side is RoamSide.Above or RoamSide.Below);
        await RoamUiTests.PumpUntil(() => false, 250);
        Save(w, "windows-room-above");

        // Away from everything: amber "Not connected to anything".
        await DragBy(canvas, MacName, 0, -400);
        await RoamUiTests.PumpUntil(() => canvas.Labels.Any(l => l.Warning), 1000);
        Assert.Equal("Not connected to anything", canvas.Labels.Single(l => l.Computer == MacName).Note);
        Assert.Equal("Not connected to anything", canvas.Labels.Single(l => l.Computer == PcName).Note);
        Assert.Contains("isn't next to this PC", d.Host.Core.Roam.StatusFor(MacName)!.Status);

        // Esc: everything back where it was when the view opened, applied, and the view closes.
        w.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        await RoamUiTests.PumpUntil(() => d.Controller.Room.Window is null);
        Assert.Null(d.Controller.Room.Window);
        Assert.Equal(new RoomGroup(MacName, 0, -196), d.Host.Core.Roam.RoomLayout!.Find(MacName));
        Assert.Equal(new RoomGroup(PcName, 302, -337), d.Host.Core.Roam.RoomLayout!.Find(PcName));
        Assert.True(await Eventually.TrueAsync(() => d.Mac.Of("roomLayout").Last().GetProperty("groups").EnumerateArray()
            .Any(g => g.GetProperty("computer").GetString() == MacName && g.GetProperty("y").GetInt32() == -196)));
    }

    [AvaloniaFact]
    public async Task Arrange_your_desk_opens_by_itself_the_first_time_a_computer_joins()
    {
        await using var d = await StartAsync(autoOpen: true);
        await RoamUiTests.PumpUntil(() => d.Controller.Room.Window is not null);
        var w = d.Controller.Room.Window!;
        Assert.Equal(MacName, d.Controller.Room.Editor!.Joined);
        Assert.Equal($"{MacName} just joined", w.JoinedLabel);
        // The other computer moves something while it's open: the view follows.
        await d.Mac.SendRawAsync("""{"type":"roomLayout","data":{"v":1,"rev":1763712000000,"by":"Haitham’s MacBook Pro","groups":[{"computer":"Haitham’s MacBook Pro","x":1196,"y":-196},{"computer":"PC-INTERN","x":0,"y":-337}]}}""");
        await RoamUiTests.PumpUntil(() => d.Controller.Room.Editor?.OffsetOf(MacName) is { X: 1196 });
        Assert.Equal((1196.0, -196.0), d.Controller.Room.Editor!.OffsetOf(MacName));
        // Done keeps it.
        w.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await RoamUiTests.PumpUntil(() => d.Controller.Room.Window is null);
        Assert.Equal(new RoomGroup(MacName, 1196, -196), d.Host.Core.Roam.RoomLayout!.Find(MacName));
    }
}
