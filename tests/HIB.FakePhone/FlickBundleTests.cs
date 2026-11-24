using System.Text.Json;
using Avalonia;
using HIB.Core.Phone;
using HIB.Views.Flick;

namespace HIB.FakePhone;

/// <summary>Flick bundles (SPACE.md §14): grouping, whole-bundle progress, the stack's drag list and the fan-out patterns.</summary>
public class FlickBundleTests
{
    static FlickCard Card(string id, string kind = "image", long size = 1000, FlickTransfer t = FlickTransfer.Waiting) => new(id, kind, id + ".jpg", "image/jpeg", size, t);

    static FlickBundleTag? Tag(string json) => FlickBundleTag.Parse(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void The_bundle_tag_is_read_from_flickStart()
    {
        Assert.Equal(new FlickBundleTag("b1", 2, 5), Tag("""{"id":"x","bundle":{"id":"b1","index":2,"count":5}}"""));
        Assert.Equal(new FlickBundleTag("b1", 0, 3), Tag("""{"bundle":{"id":"b1","index":"0","count":"3"}}"""));
        Assert.Null(Tag("""{"id":"x"}"""));
        Assert.Null(Tag("""{"bundle":{"id":"b1","index":0,"count":1}}""")); // a bundle of one is a single flick
        Assert.Null(Tag("""{"bundle":{"id":"b1","index":5,"count":5}}"""));
        Assert.Null(Tag("""{"bundle":{"id":"","index":0,"count":2}}"""));
        Assert.Null(Tag("""{"bundle":"nope"}"""));
        var wire = JsonSerializer.Serialize(new FlickBundleTag("b9", 1, 4).ToWire());
        Assert.Equal("""{"id":"b9","index":1,"count":4}""", wire);
    }

    [Fact]
    public void Items_of_one_throw_group_into_one_bundle_in_index_order()
    {
        var reg = new FlickBundleRegistry();
        var created = new List<bool>();
        foreach (var i in new[] { 3, 0, 4, 1, 2 }) // they may arrive in any order
            created.Add(reg.Accept(new FlickBundleTag("B", i, 5), Card("f" + i))!.Value.Created);
        Assert.Equal(new[] { true, false, false, false, false }, created);
        var b = Assert.Single(reg.All);
        Assert.Equal(5, b.Count);
        Assert.Equal(new[] { "f0", "f1", "f2", "f3", "f4" }, b.Items.Select(c => c!.Id).ToArray());
        Assert.Same(b, reg.ForFlick("f3"));

        // Another throw is another bundle; a plain flick or a bad index isn't a bundle at all.
        Assert.True(reg.Accept(new FlickBundleTag("C", 1, 2), Card("g1"))!.Value.Created);
        Assert.Equal(2, reg.Count);
        Assert.Null(reg.Accept(null, Card("solo")));
        Assert.Null(reg.Accept(new FlickBundleTag("D", 0, 1), Card("one")));
        Assert.Null(reg.Accept(new FlickBundleTag("B", 7, 5), Card("stray")));
        Assert.Null(reg.ForFlick("solo"));
        Assert.Equal(2, reg.Count);

        reg.Remove("B");
        Assert.Null(reg.ForFlick("f0"));
        Assert.Equal(1, reg.Count);
    }

    [Fact]
    public void The_stack_shows_progress_for_the_whole_bundle_until_all_arrived()
    {
        var b = new FlickBundle("B", 4) { From = "Haitham's S26" };
        b.Put(0, Card("a", size: 1000, t: FlickTransfer.Done));
        b.Put(1, Card("b", size: 3000, t: FlickTransfer.Receiving));
        b.Items[1]!.Progress = 0.5;
        b.Put(2, Card("c", size: 1000));
        // Item 3 hasn't announced itself yet: it weighs like an average item (5000 / 3).
        Assert.True(b.IsBusy);
        Assert.Equal((1000 + 1500) / (5000 + 5000 / 3.0), b.Progress, 6);
        Assert.Equal("4 photos", b.Title);
        Assert.Equal("Receiving 1 of 4 · 38%", b.Subtitle);
        Assert.Equal("4", b.Badge);

        b.Items[1]!.Transfer = FlickTransfer.Done;
        b.Items[2]!.Transfer = FlickTransfer.Done;
        b.Put(3, Card("d", kind: "video", size: 5000, t: FlickTransfer.Done));
        Assert.False(b.IsBusy);
        Assert.True(b.AllArrived);
        Assert.Equal(1, b.Progress, 6);
        Assert.Equal("4 photos & videos", b.Title);
        Assert.Equal("From Haitham's S26 · 10 KB", b.Subtitle);

        var failed = new FlickBundle("F", 3);
        failed.Put(0, Card("x", t: FlickTransfer.Done));
        failed.Put(1, Card("y", t: FlickTransfer.Failed));
        failed.MissingGaveUp = true;
        Assert.False(failed.IsBusy);
        Assert.Equal(2, failed.Failed);
        Assert.Equal("1 of 3 arrived", failed.Subtitle);
        Assert.Equal("3 items", new FlickBundle("E", 3).Title);
        Assert.Equal("2 files", FlickBundle.Noun(2, new[] { "file" }) is var n ? "2 " + n : "");
    }

    [Fact]
    public void Dragging_the_stack_carries_every_arrived_file_in_throw_order()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hib-bundle");
        var b = new FlickBundle("B", 5);
        for (var i = 0; i < 5; i++) b.Put(i, Card("f" + i, t: i == 2 ? FlickTransfer.Receiving : FlickTransfer.Done));
        foreach (var i in new[] { 4, 0, 1, 3 }) b.Items[i]!.FilePath = Path.Combine(dir, $"IMG_{i}.jpg");
        Assert.Equal(new[] { "IMG_0.jpg", "IMG_1.jpg", "IMG_3.jpg", "IMG_4.jpg" }, b.DragPaths.Select(Path.GetFileName).ToArray());
        Assert.Equal(dir, b.Folder);

        // A card dragged out of the fan leaves the bundle: the stack no longer carries it, and the badge counts down.
        b.Items[1]!.Taken = true;
        Assert.Equal(new[] { "IMG_0.jpg", "IMG_3.jpg", "IMG_4.jpg" }, b.DragPaths.Select(Path.GetFileName).ToArray());
        Assert.Equal("4", b.Badge);
        Assert.Equal("f0", b.Top!.Id);
        b.Items[0]!.Taken = true;
        Assert.Equal("f2", b.Top!.Id);
        Assert.Equal(3, b.Present.Count);
        Assert.Empty(new FlickBundle("E", 2).DragPaths);
    }

    public static IEnumerable<object[]> Counts() => Enumerable.Range(2, 11).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Counts))]
    public void Fans_out_in_the_agreed_pattern(int count)
    {
        // SPACE.md §14: 2 side by side · 3 two on top, one centred below · 4 2×2 · 5 dice-five · 6 3×2 · 7 2-3-2 ·
        // 8 3-2-3 · 9 3×3 · 10+ a 3×3 grid whose last card is "+N".
        var expected = count switch
        {
            2 => new[] { 2 },
            3 => new[] { 2, 1 },
            4 => new[] { 2, 2 },
            5 => new[] { 2, 1, 2 },
            6 => new[] { 3, 3 },
            7 => new[] { 2, 3, 2 },
            8 => new[] { 3, 2, 3 },
            _ => new[] { 3, 3, 3 },
        };
        Assert.Equal(expected, FlickFan.Rows(count).ToArray());
        var slots = FlickFan.Slots(count);
        Assert.Equal(Math.Min(count, 9), slots.Count);
        Assert.Equal(expected, slots.GroupBy(s => s.Uy).OrderBy(g => g.Key).Select(g => g.Count()).ToArray());
        // Every row is centred, and the pattern's bounds are centred on the landing point.
        foreach (var row in slots.GroupBy(s => s.Uy)) Assert.Equal(0, row.Sum(s => s.Ux), 6);
        Assert.Equal(0, slots.Max(s => s.Uy) + slots.Min(s => s.Uy), 6);
        // Reading order: top to bottom, left to right.
        Assert.Equal(slots.OrderBy(s => s.Uy).ThenBy(s => s.Ux).ToList(), slots.ToList());
        if (count >= 10)
        {
            var more = slots[^1];
            Assert.True(more.IsMore);
            Assert.Equal(count - 8, more.More);
            Assert.Equal(Enumerable.Range(0, 8), slots.Take(8).Select(s => s.Item));
        }
        else
        {
            Assert.Equal(Enumerable.Range(0, count), slots.Select(s => s.Item));
        }

        // Placed on a roomy screen: centred where the stack landed, full size, cards never overlap.
        var area = new Rect(24, 24, 2512, 1300);
        var at = new Point(1200, 700);
        var p = FlickFan.Place(count, at.X, at.Y, area, FlickCardView.CardWidth, FlickCardView.CardHeight);
        Assert.Equal(1, p.Scale);
        Assert.Equal(at.X, p.Bounds.Center.X, 6);
        Assert.Equal(at.Y, p.Bounds.Center.Y, 6);
        AssertApart(p);
        Assert.True(area.Contains(p.Bounds));

        // Near a corner of a small screen: moved inside, and shrunk when it can't fit.
        var small = new Rect(24, 24, 1232, 600);
        var q = FlickFan.Place(count, 1250, 640, small, FlickCardView.CardWidth, FlickCardView.CardHeight);
        Assert.True(small.Inflate(0.5).Contains(q.Bounds), $"{q.Bounds} inside {small}");
        Assert.InRange(q.Scale, 0.45, 1);
        AssertApart(q);
    }

    [Fact]
    public void Five_is_a_dice_five_two_up_one_in_the_middle_two_down()
    {
        var p = FlickFan.Place(5, 1000, 600, new Rect(0, 0, 2000, 1200), 216, 236);
        var c = p.Centers;
        var dx = 216 + FlickFan.Gap;
        var dy = (236 + FlickFan.Gap) / 2;
        Assert.Equal(new Point(1000, 600), c[2]); // the middle pip
        Assert.Equal(new Point(1000 - dx, 600 - dy), c[0]);
        Assert.Equal(new Point(1000 + dx, 600 - dy), c[1]);
        Assert.Equal(new Point(1000 - dx, 600 + dy), c[3]);
        Assert.Equal(new Point(1000 + dx, 600 + dy), c[4]);
        var three = FlickFan.Place(3, 1000, 600, new Rect(0, 0, 2000, 1200), 216, 236).Centers;
        Assert.Equal(three[0].Y, three[1].Y);
        Assert.Equal(1000, three[2].X); // centred below
        Assert.True(three[2].Y > three[0].Y);
    }

    [Fact]
    public void Fan_tilts_are_small_and_steady()
    {
        for (var i = 0; i < 12; i++)
        {
            Assert.InRange(FlickFan.Tilt(i), -2, 2);
            Assert.Equal(FlickFan.Tilt(i), FlickFan.Tilt(i));
        }
        Assert.NotEqual(FlickFan.Tilt(0), FlickFan.Tilt(1));
    }

    [Fact]
    public void A_fanned_card_springs_to_its_smaller_size()
    {
        var b = new FlickBody { X = 0, Y = 0, Scale = 1, Alpha = 1, TargetX = 300, TargetY = -120, TargetRot = 1.8, TargetScale = 0.62, Mode = FlickBodyMode.Flying };
        var t = 0.0;
        while (b.Advance(1.0 / 120) && t < 3) t += 1.0 / 120;
        Assert.Equal(FlickBodyMode.Landed, b.Mode);
        Assert.Equal(0.62, b.Scale, 6);
        Assert.Equal(300, b.X, 6);
        Assert.InRange(t, 0.2, 2);
        // Bodies that never set a target scale still land at full size.
        var plain = FlickBody.DropAt(10, 10, 0);
        while (plain.Advance(1.0 / 120)) { }
        Assert.Equal(1, plain.Scale, 6);
    }

    static void AssertApart(FanPlacement p)
    {
        var w = FlickCardView.CardWidth * p.Scale;
        var h = FlickCardView.CardHeight * p.Scale;
        for (var i = 0; i < p.Centers.Count; i++)
            for (var j = i + 1; j < p.Centers.Count; j++)
            {
                var a = p.Centers[i];
                var b = p.Centers[j];
                Assert.True(Math.Abs(a.X - b.X) >= w - 0.01 || Math.Abs(a.Y - b.Y) >= h - 0.01, $"cards {i} and {j} overlap");
            }
        // The bounds hold every card.
        foreach (var c in p.Centers)
            Assert.True(p.Bounds.Inflate(0.01).Contains(new Rect(c.X - w / 2, c.Y - h / 2, w, h)));
    }
}
