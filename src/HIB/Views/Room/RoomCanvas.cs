// H.I.B Room view (docs/SPACE.md §10.4), the desk itself — drawn like the Mac's RoomCanvas (RoomView.swift):
// a front view with every display at true proportions (a dark bezel around a glass panel, its name and
// resolution inside), a MacBook as a laptop on its thin base, external monitors on a neck and foot, a lit
// desk line, glowing cyan segments where two computers touch (where the mouse crosses) and a label under
// each group ("This PC" badge; amber "Not connected to anything"). Drag a display or a label to move
// that computer's group; it snaps within 40 mm; the scale holds still while dragging and refits after.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using HIB.Core.Roam;
using HIB.Core.Space;
using HIB.ViewModels;
using HIB.Views.Flick;

namespace HIB.Views.Room;

public sealed class RoomCanvas : Control
{
    /// <summary>Room reserved at the right for the phone chips.</summary>
    public const double PhonesWidth = 196;
    /// <summary>A laptop's base under its screen (mm).</summary>
    public const double LaptopBase = 9;
    /// <summary>The shortest stand under a monitor that sits on the desk (mm).</summary>
    public const double StandMin = 26;
    public const double FootHeight = 7;

    public static readonly Color Amber = Color.FromRgb(0xFF, 0xB8, 0x42);
    static readonly Color DeepBlue = Color.FromRgb(0x1C, 0x3F, 0x9E);
    static readonly Typeface Semi = new("fonts:Inter#Inter", FontStyle.Normal, FontWeight.SemiBold);
    static readonly Typeface Bold = new("fonts:Inter#Inter", FontStyle.Normal, FontWeight.Bold);
    static readonly Geometry LaptopGlyph = Geometry.Parse(Icons.Laptop);
    static readonly Geometry MonitorGlyph = Geometry.Parse(Icons.Monitor);
    static readonly Geometry WarningGlyph = Geometry.Parse("M10.29 3.86 L1.82 18 a2 2 0 0 0 1.71 3 h16.94 a2 2 0 0 0 1.71 -3 L13.71 3.86 a2 2 0 0 0 -3.42 0 Z M12 9 v4 M12 17 h0.01");
    static readonly Geometry UpdateGlyph = Geometry.Parse("M2 12 A10 10 0 1 0 22 12 A10 10 0 1 0 2 12 Z M12 8 v8 M8 12 l4 4 l4 -4");

    /// <summary>Room millimetres → view points.</summary>
    public readonly record struct Fit(double Scale, double Ox, double Oy)
    {
        public Point P(double x, double y) => new(Ox + x * Scale, Oy + y * Scale);
        public Rect R(RoamRect r) => new(Ox + r.X * Scale, Oy + r.Y * Scale, r.W * Scale, r.H * Scale);

        /// <summary>Scales the room to fit <paramref name="area"/> (at most <paramref name="maxScale"/> points per mm), centred across, standing on its bottom.</summary>
        public static Fit Make(RoamRect content, Rect area, double maxScale = 1.5)
        {
            if (content.IsEmpty || area.Width <= 0 || area.Height <= 0) return new Fit(1, area.Center.X, area.Center.Y);
            var s = Math.Min(Math.Min(area.Width / content.W, area.Height / content.H), maxScale);
            return new Fit(s, area.Center.X - (content.X + content.W / 2) * s, area.Bottom - content.Bottom * s);
        }
    }

    /// <summary>One computer's label (laid out each frame).</summary>
    public sealed record Label(string Computer, string Name, bool Laptop, bool IsMe, string? Note, bool Warning, Rect Rect);

    RoomEditor? _editor;
    Fit? _frozen;
    Fit _fit = new(1, 0, 0);
    readonly List<(string Key, string Computer, Rect Rect)> _screenHits = new();
    readonly List<Label> _labels = new();
    string? _pressId;
    Point _pressAt;
    (double X, double Y) _dragStart;
    bool _dragging;

    public RoomCanvas()
    {
        ClipToBounds = true;
        Focusable = false;
    }

    public RoomEditor? Editor
    {
        get => _editor;
        set
        {
            if (_editor is not null) _editor.Changed -= InvalidateVisual;
            _editor = value;
            if (_editor is not null) _editor.Changed += InvalidateVisual;
            InvalidateVisual();
        }
    }

    /// <summary>The scale and origin used for the last frame.</summary>
    public Fit CurrentFit => _fit;
    /// <summary>The labels of the last frame (tests).</summary>
    public IReadOnlyList<Label> Labels => _labels;
    /// <summary>Where each display was drawn in the last frame (tests).</summary>
    public IReadOnlyList<(string Key, string Computer, Rect Rect)> ScreenRects => _screenHits;

    public static double DeskY(RoomMap world)
        => world.Screens.Count == 0 ? 0 : world.Screens.Max(s => s.Mm.Bottom + (s.Spec.BuiltIn ? LaptopBase : StandMin));

    public static RoamRect ContentBounds(RoomMap world)
    {
        var b = RoamRect.Union(world.Screens.Select(s => s.Mm));
        if (b.IsEmpty) return b;
        var x = b.X - b.W * 0.02;
        return new RoamRect(x, b.Y, b.W * 1.04, DeskY(world) + FootHeight - b.Y);
    }

    static string Key(RoomScreen s) => $"{s.Computer}#{s.Index}";

    // ---------------- drawing ----------------

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        _screenHits.Clear();
        _labels.Clear();
        var editor = _editor;
        if (editor is null) return;
        var world = editor.World;
        var lonely = world.Lonely();
        var side = editor.Phones.Count > 0 ? PhonesWidth : 0;
        var area = new Rect(44, 58, Math.Max(10, size.Width - 88 - side), Math.Max(10, size.Height - 58 - 92));
        var fit = _frozen ?? Fit.Make(ContentBounds(world), area);
        _fit = fit;
        var deskY = world.Screens.Count == 0 ? size.Height * 0.7 : fit.P(0, DeskY(world)).Y;

        DrawDesk(ctx, size, deskY, world.Computers.Select(c => fit.R(world.BoundsOf(c))).ToList());
        foreach (var s in world.Screens) DrawSupport(ctx, s, fit, deskY);
        foreach (var s in world.Screens)
        {
            var r = fit.R(s.Mm);
            var lifted = editor.Dragging is { } d && SpaceIds.Same(d, s.Computer);
            var hovered = editor.Hovered == Key(s);
            DrawDisplay(ctx, s, r, hovered || lifted, lifted);
            _screenHits.Add((Key(s), s.Computer, r));
        }
        foreach (var p in world.Segments()) DrawPortal(ctx, p, fit);
        foreach (var l in LayOut(editor, world, fit, deskY, lonely, size))
        {
            _labels.Add(l);
            DrawLabel(ctx, l);
        }
        if (world.Computers.Count() < 2)
        {
            var text = Text(world.Screens.Count == 0 ? "Your displays show up here." : "When another computer joins your space, it shows up here too.",
                13, Semi, FlickTheme.Solid(Colors.White, 0.5));
            text.TextAlignment = TextAlignment.Center;
            text.MaxTextWidth = Math.Max(10, size.Width - side - 40);
            ctx.DrawText(text, new Point(20, size.Height - 40));
        }
    }

    static FormattedText Text(string s, double size, Typeface face, IBrush brush)
        => new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush);

    /// <summary>The wall, the desk line and the desk's top, lit by the screens standing on it.</summary>
    static void DrawDesk(DrawingContext ctx, Size size, double deskY, IReadOnlyList<Rect> glows)
    {
        var w = size.Width;
        var slab = Math.Min(64, Math.Max(0, size.Height - deskY) * 0.55);
        ctx.FillRectangle(FlickTheme.Linear(FlickTheme.Top, FlickTheme.Bottom, (FlickTheme.Cyan, 0.11, 0), (DeepBlue, 0.07, 1)), new Rect(0, deskY, w, slab));
        var sides = new RelativePoint(0, 0.5, RelativeUnit.Relative);
        var sidesEnd = new RelativePoint(1, 0.5, RelativeUnit.Relative);
        ctx.FillRectangle(FlickTheme.Linear(sides, sidesEnd, (Colors.White, 0, 0), (Colors.White, 0.09, 0.3), (Colors.White, 0.09, 0.7), (Colors.White, 0, 1)),
            new Rect(0, deskY + slab, w, 1));
        ctx.FillRectangle(FlickTheme.Linear(FlickTheme.Top, FlickTheme.Bottom, (FlickTheme.Ink, 0.35, 0), (FlickTheme.Ink, 0, 1)),
            new Rect(0, deskY + slab, w, Math.Max(0, size.Height - deskY - slab)));
        // Screen light spilling onto the desk.
        foreach (var box in glows)
        {
            var gw = box.Width * 1.1;
            var gh = Math.Max(12, slab * 0.9);
            var brush = new RadialGradientBrush
            {
                GradientStops = { new GradientStop(FlickTheme.A(FlickTheme.Cyan, 0.16), 0), new GradientStop(FlickTheme.A(FlickTheme.Cyan, 0), 1) },
            };
            ctx.DrawEllipse(brush, null, new Point(box.Center.X, deskY + slab * 0.3), gw / 2, gh / 2);
        }
        // The desk line, softly glowing.
        ctx.DrawRectangle(FlickTheme.Linear(sides, sidesEnd, (FlickTheme.Cyan, 0, 0), (FlickTheme.Cyan, 0.42, 0.25), (FlickTheme.Cyan, 0.42, 0.75), (FlickTheme.Cyan, 0, 1)),
            null, new RoundedRect(new Rect(0, deskY, w, 1)), new BoxShadows(new BoxShadow { Blur = 12, Color = FlickTheme.A(FlickTheme.Cyan, 0.35) }));
    }

    /// <summary>A laptop's base, or a monitor's neck and foot down to the desk line (behind every screen).</summary>
    static void DrawSupport(DrawingContext ctx, RoomScreen s, Fit fit, double deskY)
    {
        var r = fit.R(s.Mm);
        if (s.Spec.BuiltIn)
        {
            var h = Math.Max(4, LaptopBase * fit.Scale);
            var w = r.Width * 1.13;
            var top = r.Bottom - 0.5;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                // A thin wedge: wider than the screen, rounded at the front.
                c.BeginFigure(new Point(r.Center.X - w / 2 + h * 0.3, top), true);
                c.LineTo(new Point(r.Center.X + w / 2 - h * 0.3, top));
                c.LineTo(new Point(r.Center.X + w / 2, top + h * 0.7));
                c.QuadraticBezierTo(new Point(r.Center.X + w / 2, top + h), new Point(r.Center.X + w / 2 - h * 0.4, top + h));
                c.LineTo(new Point(r.Center.X - w / 2 + h * 0.4, top + h));
                c.QuadraticBezierTo(new Point(r.Center.X - w / 2, top + h), new Point(r.Center.X - w / 2, top + h * 0.7));
                c.EndFigure(true);
            }
            ctx.DrawGeometry(FlickTheme.Linear(FlickTheme.Top, FlickTheme.Bottom, (Color.FromRgb(158, 162, 172), 1, 0), (Color.FromRgb(66, 68, 78), 1, 1)), null, g);
            // The notch to open the lid.
            ctx.DrawRectangle(FlickTheme.Solid(Colors.Black, 0.35), null,
                new RoundedRect(new Rect(r.Center.X - w * 0.065, top, w * 0.13, h * 0.38), h * 0.12));
            return;
        }
        var neckW = Math.Clamp(r.Width * 0.055, 6, 32);
        var footH = Math.Max(3, FootHeight * fit.Scale);
        var neckH = Math.Max(0, deskY - r.Bottom);
        if (neckH > 1)
        {
            var neck = new Rect(r.Center.X - neckW / 2, r.Bottom, neckW, neckH);
            ctx.DrawRectangle(FlickTheme.Linear(new RelativePoint(0, 0.5, RelativeUnit.Relative), new RelativePoint(1, 0.5, RelativeUnit.Relative),
                    (Color.FromRgb(56, 56, 60), 1, 0), (Color.FromRgb(31, 31, 34), 1, 1)),
                new Pen(FlickTheme.Solid(Colors.White, 0.07), 0.6), new RoundedRect(neck, 2));
        }
        var footW = Math.Clamp(r.Width * 0.3, 40, 190);
        var foot = new Rect(r.Center.X - footW / 2, deskY - 1, footW, footH);
        ctx.DrawRectangle(FlickTheme.Linear(FlickTheme.Top, FlickTheme.Bottom, (Color.FromRgb(66, 66, 70), 1, 0), (Color.FromRgb(26, 26, 30), 1, 1)),
            new Pen(FlickTheme.Solid(Colors.White, 0.08), 0.6), new RoundedRect(foot, footH / 2));
    }

    /// <summary>One display: a dark bezel around a glass panel, the name and resolution inside.</summary>
    static void DrawDisplay(DrawingContext ctx, RoomScreen s, Rect r, bool highlighted, bool lifted)
    {
        var builtIn = s.Spec.BuiltIn;
        var shortSide = Math.Min(r.Width, r.Height);
        var bezel = builtIn ? Math.Max(2.5, shortSide * 0.05) : Math.Max(1.6, shortSide * 0.024);
        var radius = builtIn ? Math.Max(3, shortSide * 0.06) : Math.Max(2, shortSide * 0.035);
        var inner = Math.Max(1, radius - bezel * 0.6);
        var shadow = highlighted
            ? new BoxShadow { Blur = 28, OffsetY = lifted ? 10 : 4, Color = FlickTheme.A(FlickTheme.Cyan, 0.45) }
            : new BoxShadow { Blur = 18, OffsetY = 4, Color = FlickTheme.A(Colors.Black, 0.45) };
        ctx.DrawRectangle(FlickTheme.Linear(FlickTheme.Top, FlickTheme.Bottom, (Color.FromRgb(31, 31, 33), 1, 0), (Color.FromRgb(10, 10, 12), 1, 1)),
            null, new RoundedRect(r, radius), new BoxShadows(shadow));

        var panel = r.Deflate(bezel);
        if (panel.Width > 0 && panel.Height > 0)
        {
            using (ctx.PushClip(new RoundedRect(panel, inner)))
            {
                ctx.FillRectangle(FlickTheme.Linear(FlickTheme.TopLeft, FlickTheme.BottomRight, (Color.FromRgb(0x1A, 0x2B, 0x4D), 1, 0), (FlickTheme.Panel, 1, 1)), panel);
                var cyanGlow = new RadialGradientBrush
                {
                    Center = new RelativePoint(0, 0, RelativeUnit.Relative),
                    GradientOrigin = new RelativePoint(0, 0, RelativeUnit.Relative),
                    RadiusX = new RelativeScalar(0.9, RelativeUnit.Relative),
                    RadiusY = new RelativeScalar(1.1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(FlickTheme.A(FlickTheme.Cyan, highlighted ? 0.3 : 0.2), 0), new GradientStop(FlickTheme.A(FlickTheme.Cyan, 0), 1) },
                };
                ctx.FillRectangle(cyanGlow, panel);
                var deepGlow = new RadialGradientBrush
                {
                    Center = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientOrigin = new RelativePoint(1, 1, RelativeUnit.Relative),
                    RadiusX = new RelativeScalar(0.8, RelativeUnit.Relative),
                    RadiusY = new RelativeScalar(1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(FlickTheme.A(DeepBlue, 0.22), 0), new GradientStop(FlickTheme.A(DeepBlue, 0), 1) },
                };
                ctx.FillRectangle(deepGlow, panel);
                ctx.FillRectangle(FlickTheme.Linear(FlickTheme.Top, new RelativePoint(0.5, 0.5, RelativeUnit.Relative), (Colors.White, 0.07, 0), (Colors.White, 0, 1)), panel);
                if (builtIn)
                {
                    var line = Math.Max(2.5, r.Height * 0.03);
                    ctx.FillRectangle(FlickTheme.Solid(Colors.White, 0.12), new Rect(panel.X, panel.Bottom - line, panel.Width, line));
                }
                else
                {
                    // The chin with its little row of dots.
                    var chin = Math.Max(4, r.Height * 0.05);
                    ctx.FillRectangle(FlickTheme.Solid(FlickTheme.Ink, 0.7), new Rect(panel.X, panel.Bottom - chin, panel.Width, chin));
                    var dot = Math.Max(2.5, r.Height * 0.022);
                    for (var i = 0; i < 4; i++)
                        ctx.DrawRectangle(FlickTheme.Solid(Colors.White, 0.4), null,
                            new RoundedRect(new Rect(panel.Center.X - 2 * dot * 1.6 + i * dot * 1.6 + dot * 0.3, panel.Bottom - chin / 2 - dot / 2, dot, dot), 1));
                }
                DrawScreenText(ctx, s, panel);
            }
        }
        var stroke = highlighted ? new Pen(FlickTheme.Solid(FlickTheme.Cyan), 1.6) : new Pen(FlickTheme.Solid(Colors.White, 0.14), 0.8);
        ctx.DrawRectangle(null, stroke, new RoundedRect(r.Deflate(stroke.Thickness / 2), radius));
    }

    /// <summary>"Odyssey OLED G9 · 5120×1440" on one line when it fits, else the name over the resolution.</summary>
    static void DrawScreenText(DrawingContext ctx, RoomScreen s, Rect panel)
    {
        var font = Math.Min(14, Math.Max(8.5, panel.Height * 0.11));
        var name = string.IsNullOrWhiteSpace(s.Spec.Name) ? (s.Spec.BuiltIn ? "Built-in display" : "Display") : s.Spec.Name;
        var res = Resolution(s.Spec);
        var white = FlickTheme.Solid(Colors.White, 0.92);
        var cyan = FlickTheme.Solid(FlickTheme.Cyan, 0.85);
        var dim = FlickTheme.Solid(Colors.White, 0.35);
        var n = Text(name, font, Semi, white);
        var dot = Text(" · ", font, Semi, dim);
        var rz = Text(res, font, Semi, cyan);
        var room = panel.Width - 16;
        var dotW = dot.WidthIncludingTrailingWhitespace;
        if (n.Width + dotW + rz.Width <= room && panel.Height > font * 1.6)
        {
            var total = n.Width + dotW + rz.Width;
            var x = panel.Center.X - total / 2;
            var y = panel.Center.Y - n.Height / 2;
            ctx.DrawText(n, new Point(x, y));
            ctx.DrawText(dot, new Point(x + n.Width, y));
            ctx.DrawText(rz, new Point(x + n.Width + dotW, y));
            return;
        }
        if (room <= 8 || panel.Height < font * 1.4) return;
        n.MaxTextWidth = room;
        n.MaxLineCount = 2;
        n.Trimming = TextTrimming.CharacterEllipsis;
        n.TextAlignment = TextAlignment.Center;
        rz.MaxTextWidth = room;
        rz.MaxLineCount = 1;
        rz.TextAlignment = TextAlignment.Center;
        var showRes = panel.Height > n.Height + rz.Height + 6;
        var h = n.Height + (showRes ? rz.Height + 2 : 0);
        var top = panel.Center.Y - h / 2;
        ctx.DrawText(n, new Point(panel.X + 8, top));
        if (showRes) ctx.DrawText(rz, new Point(panel.X + 8, top + n.Height + 2));
    }

    public static string Resolution(RoomScreenSpec s)
    {
        var w = s.PxW > 0 ? s.PxW : (int)Math.Round(s.W * s.Scale);
        var h = s.PxH > 0 ? s.PxH : (int)Math.Round(s.H * s.Scale);
        return $"{w}×{h}";
    }

    /// <summary>Where the mouse crosses: a glowing cyan line along the touching stretch, half way between the two edges.</summary>
    static void DrawPortal(DrawingContext ctx, RoomPortal p, Fit fit)
    {
        var vertical = RoamSides.IsHorizontal(p.Side); // a left/right edge is a vertical line
        var a = fit.P(vertical ? p.At : p.Start, vertical ? p.Start : p.At);
        var b = fit.P(vertical ? p.At : p.End, vertical ? p.End : p.At);
        var length = vertical ? b.Y - a.Y : b.X - a.X;
        if (length <= 0) return;
        Rect Bar(double thick, double extra) => vertical
            ? new Rect(a.X - thick / 2, a.Y - extra / 2, thick, length + extra)
            : new Rect(a.X - extra / 2, a.Y - thick / 2, length + extra, thick);
        ctx.DrawRectangle(FlickTheme.Solid(FlickTheme.Cyan, 0.28), null, new RoundedRect(Bar(12, 6), 6));
        var along0 = vertical ? FlickTheme.Top : new RelativePoint(0, 0.5, RelativeUnit.Relative);
        var along1 = vertical ? FlickTheme.Bottom : new RelativePoint(1, 0.5, RelativeUnit.Relative);
        ctx.DrawRectangle(FlickTheme.Linear(along0, along1, (FlickTheme.Cyan, 0.85, 0), (FlickTheme.Cyan, 1, 0.5), (FlickTheme.Cyan, 0.85, 1)), null,
            new RoundedRect(Bar(3.5, 0), 1.75), new BoxShadows(new BoxShadow { Blur = 12, Color = FlickTheme.A(FlickTheme.Cyan, 0.95) }));
    }

    // ---------------- labels ----------------

    /// <summary>
    /// Under each group: in the row under the desk for groups on the desk, just under a raised laptop, above a group that has another
    /// one right under it. Labels sharing a row push apart sideways (the Mac's labels()).
    /// </summary>
    IReadOnlyList<Label> LayOut(RoomEditor editor, RoomMap world, Fit fit, double deskY, IReadOnlyList<string> lonely, Size canvas)
    {
        var slots = new List<Label>();
        var screens = world.Screens.Select(s => (s.Computer, R: fit.R(s.Mm))).ToList();
        var footH = Math.Max(3, FootHeight * fit.Scale);
        var nameRow = deskY + footH + 12;
        var order = world.Computers.Select(id => (Id: id, Box: fit.R(world.BoundsOf(id))))
            .OrderByDescending(x => Math.Round(x.Box.Bottom * 2) / 2).ThenBy(x => x.Box.Center.X).ToList();
        foreach (var (id, box) in order)
        {
            var c = editor.Computer(id);
            if (c is null) continue;
            var isMe = SpaceIds.Same(id, editor.Me);
            string? note = null;
            var warning = false;
            if (lonely.Any(x => SpaceIds.Same(x, id)))
            {
                note = "Not connected to anything";
                warning = true;
            }
            else if (!isMe && !editor.Speaking.Contains(id)) note = "Update H.I.B there to use this";
            var (mainW, noteW) = LabelWidths(c.Name, isMe, c.IsMac, note);
            var height = note is null ? 24 : 42;
            var width = Math.Max(mainW, noteW);
            var hasStand = world.Of(id).Any(s => !s.Spec.BuiltIn);
            var laptopBase = box.Bottom + LaptopBase * fit.Scale + 10;
            var top = hasStand || deskY - laptopBase < 40 ? nameRow : laptopBase;
            Rect At(double t) => new(box.Center.X - width / 2, t, width, height);
            bool UnderScreen(Rect r) => screens.Any(s => !SpaceIds.Same(s.Computer, id) && s.R.Intersects(r));
            if (UnderScreen(At(top)))
            {
                var above = box.Y - 10 - height;
                if (!UnderScreen(At(above)) && above > 4) top = above;
                else while (UnderScreen(At(top)) && top < canvas.Height) top += 8;
            }
            slots.Add(new Label(id, c.Name, world.Of(id).Any(s => s.Spec.BuiltIn), isMe, note, warning, At(top)));
        }
        // Same row: left to right, no overlaps, inside the canvas.
        foreach (var row in slots.Select((l, i) => (l, i)).GroupBy(x => (int)Math.Round(x.l.Rect.Y / 6)))
        {
            var sorted = row.OrderBy(x => x.l.Rect.Center.X).Select(x => x.i).ToList();
            for (var n = 0; n < sorted.Count; n++)
            {
                var r = slots[sorted[n]].Rect;
                var x = Math.Max(r.X, 10);
                if (n > 0 && x < slots[sorted[n - 1]].Rect.Right + 14) x = slots[sorted[n - 1]].Rect.Right + 14;
                slots[sorted[n]] = slots[sorted[n]] with { Rect = r.WithX(x) };
            }
            if (sorted.Count > 0 && slots[sorted[^1]].Rect.Right > canvas.Width - 10)
            {
                var shift = slots[sorted[^1]].Rect.Right - (canvas.Width - 10);
                foreach (var i in sorted) slots[i] = slots[i] with { Rect = slots[i].Rect.WithX(slots[i].Rect.X - shift) };
            }
        }
        return slots;
    }

    static (double Main, double Note) LabelWidths(string name, bool isMe, bool isMac, string? note)
    {
        var n = Text(name, 12.5, Semi, Brushes.White);
        var main = 13 + 6 + Math.Min(n.Width, 260) + (isMe ? 6 + Text(isMac ? "This Mac" : "This PC", 9.5, Bold, Brushes.White).Width + 14 : 0) + 4;
        var noteW = note is null ? 0 : 11 + 4 + Text(note, 11, Semi, Brushes.White).Width + 4;
        return (main, noteW);
    }

    static void DrawLabel(DrawingContext ctx, Label l)
    {
        var cyan = FlickTheme.Solid(FlickTheme.Cyan);
        var name = Text(l.Name, 12.5, Semi, Brushes.White);
        name.MaxTextWidth = 260;
        name.MaxLineCount = 1;
        name.Trimming = TextTrimming.CharacterEllipsis;
        var (mainW, _) = LabelWidths(l.Name, l.IsMe, false, null);
        var x = l.Rect.Center.X - mainW / 2;
        var y = l.Rect.Y + 3;
        Glyph(ctx, l.Laptop ? LaptopGlyph : MonitorGlyph, new Point(x, y + 2), 13, cyan, 2.2);
        x += 13 + 6;
        ctx.DrawText(name, new Point(x, y));
        x += Math.Min(name.Width, 260) + 6;
        if (l.IsMe)
        {
            var badge = Text("This PC", 9.5, Bold, FlickTheme.Solid(FlickTheme.Lime));
            var chip = new Rect(x, y + 0.5, badge.Width + 14, badge.Height + 5);
            ctx.DrawRectangle(FlickTheme.Solid(FlickTheme.Lime, 0.14), new Pen(FlickTheme.Solid(FlickTheme.Lime, 0.3), 0.6), new RoundedRect(chip, chip.Height / 2));
            ctx.DrawText(badge, new Point(chip.X + 7, chip.Y + 2.5));
        }
        if (l.Note is { } note)
        {
            var brush = l.Warning ? FlickTheme.Solid(Amber) : FlickTheme.Solid(Colors.White, 0.55);
            var t = Text(note, 11, Semi, brush);
            var w = 11 + 4 + t.Width;
            var nx = l.Rect.Center.X - w / 2;
            var ny = l.Rect.Y + 24;
            Glyph(ctx, l.Warning ? WarningGlyph : UpdateGlyph, new Point(nx, ny + 1.5), 11, brush, 2.4);
            ctx.DrawText(t, new Point(nx + 15, ny));
        }
    }

    static void Glyph(DrawingContext ctx, Geometry g, Point at, double size, IBrush brush, double stroke)
    {
        var s = size / 24.0;
        using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(at.X, at.Y)))
            ctx.DrawGeometry(null, new Pen(brush, stroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), g);
    }

    // ---------------- dragging ----------------

    /// <summary>The computer whose display or label is under <paramref name="p"/> (the topmost), with the display's key.</summary>
    public (string Computer, string? Key)? HitTest(Point p)
    {
        for (var i = _screenHits.Count - 1; i >= 0; i--)
            if (_screenHits[i].Rect.Contains(p)) return (_screenHits[i].Computer, _screenHits[i].Key);
        foreach (var l in _labels)
            if (l.Rect.Contains(p)) return (l.Computer, null);
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_editor is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        if (HitTest(p) is not { } hit) return;
        BeginPress(hit.Computer, p);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_pressId is not null)
        {
            MoveTo(p);
            return;
        }
        if (_editor is null) return;
        var hit = HitTest(p);
        var key = hit?.Key;
        Cursor = hit is not null ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
        if (_editor.Hovered != key)
        {
            _editor.Hovered = key;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndPress();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndPress();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_editor is { Hovered: not null } ed && _pressId is null)
        {
            ed.Hovered = null;
            InvalidateVisual();
        }
    }

    /// <summary>Starts a press on a computer's group at view point <paramref name="p"/> (tests drive these three directly).</summary>
    public void BeginPress(string computer, Point p)
    {
        if (_editor?.OffsetOf(computer) is not { } o) return;
        _pressId = computer;
        _pressAt = p;
        _dragStart = o;
        _dragging = false;
    }

    public void MoveTo(Point p)
    {
        if (_editor is null || _pressId is not { } id) return;
        if (!_dragging)
        {
            if (Math.Abs(p.X - _pressAt.X) + Math.Abs(p.Y - _pressAt.Y) < 2) return;
            _dragging = true;
            _frozen = _fit; // the scale stays put while a group is in the hand
            _editor.BeginDrag(id);
        }
        var s = _frozen is { Scale: > 0 } f ? f.Scale : 1.0;
        _editor.Drag(id, _dragStart.X + (p.X - _pressAt.X) / s, _dragStart.Y + (p.Y - _pressAt.Y) / s);
    }

    public void EndPress()
    {
        if (_pressId is null) return;
        var dragged = _dragging;
        _pressId = null;
        _dragging = false;
        _frozen = null; // refit after the drop
        if (dragged) _editor?.EndDrag();
        InvalidateVisual();
    }
}
