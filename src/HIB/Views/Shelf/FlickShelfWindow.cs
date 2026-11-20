// H.I.B — the Flick shelf on Windows (SPACE.md §5): a slim, translucent, always-on-top pill at the top centre
// of the primary screen. Drag files onto it and it opens into a glass shelf of device bubbles (the phone and
// every computer); drop on a bubble to send there (direct, through the phone, or to the phone itself).
// Clicking the pill peeks the shelf; clicking a bubble then picks files to send. It hides while a
// full-screen app is in front and when turned off in Settings (AppSettings.ShowFlickShelf).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using HIB.Core.Space;
using HIB.Services;
using HIB.Views.Flick;
using Path = Avalonia.Controls.Shapes.Path;

namespace HIB.Views.Shelf;

public sealed class FlickShelfWindow : Window
{
    public const double PillWidth = 120;
    public const double PillHeight = 10;
    const double BubbleWidth = 112;
    const double BubbleSpacing = 6;
    const double ShelfPadding = 18;
    const double ShelfHeight = 216;

    sealed class Bubble
    {
        public required SpaceDevice Device;
        public required Panel Root;
        public required Border Circle;
        public required ScaleTransform Scale;
    }

    readonly AppController _app;
    readonly Func<string, IReadOnlyList<string>, Task> _send;
    readonly Panel _root;
    readonly Border _pill;
    readonly Border _shelf;
    readonly StackPanel _row;
    readonly TextBlock _subtitle;
    readonly DispatcherTimer _collapse;
    readonly DispatcherTimer _poll;
    readonly List<Bubble> _bubbles = new();
    bool _expanded;
    bool _dragging;
    bool _busy;
    Bubble? _hover;
    string? _defaultId;

    public FlickShelfWindow(AppController app, Func<string, IReadOnlyList<string>, Task> send)
    {
        _app = app;
        _send = send;
        CanResize = false;
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        Title = "Flick shelf";
        Icon = ImageUtil.WindowIcon();
        FlickCardWindow.ApplyNoActivate(this);

        _pill = new Border
        {
            Width = PillWidth,
            Height = PillHeight,
            CornerRadius = new CornerRadius(PillHeight / 2),
            Background = FlickTheme.GlassFill(0.8),
            BorderBrush = FlickTheme.Linear(FlickTheme.TopLeft, FlickTheme.BottomRight, (FlickTheme.Cyan, 0.55, 0), (Colors.White, 0.14, 0.5), (FlickTheme.Cyan, 0.35, 1)),
            BorderThickness = new Thickness(0.8),
            Opacity = 0.78,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(_pill, "Flick — drag files here to send them to a device");
        _pill.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(_pill).Properties.IsLeftButtonPressed) Peek();
        };

        _row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = BubbleSpacing, HorizontalAlignment = HorizontalAlignment.Center };
        _subtitle = new TextBlock
        {
            Text = "Drop on a device",
            FontSize = 11,
            Foreground = FlickTheme.Solid(Colors.White, 0.55),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var title = new TextBlock
        {
            Text = "FLICK",
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            LetterSpacing = 2.2,
            Foreground = FlickTheme.Solid(FlickTheme.Cyan),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _shelf = new Border
        {
            CornerRadius = new CornerRadius(26),
            Background = FlickTheme.Linear(FlickTheme.TopLeft, FlickTheme.BottomRight, (FlickTheme.Panel, 0.94, 0), (FlickTheme.Midnight, 0.9, 1)),
            BorderBrush = FlickTheme.GlassStroke(),
            BorderThickness = new Thickness(0.8),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 10, Blur = 28, Color = FlickTheme.A(Colors.Black, 0.45) },
                new[] { new BoxShadow { Blur = 22, Color = FlickTheme.A(FlickTheme.Cyan, 0.14) } }),
            Padding = new Thickness(ShelfPadding, 12, ShelfPadding, 14),
            Margin = new Thickness(24, 8, 24, 24),
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
            Child = new StackPanel { Spacing = 10, Children = { new StackPanel { Spacing = 2, Children = { title, _subtitle } }, _row } },
        };

        _root = new Panel { Background = Brushes.Transparent, Children = { _shelf, _pill } };
        DragDrop.SetAllowDrop(_root, true);
        _root.AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        _root.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        _root.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        _root.AddHandler(DragDrop.DropEvent, OnDrop);
        _root.PointerExited += (_, _) => { if (_expanded && !_dragging && !_busy) _collapse.Start(); };
        _root.PointerEntered += (_, _) => _collapse.Stop();
        Content = _root;

        _collapse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(380) };
        _collapse.Tick += (_, _) =>
        {
            _collapse.Stop();
            if (!_busy) Collapse();
        };
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _poll.Tick += (_, _) => UpdateVisibility();
        app.Core.Space.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            if (_expanded) RebuildBubbles();
            UpdateVisibility();
        });
        Opened += (_, _) => { if (_expanded) PlaceExpanded(); else PlaceCollapsed(); };
    }

    public bool IsExpanded => _expanded;

    /// <summary>Shows the pill (if enabled) and starts watching for full-screen apps and the setting.</summary>
    public void Start()
    {
        _poll.Start();
        UpdateVisibility();
    }

    bool ShouldShow()
    {
        if (!_app.Core.Settings.ShowFlickShelf) return false;
        if (_expanded) return true;
        if (FullScreenDetector.IsFullScreenAppInFront()) return false;
        // Nothing to send to until a phone was paired or a computer is known.
        return _app.Core.Space.Devices.Count > 0;
    }

    void UpdateVisibility()
    {
        var show = ShouldShow();
        if (show && !IsVisible)
        {
            if (_expanded) PlaceExpanded(); else PlaceCollapsed();
            Show();
        }
        else if (!show && IsVisible)
        {
            Hide();
        }
    }

    Screen? PrimaryScreen() => Screens.Primary ?? Screens.All.FirstOrDefault();

    void Place(double width, double height)
    {
        Width = width;
        Height = height;
        var s = PrimaryScreen();
        if (s is null) return;
        var scale = s.Scaling > 0 ? s.Scaling : 1;
        var wa = s.WorkingArea;
        Position = new PixelPoint((int)Math.Round(wa.X + (wa.Width - width * scale) / 2), wa.Y);
    }

    void PlaceCollapsed() => Place(PillWidth, PillHeight);

    void PlaceExpanded()
    {
        var n = Math.Max(1, _bubbles.Count);
        var inner = Math.Max(n * BubbleWidth + (n - 1) * BubbleSpacing, 230);
        Place(inner + ShelfPadding * 2 + 48, ShelfHeight);
    }

    /// <summary>Opens the full shelf (drag entered, or the pill was clicked).</summary>
    public void Expand()
    {
        _collapse.Stop();
        if (_expanded) return;
        _expanded = true;
        RebuildBubbles();
        _pill.IsVisible = false;
        _shelf.IsVisible = true;
        _subtitle.Text = _dragging ? "Drop on a device" : "Click a device to send files";
        PlaceExpanded();
        if (!IsVisible) Show();
    }

    public void Collapse()
    {
        _collapse.Stop();
        _expanded = false;
        _dragging = false;
        SetHover(null);
        _shelf.IsVisible = false;
        _pill.IsVisible = true;
        PlaceCollapsed();
        UpdateVisibility();
    }

    void Peek()
    {
        Expand();
        _subtitle.Text = "Click a device to send files";
    }

    // ---------------- bubbles ----------------

    void RebuildBubbles()
    {
        var space = _app.Core.Space;
        var devices = space.Devices;
        _defaultId = space.DefaultTarget?.Id;
        _bubbles.Clear();
        _row.Children.Clear();
        foreach (var d in devices)
        {
            var b = MakeBubble(d, d.Id == _defaultId);
            _bubbles.Add(b);
            _row.Children.Add(b.Root);
        }
        if (devices.Count == 0)
        {
            _row.Children.Add(new TextBlock { Text = "No devices yet — pair your phone in H.I.B", Foreground = FlickTheme.Solid(Colors.White, 0.6), FontSize = 12, Margin = new Thickness(0, 20) });
        }
        if (_expanded) PlaceExpanded();
    }

    Bubble MakeBubble(SpaceDevice d, bool isDefault)
    {
        var scale = new ScaleTransform(1, 1);
        var circle = new Border
        {
            Width = 60,
            Height = 60,
            CornerRadius = new CornerRadius(30),
            HorizontalAlignment = HorizontalAlignment.Center,
            BorderThickness = new Thickness(1),
            RenderTransform = scale,
            Child = new Path
            {
                Data = FlickTheme.DeviceGlyph(d.Kind),
                Fill = Brushes.White,
                Width = 26,
                Height = 26,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var name = new TextBlock
        {
            Text = d.Name,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = BubbleWidth - 6,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Control status;
        if (d.InUse)
        {
            status = new Border
            {
                Background = FlickTheme.Solid(FlickTheme.Lime, 0.18),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(6, 1),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock { Text = "In use", FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = FlickTheme.Solid(FlickTheme.Lime) },
            };
        }
        else
        {
            var text = !d.CanReceive ? (d.Online ? "Not reachable" : "Offline") : d.IsPhone ? (d.Battery is int bat ? $"{bat}%" : "Phone") : d.RouteText;
            status = new TextBlock { Text = text, FontSize = 10, Foreground = FlickTheme.Solid(Colors.White, 0.55), HorizontalAlignment = HorizontalAlignment.Center };
        }
        var root = new StackPanel
        {
            Width = BubbleWidth,
            Spacing = 5,
            Background = Brushes.Transparent,
            Opacity = d.CanReceive ? 1 : 0.4,
            Cursor = d.CanReceive ? new Cursor(StandardCursorType.Hand) : Cursor.Default,
            Children = { circle, name, status },
        };
        var b = new Bubble { Device = d, Root = root, Circle = circle, Scale = scale };
        Style(b, hovered: false, isDefault);
        root.PointerPressed += async (_, e) =>
        {
            if (!e.GetCurrentPoint(root).Properties.IsLeftButtonPressed || !d.CanReceive) return;
            await PickAndSendAsync(d);
        };
        return b;
    }

    static void Style(Bubble b, bool hovered, bool isDefault)
    {
        var lit = hovered || isDefault;
        b.Circle.Background = hovered
            ? FlickTheme.Linear(FlickTheme.TopLeft, FlickTheme.BottomRight, (FlickTheme.Cyan, 0.42, 0), (FlickTheme.Panel, 0.95, 1))
            : FlickTheme.GlassFill();
        b.Circle.BorderBrush = lit ? FlickTheme.Solid(FlickTheme.Cyan, hovered ? 0.95 : 0.6) : FlickTheme.GlassStroke();
        b.Circle.BorderThickness = new Thickness(hovered ? 2 : 1);
        b.Circle.BoxShadow = lit
            ? new BoxShadows(new BoxShadow { Blur = hovered ? 22 : 14, Color = FlickTheme.A(FlickTheme.Cyan, hovered ? 0.6 : 0.32) })
            : new BoxShadows(new BoxShadow { OffsetY = 4, Blur = 10, Color = FlickTheme.A(Colors.Black, 0.3) });
        b.Scale.ScaleX = b.Scale.ScaleY = hovered ? 1.12 : 1;
    }

    void SetHover(Bubble? b)
    {
        if (ReferenceEquals(_hover, b)) return;
        var old = _hover;
        _hover = b;
        if (old is not null) Style(old, false, old.Device.Id == _defaultId);
        if (b is not null) Style(b, true, b.Device.Id == _defaultId);
        _subtitle.Text = b is null ? "Drop on a device" : $"Send to {b.Device.Name}";
    }

    Bubble? BubbleAt(Point p)
    {
        foreach (var b in _bubbles)
        {
            var origin = b.Root.TranslatePoint(new Point(0, 0), _root);
            if (origin is null) continue;
            var r = new Rect(origin.Value, b.Root.Bounds.Size).Inflate(new Thickness(BubbleSpacing / 2, 10));
            if (r.Contains(p)) return b;
        }
        return null;
    }

    // ---------------- drag & drop ----------------

    void OnDragEnter(object? sender, DragEventArgs e)
    {
        _collapse.Stop();
        if (FileDrag.IsDraggingOut || FileDrag.LocalPaths(e).Count == 0)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        _dragging = true;
        Expand();
        _subtitle.Text = "Drop on a device";
        OnDragOver(sender, e);
    }

    void OnDragOver(object? sender, DragEventArgs e)
    {
        _collapse.Stop();
        if (!_expanded || FileDrag.IsDraggingOut)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        var b = BubbleAt(e.GetPosition(_root));
        SetHover(b is { Device.CanReceive: true } ? b : null);
        // Dropping on the shelf but between bubbles goes to the highlighted default.
        e.DragEffects = _hover is not null || _defaultId is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (_expanded) _collapse.Start(); // re-entering a child cancels this
    }

    async void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = FileDrag.LocalPaths(e);
        var target = _hover?.Device.Id ?? _defaultId;
        _dragging = false;
        if (paths.Count == 0 || target is null)
        {
            Collapse();
            return;
        }
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        var name = _hover?.Device.Name ?? _app.Core.Space.Find(target)?.Name ?? target;
        _subtitle.Text = paths.Count == 1 ? $"Sending to {name}…" : $"Sending {paths.Count} files to {name}…";
        _busy = true;
        _ = Task.Delay(900).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            _busy = false;
            Collapse();
        }));
        await _send(target, paths);
    }

    async Task PickAndSendAsync(SpaceDevice d)
    {
        _busy = true;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true, Title = $"Send to {d.Name}" });
            var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
            if (paths.Count > 0) _ = _send(d.Id, paths);
        }
        catch { }
        finally
        {
            _busy = false;
            Collapse();
        }
    }
}
