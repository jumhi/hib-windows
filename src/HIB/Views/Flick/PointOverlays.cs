// H.I.B — Point & Flick overlays (SPACE.md §7.4, §7.5), drawn like the Mac's Flick panel: click-through,
// above everything, never taking focus.
//  - AimLightWindow: the screen the phone points at glows cyan from the bottom edge, with a small
//    "Flick lands here" pill at the bottom centre (cards rise from the bottom).
//  - PointTargetWindow: Point setup's target, a full-screen dim with a large pulsing cyan ring that fills
//    with the phone's hold-to-capture progress and turns lime with a check when the screen is learned.
// A new window is made each time one appears, so it shows on the virtual desktop in use.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using HIB.Core.Phone;
using HIB.Services;

namespace HIB.Views.Flick;

/// <summary>A click-through, topmost, non-activating window over part of one monitor, with timed fades.</summary>
public abstract partial class OverlayWindow : Window
{
    public static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(250);

    readonly DispatcherTimer _frame;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _lastFrame;
    double _fadeFrom, _fadeTo = 1, _fadeStart, _fadeLength;
    Action? _fadeDone;
    PixelRect _area;
    double _scaling = 1;

    protected OverlayWindow(string title, string screenId)
    {
        ScreenId = screenId;
        CanResize = false;
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        // Never paint a white sheet over the user's screen where transparency isn't available.
        TransparencyBackgroundFallback = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        Title = title;
        Icon = ImageUtil.WindowIcon();
        Root = new Panel { IsHitTestVisible = false, Opacity = 0 };
        Content = Root;
        ClickThrough.Apply(this);

        _frame = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / 60) };
        _frame.Tick += (_, _) => Frame();
        Opened += (_, _) =>
        {
            ClickThrough.AfterOpen(this);
            ApplyPlacement();
        };
        ScalingChanged += (_, _) => ApplyPlacement();
        Closed += (_, _) => _frame.Stop();
    }

    /// <summary>The Point &amp; Flick id of the monitor this overlay is on.</summary>
    public string ScreenId { get; }
    /// <summary>The monitor area it covers (pixels).</summary>
    public PixelRect Area => _area;
    public bool IsFadingOut { get; private set; }
    public bool IsClosed { get; private set; }
    protected Panel Root { get; }
    protected double AreaWidth => _area.Width / _scaling;
    protected double AreaHeight => _area.Height / _scaling;

    /// <summary>The part of <paramref name="screen"/> this overlay covers, in pixels.</summary>
    protected abstract PixelRect AreaOn(Screen screen);

    /// <summary>Lay the content out for this monitor (sizes in DIPs).</summary>
    protected virtual void OnPlaced(Screen screen, double scaling) { }

    /// <summary>Per frame while shown: <paramref name="time"/> in seconds since the window was made.</summary>
    protected virtual void Animate(double time, double dt) { }

    public void ShowOn(Screen screen)
    {
        Place(screen);
        Root.Opacity = 0;
        Show();
        FadeTo(1, FadeIn, null);
        _frame.Start();
    }

    /// <summary>Moves (or re-fits) the overlay onto <paramref name="screen"/>.</summary>
    public void Place(Screen screen)
    {
        _scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        _area = AreaOn(screen);
        OnPlaced(screen, _scaling);
        ApplyPlacement();
    }

    void ApplyPlacement()
    {
        if (_area.Width <= 0 || _area.Height <= 0) return;
        // Once on the monitor the window's own scaling is the monitor's; before that, use the monitor's.
        var s = IsVisible && DesktopScaling > 0 ? DesktopScaling : _scaling;
        Position = _area.TopLeft;
        Width = _area.Width / s;
        Height = _area.Height / s;
    }

    /// <summary>Fades out (250 ms) and closes. Safe to call twice.</summary>
    public void FadeOutAndClose()
    {
        if (IsFadingOut || IsClosed) return;
        IsFadingOut = true;
        if (!IsVisible)
        {
            CloseNow();
            return;
        }
        FadeTo(0, FadeOut, CloseNow);
        _frame.Start();
    }

    void CloseNow()
    {
        if (IsClosed) return;
        IsClosed = true;
        _frame.Stop();
        try { Close(); } catch { }
    }

    void FadeTo(double target, TimeSpan length, Action? done)
    {
        _fadeFrom = Root.Opacity;
        _fadeTo = target;
        _fadeStart = _clock.Elapsed.TotalSeconds;
        _fadeLength = Math.Max(0.001, length.TotalSeconds);
        _fadeDone = done;
    }

    void Frame()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _lastFrame, 0, 0.1);
        _lastFrame = now;
        if (Math.Abs(Root.Opacity - _fadeTo) > 0.0001 || _fadeDone is not null)
        {
            var t = Math.Clamp((now - _fadeStart) / _fadeLength, 0, 1);
            var eased = _fadeTo > _fadeFrom ? 1 - Math.Pow(1 - t, 3) : t * t * (3 - 2 * t);
            Root.Opacity = _fadeFrom + (_fadeTo - _fadeFrom) * eased;
            if (t >= 1)
            {
                Root.Opacity = _fadeTo;
                var done = _fadeDone;
                _fadeDone = null;
                done?.Invoke();
                if (IsClosed) return;
            }
        }
        Animate(now, dt);
    }
}

/// <summary>Windows: clicks go through the overlay to whatever is under it, and it never activates.</summary>
static partial class ClickThrough
{
    const uint WS_EX_TRANSPARENT = 0x00000020;
    const uint WS_EX_TOOLWINDOW = 0x00000080;
    const uint WS_EX_LAYERED = 0x00080000;
    const uint WS_EX_NOACTIVATE = 0x08000000;
    const uint LWA_ALPHA = 0x2;

    public static void Apply(Window w)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            // WS_EX_LAYERED | WS_EX_TRANSPARENT: hit-testing skips the window, so the click lands below it.
            Win32Properties.AddWindowStylesCallback(w, (style, ex) => (style, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_TRANSPARENT));
            Win32Properties.SetWindowCornerPreference(w, Win32Properties.WindowCornerPreference.DoNotRound);
        }
        catch { /* older shell: a plain topmost window */ }
    }

    /// <summary>A layered window stays invisible until its layered attributes are set: fully opaque (per-pixel alpha still applies).</summary>
    public static void AfterOpen(Window w)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (w.TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != IntPtr.Zero)
                SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
        }
        catch { }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
}

/// <summary>
/// §7.4: a soft cyan glow rising from the bottom edge and a "Flick lands here" pill, the same as the Mac's
/// AimLightView (airsync-mac PointFlickViews.swift).
/// </summary>
public sealed class AimLightWindow : OverlayWindow
{
    /// <summary>The glow covers about this much of the screen's height.</summary>
    public const double GlowRatio = 0.14;
    const double PillHeight = 36;
    public const string Label = "Flick lands here";

    readonly Panel _glow;
    readonly Border _pill;
    readonly TextBlock _label;

    public AimLightWindow(string screenId) : base("Flick aim", screenId)
    {
        // The wash: strongest at the edge, gone by 14% of the height.
        var wash = new Border
        {
            Background = FlickTheme.Linear(FlickTheme.Bottom, FlickTheme.Top,
                (FlickTheme.Cyan, 0.62, 0), (FlickTheme.Cyan, 0.28, 0.35), (FlickTheme.Cyan, 0.08, 0.72), (FlickTheme.Cyan, 0, 1)),
        };
        // A brighter core in the middle, where cards come up.
        var core = new Border
        {
            Background = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.31, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(FlickTheme.A(FlickTheme.Cyan, 0.55), 0),
                    new GradientStop(FlickTheme.A(FlickTheme.Cyan, 0.2), 0.5),
                    new GradientStop(FlickTheme.A(FlickTheme.Cyan, 0), 1),
                },
            },
        };
        // The lit edge.
        var edge = new Border
        {
            Height = 3,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = FlickTheme.Linear(new RelativePoint(0, 0.5, RelativeUnit.Relative), new RelativePoint(1, 0.5, RelativeUnit.Relative),
                (FlickTheme.Cyan, 0.15, 0), (FlickTheme.Cyan, 1, 0.5), (FlickTheme.Cyan, 0.15, 1)),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 16, Color = FlickTheme.A(FlickTheme.Cyan, 0.9) }),
        };
        _glow = new Panel { VerticalAlignment = VerticalAlignment.Bottom, ClipToBounds = true, Children = { wash, core, edge } };

        var hand = new HIB.Views.Controls.Icon
        {
            Data = HIB.ViewModels.Icons.Hand,
            Size = 15,
            StrokeThickness = 2.2,
            Foreground = FlickTheme.Solid(FlickTheme.Cyan),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var text = _label = new TextBlock
        {
            Text = Label,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _pill = new Border
        {
            Height = PillHeight,
            CornerRadius = new CornerRadius(PillHeight / 2),
            Background = FlickTheme.Solid(FlickTheme.Ink, 0.9),
            BorderBrush = FlickTheme.Solid(FlickTheme.Cyan, 0.65),
            BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 28, Color = FlickTheme.A(FlickTheme.Cyan, 0.5) },
                new[] { new BoxShadow { OffsetY = 3, Blur = 12, Color = FlickTheme.A(Colors.Black, 0.35) } }),
            Padding = new Thickness(16, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { hand, text } },
        };
        Root.Children.Add(_glow);
        Root.Children.Add(_pill);
    }

    /// <summary>What the pill says: the phone's label (SPACE.md §11.2, e.g. "Desk Deck") or "Flick lands here".</summary>
    public string LabelText => _label.Text ?? Label;

    public void SetLabel(string? label) => _label.Text = string.IsNullOrWhiteSpace(label) ? Label : label;

    /// <summary>Space taken at the bottom by a taskbar, in DIPs.</summary>
    static double BottomInset(Screen screen, double scaling)
    {
        var b = screen.Bounds;
        var wa = screen.WorkingArea.Width > 0 && screen.WorkingArea.Height > 0 ? screen.WorkingArea : b;
        return Math.Max(0, b.Bottom - Math.Min(wa.Bottom, b.Bottom)) / scaling;
    }

    /// <summary>The pill sits just above a bottom taskbar, otherwise near the edge (the Mac's pillBottom).</summary>
    static double PillBottom(double inset) => Math.Max(18, inset + 12);

    protected override PixelRect AreaOn(Screen screen)
    {
        var b = screen.Bounds;
        var scale = screen.Scaling > 0 ? screen.Scaling : 1;
        // A click-through strip along the bottom edge: the glow, and the pill above any taskbar.
        var strip = Math.Max(Math.Round(b.Height / scale * GlowRatio), PillBottom(BottomInset(screen, scale)) + PillHeight + 22);
        var px = Math.Min(b.Height, (int)Math.Round(strip * scale));
        return new PixelRect(b.X, b.Bottom - px, b.Width, px);
    }

    protected override void OnPlaced(Screen screen, double scaling)
    {
        _glow.Height = Math.Round(screen.Bounds.Height / scaling * GlowRatio);
        _pill.Margin = new Thickness(0, 0, 0, PillBottom(BottomInset(screen, scaling)));
    }

    protected override void Animate(double time, double dt)
    {
        // The Mac's breathing: 0.86 ↔ 1, 1.3 s each way.
        var breath = 0.5 - 0.5 * Math.Cos(time * Math.PI / 1.3);
        _glow.Opacity = 0.86 + 0.14 * breath;
    }
}

/// <summary>§7.5: Point setup's target on one screen, the same as the Mac's PointTargetView.</summary>
public sealed class PointTargetWindow : OverlayWindow
{
    public const string Headline = "Point your phone here";

    readonly PointRing _ring;
    readonly TextBlock _title;
    readonly TextBlock _step;

    public PointTargetWindow(string screenId) : base("Point setup", screenId)
    {
        _ring = new PointRing { HorizontalAlignment = HorizontalAlignment.Center };
        _title = new TextBlock
        {
            Text = Headline,
            FontSize = 30,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _step = new TextBlock
        {
            FontSize = 16,
            FontWeight = FontWeight.Medium,
            Foreground = FlickTheme.Solid(Colors.White, 0.7),
            HorizontalAlignment = HorizontalAlignment.Center,
            FontFeatures = new FontFeatureCollection { new FontFeature { Tag = "tnum" } },
        };
        var column = new StackPanel
        {
            Spacing = 30,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _ring,
                new StackPanel
                {
                    Spacing = 8,
                    Effect = new DropShadowEffect { OffsetX = 0, OffsetY = 2, BlurRadius = 8, Color = Colors.Black, Opacity = 0.4 },
                    Children = { _title, _step },
                },
            },
        };
        Root.Children.Add(new Border { Background = FlickTheme.Solid(FlickTheme.Ink, 0.55) });
        Root.Children.Add(column);
    }

    public AimSetupState? State { get; private set; }
    public PointRing Ring => _ring;
    public string TitleText => _title.Text ?? "";
    public string StepText => _step.Text ?? "";

    public void Update(AimSetupState state)
    {
        State = state;
        _step.Text = $"Screen {state.Step} of {state.Total}";
        _ring.Progress = state.Progress;
        _ring.Complete = state.Complete;
    }

    protected override PixelRect AreaOn(Screen screen) => screen.Bounds;

    protected override void OnPlaced(Screen screen, double scaling)
    {
        var w = screen.Bounds.Width / scaling;
        var h = screen.Bounds.Height / scaling;
        var ring = Math.Min(320, Math.Max(180, Math.Min(w, h) * 0.26));
        _ring.Width = ring;
        _ring.Height = ring;
    }

    protected override void Animate(double time, double dt) => _ring.Advance(time, dt);
}

/// <summary>
/// The target ring: rings keep leaving it outward while it waits, the hold progress fills it, and it turns
/// lime with a check when the screen is learned. Draws past its bounds (the ripples and glows).
/// </summary>
public sealed class PointRing : Control
{
    static readonly Geometry Tick = Geometry.Parse("M20 6 L9 17 L4 12");
    const double Period = 1.6;

    double _target;
    double _shown;
    bool _complete;
    double _time;
    double _completeAt = -1;

    /// <summary>0…1: how much of the ring is filled (eases toward it).</summary>
    public double Progress
    {
        get => _target;
        set => _target = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    }

    /// <summary>The filled fraction drawn now.</summary>
    public double ShownProgress => _shown;

    public bool Complete
    {
        get => _complete;
        set
        {
            if (value && !_complete) _completeAt = _time;
            if (!value) _completeAt = -1;
            _complete = value;
            InvalidateVisual();
        }
    }

    public void Advance(double time, double dt)
    {
        _time = time;
        var goal = _complete ? 1 : _target;
        // About the Mac's 0.18 s ease-out per update.
        _shown += (goal - _shown) * Math.Min(1, dt * 16);
        if (Math.Abs(goal - _shown) < 0.001) _shown = goal;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var r = size / 2;
        var line = Math.Max(10, size * 0.05);
        var accent = _complete ? FlickTheme.Lime : FlickTheme.Cyan;

        if (!_complete)
        {
            // Two rings leaving the target outward (scale 1 → 1.45 and 0.96 → 1.22, fading out, ease-out).
            var e = 1 - Math.Pow(1 - Frac(_time / Period), 2);
            var r1 = r * (1 + 0.45 * e);
            var r2 = r * (0.96 + 0.26 * e);
            ctx.DrawEllipse(null, new Pen(FlickTheme.Solid(accent, 0.7 * 0.85 * (1 - e)), 2), c, r1, r1);
            ctx.DrawEllipse(null, new Pen(FlickTheme.Solid(accent, 0.35 * 0.6 * (1 - e)), 1.5), c, r2, r2);
        }

        // Inside: a soft fill, then the track and the filled arc with its glow.
        ctx.DrawEllipse(new RadialGradientBrush
        {
            GradientStops = { new GradientStop(FlickTheme.A(accent, 0.26), 0), new GradientStop(FlickTheme.A(accent, 0.05), 1) },
        }, null, c, r, r);
        ctx.DrawEllipse(null, new Pen(FlickTheme.Solid(accent, 0.24), line), c, r, r);
        var fill = _complete ? 1 : _shown;
        if (fill > 0.001)
        {
            foreach (var (extra, alpha) in new[] { (16.0, 0.08), (9.0, 0.14), (4.0, 0.22) })
                Stroke(ctx, c, r, fill, new Pen(FlickTheme.Solid(accent, alpha), line + extra, lineCap: PenLineCap.Round));
            Stroke(ctx, c, r, fill, new Pen(FlickTheme.Solid(accent), line, lineCap: PenLineCap.Round));
        }

        if (_complete)
        {
            // The check springs in (0.3 → 1) with a lime glow.
            var t = _completeAt >= 0 ? Math.Clamp((_time - _completeAt) / 0.35, 0, 1) : 1;
            var spring = 1 + 2.70158 * Math.Pow(t - 1, 3) + 1.70158 * Math.Pow(t - 1, 2); // ease-out-back
            var box = size * 0.45 * (0.3 + 0.7 * spring);
            using (ctx.PushOpacity(Math.Clamp(t * 2.5, 0, 1)))
            {
                DrawGlyph(ctx, Tick, c, box, FlickTheme.Solid(FlickTheme.Lime, 0.12), 5.4);
                DrawGlyph(ctx, Tick, c, box, FlickTheme.Solid(FlickTheme.Lime), 2.2);
            }
        }
        else
        {
            // The bullseye the phone aims at.
            var dot = size * 0.035;
            ctx.DrawEllipse(FlickTheme.Solid(FlickTheme.Cyan, 0.12), null, c, dot * 2.6, dot * 2.6);
            ctx.DrawEllipse(FlickTheme.Solid(FlickTheme.Cyan, 0.25), null, c, dot * 1.7, dot * 1.7);
            ctx.DrawEllipse(FlickTheme.Solid(FlickTheme.Cyan), null, c, dot, dot);
        }
    }

    static void Stroke(DrawingContext ctx, Point c, double r, double fraction, Pen pen)
    {
        if (fraction >= 0.999) ctx.DrawEllipse(null, pen, c, r, r);
        else ctx.DrawGeometry(null, pen, Arc(c, r, fraction));
    }

    static void DrawGlyph(DrawingContext ctx, Geometry glyph, Point c, double size, IBrush brush, double stroke)
    {
        var s = size / 24;
        using (ctx.PushTransform(Matrix.CreateTranslation(-12, -11.5) * Matrix.CreateScale(s, s) * Matrix.CreateTranslation(c.X, c.Y)))
            ctx.DrawGeometry(null, new Pen(brush, stroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), glyph);
    }

    static Geometry Arc(Point c, double r, double fraction)
    {
        var start = -Math.PI / 2;
        var end = start + Math.PI * 2 * fraction;
        var g = new StreamGeometry();
        using (var g2 = g.Open())
        {
            g2.BeginFigure(new Point(c.X + r * Math.Cos(start), c.Y + r * Math.Sin(start)), false);
            g2.ArcTo(new Point(c.X + r * Math.Cos(end), c.Y + r * Math.Sin(end)), new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise);
            g2.EndFigure(false);
        }
        return g;
    }

    static double Frac(double x) => x - Math.Floor(x);
}
