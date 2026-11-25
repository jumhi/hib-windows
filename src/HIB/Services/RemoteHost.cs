// H.I.B Phone Remote on the desktop (docs/SPACE.md §15.4): shows the "Controlled from <phone>" pill with Stop on the main
// display for as long as a /screen session is open.
using System;
using Avalonia.Threading;
using HIB.Views.Remote;

namespace HIB.Services;

public sealed class RemoteHost : IDisposable
{
    readonly AppController _app;
    RemotePillWindow? _pill;

    public RemoteHost(AppController app)
    {
        _app = app;
        app.Core.Remote.SessionChanged += _ => Dispatcher.UIThread.Post(Sync);
    }

    /// <summary>The pill on screen (UI thread), or null.</summary>
    public RemotePillWindow? Pill => _pill;

    /// <summary>Follows the core: the pill shows the phone viewing now, or goes away.</summary>
    public void Sync()
    {
        var phone = _app.Core.Remote.ViewingPhone;
        if (phone is null)
        {
            _pill?.Close();
            _pill = null;
            return;
        }
        if (_pill is { } current && current.Phone == phone && current.IsVisible) return;
        _pill?.Close();
        var screen = _app.Screens.Primary;
        _pill = new RemotePillWindow(phone, () => _app.Core.Remote.Stop());
        if (screen is not null) _pill.ShowOn(screen);
        else _pill.Show();
    }

    public void Dispose()
    {
        _pill?.Close();
        _pill = null;
    }
}
