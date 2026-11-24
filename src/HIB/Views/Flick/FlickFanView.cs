// H.I.B — a fanned-out Flick bundle (SPACE.md §14): one transparent, non-activating window over the pattern, holding
// the cards (each a real FlickCardView: click opens, drag out, right-click), a soft ink-and-cyan halo behind them
// (clicking it — the empty space — gathers the stack again) and a glass pill with Gather and Show in folder.
using System;
using System.Collections.Generic;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HIB.Services;

namespace HIB.Views.Flick;

public sealed class FlickFanView : Panel
{
    /// <summary>Room under the pattern for the Gather pill.</summary>
    public const double PillSpace = 64;

    readonly Border _halo;
    readonly Border _glow;
    readonly Canvas _canvas;
    readonly Border _pill;
    double _haloT;
    double _haloTarget = 1;
    double _pillT;
    double _pillTarget;

    public FlickFanView(Size size, Point pillCenter)
    {
        Width = size.Width;
        Height = size.Height;
        ClipToBounds = true;

        // The empty space: a dim ink pool with a faint cyan core (no purple), so the cards read on any wallpaper.
        // It has a brush everywhere, so a click on it reaches us (and gathers) instead of the app underneath.
        _halo = new Border
        {
            Background = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(FlickTheme.A(FlickTheme.Ink, 0.46), 0),
                    new GradientStop(FlickTheme.A(FlickTheme.Ink, 0.30), 0.55),
                    new GradientStop(FlickTheme.A(FlickTheme.Ink, 0.004), 1),
                },
            },
        };
        _glow = new Border
        {
            IsHitTestVisible = false,
            Background = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(FlickTheme.A(FlickTheme.Cyan, 0.13), 0),
                    new GradientStop(FlickTheme.A(FlickTheme.Cyan, 0.04), 0.5),
                    new GradientStop(FlickTheme.A(FlickTheme.Cyan, 0), 0.9),
                },
            },
        };
        _canvas = new Canvas { Width = size.Width, Height = size.Height };

        GatherButton = PillButton(FlickTheme.Gather, "Gather");
        FolderButton = PillButton(FlickTheme.Folder, "Show in folder");
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Children =
            {
                GatherButton,
                new Border { Width = 1, Height = 18, Margin = new Thickness(4, 0), Background = FlickTheme.Solid(Colors.White, 0.14), VerticalAlignment = VerticalAlignment.Center },
                FolderButton,
            },
        };
        _pill = new Border
        {
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(5),
            Background = FlickTheme.GlassFill(),
            BorderBrush = FlickTheme.GlassStroke(),
            BorderThickness = new Thickness(0.8),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 10, Blur = 26, Color = FlickTheme.A(Colors.Black, 0.45) },
                new[] { new BoxShadow { Blur = 18, Color = FlickTheme.A(FlickTheme.Cyan, 0.16) } }),
            Child = row,
            Opacity = 0,
            IsHitTestVisible = false,
            RenderTransform = new TranslateTransform(0, 10),
        };
        _pill.Measure(Size.Infinity);
        Canvas.SetLeft(_pill, pillCenter.X - _pill.DesiredSize.Width / 2);
        Canvas.SetTop(_pill, pillCenter.Y - _pill.DesiredSize.Height / 2);

        Children.Add(_halo);
        Children.Add(_glow);
        Children.Add(_canvas);
        _canvas.Children.Add(_pill);
        Animate(0);
    }

    public Border GatherButton { get; }
    public Border FolderButton { get; }
    /// <summary>The empty space around the cards.</summary>
    public Control Halo => _halo;
    public IReadOnlyList<Control> CardViews => _cards;
    readonly List<Control> _cards = new();

    /// <summary>Adds a card under the ones already added (add the top card last).</summary>
    public void AddCard(FlickCardView view)
    {
        view.RenderTransform = new TransformGroup { Children = { new ScaleTransform(), new RotateTransform() } };
        _cards.Add(view);
        _canvas.Children.Insert(Math.Max(0, _canvas.Children.Count - 1), view); // the pill stays on top
    }

    public void RemoveCard(FlickCardView view)
    {
        _cards.Remove(view);
        _canvas.Children.Remove(view);
    }

    /// <summary>Puts a card where its body is (fan-local DIPs).</summary>
    public static void Place(FlickCardView view, in FlickBody b)
    {
        Canvas.SetLeft(view, b.X - FlickCardView.ContainerWidth / 2);
        Canvas.SetTop(view, b.Y - FlickCardView.ContainerHeight / 2);
        if (view.RenderTransform is TransformGroup g)
        {
            var s = (ScaleTransform)g.Children[0];
            s.ScaleX = s.ScaleY = b.Scale;
            ((RotateTransform)g.Children[1]).Angle = b.Rot;
        }
        view.Opacity = Math.Clamp(b.Alpha, 0, 1);
    }

    /// <summary>Lifts a hovered card above its neighbours.</summary>
    public static void Raise(FlickCardView view, bool raised) => view.ZIndex = raised ? 10 : 0;

    public bool PillShown => _pillTarget > 0;

    /// <summary>Shows the Gather pill (once the cards have settled) or hides it.</summary>
    public void ShowPill(bool show)
    {
        _pillTarget = show ? 1 : 0;
        _pill.IsHitTestVisible = show;
    }

    /// <summary>Fades the halo away (gathering) or in.</summary>
    public void ShowHalo(bool show)
    {
        _haloTarget = show ? 1 : 0;
        _halo.IsHitTestVisible = show;
    }

    public bool Animate(double dt)
    {
        var busy = false;
        if (Math.Abs(_haloT - _haloTarget) > 0.001 || dt == 0)
        {
            _haloT += Math.Sign(_haloTarget - _haloT) * Math.Min(Math.Abs(_haloTarget - _haloT), dt / 0.28);
            var e = 1 - Math.Pow(1 - _haloT, 2);
            _halo.Opacity = e;
            _glow.Opacity = e;
            busy = true;
        }
        if (Math.Abs(_pillT - _pillTarget) > 0.001 || dt == 0)
        {
            _pillT += Math.Sign(_pillTarget - _pillT) * Math.Min(Math.Abs(_pillTarget - _pillT), dt / 0.22);
            var e = 1 - Math.Pow(1 - _pillT, 3);
            _pill.Opacity = e;
            ((TranslateTransform)_pill.RenderTransform!).Y = 10 * (1 - e);
            busy = true;
        }
        return busy;
    }

    /// <summary>Snaps the halo and pill to their targets (design shots).</summary>
    public void Settle()
    {
        _haloT = _haloTarget;
        _pillT = _pillTarget;
        _halo.Opacity = _glow.Opacity = _haloT;
        _pill.Opacity = _pillT;
        ((TranslateTransform)_pill.RenderTransform!).Y = 10 * (1 - _pillT);
    }

    static Border PillButton(Geometry glyph, string text)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(12, 7),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                Children =
                {
                    new Path { Data = glyph, Fill = FlickTheme.Solid(FlickTheme.Cyan), Width = 13, Height = 13, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        b.PointerEntered += (_, _) => b.Background = FlickTheme.Solid(FlickTheme.Cyan, 0.14);
        b.PointerExited += (_, _) => b.Background = Brushes.Transparent;
        return b;
    }
}

/// <summary>The window around a fan: borderless, transparent, always on top, never takes focus.</summary>
public sealed class FlickFanWindow : Window
{
    public FlickFanWindow(FlickFanView view)
    {
        View = view;
        Width = view.Width;
        Height = view.Height;
        CanResize = false;
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "Flick";
        Icon = ImageUtil.WindowIcon();
        Content = view;
        FlickCardWindow.ApplyNoActivate(this);
    }

    public FlickFanView View { get; }
}

/// <summary>The window around a bundle's stack (like <see cref="FlickCardWindow"/>, a little larger).</summary>
public sealed class FlickStackWindow : Window
{
    public FlickStackWindow(FlickStackView view)
    {
        View = view;
        Width = FlickStackView.ContainerWidth;
        Height = FlickStackView.ContainerHeight;
        CanResize = false;
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "Flick";
        Icon = ImageUtil.WindowIcon();
        Content = view;
        FlickCardWindow.ApplyNoActivate(this);
    }

    public FlickStackView View { get; }
}
