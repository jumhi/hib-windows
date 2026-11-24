// H.I.B — Flick bundles on screen (SPACE.md §14). Every item of one throw lands as ONE stacked card (the usual flight
// and landing spring) that shows the whole bundle's progress. Clicking the stack fans it out into its pattern
// (dice-five for 5, 3×2 for 6 …) centred where it landed, each card on its own staggered spring; fanned cards work like
// single cards (click opens, drag out, right-click). Gather, Esc or a click on the empty space stacks them again.
// Dragging the stack drags every file at once.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using HIB.Core.Phone;
using HIB.Core.Platform;
using HIB.Services;

namespace HIB.Views.Flick;

public sealed partial class FlickCardManager
{
    sealed class StackEntry
    {
        public required FlickBundle Bundle;
        public required FlickStackView View;
        public required FlickStackWindow Window;
        public FlickBody Body;
        public PixelRect ScreenBounds;
        public double Scaling = 1;
        public Rect Visible; // DIPs, relative to ScreenBounds' origin
        public bool Hidden;
        public bool DraggingOut;
        public PointerPressedEventArgs? Press;
        public Point PressPos;
        public bool Dragged;
        public Vector Wheel;
        public DateTime WheelAt;
        public Fan? Fan;
    }

    sealed class FanCard
    {
        public required FlickCard Card;
        public required FlickCardView View;
        public FlickBody Body;
        public int Place;
        public double Delay;
        public bool Leaving;
        public bool DraggingOut;
        public PointerPressedEventArgs? Press;
        public Point PressPos;
        public bool Dragged;
    }

    sealed class Fan
    {
        public required FlickFanWindow Window;
        public required FlickFanView View;
        public required FanPlacement Placement;
        /// <summary>The fan window's rectangle, DIPs relative to the screen.</summary>
        public Rect Area;
        public readonly List<FanCard> Cards = new();
        public bool Gathering;
        public bool Settled;
        public DateTime LastActivity = DateTime.UtcNow;
    }

    /// <summary>Fan-out and gather run their springs a little faster than a landing (same damping, snappier).</summary>
    const double FanSpeed = 1.55;
    const double GatherSpeed = 1.9;
    /// <summary>Stagger between cards leaving the stack (reading order) and coming back (reverse).</summary>
    const double FanStagger = 0.045;
    const double GatherStagger = 0.03;
    /// <summary>Room around the pattern inside the fan window (springs overshoot, glows spread).</summary>
    const double FanMargin = 72;

    readonly FlickBundleRegistry _bundles = new();
    readonly Dictionary<string, StackEntry> _stacks = new();
    readonly HashSet<string> _finishedBundles = new();
    bool _escDown;
    bool _mouseDown;

    // ---------------- public (UI thread) ----------------

    /// <summary>Bundles on screen (SPACE.md §14).</summary>
    public FlickBundleRegistry Bundles => _bundles;

    /// <summary>The stacked bundles' windows (hidden while fanned).</summary>
    public IReadOnlyList<FlickStackWindow> Stacks => _stacks.Values.Select(s => s.Window).ToList();

    public FlickStackWindow? StackWindow(string bundleId) => _stacks.TryGetValue(bundleId, out var s) ? s.Window : null;

    /// <summary>The fan's window while the bundle is spread out.</summary>
    public FlickFanWindow? FanWindow(string bundleId) => _stacks.TryGetValue(bundleId, out var s) ? s.Fan?.Window : null;

    /// <summary>The bundle is spread out and its cards have settled in their places.</summary>
    public bool IsFanSettled(string bundleId) => _stacks.TryGetValue(bundleId, out var s) && s.Fan is { Settled: true, Gathering: false };

    /// <summary>Where each fanned card is now (screen DIPs) — tests and design shots.</summary>
    public IReadOnlyList<Point> FanCardCenters(string bundleId)
        => _stacks.TryGetValue(bundleId, out var s) && s.Fan is { } f
            ? f.Cards.OrderBy(c => c.Place).Select(c => new Point(f.Area.X + c.Body.X, f.Area.Y + c.Body.Y)).ToList()
            : Array.Empty<Point>();

    /// <summary>The fan's planned placement (screen DIPs).</summary>
    public FanPlacement? FanPlacementOf(string bundleId) => _stacks.TryGetValue(bundleId, out var s) ? s.Fan?.Placement : null;

    /// <summary>The stack's centre on its screen (DIPs).</summary>
    public Point? StackCenter(string bundleId) => _stacks.TryGetValue(bundleId, out var s) ? new Point(s.Body.X, s.Body.Y) : null;

    /// <summary>The files a drag of the stack carries now.</summary>
    public IReadOnlyList<string> StackDragPaths(string bundleId) => _bundles.Get(bundleId)?.DragPaths ?? Array.Empty<string>();

    /// <summary>Clicking the stack: spread it out.</summary>
    public void FanOut(string bundleId)
    {
        if (_stacks.TryGetValue(bundleId, out var s)) FanOut(s);
    }

    /// <summary>Gather / Esc / a click on the empty space: stack the cards again.</summary>
    public void Gather(string bundleId)
    {
        if (_stacks.TryGetValue(bundleId, out var s)) Gather(s);
    }

    public void GatherAll()
    {
        foreach (var s in _stacks.Values.ToList()) Gather(s);
    }

    /// <summary>Esc while a bundle is spread out.</summary>
    public void HandleEscape() => GatherAll();

    // ---------------- protocol (UI thread) ----------------

    /// <summary>A flickStart with <c>bundle</c>: join (or start) its stack. False: not a usable bundle — a single card.</summary>
    bool OnBundleFlick(FlickInfo f, string kind, string name, Bitmap? thumb)
    {
        var tag = f.Bundle!;
        if (_finishedBundles.Contains(tag.Id)) return true; // the stack already settled away
        if (_bundles.ForFlick(f.Id) is { } known)
        {
            // A repeated flickStart (or one that followed its transfer): enrich the item.
            if (known.Items.FirstOrDefault(c => c?.Id == f.Id) is { } item)
            {
                item.Kind = kind;
                if (string.IsNullOrEmpty(item.Name) || item.Name == "Flick") item.Name = name;
                item.Thumbnail ??= thumb;
                item.Text ??= f.Text;
                item.From ??= f.From;
            }
            RefreshBundle(known.Id);
            return true;
        }

        var needsTransfer = kind is not ("link" or "text");
        var card = new FlickCard(f.Id, kind, name, f.Mime ?? "", f.Size, needsTransfer ? FlickTransfer.Waiting : FlickTransfer.None)
        {
            Thumbnail = thumb,
            Text = f.Text,
            From = f.From,
        };
        // The transfer beat its flickStart and brought a single card in: that card joins the stack instead.
        if (_cards.TryGetValue(f.Id, out var single))
        {
            var c = single.Card;
            card.Transfer = c.Transfer;
            card.Progress = c.Progress;
            card.FilePath = c.FilePath;
            card.FailReason = c.FailReason;
            if (c.Size > 0) card.Size = c.Size;
            card.Thumbnail ??= c.Thumbnail;
        }
        var accepted = _bundles.Accept(tag, card);
        if (accepted is null) return false;
        if (single is not null)
        {
            _cards.Remove(f.Id);
            _order.Remove(f.Id);
            try { single.Window.Close(); } catch { }
        }
        var (bundle, created) = accepted.Value;
        bundle.LastActivity = DateTime.UtcNow;
        if (created) SpawnStack(bundle, f);
        else RefreshBundle(bundle.Id);
        return true;
    }

    bool OnBundleProgress(TransferProgress p)
    {
        if (_bundles.ForFlick(p.FlickId!) is not { } bundle) return false;
        if (bundle.Items.FirstOrDefault(c => c?.Id == p.FlickId) is not { } c) return true;
        if (p.Failed) return true; // OnFailed explains why
        if (c.Transfer == FlickTransfer.Waiting) c.Transfer = FlickTransfer.Receiving;
        if (c.Transfer == FlickTransfer.Receiving)
        {
            c.Size = p.Size > 0 ? p.Size : c.Size;
            c.Progress = p.Size > 0 ? Math.Clamp((double)p.Received / p.Size, 0, 1) : 0;
            if ((c.Name == "Flick" || c.Name.Length == 0) && p.Name.Length > 0) c.Name = p.Name;
        }
        c.LastActivity = bundle.LastActivity = DateTime.UtcNow;
        RefreshBundle(bundle.Id);
        return true;
    }

    bool OnBundleReceived(ReceivedFile f)
    {
        if (_bundles.ForFlick(f.FlickId!) is not { } bundle) return false;
        if (bundle.Items.FirstOrDefault(c => c?.Id == f.FlickId) is not { } c) return true;
        c.FilePath = f.Path;
        c.Name = f.Name;
        c.Size = f.Size;
        c.From ??= f.From;
        bundle.From ??= f.From;
        c.Progress = 1;
        c.Transfer = FlickTransfer.Done;
        c.LastActivity = bundle.LastActivity = DateTime.UtcNow;
        bundle.Remaining = Math.Max(bundle.Remaining, 10);
        RefreshBundle(bundle.Id);
        if (c.Thumbnail is null && c.Kind == "image" && f.Size < 60_000_000) _ = LoadThumbnailAsync(c.Id, f.Path);
        return true;
    }

    bool OnBundleFailed(string flickId, string reason)
    {
        if (_bundles.ForFlick(flickId) is not { } bundle) return false;
        if (bundle.Items.FirstOrDefault(c => c?.Id == flickId) is { } c)
        {
            c.Transfer = FlickTransfer.Failed;
            c.FailReason = reason == "Disconnected" ? "The sender disconnected" : reason;
            bundle.LastActivity = DateTime.UtcNow;
        }
        RefreshBundle(bundle.Id);
        return true;
    }

    /// <summary>Re-reads a bundle into its stack and, when spread out, its cards.</summary>
    void RefreshBundle(string bundleId)
    {
        if (!_stacks.TryGetValue(bundleId, out var s)) return;
        s.View.Refresh();
        if (s.Fan is { } fan)
            foreach (var c in fan.Cards) c.View.Refresh();
    }

    int LiveSlots()
        => _order.Count(id => _cards.TryGetValue(id, out var o) && o.Body.Mode is not (FlickBodyMode.Dismissing or FlickBodyMode.Flung))
           + _stacks.Values.Count(s => s.Body.Mode is not (FlickBodyMode.Dismissing or FlickBodyMode.Flung));

    // ---------------- the stack ----------------

    void SpawnStack(FlickBundle bundle, FlickInfo f)
    {
        var view = new FlickStackView(bundle);
        var window = new FlickStackWindow(view);
        var s = new StackEntry { Bundle = bundle, View = view, Window = window };

        Screen? screen = null;
        (double X, double Y)? dropAt = null;
        if (f.At is { } at && DropScreen(at.X, at.Y) is { } drop)
        {
            screen = drop.Screen;
            dropAt = (at.X - drop.Info.X, at.Y - drop.Info.Y);
        }
        else
        {
            try { screen = _app.Flick.PickFor(f); } catch { }
        }
        screen ??= ScreenUnderCursor(window);
        if (screen is not null)
        {
            s.ScreenBounds = screen.Bounds;
            s.Scaling = screen.Scaling > 0 ? screen.Scaling : 1;
            var wa = screen.WorkingArea;
            s.Visible = new Rect((wa.X - screen.Bounds.X) / s.Scaling, (wa.Y - screen.Bounds.Y) / s.Scaling, wa.Width / s.Scaling, wa.Height / s.Scaling);
        }
        else
        {
            s.ScreenBounds = new PixelRect(0, 0, 1920, 1080);
            s.Visible = new Rect(0, 0, 1920, 1040);
        }

        var v = s.Visible;
        var halfW = FlickCardView.CardWidth / 2 + 40;
        var halfH = FlickCardView.CardHeight / 2 + 40;
        var ratio = Math.Clamp(f.ExitXRatio, 0.05, 0.95);
        var slot = LiveSlots() % 5;
        var tx = Math.Clamp(v.X + v.Width / 2 + (ratio - 0.5) * v.Width * 0.3 + slot * 26, v.X + halfW, Math.Max(v.X + halfW, v.Right - halfW));
        var ty = Math.Clamp(v.Bottom - v.Height * 0.32 - slot * 16, v.Y + halfH, Math.Max(v.Y + halfH, v.Bottom - halfH));
        if (dropAt is { } d)
        {
            var dx = Math.Clamp(d.X, v.X + halfW, Math.Max(v.X + halfW, v.Right - halfW));
            var dy = Math.Clamp(d.Y, v.Y + halfH, Math.Max(v.Y + halfH, v.Bottom - halfH));
            s.Body = FlickBody.DropAt(dx, dy, slot);
        }
        else
        {
            s.Body = FlickBody.Launch(s.ScreenBounds.Width / s.Scaling, s.ScreenBounds.Height / s.Scaling, FlickCardView.CardHeight,
                ratio, f.Velocity, f.Angle, tx, ty, slot);
        }

        view.RenderTransform = new Avalonia.Media.TransformGroup { Children = { new Avalonia.Media.ScaleTransform(), new Avalonia.Media.RotateTransform() } };
        view.Face.IsHitTestVisible = false;
        HookStack(s);
        _stacks[bundle.Id] = s;
        PlaceStack(s);
        window.Show();
        PlaceStack(s);
        StartTicking();
    }

    void PlaceStack(StackEntry s)
    {
        var b = s.Body;
        var k = s.Scaling;
        s.Window.Position = new PixelPoint(
            (int)Math.Round(s.ScreenBounds.X + (b.X - FlickStackView.ContainerWidth / 2) * k),
            (int)Math.Round(s.ScreenBounds.Y + (b.Y - FlickStackView.ContainerHeight / 2) * k));
        if (s.View.RenderTransform is Avalonia.Media.TransformGroup g)
        {
            var scale = (Avalonia.Media.ScaleTransform)g.Children[0];
            scale.ScaleX = b.Scale;
            scale.ScaleY = b.Scale;
            ((Avalonia.Media.RotateTransform)g.Children[1]).Angle = b.Rot;
        }
        s.View.Opacity = Math.Clamp(b.Alpha, 0, 1);
    }

    void StackLanded(StackEntry s)
    {
        s.Bundle.Landed = true;
        s.Bundle.LastActivity = DateTime.UtcNow;
        s.View.Face.IsHitTestVisible = true;
        s.View.Pulse();
        s.View.Refresh();
    }

    void HookStack(StackEntry s)
    {
        var face = s.View.Face;
        var bundle = s.Bundle;
        face.PointerEntered += (_, _) =>
        {
            if (!bundle.Landed || bundle.Hovered) return;
            bundle.Hovered = true;
            s.View.Refresh();
        };
        face.PointerExited += (_, _) =>
        {
            if (!bundle.Hovered) return;
            bundle.Hovered = false;
            s.View.Refresh();
        };
        face.PointerPressed += (_, args) =>
        {
            if (!bundle.Landed || s.Body.Mode != FlickBodyMode.Landed || s.Fan is not null) return;
            if (!args.GetCurrentPoint(face).Properties.IsLeftButtonPressed) return;
            s.Press = args;
            s.PressPos = args.GetPosition(face);
            s.Dragged = false;
        };
        face.PointerMoved += async (_, args) =>
        {
            if (s.Press is null || s.Dragged) return;
            var p = args.GetPosition(face);
            if (Math.Abs(p.X - s.PressPos.X) + Math.Abs(p.Y - s.PressPos.Y) < 5) return;
            s.Dragged = true;
            var press = s.Press;
            s.Press = null;
            await DragOutStackAsync(s, press);
        };
        face.PointerReleased += (_, _) =>
        {
            var clicked = s.Press is not null && !s.Dragged;
            s.Press = null;
            if (clicked) FanOut(s);
        };
        face.PointerWheelChanged += (_, args) =>
        {
            // A decisive two-finger swipe throws the whole stack away, like a single card.
            if (s.Body.Mode != FlickBodyMode.Landed || s.Fan is not null) return;
            if ((DateTime.UtcNow - s.WheelAt).TotalMilliseconds > 400) s.Wheel = default;
            s.WheelAt = DateTime.UtcNow;
            s.Wheel += args.Delta;
            if (Math.Abs(s.Wheel.X) > 2.5 || Math.Abs(s.Wheel.Y) > 3.5)
            {
                var dir = new Vector(-s.Wheel.X, -s.Wheel.Y);
                s.Wheel = default;
                FlingStack(s, dir);
            }
            args.Handled = true;
        };
        face.ContextMenu = BuildStackMenu(s);
    }

    ContextMenu BuildStackMenu(StackEntry s)
    {
        var spread = new MenuItem { Header = "Spread out" };
        spread.Click += (_, _) => FanOut(s);
        var reveal = new MenuItem { Header = OperatingSystem.IsWindows() ? "Show in Explorer" : "Show in folder" };
        reveal.Click += (_, _) => RevealBundle(s.Bundle);
        var copy = new MenuItem { Header = "Copy all" };
        copy.Click += async (_, _) => await CopyFilesAsync(s.Window, s.Bundle.DragPaths);
        var delete = new MenuItem { Header = "Delete Stack" };
        delete.Click += (_, _) => DismissBundle(s.Bundle.Id);
        var menu = new ContextMenu { Items = { spread, reveal, copy, new Separator(), delete } };
        menu.Opening += (_, _) =>
        {
            var any = s.Bundle.DragPaths.Count > 0;
            reveal.IsEnabled = any;
            copy.IsEnabled = any;
        };
        return menu;
    }

    async Task DragOutStackAsync(StackEntry s, PointerPressedEventArgs press)
    {
        var paths = s.Bundle.DragPaths;
        if (paths.Count == 0) return; // still arriving: nothing to hand over yet
        s.DraggingOut = true;
        try
        {
            var result = await FileDrag.StartAsync(s.View.Face, press, paths); // every file at once
            if (result != DragDropEffects.None && !s.Bundle.IsBusy) DismissBundle(s.Bundle.Id, animated: false);
        }
        catch (Exception ex) { Debug.WriteLine("Flick stack drag failed: " + ex.Message); }
        finally { s.DraggingOut = false; }
    }

    void FlingStack(StackEntry s, Vector direction)
    {
        if (s.Body.Mode != FlickBodyMode.Landed) return;
        FinishBundle(s.Bundle);
        var len = Math.Max(1, direction.Length);
        s.Body.Vx = direction.X / len * 2600;
        s.Body.Vy = direction.Y / len * 2600;
        s.Body.RotVel = direction.X / len * 160;
        s.Body.Mode = FlickBodyMode.Flung;
        s.View.Face.IsHitTestVisible = false;
        StartTicking();
    }

    /// <summary>The stack settles away toward where its files live (or vanishes at once after a drag delivered them).</summary>
    public void DismissBundle(string bundleId, bool animated = true)
    {
        if (!_stacks.TryGetValue(bundleId, out var s) || s.Body.Mode is FlickBodyMode.Dismissing or FlickBodyMode.Flung) return;
        CloseFan(s);
        FinishBundle(s.Bundle);
        var to = DismissPoint(s.Visible, s.Body.X, s.Body.Y, s.Bundle.DragPaths.FirstOrDefault());
        s.Body.DismissFromX = s.Body.X;
        s.Body.DismissFromY = s.Body.Y;
        s.Body.DismissToX = animated ? to.X : s.Body.X;
        s.Body.DismissToY = animated ? to.Y : s.Body.Y;
        s.Body.DismissT = animated ? 0 : 0.45;
        s.Body.Mode = FlickBodyMode.Dismissing;
        s.Bundle.Hovered = false;
        s.View.Face.IsHitTestVisible = false;
        StartTicking();
    }

    void FinishBundle(FlickBundle b)
    {
        _finishedBundles.Add(b.Id);
        foreach (var c in b.Items)
            if (c is not null) _finished.Add(c.Id);
        if (_finishedBundles.Count > 200) _finishedBundles.Clear();
    }

    void RemoveStack(string bundleId)
    {
        if (!_stacks.Remove(bundleId, out var s)) return;
        CloseFan(s);
        _bundles.Remove(bundleId);
        try { s.Window.Close(); } catch { }
    }

    // ---------------- fan out & gather ----------------

    void FanOut(StackEntry s)
    {
        var bundle = s.Bundle;
        if (s.Fan is not null || !bundle.Landed || s.Body.Mode != FlickBodyMode.Landed) return;
        var present = bundle.Present;
        if (present.Count == 0) return;

        // The pattern, centred where the stack landed, inside the work area with room below for the Gather pill.
        var v = s.Visible;
        var area = new Rect(v.X + 24, v.Y + 24, Math.Max(1, v.Width - 48), Math.Max(1, v.Height - 48 - FlickFanView.PillSpace));
        var placement = FlickFan.Place(present.Count, s.Body.X, s.Body.Y, area, FlickCardView.CardWidth, FlickCardView.CardHeight);
        var stackCard = new Rect(s.Body.X - FlickCardView.CardWidth / 2, s.Body.Y - FlickCardView.CardHeight / 2, FlickCardView.CardWidth, FlickCardView.CardHeight);
        var screen = new Rect(0, 0, s.ScreenBounds.Width / s.Scaling, s.ScreenBounds.Height / s.Scaling);
        var rect = placement.Bounds.Union(stackCard)
            .Inflate(new Thickness(FanMargin, FanMargin, FanMargin, FanMargin + FlickFanView.PillSpace))
            .Intersect(screen);
        var pill = new Point(placement.Bounds.Center.X - rect.X,
            Math.Min(rect.Height - FlickFanView.PillSpace / 2, placement.Bounds.Bottom - rect.Y + FlickFanView.PillSpace / 2 + 4));

        var view = new FlickFanView(rect.Size, pill);
        var window = new FlickFanWindow(view);
        var fan = new Fan { Window = window, View = view, Placement = placement, Area = rect };

        var sx = s.Body.X - rect.X;
        var sy = s.Body.Y - rect.Y;
        // Bottom card first, so the first item (the top of the stack) is added last and stays on top.
        for (var place = placement.Slots.Count - 1; place >= 0; place--)
        {
            var slot = placement.Slots[place];
            var card = slot.IsMore
                ? new FlickCard("more:" + bundle.Id, "more", $"{slot.More} more", "", 0, FlickTransfer.None)
                {
                    More = slot.More,
                    SubtitleOverride = "Open the folder",
                    BadgeKind = "more",
                }
                : present[slot.Item];
            card.Landed = false;
            card.Hovered = false;
            var cardView = new FlickCardView(card);
            var start = FlickStackView.StackSpot(place, s.Body.Rot);
            var target = placement.Centers[place];
            var fc = new FanCard
            {
                Card = card,
                View = cardView,
                Place = place,
                Delay = place * FanStagger,
                Body = new FlickBody
                {
                    X = sx + start.X,
                    Y = sy + start.Y,
                    Rot = start.Rot,
                    Scale = 1,
                    Alpha = 1,
                    TargetX = target.X - rect.X,
                    TargetY = target.Y - rect.Y,
                    TargetRot = FlickFan.Tilt(place),
                    TargetScale = placement.Scale,
                    Mode = FlickBodyMode.Flying,
                },
            };
            cardView.Face.IsHitTestVisible = false;
            view.AddCard(cardView);
            FlickFanView.Place(cardView, fc.Body);
            HookFanCard(s, fan, fc);
            fan.Cards.Add(fc);
        }
        fan.Cards.Reverse(); // by place

        view.Halo.PointerPressed += (_, _) => Gather(s);
        view.GatherButton.PointerReleased += (_, _) => Gather(s);
        view.FolderButton.PointerReleased += (_, _) => RevealBundle(bundle);
        view.ShowHalo(true);

        bundle.Fanned = true;
        bundle.Hovered = false;
        s.Fan = fan;
        s.Hidden = true;
        s.Window.Hide();
        window.Position = new PixelPoint(
            (int)Math.Round(s.ScreenBounds.X + rect.X * s.Scaling),
            (int)Math.Round(s.ScreenBounds.Y + rect.Y * s.Scaling));
        window.Show();
        StartTicking();
    }

    void Gather(StackEntry s)
    {
        if (s.Fan is not { } fan || fan.Gathering) return;
        fan.Gathering = true;
        fan.View.ShowPill(false);
        fan.View.ShowHalo(false);
        var cx = s.Body.X - fan.Area.X;
        var cy = s.Body.Y - fan.Area.Y;
        var live = fan.Cards.Where(c => !c.Leaving).OrderBy(c => c.Place).ToList();
        for (var i = 0; i < live.Count; i++)
        {
            var c = live[i];
            var spot = FlickStackView.StackSpot(i, s.Body.Rot);
            c.Body.TargetX = cx + spot.X;
            c.Body.TargetY = cy + spot.Y;
            c.Body.TargetRot = spot.Rot;
            c.Body.TargetScale = 1;
            c.Body.Mode = FlickBodyMode.Flying;
            c.Delay = (live.Count - 1 - i) * GatherStagger;
            c.Card.Hovered = false;
            c.View.Face.IsHitTestVisible = false;
            FlickFanView.Raise(c.View, false);
            c.View.Refresh();
        }
        StartTicking();
    }

    /// <summary>The cards are home: the fan goes, the stack comes back with a pulse.</summary>
    void FinishGather(StackEntry s)
    {
        CloseFan(s);
        s.Bundle.Remaining = Math.Max(s.Bundle.Remaining, 8);
        if (s.Bundle.Present.Count == 0) DismissBundle(s.Bundle.Id, animated: false);
        else s.View.Pulse();
    }

    void CloseFan(StackEntry s)
    {
        if (s.Fan is not { } fan) return;
        s.Fan = null;
        foreach (var c in fan.Cards)
        {
            c.Card.Landed = true;
            c.Card.Hovered = false;
        }
        try { fan.Window.Close(); } catch { }
        s.Bundle.Fanned = false;
        s.Bundle.Hovered = false;
        if (s.Hidden)
        {
            s.Hidden = false;
            PlaceStack(s);
            s.Window.Show();
            PlaceStack(s);
        }
        s.View.Refresh();
    }

    void FrameBundles(double dt)
    {
        if (_stacks.Count == 0) return;
        var removed = new List<string>();
        var anyFan = false;
        foreach (var (id, s) in _stacks.ToList())
        {
            if (!s.Hidden && s.Body.Mode != FlickBodyMode.Landed)
            {
                var wasFlying = s.Body.Mode == FlickBodyMode.Flying;
                var moving = s.Body.Advance(dt);
                PlaceStack(s);
                if (!moving)
                {
                    if (wasFlying && s.Body.Mode == FlickBodyMode.Landed) StackLanded(s);
                    else if (s.Body.Mode is FlickBodyMode.Dismissing or FlickBodyMode.Flung) removed.Add(id);
                }
            }
            if (!s.Hidden) s.View.Animate(dt);
            if (s.Fan is { } fan)
            {
                anyFan = true;
                FrameFan(s, fan, dt);
            }
        }
        foreach (var id in removed) RemoveStack(id);
        if (anyFan) PollGlobalInput();
    }

    void FrameFan(StackEntry s, Fan fan, double dt)
    {
        var allHome = true;
        foreach (var c in fan.Cards.ToList())
        {
            if (c.Delay > 0)
            {
                c.Delay -= dt;
                allHome = false;
                continue;
            }
            if (c.Body.Mode != FlickBodyMode.Landed)
            {
                var moving = c.Body.Advance(c.Leaving ? dt : dt * (fan.Gathering ? GatherSpeed : FanSpeed));
                FlickFanView.Place(c.View, c.Body);
                if (c.Leaving)
                {
                    if (!moving)
                    {
                        fan.View.RemoveCard(c.View);
                        fan.Cards.Remove(c);
                    }
                    continue;
                }
                if (moving) allHome = false;
            }
            c.View.Animate(dt);
        }
        fan.View.Animate(dt);
        if (s.Fan != fan) return;
        if (fan.Cards.Count == 0)
        {
            // Every card was dragged away: nothing left to stack.
            CloseFan(s);
            DismissBundle(s.Bundle.Id, animated: false);
            return;
        }
        if (fan.Gathering)
        {
            if (allHome) FinishGather(s);
        }
        else if (allHome && !fan.Settled)
        {
            fan.Settled = true;
            foreach (var c in fan.Cards.Where(c => !c.Leaving))
            {
                c.Card.Landed = true;
                c.View.Face.IsHitTestVisible = true;
                c.View.Refresh();
            }
            fan.View.ShowPill(true);
        }
    }

    void HousekeepBundles(double elapsed)
    {
        var now = DateTime.UtcNow;
        foreach (var s in _stacks.Values.ToList())
        {
            var b = s.Bundle;
            if (s.Body.Mode != FlickBodyMode.Landed) continue;
            // A bundle whose items stop coming gives up on the rest instead of waiting forever.
            if (b.IsBusy && (now - b.LastActivity).TotalSeconds > (b.Arrived == 0 && b.Progress <= 0 ? 30 : 45))
            {
                foreach (var c in b.Items)
                {
                    if (c is not { IsBusy: true }) continue;
                    c.FailReason = c.Transfer == FlickTransfer.Waiting ? "Transfer didn't start" : "Transfer stalled";
                    c.Transfer = FlickTransfer.Failed;
                }
                b.MissingGaveUp = true;
                b.Remaining = Math.Min(b.Remaining, 8);
                RefreshBundle(b.Id);
            }
            if (s.Fan is { } fan)
            {
                // Spread out and left alone for a long while: stack it again.
                if (!fan.Gathering && fan.Cards.All(c => !c.Card.Hovered && !c.DraggingOut) && (now - fan.LastActivity).TotalSeconds > 40) Gather(s);
                continue;
            }
            if (b.Hovered || b.IsBusy || s.DraggingOut) continue;
            b.Remaining -= elapsed;
            if (b.Remaining <= 0) DismissBundle(b.Id);
        }
    }

    // ---------------- fanned cards: like single cards ----------------

    void HookFanCard(StackEntry s, Fan fan, FanCard c)
    {
        var face = c.View.Face;
        face.PointerEntered += (_, _) => HoverFanCard(fan, c, true);
        face.PointerExited += (_, _) => HoverFanCard(fan, c, false);
        face.PointerPressed += (_, args) =>
        {
            if (!fan.Settled || fan.Gathering || c.Leaving) return;
            if (!args.GetCurrentPoint(face).Properties.IsLeftButtonPressed) return;
            c.Press = args;
            c.PressPos = args.GetPosition(face);
            c.Dragged = false;
            fan.LastActivity = DateTime.UtcNow;
        };
        face.PointerMoved += async (_, args) =>
        {
            if (c.Press is null || c.Dragged) return;
            var p = args.GetPosition(face);
            if (Math.Abs(p.X - c.PressPos.X) + Math.Abs(p.Y - c.PressPos.Y) < 5) return;
            c.Dragged = true;
            var press = c.Press;
            c.Press = null;
            await DragOutFanCardAsync(s, fan, c, press);
        };
        face.PointerReleased += (_, _) =>
        {
            var clicked = c.Press is not null && !c.Dragged;
            c.Press = null;
            if (clicked) OpenFanCard(s, fan, c);
        };
        face.ContextMenu = BuildFanCardMenu(s, fan, c);
    }

    void HoverFanCard(Fan fan, FanCard c, bool hovered)
    {
        fan.LastActivity = DateTime.UtcNow;
        if (!fan.Settled || fan.Gathering || c.Leaving || c.Card.Hovered == hovered) return;
        c.Card.Hovered = hovered;
        FlickFanView.Raise(c.View, hovered);
        // A small lift on its spring.
        c.Body.TargetScale = fan.Placement.Scale * (hovered ? 1.045 : 1);
        if (c.Body.Mode == FlickBodyMode.Landed) c.Body.Mode = FlickBodyMode.Flying;
        c.View.Refresh();
        StartTicking();
    }

    void OpenFanCard(StackEntry s, Fan fan, FanCard c)
    {
        fan.LastActivity = DateTime.UtcNow;
        var card = c.Card;
        if (card.Kind == "more")
        {
            RevealBundle(s.Bundle);
            return;
        }
        if (card.FilePath is { } path) PlatformShell.Open(path);
        else if (card.LinkUrl is { } url) PlatformShell.Open(url);
        else if (card.Kind == "text" && card.Text is { } text)
        {
            _ = CopyTextAsync(fan.Window, text);
            card.Note = "Copied to clipboard";
            c.View.Refresh();
        }
    }

    ContextMenu BuildFanCardMenu(StackEntry s, Fan fan, FanCard c)
    {
        var card = c.Card;
        var open = new MenuItem { Header = card.Kind == "more" ? "Open the folder" : card.Kind == "link" ? "Open Link" : "Open" };
        open.Click += (_, _) => OpenFanCard(s, fan, c);
        var reveal = new MenuItem { Header = OperatingSystem.IsWindows() ? "Show in Explorer" : "Show in folder" };
        reveal.Click += (_, _) => { if (card.FilePath is { } p) PlatformShell.RevealInFolder(p); else RevealBundle(s.Bundle); };
        var copy = new MenuItem { Header = "Copy" };
        copy.Click += async (_, _) =>
        {
            if (card.FilePath is { } p) await CopyFilesAsync(fan.Window, new[] { p });
            else if (card.Kind == "more") await CopyFilesAsync(fan.Window, OverflowPaths(s.Bundle));
            else if ((card.LinkUrl ?? card.Text) is { } t) await CopyTextAsync(fan.Window, t);
        };
        var remove = new MenuItem { Header = "Remove from Stack" };
        remove.Click += (_, _) => TakeFanCard(s, fan, c);
        var gather = new MenuItem { Header = "Gather" };
        gather.Click += (_, _) => Gather(s);
        var menu = new ContextMenu { Items = { open, reveal, copy, new Separator(), gather, remove } };
        menu.Opening += (_, _) =>
        {
            fan.LastActivity = DateTime.UtcNow;
            open.IsEnabled = card.Kind == "more" || card.FilePath is not null || card.LinkUrl is not null || card.Kind == "text";
            reveal.IsVisible = card.FilePath is not null || card.Kind == "more";
            remove.IsVisible = card.Kind != "more";
        };
        return menu;
    }

    async Task DragOutFanCardAsync(StackEntry s, Fan fan, FanCard c, PointerPressedEventArgs press)
    {
        var card = c.Card;
        c.DraggingOut = true;
        fan.LastActivity = DateTime.UtcNow;
        try
        {
            DragDropEffects result;
            if (card.Kind == "more")
            {
                var paths = OverflowPaths(s.Bundle);
                if (paths.Count == 0) return;
                result = await FileDrag.StartAsync(c.View.Face, press, paths);
                return; // the "+N" card stays: it stands for the folder
            }
            if (card.FilePath is { } path) result = await FileDrag.StartAsync(c.View.Face, press, path);
            else if ((card.LinkUrl ?? card.Text) is { Length: > 0 } text)
            {
                var data = new DataTransfer();
                data.Add(DataTransferItem.CreateText(text));
                result = await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy);
            }
            else return;
            if (result != DragDropEffects.None) TakeFanCard(s, fan, c); // delivered: it leaves the bundle
        }
        catch (Exception ex) { Debug.WriteLine("Flick fan drag failed: " + ex.Message); }
        finally { c.DraggingOut = false; }
    }

    /// <summary>A card leaves the bundle (dragged out, or removed): it shrinks away where it is.</summary>
    void TakeFanCard(StackEntry s, Fan fan, FanCard c)
    {
        if (c.Leaving || c.Card.Kind == "more") return;
        c.Leaving = true;
        c.Card.Taken = true;
        c.Card.Hovered = false;
        c.View.Face.IsHitTestVisible = false;
        c.Body.DismissFromX = c.Body.DismissToX = c.Body.X;
        c.Body.DismissFromY = c.Body.DismissToY = c.Body.Y;
        c.Body.DismissT = 0;
        c.Body.Mode = FlickBodyMode.Dismissing;
        c.Delay = 0;
        s.View.Refresh();
        StartTicking();
    }

    /// <summary>Files behind the "+N" card (the items past the shown ones).</summary>
    static IReadOnlyList<string> OverflowPaths(FlickBundle b)
        => b.Present.Skip(FlickFan.MaxCards - 1).Where(c => c.FilePath is not null).Select(c => c.FilePath!).ToList();

    static void RevealBundle(FlickBundle b)
    {
        if (b.DragPaths.FirstOrDefault() is { } first) PlatformShell.RevealInFolder(first);
        else if (b.Folder is { } dir) PlatformShell.Open(dir + Path.DirectorySeparatorChar);
    }

    async Task CopyFilesAsync(TopLevel top, IReadOnlyList<string> paths)
    {
        try
        {
            if (paths.Count == 0 || top.StorageProvider is not { } storage || top.Clipboard is not { } cb) return;
            var data = new DataTransfer();
            var any = false;
            foreach (var p in paths)
            {
                if (await storage.TryGetFileFromPathAsync(p) is not { } file) continue;
                data.Add(DataTransferItem.CreateFile(file));
                any = true;
            }
            if (any) await cb.SetDataAsync(data);
        }
        catch { }
    }

    // ---------------- Esc and clicks elsewhere (Windows) ----------------

    /// <summary>
    /// While a bundle is spread out: Esc anywhere, or a click outside every Flick window, gathers it. The windows never take
    /// focus (they'd steal it from the user's app), so the keyboard and mouse are read directly.
    /// </summary>
    void PollGlobalInput()
    {
        if (!OperatingSystem.IsWindows()) return;
        var esc = FlickInput.IsDown(FlickInput.VkEscape);
        if (esc && !_escDown) GatherAll();
        _escDown = esc;
        var mouse = FlickInput.IsDown(FlickInput.VkLButton) || FlickInput.IsDown(FlickInput.VkRButton);
        if (mouse && !_mouseDown && GlobalCursor.Position is { } p && !OverFlickWindow(p)) GatherAll();
        _mouseDown = mouse;
    }

    bool OverFlickWindow(PixelPoint p)
    {
        static bool Over(Window w, PixelPoint p)
        {
            if (!w.IsVisible) return false;
            var k = w.RenderScaling > 0 ? w.RenderScaling : 1;
            var r = new PixelRect(w.Position, new PixelSize((int)Math.Ceiling(w.Width * k), (int)Math.Ceiling(w.Height * k)));
            return r.Contains(p);
        }
        foreach (var s in _stacks.Values)
        {
            if (s.Fan is { } f && Over(f.Window, p)) return true;
            if (!s.Hidden && Over(s.Window, p)) return true;
        }
        return _cards.Values.Any(e => Over(e.Window, p));
    }
}

/// <summary>Reads keys and buttons straight from Windows (the Flick windows never have focus).</summary>
internal static partial class FlickInput
{
    public const int VkLButton = 0x01;
    public const int VkRButton = 0x02;
    public const int VkEscape = 0x1B;

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    public static bool IsDown(int vk)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
        catch { return false; }
    }
}
