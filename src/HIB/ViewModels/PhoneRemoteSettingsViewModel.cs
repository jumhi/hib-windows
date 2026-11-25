// Settings › Phone remote (docs/SPACE.md §15.4; same words on the Mac): Allow viewing and control, Unlock from phone
// (on Windows it explains managed / not yet), Stay reachable when locked, who is viewing now (with Stop), and how to
// reach this computer from anywhere with Tailscale.
using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HIB.Core.Remote;
using HIB.Services;

namespace HIB.ViewModels;

public sealed partial class PhoneRemoteSettingsViewModel : ViewModelBase
{
    public const string ManagedText = "Your organisation manages this PC, so Unlock from phone isn't available here. Viewing and control still work while it's unlocked.";
    public const string NotYetText = "Unlock from phone isn't available on Windows yet. Viewing and control work while this PC is unlocked.";
    public const string StayAwakeText = "Stays awake while locked and plugged in, so your phone can reach it.";
    public const string WifiNote = "On Wi-Fi, power saving can still drop the connection.";
    public const string TailscaleText = "To use Remote away from home, install Tailscale on the phone and on this PC and sign in to the same account on both. On a work or university PC, check with IT first.";

    readonly PhoneRemote _remote;
    readonly Func<System.Collections.Generic.IReadOnlyList<string>> _tailscale;
    bool _loading = true;

    public PhoneRemoteSettingsViewModel(AppController app) : this(app.Core.Remote, app.Core.Options.Remote.TailscaleAddrs) { }

    public PhoneRemoteSettingsViewModel(PhoneRemote remote, Func<System.Collections.Generic.IReadOnlyList<string>> tailscale)
    {
        _remote = remote;
        _tailscale = tailscale;
        remote.StateChanged += () => Ui.Post(Refresh);
        remote.SessionChanged += _ => Ui.Post(Refresh);
        Refresh();
        _loading = false;
    }

    [ObservableProperty] public partial bool Allowed { get; set; }
    [ObservableProperty] public partial bool StayReachable { get; set; }
    [ObservableProperty] public partial string UnlockText { get; set; } = "";
    [ObservableProperty] public partial string ViewingText { get; set; } = "";
    [ObservableProperty] public partial bool IsViewing { get; set; }
    [ObservableProperty] public partial string TailscaleStatus { get; set; } = "";

    public string StayAwakeCaption => StayAwakeText;
    public string WifiCaption => WifiNote;
    public string TailscaleCaption => TailscaleText;

    partial void OnAllowedChanged(bool value)
    {
        if (!_loading) _remote.Allowed = value;
    }

    partial void OnStayReachableChanged(bool value)
    {
        if (!_loading) _remote.StayReachable = value;
    }

    [RelayCommand]
    void Stop() => _remote.Stop();

    public void Refresh()
    {
        var was = _loading;
        _loading = true;
        Allowed = _remote.Allowed;
        StayReachable = _remote.StayReachable;
        UnlockText = _remote.UnlockState == RemoteUnlockAvailability.Managed ? ManagedText : NotYetText;
        var phone = _remote.ViewingPhone;
        IsViewing = phone is not null;
        ViewingText = phone is not null ? $"Controlled from {phone}" : _remote.CanView ? "Nobody is viewing this PC" : "Viewing isn't available on this system";
        System.Collections.Generic.IReadOnlyList<string> addrs;
        try { addrs = _tailscale(); }
        catch { addrs = Array.Empty<string>(); }
        TailscaleStatus = addrs.Count > 0 ? "Tailscale on this PC: " + string.Join(", ", addrs.Take(2)) : "Tailscale isn't running on this PC.";
        _loading = was;
    }
}
