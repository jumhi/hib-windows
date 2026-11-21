// H.I.B — incoming Flick, the same as on the Mac (FlickManager.swift): every flick (from the phone, another
// computer through the phone, or a direct peer link) rises from the bottom edge as a card (like the Mac), lands with a spring,
// shows the transfer, then can be clicked to open, dragged into any app, right-clicked, swiped away, and
// settles away on its own. Cards stack with a small offset and alternating tilt.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using HIB.Core.Phone;
using HIB.Core.Platform;
using HIB.Services;

namespace HIB.Views.Flick;

public sealed class FlickCardManager
{
    sealed class Entry
    {
        public required FlickCard Card;
        public required FlickCardView View;
        public required FlickCardWindow Window;
        public FlickBody Body;
        public PixelRect ScreenBounds;
        public double Scaling = 1;
        public Rect Visible; // DIPs, relative to ScreenBounds' origin
        public bool DraggingOut;
        public PointerPressedEventArgs? Press;
        public Point PressPos;
        public bool Dragged;
        public Vector Wheel;
        public DateTime WheelAt;
    }

    readonly AppController _app;
    readonly Dictionary<string, Entry> _cards = new();
    readonly List<string> _order = new();
    readonly HashSet<string> _finished = new();
    readonly Dictionary<string, string> _transferToFlick = new();
    readonly Stopwatch _clock = new();
    readonly DispatcherTimer _timer;
    double _last;
    double _housekeep;

    public FlickCardManager(AppController app)
    {
        _app = app;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / 120) };
        _timer.Tick += (_, _) => Frame();
        var core = app.Core;
        core.Space.FlickArrived += f => Dispatcher.UIThread.Post(() => OnFlick(f));
        core.Files.Progress += p => Dispatcher.UIThread.Post(() => OnProgress(p));
        core.Files.Received += f => Dispatcher.UIThread.Post(() => OnReceived(f));
        core.Files.Failed += (id, reason) => Dispatcher.UIThread.Post(() => OnFailed(id, reason));
    }

    /// <summary>Cards on screen (UI thread).</summary>
    public IReadOnlyList<FlickCardWindow> Windows => _order.Where(_cards.ContainsKey).Select(id => _cards[id].Window).ToList();

    public IReadOnlyList<FlickCard> Cards => _order.Where(_cards.ContainsKey).Select(id => _cards[id].Card).ToList();

    /// <summary>True when this transfer id belongs to a Flick (its failures show on the card, not as a toast).</summary>
    public bool OwnsTransfer(string transferId) => _transferToFlick.ContainsKey(transferId);

    // ---------------- protocol entry points (UI thread) ----------------

    void OnFlick(FlickInfo f)
    {
        if (_finished.Contains(f.Id)) return;
        var kind = f.Kind is "image" or "video" or "file" or "link" or "text" ? f.Kind : "file";
        var name = f.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = kind switch
            {
                "link" => Uri.TryCreate(f.Text?.Trim(), UriKind.Absolute, out var u) ? u.Host : "Link",
                "text" => "Text",
                _ => "Flick",
            };
        }
        var thumb = ImageUtil.FromBytes(f.Thumbnail);
        if (_cards.TryGetValue(f.Id, out var existing))
        {
            // fileTransferInit beat flickStart: enrich the card already in flight.
            var c = existing.Card;
            c.Kind = kind;
            if (string.IsNullOrEmpty(c.Name) || c.Name == "Flick") c.Name = name;
            c.Thumbnail ??= thumb;
            c.Text ??= f.Text;
            c.From ??= f.From;
            existing.View.Refresh();
            return;
        }
        var needsTransfer = kind is not ("link" or "text");
        var card = new FlickCard(f.Id, kind, name, f.Mime ?? "", f.Size, needsTransfer ? FlickTransfer.Waiting : FlickTransfer.None)
        {
            Thumbnail = thumb,
            Text = f.Text,
            From = f.From,
        };
        Screen? screen = null;
        (double X, double Y)? dropAt = null;
        if (f.At is { } at && DropScreen(at.X, at.Y) is { } drop)
        {
            // Roam (SPACE.md §9.5): the file was dragged across and dropped here — the card appears at the drop point.
            screen = drop.Screen;
            dropAt = (at.X - drop.Info.X, at.Y - drop.Info.Y);
        }
        else
        {
            try { screen = _app.Flick.PickFor(f); } catch { }
        }
        Spawn(card, f.ExitXRatio, f.Velocity, f.Angle, screen, dropAt);
    }

    /// <summary>The monitor whose points rectangle holds (x, y), else the nearest one.</summary>
    (HIB.Core.Phone.DisplayScreen Info, Screen Screen)? DropScreen(double x, double y)
    {
        var all = _app.Screens.Current();
        if (all.Count == 0) return null;
        var rects = all.Select(s => new HIB.Core.Roam.RoamRect(s.Info.X, s.Info.Y, s.Info.W, s.Info.H)).ToList();
        var i = HIB.Core.Roam.RoamGeometry.ScreenAt(x, y, rects);
        return i < 0 ? null : all[i];
    }

    void OnProgress(TransferProgress p)
    {
        if (p.FlickId is null) return;
        _transferToFlick[p.Id] = p.FlickId;
        if (_finished.Contains(p.FlickId)) return;
        if (!_cards.TryGetValue(p.FlickId, out var e))
        {
            if (p.Done) return;
            // The transfer started without a flickStart (or before it): create the card from the init.
            var mime = FileSender.MimeFor(p.Name);
            var card = new FlickCard(p.FlickId, HIB.Core.Space.SpaceIds.KindForMime(mime), p.Name, mime, p.Size, FlickTransfer.Receiving);
            Spawn(card, 0.5, 1800, 0, null);
            return;
        }
        var c = e.Card;
        if (p.Failed) return; // OnFailed explains why
        if (c.Transfer == FlickTransfer.Waiting) c.Transfer = FlickTransfer.Receiving;
        if (c.Transfer == FlickTransfer.Receiving)
        {
            c.Size = p.Size > 0 ? p.Size : c.Size;
            c.Progress = p.Size > 0 ? Math.Clamp((double)p.Received / p.Size, 0, 1) : 0;
            if ((c.Name == "Flick" || c.Name.Length == 0) && p.Name.Length > 0) c.Name = p.Name;
        }
        c.LastActivity = DateTime.UtcNow;
        e.View.Refresh();
    }

    void OnReceived(ReceivedFile f)
    {
        if (f.FlickId is null || _finished.Contains(f.FlickId)) return;
        if (!_cards.TryGetValue(f.FlickId, out var e))
        {
            var mime = FileSender.MimeFor(f.Name);
            var fresh = new FlickCard(f.FlickId, HIB.Core.Space.SpaceIds.KindForMime(mime), f.Name, mime, f.Size, FlickTransfer.Receiving) { From = f.From };
            Spawn(fresh, 0.5, 1800, 0, _app.Flick.LandingScreen(f.FlickId));
            e = _cards[f.FlickId];
        }
        var c = e.Card;
        c.FilePath = f.Path;
        c.Name = f.Name;
        c.Size = f.Size;
        c.From ??= f.From;
        c.Progress = 1;
        c.Transfer = FlickTransfer.Done;
        c.Remaining = Math.Max(c.Remaining, 8);
        c.LastActivity = DateTime.UtcNow;
        e.View.Refresh();
        if (c.Thumbnail is null && c.Kind == "image" && f.Size < 60_000_000) _ = LoadThumbnailAsync(c.Id, f.Path);
    }

    void OnFailed(string transferId, string reason)
    {
        var flickId = _transferToFlick.TryGetValue(transferId, out var fid) ? fid : transferId;
        if (!_cards.TryGetValue(flickId, out var e)) return;
        var c = e.Card;
        c.Transfer = FlickTransfer.Failed;
        c.FailReason = reason == "Disconnected" ? "The sender disconnected" : reason;
        c.Remaining = Math.Min(c.Remaining, 6);
        e.View.Refresh();
    }

    async Task LoadThumbnailAsync(string id, string path)
    {
        var bmp = await Task.Run(() =>
        {
            try
            {
                using var s = File.OpenRead(path);
                return Bitmap.DecodeToWidth(s, 512, BitmapInterpolationMode.HighQuality);
            }
            catch { return null; }
        });
        if (bmp is null || !_cards.TryGetValue(id, out var e)) return;
        e.Card.Thumbnail = bmp;
        e.View.Refresh();
    }

    // ---------------- spawning & physics ----------------

    void Spawn(FlickCard card, double exitXRatio, double velocity, double angle, Screen? chosen, (double X, double Y)? dropAt = null)
    {
        var view = new FlickCardView(card);
        var window = new FlickCardWindow(view);
        var e = new Entry { Card = card, View = view, Window = window };

        var screen = chosen ?? ScreenUnderCursor(window);
        if (screen is not null)
        {
            e.ScreenBounds = screen.Bounds;
            e.Scaling = screen.Scaling > 0 ? screen.Scaling : 1;
            var wa = screen.WorkingArea;
            e.Visible = new Rect((wa.X - screen.Bounds.X) / e.Scaling, (wa.Y - screen.Bounds.Y) / e.Scaling, wa.Width / e.Scaling, wa.Height / e.Scaling);
        }
        else
        {
            e.ScreenBounds = new PixelRect(0, 0, 1920, 1080);
            e.Visible = new Rect(0, 0, 1920, 1040);
        }

        var areaWidth = e.ScreenBounds.Width / e.Scaling;
        var v = e.Visible;
        var ratio = Math.Clamp(exitXRatio, 0.05, 0.95);
        // Stack: each live card takes the next slot, offset diagonally with alternating tilt.
        var slot = _order.Count(id => _cards.TryGetValue(id, out var o) && o.Body.Mode is not (FlickBodyMode.Dismissing or FlickBodyMode.Flung)) % 5;
        var tx = v.X + v.Width / 2 + (ratio - 0.5) * v.Width * 0.3 + slot * 26;
        var ty = v.Bottom - v.Height * 0.30 - slot * 16;
        tx = Math.Clamp(tx, v.X + FlickCardView.CardWidth / 2 + 24, Math.Max(v.X + FlickCardView.CardWidth / 2 + 24, v.Right - FlickCardView.CardWidth / 2 - 24));
        ty = Math.Clamp(ty, v.Y + FlickCardView.CardHeight / 2 + 24, Math.Max(v.Y + FlickCardView.CardHeight / 2 + 24, v.Bottom - FlickCardView.CardHeight / 2 - 24));
        var areaHeight = e.ScreenBounds.Height / e.Scaling;
        if (dropAt is { } at)
        {
            // Dropped here: grow out of the ghost at the drop point (kept on screen).
            var dx = Math.Clamp(at.X, v.X + FlickCardView.CardWidth / 2 + 8, Math.Max(v.X + FlickCardView.CardWidth / 2 + 8, v.Right - FlickCardView.CardWidth / 2 - 8));
            var dy = Math.Clamp(at.Y, v.Y + FlickCardView.CardHeight / 2 + 8, Math.Max(v.Y + FlickCardView.CardHeight / 2 + 8, v.Bottom - FlickCardView.CardHeight / 2 - 8));
            e.Body = FlickBody.DropAt(dx, dy, slot);
        }
        else e.Body = FlickBody.Launch(areaWidth, areaHeight, FlickCardView.CardHeight, ratio, velocity, angle, tx, ty, slot);

        view.RenderTransform = new TransformGroup { Children = { new ScaleTransform(), new RotateTransform() } };
        view.Face.IsHitTestVisible = false;
        Hook(e);
        _cards[card.Id] = e;
        _order.Add(card.Id);
        Place(e);
        window.Show();
        Place(e); // after Show the platform knows the window's screen/scaling
        StartTicking();
    }

    static Screen? ScreenUnderCursor(Window probe)
    {
        var screens = probe.Screens;
        if (screens is null) return null;
        if (GlobalCursor.Position is { } c && screens.All.FirstOrDefault(s => s.Bounds.Contains(c)) is { } under) return under;
        return screens.Primary ?? screens.All.FirstOrDefault();
    }

    void Place(Entry e)
    {
        var b = e.Body;
        var s = e.Scaling;
        e.Window.Position = new PixelPoint(
            (int)Math.Round(e.ScreenBounds.X + (b.X - FlickCardView.ContainerWidth / 2) * s),
            (int)Math.Round(e.ScreenBounds.Y + (b.Y - FlickCardView.ContainerHeight / 2) * s));
        if (e.View.RenderTransform is TransformGroup g)
        {
            var scale = (ScaleTransform)g.Children[0];
            scale.ScaleX = b.Scale;
            scale.ScaleY = b.Scale;
            ((RotateTransform)g.Children[1]).Angle = b.Rot;
        }
        e.View.Opacity = Math.Clamp(b.Alpha, 0, 1);
    }

    void StartTicking()
    {
        if (_timer.IsEnabled) return;
        _clock.Restart();
        _last = 0;
        _timer.Start();
    }

    void Frame()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _last, 1.0 / 240, 1.0 / 20);
        _last = now;
        var removed = new List<string>();
        foreach (var (id, e) in _cards.ToList())
        {
            if (e.Body.Mode != FlickBodyMode.Landed)
            {
                var wasFlying = e.Body.Mode == FlickBodyMode.Flying;
                var moving = e.Body.Advance(dt);
                Place(e);
                if (!moving)
                {
                    if (wasFlying && e.Body.Mode == FlickBodyMode.Landed) Landed(e);
                    else if (e.Body.Mode is FlickBodyMode.Dismissing or FlickBodyMode.Flung) removed.Add(id);
                }
            }
            e.View.Animate(dt);
        }
        foreach (var id in removed) Remove(id);

        _housekeep += dt;
        if (_housekeep >= 0.25)
        {
            Housekeep(_housekeep);
            _housekeep = 0;
        }
        if (_cards.Count == 0) _timer.Stop();
    }

    void Landed(Entry e)
    {
        var c = e.Card;
        c.Landed = true;
        c.LastActivity = DateTime.UtcNow;
        e.View.Face.IsHitTestVisible = true;
        e.View.Pulse();
        switch (c.Kind)
        {
            case "link":
                if (_app.Core.Settings.AutoOpenLinks && c.LinkUrl is { } url)
                {
                    PlatformShell.Open(url);
                    c.Note = "Opened in browser";
                }
                break;
            case "text":
                if (c.Text is { Length: > 0 } text) _ = CopyTextAsync(e.Window, text);
                break;
        }
        e.View.Refresh();
    }

    void Housekeep(double elapsed)
    {
        var now = DateTime.UtcNow;
        foreach (var (id, e) in _cards.ToList())
        {
            if (e.Body.Mode != FlickBodyMode.Landed) continue;
            var c = e.Card;
            // A file that never starts / stalls gives up instead of hanging forever.
            if (c.IsBusy && (now - c.LastActivity).TotalSeconds > (c.Transfer == FlickTransfer.Waiting ? 25 : 45))
            {
                c.FailReason = c.Transfer == FlickTransfer.Waiting ? "Transfer didn't start" : "Transfer stalled";
                c.Transfer = FlickTransfer.Failed;
                c.Remaining = 6;
                e.View.Refresh();
            }
            if (c.Hovered || c.IsBusy || e.DraggingOut) continue;
            c.Remaining -= elapsed;
            if (c.Remaining <= 0) Dismiss(id);
        }
    }

    // ---------------- dismissal ----------------

    public void Dismiss(string id, bool animated = true)
    {
        if (!_cards.TryGetValue(id, out var e) || e.Body.Mode is FlickBodyMode.Dismissing or FlickBodyMode.Flung) return;
        _finished.Add(id);
        var to = DismissTarget(e);
        e.Body.DismissFromX = e.Body.X;
        e.Body.DismissFromY = e.Body.Y;
        e.Body.DismissToX = animated ? to.X : e.Body.X;
        e.Body.DismissToY = animated ? to.Y : e.Body.Y;
        e.Body.DismissT = animated ? 0 : 0.45;
        e.Body.Mode = FlickBodyMode.Dismissing;
        e.Card.Hovered = false;
        e.View.Face.IsHitTestVisible = false;
        StartTicking();
    }

    void Fling(string id, Vector direction)
    {
        if (!_cards.TryGetValue(id, out var e) || e.Body.Mode != FlickBodyMode.Landed) return;
        _finished.Add(id);
        var len = Math.Max(1, direction.Length);
        e.Body.Vx = direction.X / len * 2600;
        e.Body.Vy = direction.Y / len * 2600;
        e.Body.RotVel = direction.X / len * 160;
        e.Body.Mode = FlickBodyMode.Flung;
        e.View.Face.IsHitTestVisible = false;
        StartTicking();
    }

    /// <summary>Shrink toward where the file lives: Desktop icons sit top-left on Windows; anything else heads for the taskbar.</summary>
    static Point DismissTarget(Entry e)
    {
        var v = e.Visible;
        var path = e.Card.FilePath;
        if (path is null) return new Point(e.Body.X, e.Body.Y + 40);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(desktop) && string.Equals(Path.GetDirectoryName(path)?.TrimEnd('\\', '/'), desktop.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return new Point(v.X + 60, v.Y + 70);
        return new Point(v.X + v.Width / 2 + 60, v.Bottom + 10);
    }

    void Remove(string id)
    {
        if (!_cards.Remove(id, out var e)) return;
        _order.Remove(id);
        try { e.Window.Close(); } catch { }
        if (_finished.Count > 200) _finished.Clear();
        if (_transferToFlick.Count > 400) _transferToFlick.Clear();
    }

    // ---------------- pointer: hover, click, drag out, swipe, menu ----------------

    void Hook(Entry e)
    {
        var face = e.View.Face;
        var id = e.Card.Id;
        face.PointerEntered += (_, _) => SetHover(e, true);
        face.PointerExited += (_, _) => SetHover(e, false);
        face.PointerPressed += (_, args) =>
        {
            if (!e.Card.Landed || e.Body.Mode != FlickBodyMode.Landed) return;
            var point = args.GetCurrentPoint(face);
            if (!point.Properties.IsLeftButtonPressed) return;
            e.Press = args;
            e.PressPos = args.GetPosition(face);
            e.Dragged = false;
        };
        face.PointerMoved += async (_, args) =>
        {
            if (e.Press is null || e.Dragged) return;
            var p = args.GetPosition(face);
            if (Math.Abs(p.X - e.PressPos.X) + Math.Abs(p.Y - e.PressPos.Y) < 5) return;
            e.Dragged = true;
            var press = e.Press;
            e.Press = null;
            await DragOutAsync(e, press);
        };
        face.PointerReleased += (_, _) =>
        {
            var clicked = e.Press is not null && !e.Dragged;
            e.Press = null;
            if (clicked) Open(e);
        };
        face.PointerWheelChanged += (_, args) =>
        {
            // Two-finger swipe (precision touchpad) or a decisive wheel flick throws the card away.
            if (e.Body.Mode != FlickBodyMode.Landed) return;
            if ((DateTime.UtcNow - e.WheelAt).TotalMilliseconds > 400) e.Wheel = default;
            e.WheelAt = DateTime.UtcNow;
            e.Wheel += args.Delta;
            if (Math.Abs(e.Wheel.X) > 2.5 || Math.Abs(e.Wheel.Y) > 3.5)
            {
                var dir = new Vector(-e.Wheel.X, -e.Wheel.Y);
                e.Wheel = default;
                Fling(id, dir);
            }
            args.Handled = true;
        };
        face.ContextMenu = BuildMenu(e);
    }

    void SetHover(Entry e, bool hovered)
    {
        if (e.Card.Hovered == hovered) return;
        e.Card.Hovered = hovered && e.Card.Landed;
        e.View.Refresh();
    }

    ContextMenu BuildMenu(Entry e)
    {
        var open = new MenuItem { Header = e.Card.Kind == "link" ? "Open Link" : "Open" };
        open.Click += (_, _) => Open(e);
        var reveal = new MenuItem { Header = OperatingSystem.IsWindows() ? "Show in Explorer" : "Show in folder" };
        reveal.Click += (_, _) => { if (e.Card.FilePath is { } p) PlatformShell.RevealInFolder(p); };
        var copy = new MenuItem { Header = "Copy" };
        copy.Click += async (_, _) => await CopyAsync(e);
        var delete = new MenuItem { Header = "Delete Card" };
        delete.Click += (_, _) => Dismiss(e.Card.Id);
        var menu = new ContextMenu { Items = { open, reveal, copy, new Separator(), delete } };
        menu.Opening += (_, _) =>
        {
            open.IsEnabled = e.Card.FilePath is not null || e.Card.LinkUrl is not null || e.Card.Kind == "text";
            reveal.IsVisible = e.Card.FilePath is not null;
            copy.IsEnabled = e.Card.FilePath is not null || e.Card.Text is not null;
        };
        return menu;
    }

    void Open(Entry e)
    {
        var c = e.Card;
        if (c.FilePath is { } path) PlatformShell.Open(path);
        else if (c.LinkUrl is { } url) PlatformShell.Open(url);
        else if (c.Kind == "text" && c.Text is { } text)
        {
            _ = CopyTextAsync(e.Window, text);
            c.Note = "Copied to clipboard";
            e.View.Refresh();
            return;
        }
        else return;
        c.Remaining = Math.Min(c.Remaining, 3);
    }

    async Task CopyAsync(Entry e)
    {
        var c = e.Card;
        try
        {
            if (c.FilePath is { } path && e.Window.StorageProvider is { } storage && e.Window.Clipboard is { } cb
                && await storage.TryGetFileFromPathAsync(path) is { } file)
            {
                var data = new DataTransfer();
                data.Add(DataTransferItem.CreateFile(file));
                await cb.SetDataAsync(data);
            }
            else if (c.Text is { } text)
            {
                await CopyTextAsync(e.Window, text);
            }
            else return;
            c.Note = "Copied";
            e.View.Refresh();
        }
        catch { }
    }

    async Task CopyTextAsync(TopLevel top, string text)
    {
        // Copy without bouncing it back to the phone (the phone shares the clipboard itself).
        _app.Core.Clipboard?.SuppressNextLocalChange(text);
        var cb = top.Clipboard ?? _app.MainWindow?.Clipboard;
        if (cb is null) return;
        try { await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, text); } catch { }
    }

    async Task DragOutAsync(Entry e, PointerPressedEventArgs press)
    {
        var c = e.Card;
        e.DraggingOut = true;
        try
        {
            DragDropEffects result;
            if (c.FilePath is { } path)
            {
                result = await FileDrag.StartAsync(e.View.Face, press, path); // Explorer, WhatsApp, Outlook, upload boxes…
            }
            else if ((c.LinkUrl ?? c.Text) is { Length: > 0 } text)
            {
                var data = new DataTransfer();
                data.Add(DataTransferItem.CreateText(text));
                result = await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy);
            }
            else return; // still arriving: nothing to hand over yet
            if (result != DragDropEffects.None) Dismiss(c.Id, animated: false); // delivered: the card has done its job
        }
        catch (Exception ex) { Debug.WriteLine("Flick drag failed: " + ex.Message); }
        finally { e.DraggingOut = false; }
    }
}
