using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using HIB.Core.Phone;

namespace HIB.Services;

/// <summary>
/// Maps Avalonia monitors to the core <see cref="FlickScreenPicker"/>, shows the "teach my screens"
/// hints, and remembers which monitor each flick landed on. A flick that names its screen (Point &amp; Flick,
/// SPACE.md §7.4 <c>flickStart.screen</c>) lands there whenever that screen exists.
/// </summary>
public sealed class FlickPresenter
{
    readonly FlickScreenPicker _picker;
    readonly ScreenMap _screens;
    readonly Dictionary<string, Screen> _landing = new();
    Window? _hint;

    public FlickPresenter(FlickScreenPicker picker, ScreenMap screens)
    {
        _picker = picker;
        _screens = screens;
        picker.TeachingStepChanged += (key, index, total) => Dispatcher.UIThread.Post(() => ShowHint(key, index, total));
    }

    public FlickScreenPicker Picker => _picker;

    IReadOnlyList<Screen> Screens => _screens.All;

    /// <summary>A stable key per monitor: its device name plus native resolution.</summary>
    public static string KeyFor(Screen s, int index)
        => $"{(string.IsNullOrWhiteSpace(s.DisplayName) ? "Display" + (index + 1) : s.DisplayName)} {s.Bounds.Width}x{s.Bounds.Height}";

    public List<(FlickScreen model, Screen screen)> CurrentScreens()
        => Screens.Select((s, i) => (new FlickScreen(KeyFor(s, i), s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height), s)).ToList();

    public int ScreenCount => Screens.Count;
    public int TaughtCount => _picker.TaughtCount(CurrentScreens().Select(x => x.model));

    /// <summary>Must run on the UI thread.</summary>
    public Screen? PickFor(FlickInfo flick)
    {
        // Point & Flick: the phone already chose the screen; it overrides this computer's own aim/teaching.
        if (_screens.Find(flick.Screen) is { } named)
        {
            _landing[flick.Id] = named;
            return named;
        }
        var list = CurrentScreens();
        if (list.Count == 0) return null;
        var primary = Screens.FirstOrDefault(s => s.IsPrimary) ?? list[0].screen;
        var pointer = GlobalCursor.Position ?? primary.Bounds.Center;
        var chosen = _picker.Pick(list.Select(x => x.model).ToList(), flick.Heading, flick.Elevation, flick.Angle, pointer.X, pointer.Y);
        var screen = list.FirstOrDefault(x => x.model == chosen).screen ?? primary;
        _landing[flick.Id] = screen;
        return screen;
    }

    public Screen? LandingScreen(string? flickId)
        => flickId is not null && _landing.TryGetValue(flickId, out var s) ? s : null;

    public void StartTeaching() => _picker.StartTeaching(CurrentScreens().Select(x => x.model).ToList());

    public void CancelTeaching() => _picker.CancelTeaching();

    void ShowHint(string? key, int index, int total)
    {
        _hint?.Close();
        _hint = null;
        if (key is null) return;
        var target = CurrentScreens().FirstOrDefault(x => x.model.Key == key).screen;
        if (target is null) return;

        var window = new Window
        {
            Width = 540,
            SizeToContent = SizeToContent.Height,
            WindowDecorations = WindowDecorations.None,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            CanResize = false,
            Background = Brushes.Transparent,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            Icon = ImageUtil.WindowIcon(),
            Title = "Teach Flick",
            Content = new Border
            {
                Classes = { "toast" },
                Padding = new Thickness(28, 26),
                Child = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "Point your phone here and flick anything", FontSize = 22, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = $"Teaching Flick your screens — {index} of {total}. Stand where you usually sit.", Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap },
                        new Button { Content = "Stop teaching", HorizontalAlignment = HorizontalAlignment.Center, Classes = { "soft" }, Margin = new Thickness(0, 8, 0, 0) },
                    },
                },
            },
        };
        if (window.Content is Border { Child: StackPanel sp } && sp.Children[2] is Button stop)
            stop.Click += (_, _) => _picker.CancelTeaching();
        window.Opened += (_, _) =>
        {
            var scale = target.Scaling;
            var w = window.Bounds.Width * scale;
            var h = window.Bounds.Height * scale;
            window.Position = new PixelPoint((int)(target.Bounds.Center.X - w / 2), (int)(target.Bounds.Center.Y - h / 2));
        };
        window.Show();
        _hint = window;
    }
}
