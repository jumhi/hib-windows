using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HIB.Core.Phone;
using HIB.Core.Space;
using HIB.Services;

namespace HIB.ViewModels;

// =====================================================================
// Apps tab (the Mac's AppGridView)
// =====================================================================

public partial class AppsViewModel : ViewModelBase
{
    readonly AppController _app;
    readonly Dictionary<string, PhoneAppItem> _items = new();
    readonly List<string> _recent = new();

    public AppsViewModel(AppController app)
    {
        _app = app;
        app.Core.Hub.AppsChanged += () => Ui.Post(Load);
        app.Core.Hub.NotificationPosted += n => Ui.Post(() => Touch(n.Package, launched: false));
        foreach (var n in app.Core.Hub.Notifications.Take(20).Reverse()) Touch(n.Package, launched: false);
        Load();
    }

    public ObservableCollection<PhoneAppItem> Apps { get; } = new();
    /// <summary>Up to five apps for the phone mockup: last opened from here, then the latest to notify.</summary>
    public ObservableCollection<PhoneAppItem> Recent { get; } = new();
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "";
    public bool HasSearch => SearchText.Trim().Length > 0;
    public bool HasApps => _items.Count > 0;
    public bool IsEmpty => _items.Count == 0;
    public bool NoMatches => HasApps && Apps.Count == 0;
    public bool HasStatus => Status.Length > 0;

    /// <summary>Raised when the page wants the search box focused (on open, like the Mac).</summary>
    public event Action? FocusRequested;
    public void FocusSearch() => FocusRequested?.Invoke();

    partial void OnSearchTextChanged(string value)
    {
        Filter();
        OnPropertyChanged(nameof(HasSearch));
    }

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    void Load()
    {
        var apps = _app.Core.Hub.Apps;
        foreach (var gone in _items.Keys.Where(k => !apps.ContainsKey(k)).ToList()) _items.Remove(gone);
        foreach (var (pkg, a) in apps)
            if (!_items.TryGetValue(pkg, out var item) || item.App != a) _items[pkg] = new PhoneAppItem(a) { Owner = this };
        Filter();
        RebuildRecent();
        OnPropertyChanged(nameof(HasApps));
        OnPropertyChanged(nameof(IsEmpty));
    }

    void Filter()
    {
        var q = SearchText.Trim();
        IEnumerable<PhoneAppItem> list = _items.Values;
        if (q.Length == 0)
            list = list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase);
        else
            list = list.Where(a => a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                .OrderByDescending(a => a.Name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase))
                .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase);
        var result = list.Take(400).ToList();
        Apps.Clear();
        for (var i = 0; i < result.Count; i++)
        {
            result[i].IsHighlighted = q.Length > 0 && i == 0;
            Apps.Add(result[i]);
        }
        OnPropertyChanged(nameof(NoMatches));
    }

    void Touch(string? package, bool launched)
    {
        if (string.IsNullOrEmpty(package)) return;
        if (!launched && _recent.Contains(package)) return; // a notification doesn't push a launched app down
        _recent.Remove(package);
        _recent.Insert(0, package);
        if (_recent.Count > 20) _recent.RemoveAt(_recent.Count - 1);
        RebuildRecent();
    }

    void RebuildRecent()
    {
        var list = _recent.Where(_items.ContainsKey).Select(p => _items[p]).Take(5).ToList();
        if (Recent.SequenceEqual(list)) return;
        Recent.Clear();
        foreach (var a in list) Recent.Add(a);
    }

    [RelayCommand]
    async Task Launch(PhoneAppItem? item)
    {
        if (item is null) return;
        if (_app.Features is not { } f) { Status = "Opening apps needs the mirroring tools."; return; }
        Touch(item.Package, launched: true);
        item.IsLaunching = true;
        Status = f.Adb.IsConnected ? "" : "Connecting ADB…";
        var s = await f.Launcher.AppAsync(item.Package, item.Name);
        Status = s is null ? (f.Launcher.LastProblem is { Length: > 0 } why ? why : $"Couldn't open {item.Name}.") : "";
        await Task.Delay(2500);
        item.IsLaunching = false;
    }

    /// <summary>Enter in the search box opens the first match.</summary>
    [RelayCommand]
    Task LaunchFirst() => Apps.Count > 0 && HasSearch ? Launch(Apps[0]) : Task.CompletedTask;

    [RelayCommand] void ClearSearch() => SearchText = "";

    [RelayCommand]
    async Task ToggleMute(PhoneAppItem? item)
    {
        if (item is null) return;
        var ok = await _app.Core.Hub.ToggleAppNotificationsAsync(item.Package, item.IsMuted);
        Status = ok ? (item.IsMuted ? $"{item.Name} notifications are back on" : $"{item.Name} notifications muted") : "Phone is not connected";
    }
}

// =====================================================================
// Devices tab (SPACE.md §5)
// =====================================================================

public sealed partial class DeviceCardViewModel : ObservableObject
{
    readonly DevicesViewModel _owner;

    public DeviceCardViewModel(DevicesViewModel owner, SpaceDevice device, bool isThisPc)
    {
        _owner = owner;
        Device = device;
        IsThisPc = isThisPc;
    }

    public SpaceDevice Device { get; private set; }
    public bool IsThisPc { get; }
    public string Id => Device.Id;
    public string Name => Device.Name;
    public bool IsPhone => Device.IsPhone;
    public bool IsComputer => !IsPhone && !IsThisPc;
    public string Model => IsThisPc ? "This PC" : Device.Model;
    public string KindIcon => Device.Kind switch
    {
        SpaceDeviceKind.Phone => Icons.Smartphone,
        SpaceDeviceKind.Mac => Icons.Laptop,
        _ => Icons.Monitor,
    };
    public bool IsOnline => Device.Online;
    public bool IsInUse => Device.InUse;
    public bool IsOffline => !Device.Online;
    public string StatusText => IsThisPc ? (Device.InUse ? "In use" : "This computer") : Device.StatusText;
    public string RouteText => Device.RouteText;
    public bool HasRoute => !IsThisPc && Device.Online && Device.RouteText.Length > 0 && Device.Route != SpaceRoute.Unavailable;
    public string RouteIcon => Device.Route == SpaceRoute.Direct ? Icons.Zap : Icons.Smartphone;
    public bool HasBattery => Device.Battery is not null;
    public string BatteryText => Device.Battery is { } b ? $"{b}%" : "";
    public string BatteryIcon => Device.Battery switch
    {
        null => Icons.Battery,
        <= 15 => Icons.BatteryWarning,
        <= 40 => Icons.BatteryLow,
        <= 75 => Icons.BatteryMedium,
        _ => Icons.BatteryFull,
    };
    public bool IsPlaying => Device.IsPlayingAudio || (IsThisPc && _owner.IsPlayingHere);
    public bool CanReceive => Device.CanReceive;
    public string DropText => $"Drop to send to {Name}";
    public string OfflineHint => IsPhone ? "Open H.I.B on the phone to reconnect." : "It shows up here as soon as it connects to your phone.";
    public string SendFilesTip => CanReceive ? $"Choose files to send to {Name}" : $"{Name} isn't reachable right now";

    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool IsDropTarget { get; set; }
    public bool HasStatus => Status.Length > 0;
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    public string RingLabel => _owner.IsRinging ? "Stop ringing" : "Ring";
    public string PlayLabel => _owner.IsPlayingHere ? "Stop" : "Play phone audio here";
    public string PlayIcon => _owner.IsPlayingHere ? Icons.Pause : Icons.Speaker;

    internal void Update(SpaceDevice d)
    {
        Device = d;
        Refresh();
    }

    internal void Refresh() => OnPropertyChanged(string.Empty);

    [RelayCommand]
    async Task SendFiles()
    {
        var paths = await SpaceActions.PickFilesAsync(_owner.App, "Send to " + Name);
        if (paths.Count > 0) await DropAsync(paths);
    }

    /// <summary>Files dropped on the card (or picked): send them to this device.</summary>
    public async Task DropAsync(IReadOnlyList<string> paths)
    {
        if (IsThisPc) return;
        if (!CanReceive) { Status = $"{Name} isn't reachable right now"; return; }
        Status = paths.Count == 1 ? "Sending 1 file…" : $"Sending {paths.Count} files…";
        var n = await SpaceActions.SendFilesAsync(_owner.App, Device, paths);
        Status = n == 0 ? "Couldn't send" : n == 1 ? "Sent 1 file" : $"Sent {n} files";
    }

    [RelayCommand]
    async Task SendClipboard()
    {
        Status = "Sending the clipboard…";
        Status = await SpaceActions.SendClipboardAsync(_owner.App, Device);
    }

    [RelayCommand] void Ring() => _owner.Ring();
    [RelayCommand] Task TogglePlay() => _owner.TogglePlayHereAsync();

    // ---------------- Mouse & keyboard (Roam, SPACE.md §9.6) ----------------

    HIB.Core.Roam.RoamEngine RoamEngine => _owner.App.Core.Roam;
    HIB.Core.Roam.RoamPeerStatus? Roam => IsComputer ? RoamEngine.StatusFor(Id) : null;

    /// <summary>The row shows for every computer on a direct link.</summary>
    public bool HasRoam => IsComputer && Device.Online && Device.Route == SpaceRoute.Direct && Roam is not null;

    public bool RoamEnabled
    {
        get => Roam?.Enabled ?? false;
        set
        {
            if (Roam is { } r && r.Enabled != value) RoamEngine.SetEnabled(Id, value);
        }
    }

    public bool RoamCmdAsCtrl
    {
        get => Roam?.CmdAsCtrl ?? true;
        set
        {
            if (Roam is { } r && r.CmdAsCtrl != value) RoamEngine.SetCmdAsCtrl(Id, value);
        }
    }

    /// <summary>"⌘ works as Ctrl on the PC" only means something with a Mac.</summary>
    public bool RoamIsMac => Roam?.IsMac ?? Device.Kind == SpaceDeviceKind.Mac;
    public bool RoamLeftSelected => Roam is { Side: HIB.Core.Roam.RoamSide.Left, NeedsAsk: false };
    public bool RoamRightSelected => Roam is { Side: HIB.Core.Roam.RoamSide.Right };
    public bool RoamAsks => Roam?.NeedsAsk ?? false;

    public string RoamSideLabel => Roam switch
    {
        null => "",
        { NeedsAsk: true } => $"Where is {Name}?",
        { SideSource: HIB.Core.Roam.RoamSideSource.PointSetup, Side: HIB.Core.Roam.RoamSide.Above } => "Above this PC · from Point setup",
        { SideSource: HIB.Core.Roam.RoamSideSource.PointSetup, Side: HIB.Core.Roam.RoamSide.Below } => "Below this PC · from Point setup",
        { SideSource: HIB.Core.Roam.RoamSideSource.PointSetup } => "Side · from Point setup",
        { SideSource: HIB.Core.Roam.RoamSideSource.Room, Side: var s } => $"{RoomSideWord(s)} this PC · from your desk",
        _ => "Side · set by you",
    };

    static string RoomSideWord(HIB.Core.Roam.RoamSide s) => s switch
    {
        HIB.Core.Roam.RoamSide.Left => "Left of",
        HIB.Core.Roam.RoamSide.Right => "Right of",
        HIB.Core.Roam.RoamSide.Above => "Above",
        _ => "Below",
    };

    /// <summary>With a room the desk decides; the ← / → picker gives way to "Arrange".</summary>
    public bool RoamInRoom => Roam is { SideSource: HIB.Core.Roam.RoamSideSource.Room };
    public bool RoamShowsPicker => !RoamInRoom;

    [RelayCommand] void Arrange() => _owner.App.Room.Show();

    public string RoamStatus => Roam?.Status ?? "";
    public bool RoamActive => Roam is { Role: not HIB.Core.Roam.RoamRole.None };
    public const string RoamLimitText = "No admin needed · can’t control admin apps or lock screen";
    public string RoamInfo => RoamLimitText;

    [RelayCommand] void RoamLeft() => RoamEngine.SetSide(Id, HIB.Core.Roam.RoamSide.Left);
    [RelayCommand] void RoamRight() => RoamEngine.SetSide(Id, HIB.Core.Roam.RoamSide.Right);

    // ---------------- Roam round 2 (SPACE.md §12.2–§12.5) ----------------

    /// <summary>"Latency 3 ms" (the link's ping round trip, median of the last 20), or empty before the first.</summary>
    public string RoamLatencyText => Roam?.LatencyMs is { } ms ? HIB.Core.Roam.LatencyMeter.Text(ms) : "";
    public bool RoamHasLatency => RoamLatencyText.Length > 0;
    /// <summary>Above 25 ms: "Wi-Fi is adding delay — 5 GHz or a cable to one computer helps".</summary>
    public bool RoamSlowLink => Roam?.LatencyMs is { } ms && ms > HIB.Core.Roam.LatencyMeter.TipAboveMs;
    public string RoamLatencyTip => HIB.Core.Roam.LatencyMeter.Tip;

    /// <summary>Pointer speed on that computer, 0.5×–2×.</summary>
    public double RoamPointerSpeed
    {
        get => Roam?.PointerSpeed ?? 1;
        set
        {
            if (Roam is { } r && Math.Abs(r.PointerSpeed - value) > 0.001) RoamEngine.SetPointerSpeed(Id, value);
        }
    }

    public string RoamPointerSpeedText => HIB.Core.Roam.RoamSpeed.Label(RoamPointerSpeed);

    /// <summary>Scroll speed on that computer, 0.5×–2×.</summary>
    public double RoamScrollSpeed
    {
        get => Roam?.ScrollSpeed ?? 1;
        set
        {
            if (Roam is { } r && Math.Abs(r.ScrollSpeed - value) > 0.001) RoamEngine.SetScrollSpeed(Id, value);
        }
    }

    public string RoamScrollSpeedText => HIB.Core.Roam.RoamSpeed.Label(RoamScrollSpeed);

    public bool RoamNaturalScroll
    {
        get => Roam?.NaturalScroll ?? false;
        set
        {
            if (Roam is { } r && r.NaturalScroll != value) RoamEngine.SetNaturalScroll(Id, value);
        }
    }

    /// <summary>What a computer's game is doing, when it is one ("Game mode on PC-INTERN · VALORANT").</summary>
    public bool RoamHasGame => Roam is { RemoteGame: not HIB.Core.Roam.RoamGameMode.Normal };
    public string RoamGameText => Roam is { RemoteGame: var g, RemoteApp: var app } && g != HIB.Core.Roam.RoamGameMode.Normal
        ? (g == HIB.Core.Roam.RoamGameMode.Locked ? "Game in front" : "Full-screen app in front") + (app.Length > 0 ? " · " + System.IO.Path.GetFileNameWithoutExtension(app) : "")
        : "";

    /// <summary>How "Get log" opens the saved file (tests replace it).</summary>
    public static Action<string> RevealLog { get; set; } = HIB.Core.Platform.PlatformShell.RevealInFolder;

    [ObservableProperty] public partial bool IsGettingLog { get; set; }

    /// <summary>
    /// SPACE.md §12.2 "Get log": asks that computer for its H.I.B log over the direct link and saves it as
    /// %LOCALAPPDATA%\H.I.B\logs\&lt;name&gt;.log, then shows it in its folder.
    /// </summary>
    [RelayCommand]
    async Task GetLog()
    {
        if (IsGettingLog) return;
        IsGettingLog = true;
        Status = $"Getting the log from {Name}…";
        try
        {
            var (path, problem) = await HIB.Core.Space.PeerDiagnostics.FetchAsync(_owner.App.Core.SpaceEngine.Network, Id, Name);
            if (path is null)
            {
                Status = problem ?? "Couldn't get the log";
                return;
            }
            Status = $"Saved {System.IO.Path.GetFileName(path)} with this PC's logs";
            RevealLog(path);
        }
        finally { IsGettingLog = false; }
    }
}

public partial class DevicesViewModel : ViewModelBase
{
    readonly List<DeviceCardViewModel> _cards = new();

    public DevicesViewModel(AppController app)
    {
        App = app;
        app.Core.Space.Changed += () => Ui.Post(Refresh);
        app.Core.Roam.Changed += () => Ui.Post(Refresh);
        if (app.Features is { } f)
        {
            f.Audio.PropertyChanged += (_, _) => Ui.Post(Refresh);
            f.Ring.PropertyChanged += (_, _) => Ui.Post(Refresh);
        }
        else app.Core.Hub.RingStateChanged += _ => Ui.Post(Refresh);
        app.Core.Link.ConnectionChanged += (_, connected) =>
        {
            if (connected) _ = app.Core.Ecosystem.RequestAsync();
            Ui.Post(Refresh);
        };
        ThisPc = new DeviceCardViewModel(this, ThisDevice(), true);
        Refresh();
        if (app.Core.Link.IsConnected) _ = app.Core.Ecosystem.RequestAsync();
    }

    internal AppController App { get; }

    /// <summary>The phone, then the other computers. This PC is <see cref="ThisPc"/> (shown last, small).</summary>
    public ObservableCollection<DeviceCardViewModel> Devices { get; } = new();
    public DeviceCardViewModel ThisPc { get; }
    [ObservableProperty] public partial string? PlayingOnText { get; set; }
    [ObservableProperty] public partial bool IsPlayingHere { get; set; }
    [ObservableProperty] public partial bool IsRinging { get; set; }
    public bool HasPlayingElsewhere => PlayingOnText is not null;
    public bool IsEmpty => Devices.Count == 0;
    public string Summary
    {
        get
        {
            var online = Devices.Count(d => d.IsOnline);
            return Devices.Count == 0 ? "Pair your phone to see your devices here."
                : $"{online} of {Devices.Count} online · drop files on a device to send them there";
        }
    }

    partial void OnPlayingOnTextChanged(string? value) => OnPropertyChanged(nameof(HasPlayingElsewhere));

    SpaceDevice ThisDevice() => new(App.Core.Space.ThisDeviceName, App.Core.Space.ThisDeviceName, SpaceDeviceKind.Windows,
        "This PC", true, false, SpaceRoute.Unavailable, null, IsPlayingHere);

    void Refresh()
    {
        var space = App.Core.Space;
        var devices = space.Devices;
        var playingOn = space.PlayingOn;
        IsPlayingHere = (playingOn is not null && playingOn == space.ThisDeviceName) || (App.Features?.Audio.IsActive ?? false);
        IsRinging = App.Features?.Ring.IsRinging ?? App.Core.Hub.IsRinging;
        var elsewhere = playingOn is not null && playingOn != space.ThisDeviceName
            ? devices.FirstOrDefault(d => d.Id == playingOn)?.Name ?? playingOn
            : null;
        PlayingOnText = elsewhere is null ? null : $"Playing on {elsewhere}";

        var wanted = new List<DeviceCardViewModel>();
        foreach (var d in devices)
        {
            var card = _cards.FirstOrDefault(c => c.Id == d.Id);
            if (card is null) { card = new DeviceCardViewModel(this, d, false); _cards.Add(card); }
            else card.Update(d);
            wanted.Add(card);
        }
        if (!Devices.SequenceEqual(wanted))
        {
            Devices.Clear();
            foreach (var c in wanted) Devices.Add(c);
        }
        ThisPc.Update(ThisDevice());
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CanArrange));
    }

    internal void Ring()
    {
        if (App.Features is { } f) f.Ring.Toggle();
        else _ = App.Core.Hub.RingAsync(!App.Core.Hub.IsRinging);
    }

    /// <summary>"Play phone audio here" / "Stop" (SPACE.md §3.5).</summary>
    internal async Task TogglePlayHereAsync()
    {
        var on = !IsPlayingHere;
        ThisPc.Status = on ? "Starting phone audio…" : "";
        var ok = await App.Core.Space.PlayHereAsync(on);
        // Until the engine coordinates Play, use this PC's own phone-audio bridge directly.
        if (!ok && App.Features is { } f)
        {
            if (on) f.Audio.Start(); else f.Audio.Stop();
            ok = true;
        }
        if (!ok) ThisPc.Status = "Phone audio needs ADB (Settings › Mirroring & ADB).";
        else if (!on) ThisPc.Status = "";
        Refresh();
    }

    [RelayCommand] void OpenPair() => App.Navigate?.Invoke("pair");

    /// <summary>"Arrange": the Room view, your desk with every computer's displays (SPACE.md §10.4).</summary>
    [RelayCommand] void Arrange() => App.Room.Show();

    /// <summary>Arrange shows once there is another computer to place.</summary>
    public bool CanArrange => Devices.Any(d => d.IsComputer);
}
