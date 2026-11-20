using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HIB.Views.Controls;

/// <summary>
/// Big "liquid glass" digits like the Mac's phone clock (TimeView): the glyph outlines are filled with a
/// faint white and edged with a brighter rim, so the wallpaper shows through.
/// </summary>
public sealed class GlassText : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<GlassText, string?>(nameof(Text));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<GlassText, double>(nameof(FontSize), 88);

    /// <summary>Extra space between characters (negative tightens, like the Mac's kern -12).</summary>
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<GlassText, double>(nameof(Spacing), -6);

    static readonly Typeface Face = new("fonts:Inter#Inter", FontStyle.Normal, FontWeight.Bold);

    static GlassText()
    {
        AffectsMeasure<GlassText>(TextProperty, FontSizeProperty, SpacingProperty);
        AffectsRender<GlassText>(TextProperty, FontSizeProperty, SpacingProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<GlassText>(false);
    }

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    Geometry? _geometry;
    string? _built;

    Geometry? Build()
    {
        var key = $"{Text}|{FontSize}|{Spacing}";
        if (_built == key) return _geometry;
        _built = key;
        _geometry = null;
        if (string.IsNullOrEmpty(Text)) return null;
        var group = new GeometryGroup { FillRule = FillRule.NonZero };
        double x = 0;
        foreach (var ch in Text)
        {
            var ft = new FormattedText(ch.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, FontSize, Brushes.White);
            var g = ft.BuildGeometry(new Point(x, 0));
            // Some glyphs (4, 7…) are drawn with overlapping contours; merge them so the rim only
            // traces the outside, like one piece of glass.
            if (g is not null) group.Children.Add(new CombinedGeometry(GeometryCombineMode.Union, g, g));
            x += ft.WidthIncludingTrailingWhitespace + Spacing;
        }
        _geometry = group;
        return _geometry;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var g = Build();
        if (g is null) return default;
        var b = g.Bounds;
        return new Size(b.Width + 2, b.Height + 2);
    }

    public override void Render(DrawingContext context)
    {
        var g = Build();
        if (g is null) return;
        var b = g.Bounds;
        var dx = (Bounds.Width - b.Width) / 2 - b.X;
        var dy = (Bounds.Height - b.Height) / 2 - b.Y;
        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(92, 255, 255, 255), 0), new GradientStop(Color.FromArgb(46, 255, 255, 255), 1) },
        };
        var rim = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(170, 255, 255, 255), 0), new GradientStop(Color.FromArgb(56, 255, 255, 255), 1) },
        };
        using (context.PushTransform(Matrix.CreateTranslation(dx, dy)))
            context.DrawGeometry(fill, new Pen(rim, 1.1), g);
    }
}
