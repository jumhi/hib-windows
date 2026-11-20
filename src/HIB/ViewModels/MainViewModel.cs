using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HIB.Services;

namespace HIB.ViewModels;

public enum NavKind
{
    /// <summary>An icon in the floating tab pill at the top (Notifications, Apps, Devices, Settings).</summary>
    Tab,
    /// <summary>A row in the Settings section list.</summary>
    Section,
}

/// <summary>
/// A page: a tab in the top pill or a Settings section. The content factory may return a
/// ViewModelBase (resolved by ViewLocator) or any Avalonia Control.
/// </summary>
public sealed partial class NavItem : ObservableObject
{
    readonly Func<object?> _factory;
    object? _content;

    public NavItem(string key, string title, string iconPath, Func<object?> factory, int order = 100, NavKind kind = NavKind.Section, string? subtitle = null)
    {
        Key = key;
        Title = title;
        IconPath = iconPath;
        _factory = factory;
        Order = order;
        Kind = kind;
        Subtitle = subtitle;
    }

    public string Key { get; }
    public string Title { get; }
    /// <summary>Lucide path data (24×24) for <see cref="HIB.Views.Controls.Icon"/>.</summary>
    public string IconPath { get; }
    public int Order { get; }
    public NavKind Kind { get; }
    public bool IsTab => Kind == NavKind.Tab;
    public object? Content => _content ??= _factory();

    [ObservableProperty] public partial string? Subtitle { get; set; }
    [ObservableProperty] public partial string? Badge { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    public bool HasBadge => !string.IsNullOrEmpty(Badge);
    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);
    partial void OnBadgeChanged(string? value) => OnPropertyChanged(nameof(HasBadge));
    partial void OnSubtitleChanged(string? value) => OnPropertyChanged(nameof(HasSubtitle));
}

/// <summary>
/// The main window, laid out like the Mac app: a left column (the linked phone, or the Settings section
/// list) and, on the right, a floating tab pill with the page below it. With no phone paired yet the
/// window shows the pairing (QR) page instead.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    readonly AppController _app;
    readonly NavItem _scanTab;
    readonly NavItem _notificationsTab;
    readonly NavItem _appsTab;
    readonly NavItem _devicesTab;
    readonly NavItem _settingsTab;

    public MainViewModel(AppController app)
    {
        _app = app;
        var core = app.Core;
        Home = new HomeViewModel(app);
        Notifications = new NotificationsViewModel(app);
        Apps = new AppsViewModel(app);
        Devices = new DevicesViewModel(app);
        Settings = new SettingsViewModel(app);
        Pair = new PairViewModel(app);
        Home.RecentApps = Apps.Recent;
        // The phone's "mute" quick action and Settings › Notifications are the same switch.
        Home.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(HomeViewModel.IsSilenced)) Settings.NotificationToasts = !Home.IsSilenced; };
        Settings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SettingsViewModel.NotificationToasts)) Home.IsSilenced = !Settings.NotificationToasts; };

        // ---- tabs (top pill) ----
        _scanTab = Register(new NavItem("scan", "Pair your phone", Icons.QrCode, () => Pair, 0, NavKind.Tab));
        _notificationsTab = Register(new NavItem("notifications", "Notifications", Icons.Bell, () => Notifications, 10, NavKind.Tab));
        _appsTab = Register(new NavItem("apps", "Apps", Icons.LayoutGrid, () => Apps, 20, NavKind.Tab));
        _devicesTab = Register(new NavItem("devices", "Devices", Icons.MonitorSmartphone, () => Devices, 30, NavKind.Tab));
        _settingsTab = Register(new NavItem("settings", "Settings", Icons.Settings, () => null, 40, NavKind.Tab));

        // ---- Settings sections (everything that used to be a sidebar page) ----
        Register(new NavItem("general", "This PC", Icons.Monitor, () => new GeneralSettingsViewModel(Settings), 100, subtitle: core.Link.DesktopName));
        Register(new NavItem("alerts", "Notifications", Icons.Bell, () => new AlertSettingsViewModel(Settings), 110));
        Register(new NavItem("clipboard", "Clipboard", Icons.Clipboard, () => new ClipboardViewModel(app), 120));
        Register(new NavItem("files", "Files", Icons.Folder, () => new FilesViewModel(app), 130));
        Register(new NavItem("flick", "Flick", Icons.Hand, () => new FlickSettingsViewModel(Settings), 140));
        if (app.Features is { } f)
        {
            Register(new NavItem("phone", "Mirroring & ADB", Icons.Cast, () => new PhoneViewModel(app, f), 150));
            Register(new NavItem("webcam", "Webcam", Icons.Webcam, () => new WebcamViewModel(app, f), 160));
            Register(new NavItem("calls", "Calls", Icons.Phone, () => new CallsViewModel(app, f), 170));
            Register(new NavItem("findmy", "Find My", Icons.MapPin, () => new FindMyViewModel(app, f), 180));
            Register(new NavItem("search", "Search phone", Icons.Search, () => new SearchPhoneViewModel(app, f), 190));
            Register(new NavItem("agent", "Phone Agent", Icons.Bot, () => new PhoneAgentViewModel(app, f), 200));
        }
        Register(new NavItem("pair", "Pair a phone", Icons.QrCode, () => Pair, 900));
        Register(new NavItem("about", "About", Icons.Info, () => new AboutViewModel(), 910));
        SelectedSection = Sections.FirstOrDefault();

        Notifications.CountChanged += c => _notificationsTab.Badge = c > 0 ? (c > 99 ? "99+" : c.ToString()) : null;

        app.Navigate = key => Navigate(key);
        core.Link.ConnectionChanged += (_, _) => Ui.Post(RefreshStatus);
        core.Link.DeviceUpdated += (_, _) => Ui.Post(RefreshStatus);
        RefreshStatus();

        // Like the Mac: pairing first when there's no phone, otherwise Notifications.
        Navigate(IsPaired ? "notifications" : "scan");
    }

    public HomeViewModel Home { get; }
    public NotificationsViewModel Notifications { get; }
    public AppsViewModel Apps { get; }
    public DevicesViewModel Devices { get; }
    public SettingsViewModel Settings { get; }
    public PairViewModel Pair { get; }

    /// <summary>Every page (tabs and Settings sections).</summary>
    public ObservableCollection<NavItem> NavItems { get; } = new();
    /// <summary>The icons shown in the floating tab pill right now.</summary>
    public ObservableCollection<NavItem> Tabs { get; } = new();
    /// <summary>The Settings section list.</summary>
    public ObservableCollection<NavItem> Sections { get; } = new();

    [ObservableProperty] public partial NavItem? SelectedTab { get; set; }
    [ObservableProperty] public partial NavItem? SelectedSection { get; set; }
    [ObservableProperty] public partial object? CurrentContent { get; set; }
    [ObservableProperty] public partial bool IsConnected { get; set; }
    [ObservableProperty] public partial bool IsPaired { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "Waiting for phone";
    [ObservableProperty] public partial string DesktopName { get; set; } = "";
    public Bitmap? AppIcon { get; } = ImageUtil.AppIcon();
    public string VersionText => "H.I.B v" + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "4.1.0");

    /// <summary>Settings is open: the left column shows the section list.</summary>
    public bool IsSettings => SelectedTab == _settingsTab;
    /// <summary>The phone column (linked phone + mockup + Mirror/Desktop).</summary>
    public bool ShowPhoneColumn => IsPaired && !IsSettings;
    /// <summary>Any left column at all (none on the pairing page).</summary>
    public bool ShowLeftColumn => ShowPhoneColumn || IsSettings;

    NavItem Register(NavItem item)
    {
        RegisterPage(item);
        return item;
    }

    /// <summary>Adds a page (feature modules can call this). Sections are ordered by <see cref="NavItem.Order"/>.</summary>
    public void RegisterPage(NavItem item)
    {
        NavItems.Add(item);
        if (item.Kind != NavKind.Section) return;
        var index = 0;
        while (index < Sections.Count && Sections[index].Order <= item.Order) index++;
        Sections.Insert(index, item);
    }

    void RebuildTabs()
    {
        var wanted = IsPaired
            ? new[] { _notificationsTab, _appsTab, _devicesTab, _settingsTab }
            : new[] { _scanTab, _settingsTab };
        if (Tabs.SequenceEqual(wanted)) return;
        Tabs.Clear();
        foreach (var t in wanted) Tabs.Add(t);
    }

    /// <summary>
    /// Opens a tab ("notifications", "apps", "devices", "settings", "scan") or a Settings section
    /// ("general", "alerts", "clipboard", "files", "flick", "phone", "webcam", "calls", "findmy", "search",
    /// "agent", "pair", "about").
    /// </summary>
    [RelayCommand]
    public void Navigate(string key)
    {
        key = key switch
        {
            "home" => "devices",
            "pair" when !IsPaired => "scan",
            "scan" when IsPaired => "pair",
            "notifications" or "apps" or "devices" when !IsPaired => "scan",
            _ => key,
        };
        var item = NavItems.FirstOrDefault(n => n.Key == key);
        if (item is null) return;
        if (item.Kind == NavKind.Section)
        {
            SelectedSection = item;
            SelectedTab = _settingsTab;
        }
        else SelectedTab = item;
        UpdateContent();
    }

    [RelayCommand]
    void Select(NavItem item) => Navigate(item.Key);

    partial void OnSelectedTabChanged(NavItem? value) => UpdateContent();
    partial void OnSelectedSectionChanged(NavItem? value) => UpdateContent();

    void UpdateContent()
    {
        foreach (var n in NavItems)
            n.IsSelected = n == SelectedTab || (IsSettings && n == SelectedSection);
        CurrentContent = IsSettings ? SelectedSection?.Content : SelectedTab?.Content;
        Notifications.MarkHidden();
        if (CurrentContent is NotificationsViewModel nvm) nvm.MarkSeen();
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(ShowPhoneColumn));
        OnPropertyChanged(nameof(ShowLeftColumn));
    }

    void RefreshStatus()
    {
        var link = _app.Core.Link;
        IsConnected = link.IsConnected;
        DesktopName = link.DesktopName;
        StatusText = link.IsConnected && link.Device is { } d ? $"Connected · {d.Name}" : "Waiting for phone";
        var general = NavItems.FirstOrDefault(n => n.Key == "general");
        if (general is not null) general.Subtitle = link.DesktopName;

        var wasPaired = IsPaired;
        IsPaired = link.IsConnected || _app.Core.Settings.LastPhoneName is not null;
        RebuildTabs();
        if (IsPaired && !wasPaired && SelectedTab == _scanTab) Navigate("notifications");
        else if (!IsPaired && wasPaired && !IsSettings) Navigate("scan");
        OnPropertyChanged(nameof(ShowPhoneColumn));
        OnPropertyChanged(nameof(ShowLeftColumn));
    }
}
