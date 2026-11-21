using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HIB.Services;
using HIB.ViewModels;
using HIB.Views.Controls;
using HIB.Views.Flick;

namespace HIB.Views.Room;

/// <summary>"Your desk" (docs/SPACE.md §10.4): the Room view over a <see cref="RoomEditor"/>. Esc cancels, Enter is Done.</summary>
public partial class RoomWindow : Window
{
    RoomEditor? _editor;

    public RoomWindow()
    {
        InitializeComponent();
        Icon = ImageUtil.WindowIcon();
        WindowDecorationProperties.SetElementRole(TitleStrip, WindowDecorationsElementRole.TitleBar);
        CancelButton.Click += (_, _) => _editor?.Cancel();
        DoneButton.Click += (_, _) => _editor?.Done();
    }

    public RoomEditor? Editor
    {
        get => _editor;
        set
        {
            if (_editor is not null) _editor.Changed -= Sync;
            _editor = value;
            Canvas.Editor = value;
            if (_editor is not null) _editor.Changed += Sync;
            Sync();
        }
    }

    public RoomCanvas DeskCanvas => Canvas;

    /// <summary>The header chip ("PC-INTERN just joined") and the phone chips follow the editor.</summary>
    void Sync()
    {
        var e = _editor;
        JoinedChip.IsVisible = e?.Joined is { Length: > 0 };
        JoinedText.Text = e?.Joined is { Length: > 0 } j ? $"{j} just joined" : "";
        var phones = e?.Phones ?? System.Array.Empty<RoomPhone>();
        PhonesColumn.IsVisible = phones.Count > 0;
        if (PhonesList.Children.Count != phones.Count || !PhonesList.Children.OfType<Border>().Select(b => b.Tag as RoomPhone).SequenceEqual(phones))
        {
            PhonesList.Children.Clear();
            foreach (var p in phones) PhonesList.Children.Add(PhoneChip(p));
        }
    }

    public string JoinedLabel => JoinedText.Text ?? "";

    /// <summary>A phone at the side: its icon in a cyan tile, the name and the battery (lime when fine, amber when low).</summary>
    static Border PhoneChip(RoomPhone p)
    {
        var tile = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new Avalonia.CornerRadius(9),
            Background = FlickTheme.Linear(FlickTheme.TopLeft, FlickTheme.BottomRight, (FlickTheme.Cyan, p.Online ? 0.22 : 0.08, 0), (FlickTheme.Midnight, 0.6, 1)),
            Child = new Icon { Data = Icons.Smartphone, Size = 16, Foreground = FlickTheme.Solid(p.Online ? FlickTheme.Cyan : Colors.White, p.Online ? 1 : 0.4), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var lines = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(10, 0, 0, 0) };
        lines.Children.Add(new TextBlock { Text = p.Name, FontSize = 12, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 112 });
        if (p.Battery is { } b)
        {
            var low = b <= 20 && !p.Charging;
            var color = low ? RoomCanvas.Amber : FlickTheme.Lime;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            row.Children.Add(new Icon
            {
                Data = p.Charging ? Icons.BatteryCharging : b <= 15 ? Icons.BatteryWarning : b <= 40 ? Icons.BatteryLow : b <= 75 ? Icons.BatteryMedium : Icons.BatteryFull,
                Size = 14, StrokeThickness = 2, Foreground = FlickTheme.Solid(color),
            });
            row.Children.Add(new TextBlock { Text = $"{b}%", FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = FlickTheme.Solid(color), VerticalAlignment = VerticalAlignment.Center });
            lines.Children.Add(row);
        }
        else lines.Children.Add(new TextBlock { Text = p.Online ? "Connected" : "Offline", FontSize = 10.5, Foreground = FlickTheme.Solid(Colors.White, 0.5) });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(tile);
        Grid.SetColumn(lines, 1);
        grid.Children.Add(lines);
        return new Border
        {
            Tag = p,
            Padding = new Avalonia.Thickness(9),
            CornerRadius = new Avalonia.CornerRadius(14),
            Background = FlickTheme.Solid(Colors.White, 0.05),
            BorderBrush = FlickTheme.Solid(Colors.White, 0.1),
            BorderThickness = new Avalonia.Thickness(0.7),
            Opacity = p.Online ? 1 : 0.7,
            Child = grid,
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            _editor?.Cancel();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            _editor?.Done();
            e.Handled = true;
        }
    }
}
