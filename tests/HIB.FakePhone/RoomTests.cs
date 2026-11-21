using System.Text.Json;
using HIB.Core.Phone;
using HIB.Core.Platform;
using HIB.Core.Roam;
using HIB.Core.Space;

namespace HIB.FakePhone;

/// <summary>
/// Room (docs/SPACE.md §10): EDID sizes, screens placed in millimetres, roomLayout and its conflict rule, the first
/// arrangement, snapping, portals in every direction, crossings through them (2 and 3 computers) and the room shared
/// between two computers over the peer link.
/// </summary>
public class RoomTests
{
    internal const string Pc = "Office PC";
    internal const string Mac = "Haitham’s MacBook";
    internal const string Studio = "Studio PC";

    /// <summary>This PC's Odyssey OLED G9: 5120×1440 at 100 %, 1196×337 mm.</summary>
    internal static readonly RoamDisplay G9 = new(@"\\.\DISPLAY1", new RoamRect(0, 0, 5120, 1440), 1, true)
    {
        Name = "Odyssey OLED G9",
        MmW = 1196,
        MmH = 337,
    };

    /// <summary>A MacBook Pro 14": 1512×982 points at 2×, 302×196 mm.</summary>
    internal static readonly RoomScreenSpec MacBookScreen = new("37D8832A-2D66", "Built-in Retina Display", 0, 0, 1512, 982, 302, 196, true, 3024, 1964, 2);

    internal static RoomComputer PcComputer(params RoamDisplay[] displays)
        => new(Pc, Pc, "windows", (displays.Length > 0 ? displays : new[] { G9 }).Select(RoomScreenSpec.From).ToList(), IsSelf: true);

    internal static RoomComputer MacComputer() => new(Mac, Mac, "mac", new[] { MacBookScreen });

    /// <summary>A 1920×1080 PC with no EDID: 0.25 mm per DIP (480×270 mm).</summary>
    internal static RoomComputer StudioComputer() => new(Studio, Studio, "windows", new[] { new RoomScreenSpec(@"\\.\DISPLAY1", "Display 1", 0, 0, 1920, 1080) });

    static RoomLayout Layout(params (string C, double X, double Y)[] groups) => new(5, Pc, groups.Select(g => new RoomGroup(g.C, g.X, g.Y)).ToList());

    // ---------------- §10.1 EDID ----------------

    /// <summary>A 128-byte EDID with the given cm (bytes 21/22), detailed timing mm and optional name descriptor.</summary>
    internal static byte[] MakeEdid(int cmW, int cmH, int mmW, int mmH, string? name = null)
    {
        var e = new byte[128];
        new byte[] { 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0 }.CopyTo(e, 0);
        e[21] = (byte)cmW;
        e[22] = (byte)cmH;
        // Detailed timing at 54: a pixel clock, then the image size at 66–68.
        e[54] = 0x01;
        e[55] = 0x1D;
        e[66] = (byte)(mmW & 0xFF);
        e[67] = (byte)(mmH & 0xFF);
        e[68] = (byte)(((mmW >> 8) & 0x0F) << 4 | ((mmH >> 8) & 0x0F));
        if (name is not null)
        {
            // Monitor name descriptor at 72: 00 00 00 FC 00 + 13 bytes, 0x0A-terminated, space-padded.
            e[75] = 0xFC;
            var bytes = System.Text.Encoding.ASCII.GetBytes(name);
            for (var i = 0; i < 13; i++) e[77 + i] = i < bytes.Length ? bytes[i] : i == bytes.Length ? (byte)0x0A : (byte)0x20;
        }
        return e;
    }

    [Fact]
    public void Edid_gives_the_physical_size_in_millimetres()
    {
        // The G9: 120×34 cm in the basic block, 1196×337 mm in the detailed timing (needs the high nibbles).
        Assert.Equal((1196.0, 337.0), Edid.PhysicalSizeMm(MakeEdid(120, 34, 1196, 337)));
        // Only centimetres.
        Assert.Equal((600.0, 340.0), Edid.PhysicalSizeMm(MakeEdid(60, 34, 0, 0)));
        // A detailed timing that disagrees with the cm (some monitors put cm there): the cm win.
        Assert.Equal((530.0, 300.0), Edid.PhysicalSizeMm(MakeEdid(53, 30, 53, 30)));
        // cm 0 (a projector), only the timing.
        Assert.Equal((1200.0, 680.0), Edid.PhysicalSizeMm(MakeEdid(0, 0, 1200, 680)));
        // Not an EDID / too short / unknown.
        Assert.Equal((0.0, 0.0), Edid.PhysicalSizeMm(new byte[128]));
        Assert.Equal((0.0, 0.0), Edid.PhysicalSizeMm(new byte[12]));
        Assert.Equal((0.0, 0.0), Edid.PhysicalSizeMm(MakeEdid(0, 0, 0, 0)));
        Assert.Equal("Odyssey G95SC", Edid.MonitorName(MakeEdid(120, 34, 1196, 337, "Odyssey G95SC")));

        // Cleaned at the source (as the Mac does), so both computers use the same numbers: whole mm, turned like the bounds,
        // and 0 when it can't be right — 0.05–1.5 mm per DIP on each axis, the two within 1.5× of each other.
        Assert.Equal((1196.0, 337.0), RoomGeometry.Millimetres(1196, 337, 5120, 1440));
        Assert.Equal((302.0, 196.0), RoomGeometry.Millimetres(301.6, 196.4, 1512, 982));
        Assert.Equal((336.0, 597.0), RoomGeometry.Millimetres(597, 336, 1440, 2560)); // a portrait monitor
        Assert.Equal((0.0, 0.0), RoomGeometry.Millimetres(16, 9, 1920, 1080));        // an aspect-only EDID
        Assert.Equal((0.0, 0.0), RoomGeometry.Millimetres(600, 600, 1920, 1080));     // aspect wildly off
        Assert.Equal((0.0, 0.0), RoomGeometry.Millimetres(5000, 2800, 1920, 1080));   // a TV claiming metres
        Assert.Equal((0.0, 0.0), RoomGeometry.Millimetres(0, 337, 5120, 1440));
        Assert.Null(Edid.MonitorName(MakeEdid(120, 34, 1196, 337)));
    }

    [Fact]
    public void Monitor_device_paths_lead_to_the_registry_key_with_the_edid()
    {
        Assert.Equal(("SAM7179", "5&2a5bd0b2&0&UID4352"),
            MonitorDetailsProvider.RegistryKeyFor(@"\\?\DISPLAY#SAM7179#5&2a5bd0b2&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}"));
        Assert.Null(MonitorDetailsProvider.RegistryKeyFor(""));
        Assert.Null(MonitorDetailsProvider.RegistryKeyFor(@"\\?\USB#VID_1234#x#{guid}"));
        Assert.Empty(OperatingSystem.IsWindows() ? Array.Empty<MonitorDetails>() : MonitorDetailsProvider.Query()); // nothing to read off Windows
    }

    [Fact]
    public void Screens_info_and_ctlHello_carry_name_millimetres_and_pixels()
    {
        var info = new DisplayScreen(@"\\.\DISPLAY1", "Odyssey OLED G9", true, 0, 0, 5120, 1440) { MmW = 1196, MmH = 337, PxW = 5120, PxH = 1440, Scale = 1 };
        var json = JsonSerializer.SerializeToElement(PointAim.BuildScreensInfo(new[] { info })).GetProperty("screens")[0];
        Assert.Equal("Odyssey OLED G9", json.GetProperty("name").GetString());
        Assert.Equal(1196, json.GetProperty("mmW").GetDouble());
        Assert.Equal(337, json.GetProperty("mmH").GetDouble());
        Assert.False(json.GetProperty("builtIn").GetBoolean());
        Assert.Equal(5120, json.GetProperty("pxW").GetInt32());
        Assert.Equal(1440, json.GetProperty("pxH").GetInt32());
        Assert.Equal(1, json.GetProperty("scale").GetDouble());

        var hello = new CtlHello(true, "windows", new[] { new RoamScreen("x", 0, 0, 1512, 982) { Name = "Built-in Retina Display", MmW = 302, MmH = 196, BuiltIn = true, PxW = 3024, PxH = 1964, Scale = 2 } }, null, true);
        using var doc = JsonDocument.Parse(hello.ToWire().ToJsonString());
        var back = CtlHello.Parse(doc.RootElement).Screens[0];
        Assert.Equal(("Built-in Retina Display", 302.0, 196.0, true, 3024, 1964, 2.0), (back.Name, back.MmW, back.MmH, back.BuiltIn, back.PxW, back.PxH, back.Scale));
        // An older computer (no §10.1 fields): unknown mm, pixels from points × 1.
        using var old = JsonDocument.Parse("""{"screens":[{"id":"a","x":0,"y":0,"w":1512,"h":982}]}""");
        var o = CtlHello.Parse(old.RootElement).Screens[0];
        Assert.Equal((0.0, 0.0, false, 1512, 982), (o.MmW, o.MmH, o.BuiltIn, o.PxW, o.PxH));
    }

    [Fact]
    public void Screen_map_names_monitors_and_sizes_them_from_windows()
    {
        var screens = new[]
        {
            FakeScreen.Make(@"\\.\DISPLAY1", new Avalonia.PixelRect(0, 0, 5120, 1440), 1, true),
            FakeScreen.Make(@"\\.\DISPLAY2", new Avalonia.PixelRect(5120, 0, 1440, 2560), 1), // a portrait monitor
            FakeScreen.Make(@"\\.\DISPLAY3", new Avalonia.PixelRect(-1920, 0, 1920, 1080), 1),
        };
        var details = new[]
        {
            new MonitorDetails(@"\\.\DISPLAY1", "Odyssey OLED G9", 1196, 337, false),
            new MonitorDetails(@"\\.\display2", "DELL U2720Q", 597, 336, false),
        };
        var d = HIB.Services.ScreenMap.Describe(screens, details).Select(x => x.Info).ToList();
        Assert.Equal(("Odyssey OLED G9", 1196.0, 337.0, 5120, 1440), (d[0].Name, d[0].MmW, d[0].MmH, d[0].PxW, d[0].PxH));
        Assert.Equal(("DELL U2720Q", 336.0, 597.0), (d[1].Name, d[1].MmW, d[1].MmH)); // turned: the EDID size swaps
        Assert.Equal(("Display 3", 0.0, 0.0), (d[2].Name, d[2].MmW, d[2].MmH)); // unknown
    }

    // ---------------- §10.2 placement ----------------

    [Fact]
    public void A_computers_screens_keep_their_arrangement_at_its_primary_screens_mm_per_dip()
    {
        // The G9 with a 27" 4K above-right at 150 % (2560×1440 DIPs), EDID 597×336 mm.
        var top = new RoamDisplay(@"\\.\DISPLAY2", new RoamRect(3840, -2160, 3840, 2160), 1.5) { MmW = 597, MmH = 336 };
        var pc = PcComputer(G9, top);
        var k = 1196.0 / 5120;
        Assert.Equal(k, RoomGeometry.MmPerDip(pc.Screens), 9);
        var placed = RoomGeometry.Place(pc, 100, 50);
        Assert.Equal(new RoamRect(100, 50, 1196, 337), placed[0].Mm);
        // Position = OS DIPs (2560, −1440) × the primary's mm per DIP; size = its own mm.
        Assert.Equal(100 + 2560 * k, placed[1].Mm.X, 6);
        Assert.Equal(50 - 1440 * k, placed[1].Mm.Y, 6);
        Assert.Equal((597.0, 336.0), (placed[1].Mm.W, placed[1].Mm.H));
        // Unknown millimetres: 0.25 mm per DIP.
        var studio = RoomGeometry.Place(StudioComputer(), 0, 0)[0];
        Assert.Equal(new RoamRect(0, 0, 480, 270), studio.Mm);
        // The primary is the screen at (0, 0) even if it isn't listed first.
        var shuffled = new[] { RoomScreenSpec.From(top), RoomScreenSpec.From(G9) with { Primary = false } };
        Assert.Equal(k, RoomGeometry.MmPerDip(shuffled), 9);
    }

    [Fact]
    public void RoomLayout_wire_and_the_conflict_rule()
    {
        var l = new RoomLayout(1763712000000, Mac, new[] { new RoomGroup(Mac, 0, 0), new RoomGroup("PC-INTERN", 1210, -40) });
        using var doc = JsonDocument.Parse(l.ToWire().ToJsonString());
        var d = doc.RootElement;
        Assert.Equal(1, d.GetProperty("v").GetInt32());
        Assert.Equal(1763712000000, d.GetProperty("rev").GetInt64());
        Assert.Equal(Mac, d.GetProperty("by").GetString());
        Assert.Equal("PC-INTERN", d.GetProperty("groups")[1].GetProperty("computer").GetString());
        Assert.Equal(1210, d.GetProperty("groups")[1].GetProperty("x").GetDouble());
        Assert.Equal(-40, d.GetProperty("groups")[1].GetProperty("y").GetDouble());
        var back = RoomLayout.Parse(d)!;
        Assert.True(back.SamePlaces(l));
        Assert.Equal(l.Rev, back.Rev);
        // Offsets are whole millimetres on the wire (half away from zero), and parsed that way.
        using var frac = JsonDocument.Parse(new RoomLayout(1, Pc, new[] { new RoomGroup(Pc, 1210.5, -40.5) }).ToWire().ToJsonString());
        Assert.Equal("1211", frac.RootElement.GetProperty("groups")[0].GetProperty("x").GetRawText());
        Assert.Equal("-41", frac.RootElement.GetProperty("groups")[0].GetProperty("y").GetRawText());
        using var inFrac = JsonDocument.Parse("""{"v":1,"rev":2,"by":"x","groups":[{"computer":"A","x":12.5,"y":-0.4}]}""");
        Assert.Equal(new RoomGroup("A", 13, 0), RoomLayout.Parse(inFrac.RootElement)!.Groups[0]);

        // Highest rev wins; equal rev → lower by (UTF-8 bytes); the same one never "wins" again.
        var older = l with { Rev = 5 };
        Assert.True(RoomLayout.Wins(l, older));
        Assert.False(RoomLayout.Wins(older, l));
        Assert.True(RoomLayout.Wins(l with { By = "Aardvark" }, l));
        Assert.False(RoomLayout.Wins(l with { By = "Zed" }, l));
        Assert.False(RoomLayout.Wins(l, l));
        Assert.True(RoomLayout.Wins(l, null));
        // "Haitham’s MacBook" < "Office PC" in UTF-8, and é (C3 A9) sorts after z.
        Assert.True(RoomLayout.Wins(new RoomLayout(0, Mac, l.Groups), new RoomLayout(0, Pc, l.Groups)));
        Assert.True(RoomLayout.Wins(new RoomLayout(0, "zed", l.Groups), new RoomLayout(0, "éd", l.Groups)));
        // The same author's same rev with other groups replaces it (an auto layout re-sent after someone joined)…
        var moved = new RoomLayout(0, Mac, new[] { new RoomGroup(Mac, 0, -196), new RoomGroup(Pc, 302, -337) });
        Assert.True(RoomLayout.Wins(moved, new RoomLayout(0, Mac, l.Groups)));
        Assert.True(RoomLayout.Adopt(moved, new RoomLayout(0, Mac, l.Groups), Pc));
        // …but a computer never takes back an older copy of its own layout; only a newer one (its storage was lost).
        Assert.False(RoomLayout.Adopt(moved with { By = Pc }, new RoomLayout(0, Pc, l.Groups), Pc));
        Assert.False(RoomLayout.Adopt(moved with { By = Pc, Rev = 3 }, new RoomLayout(7, Pc, l.Groups), Pc));
        Assert.True(RoomLayout.Adopt(moved with { By = Pc, Rev = 9 }, new RoomLayout(7, Pc, l.Groups), Pc));
        // A new rev is the clock, but always past the current.
        var now = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTimeOffset(now).ToUnixTimeMilliseconds(), RoomLayout.NextRev(older, now));
        Assert.Equal(l.Rev + 1, RoomLayout.NextRev(l with { Rev = long.MaxValue / 2 }, now) - (long.MaxValue / 2 - l.Rev));
    }

    static (RoamSide, bool) Unknown(string _) => (RoamSide.Right, false);

    [Fact]
    public void Auto_place_puts_the_mac_first_and_the_others_beside_it_on_one_desk_line()
    {
        var computers = new[] { PcComputer(), MacComputer() };
        var none = Array.Empty<RoomGroup>();
        // Nothing known: the Mac first, the PC to its right, bottoms on y = 0 — the same on the Mac and the PC.
        var groups = RoomGeometry.AutoPlace(computers, Pc, none, Unknown);
        Assert.Equal(new[] { new RoomGroup(Pc, 302, -337), new RoomGroup(Mac, 0, -196) }, groups);
        Assert.Equal(groups.OrderBy(g => g.Computer), RoomGeometry.AutoPlace(computers, Mac, none, Unknown).OrderBy(g => g.Computer));
        var room = RoomGeometry.Build(computers, groups);
        Assert.Equal(0, room.BoundsOf(Mac).Bottom);
        Assert.Equal(0, room.BoundsOf(Pc).Bottom);

        // Known sides (Point setup / a manual choice), seen from me: I'm at the origin's desk line, the others around me.
        var left = RoomGeometry.AutoPlace(computers, Pc, none, id => id == Mac ? (RoamSide.Left, true) : Unknown(id));
        Assert.Equal(new RoomGroup(Pc, 0, -337), left.Single(g => g.Computer == Pc));
        Assert.Equal(new RoomGroup(Mac, -302, -196), left.Single(g => g.Computer == Mac)); // bottoms on my bottom
        var above = RoomGeometry.AutoPlace(computers, Pc, none, id => id == Mac ? (RoamSide.Above, true) : Unknown(id));
        Assert.Equal(new RoomGroup(Mac, 598 - 151, -337 - 196), above.Single(g => g.Computer == Mac)); // centred on me
        // Without a Mac: by id, to the right, one desk line.
        var two = RoomGeometry.AutoPlace(new[] { StudioComputer(), PcComputer() }, Studio, none, Unknown);
        Assert.Equal(new RoomGroup(Pc, 0, -337), two.Single(g => g.Computer == Pc));
        Assert.Equal(new RoomGroup(Studio, 1196, -270), two.Single(g => g.Computer == Studio));
        // Half millimetres round away from zero (a 245.5 mm tall screen: −246).
        var half = RoomGeometry.AutoPlace(new[] { new RoomComputer("M", "M", "mac", new[] { new RoomScreenSpec("a", "", 0, 0, 1512, 982) }) }, "M", none, Unknown);
        Assert.Equal(new RoomGroup("M", 0, -246), half[0]);
    }

    [Fact]
    public void A_newcomer_goes_beside_an_arrangement_the_user_made()
    {
        var arranged = new[] { new RoomGroup(Pc, 0, 0), new RoomGroup(Mac, 447, -196) };
        var all = new[] { PcComputer(), MacComputer(), StudioComputer() };
        var next = RoomGeometry.AutoPlace(all, Pc, arranged, Unknown);
        Assert.Equal(new RoomGroup(Pc, 0, 0), next.Single(g => g.Computer == Pc)); // untouched
        Assert.Equal(new RoomGroup(Mac, 447, -196), next.Single(g => g.Computer == Mac));
        Assert.Equal(new RoomGroup(Studio, 1196, 337 - 270), next.Single(g => g.Computer == Studio)); // right of everything, on my desk line
        var onLeft = RoomGeometry.AutoPlace(all, Pc, arranged, id => id == Studio ? (RoamSide.Left, true) : Unknown(id));
        Assert.Equal(new RoomGroup(Studio, -480, 67), onLeft.Single(g => g.Computer == Studio));
        // Learning: touching my group's box, centred on that edge.
        Assert.Equal((1196.0, 71.0), RoomGeometry.Touching(MacComputer(), RoamSide.Right, new RoamRect(0, 0, 1196, 337))); // 168.5 − 98 = 70.5 → 71
        Assert.Equal((447.0, -196.0), RoomGeometry.Touching(MacComputer(), RoamSide.Above, new RoamRect(0, 0, 1196, 337)));
    }

    [Fact]
    public void Dragging_snaps_edges_within_40_mm()
    {
        var g9 = new RoamRect(0, 0, 1196, 337);
        // A MacBook dragged near the G9's right edge and its desk line: touching and bottoms aligned.
        var (dx, dy) = RoomGeometry.Snap(new[] { new RoamRect(1225, 160, 302, 196) }, new[] { g9 });
        Assert.Equal((-29.0, -19.0), (dx, dy));
        // Above, nearly touching the top: snaps down onto it.
        (dx, dy) = RoomGeometry.Snap(new[] { new RoamRect(447, -230, 302, 196) }, new[] { g9 });
        Assert.Equal((0.0, 34.0), (dx, dy));
        // Too far on both axes: nothing.
        Assert.Equal((0.0, 0.0), RoomGeometry.Snap(new[] { new RoamRect(1300, -400, 302, 196) }, new[] { g9 }));
        // The nearest edge on each axis: its left edge onto the G9's right edge (−4), its top onto the G9's bottom (−13).
        Assert.Equal((-4.0, -13.0), RoomGeometry.Snap(new[] { new RoamRect(1200, 350, 302, 196) }, new[] { g9 }));
        // Edges only snap to groups nearby on the other axis: well below the G9, its right edge doesn't pull.
        Assert.Equal((0.0, 0.0), RoomGeometry.Snap(new[] { new RoamRect(1200, 500, 302, 196) }, new[] { g9 }));
        // Stacked groups also centre.
        Assert.Equal((-20.0, 0.0), RoomGeometry.Snap(new[] { new RoamRect(467, -196, 302, 196) }, new[] { g9 }));
    }

    // ---------------- §10.3 portals ----------------

    [Fact]
    public void Portals_on_all_four_sides_with_partial_overlap()
    {
        var pc = PcComputer();
        var mac = MacComputer();
        // Right: the MacBook against the G9's right edge, bottoms aligned: only the bottom 196 mm cross.
        var room = RoomGeometry.Build(new[] { pc, mac }, Layout((Pc, 0, 0), (Mac, 1196, 141)));
        var p = Assert.Single(room.Portals, x => x.FromComputer == Pc);
        Assert.Equal((RoamSide.Right, Mac, 141.0, 337.0), (p.Side, p.ToComputer, p.Start, p.End));
        var back = Assert.Single(room.Portals, x => x.FromComputer == Mac);
        Assert.Equal((RoamSide.Left, 141.0, 337.0), (back.Side, back.Start, back.End));
        Assert.True(room.Touches(Pc, Mac) && room.Connected(Mac));

        // Left, with a 25 mm gap: still a portal (within 30).
        room = RoomGeometry.Build(new[] { pc, mac }, Layout((Pc, 0, 0), (Mac, -302 - 25, 141)));
        Assert.Equal(RoamSide.Left, Assert.Single(room.Portals, x => x.FromComputer == Pc).Side);
        // 31 mm: none.
        room = RoomGeometry.Build(new[] { pc, mac }, Layout((Pc, 0, 0), (Mac, -302 - 31, 141)));
        Assert.Empty(room.Portals);
        Assert.False(room.Connected(Mac));

        // Above (the MacBook on top of the G9, centred) and below.
        room = RoomGeometry.Build(new[] { pc, mac }, Layout((Pc, 0, 0), (Mac, 447, -196)));
        p = Assert.Single(room.Portals, x => x.FromComputer == Pc);
        Assert.Equal((RoamSide.Above, 447.0, 749.0), (p.Side, p.Start, p.End));
        Assert.Equal(RoamSide.Below, Assert.Single(room.Portals, x => x.FromComputer == Mac).Side);
        room = RoomGeometry.Build(new[] { pc, mac }, Layout((Pc, 0, 0), (Mac, 1000, 337 + 10)));
        p = Assert.Single(room.Portals, x => x.FromComputer == Pc);
        Assert.Equal((RoamSide.Below, 1000.0, 1196.0), (p.Side, p.Start, p.End)); // hanging off the right end: partial

        // Corner to corner (no overlap) and overlapping each other: no portal.
        Assert.Empty(RoomGeometry.Build(new[] { pc, mac }, Layout((Pc, 0, 0), (Mac, 1196, 337))).Portals);
        Assert.Empty(RoomGeometry.Build(new[] { pc, mac }, Layout((Pc, 0, 0), (Mac, 400, 100))).Portals);
    }

    [Fact]
    public void Crossing_enters_at_the_same_room_position_clamped_into_the_overlap()
    {
        var room = RoomGeometry.Build(new[] { PcComputer(), MacComputer() }, Layout((Pc, 0, 0), (Mac, 447, -196)));
        var g9 = room.Find(Pc, G9.Id)!;
        var mbp = room.Find(Mac, MacBookScreen.Id)!;
        // mm ↔ desktop: the G9's centre column is 598 mm; that is 151/302 across the MacBook.
        Assert.Equal(598, RoomGeometry.Along(g9, RoamSide.Above, 2560, 0), 6);
        Assert.Equal((2560.0, 720.0), RoomGeometry.ToDips(g9, 598, 168.5));
        Assert.Equal((598.0, 168.5), RoomGeometry.ToMm(g9, 2560, 720));
        var c = room.Cross(Pc, G9.Id, RoamSide.Above, 2560, 0)!;
        Assert.Equal((Mac, MacBookScreen.Id), (c.To.Computer, c.To.Id));
        Assert.Equal(756, c.X, 6);
        Assert.Equal(980, c.Y); // 1 point inside the MacBook's bottom row
        // Outside the overlap there is no crossing.
        Assert.Null(room.Cross(Pc, G9.Id, RoamSide.Above, 1000, 0));
        Assert.Null(room.Cross(Pc, G9.Id, RoamSide.Right, 5119, 700));
        // Back down from the MacBook's bottom-left corner: the G9 at 447 mm.
        c = room.Cross(Mac, MacBookScreen.Id, RoamSide.Below, 0, 981)!;
        Assert.Equal(Pc, c.To.Computer);
        Assert.Equal(447 / (1196.0 / 5120), c.X, 3);
        Assert.Equal(1, c.Y);

        // Clamping: a portal shorter than the edge (the MacBook's bottom 196 mm of the G9's left edge) maps onto the
        // overlap, and the entry point is inside the target screen.
        room = RoomGeometry.Build(new[] { PcComputer(), MacComputer() }, Layout((Pc, 0, 0), (Mac, -302, 141)));
        c = room.Cross(Pc, G9.Id, RoamSide.Left, 0, 1439)!;
        Assert.Equal(1510, c.X); // 1 point inside the MacBook's right column
        Assert.InRange(c.Y, 980, 981);
        Assert.Equal(337, c.Along, 0); // at the overlap's end
        Assert.Equal(0, room.Cross(Pc, G9.Id, RoamSide.Left, 0, 141 / (337.0 / 1440))!.Y, 6);
    }

    [Fact]
    public void Three_computers_each_reach_their_neighbours_and_the_nearest_portal_wins()
    {
        // Studio (480×270) left of the G9, tops aligned; the MacBook on top of both, over the gap between them.
        var computers = new[] { PcComputer(), MacComputer(), StudioComputer() };
        var room = RoomGeometry.Build(computers, Layout((Pc, 0, 0), (Studio, -480, 0), (Mac, -151, -196)));
        Assert.Contains(room.Portals, p => p.FromComputer == Pc && p.Side == RoamSide.Left && p.ToComputer == Studio);
        Assert.Contains(room.Portals, p => p.FromComputer == Pc && p.Side == RoamSide.Above && p.ToComputer == Mac && p.Start == 0 && p.End == 151);
        Assert.Contains(room.Portals, p => p.FromComputer == Studio && p.Side == RoamSide.Above && p.ToComputer == Mac);
        Assert.Contains(room.Portals, p => p.FromComputer == Mac && p.Side == RoamSide.Below && p.ToComputer == Studio && p.Start == -151 && p.End == 0);
        // From the MacBook's bottom edge: left half → Studio, right half → the PC.
        Assert.Equal(Studio, room.Cross(Mac, MacBookScreen.Id, RoamSide.Below, 300, 981)!.To.Computer);
        Assert.Equal(Pc, room.Cross(Mac, MacBookScreen.Id, RoamSide.Below, 1200, 981)!.To.Computer);
        // Only computers the caller allows (e.g. linked ones) count.
        Assert.Null(room.Cross(Mac, MacBookScreen.Id, RoamSide.Below, 300, 981, id => id != Studio));
        // Two candidates on one stretch: the nearer one.
        var both = new RoomMap(RoomGeometry.Place(PcComputer(), 0, 0)
            .Concat(RoomGeometry.Place(MacComputer(), 1196 + 20, 141))
            .Concat(RoomGeometry.Place(StudioComputer(), 1196 + 5, 0)));
        Assert.Equal(Studio, both.PortalAt(Pc, G9.Id, RoamSide.Right, 200)!.ToComputer);
        // Same gap: the longer overlap (the Studio's 270 mm over the MacBook's 196), then the lower id.
        var even = new RoomMap(RoomGeometry.Place(PcComputer(), 0, 0)
            .Concat(RoomGeometry.Place(MacComputer(), 1196, 141))
            .Concat(RoomGeometry.Place(StudioComputer(), 1196, 0)));
        Assert.Equal(Studio, even.PortalAt(Pc, G9.Id, RoamSide.Right, 200)!.ToComputer);
        var same = new RoomMap(RoomGeometry.Place(PcComputer(), 0, 0)
            .Concat(RoomGeometry.Place(MacComputer(), 1196, 141))
            .Concat(RoomGeometry.Place(new RoomComputer("Zed", "Zed", "mac", new[] { MacBookScreen }), 1196, 141)));
        Assert.Equal(Mac, same.PortalAt(Pc, G9.Id, RoamSide.Right, 200)!.ToComputer);
        Assert.Equal(RoamSide.Right, RoomGeometry.SideOf(both.BoundsOf(Pc), both.BoundsOf(Mac)));
        Assert.Equal(RoamSide.Above, RoomGeometry.SideOf(new RoamRect(0, 0, 1196, 337), new RoamRect(447, -196, 302, 196)));
    }

    // ---------------- the controller through portals ----------------

    sealed class Clock
    {
        public DateTime Now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
    }

    static (RoamController c, RecordingHost h) RoomController(RoomLayout layout, params RoomComputer[] others)
    {
        var clock = new Clock();
        var h = new RecordingHost();
        var c = new RoamController(h, () => clock.Now);
        c.SetDisplays(new[] { G9 });
        var room = RoomGeometry.Build(new[] { PcComputer() }.Concat(others), layout);
        c.SetRoom(room, Pc);
        c.SetNeighbors(others.Select(o => new RoamNeighbor(o.Id, o.Name, RoomGeometry.SideOf(room.BoundsOf(Pc), room.BoundsOf(o.Id)),
            o.Screens.Select(s => s.Dips).ToList(), o.Platform, true, InRoom: true)).ToList());
        return (c, h);
    }

    [Fact]
    public void The_cursor_goes_up_into_the_macbook_above_the_g9_and_comes_back_down()
    {
        var (c, h) = RoomController(Layout((Pc, 0, 0), (Mac, 447, -196)), MacComputer());
        // Near the top edge, under the MacBook: the glow covers only the portal.
        c.Handle(RoamInput.Pointer(2560, 20, false));
        Assert.NotNull(h.Glow);
        Assert.Equal(RoamSide.Above, h.Glow!.Side);
        Assert.Equal(447 / (1196.0 / 5120), h.Glow.SegStart, 3);
        Assert.Equal(749 / (1196.0 / 5120), h.Glow.SegEnd, 3);
        // Not under the MacBook: the top edge leads nowhere.
        c.Handle(RoamInput.Pointer(500, -6, false));
        Assert.Equal(RoamMode.Home, c.Mode);
        Assert.Null(h.Glow);
        // The right edge leads nowhere either (no §9.2 whole-desktop edge with a room).
        c.Handle(RoamInput.Pointer(5125, 700, false));
        Assert.Equal(RoamMode.Home, c.Mode);

        // Up under the MacBook's middle.
        c.Handle(RoamInput.Pointer(2560, -4, false));
        Assert.Equal(RoamMode.Away, c.Mode);
        Assert.Equal(Mac, h.Entered!.Value.Peer.PeerId);
        Assert.Equal(756, h.Entered.Value.X, 6);
        Assert.Equal(980, h.Entered.Value.Y);
        // Over the MacBook's left edge: nothing there, it stops.
        c.Handle(RoamInput.Motion(-2000, -300));
        Assert.Equal(RoamMode.Away, c.Mode);
        Assert.Equal((0.0, 680.0), c.Virtual);
        // In its bottom-left corner the corner lock holds (§9.2).
        c.Handle(RoamInput.Motion(0, 400));
        Assert.Equal(RoamMode.Away, c.Mode);
        // Down off its bottom edge 100 points in: home onto the G9's top edge at the same room position, 2 px in.
        c.Handle(RoamInput.Motion(100, 400));
        Assert.Equal(RoamMode.Home, c.Mode);
        Assert.Equal(1, h.Leaves);
        Assert.Equal((447 + 100.0 / 1512 * 302) / (1196.0 / 5120), h.Uncaptured!.Value.X, 0);
        Assert.Equal(1, h.Uncaptured.Value.Y);
    }

    [Fact]
    public void With_three_computers_the_cursor_goes_straight_on_from_one_to_the_next()
    {
        // The MacBook on top of the G9's left end; the Studio PC to the MacBook's right, also on the G9.
        var (c, h) = RoomController(Layout((Pc, 0, 0), (Mac, 0, -196), (Studio, 302, -270)), MacComputer(), StudioComputer());
        c.Handle(RoamInput.Pointer(300, -3, false));
        Assert.Equal(Mac, c.Target!.PeerId);
        c.Handle(RoamInput.KeyEvent(0x04, true)); // held while switching: lifted on the MacBook
        // Right across the MacBook's right edge, 100 mm down from its top → the Studio's left edge at 100 + 74 mm.
        var (vx, vy) = c.Virtual;
        c.Handle(RoamInput.Motion(1512 - vx + 5, 100 / (196.0 / 982) - vy));
        Assert.Equal(Studio, c.Target!.PeerId);
        Assert.Equal(RoamMode.Away, c.Mode);
        Assert.Equal(1, h.Leaves);
        Assert.Contains(h.Sent, m => m.Type == RoamWire.Key && m.Data["usage"]!.GetValue<int>() == 0x04 && !m.Data["down"]!.GetValue<bool>());
        Assert.Equal(Studio, h.Entered!.Value.Peer.PeerId);
        Assert.Equal(1, h.Entered.Value.X);
        Assert.Equal((100.0 + 74) / 270 * 1080, h.Entered.Value.Y, 1);
        Assert.Null(h.Uncaptured); // never came home in between
        // Down off the Studio's bottom edge → home on the G9.
        c.Handle(RoamInput.Motion(200, 2000));
        Assert.Equal(RoamMode.Home, c.Mode);
        Assert.NotNull(h.Uncaptured);
    }
    // ---------------- the room shared over the peer link ----------------

    internal sealed class Desk : IAsyncDisposable
    {
        public required TestHost Pc, Mac;
        public required FakeRoamPlatform PcInput, MacInput;
        public async ValueTask DisposeAsync()
        {
            await Pc.DisposeAsync();
            await Mac.DisposeAsync();
        }
    }

    /// <summary>"Office PC" with the G9 and "Haitham’s MacBook" (a second core playing the Mac, platform "mac"), linked directly.</summary>
    internal static async Task<Desk> DeskAsync()
    {
        var pcIn = new FakeRoamPlatform();
        var macIn = new FakeRoamPlatform { Platform = "mac" };
        var pc = new TestHost(name: Pc, space: RoamTests.FastSpace(), roam: new RoamOptions { Platform = () => pcIn });
        var mac = new TestHost(name: Mac, space: RoamTests.FastSpace(), roam: new RoamOptions { Platform = () => macIn });
        pc.Core.SpaceEngine.SetSpaceKey(RoamTests.SpaceKey);
        mac.Core.SpaceEngine.SetSpaceKey(RoamTests.SpaceKey);
        pc.Core.SpaceEngine.RememberPeer(Mac, Mac, "mac", new[] { "127.0.0.1" }, mac.Core.Link.Port);
        mac.Core.SpaceEngine.RememberPeer(Pc, Pc, "windows", new[] { "127.0.0.1" }, pc.Core.Link.Port);
        pc.Core.Roam.SetDisplays(new[] { G9 });
        mac.Core.Roam.SetDisplays(new[] { new RoamDisplay(MacBookScreen.Id, new RoamRect(0, 0, 3024, 1964), 2, true) { Name = "Built-in Retina Display", MmW = 302, MmH = 196, BuiltIn = true } });
        Assert.True(await Eventually.TrueAsync(() => pc.Core.Roam.InRoom(Mac) && mac.Core.Roam.InRoom(Pc), TimeSpan.FromSeconds(10)));
        return new Desk { Pc = pc, Mac = mac, PcInput = pcIn, MacInput = macIn };
    }

    static bool SameRoom(Desk d) => d.Pc.Core.Roam.RoomLayout is { } a && d.Mac.Core.Roam.RoomLayout is { } b
                                    && a.Rev == b.Rev && a.By == b.By && a.SamePlaces(b);

    [Fact]
    public async Task Two_computers_agree_on_one_room_and_the_cursor_follows_it()
    {
        await using var d = await DeskAsync();
        // Both auto-placed (rev 0); the lower id's wins on both: the Mac at the origin, the PC right of it, one desk line.
        Assert.True(await Eventually.TrueAsync(() => SameRoom(d)));
        var room = d.Pc.Core.Roam.RoomLayout!;
        Assert.Equal(0, room.Rev);
        Assert.Equal(Mac, room.By);
        Assert.Equal(new RoomGroup(Mac, 0, -196), room.Find(Mac));
        Assert.Equal(new RoomGroup(Pc, 302, -337), room.Find(Pc));
        var status = d.Pc.Core.Roam.StatusFor(Mac)!;
        Assert.Equal((RoamSide.Left, RoamSideSource.Room, false), (status.Side, status.SideSource, status.NeedsAsk));
        Assert.Equal($"Move the cursor off the left edge to go to {Mac}", status.Status);
        // The snapshot the Room view draws: both computers with their real sizes.
        var snap = d.Pc.Core.Roam.Room;
        Assert.Equal(new[] { Pc, Mac }, snap.Computers.Select(c => c.Id));
        Assert.Equal((1196.0, 337.0), (snap.Computers[0].Screens[0].MmW, snap.Computers[0].Screens[0].MmH));
        Assert.Equal((302.0, 196.0, true), (snap.Computers[1].Screens[0].MmW, snap.Computers[1].Screens[0].MmH, snap.Computers[1].Screens[0].BuiltIn));

        // High on the G9's left edge there's nothing (the MacBook is shorter); low down, it crosses.
        d.PcInput.Raise(RoamInput.Pointer(-3, 100, false));
        Assert.Equal(RoamMode.Home, d.Pc.Core.Roam.Mode);
        d.PcInput.Raise(RoamInput.Pointer(-3, 1300, false));
        Assert.Equal(RoamMode.Away, d.Pc.Core.Roam.Mode);
        Assert.True(await Eventually.TrueAsync(() => d.Mac.Core.Roam.ControlledById == Pc));
        Assert.True(await Eventually.TrueAsync(() => d.MacInput.Moves.Count > 0));
        var at = -337 + 1300 * 337.0 / 1440;
        Assert.Equal(3020, d.MacInput.Moves[0].X, 0); // 1 point inside the MacBook's right column (1510 points × 2)
        Assert.Equal((at + 196) / 196 * 982 * 2, d.MacInput.Moves[0].Y, 0);
        d.PcInput.Raise(RoamInput.Motion(50, 0)); // straight back
        Assert.True(await Eventually.TrueAsync(() => d.Pc.Core.Roam.Mode == RoamMode.Home && d.Mac.Core.Roam.ControlledById is null));
    }

    [Fact]
    public async Task Arranging_the_desk_reaches_the_other_computer_and_changes_where_the_mouse_goes()
    {
        await using var d = await DeskAsync();
        Assert.True(await Eventually.TrueAsync(() => SameRoom(d)));
        var changes = 0;
        d.Mac.Core.Roam.RoomChanged += () => Interlocked.Increment(ref changes);
        // The user puts the MacBook on top of the G9, centred.
        d.Pc.Core.Roam.SetRoomGroups(new[] { new RoomGroup(Pc, 0, 0), new RoomGroup(Mac, 447, -196) });
        Assert.True(await Eventually.TrueAsync(() => SameRoom(d) && d.Mac.Core.Roam.RoomLayout!.Rev > 0));
        Assert.Equal(Pc, d.Mac.Core.Roam.RoomLayout!.By);
        Assert.True(changes > 0);
        Assert.Equal(RoamSide.Above, d.Pc.Core.Roam.StatusFor(Mac)!.Side);
        Assert.True(await Eventually.TrueAsync(() => d.Mac.Core.Roam.StatusFor(Pc) is { Side: RoamSide.Below, SideSource: RoamSideSource.Room }));
        // The left edge is dead now; the top centre goes up into the MacBook's bottom edge.
        d.PcInput.Raise(RoamInput.Pointer(-3, 1300, false));
        Assert.Equal(RoamMode.Home, d.Pc.Core.Roam.Mode);
        d.PcInput.Raise(RoamInput.Pointer(2560, -2, false));
        Assert.Equal(RoamMode.Away, d.Pc.Core.Roam.Mode);
        Assert.True(await Eventually.TrueAsync(() => d.MacInput.Moves.Count > 0));
        Assert.Equal((1512.0, 1960.0), d.MacInput.Moves[0]); // (756, 980) points × 2
        d.PcInput.Raise(RoamInput.Motion(0, 20));
        Assert.True(await Eventually.TrueAsync(() => d.Pc.Core.Roam.Mode == RoamMode.Home));
        Assert.Equal((2560.0, 1.0), d.PcInput.Unparked!.Value);

        // A later edit on the Mac wins on both.
        d.Mac.Core.Roam.SetRoomGroups(new[] { new RoomGroup(Mac, 0, 0), new RoomGroup(Pc, 302, -141) });
        Assert.True(await Eventually.TrueAsync(() => SameRoom(d) && d.Pc.Core.Roam.RoomLayout!.By == Mac));
        Assert.Equal(RoamSide.Left, d.Pc.Core.Roam.StatusFor(Mac)!.Side);
    }

    [Fact]
    public async Task Pushing_an_empty_edge_moves_the_macbook_s_group_there_and_undo_puts_it_back()
    {
        await using var d = await DeskAsync();
        Assert.True(await Eventually.TrueAsync(() => SameRoom(d)));
        RoamLearnedNotice? notice = null;
        d.Pc.Core.Roam.SideLearned += n => notice = n;
        // The MacBook is on the left; push the G9's right edge three times.
        for (var i = 0; i < 3; i++)
        {
            d.PcInput.Raise(RoamInput.Pointer(5160, 700, false));
            d.PcInput.Raise(RoamInput.Pointer(5000, 700, false));
        }
        Assert.Equal(new RoamLearnedNotice(Mac, Mac, RoamSide.Right), notice);
        var learned = d.Pc.Core.Roam.RoomLayout!;
        Assert.True(learned.Rev > 0);
        Assert.Equal(new RoomGroup(Mac, 302 + 1196, -267), learned.Find(Mac)); // touching my group, centred: −337 + 168.5 − 98 = −266.5 → −267
        Assert.True(await Eventually.TrueAsync(() => SameRoom(d)));
        Assert.Equal(RoamSide.Right, d.Pc.Core.Roam.StatusFor(Mac)!.Side);
        // The next push goes across, in the middle of the edge.
        d.PcInput.Raise(RoamInput.Pointer(5125, 720, false));
        Assert.Equal(RoamMode.Away, d.Pc.Core.Roam.Mode);
        d.PcInput.Raise(RoamInput.Motion(-30, 0));
        Assert.True(await Eventually.TrueAsync(() => d.Pc.Core.Roam.Mode == RoamMode.Home));
        // Undo: back on the left, as a newer rev both computers keep.
        d.Pc.Core.Roam.UndoLearned();
        Assert.Equal(new RoomGroup(Mac, 0, -196), d.Pc.Core.Roam.RoomLayout!.Find(Mac));
        Assert.True(d.Pc.Core.Roam.RoomLayout!.Rev > learned.Rev);
        Assert.True(await Eventually.TrueAsync(() => SameRoom(d)));
        Assert.Equal(RoamSide.Left, d.Pc.Core.Roam.StatusFor(Mac)!.Side);
    }

    [Fact]
    public async Task Portals_only_with_computers_that_speak_the_room_and_a_newcomer_is_announced_once()
    {
        var joined = new List<string>();
        var pcIn = new FakeRoamPlatform();
        var root = Path.Combine(Path.GetTempPath(), "hib-tests", Guid.NewGuid().ToString("N")[..8]);
        await using (var pc = new TestHost(name: Pc, space: RoamTests.FastSpace(), root: root, roam: new RoamOptions { Platform = () => pcIn }) { KeepRoot = true })
        {
            pc.Core.Roam.ComputerJoined += c => { lock (joined) joined.Add(c.Id); };
            pc.Core.SpaceEngine.SetSpaceKey(RoamTests.SpaceKey);
            pc.Core.Roam.SetDisplays(new[] { G9 });
            await using var mac = await RawMac.DialAsync(pc.Core.Link.Port, RoamTests.SpaceKey, Mac);
            await mac.SendRawAsync(RoamUiTests.MacHello);
            Assert.True(await Eventually.TrueAsync(() => pc.Core.Roam.StatusFor(Mac) is { Ready: true }));
            // An older Mac (no roomLayout): §9.2 edges, no "Arrange your desk"; it's still placed and told about the room.
            Assert.False(pc.Core.Roam.InRoom(Mac));
            Assert.Equal(new RoomGroup(Mac, 0, -246), pc.Core.Roam.RoomLayout?.Find(Mac)); // 982 points × 0.25 mm, whole mm
            Assert.True(await Eventually.TrueAsync(() => mac.Of("roomLayout").Count > 0));
            Assert.Equal(0, mac.Of("roomLayout")[0].GetProperty("rev").GetInt64());
            await Task.Delay(200);
            lock (joined) Assert.Empty(joined);
            // Once it speaks roomLayout, its arrangement wins (rev 0, lower id), the portals take over and the room opens once.
            await mac.SendRawAsync("""{"type":"roomLayout","data":{"v":1,"rev":0,"by":"Haitham’s MacBook","groups":[{"computer":"Haitham’s MacBook","x":0,"y":-246},{"computer":"Office PC","x":378,"y":-92.5}]}}""");
            Assert.True(await Eventually.TrueAsync(() => pc.Core.Roam.InRoom(Mac)));
            Assert.Equal(new RoomGroup(Pc, 378, -93), pc.Core.Roam.RoomLayout!.Find(Pc)); // whole mm
            Assert.True(await Eventually.TrueAsync(() => { lock (joined) return joined.Count == 1; }));
            Assert.Equal(Mac, joined[0]);
            // One that loses (same rev, a higher `by`) is answered with the winner, so both end up the same.
            var told = mac.Of("roomLayout").Count;
            await mac.SendRawAsync("""{"type":"roomLayout","data":{"v":1,"rev":0,"by":"Zed","groups":[{"computer":"Office PC","x":0,"y":0}]}}""");
            Assert.True(await Eventually.TrueAsync(() => mac.Of("roomLayout").Count > told));
            Assert.Equal(Mac, mac.Of("roomLayout").Last().GetProperty("by").GetString());
            Assert.Equal(new RoomGroup(Pc, 378, -93), pc.Core.Roam.RoomLayout!.Find(Pc));
            // An older copy of this PC's own layout (relayed back) is never taken.
            pc.Core.Roam.SetRoomGroups(new[] { new RoomGroup(Pc, 0, 0), new RoomGroup(Mac, 447, -246) });
            var mine = pc.Core.Roam.RoomLayout!;
            await mac.SendRawAsync("{\"type\":\"roomLayout\",\"data\":{\"v\":1,\"rev\":" + mine.Rev + ",\"by\":\"Office PC\",\"groups\":[{\"computer\":\"Office PC\",\"x\":5,\"y\":5}]}}");
            await Task.Delay(300);
            Assert.Equal(new RoomGroup(Pc, 0, 0), pc.Core.Roam.RoomLayout!.Find(Pc));
        }
        await using (var again = new TestHost(name: Pc, space: RoamTests.FastSpace(), root: root, roam: new RoamOptions { Platform = () => pcIn }))
        {
            again.Core.Roam.ComputerJoined += c => { lock (joined) joined.Add(c.Id); };
            again.Core.SpaceEngine.SetSpaceKey(RoamTests.SpaceKey);
            again.Core.Roam.SetDisplays(new[] { G9 });
            await using var mac = await RawMac.DialAsync(again.Core.Link.Port, RoamTests.SpaceKey, Mac);
            await mac.SendRawAsync(RoamUiTests.MacHello);
            await mac.SendRawAsync("""{"type":"roomLayout","data":{"v":1,"rev":0,"by":"Haitham’s MacBook","groups":[{"computer":"Haitham’s MacBook","x":0,"y":-246},{"computer":"Office PC","x":378,"y":-93}]}}""");
            Assert.True(await Eventually.TrueAsync(() => again.Core.Roam.InRoom(Mac)));
            await Task.Delay(300);
            Assert.Single(joined); // seen before: no second "Arrange your desk"
            Assert.Equal(new RoomGroup(Mac, 447, -246), again.Core.Roam.RoomLayout!.Find(Mac)); // the arranged room was remembered and wins
            Assert.True(await Eventually.TrueAsync(() => mac.Of("roomLayout").Any(l => l.GetProperty("by").GetString() == Pc)));
        }
    }
}
