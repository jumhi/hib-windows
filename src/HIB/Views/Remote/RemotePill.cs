// H.I.B Phone Remote (docs/SPACE.md §15.4): while the phone views this computer, a pill at the top centre of the main
// display says "● Controlled from <phone>" with a Stop button, in the Flick / Roam look (ink capsule, lime edge). It
// never takes focus and lets clicks through everywhere except the Stop button: on Windows the window is transparent to
// the mouse (WS_EX_TRANSPARENT) and turns solid only while the cursor is over the button.
using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using HIB.Services;
using HIB.Views.Flick;

namespace HIB.Views.Remote;

public sealed partial class RemotePillWindow : Window
{
    const long WS_EX_TRANSPARENT = 0x00000020;
    readonly DispatcherTimer _hover;
    bool _solid;

    public RemotePillWindow(string phone, Action stop)
    {
        Phone = phone;
        SizeToContent = SizeToContent.WidthAndHeight;
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
        Title = "Phone remote";
        Icon = ImageUtil.WindowIcon();
        StopButton = new Button
        {
            Content = "Stop",
            Padding = new Thickness(12, 3),
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            Background = FlickTheme.Solid(Color.FromRgb(0xFF, 0x5A, 0x5F), 0.85),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(999),
            VerticalAlignment = VerticalAlignment.Center,
        };
        StopButton.Click += (_, _) => stop();
        Content = Capsule(TextFor(phone), StopButton);
        ClickThrough.Apply(this);
        Opened += (_, _) => ClickThrough.AfterOpen(this);
        _hover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _hover.Tick += (_, _) => FollowCursor();
        Opened += (_, _) => { if (OperatingSystem.IsWindows()) _hover.Start(); };
        Closed += (_, _) => _hover.Stop();
    }

    public string Phone { get; }
    public string Text => TextFor(Phone);
    public Button StopButton { get; }

    public static string TextFor(string phone) => $"Controlled from {phone}";

    static Border Capsule(string text, Button stop) => new()
    {
        Margin = new Thickness(16),
        Height = 40,
        CornerRadius = new CornerRadius(20),
        Background = FlickTheme.Solid(FlickTheme.Ink, 0.94),
        BorderBrush = FlickTheme.Solid(FlickTheme.Lime, 0.6),
        BorderThickness = new Thickness(1),
        BoxShadow = new BoxShadows(new BoxShadow { Blur = 24, Color = FlickTheme.A(FlickTheme.Lime, 0.35) },
            new[] { new BoxShadow { OffsetY = 3, Blur = 12, Color = FlickTheme.A(Colors.Black, 0.35) } }),
        Padding = new Thickness(14, 0, 6, 0),
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = FlickTheme.Solid(FlickTheme.Lime), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
                stop,
            },
        },
    };

    Screen? _screen;

    /// <summary>Top centre of this screen (the capsule sits 16 points down, inside its margin); stays centred as it lays out.</summary>
    public void ShowOn(Screen screen)
    {
        _screen = screen;
        Place();
        if (!IsVisible)
        {
            SizeChanged += (_, _) => Place();
            Show();
        }
    }

    void Place()
    {
        if (_screen is not { } screen) return;
        var scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        var width = ClientSize.Width > 0 ? ClientSize.Width : MeasuredSize().Width;
        var b = screen.Bounds;
        Position = new PixelPoint(b.X + (int)Math.Round((b.Width - width * scaling) / 2), b.Y);
    }

    public Size MeasuredSize()
    {
        if (Content is not Control c) return default;
        c.Measure(Size.Infinity);
        return c.DesiredSize;
    }

    /// <summary>Solid only while the cursor is over Stop, so the rest of the pill never blocks a click.</summary>
    void FollowCursor()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;
        if (!GetCursorPos(out var p) || !StopButton.IsVisible) return;
        var topLeft = StopButton.PointToScreen(new Point(0, 0));
        var bottomRight = StopButton.PointToScreen(new Point(StopButton.Bounds.Width, StopButton.Bounds.Height));
        var over = p.X >= topLeft.X && p.X < bottomRight.X && p.Y >= topLeft.Y && p.Y < bottomRight.Y;
        if (over == _solid) return;
        _solid = over;
        var ex = (long)GetWindowLongPtrW(hwnd, -20 /* GWL_EXSTYLE */);
        ex = over ? ex & ~WS_EX_TRANSPARENT : ex | WS_EX_TRANSPARENT;
        SetWindowLongPtrW(hwnd, -20, (nint)ex);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetCursorPos(out POINT p);
    [LibraryImport("user32.dll")] private static partial nint GetWindowLongPtrW(nint hwnd, int index);
    [LibraryImport("user32.dll")] private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);
}
