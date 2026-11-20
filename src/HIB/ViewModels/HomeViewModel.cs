using System;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HIB.Core.Link;
using HIB.Core.Phone;
using HIB.Services;

namespace HIB.ViewModels;

public partial class HomeViewModel : ViewModelBase
{
    readonly AppController _app;
    readonly DispatcherTimer _tick;
    NowPlaying? _np;
    byte[]? _wallpaperBytes;
    byte[]? _artBytes;

    public HomeViewModel(AppController app)
    {
        _app = app;
        var core = app.Core;
        core.Link.ConnectionChanged += (_, _) => Ui.Post(RefreshDevice);
        core.Link.DeviceUpdated += (_, _) => Ui.Post(RefreshDevice);
        core.Hub.NowPlayingChanged += np => Ui.Post(() => ApplyNowPlaying(np));
        if (app.Features is { } f)
            f.Ring.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(f.Ring.IsRinging)) Ui.Post(() => IsRinging = f.Ring.IsRinging); };
        else
            core.Hub.RingStateChanged += r => Ui.Post(() => IsRinging = r);
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => UpdatePosition();
        _tick.Start();
        RefreshDevice();
        ApplyNowPlaying(core.Hub.NowPlaying);
    }

    // ---- device ----
    [ObservableProperty] public partial bool IsConnected { get; set; }
    [ObservableProperty] public partial string DeviceName { get; set; } = "No phone connected";
    [ObservableProperty] public partial string DeviceDetail { get; set; } = "";
    [ObservableProperty] public partial int Battery { get; set; } = -1;
    [ObservableProperty] public partial bool IsCharging { get; set; }
    [ObservableProperty] public partial Bitmap? Wallpaper { get; set; }
    [ObservableProperty] public partial bool IsRinging { get; set; }
    [ObservableProperty] public partial string HintText { get; set; } = "";
    public bool HasBattery => Battery >= 0;
    public string BatteryText => Battery < 0 ? "—" : $"{Battery}%{(IsCharging ? " · charging" : "")}";
    public bool HasWallpaper => Wallpaper is not null;
    public string RingLabel => IsRinging ? "Stop ringing" : "Ring my phone";

    // ---- now playing ----
    [ObservableProperty] public partial bool HasMedia { get; set; }
    [ObservableProperty] public partial string MediaTitle { get; set; } = "";
    [ObservableProperty] public partial string MediaArtist { get; set; } = "";
    [ObservableProperty] public partial Bitmap? AlbumArt { get; set; }
    [ObservableProperty] public partial bool IsPlaying { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string Elapsed { get; set; } = "0:00";
    [ObservableProperty] public partial string Duration { get; set; } = "0:00";
    [ObservableProperty] public partial int Volume { get; set; }
    [ObservableProperty] public partial bool IsMuted { get; set; }
    public bool HasAlbumArt => AlbumArt is not null;
    public string PlayPauseIcon => IsPlaying
        ? "M14,19H18V5H14M6,19H10V5H6V19Z"
        : "M8,5.14V19.14L19,12.14L8,5.14Z";

    partial void OnBatteryChanged(int value) { OnPropertyChanged(nameof(HasBattery)); OnPropertyChanged(nameof(BatteryText)); }
    partial void OnIsChargingChanged(bool value) => OnPropertyChanged(nameof(BatteryText));
    partial void OnWallpaperChanged(Bitmap? value) => OnPropertyChanged(nameof(HasWallpaper));
    partial void OnIsRingingChanged(bool value) => OnPropertyChanged(nameof(RingLabel));
    partial void OnAlbumArtChanged(Bitmap? value) => OnPropertyChanged(nameof(HasAlbumArt));
    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayPauseIcon));

    void RefreshDevice()
    {
        var link = _app.Core.Link;
        IsConnected = link.IsConnected;
        var d = link.Device;
        if (d is null)
        {
            DeviceName = "No phone connected";
            DeviceDetail = "";
            Battery = -1;
            HintText = _app.Core.Settings.LastPhoneName is null
                ? "Open the Pair page and scan the QR code with H.I.B on your phone."
                : $"Waiting for {_app.Core.Settings.LastPhoneName}. Open H.I.B on the phone or press Reconnect.";
            return;
        }
        DeviceName = d.Name;
        DeviceDetail = link.IsConnected
            ? $"{d.IpAddress} · H.I.B {d.Version}"
            : $"Disconnected · last seen at {d.IpAddress}";
        Battery = d.BatteryLevel;
        IsCharging = d.IsCharging;
        HintText = link.IsConnected ? "" : "Open H.I.B on the phone or press Reconnect.";
        if (!ReferenceEquals(d.Wallpaper, _wallpaperBytes))
        {
            _wallpaperBytes = d.Wallpaper;
            Wallpaper = ImageUtil.FromBytes(d.Wallpaper);
        }
    }

    void ApplyNowPlaying(NowPlaying? np)
    {
        _np = np;
        HasMedia = np is not null && np.HasTrack && _app.Core.Link.IsConnected;
        if (np is null) return;
        MediaTitle = np.Title;
        MediaArtist = np.Artist;
        IsPlaying = np.IsPlaying;
        Volume = np.Volume;
        IsMuted = np.IsMuted;
        if (!ReferenceEquals(np.AlbumArt, _artBytes))
        {
            _artBytes = np.AlbumArt;
            AlbumArt = ImageUtil.FromBytes(np.AlbumArt);
        }
        Duration = Format(np.DurationMs);
        UpdatePosition();
    }

    void UpdatePosition()
    {
        if (_np is null) return;
        var pos = _np.CurrentPositionMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Elapsed = Format(pos);
        Progress = _np.DurationMs > 0 ? 100.0 * pos / _np.DurationMs : 0;
    }

    static string Format(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    // ---- commands ----
    [RelayCommand] Task PlayPause() => _app.Core.Hub.MediaControlAsync("playPause");
    [RelayCommand] Task Next() => _app.Core.Hub.MediaControlAsync("next");
    [RelayCommand] Task Previous() => _app.Core.Hub.MediaControlAsync("previous");
    [RelayCommand] Task VolumeUp() => _app.Core.Hub.VolumeUpAsync();
    [RelayCommand] Task VolumeDown() => _app.Core.Hub.VolumeDownAsync();
    [RelayCommand] Task ToggleMute() => _app.Core.Hub.ToggleMuteAsync();
    [RelayCommand]
    Task Ring()
    {
        // RingService picks the route: link → ADB broadcast → Find My relay.
        if (_app.Features is { } f) { f.Ring.Toggle(); return Task.CompletedTask; }
        return _app.Core.Hub.RingAsync(!IsRinging);
    }
    [RelayCommand] void OpenPair() => _app.Navigate?.Invoke("pair");
    [RelayCommand] Task Disconnect() => _app.Core.Hub.DisconnectPhoneAsync();

    [RelayCommand]
    async Task Reconnect()
    {
        HintText = "Asking the phone to connect…";
        if (_app.Core.Udp is not null) await _app.Core.Udp.BurstAsync();
        var ok = await _app.Core.WakePhoneAsync();
        HintText = ok ? "Phone answered — connecting…" : "Phone did not answer. Make sure H.I.B is open on the phone and on the same Wi-Fi.";
    }
}
