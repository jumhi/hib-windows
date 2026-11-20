using System;
using Avalonia.Threading;
using HIB.Features.Adb;
using HIB.Features.Mirror;
using HIB.ViewModels;
using HIB.Views;

namespace HIB.Services;

/// <summary>
/// Opens the one "Set up mirroring" card (docs/SPACE.md §8.3). Every place that needs ADB — Mirror, Desktop,
/// app windows, the tray, the Mirroring &amp; ADB page, the "couldn't connect" toast — lands here instead of a
/// dead end. A second call brings the open card to the front.
/// </summary>
public sealed class AdbSetupPresenter
{
    readonly AppController _app;
    readonly FeatureHost _f;
    AdbSetupWindow? _window;
    AdbSetupViewModel? _vm;

    public AdbSetupPresenter(AppController app, FeatureHost f)
    {
        _app = app;
        _f = f;
    }

    public bool IsOpen => _window is not null;
    internal AdbSetupWindow? Window => _window;
    public AdbSetupViewModel? Current => _vm;

    /// <param name="reason">Why it opened (shown as a small note), e.g. adb's own words.</param>
    /// <param name="then">What to start once ADB is ready (the card's primary button).</param>
    public void Show(string? reason = null, MirrorRequest? then = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(reason, then));
            return;
        }
        if (_window is { } open && _vm is { } vm)
        {
            if (then is not null) vm.Then = then;
            if (!string.IsNullOrWhiteSpace(reason)) vm.Note = reason;
            open.Activate();
            return;
        }

        var core = _app.Core;
        var session = new AdbSetupSession(_f.Adb, _f.Pairing,
            (open, watch) => core.Link.IsConnected ? core.Hub.RequestAdbSetupAsync(open, watch) : System.Threading.Tasks.Task.CompletedTask,
            () => core.Link.IsConnected);
        _f.Pairing.PhoneIp = _f.PhoneIp ?? _f.Pairing.PhoneIp;
        _vm = new AdbSetupViewModel(_app, _f, session) { Then = then, Note = reason ?? "" };
        _window = new AdbSetupWindow { DataContext = _vm };
        _vm.Close = () => _window?.Close();
        _window.Closed += (_, _) =>
        {
            _vm?.Dispose();
            _vm = null;
            _window = null;
        };
        _window.Show();
        _window.Activate();
        _ = session.StartAsync();
    }

    public void Close() => _window?.Close();
}
