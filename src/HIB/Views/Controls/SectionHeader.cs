using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace HIB.Views.Controls;

/// <summary>The Mac's SettingsHeaderView: a cyan icon and a small bold secondary title.</summary>
public sealed class SectionHeader : StackPanel
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SectionHeader, string?>(nameof(Title));

    public static readonly StyledProperty<string?> IconDataProperty =
        AvaloniaProperty.Register<SectionHeader, string?>(nameof(IconData));

    readonly Icon _icon = new() { Size = 15, StrokeThickness = 2.2, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };

    public SectionHeader()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 8;
        Margin = new Thickness(8, 4, 8, 0);
        _text.Classes.Add("section-title");
        _icon.Classes.Add("cyan");
        Children.Add(_icon);
        Children.Add(_text);
    }

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? IconData { get => GetValue(IconDataProperty); set => SetValue(IconDataProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty) _text.Text = Title;
        else if (change.Property == IconDataProperty) _icon.Data = IconData;
    }
}
