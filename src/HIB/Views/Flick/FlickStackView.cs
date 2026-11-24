// H.I.B — a Flick bundle as one stacked card (SPACE.md §14): the first item on top (the usual card face, with the
// whole bundle's progress), the next three peeking out behind with small rotations, and a count badge — cyan while
// the files arrive, lime once they all did. Hovering spreads the peeking cards a little and offers "Spread out".
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace HIB.Views.Flick;

/// <summary>A card behind the top one: only its edges show (a slice of its preview and its stroke).</summary>
public sealed class FlickPeekCard : Panel
{
    readonly Image _thumb;
    readonly Border _wash;
    readonly Path _glyph;

    public FlickPeekCard()
    {
        Width = FlickCardView.CardWidth;
        Height = FlickCardView.CardHeight;
        IsHitTestVisible = false;
        _thumb = new Image { Stretch = Stretch.UniformToFill, Width = FlickCardView.CardWidth, Height = FlickCardView.PreviewHeight };
        _wash = new Border
        {
            Background = FlickTheme.Linear(FlickTheme.TopLeft, FlickTheme.BottomRight, (FlickTheme.Cyan, 0.34, 0), (FlickTheme.Midnight, 1, 0.7), (FlickTheme.Ink, 1, 1)),
        };
        _glyph = new Path
        {
            Fill = FlickTheme.Solid(Colors.White, 0.85),
            Width = 34,
            Height = 34,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var preview = new Panel
        {
            Width = FlickCardView.CardWidth,
            Height = FlickCardView.PreviewHeight,
            ClipToBounds = true,
            Children = { _wash, _glyph, _thumb },
        };
        // Two faint lines where the name and the subtitle would be.
        var lines = new StackPanel
        {
            Margin = new Thickness(14, 0),
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border { Height = 8, Width = 120, CornerRadius = new CornerRadius(4), Background = FlickTheme.Solid(Colors.White, 0.22), HorizontalAlignment = HorizontalAlignment.Left },
                new Border { Height = 6, Width = 78, CornerRadius = new CornerRadius(3), Background = FlickTheme.Solid(Colors.White, 0.12), HorizontalAlignment = HorizontalAlignment.Left },
            },
        };
        var grid = new Grid { RowDefinitions = new RowDefinitions($"{FlickCardView.PreviewHeight.ToString(CultureInfo.InvariantCulture)},*") };
        grid.Children.Add(preview);
        Grid.SetRow(lines, 1);
        grid.Children.Add(lines);
        Children.Add(new Border
        {
            CornerRadius = new CornerRadius(22),
            Background = FlickTheme.Solid(FlickTheme.Ink),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 8, Blur = 22, Color = FlickTheme.A(Colors.Black, 0.45) }),
        });
        Children.Add(new Border
        {
            CornerRadius = new CornerRadius(22),
            ClipToBounds = true,
            Background = FlickTheme.CardFill(),
            Child = grid,
        });
        Children.Add(new Border
        {
            CornerRadius = new CornerRadius(22),
            BorderThickness = new Thickness(1),
            BorderBrush = FlickTheme.CardStroke(false),
        });
        RenderTransform = new TransformGroup { Children = { new RotateTransform(), new TranslateTransform() } };
    }

    /// <summary>Shows <paramref name="card"/> (null: an item that hasn't announced itself yet).</summary>
    public void Show(FlickCard? card)
    {
        _thumb.Source = card?.Thumbnail;
        _thumb.IsVisible = card?.Thumbnail is not null;
        _glyph.Data = FlickTheme.KindGlyph(card?.Kind ?? "file");
        _glyph.IsVisible = card?.Thumbnail is null;
    }

    public void Place(double x, double y, double rot)
    {
        var g = (TransformGroup)RenderTransform!;
        ((RotateTransform)g.Children[0]).Angle = rot;
        var t = (TranslateTransform)g.Children[1];
        t.X = x;
        t.Y = y;
    }
}

/// <summary>The stack: peeking cards, the top card (a <see cref="FlickCardView"/> showing the bundle) and the count badge.</summary>
public sealed class FlickStackView : Panel
{
    /// <summary>Room around the card for the peeking cards, their rotation, the badge, the glow and the hover hint.</summary>
    public const double Pad = 60;
    public const double ContainerWidth = FlickCardView.CardWidth + Pad * 2;
    public const double ContainerHeight = FlickCardView.CardHeight + Pad * 2;

    /// <summary>Where the 2nd, 3rd and 4th items peek out (offset from the top card, DIPs, and rotation in degrees).</summary>
    public static readonly (double X, double Y, double Rot)[] Peeks = [(12, -9, 6.5), (-13, -6, -7), (3, -17, 2.5)];

    readonly FlickCard _face;
    readonly FlickCardView _top;
    readonly FlickPeekCard[] _peeks;
    readonly Border _badge;
    readonly TextBlock _badgeText;
    readonly Border _hint;
    double _spread;
    double _badgePop = -1;
    bool _wasComplete;

    public FlickStackView(FlickBundle bundle)
    {
        Bundle = bundle;
        Width = ContainerWidth;
        Height = ContainerHeight;
        Background = Brushes.Transparent;

        _face = new FlickCard("bundle:" + bundle.Id, "file", bundle.Title, "", 0, FlickTransfer.Waiting) { BadgeKind = "stack" };
        _top = new FlickCardView(_face);
        _peeks = Enumerable.Range(0, Peeks.Length).Select(_ => new FlickPeekCard()).ToArray();

        _badgeText = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = FlickTheme.Solid(FlickTheme.Ink),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _badge = new Border
        {
            MinWidth = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(8, 0),
            BorderThickness = new Thickness(2.5),
            BorderBrush = FlickTheme.Solid(FlickTheme.Ink),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(Pad + FlickCardView.CardWidth - 22, Pad - 12, 0, 0),
            Child = _badgeText,
            IsHitTestVisible = false,
            RenderTransform = new ScaleTransform(1, 1),
        };
        _hint = new Border
        {
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(10, 4),
            Background = FlickTheme.GlassFill(),
            BorderBrush = FlickTheme.GlassStroke(),
            BorderThickness = new Thickness(0.8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, Pad + FlickCardView.CardHeight + 14, 0, 0),
            Opacity = 0,
            IsHitTestVisible = false,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new Path { Data = FlickTheme.Stack, Fill = FlickTheme.Solid(FlickTheme.Cyan), Width = 11, Height = 11, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "Click to spread out", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = FlickTheme.Solid(Colors.White, 0.85) },
                },
            },
        };

        for (var i = _peeks.Length - 1; i >= 0; i--)
        {
            _peeks[i].HorizontalAlignment = HorizontalAlignment.Center;
            _peeks[i].VerticalAlignment = VerticalAlignment.Center;
            Children.Add(_peeks[i]);
        }
        _top.HorizontalAlignment = HorizontalAlignment.Center;
        _top.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(_top);
        Children.Add(_badge);
        Children.Add(_hint);
        Refresh();
        Animate(0);
    }

    public FlickBundle Bundle { get; }

    /// <summary>The top card's face: the stack's hit area.</summary>
    public Control Face => _top.Face;

    /// <summary>What the top card shows (title, whole-bundle progress, first preview).</summary>
    public FlickCard FaceCard => _face;

    public FlickCardView Top => _top;

    /// <summary>How many cards peek out behind the top one.</summary>
    public int PeekCount => _peeks.Count(p => p.IsVisible);

    public string BadgeText => _badgeText.Text ?? "";

    public void Pulse() => _top.Pulse();

    /// <summary>Re-reads the bundle into the visuals.</summary>
    public void Refresh()
    {
        var b = Bundle;
        var top = b.Top;
        var f = _face;
        f.Kind = top?.Kind ?? "file";
        f.Name = b.Title;
        f.Text = top?.Text;
        f.Thumbnail = top?.Thumbnail;
        f.From = b.From;
        f.Size = b.TotalSize;
        f.Landed = b.Landed;
        f.Hovered = b.Hovered;
        f.Progress = b.Progress;
        f.Transfer = b.IsBusy ? (b.Arrived == 0 && b.Progress <= 0 ? FlickTransfer.Waiting : FlickTransfer.Receiving)
            : b.Arrived == 0 && b.Failed > 0 ? FlickTransfer.Failed
            : FlickTransfer.Done;
        f.FailReason = b.Arrived == 0 ? "Didn't arrive" : null;
        f.SubtitleOverride = b.Subtitle;
        _top.Refresh();

        // The next items (or blank cards for items still on their way) peek out behind.
        var behind = b.Items.Where(c => c is not { Taken: true }).Skip(1).Take(_peeks.Length).ToList();
        for (var i = 0; i < _peeks.Length; i++)
        {
            _peeks[i].IsVisible = i < behind.Count;
            if (i < behind.Count) _peeks[i].Show(behind[i]);
        }

        var complete = !b.IsBusy && b.Failed == 0;
        _badgeText.Text = b.Badge;
        _badge.Background = FlickTheme.Solid(complete ? FlickTheme.Lime : FlickTheme.Cyan);
        _badge.BoxShadow = new BoxShadows(new BoxShadow { Blur = 14, Color = FlickTheme.A(complete ? FlickTheme.Lime : FlickTheme.Cyan, 0.55) });
        if (complete && !_wasComplete) _badgePop = 0;
        _wasComplete = complete;
    }

    /// <summary>Advances the hover spread, the badge pop and the top card's own animations.</summary>
    public bool Animate(double dt)
    {
        var busy = _top.Animate(dt);
        var target = Bundle.Hovered ? 1.0 : 0.0;
        if (Math.Abs(_spread - target) > 0.001 || dt == 0)
        {
            _spread += (target - _spread) * Math.Min(1, dt * 12);
            if (Math.Abs(_spread - target) < 0.002) _spread = target;
            for (var i = 0; i < _peeks.Length; i++)
            {
                var (x, y, r) = Peeks[i];
                _peeks[i].Place(x * (1 + 0.7 * _spread), y * (1 + 0.7 * _spread), r * (1 + 0.55 * _spread));
            }
            _hint.Opacity = _spread;
            busy = true;
        }
        if (_badgePop >= 0)
        {
            _badgePop += dt;
            var k = Math.Min(1, _badgePop / 0.45);
            var s = 1 + 0.35 * Math.Sin(k * Math.PI) * (1 - k * 0.4);
            var st = (ScaleTransform)_badge.RenderTransform!;
            st.ScaleX = st.ScaleY = s;
            if (k >= 1) { _badgePop = -1; st.ScaleX = st.ScaleY = 1; }
            busy = true;
        }
        return busy;
    }

    /// <summary>Where the item at <paramref name="place"/> sits in the stack, relative to the top card's centre,
    /// for a stack turned by <paramref name="stackRot"/> degrees (gathering cards fly there).</summary>
    public static (double X, double Y, double Rot) StackSpot(int place, double stackRot)
    {
        if (place <= 0 || place > Peeks.Length) return (0, 0, stackRot);
        var (x, y, r) = Peeks[place - 1];
        var a = stackRot * Math.PI / 180;
        return (x * Math.Cos(a) - y * Math.Sin(a), x * Math.Sin(a) + y * Math.Cos(a), stackRot + r);
    }
}
