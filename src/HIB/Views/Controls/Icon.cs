using System;
using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace HIB.Views.Controls;

/// <summary>
/// An outline icon (Lucide path data in a 24×24 box, see <see cref="HIB.ViewModels.Icons"/>) stroked with
/// round caps, like SF Symbols on the Mac. Inherits the text colour, so it follows button states.
/// </summary>
public sealed class Icon : Control
{
    static readonly ConcurrentDictionary<string, Geometry?> Cache = new();

    public static readonly StyledProperty<string?> DataProperty =
        AvaloniaProperty.Register<Icon, string?>(nameof(Data));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Size), 16);

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 2);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, ForegroundProperty, StrokeThicknessProperty, SizeProperty);
        AffectsMeasure<Icon>(SizeProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<Icon>(false);
    }

    public string? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    static Geometry? Parse(string data) => Cache.GetOrAdd(data, d =>
    {
        try { return Geometry.Parse(d); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Icon path did not parse: {ex.Message}");
            return null;
        }
    });

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (string.IsNullOrEmpty(Data) || Foreground is not { } brush) return;
        var geometry = Parse(Data);
        if (geometry is null) return;
        var scale = Size / 24.0;
        var dx = (Bounds.Width - Size) / 2;
        var dy = (Bounds.Height - Size) / 2;
        var pen = new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
            context.DrawGeometry(null, pen, geometry);
    }
}
