// H.I.B Roam overlays (docs/SPACE.md §9.5–§9.6), in the Flick look (ink #090C21, cyan #20D9FF, lime #BAFF38):
//  - RoamEdgeWindow: near a shared edge, a thin cyan glow runs along it with a small pill "PC-INTERN →".
//  - RoamGhostWindow: a file dragged in from the other computer, as a small card under the cursor.
//  - RoamNoticeWindow: "PC-INTERN is on this side now · Undo" when a moved computer was learned.
//  - RoamDropCatcherWindow: an invisible drop target under the cursor at the edge; it tells whether a file drag
//    is on, and the local drag stays parked on it while the files go across.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using HIB.Core.Roam;
using HIB.Services;
using HIB.Views.Flick;

namespace HIB.Views.Roam;

/// <summary>§9.6 edge glow: click-through, above everything, along the shared edge of one monitor.</summary>
public sealed class RoamEdgeWindow : OverlayWindow
{
    /// <summary>The click-through strip is this deep (DIPs) beside a left/right edge (room for the pill), and above/below.</summary>
    public const double SideDepth = 300;
    public const double TopDepth = 96;
    const double Glow = 30;
    const double PillHeight = 32;

    readonly Border _wash;
    readonly Border _line;
    readonly Border _hot;
    readonly Border _pill;
    readonly TextBlock _text;
    double _along;
    double _length;

    public RoamEdgeWindow(RoamSide side, string screenId) : base("Roam edge", screenId)
    {
        Side = side;
        var horizontal = RoamSides.IsHorizontal(side);
        // Gradients run from the edge inward.
        var (from, to) = side switch
        {
            RoamSide.Left => (new RelativePoint(0, 0.5, RelativeUnit.Relative), new RelativePoint(1, 0.5, RelativeUnit.Relative)),
            RoamSide.Right => (new RelativePoint(1, 0.5, RelativeUnit.Relative), new RelativePoint(0, 0.5, RelativeUnit.Relative)),
            RoamSide.Above => (FlickTheme.Top, FlickTheme.Bottom),
            _ => (FlickTheme.Bottom, FlickTheme.Top),
        };
        _wash = new Border
        {
            Background = FlickTheme.Linear(from, to, (FlickTheme.Cyan, 0.30, 0), (FlickTheme.Cyan, 0.09, 0.45), (FlickTheme.Cyan, 0, 1)),
        };
        var alongStart = horizontal ? FlickTheme.Top : new RelativePoint(0, 0.5, RelativeUnit.Relative);
        var alongEnd = horizontal ? FlickTheme.Bottom : new RelativePoint(1, 0.5, RelativeUnit.Relative);
        _line = new Border
        {
            Background = FlickTheme.Linear(alongStart, alongEnd, (FlickTheme.Cyan, 0.2, 0), (FlickTheme.Cyan, 0.95, 0.5), (FlickTheme.Cyan, 0.2, 1)),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 14, Color = FlickTheme.A(FlickTheme.Cyan, 0.85) }),
        };
        // A brighter spot where the cursor meets the edge.
        _hot = new Border
        {
            Background = new RadialGradientBrush
            {
                Center = new RelativePoint(side == RoamSide.Left ? 0 : side == RoamSide.Right ? 1 : 0.5, side == RoamSide.Above ? 0 : side == RoamSide.Below ? 1 : 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(side == RoamSide.Left ? 0 : side == RoamSide.Right ? 1 : 0.5, side == RoamSide.Above ? 0 : side == RoamSide.Below ? 1 : 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(horizontal ? 1 : 0.5, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(horizontal ? 0.5 : 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(FlickTheme.A(FlickTheme.Cyan, 0.5), 0),
                    new GradientStop(FlickTheme.A(FlickTheme.Cyan, 0.16), 0.55),
                    new GradientStop(FlickTheme.A(FlickTheme.Cyan, 0), 1),
                },
            },
        };
        _text = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var mouse = new HIB.Views.Controls.Icon
        {
            Data = HIB.ViewModels.Icons.Mouse,
            Size = 14,
            StrokeThickness = 2.2,
            Foreground = FlickTheme.Solid(FlickTheme.Cyan),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _pill = new Border
        {
            Height = PillHeight,
            CornerRadius = new CornerRadius(PillHeight / 2),
            Background = FlickTheme.Solid(FlickTheme.Ink, 0.9),
            BorderBrush = FlickTheme.Solid(FlickTheme.Cyan, 0.65),
            BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 24, Color = FlickTheme.A(FlickTheme.Cyan, 0.45) },
                new[] { new BoxShadow { OffsetY = 3, Blur = 12, Color = FlickTheme.A(Colors.Black, 0.35) } }),
            Padding = new Thickness(14, 0),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { mouse, _text } },
        };
        Root.Children.Add(_wash);
        Root.Children.Add(_hot);
        Root.Children.Add(_line);
        Root.Children.Add(_pill);
        Arrange(side);
    }

    public RoamSide Side { get; }
    public string PillText => _text.Text ?? "";
    /// <summary>Where the pill sits along the edge, DIPs from the monitor's top (left/right) or left (top/bottom).</summary>
    public double Along => _along;

    void Arrange(RoamSide side)
    {
        var horizontal = RoamSides.IsHorizontal(side);
        var hAlign = side == RoamSide.Left ? HorizontalAlignment.Left : side == RoamSide.Right ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        var vAlign = side == RoamSide.Above ? VerticalAlignment.Top : side == RoamSide.Below ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        foreach (var b in new[] { _wash, _line, _hot })
        {
            b.HorizontalAlignment = hAlign;
            b.VerticalAlignment = vAlign;
        }
        if (horizontal)
        {
            _wash.Width = Glow;
            _line.Width = 3;
            _hot.Width = Glow * 2.4;
            _pill.HorizontalAlignment = hAlign;
            _pill.VerticalAlignment = VerticalAlignment.Top;
        }
        else
        {
            _wash.Height = Glow;
            _line.Height = 3;
            _hot.Height = Glow * 2.4;
            _pill.VerticalAlignment = vAlign;
            _pill.HorizontalAlignment = HorizontalAlignment.Left;
        }
    }

    /// <summary>Shows <paramref name="pillText"/> beside the edge at <paramref name="along"/> DIPs.</summary>
    public void Update(string pillText, double along)
    {
        _text.Text = pillText;
        _along = along;
        Layout();
    }

    void Layout()
    {
        var len = _length > 0 ? _length : 800;
        var pos = Math.Clamp(_along, PillHeight, Math.Max(PillHeight, len - PillHeight));
        const double hotLength = 260;
        switch (Side)
        {
            case RoamSide.Left:
                _pill.Margin = new Thickness(16, pos - PillHeight / 2, 0, 0);
                _hot.Height = hotLength;
                _hot.VerticalAlignment = VerticalAlignment.Top;
                _hot.Margin = new Thickness(0, pos - hotLength / 2, 0, 0);
                break;
            case RoamSide.Right:
                _pill.Margin = new Thickness(0, pos - PillHeight / 2, 16, 0);
                _hot.Height = hotLength;
                _hot.VerticalAlignment = VerticalAlignment.Top;
                _hot.Margin = new Thickness(0, pos - hotLength / 2, 0, 0);
                break;
            case RoamSide.Above:
                _pill.Margin = new Thickness(Math.Max(8, pos - 90), 16, 0, 0);
                _hot.Width = hotLength;
                _hot.HorizontalAlignment = HorizontalAlignment.Left;
                _hot.Margin = new Thickness(pos - hotLength / 2, 0, 0, 0);
                break;
            default:
                _pill.Margin = new Thickness(Math.Max(8, pos - 90), 0, 0, 16);
                _hot.Width = hotLength;
                _hot.HorizontalAlignment = HorizontalAlignment.Left;
                _hot.Margin = new Thickness(pos - hotLength / 2, 0, 0, 0);
                break;
        }
    }

    protected override PixelRect AreaOn(Screen screen)
    {
        var b = screen.Bounds;
        var scale = screen.Scaling > 0 ? screen.Scaling : 1;
        var side = (int)Math.Round(SideDepth * scale);
        var top = (int)Math.Round(TopDepth * scale);
        return Side switch
        {
            RoamSide.Left => new PixelRect(b.X, b.Y, Math.Min(side, b.Width), b.Height),
            RoamSide.Right => new PixelRect(b.Right - Math.Min(side, b.Width), b.Y, Math.Min(side, b.Width), b.Height),
            RoamSide.Above => new PixelRect(b.X, b.Y, b.Width, Math.Min(top, b.Height)),
            _ => new PixelRect(b.X, b.Bottom - Math.Min(top, b.Height), b.Width, Math.Min(top, b.Height)),
        };
    }

    protected override void OnPlaced(Screen screen, double scaling)
    {
        _length = RoamSides.IsHorizontal(Side) ? screen.Bounds.Height / scaling : screen.Bounds.Width / scaling;
        Layout();
    }

    protected override void Animate(double time, double dt)
    {
        var breath = 0.5 - 0.5 * Math.Cos(time * Math.PI / 1.1);
        _line.Opacity = 0.82 + 0.18 * breath;
        _hot.Opacity = 0.75 + 0.25 * breath;
    }
}

/// <summary>A small click-through card under the cursor while a file drag from the other computer is over this one.</summary>
public sealed class RoamGhostWindow : Window
{
    public const double CardWidth = 232;
    public const double CardHeight = 58;

    readonly TextBlock _name;
    readonly TextBlock _sub;
    readonly HIB.Views.Controls.Icon _glyph;

    public RoamGhostWindow()
    {
        Width = CardWidth + 24;
        Height = CardHeight + 24;
        CanResize = false;
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        TransparencyBackgroundFallback = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "Roam drag";
        Icon = ImageUtil.WindowIcon();
        _glyph = new HIB.Views.Controls.Icon
        {
            Data = HIB.ViewModels.Icons.File,
            Size = 20,
            Foreground = FlickTheme.Solid(FlickTheme.Cyan),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _name = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
        _sub = new TextBlock { FontSize = 11.5, Foreground = FlickTheme.Solid(FlickTheme.Lime, 0.9), Text = "Drop to save it here" };
        Content = new Border
        {
            Margin = new Thickness(12),
            Width = CardWidth,
            Height = CardHeight,
            CornerRadius = new CornerRadius(16),
            Background = FlickTheme.CardFill(),
            BorderBrush = FlickTheme.CardStroke(false),
            BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 18, Color = FlickTheme.A(FlickTheme.Cyan, 0.35) },
                new[] { new BoxShadow { OffsetY = 4, Blur = 14, Color = FlickTheme.A(Colors.Black, 0.45) } }),
            Padding = new Thickness(10, 8),
            Opacity = 0.94,
            IsHitTestVisible = false,
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Children =
                {
                    new Border
                    {
                        Width = 38,
                        Height = 38,
                        CornerRadius = new CornerRadius(11),
                        Background = FlickTheme.Solid(FlickTheme.Cyan, 0.14),
                        Child = _glyph,
                    },
                    new StackPanel
                    {
                        Margin = new Thickness(10, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Spacing = 2,
                        Children = { _name, _sub },
                        [Grid.ColumnProperty] = 1,
                    },
                },
            },
        };
        ClickThrough.Apply(this);
        Opened += (_, _) => ClickThrough.AfterOpen(this);
    }

    public string NameText => _name.Text ?? "";

    public void Update(RoamGhost ghost)
    {
        _name.Text = ghost.Count > 1 && string.IsNullOrEmpty(ghost.Name) ? $"{ghost.Count} files" : ghost.Name;
        _glyph.Data = ghost.Count > 1 ? HIB.ViewModels.Icons.Layers : HIB.ViewModels.Icons.File;
    }

    /// <summary>Puts the card just below-right of the cursor at pixel (<paramref name="px"/>, <paramref name="py"/>).</summary>
    public void MoveTo(double px, double py, double scaling)
    {
        Position = new PixelPoint((int)Math.Round(px + 6 * scaling), (int)Math.Round(py + 10 * scaling));
    }
}

/// <summary>"PC-INTERN is on this side now · Undo" for 5 s at the edge Roam just learned (clickable, never activates).</summary>
public sealed class RoamNoticeWindow : Window
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);
    readonly DispatcherTimer _timer;

    public RoamNoticeWindow(string text, Action undo)
    {
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        TransparencyBackgroundFallback = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "Roam";
        Icon = ImageUtil.WindowIcon();
        Text = text;
        var undoButton = new Button
        {
            Content = "Undo",
            Padding = new Thickness(10, 3),
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = FlickTheme.Solid(FlickTheme.Cyan),
            Background = FlickTheme.Solid(FlickTheme.Cyan, 0.14),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(999),
            VerticalAlignment = VerticalAlignment.Center,
        };
        undoButton.Click += (_, _) =>
        {
            undo();
            Close();
        };
        Content = new Border
        {
            Margin = new Thickness(16),
            Height = 38,
            CornerRadius = new CornerRadius(19),
            Background = FlickTheme.Solid(FlickTheme.Ink, 0.92),
            BorderBrush = FlickTheme.Solid(FlickTheme.Lime, 0.6),
            BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 24, Color = FlickTheme.A(FlickTheme.Lime, 0.35) }),
            Padding = new Thickness(14, 0, 5, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new HIB.Views.Controls.Icon { Data = HIB.ViewModels.Icons.Mouse, Size = 14, StrokeThickness = 2.2, Foreground = FlickTheme.Solid(FlickTheme.Lime), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
                    undoButton,
                },
            },
        };
        FlickCardWindow.ApplyNoActivate(this);
        _timer = new DispatcherTimer { Interval = Lifetime };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Close();
        };
        Opened += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    public string Text { get; }

    public static string TextFor(RoamLearnedNotice n) => $"{n.Name} is on this side now";
}

/// <summary>
/// §9.5 drop catcher: an all-but-invisible drop target (alpha 1/255, so it still takes drops) under the cursor at the
/// edge. A file drag over it reports its paths; the drop itself is ignored (the files go across instead).
/// </summary>
public sealed class RoamDropCatcherWindow : Window
{
    public const double Size = 56;

    public RoamDropCatcherWindow(Action<IReadOnlyList<string>> files)
    {
        Width = Size;
        Height = Size;
        CanResize = false;
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = new SolidColorBrush(Color.FromArgb(1, 0x09, 0x0C, 0x21));
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        TransparencyBackgroundFallback = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "Roam drop";
        Icon = ImageUtil.WindowIcon();
        DragDrop.SetAllowDrop(this, true);
        var surface = new Border { Background = new SolidColorBrush(Color.FromArgb(1, 0x09, 0x0C, 0x21)) };
        DragDrop.SetAllowDrop(surface, true);
        Content = surface;
        void Over(object? sender, DragEventArgs e)
        {
            var paths = FileDrag.LocalPaths(e);
            e.DragEffects = paths.Count > 0 && !FileDrag.IsDraggingOut ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            if (paths.Count > 0 && !FileDrag.IsDraggingOut) files(paths);
        }
        AddHandler(DragDrop.DragEnterEvent, Over);
        AddHandler(DragDrop.DragOverEvent, Over);
        AddHandler(DragDrop.DropEvent, (_, e) => e.Handled = true); // the files already went across
        FlickCardWindow.ApplyNoActivate(this);
    }

    /// <summary>Under the cursor at the edge described by <paramref name="hint"/> (pixels).</summary>
    public void Place(RoamEdgeHint hint, double scaling)
    {
        var px = (int)Math.Round(Size * scaling);
        var s = hint.Screen;
        var along = (int)Math.Round(hint.Along);
        Position = hint.Side switch
        {
            RoamSide.Left => new PixelPoint((int)s.X, along - px / 2),
            RoamSide.Right => new PixelPoint((int)(s.Right - px), along - px / 2),
            RoamSide.Above => new PixelPoint(along - px / 2, (int)s.Y),
            _ => new PixelPoint(along - px / 2, (int)(s.Bottom - px)),
        };
    }
}
