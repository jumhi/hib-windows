using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HIB.Views.Controls;

/// <summary>
/// HIBAmbientBackground from the Mac app: ink → midnight → deep teal gradient, a blurred cyan light
/// top-right, a blurred lavender light bottom-left and a faint 34 px cyan grid.
/// </summary>
public sealed class AmbientBackground : Control
{
    static readonly Color Ink = Color.Parse("#090C21");
    static readonly Color Midnight = Color.Parse("#111533");
    static readonly Color Deep = Color.Parse("#0B1A2E");
    static readonly Color Cyan = Color.Parse("#20D9FF");
    static readonly Color Lavender = Color.Parse("#7A6BFF");

    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<AmbientBackground, bool>(nameof(ShowGrid), true);

    public bool ShowGrid { get => GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }

    static AmbientBackground()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<AmbientBackground>(false);
        AffectsRender<AmbientBackground>(ShowGridProperty);
    }

    StreamGeometry? _grid;
    Size _gridSize;

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var rect = new Rect(0, 0, w, h);

        context.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Ink, 0), new GradientStop(Midnight, 0.5), new GradientStop(Deep, 1) },
        }, rect);

        // SwiftUI: Circle(width: w*0.62).blur(90).offset(+0.34w, -0.42h) and Circle(width: w*0.52).blur(110).offset(-0.42w, +0.34h)
        Glow(context, new Point(w * 0.84, h * 0.08), w * 0.31, 90, Cyan, 0.18);
        Glow(context, new Point(w * 0.08, h * 0.84), w * 0.26, 110, Lavender, 0.11);

        if (ShowGrid)
        {
            if (_grid is null || _gridSize != Bounds.Size)
            {
                _grid = BuildGrid(w, h);
                _gridSize = Bounds.Size;
            }
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Cyan, 0.05), 0.5), _grid);
        }
    }

    /// <summary>A soft disc: full colour inside, fading out over the blur radius.</summary>
    static void Glow(DrawingContext context, Point center, double radius, double blur, Color color, double alpha)
    {
        var outer = radius + blur * 1.2;
        var brush = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(WithAlpha(color, alpha), 0),
                new GradientStop(WithAlpha(color, alpha * 0.92), System.Math.Max(0, (radius - blur * 0.6) / outer)),
                new GradientStop(WithAlpha(color, alpha * 0.45), radius / outer),
                new GradientStop(WithAlpha(color, alpha * 0.12), (radius + blur * 0.6) / outer),
                new GradientStop(WithAlpha(color, 0), 1),
            },
        };
        context.DrawEllipse(brush, null, center, outer, outer);
    }

    static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)System.Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);

    static StreamGeometry BuildGrid(double w, double h)
    {
        const double spacing = 34;
        var g = new StreamGeometry();
        using var ctx = g.Open();
        for (double x = 0; x <= w; x += spacing)
        {
            ctx.BeginFigure(new Point(x + 0.25, 0), false);
            ctx.LineTo(new Point(x + 0.25, h));
            ctx.EndFigure(false);
        }
        for (double y = 0; y <= h; y += spacing)
        {
            ctx.BeginFigure(new Point(0, y + 0.25), false);
            ctx.LineTo(new Point(w, y + 0.25));
            ctx.EndFigure(false);
        }
        return g;
    }
}
