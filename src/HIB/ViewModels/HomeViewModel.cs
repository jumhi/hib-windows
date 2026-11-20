using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HIB.Core.Phone;
using HIB.Core.Space;
using HIB.Features.Adb;
using HIB.Features.Mirror;
using HIB.Services;

namespace HIB.ViewModels;

/// <summary>A small chip in the linked-phone card for another online computer.</summary>
public sealed record DeviceChip(string Name, string IconPath, bool InUse)
{
    public string Tip => InUse ? $"{Name} · in use" : $"{Name} · connected";
}

/// <summary>
/// The left column, like the Mac's SidebarView + PhoneView: the linked phone card, a phone mockup
/// (wallpaper, status pill, clock, recent apps, quick actions, battery/volume/music) and Mirror/Desktop.
/// </summary>
public partial class HomeViewModel : ViewModelBase
{
    readonly AppController _app;
    readonly DispatcherTimer _tick;
    NowPlaying? _np;
    byte[]? _wallpaperBytes;
    byte[]? _artBytes;
    bool _applying;
    DispatcherTimer? _volumeDebounce;

    public HomeViewModel(AppController app)
    {
        _app = app;
        var core = app.Core;
        core.Link.ConnectionChanged += (_, _) => Ui.Post(RefreshDevice);
        core.Link.DeviceUpdated += (_, _) => Ui.Post(RefreshDevice);
        core.Hub.NowPlayingChanged += np => Ui.Post(() => ApplyNowPlaying(np));
        core.Space.Changed += () => Ui.Post(RefreshSpace);
        if (app.Features is { } f)
        {
            f.Ring.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(f.Ring.IsRinging)) Ui.Post(() => IsRinging = f.Ring.IsRinging); };
            f.Adb.PropertyChanged += (_, _) => Ui.Post(RefreshFeatures);
            f.Audio.PropertyChanged += (_, _) => Ui.Post(RefreshFeatures);
            f.Mirror.PropertyChanged += (_, _) => Ui.Post(RefreshFeatures);
            f.Launcher.PropertyChanged += (_, _) => Ui.Post(RefreshFeatures);
        }
        else
            core.Hub.RingStateChanged += r => Ui.Post(() => IsRinging = r);
        IsSilenced = !core.Settings.NotificationToasts;
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => { UpdatePosition(); UpdateClock(); };
        _tick.Start();
        UpdateClock();
        RefreshDevice();
        RefreshFeatures();
        RefreshSpace();
        ApplyNowPlaying(core.Hub.NowPlaying);
    }

    public bool HasFeatures => _app.Features is not null;

    // ---- linked phone ----
    [ObservableProperty] public partial bool IsConnected { get; set; }
    [ObservableProperty] public partial string DeviceName { get; set; } = "No phone connected";
    [ObservableProperty] public partial string DeviceDetail { get; set; } = "";
    [ObservableProperty] public partial int Battery { get; set; } = -1;
    [ObservableProperty] public partial bool IsCharging { get; set; }
    [ObservableProperty] public partial Bitmap? Wallpaper { get; set; }
    [ObservableProperty] public partial bool IsRinging { get; set; }
    [ObservableProperty] public partial string HintText { get; set; } = "";
    public ObservableCollection<DeviceChip> OtherDevices { get; } = new();
    public bool HasOtherDevices => OtherDevices.Count > 0;
    public bool HasBattery => Battery >= 0;
    public string BatteryText => Battery < 0 ? "—" : $"{Battery}%{(IsCharging ? " · charging" : "")}";
    public string BatteryPercent => Battery < 0 ? "—" : $"{Battery}%";
    public string BatteryIcon => IsCharging ? Icons.BatteryCharging
        : Battery < 0 ? Icons.Battery
        : Battery <= 15 ? Icons.BatteryWarning
        : Battery <= 40 ? Icons.BatteryLow
        : Battery <= 75 ? Icons.BatteryMedium
        : Icons.BatteryFull;
    public bool HasWallpaper => Wallpaper is not null;
    public string RingLabel => IsRinging ? "Stop ringing" : "Ring my phone";
    /// <summary>The Mac shortens the name in the linked card to 16 characters.</summary>
    public string ShortName => DeviceName.Length > 16 ? DeviceName[..16] + "…" : DeviceName;
    /// <summary>Like the Mac, the phone screen dims while the phone is away.</summary>
    public double ScreenOpacity => IsConnected ? 1 : 0.5;
    public string ConnectionText => IsConnected ? "Connected" : _app.Core.Settings.LastPhoneName is { } n ? $"Looking for {n}…" : "Not connected";

    // ---- the phone screen ----
    [ObservableProperty] public partial string Hour { get; set; } = "00";
    [ObservableProperty] public partial string Minute { get; set; } = "00";
    /// <summary>Album art while music plays (like the Mac), otherwise the wallpaper.</summary>
    public Bitmap? ScreenImage => IsPlaying && AlbumArt is not null ? AlbumArt : Wallpaper;
    public bool HasScreenImage => ScreenImage is not null;
    public ObservableCollection<PhoneAppItem>? RecentApps { get; set; }

    // ---- status pill ----
    [ObservableProperty] public partial bool IsAdbConnected { get; set; }
    [ObservableProperty] public partial bool IsAdbWired { get; set; }
    [ObservableProperty] public partial bool IsAudioHere { get; set; }
    [ObservableProperty] public partial bool IsMirroring { get; set; }
    [ObservableProperty] public partial string AdbText { get; set; } = "ADB isn't connected";
    /// <summary>Mirror / Desktop show "Starting…" from the click until scrcpy is launched.</summary>
    [ObservableProperty] public partial string MirrorLabel { get; set; } = "Mirror";
    [ObservableProperty] public partial string DesktopLabel { get; set; } = "Desktop";
    public string AdbModeIcon => IsAdbWired ? Icons.Cable : Icons.Radio;
    public string AdbModeText => IsAdbWired ? "Wired ADB" : "Wireless ADB";
    public string LinkText => IsConnected ? $"Connected over Wi-Fi · {_app.Core.Link.Device?.IpAddress}" : "Not connected";

    // ---- quick actions ----
    /// <summary>Pop-ups for phone notifications are off on this PC (the Mac's "Mute" quick action).</summary>
    [ObservableProperty] public partial bool IsSilenced { get; set; }
    public string SilenceIcon => IsSilenced ? Icons.BellOff : Icons.Bell;
    public string SilenceTip => IsSilenced ? "Unmute notifications" : "Mute notifications";

    // ---- now playing ----
    [ObservableProperty] public partial bool HasMedia { get; set; }
    [ObservableProperty] public partial bool ShowMusicCard { get; set; } = true;
    [ObservableProperty] public partial string MediaTitle { get; set; } = "";
    [ObservableProperty] public partial string MediaArtist { get; set; } = "";
    [ObservableProperty] public partial Bitmap? AlbumArt { get; set; }
    [ObservableProperty] public partial bool IsPlaying { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string Elapsed { get; set; } = "0:00";
    [ObservableProperty] public partial string Duration { get; set; } = "0:00";
    [ObservableProperty] public partial int Volume { get; set; }
    [ObservableProperty] public partial bool IsMuted { get; set; }
    [ObservableProperty] public partial double VolumeValue { get; set; }
    public bool HasAlbumArt => AlbumArt is not null;
    public bool HasDuration => _np is { DurationMs: > 0 };
    public bool MusicCardVisible => HasMedia && ShowMusicCard;
    /// <summary>The clock stacks (hour over minute) when the music card is hidden, like the Mac.</summary>
    public bool ClockStacked => !MusicCardVisible;
    public string PlayPauseIcon => IsPlaying ? Icons.Pause : Icons.Play;
    public string MusicToggleIcon => IsPlaying ? Icons.ListMusic : Icons.Music;
    public string VolumeIcon => IsMuted || Volume == 0 ? Icons.VolumeX : Volume <= 33 ? Icons.Volume : Volume <= 66 ? Icons.Volume1 : Icons.Volume2;
    public string VolumeText => IsMuted ? "Muted" : $"{Volume}%";

    partial void OnBatteryChanged(int value) { OnPropertyChanged(nameof(HasBattery)); OnPropertyChanged(nameof(BatteryText)); OnPropertyChanged(nameof(BatteryPercent)); OnPropertyChanged(nameof(BatteryIcon)); }
    partial void OnIsChargingChanged(bool value) { OnPropertyChanged(nameof(BatteryText)); OnPropertyChanged(nameof(BatteryIcon)); }
    partial void OnWallpaperChanged(Bitmap? value) { OnPropertyChanged(nameof(HasWallpaper)); OnScreenChanged(); }
    partial void OnIsRingingChanged(bool value) => OnPropertyChanged(nameof(RingLabel));
    partial void OnAlbumArtChanged(Bitmap? value) { OnPropertyChanged(nameof(HasAlbumArt)); OnScreenChanged(); }
    partial void OnIsPlayingChanged(bool value) { OnPropertyChanged(nameof(PlayPauseIcon)); OnPropertyChanged(nameof(MusicToggleIcon)); OnScreenChanged(); }
    partial void OnDeviceNameChanged(string value) => OnPropertyChanged(nameof(ShortName));
    partial void OnIsConnectedChanged(bool value) { OnPropertyChanged(nameof(ConnectionText)); OnPropertyChanged(nameof(LinkText)); OnPropertyChanged(nameof(ScreenOpacity)); }
    partial void OnIsAdbWiredChanged(bool value) { OnPropertyChanged(nameof(AdbModeIcon)); OnPropertyChanged(nameof(AdbModeText)); }
    partial void OnIsSilencedChanged(bool value) { OnPropertyChanged(nameof(SilenceIcon)); OnPropertyChanged(nameof(SilenceTip)); }
    partial void OnHasMediaChanged(bool value) => OnMusicLayoutChanged();
    partial void OnShowMusicCardChanged(bool value) => OnMusicLayoutChanged();
    partial void OnVolumeChanged(int value) { OnPropertyChanged(nameof(VolumeIcon)); OnPropertyChanged(nameof(VolumeText)); }
    partial void OnIsMutedChanged(bool value) { OnPropertyChanged(nameof(VolumeIcon)); OnPropertyChanged(nameof(VolumeText)); }

    void OnScreenChanged() { OnPropertyChanged(nameof(ScreenImage)); OnPropertyChanged(nameof(HasScreenImage)); }
    void OnMusicLayoutChanged() { OnPropertyChanged(nameof(MusicCardVisible)); OnPropertyChanged(nameof(ClockStacked)); }

    void UpdateClock()
    {
        var now = DateTime.Now;
        var h24 = !CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('t');
        var hour = h24 ? now.Hour : (now.Hour % 12 == 0 ? 12 : now.Hour % 12);
        Hour = hour.ToString("00");
        Minute = now.Minute.ToString("00");
    }

    void RefreshDevice()
    {
        var link = _app.Core.Link;
        IsConnected = link.IsConnected;
        OnPropertyChanged(nameof(ConnectionText));
        OnPropertyChanged(nameof(LinkText));
        var d = link.Device;
        if (d is null)
        {
            DeviceName = _app.Core.Settings.LastPhoneName ?? "No phone connected";
            DeviceDetail = "";
            Battery = -1;
            HintText = _app.Core.Settings.LastPhoneName is null
                ? "Scan the QR code with H.I.B on your phone."
                : $"Open H.I.B on {_app.Core.Settings.LastPhoneName} or press Reconnect.";
            return;
        }
        DeviceName = d.Name;
        DeviceDetail = link.IsConnected ? $"{d.IpAddress} · H.I.B {d.Version}" : $"Disconnected · last seen at {d.IpAddress}";
        Battery = d.BatteryLevel;
        IsCharging = d.IsCharging;
        HintText = link.IsConnected ? "" : "Open H.I.B on the phone or press Reconnect.";
        if (!ReferenceEquals(d.Wallpaper, _wallpaperBytes))
        {
            _wallpaperBytes = d.Wallpaper;
            Wallpaper = ImageUtil.FromBytes(d.Wallpaper);
        }
    }

    void RefreshFeatures()
    {
        if (_app.Features is not { } f) return;
        IsAdbConnected = f.Adb.IsConnected;
        IsAdbWired = f.Adb.Mode == AdbConnectionMode.Wired;
        IsAudioHere = f.Audio.IsActive;
        IsMirroring = f.Mirror.IsMirroring;
        AdbText = f.Adb.IsConnecting ? "Connecting ADB…" : f.Adb.IsConnected ? $"ADB connected ({(IsAdbWired ? "USB" : "Wi-Fi")})" : "ADB isn't connected";
        var starting = f.Launcher.StartingKind;
        MirrorLabel = starting is MirrorKind.Mirror or MirrorKind.App ? "Starting…" : "Mirror";
        DesktopLabel = starting is MirrorKind.Desktop ? "Starting…" : "Desktop";
    }

    void RefreshSpace()
    {
        var others = _app.Core.Space.Devices
            .Where(d => !d.IsPhone && d.Online)
            .Select(d => new DeviceChip(d.Name, d.Kind == SpaceDeviceKind.Windows ? Icons.Monitor : Icons.Laptop, d.InUse))
            .ToList();
        if (OtherDevices.SequenceEqual(others)) return;
        OtherDevices.Clear();
        foreach (var o in others) OtherDevices.Add(o);
        OnPropertyChanged(nameof(HasOtherDevices));
    }

    void ApplyNowPlaying(NowPlaying? np)
    {
        _np = np;
        HasMedia = np is not null && np.HasTrack && _app.Core.Link.IsConnected;
        OnPropertyChanged(nameof(HasDuration));
        if (np is null) return;
        MediaTitle = np.Title;
        MediaArtist = np.Artist;
        IsPlaying = np.IsPlaying;
        Volume = np.Volume;
        IsMuted = np.IsMuted;
        if (_volumeDebounce is not { IsEnabled: true })
        {
            _applying = true;
            VolumeValue = np.Volume;
            _applying = false;
        }
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

    // ---- media ----
    [RelayCommand] Task PlayPause() => _app.Core.Hub.MediaControlAsync("playPause");
    [RelayCommand] Task Next() => _app.Core.Hub.MediaControlAsync("next");
    [RelayCommand] Task Previous() => _app.Core.Hub.MediaControlAsync("previous");
    [RelayCommand] Task VolumeUp() => _app.Core.Hub.VolumeUpAsync();
    [RelayCommand] Task VolumeDown() => _app.Core.Hub.VolumeDownAsync();
    [RelayCommand] Task ToggleMute() => _app.Core.Hub.ToggleMuteAsync();
    [RelayCommand] void ToggleMusicCard() => ShowMusicCard = !ShowMusicCard;

    /// <summary>The volume slider moved: set the phone volume once it settles.</summary>
    partial void OnVolumeValueChanged(double value)
    {
        if (_applying) return;
        if (_volumeDebounce is null)
        {
            _volumeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _volumeDebounce.Tick += (_, _) =>
            {
                _volumeDebounce!.Stop();
                _ = _app.Core.Hub.SetVolumeAsync((int)Math.Round(VolumeValue));
            };
        }
        _volumeDebounce.Stop();
        _volumeDebounce.Start();
    }

    [RelayCommand]
    Task Ring()
    {
        // RingService picks the route: link → ADB broadcast → Find My relay.
        if (_app.Features is { } f) { f.Ring.Toggle(); return Task.CompletedTask; }
        return _app.Core.Hub.RingAsync(!IsRinging);
    }

    // ---- quick actions (same four as the Mac: send, files, mute, clipboard) ----
    [RelayCommand]
    async Task SendFiles()
    {
        var phone = SpaceActions.Phone(_app);
        var paths = await SpaceActions.PickFilesAsync(_app, "Send to " + phone.Name);
        if (paths.Count > 0) await SpaceActions.SendFilesAsync(_app, phone, paths);
    }

    [RelayCommand] void OpenFiles() => _app.Navigate?.Invoke("files");

    [RelayCommand]
    void ToggleSilence()
    {
        IsSilenced = !IsSilenced;
        _app.Core.Settings.NotificationToasts = !IsSilenced;
        _app.Core.Settings.Save();
    }

    [RelayCommand]
    async Task SendClipboard()
    {
        var status = await SpaceActions.SendClipboardAsync(_app, SpaceActions.Phone(_app));
        _app.Toasts.Show(new ToastViewModel { Caption = "Clipboard", Title = status, Icon = ImageUtil.AppIcon(), Seconds = 4 });
    }

    // ---- connection ----
    [RelayCommand] void OpenPair() => _app.Navigate?.Invoke("pair");
    [RelayCommand] void OpenDevices() => _app.Navigate?.Invoke("devices");
    [RelayCommand] Task Disconnect() => _app.Core.Hub.DisconnectPhoneAsync();

    [RelayCommand]
    async Task Reconnect()
    {
        HintText = "Asking the phone to connect…";
        if (_app.Core.Udp is not null) await _app.Core.Udp.BurstAsync();
        var ok = await _app.Core.WakePhoneAsync();
        HintText = ok ? "Phone answered — connecting…" : "No answer. Open H.I.B on the phone (same Wi-Fi).";
    }

    [RelayCommand]
    async Task ToggleAdb()
    {
        if (_app.Features is not { } f) return;
        if (f.Adb.IsConnected) { await f.Adb.DisconnectAsync(userInitiated: true); return; }
        if (_app.Core.Link.Device is null) { Problem("Connect your phone first."); return; }
        // Not connected → the "Set up mirroring" card shows what the phone still needs (never a dead end).
        var r = await f.AutoConnectAdbAsync(silent: false, notify: false);
        if (r is not { Success: true }) f.Setup.Show(r is null ? null : HIB.Features.Adb.AdbService.Explain(r));
    }

    // ---- recent apps / mirroring (MirrorLauncher shows progress and the real reason on failure) ----
    [RelayCommand]
    async Task OpenApp(PhoneAppItem? item)
    {
        if (item is null || _app.Features is not { } f) return;
        await f.Launcher.AppAsync(item.Package, item.Name);
    }

    [RelayCommand]
    async Task Mirror()
    {
        if (_app.Features is not { } f) { Problem("Mirroring isn't available in this build."); return; }
        await f.Launcher.MirrorAsync();
    }

    [RelayCommand]
    async Task Desktop()
    {
        if (_app.Features is not { } f) { Problem("Desktop mode isn't available in this build."); return; }
        await f.Launcher.DesktopAsync();
    }

    [RelayCommand] void StopMirroring() => _app.Features?.Mirror.StopAll();
    [RelayCommand] void ToggleAudioHere() => _app.Features?.Audio.Toggle();
    [RelayCommand] void OpenApps() => _app.Navigate?.Invoke("apps");
    [RelayCommand] void OpenMirrorSettings() => _app.Navigate?.Invoke("phone");

    void Problem(string text) => _app.Toasts.Show(new ToastViewModel { Caption = "Mirroring", Title = text, Icon = ImageUtil.AppIcon(), Seconds = 6 });
}
