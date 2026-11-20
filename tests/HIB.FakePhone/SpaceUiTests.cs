using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HIB.Services;
using HIB.Views;
using HIB.Views.Flick;
using HIB.Views.Shelf;

namespace HIB.FakePhone;

/// <summary>
/// The incoming Flick card and the Flick shelf, rendered headlessly (Skia) while a fake phone feeds the core.
/// PNGs go to HIB_SCREENSHOT_DIR if set.
/// </summary>
public class SpaceUiTests
{
    static readonly string? ShotDir = Environment.GetEnvironmentVariable("HIB_SCREENSHOT_DIR");

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

    static async Task PumpUntil(Func<bool> cond, int ms = 4000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until && !cond())
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(8);
        }
        Dispatcher.UIThread.RunJobs();
    }

    static async Task SendFileAsync(FakePhone phone, string id, string name, byte[] bytes, string? from = null)
    {
        await phone.SendAsync("fileTransferInit", new { id, name, size = bytes.Length, mime = "image/png", chunkSize = 65536, flickId = id, relayFrom = from });
        for (var i = 0; i * 65536 < bytes.Length; i++)
        {
            var len = Math.Min(65536, bytes.Length - i * 65536);
            await phone.SendAsync("fileChunk", new { id, index = i, chunk = Convert.ToBase64String(bytes, i * 65536, len) });
            await phone.WaitForAsync("fileChunkAck");
        }
        await phone.SendAsync("fileTransferComplete", new { id, name, size = bytes.Length });
    }

    [AvaloniaFact]
    public async Task Incoming_flick_flies_in_as_the_mac_card_and_lands()
    {
        await using var host = new TestHost(name: "Office PC");
        var main = new MainWindow { Width = 900, Height = 600 };
        var controller = new AppController(host.Core, null) { WindowOverride = main };
        await using var phone = await host.ConnectPhoneAsync();

        var png = File.ReadAllBytes(SpaceTests.MakePng(host.Root, "sunset.png", 900, 600));
        var id = Guid.NewGuid().ToString();
        await phone.SendAsync("flickStart", new { id, kind = "image", name = "sunset.png", mime = "image/png", size = png.Length,
            exitXRatio = 0.42, velocity = 2100.0, angle = -8.0, from = "Haitham’s MacBook", fromPlatform = "mac" });
        await PumpUntil(() => controller.Cards.Windows.Count == 1);
        var window = Assert.Single(controller.Cards.Windows);
        var card = window.View.Card;
        Assert.Equal(FlickTransfer.Waiting, card.Transfer);
        Assert.False(card.Landed);
        Assert.Equal("From Haitham’s MacBook · Waiting…", card.Subtitle);
        Save(window, "flick-card-flying");

        await PumpUntil(() => card.Landed, 5000);
        Assert.True(card.Landed);
        await SendFileAsync(phone, id, "sunset.png", png, "Haitham’s MacBook");
        await PumpUntil(() => card.Transfer == FlickTransfer.Done, 5000);
        Assert.Equal(FlickTransfer.Done, card.Transfer);
        Assert.NotNull(card.FilePath);
        Assert.Equal(png, File.ReadAllBytes(card.FilePath!));
        Assert.StartsWith("From Haitham’s MacBook · ", card.Subtitle);
        await PumpUntil(() => card.Thumbnail is not null, 3000);
        Assert.NotNull(card.Thumbnail); // preview made from the received file
        await Task.Delay(1500); // let the check mark pop and fade
        await PumpUntil(() => false, 200);
        Save(window, "flick-card");

        // No "Receiving…" toast for flicks any more: the card is the UI.
        Assert.Empty(controller.Toasts.OpenToasts);

        // A text flick copies and says so.
        await phone.SendAsync("flickStart", new { id = Guid.NewGuid().ToString(), kind = "text", text = "Meet at 7 at the usual place", exitXRatio = 0.6, velocity = 1800.0, angle = 6.0 });
        await PumpUntil(() => controller.Cards.Cards.Count == 2 && controller.Cards.Cards[1].Landed, 5000);
        var text = controller.Cards.Cards[1];
        Assert.Equal("Copied to clipboard", text.Subtitle);
        Save(controller.Cards.Windows[1], "flick-card-text");

        controller.Cards.Dismiss(card.Id);
        await PumpUntil(() => controller.Cards.Cards.Count == 1, 3000);
        Assert.Single(controller.Cards.Cards);
    }

    [AvaloniaFact]
    public async Task Shelf_opens_on_drag_and_sends_the_drop_to_the_bubble()
    {
        await using var host = new TestHost(name: "Office PC");
        var main = new MainWindow { Width = 900, Height = 600 };
        var controller = new AppController(host.Core, null) { WindowOverride = main };
        await using var phone = await host.ConnectPhoneAsync();
        await phone.SendAsync("status", new { battery = new { level = 80, isCharging = false } });
        await phone.SendAsync("desktopsList", new Dictionary<string, object?>
        {
            ["you"] = "Office PC",
            ["phone"] = new { id = "phone", name = "Haitham's S26", model = "SM-S948B", battery = 80 },
            ["inUse"] = "Haitham’s MacBook",
            ["desktops"] = new object[]
            {
                new { id = "Haitham’s MacBook", name = "Haitham’s MacBook", platform = "mac", type = "MacBook Pro", connected = true, inUse = true, idleSeconds = 2 },
                new { id = "Studio PC", name = "Studio PC", platform = "windows", type = "Windows PC", connected = false },
            },
        });
        await PumpUntil(() => host.Core.Space.Devices.Count == 3);

        var sent = new List<(string id, IReadOnlyList<string> paths)>();
        var shelf = new FlickShelfWindow(controller, (id, paths) => { sent.Add((id, paths)); return Task.CompletedTask; });
        shelf.Start();
        await PumpUntil(() => shelf.IsVisible);
        Assert.True(shelf.IsVisible);
        Assert.False(shelf.IsExpanded);
        Assert.Equal(FlickShelfWindow.PillWidth, shelf.Width);
        Save(shelf, "shelf-pill");

        var file = Path.Combine(host.Root, "report.pdf");
        File.WriteAllText(file, "pdf");
        var item = await shelf.StorageProvider.TryGetFileFromPathAsync(file);
        Assert.NotNull(item);
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateFile(item!));
        shelf.DragDrop(new Point(60, 5), RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
        await PumpUntil(() => shelf.IsExpanded, 1000);
        Assert.True(shelf.IsExpanded);
        Assert.True(shelf.Width > 300);
        await PumpUntil(() => false, 150);
        Save(shelf, "shelf-open");

        // Hover the phone bubble (first), then drop.
        var root = (Panel)shelf.Content!;
        var bubbles = root.GetVisualDescendants().OfType<StackPanel>().Where(s => s.Width == 112).ToList();
        Assert.Equal(3, bubbles.Count);
        var phoneBubble = bubbles[0].TranslatePoint(new Point(56, 30), root)!.Value;
        shelf.DragDrop(phoneBubble, RawDragEventType.DragOver, data, DragDropEffects.Copy, RawInputModifiers.None);
        await PumpUntil(() => false, 100);
        Save(shelf, "shelf-hover");
        shelf.DragDrop(phoneBubble, RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None);
        await PumpUntil(() => sent.Count == 1, 1000);
        var (target, paths) = Assert.Single(sent);
        Assert.Equal("phone", target);
        Assert.Equal(file, Assert.Single(paths));

        // Dropping between bubbles goes to the default target (the computer in use).
        shelf.DragDrop(new Point(60, 5), RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
        await PumpUntil(() => shelf.IsExpanded, 1000);
        shelf.DragDrop(new Point(shelf.Width / 2, 12), RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None);
        await PumpUntil(() => sent.Count == 2, 1000);
        Assert.Equal("Haitham’s MacBook", sent[1].id);

        // Turned off in Settings → hidden.
        host.Core.Settings.ShowFlickShelf = false;
        await PumpUntil(() => !shelf.IsVisible, 2500);
        Assert.False(shelf.IsVisible);
        shelf.Close();
    }

    [Fact]
    public void Send_to_arguments_and_shortcut_names()
    {
        var req = SendToRequest.Parse(new[] { "--send-to", "Haitham’s MacBook", "a.txt", "/tmp/b c.pdf" });
        Assert.NotNull(req);
        Assert.Equal("Haitham’s MacBook", req!.DeviceId);
        Assert.Equal(2, req.Files.Count);
        Assert.True(Path.IsPathFullyQualified(req.Files[0]));
        Assert.Equal(req, SendToRequest.FromJson(req.ToJson()) is { } back && back.DeviceId == req.DeviceId && back.Files.SequenceEqual(req.Files) ? req : null);
        Assert.Null(SendToRequest.Parse(new[] { "--minimized" }));
        Assert.Equal("H.I.B – Haitham’s MacBook.lnk", SendToShortcuts.FileNameFor("Haitham’s MacBook"));
        Assert.Equal("H.I.B – Office_PC_.lnk", SendToShortcuts.FileNameFor("Office/PC?"));
        Assert.Equal("--send-to \"Office PC\"", SendToShortcuts.ArgumentsFor("Office PC"));
    }

    [Fact]
    public async Task Send_to_pipe_hands_requests_to_the_running_app()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var got = new TaskCompletionSource<SendToRequest>();
        var server = SendToPipe.ServeAsync(r => got.TrySetResult(r), cts.Token);
        var req = new SendToRequest("phone", new[] { "/tmp/x.jpg" });
        var ok = false;
        for (var i = 0; i < 20 && !ok; i++)
        {
            ok = SendToPipe.TryForward(req, 500);
            if (!ok) await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.True(ok);
        var r = await got.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("phone", r.DeviceId);
        Assert.Equal("/tmp/x.jpg", Assert.Single(r.Files));
        cts.Cancel();
        await server;
    }

    [Fact]
    public void Flick_body_rises_from_the_bottom_edge_like_the_mac()
    {
        var b = FlickBody.Launch(1600, 1000, FlickCardView.CardHeight, 0.42, 2100, -8, 760, 700, 0);
        Assert.True(b.Y > 1000); // starts below the bottom edge
        Assert.True(b.Vy < 0); // moving up into the screen
        var minY = b.Y;
        var t = 0.0;
        while (b.Advance(1.0 / 60) && t < 3) { t += 1.0 / 60; minY = Math.Min(minY, b.Y); }
        Assert.Equal(FlickBodyMode.Landed, b.Mode);
        Assert.Equal(760, b.X, 3);
        Assert.Equal(700, b.Y, 3);
        Assert.True(minY < 700 - 20, "springs past the landing spot a little");
        Assert.InRange(t, 0.3, 2.0); // settles in about a second
    }
}
