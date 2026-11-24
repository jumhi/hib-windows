// H.I.B — the face of a flicked card, matching the Mac's FlickCardContent.swift: a 216×236 rounded card
// (panel → ink) with a 156 pt preview, a kind badge, a progress ring while the file arrives, a lime check
// when it lands, the name and "From <device> · size", a cyan glow and a landing ripple.
using System;
using System.Globalization;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using HIB.Core.Platform;
using HIB.Core.Space;

namespace HIB.Views.Flick;

public enum FlickTransfer { None, Waiting, Receiving, Done, Failed }

/// <summary>What one card shows (the Mac's FlickCardModel without the physics).</summary>
public sealed class FlickCard
{
    public FlickCard(string id, string kind, string name, string mime, long size, FlickTransfer transfer)
    {
        Id = id;
        Kind = kind;
        Name = name;
        Mime = mime;
        Size = size;
        Transfer = transfer;
    }

    public string Id { get; }
    public string Kind { get; set; }
    public string Name { get; set; }
    public string Mime { get; set; }
    public long Size { get; set; }
    public Bitmap? Thumbnail { get; set; }
    public string? Text { get; set; }
    public string? From { get; set; }
    public double Progress { get; set; }
    public FlickTransfer Transfer { get; set; }
    public string? FailReason { get; set; }
    public string? FilePath { get; set; }
    public bool Landed { get; set; }
    public bool Hovered { get; set; }
    public string? Note { get; set; }
    /// <summary>Replaces the computed subtitle (a bundle's stack: "Receiving 2 of 5 · 40%").</summary>
    public string? SubtitleOverride { get; set; }
    /// <summary>The small glyph top-left, when it isn't the kind's own ("stack" for a bundle).</summary>
    public string? BadgeKind { get; set; }
    /// <summary>For the "+N" card of a fanned bundle: how many more the folder holds.</summary>
    public int More { get; set; }
    /// <summary>Dragged out of a fanned bundle: it left the bundle.</summary>
    public bool Taken { get; set; }
    /// <summary>Seconds left before auto-dismiss (counts down once landed, idle and not hovered).</summary>
    public double Remaining { get; set; } = 12;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;

    public bool IsBusy => Transfer is FlickTransfer.Waiting or FlickTransfer.Receiving;

    /// <summary>For link cards: a URL that can be opened (adds https:// to a bare host).</summary>
    public string? LinkUrl
    {
        get
        {
            if (Kind != "link" || string.IsNullOrWhiteSpace(Text)) return null;
            var raw = Text.Trim();
            var url = Uri.TryCreate(raw, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Scheme) && raw.Contains("://") ? raw : "https://" + raw;
            return PlatformShell.IsSafeUrl(url) ? url : null;
        }
    }

    public string? LinkHost => LinkUrl is { } l && Uri.TryCreate(l, UriKind.Absolute, out var u) ? u.Host : null;

    public string Subtitle
    {
        get
        {
            if (Note is not null) return Note;
            if (SubtitleOverride is not null) return SubtitleOverride;
            if (From is { Length: > 0 } from && Transfer != FlickTransfer.Failed) return $"From {from} · {BaseSubtitle}";
            return BaseSubtitle;
        }
    }

    string BaseSubtitle => Transfer switch
    {
        FlickTransfer.Waiting => From is { Length: > 0 } ? "Waiting…" : "Waiting for phone…",
        FlickTransfer.Receiving => $"Receiving {(int)(Progress * 100)}%",
        FlickTransfer.Failed => FailReason ?? "Didn't arrive",
        _ => Kind switch
        {
            "link" => LinkHost ?? "Link",
            "text" => "Copied to clipboard",
            _ => Size > 0 ? FormatSize(Size) : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Kind),
        },
    };

    public static string FormatSize(long bytes)
    {
        string[] units = { "bytes", "KB", "MB", "GB" };
        double v = bytes;
        var i = 0;
        while (v >= 1000 && i < units.Length - 1) { v /= 1000; i++; }
        return i == 0 ? $"{bytes} bytes" : $"{v:0.#} {units[i]}";
    }
}

/// <summary>A ring: track + arc (progress) or a spinning arc (waiting).</summary>
public sealed class FlickRing : Control
{
    public double Progress { get; set; }
    public bool Spinning { get; set; }
    public double SpinAngle { get; set; }
    public double Thickness { get; set; } = 4;

    public override void Render(DrawingContext context)
    {
        var r = Math.Min(Bounds.Width, Bounds.Height) / 2 - Thickness / 2;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        context.DrawEllipse(null, new Pen(FlickTheme.Solid(Colors.White, 0.18), Thickness), c, r, r);
        double start, sweep;
        if (Spinning)
        {
            start = SpinAngle;
            sweep = 100;
        }
        else
        {
            start = -90;
            sweep = 360 * Math.Clamp(Math.Max(0.02, Progress), 0, 0.9999);
        }
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            var a0 = start * Math.PI / 180;
            var a1 = (start + sweep) * Math.PI / 180;
            ctx.BeginFigure(new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0)), false);
            ctx.ArcTo(new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1)), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(FlickTheme.Solid(Spinning ? Colors.White : FlickTheme.Cyan), Thickness, lineCap: PenLineCap.Round), g);
    }
}

/// <summary>The card visuals inside a transparent container (the margin holds the glow, shadow and ripple).</summary>
public sealed class FlickCardView : Panel
{
    public const double CardWidth = 216;
    public const double CardHeight = 236;
    public const double PreviewHeight = 156;
    public const double Pad = 40;
    public const double ContainerWidth = CardWidth + Pad * 2;
    public const double ContainerHeight = CardHeight + Pad * 2;
    const double Radius = 22;

    readonly Border _shadow;
    readonly Border _card;
    readonly Border _stroke;
    readonly Border _ripple;
    readonly Panel _preview;
    readonly Image _thumb;
    readonly Panel _placeholder;
    readonly Path _play;
    readonly Path _badgeGlyph;
    readonly TextBlock _name;
    readonly TextBlock _subtitle;
    readonly Panel _status;
    readonly FlickRing _ring;
    readonly Border _failed;
    readonly Path _check;
    double _rippleT = 2;
    double _checkT = -1;
    FlickTransfer _lastTransfer;
    string? _placeholderKey;

    public FlickCardView(FlickCard card)
    {
        Card = card;
        Width = ContainerWidth;
        Height = ContainerHeight;
        Background = Brushes.Transparent;

        _ripple = new Border
        {
            Width = CardWidth,
            Height = CardHeight,
            CornerRadius = new CornerRadius(Radius),
            BorderBrush = FlickTheme.Solid(FlickTheme.Cyan),
            BorderThickness = new Thickness(3),
            Opacity = 0,
            IsHitTestVisible = false,
            RenderTransform = new ScaleTransform(1, 1),
        };
        _shadow = new Border
        {
            Width = CardWidth,
            Height = CardHeight,
            CornerRadius = new CornerRadius(Radius),
            Background = FlickTheme.Solid(FlickTheme.Ink),
            IsHitTestVisible = false,
        };

        _thumb = new Image { Stretch = Stretch.UniformToFill, Width = CardWidth, Height = PreviewHeight };
        _placeholder = new Panel();
        _play = new Path
        {
            Data = FlickTheme.PlayCircle,
            Fill = FlickTheme.Solid(Colors.White, 0.92),
            Width = 34,
            Height = 34,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        _badgeGlyph = new Path { Fill = Brushes.White, Width = 11, Height = 11, Stretch = Stretch.Uniform };
        var badge = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = FlickTheme.Solid(Colors.Black, 0.45),
            BorderBrush = FlickTheme.Solid(Colors.White, 0.2),
            BorderThickness = new Thickness(0.5),
            Margin = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = _badgeGlyph,
        };

        _ring = new FlickRing { Width = 40, Height = 40 };
        _status = new Panel
        {
            Width = 54,
            Height = 54,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Ellipse { Fill = FlickTheme.Solid(Colors.Black, 0.5) },
                _ring,
            },
        };
        _failed = new Border
        {
            Width = 54,
            Height = 54,
            CornerRadius = new CornerRadius(27),
            Background = FlickTheme.Solid(Colors.Black, 0.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Path { Data = FlickTheme.Alert, Fill = FlickTheme.Solid(FlickTheme.Danger), Width = 26, Height = 26, Stretch = Stretch.Uniform },
            IsVisible = false,
        };
        _check = new Path
        {
            Data = FlickTheme.CheckCircle,
            Fill = FlickTheme.Solid(FlickTheme.Lime),
            Width = 40,
            Height = 40,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            RenderTransform = new ScaleTransform(0.4, 0.4),
            Effect = new DropShadowEffect { Color = FlickTheme.A(Colors.Black, 0.35), BlurRadius = 10, OffsetX = 0, OffsetY = 2 },
        };

        _preview = new Panel
        {
            Width = CardWidth,
            Height = PreviewHeight,
            ClipToBounds = true,
            Children = { _placeholder, _thumb, _play, badge, _status, _failed, _check },
        };

        _name = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };
        _subtitle = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeight.Medium,
            Foreground = FlickTheme.Solid(Colors.White, 0.6),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            Margin = new Thickness(0, 3, 0, 0),
        };
        var text = new StackPanel
        {
            Margin = new Thickness(14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _name, _subtitle },
        };
        var grid = new Grid { RowDefinitions = new RowDefinitions($"{PreviewHeight.ToString(CultureInfo.InvariantCulture)},*") };
        grid.Children.Add(_preview);
        Grid.SetRow(text, 1);
        grid.Children.Add(text);

        _card = new Border
        {
            Width = CardWidth,
            Height = CardHeight,
            CornerRadius = new CornerRadius(Radius),
            ClipToBounds = true,
            Background = FlickTheme.CardFill(),
            Child = grid,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        _stroke = new Border
        {
            Width = CardWidth,
            Height = CardHeight,
            CornerRadius = new CornerRadius(Radius),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
        };

        Children.Add(_ripple);
        Children.Add(_shadow);
        Children.Add(_card);
        Children.Add(_stroke);
        foreach (var c in Children)
        {
            c.HorizontalAlignment = HorizontalAlignment.Center;
            c.VerticalAlignment = VerticalAlignment.Center;
        }
        Refresh();
    }

    public FlickCard Card { get; }

    /// <summary>The card rectangle (hit area) inside this view.</summary>
    public Control Face => _card;

    /// <summary>Plays the landing ripple.</summary>
    public void Pulse() => _rippleT = 0;

    /// <summary>Re-reads <see cref="Card"/> into the visuals.</summary>
    public void Refresh()
    {
        var c = Card;
        _name.Text = MiddleEllipsis(c.Name, 30);
        _subtitle.Text = c.Subtitle;
        _subtitle.Foreground = c.Transfer == FlickTransfer.Failed && c.Note is null
            ? FlickTheme.Solid(FlickTheme.Danger, 0.9)
            : FlickTheme.Solid(Colors.White, 0.6);
        _badgeGlyph.Data = FlickTheme.KindGlyph(c.BadgeKind ?? c.Kind);
        _thumb.Source = c.Thumbnail;
        _thumb.IsVisible = c.Thumbnail is not null;
        _play.IsVisible = c.Thumbnail is not null && c.Kind == "video";
        _placeholder.IsVisible = c.Thumbnail is null;
        if (c.Thumbnail is null) BuildPlaceholder(c);

        _status.IsVisible = c.IsBusy;
        _ring.Spinning = c.Transfer == FlickTransfer.Waiting;
        _ring.Progress = c.Progress;
        _ring.InvalidateVisual();
        _failed.IsVisible = c.Transfer == FlickTransfer.Failed;
        if (c.Transfer == FlickTransfer.Done && _lastTransfer != FlickTransfer.Done) _checkT = 0;
        _lastTransfer = c.Transfer;

        // Mac: a strong cyan glow while flying, soft once landed, brighter when hovered.
        var glow = c.Landed ? (c.Hovered ? 0.45 : 0.18) : 0.55;
        _shadow.BoxShadow = new BoxShadows(
            new BoxShadow { OffsetX = 0, OffsetY = 14, Blur = 30, Color = FlickTheme.A(Colors.Black, 0.42) },
            new[] { new BoxShadow { Blur = c.Landed ? 22 : 34, Spread = 0, Color = FlickTheme.A(FlickTheme.Cyan, glow) } });
        _stroke.BorderBrush = FlickTheme.CardStroke(c.Hovered);
    }

    void BuildPlaceholder(FlickCard c)
    {
        var key = $"{c.Kind}|{c.Text}|{c.Name}|{c.More}";
        if (key == _placeholderKey) return;
        _placeholderKey = key;
        _placeholder.Children.Clear();
        // Mac: a soft diagonal wash behind an icon — navy and cyan only (no purple on Windows).
        _placeholder.Children.Add(new Border
        {
            Background = FlickTheme.Linear(FlickTheme.TopLeft, FlickTheme.BottomRight,
                (FlickTheme.Cyan, 0.42, 0), (FlickTheme.Midnight, 1, 0.68), (FlickTheme.Ink, 1, 1)),
        });
        Control content;
        switch (c.Kind)
        {
            case "more":
                // A fanned bundle's "+N" card: the rest wait in the folder.
                content = new StackPanel
                {
                    Spacing = 2,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "+" + c.More.ToString(CultureInfo.InvariantCulture),
                            FontSize = 46,
                            FontWeight = FontWeight.Bold,
                            Foreground = FlickTheme.Solid(FlickTheme.Cyan),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Effect = new DropShadowEffect { Color = FlickTheme.A(FlickTheme.Cyan, 0.55), BlurRadius = 18, OffsetX = 0, OffsetY = 0 },
                        },
                        new TextBlock { Text = "more in the folder", FontSize = 11, FontWeight = FontWeight.Medium, Foreground = FlickTheme.Solid(Colors.White, 0.7), HorizontalAlignment = HorizontalAlignment.Center },
                    },
                };
                break;
            case "link":
                content = new StackPanel
                {
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new Path { Data = FlickTheme.Globe, Fill = Brushes.White, Width = 40, Height = 40, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = c.LinkHost ?? "", FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = FlickTheme.Solid(Colors.White, 0.9),
                            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = CardWidth - 24, HorizontalAlignment = HorizontalAlignment.Center },
                    },
                };
                break;
            case "text":
                content = new TextBlock
                {
                    Text = c.Text ?? "",
                    FontSize = 12,
                    FontWeight = FontWeight.Medium,
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 5,
                    Margin = new Thickness(14, 42, 14, 10),
                    VerticalAlignment = VerticalAlignment.Top,
                };
                break;
            default:
                var ext = System.IO.Path.GetExtension(c.Name).TrimStart('.').ToUpperInvariant();
                content = new Panel
                {
                    Width = 64,
                    Height = 78,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Effect = new DropShadowEffect { Color = FlickTheme.A(Colors.Black, 0.3), BlurRadius = 14, OffsetY = 4, OffsetX = 0 },
                    Children =
                    {
                        new Path { Data = FlickTheme.KindGlyph(c.Kind == "file" ? "file" : c.Kind), Fill = FlickTheme.Solid(Colors.White, 0.95), Stretch = Stretch.Uniform },
                        new Border
                        {
                            IsVisible = ext.Length is > 0 and <= 5 && c.Kind == "file",
                            Background = FlickTheme.Solid(FlickTheme.Ink, 0.92),
                            CornerRadius = new CornerRadius(4),
                            Padding = new Thickness(5, 1),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Bottom,
                            Margin = new Thickness(0, 0, 0, 12),
                            Child = new TextBlock { Text = ext, FontSize = 10, FontWeight = FontWeight.Bold, Foreground = FlickTheme.Solid(FlickTheme.Cyan) },
                        },
                    },
                };
                break;
        }
        _placeholder.Children.Add(content);
    }

    /// <summary>Advances the ripple, spinner and check animations.</summary>
    public bool Animate(double dt)
    {
        var busy = false;
        if (_rippleT < 1)
        {
            _rippleT = Math.Min(1, _rippleT + dt / 0.75);
            var e = 1 - Math.Pow(1 - _rippleT, 3); // ease-out
            var s = 1 + 0.22 * e;
            ((ScaleTransform)_ripple.RenderTransform!).ScaleX = s;
            ((ScaleTransform)_ripple.RenderTransform!).ScaleY = s;
            _ripple.Opacity = 0.9 * (1 - e);
            busy = true;
        }
        if (_ring.Spinning && _status.IsVisible)
        {
            _ring.SpinAngle = (_ring.SpinAngle + dt * 300) % 360;
            _ring.InvalidateVisual();
            busy = true;
        }
        if (_checkT >= 0)
        {
            _checkT += dt;
            var t = _checkT;
            double scale, opacity;
            if (t < 0.35)
            {
                // spring (response 0.35, damping 0.6) ≈ overshooting ease
                var k = t / 0.35;
                scale = 0.4 + 0.6 * (1 + 0.18 * Math.Sin(k * Math.PI)) * (1 - Math.Pow(1 - k, 3));
                opacity = Math.Min(1, k * 2);
            }
            else if (t < 1.1)
            {
                scale = 1;
                opacity = 1;
            }
            else
            {
                var k = Math.Min(1, (t - 1.1) / 0.3);
                scale = 1;
                opacity = 1 - k;
                if (k >= 1) _checkT = -1;
            }
            ((ScaleTransform)_check.RenderTransform!).ScaleX = scale;
            ((ScaleTransform)_check.RenderTransform!).ScaleY = scale;
            _check.Opacity = opacity;
            busy = true;
        }
        return busy;
    }

    static string MiddleEllipsis(string s, int max)
    {
        if (s.Length <= max) return s;
        var head = (max - 1) * 3 / 5;
        var tail = max - 1 - head;
        return s[..head] + "…" + s[^tail..];
    }
}
