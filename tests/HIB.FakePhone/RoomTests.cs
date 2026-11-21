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
        // A new rev is the clock, but always past the current.
        var now = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTimeOffset(now).ToUnixTimeMilliseconds(), RoomLayout.NextRev(older, now));
        Assert.Equal(l.Rev + 1, RoomLayout.NextRev(l with { Rev = long.MaxValue / 2 }, now) - (long.MaxValue / 2 - l.Rev));
    }

    [Fact]
    public void Auto_place_puts_the_mac_first_and_the_others_beside_it_on_one_desk_line()
    {
        var computers = new[] { PcComputer(), MacComputer() };
        // Nothing known: the Mac first, the PC to its right, bottoms aligned.
        var l = RoomGeometry.AutoPlace(computers, (_, _) => null, Pc);
        Assert.Equal(0, l.Rev);
        Assert.Equal(Pc, l.By);
        Assert.Equal(new RoomGroup(Mac, 0, 0), l.Find(Mac));
        Assert.Equal(new RoomGroup(Pc, 302, -141), l.Find(Pc)); // 196 − 337
        var room = RoomGeometry.Build(computers, l);
        Assert.Equal(room.BoundsOf(Mac).Bottom, room.BoundsOf(Pc).Bottom);

        // The side function says the PC is left of the Mac / the Mac is above the PC.
        var left = RoomGeometry.AutoPlace(computers, (a, b) => a == Mac && b == Pc ? RoamSide.Left : null, Pc);
        Assert.Equal(new RoomGroup(Pc, -1196, -141), left.Find(Pc));
        var below = RoomGeometry.AutoPlace(computers, (a, b) => a == Mac && b == Pc ? RoamSide.Below : null, Pc);
        Assert.Equal(new RoomGroup(Pc, 151 - 598, 196), below.Find(Pc)); // centred under the MacBook
        // Without a Mac the lowest id anchors; three computers chain to the right.
        var three = RoomGeometry.AutoPlace(new[] { StudioComputer(), PcComputer() }, (_, _) => null, Studio);
        Assert.Equal(new RoomGroup(Pc, 0, 0), three.Find(Pc));
        Assert.Equal(new RoomGroup(Studio, 1196, 337 - 270), three.Find(Studio));
    }

    [Fact]
    public void A_newcomer_goes_beside_an_arrangement_the_user_made()
    {
        var arranged = Layout((Pc, 0, 0), (Mac, 447, -196));
        var all = new[] { PcComputer(), MacComputer(), StudioComputer() };
        var next = RoomGeometry.AddMissing(arranged, all, Pc, (_, _) => null);
        Assert.Equal(new RoomGroup(Pc, 0, 0), next.Find(Pc)); // untouched
        Assert.Equal(new RoomGroup(Mac, 447, -196), next.Find(Mac));
        Assert.Equal(new RoomGroup(Studio, 1196, 337 - 270), next.Find(Studio)); // right of everything, on the desk line
        var onLeft = RoomGeometry.AddMissing(arranged, all, Pc, (a, b) => b == Studio ? RoamSide.Left : null);
        Assert.Equal(new RoomGroup(Studio, -480, 67), onLeft.Find(Studio));
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
        // The nearest edge wins.
        (dx, _) = RoomGeometry.Snap(new[] { new RoamRect(1200, 500, 302, 196) }, new[] { g9 });
        Assert.Equal(-4, dx);
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
        Assert.Equal(981, c.Y); // the MacBook's bottom row
        // Outside the overlap there is no crossing.
        Assert.Null(room.Cross(Pc, G9.Id, RoamSide.Above, 1000, 0));
        Assert.Null(room.Cross(Pc, G9.Id, RoamSide.Right, 5119, 700));
        // Back down from the MacBook's bottom-left corner: the G9 at 447 mm.
        c = room.Cross(Mac, MacBookScreen.Id, RoamSide.Below, 0, 981)!;
        Assert.Equal(Pc, c.To.Computer);
        Assert.Equal(447 / (1196.0 / 5120), c.X, 3);
        Assert.Equal(0, c.Y);

        // Clamping: a portal shorter than the edge (the MacBook's bottom 196 mm of the G9's left edge) maps onto the
        // overlap, and the entry point is inside the target screen.
        room = RoomGeometry.Build(new[] { PcComputer(), MacComputer() }, Layout((Pc, 0, 0), (Mac, -302, 141)));
        c = room.Cross(Pc, G9.Id, RoamSide.Left, 0, 1439)!;
        Assert.Equal(1511, c.X); // the MacBook's right column
        Assert.InRange(c.Y, 980, 981);
        Assert.Equal(337, c.At, 0); // at the overlap's end
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
        Assert.Equal(981, h.Entered.Value.Y);
        // Over the MacBook's left edge: nothing there, it stops.
        c.Handle(RoamInput.Motion(-2000, -300));
        Assert.Equal(RoamMode.Away, c.Mode);
        Assert.Equal((0.0, 681.0), c.Virtual);
        // In its bottom-left corner the corner lock holds (§9.2).
        c.Handle(RoamInput.Motion(0, 400));
        Assert.Equal(RoamMode.Away, c.Mode);
        // Down off its bottom edge 100 points in: home onto the G9's top edge at the same room position, 2 px in.
        c.Handle(RoamInput.Motion(100, 400));
        Assert.Equal(RoamMode.Home, c.Mode);
        Assert.Equal(1, h.Leaves);
        Assert.Equal((447 + 100.0 / 1512 * 302) / (1196.0 / 5120), h.Uncaptured!.Value.X, 3);
        Assert.Equal(2, h.Uncaptured.Value.Y);
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
        Assert.Equal(0, h.Entered.Value.X);
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
        Assert.Equal(new RoomGroup(Mac, 0, 0), room.Find(Mac));
        Assert.Equal(new RoomGroup(Pc, 302, -141), room.Find(Pc));
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
        var at = -141 + 1300 * 337.0 / 1440;
        Assert.Equal(3022, d.MacInput.Moves[0].X, 0); // the MacBook's right column (1511 points × 2)
        Assert.Equal(at / 196 * 982 * 2, d.MacInput.Moves[0].Y, 0);
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
        Assert.Equal((1512.0, 1962.0), d.MacInput.Moves[0]); // (756, 981) points × 2
        d.PcInput.Raise(RoamInput.Motion(0, 20));
        Assert.True(await Eventually.TrueAsync(() => d.Pc.Core.Roam.Mode == RoamMode.Home));
        Assert.Equal((2560.0, 2.0), d.PcInput.Unparked!.Value);

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
        Assert.Equal(new RoomGroup(Mac, 302 + 1196, -141 + 337 / 2.0 - 98), learned.Find(Mac)); // touching, centred on the edge
        Assert.True(await Eventually.TrueAsync(() => SameRoom(d)));
        Assert.Equal(RoamSide.Right, d.Pc.Core.Roam.StatusFor(Mac)!.Side);
        // The next push goes across, in the middle of the edge.
        d.PcInput.Raise(RoamInput.Pointer(5125, 720, false));
        Assert.Equal(RoamMode.Away, d.Pc.Core.Roam.Mode);
        d.PcInput.Raise(RoamInput.Motion(-30, 0));
        Assert.True(await Eventually.TrueAsync(() => d.Pc.Core.Roam.Mode == RoamMode.Home));
        // Undo: back on the left, as a newer rev both computers keep.
        d.Pc.Core.Roam.UndoLearned();
        Assert.Equal(new RoomGroup(Mac, 0, 0), d.Pc.Core.Roam.RoomLayout!.Find(Mac));
        Assert.True(d.Pc.Core.Roam.RoomLayout!.Rev > learned.Rev);
        Assert.True(await Eventually.TrueAsync(() => SameRoom(d)));
        Assert.Equal(RoamSide.Left, d.Pc.Core.Roam.StatusFor(Mac)!.Side);
    }

    [Fact]
    public async Task A_computer_seen_for_the_first_time_is_announced_once()
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
            Assert.True(await Eventually.TrueAsync(() => { lock (joined) return joined.Count == 1; }));
            Assert.Equal(Mac, joined[0]);
            // An older Mac (no roomLayout) on a room nobody arranged: §9.2 edges, but the room still places it.
            Assert.False(pc.Core.Roam.InRoom(Mac));
            Assert.NotNull(pc.Core.Roam.RoomLayout?.Find(Mac));
            Assert.True(await Eventually.TrueAsync(() => mac.Of("roomLayout").Count > 0)); // it was told anyway
            // Once it speaks roomLayout, the room's portals take over.
            await mac.SendRawAsync("""{"type":"roomLayout","data":{"v":1,"rev":0,"by":"Haitham’s MacBook","groups":[{"computer":"Haitham’s MacBook","x":0,"y":0},{"computer":"Office PC","x":378,"y":-92.5}]}}""");
            Assert.True(await Eventually.TrueAsync(() => pc.Core.Roam.InRoom(Mac)));
            Assert.Equal(new RoomGroup(Pc, 378, -92.5), pc.Core.Roam.RoomLayout!.Find(Pc));
            // One that loses (same rev, a higher `by`) is answered with the winner, so both end up the same.
            var told = mac.Of("roomLayout").Count;
            await mac.SendRawAsync("""{"type":"roomLayout","data":{"v":1,"rev":0,"by":"Zed","groups":[{"computer":"Office PC","x":0,"y":0}]}}""");
            Assert.True(await Eventually.TrueAsync(() => mac.Of("roomLayout").Count > told));
            var answer = mac.Of("roomLayout").Last();
            Assert.Equal(Mac, answer.GetProperty("by").GetString());
            Assert.Equal(new RoomGroup(Pc, 378, -92.5), pc.Core.Roam.RoomLayout!.Find(Pc));
        }
        await using (var again = new TestHost(name: Pc, space: RoamTests.FastSpace(), root: root, roam: new RoamOptions { Platform = () => pcIn }))
        {
            again.Core.Roam.ComputerJoined += c => { lock (joined) joined.Add(c.Id); };
            again.Core.SpaceEngine.SetSpaceKey(RoamTests.SpaceKey);
            again.Core.Roam.SetDisplays(new[] { G9 });
            await using var mac = await RawMac.DialAsync(again.Core.Link.Port, RoamTests.SpaceKey, Mac);
            await mac.SendRawAsync(RoamUiTests.MacHello);
            Assert.True(await Eventually.TrueAsync(() => again.Core.Roam.StatusFor(Mac) is { Ready: true }));
            await Task.Delay(300);
            Assert.Single(joined); // seen before: no second "Arrange your desk"
            Assert.Equal(new RoomGroup(Pc, 378, -92.5), again.Core.Roam.RoomLayout!.Find(Pc)); // remembered
        }
    }
}
