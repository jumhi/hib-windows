using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HIB.Services;

namespace HIB.ViewModels;

/// <summary>
/// A sidebar entry. Feature modules add pages with <see cref="MainViewModel.RegisterPage"/>;
/// the content factory may return a ViewModelBase (resolved by ViewLocator) or any Avalonia Control.
/// </summary>
public sealed partial class NavItem : ObservableObject
{
    readonly Func<object> _factory;
    object? _content;

    public NavItem(string key, string title, string iconPath, Func<object> factory, int order = 100, bool footer = false)
    {
        Key = key;
        Title = title;
        IconPath = iconPath;
        _factory = factory;
        Order = order;
        IsFooter = footer;
    }

    public string Key { get; }
    public string Title { get; }
    /// <summary>SVG path data (24×24 viewbox) for the PathIcon.</summary>
    public string IconPath { get; }
    public int Order { get; }
    public bool IsFooter { get; }
    public object Content => _content ??= _factory();

    [ObservableProperty] public partial string? Badge { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    public bool HasBadge => !string.IsNullOrEmpty(Badge);
    partial void OnBadgeChanged(string? value) => OnPropertyChanged(nameof(HasBadge));
}

public static class Icons
{
    public const string Home = "M10,20V14H14V20H19V12H22L12,3L2,12H5V20H10Z";
    public const string Bell = "M21,19V20H3V19L5,17V11C5,7.9 7.03,5.17 10,4.29C10,4.19 10,4.1 10,4A2,2 0 0,1 12,2A2,2 0 0,1 14,4C14,4.1 14,4.19 14,4.29C16.97,5.17 19,7.9 19,11V17L21,19M14,21A2,2 0 0,1 12,23A2,2 0 0,1 10,21";
    public const string Clipboard = "M19,3H14.82C14.4,1.84 13.3,1 12,1C10.7,1 9.6,1.84 9.18,3H5A2,2 0 0,0 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V5A2,2 0 0,0 19,3M12,3A1,1 0 0,1 13,4A1,1 0 0,1 12,5A1,1 0 0,1 11,4A1,1 0 0,1 12,3";
    public const string Folder = "M10,4H4C2.89,4 2,4.89 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8C22,6.89 21.1,6 20,6H12L10,4Z";
    public const string Qr = "M3,11H5V13H3V11M11,5H13V9H11V5M9,11H13V15H11V13H9V11M15,11H17V13H19V11H21V13H19V15H21V19H19V21H17V19H13V21H11V17H15V15H17V13H15V11M19,19V15H17V19H19M15,3H21V9H15V3M17,5V7H19V5H17M3,3H9V9H3V3M5,5V7H7V5H5M3,15H9V21H3V15M5,17V19H7V17H5Z";
    public const string Tune = "M3,17V19H9V17H3M3,5V7H13V5H3M13,21V19H21V17H13V15H11V21H13M7,9V11H3V13H7V15H9V9H7M21,13V11H11V13H21M15,9H17V7H21V5H17V3H15V9Z";
    public const string Info = "M13,9H11V7H13M13,17H11V11H13M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2Z";
    public const string Android = "M17,19H7V5H17M17,1H7C5.89,1 5,1.89 5,3V21A2,2 0 0,0 7,23H17A2,2 0 0,0 19,21V3C19,1.89 18.1,1 17,1Z";
    public const string MapMarker = "M12,11.5A2.5,2.5 0 0,1 9.5,9A2.5,2.5 0 0,1 12,6.5A2.5,2.5 0 0,1 14.5,9A2.5,2.5 0 0,1 12,11.5M12,2A7,7 0 0,0 5,9C5,14.25 12,22 12,22C12,22 19,14.25 19,9A7,7 0 0,0 12,2Z";
    public const string Robot = "M12,2A2,2 0 0,1 14,4C14,4.74 13.6,5.39 13,5.73V7H14A7,7 0 0,1 21,14H22A1,1 0 0,1 23,15V18A1,1 0 0,1 22,19H21V20A2,2 0 0,1 19,22H5A2,2 0 0,1 3,20V19H2A1,1 0 0,1 1,18V15A1,1 0 0,1 2,14H3A7,7 0 0,1 10,7H11V5.73C10.4,5.39 10,4.74 10,4A2,2 0 0,1 12,2M7.5,13A2.5,2.5 0 0,0 5,15.5A2.5,2.5 0 0,0 7.5,18A2.5,2.5 0 0,0 10,15.5A2.5,2.5 0 0,0 7.5,13M16.5,13A2.5,2.5 0 0,0 14,15.5A2.5,2.5 0 0,0 16.5,18A2.5,2.5 0 0,0 19,15.5A2.5,2.5 0 0,0 16.5,13Z";
    public const string Magnify = "M9.5,3A6.5,6.5 0 0,1 16,9.5C16,11.11 15.41,12.59 14.44,13.73L14.71,14H15.5L20.5,19L19,20.5L14,15.5V14.71L13.73,14.44C12.59,15.41 11.11,16 9.5,16A6.5,6.5 0 0,1 3,9.5A6.5,6.5 0 0,1 9.5,3M9.5,5C7,5 5,7 5,9.5C5,12 7,14 9.5,14C12,14 14,12 14,9.5C14,7 12,5 9.5,5Z";
    public const string Phone = "M6.62,10.79C8.06,13.62 10.38,15.94 13.21,17.38L15.41,15.18C15.69,14.9 16.08,14.82 16.43,14.93C17.55,15.3 18.75,15.5 20,15.5A1,1 0 0,1 21,16.5V20A1,1 0 0,1 20,21A17,17 0 0,1 3,4A1,1 0 0,1 4,3H7.5A1,1 0 0,1 8.5,4C8.5,5.25 8.7,6.45 9.07,7.57C9.18,7.92 9.1,8.31 8.82,8.59L6.62,10.79Z";
}

public partial class MainViewModel : ViewModelBase
{
    readonly AppController _app;

    public MainViewModel(AppController app)
    {
        _app = app;
        var core = app.Core;
        Home = new HomeViewModel(app);
        Notifications = new NotificationsViewModel(app);

        RegisterPage(new NavItem("home", "Home", Icons.Home, () => Home, 0));
        RegisterPage(new NavItem("notifications", "Notifications", Icons.Bell, () => Notifications, 10));
        RegisterPage(new NavItem("clipboard", "Clipboard", Icons.Clipboard, () => new ClipboardViewModel(app), 20));
        RegisterPage(new NavItem("files", "Files", Icons.Folder, () => new FilesViewModel(app), 30));
        if (app.Features is { } f)
        {
            RegisterPage(new NavItem("phone", "Phone", Icons.Android, () => new PhoneViewModel(app, f), 40));
            RegisterPage(new NavItem("search", "Search phone", Icons.Magnify, () => new SearchPhoneViewModel(app, f), 50));
            RegisterPage(new NavItem("findmy", "Find My", Icons.MapMarker, () => new FindMyViewModel(app, f), 60));
            RegisterPage(new NavItem("agent", "Phone Agent", Icons.Robot, () => new PhoneAgentViewModel(app, f), 70));
        }
        RegisterPage(new NavItem("pair", "Pair", Icons.Qr, () => new PairViewModel(app), 900, footer: true));
        RegisterPage(new NavItem("settings", "Settings", Icons.Tune, () => new SettingsViewModel(app), 910, footer: true));
        RegisterPage(new NavItem("about", "About", Icons.Info, () => new AboutViewModel(), 920, footer: true));

        Notifications.CountChanged += c =>
        {
            var item = NavItems.FirstOrDefault(n => n.Key == "notifications");
            if (item is not null) item.Badge = c > 0 ? (c > 99 ? "99+" : c.ToString()) : null;
        };

        app.Navigate = key => Navigate(key);
        core.Link.ConnectionChanged += (_, _) => Ui.Post(RefreshStatus);
        core.Link.DeviceUpdated += (_, _) => Ui.Post(RefreshStatus);
        RefreshStatus();

        // Unpaired first run: open the Pair page.
        Navigate(core.Settings.LastPhoneName is null ? "pair" : "home");
    }

    public HomeViewModel Home { get; }
    public NotificationsViewModel Notifications { get; }

    public ObservableCollection<NavItem> NavItems { get; } = new();
    public ObservableCollection<NavItem> FooterItems { get; } = new();

    [ObservableProperty] public partial NavItem? Selected { get; set; }
    [ObservableProperty] public partial object? CurrentContent { get; set; }
    [ObservableProperty] public partial bool IsConnected { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "Waiting for phone";
    [ObservableProperty] public partial string DesktopName { get; set; } = "";
    public Bitmap? AppIcon { get; } = ImageUtil.AppIcon();

    /// <summary>Adds a sidebar page (feature modules can call this). Ordered by <see cref="NavItem.Order"/>.</summary>
    public void RegisterPage(NavItem item)
    {
        var list = item.IsFooter ? FooterItems : NavItems;
        var index = 0;
        while (index < list.Count && list[index].Order <= item.Order) index++;
        list.Insert(index, item);
    }

    [RelayCommand]
    public void Navigate(string key)
    {
        var item = NavItems.Concat(FooterItems).FirstOrDefault(n => n.Key == key);
        if (item is not null) Selected = item;
    }

    [RelayCommand]
    void Select(NavItem item) => Selected = item;

    partial void OnSelectedChanged(NavItem? value)
    {
        foreach (var n in NavItems.Concat(FooterItems)) n.IsSelected = n == value;
        CurrentContent = value?.Content;
        Notifications.MarkHidden();
        if (value?.Content is NotificationsViewModel nvm) nvm.MarkSeen();
    }

    void RefreshStatus()
    {
        var link = _app.Core.Link;
        IsConnected = link.IsConnected;
        DesktopName = link.DesktopName;
        StatusText = link.IsConnected && link.Device is { } d ? $"Connected · {d.Name}" : "Waiting for phone";
    }
}
