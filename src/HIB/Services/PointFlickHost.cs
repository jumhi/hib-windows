// H.I.B — Point & Flick on Windows (SPACE.md §7): keeps the phone's list of this computer's screens current
// (screensInfo on connect, on display changes, on request) and shows the aim light and the point target
// on the right monitor when the phone says so.
using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using HIB.Core.Phone;
using HIB.Views.Flick;

namespace HIB.Services;

public sealed class PointFlickHost : IDisposable
{
    readonly AppController _app;
    readonly DispatcherTimer _poll;
    Screens? _watched;
    AimLightWindow? _light;
    PointTargetWindow? _target;

    public PointFlickHost(AppController app)
    {
        _app = app;
        var aim = app.Core.Aim;
        aim.LitChanged += _ => Dispatcher.UIThread.Post(SyncLight);
        aim.SetupChanged += _ => Dispatcher.UIThread.Post(SyncTarget);
        // Avalonia's Screens.Changed covers plug/unplug and resolution changes; the poll covers the rest
        // (and the main window not existing yet).
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _poll.Tick += (_, _) => PublishScreens();
        _poll.Start();
        Dispatcher.UIThread.Post(PublishScreens);
    }

    /// <summary>The aim light on screen (UI thread), or null.</summary>
    public AimLightWindow? AimLight => _light is { IsFadingOut: false, IsClosed: false } l ? l : null;

    /// <summary>The point target on screen (UI thread), or null.</summary>
    public PointTargetWindow? PointTarget => _target is { IsFadingOut: false, IsClosed: false } t ? t : null;

    /// <summary>Tells the core (and so the phone, when it changed) which monitors this computer has. UI thread.</summary>
    public void PublishScreens()
    {
        Watch();
        var screens = _app.Screens.Current().Select(x => x.Info).ToList();
        if (screens.Count > 0) _app.Core.Aim.SetScreens(screens);
        // Roam needs the same monitors (with pixels and scale) for its edges and injection.
        _app.Roam.PublishDisplays();
    }

    void Watch()
    {
        Screens? live;
        try { live = _app.Screens.Live; }
        catch { live = null; }
        if (ReferenceEquals(live, _watched)) return;
        if (_watched is not null) _watched.Changed -= OnScreensChanged;
        _watched = live;
        if (live is not null) live.Changed += OnScreensChanged;
    }

    void OnScreensChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(PublishScreens);

    // The overlays follow the core's current state, so posts that arrive late or out of order can't leave a stale one.

    void SyncLight()
    {
        var lit = _app.Core.Aim.Lit;
        if (lit is null)
        {
            _light?.FadeOutAndClose();
            _light = null;
            return;
        }
        var screen = _app.Screens.Find(lit.Id) ?? _app.Screens.Primary;
        if (screen is null) return;
        if (_light is { IsFadingOut: false, IsClosed: false } current && current.ScreenId == lit.Id)
        {
            current.Place(screen); // same screen; its bounds may have changed
            return;
        }
        _light?.FadeOutAndClose();
        _light = new AimLightWindow(lit.Id);
        _light.ShowOn(screen);
    }

    void SyncTarget()
    {
        var setup = _app.Core.Aim.Setup;
        if (setup is null)
        {
            _target?.FadeOutAndClose();
            _target = null;
            return;
        }
        var screen = _app.Screens.Find(setup.Screen.Id) ?? _app.Screens.Primary;
        if (screen is null) return;
        if (_target is { IsFadingOut: false, IsClosed: false } current && current.ScreenId == setup.Screen.Id)
        {
            current.Update(setup);
            current.Place(screen);
            return;
        }
        _target?.FadeOutAndClose();
        _target = new PointTargetWindow(setup.Screen.Id);
        _target.Update(setup);
        _target.ShowOn(screen);
    }

    public void Dispose()
    {
        _poll.Stop();
        if (_watched is not null) _watched.Changed -= OnScreensChanged;
        _light?.FadeOutAndClose();
        _target?.FadeOutAndClose();
    }
}
