using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using HIB.Services;
using HIB.Views;
using HIB.Views.Flick;

namespace HIB.FakePhone;

/// <summary>
/// A bundle thrown by the (fake) phone lands as one stack, fans out on click into its pattern, gathers again with
/// the halo, Gather or Esc, and a drag of the stack carries every file (SPACE.md §14). Rendered headlessly.
/// </summary>
public class FlickBundleUiTests
{
    static async Task PumpUntil(Func<bool> cond, int ms = 5000)
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

    static async Task SendFileAsync(FakePhone phone, string flickId, string name, byte[] bytes)
    {
        var id = Guid.NewGuid().ToString();
        await phone.SendAsync("fileTransferInit", new { id, name, size = bytes.Length, mime = "image/png", chunkSize = 65536, flickId });
        for (var i = 0; i * 65536 < bytes.Length; i++)
        {
            var len = Math.Min(65536, bytes.Length - i * 65536);
            await phone.SendAsync("fileChunk", new { id, index = i, chunk = Convert.ToBase64String(bytes, i * 65536, len) });
            await phone.WaitForAsync("fileChunkAck");
        }
        await phone.SendAsync("fileTransferComplete", new { id, name, size = bytes.Length });
    }

    static void Click(TopLevel w, Point p)
    {
        w.MouseMove(p);
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task A_bundle_lands_as_one_stack_fans_out_and_gathers_again()
    {
        await using var host = new TestHost(name: "Office PC");
        var main = new MainWindow { Width = 900, Height = 600 };
        var controller = new AppController(host.Core, null) { WindowOverride = main };
        await using var phone = await host.ConnectPhoneAsync();
        var cards = controller.Cards;

        // The phone throws five photos at once.
        var bundleId = Guid.NewGuid().ToString();
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid().ToString()).ToArray();
        var pngs = Enumerable.Range(0, 5).Select(i => File.ReadAllBytes(SpaceTests.MakePng(host.Root, $"IMG_{i}.png", 300 + i * 10, 200))).ToArray();
        for (var i = 0; i < 5; i++)
            await phone.SendAsync("flickStart", new
            {
                id = ids[i], kind = "image", name = $"IMG_{i}.png", mime = "image/png", size = pngs[i].Length,
                exitXRatio = 0.5, velocity = 2000.0, angle = 0.0, from = "Haitham's S26",
                bundle = new { id = bundleId, index = i, count = 5 },
            });
        await PumpUntil(() => cards.Stacks.Count == 1 && cards.Bundles.Get(bundleId)?.Announced == 5);
        var stack = Assert.Single(cards.Stacks);
        Assert.Empty(cards.Windows); // no single cards: one stack
        var bundle = cards.Bundles.Get(bundleId)!;
        Assert.Equal("5", stack.View.BadgeText);
        Assert.Equal("5 photos", stack.View.FaceCard.Name);
        Assert.Equal(3, stack.View.PeekCount);
        Assert.StartsWith("Receiving 0 of 5", stack.View.FaceCard.Subtitle);
        await PumpUntil(() => bundle.Landed);
        Assert.True(bundle.Landed);

        // The files arrive one after another; the stack shows the whole bundle's progress.
        for (var i = 0; i < 5; i++)
        {
            await SendFileAsync(phone, ids[i], $"IMG_{i}.png", pngs[i]);
            if (i == 1)
            {
                await PumpUntil(() => bundle.Arrived == 2);
                Assert.StartsWith("Receiving 2 of 5", stack.View.FaceCard.Subtitle);
            }
        }
        await PumpUntil(() => bundle.AllArrived);
        Assert.True(bundle.AllArrived);
        Assert.StartsWith("From Haitham's S26 · ", stack.View.FaceCard.Subtitle);
        Assert.Equal(FlickTransfer.Done, stack.View.FaceCard.Transfer);

        // Dragging the stack would carry all five files, in throw order.
        var drag = cards.StackDragPaths(bundleId);
        Assert.Equal(Enumerable.Range(0, 5).Select(i => $"IMG_{i}.png"), drag.Select(Path.GetFileName));
        Assert.All(drag, p => Assert.True(File.Exists(p)));
        for (var i = 0; i < 5; i++) Assert.Equal(pngs[i], File.ReadAllBytes(drag[i]));

        // Click the stack: it fans out into the dice-five, centred where it landed.
        var center = cards.StackCenter(bundleId)!.Value;
        Click(stack, new Point(stack.Width / 2, stack.Height / 2));
        Assert.NotNull(cards.FanWindow(bundleId));
        Assert.False(stack.IsVisible);
        await PumpUntil(() => cards.IsFanSettled(bundleId));
        Assert.True(cards.IsFanSettled(bundleId));
        var placement = cards.FanPlacementOf(bundleId)!;
        Assert.Equal(new[] { 2, 1, 2 }, placement.Slots.GroupBy(s => s.Uy).OrderBy(g => g.Key).Select(g => g.Count()).ToArray());
        var now = cards.FanCardCenters(bundleId);
        Assert.Equal(5, now.Count);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(placement.Centers[i].X, now[i].X, 1);
            Assert.Equal(placement.Centers[i].Y, now[i].Y, 1);
        }
        Assert.Equal(center.X, placement.Centers[2].X, 1); // the middle pip sits where the stack was
        var fan = cards.FanWindow(bundleId)!;
        Assert.Equal(5, fan.View.CardViews.Count);

        // A click on the empty space gathers them back into the stack (on press: the fan may be gone by the release).
        fan.MouseMove(new Point(6, 6));
        fan.MouseDown(new Point(6, 6), MouseButton.Left);
        await PumpUntil(() => cards.FanWindow(bundleId) is null);
        Assert.Null(cards.FanWindow(bundleId));
        Assert.True(stack.IsVisible);
        Assert.False(bundle.Fanned);

        // Again, then Esc.
        cards.FanOut(bundleId);
        await PumpUntil(() => cards.IsFanSettled(bundleId));
        cards.HandleEscape();
        await PumpUntil(() => cards.FanWindow(bundleId) is null);
        Assert.Null(cards.FanWindow(bundleId));
        Assert.True(stack.IsVisible);

        // And the Gather button.
        cards.FanOut(bundleId);
        await PumpUntil(() => cards.IsFanSettled(bundleId));
        cards.Gather(bundleId);
        await PumpUntil(() => cards.FanWindow(bundleId) is null);
        Assert.True(stack.IsVisible);
        Assert.Equal(5, cards.StackDragPaths(bundleId).Count);

        // Dismissing the stack settles it away; late flickStarts of that throw don't bring it back.
        cards.DismissBundle(bundleId);
        await PumpUntil(() => cards.Stacks.Count == 0, 3000);
        Assert.Empty(cards.Stacks);
        await phone.SendAsync("flickStart", new { id = ids[0], kind = "image", name = "IMG_0.png", bundle = new { id = bundleId, index = 0, count = 5 } });
        await PumpUntil(() => false, 200);
        Assert.Empty(cards.Stacks);
        Assert.Empty(cards.Windows);
    }

    [AvaloniaFact]
    public async Task Ten_or_more_fan_out_as_a_grid_with_a_more_card()
    {
        await using var host = new TestHost(name: "Office PC");
        var main = new MainWindow { Width = 900, Height = 600 };
        var controller = new AppController(host.Core, null) { WindowOverride = main };
        await using var phone = await host.ConnectPhoneAsync();
        var cards = controller.Cards;
        var bundleId = Guid.NewGuid().ToString();
        for (var i = 0; i < 12; i++)
            await phone.SendAsync("flickStart", new { id = Guid.NewGuid().ToString(), kind = "text", text = $"Note {i}", exitXRatio = 0.5, velocity = 2000.0, bundle = new { id = bundleId, index = i, count = 12 } });
        await PumpUntil(() => cards.Bundles.Get(bundleId) is { Landed: true, Announced: 12 });
        var bundle = cards.Bundles.Get(bundleId)!;
        Assert.Equal("12 notes", bundle.Title);
        Assert.Equal("12", cards.Stacks.Single().View.BadgeText);
        cards.FanOut(bundleId);
        await PumpUntil(() => cards.IsFanSettled(bundleId));
        var placement = cards.FanPlacementOf(bundleId)!;
        Assert.Equal(9, placement.Slots.Count);
        Assert.Equal(4, placement.Slots[^1].More);
        var fan = cards.FanWindow(bundleId)!;
        Assert.Contains(fan.View.CardViews.OfType<FlickCardView>(), v => v.Card.Kind == "more" && v.Card.Name == "4 more");
        cards.GatherAll();
        await PumpUntil(() => cards.FanWindow(bundleId) is null);
        Assert.Null(cards.FanWindow(bundleId));
    }
}
