// H.I.B — one flicked card on screen: a small borderless, transparent, always-on-top window that never
// takes focus (the Mac's non-activating FlickPanel), moved every frame by FlickCardManager.
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HIB.Services;

namespace HIB.Views.Flick;

public sealed class FlickCardWindow : Window
{
    const uint WS_EX_TOOLWINDOW = 0x00000080;
    const uint WS_EX_NOACTIVATE = 0x08000000;

    public FlickCardWindow(FlickCardView view)
    {
        View = view;
        Width = FlickCardView.ContainerWidth;
        Height = FlickCardView.ContainerHeight;
        CanResize = false;
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "Flick";
        Icon = ImageUtil.WindowIcon();
        Content = view;
        ApplyNoActivate(this);
    }

    public FlickCardView View { get; }

    /// <summary>
    /// Windows: never activate on click and stay out of Alt+Tab (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW), and no
    /// Windows 11 corner rounding on a transparent window.
    /// </summary>
    public static void ApplyNoActivate(Window w)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            Win32Properties.AddWindowStylesCallback(w, (style, ex) => (style, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
            Win32Properties.SetWindowCornerPreference(w, Win32Properties.WindowCornerPreference.DoNotRound);
        }
        catch { /* older shell: plain topmost window */ }
    }
}
