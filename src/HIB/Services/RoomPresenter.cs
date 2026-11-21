// H.I.B Room (docs/SPACE.md §10.4): opens "Your desk" from the Devices tab ("Arrange") and by itself the
// first time a computer that speaks the room joins ("Arrange your desk"). Keeps it live: the other
// computer moving something, a computer joining or leaving, the phone's battery; every drop and Cancel
// applies at once through the Roam engine (roomLayout goes out).
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using HIB.Core.Roam;
using HIB.Core.Space;
using HIB.ViewModels;
using HIB.Views.Room;

namespace HIB.Services;

public sealed class RoomPresenter
{
    readonly AppController _app;
    RoomWindow? _window;
    RoomEditor? _editor;
    bool _posted;

    public RoomPresenter(AppController app, bool autoOpen)
    {
        _app = app;
        AutoOpen = autoOpen;
        var roam = app.Core.Roam;
        roam.RoomChanged += Post;
        roam.Changed += Post;
        app.Core.Space.Changed += Post;
        roam.ComputerJoined += c => Dispatcher.UIThread.Post(() =>
        {
            if (AutoOpen) Show(c.Name);
        });
    }

    /// <summary>Open by itself when a new computer joins (the real app; tests turn it on to check).</summary>
    public bool AutoOpen { get; set; }

    public bool IsOpen => _window is not null;
    public RoomWindow? Window => _window;
    public RoomEditor? Editor => _editor;

    /// <summary>Opens "Your desk" (or brings it to the front). <paramref name="joined"/>: the computer that just joined.</summary>
    public void Show(string? joined = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(joined));
            return;
        }
        if (_window is { } open && _editor is { } ed)
        {
            if (joined is not null) ed.Joined = joined;
            Refresh();
            open.Activate();
            return;
        }
        var roam = _app.Core.Roam;
        var snap = roam.Room;
        _editor = new RoomEditor(snap.Self, snap.Computers, snap.Groups, Phones(), Speaking(snap), joined)
        {
            OnApply = groups => roam.SetRoomGroups(groups),
        };
        _window = new RoomWindow { Editor = _editor };
        _editor.OnClose = () => _window?.Close();
        _window.Closed += (_, _) =>
        {
            _window = null;
            _editor = null;
        };
        _window.Show();
        _window.Activate();
    }

    public void Close() => _window?.Close();

    void Post()
    {
        if (_window is null || _posted) return;
        _posted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _posted = false;
            Refresh();
        });
    }

    void Refresh()
    {
        if (_editor is not { } ed) return;
        var snap = _app.Core.Roam.Room;
        ed.Refresh(snap.Computers, snap.Groups, Speaking(snap), Phones());
    }

    IReadOnlyCollection<string> Speaking(RoomSnapshot snap)
        => snap.Computers.Where(c => !c.IsSelf && _app.Core.Roam.InRoom(c.Id)).Select(c => c.Id).ToList();

    IReadOnlyList<RoomPhone> Phones()
    {
        var space = _app.Core.Space;
        var charging = space is SpaceEngine e ? e.ListState.PhoneCharging ?? false : false;
        return space.Devices.Where(d => d.IsPhone).Select(d => new RoomPhone(d.Id, d.Name, d.Battery, charging, d.Online)).ToList();
    }
}
