using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HIB.Core.Discovery;
using HIB.Core.Pairing;
using HIB.Core.Phone;
using HIB.Core.Platform;
using HIB.Services;
using QRCoder;

namespace HIB.ViewModels;

// =====================================================================
// Clipboard
// =====================================================================

public sealed partial class ClipboardItemViewModel : ObservableObject
{
    public ClipboardItemViewModel(ClipboardEntry e)
    {
        Entry = e;
        Image = ImageUtil.FromBytes(e.Image);
    }

    public ClipboardEntry Entry { get; }
    public string? Text => Entry.Text;
    public bool IsText => Entry.Text is not null;
    public Bitmap? Image { get; }
    public bool IsImage => Image is not null;
    public string Direction => Entry.FromPhone ? "From phone" : "To phone";
    public bool FromPhone => Entry.FromPhone;
    public string Time => Entry.Time.ToString("HH:mm:ss");
}

public partial class ClipboardViewModel : ViewModelBase
{
    readonly AppController _app;

    public ClipboardViewModel(AppController app)
    {
        _app = app;
        foreach (var e in ClipboardHistory.Entries) Items.Insert(0, new ClipboardItemViewModel(e));
        ClipboardHistory.Added += e => Ui.Post(() =>
        {
            Items.Insert(0, new ClipboardItemViewModel(e));
            if (Items.Count > 100) Items.RemoveAt(Items.Count - 1);
            OnPropertyChanged(nameof(IsEmpty));
        });
        SyncEnabled = app.Core.Settings.ClipboardSync;
        ImagesEnabled = app.Core.Settings.ClipboardImages;
    }

    public ObservableCollection<ClipboardItemViewModel> Items { get; } = new();
    public bool IsEmpty => Items.Count == 0;

    [ObservableProperty] public partial bool SyncEnabled { get; set; }
    [ObservableProperty] public partial bool ImagesEnabled { get; set; }
    [ObservableProperty] public partial string SendText { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "";

    partial void OnSyncEnabledChanged(bool value) { _app.Core.Settings.ClipboardSync = value; _app.Core.Settings.Save(); }
    partial void OnImagesEnabledChanged(bool value) { _app.Core.Settings.ClipboardImages = value; _app.Core.Settings.Save(); }

    [RelayCommand]
    async Task Send()
    {
        var text = SendText;
        if (string.IsNullOrEmpty(text)) return;
        var ok = await _app.Core.Hub.SendClipboardTextAsync(text);
        Status = ok ? "Sent to phone clipboard" : "Phone is not connected";
        if (ok)
        {
            ClipboardHistory.Add(new ClipboardEntry(false, text, null, DateTimeOffset.Now));
            SendText = "";
        }
    }

    [RelayCommand]
    async Task Copy(ClipboardItemViewModel item)
    {
        var cb = _app.MainWindow?.Clipboard;
        if (cb is null) return;
        if (item.Text is not null) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, item.Text);
        else if (item.Image is not null) await Avalonia.Input.Platform.ClipboardExtensions.SetBitmapAsync(cb, item.Image);
        Status = "Copied";
    }

    [RelayCommand]
    void Clear()
    {
        ClipboardHistory.Clear();
        Items.Clear();
        OnPropertyChanged(nameof(IsEmpty));
    }
}

/// <summary>In-memory clipboard history (both directions), fed from ClipboardSync.</summary>
public static class ClipboardHistory
{
    static readonly System.Collections.Generic.List<ClipboardEntry> _entries = new();
    public static event Action<ClipboardEntry>? Added;
    public static ClipboardEntry[] Entries { get { lock (_entries) return _entries.ToArray(); } }

    public static void Add(ClipboardEntry e)
    {
        lock (_entries)
        {
            _entries.Add(e);
            if (_entries.Count > 100) _entries.RemoveAt(0);
        }
        Added?.Invoke(e);
    }

    public static void Clear() { lock (_entries) _entries.Clear(); }
}

// =====================================================================
// Files
// =====================================================================

public sealed partial class TransferItemViewModel : ObservableObject
{
    public TransferItemViewModel(string id, string name) { Id = id; Name = name; }
    public string Id { get; }
    public string Name { get; }
    [ObservableProperty] public partial double Percent { get; set; }
    [ObservableProperty] public partial string Detail { get; set; } = "";
}

public sealed partial class FileItemViewModel : ObservableObject
{
    public FileItemViewModel(ReceivedFile f) { File = f; }
    public ReceivedFile File { get; }
    public string Name => File.Name;
    public string Detail => $"{AppController.FormatSize(File.Size)} · {File.Received:HH:mm} · {(File.From is { Length: > 0 } from ? "From " + from : File.FlickId is not null ? "Flick" : File.IsSearchFetch ? "Phone search" : "Phone")}";
    public string Path => File.Path;
    [RelayCommand] void Open() => PlatformShell.Open(File.Path);
    [RelayCommand] void Reveal() => PlatformShell.RevealInFolder(File.Path);
}

public partial class FilesViewModel : ViewModelBase
{
    readonly AppController _app;

    public FilesViewModel(AppController app)
    {
        _app = app;
        foreach (var f in ReceivedFiles.All) Files.Insert(0, new FileItemViewModel(f));
        ReceivedFiles.Added += f => Ui.Post(() => { Files.Insert(0, new FileItemViewModel(f)); OnPropertyChanged(nameof(IsEmpty)); });
        app.Core.Files.Progress += p => Ui.Post(() => OnProgress(p));
        app.Core.Ecosystem.Changed += () => Ui.Post(RefreshTargets);
        app.Core.Link.ConnectionChanged += (_, _) => Ui.Post(RefreshTargets);
        RefreshTargets();
        if (app.Core.Link.IsConnected) _ = app.Core.Ecosystem.RequestAsync();
    }

    // ---- Send to… (the phone, or another computer through the phone) ----

    public ObservableCollection<SendTarget> Targets { get; } = new();
    [ObservableProperty] public partial SendTarget? SelectedTarget { get; set; }
    public bool CanSend => Targets.Count > 0;
    public string SendHint => Targets.Count switch
    {
        0 => "Connect your phone to send files.",
        1 => "Drop files anywhere on this window, or choose them, to put them on your phone (Downloads/H.I.B).",
        _ => "Pick where they go, then drop files anywhere on this window or choose them. Other computers receive them through your phone.",
    };

    partial void OnSelectedTargetChanged(SendTarget? value)
    {
        if (value is not null) _app.PreferredSendTarget = value.Id;
    }

    void RefreshTargets()
    {
        var keep = SelectedTarget?.Id ?? _app.PreferredSendTarget;
        Targets.Clear();
        foreach (var t in _app.Core.Ecosystem.Targets) Targets.Add(t);
        SelectedTarget = Targets.FirstOrDefault(t => t.Id == keep) ?? Targets.FirstOrDefault();
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(SendHint));
    }

    [RelayCommand]
    async Task ChooseFiles()
    {
        var target = SelectedTarget;
        var storage = _app.MainWindow?.StorageProvider;
        if (target is null || storage is null) return;
        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Send to " + target.Name,
            AllowMultiple = true,
        });
        var paths = files.Select(f => Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(f)).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
        if (paths.Count > 0) await _app.SendFilesAsync(paths, target);
    }

    public ObservableCollection<TransferItemViewModel> Transfers { get; } = new();
    public ObservableCollection<FileItemViewModel> Files { get; } = new();
    public bool IsEmpty => Files.Count == 0 && Transfers.Count == 0;
    public string DownloadsPath => AppPaths.DownloadsDir;

    void OnProgress(TransferProgress p)
    {
        var t = Transfers.FirstOrDefault(x => x.Id == p.Id);
        if (p.Done)
        {
            if (t is not null) Transfers.Remove(t);
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }
        if (t is null)
        {
            t = new TransferItemViewModel(p.Id, p.Name);
            Transfers.Insert(0, t);
            OnPropertyChanged(nameof(IsEmpty));
        }
        t.Percent = p.Size > 0 ? 100.0 * p.Received / p.Size : 0;
        t.Detail = $"{AppController.FormatSize(p.Received)} of {AppController.FormatSize(p.Size)}";
    }

    [RelayCommand] void OpenDownloads() => PlatformShell.Open(AppPaths.DownloadsDir);
}

/// <summary>Files received this session (fed by the app controller).</summary>
public static class ReceivedFiles
{
    static readonly System.Collections.Generic.List<ReceivedFile> _all = new();
    public static event Action<ReceivedFile>? Added;
    public static ReceivedFile[] All { get { lock (_all) return _all.ToArray(); } }
    public static void Add(ReceivedFile f) { lock (_all) _all.Add(f); Added?.Invoke(f); }
}

// =====================================================================
// Pair
// =====================================================================

public sealed record DiscoveredPhoneItem(string Name, string Ips)
{
    public IReadOnlyList<string> IpList { get; init; } = Array.Empty<string>();
}

public partial class PairViewModel : ViewModelBase
{
    readonly AppController _app;

    public PairViewModel(AppController app)
    {
        _app = app;
        Refresh();
        app.Core.Link.ConnectionChanged += (_, _) => Ui.Post(RefreshStatus);
        if (app.Core.Udp is not null) app.Core.Udp.PhonesChanged += () => Ui.Post(RefreshPhones);
        RefreshPhones();
    }

    [ObservableProperty] public partial Bitmap? QrImage { get; set; }
    [ObservableProperty] public partial string QrText { get; set; } = "";
    [ObservableProperty] public partial string DesktopName { get; set; } = "";
    [ObservableProperty] public partial string Addresses { get; set; } = "";
    [ObservableProperty] public partial int Port { get; set; }
    [ObservableProperty] public partial string ConnectionStatus { get; set; } = "";
    [ObservableProperty] public partial bool ConfirmReset { get; set; }
    [ObservableProperty] public partial bool ShowQrText { get; set; }
    [ObservableProperty] public partial string DiscoveryMode { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool IsConnected { get; set; }
    public ObservableCollection<DiscoveredPhoneItem> Phones { get; } = new();
    public bool HasPhones => Phones.Count > 0;
    public bool NoPhones => Phones.Count == 0;
    public bool FirewallSupported => FirewallHelper.IsSupported;
    /// <summary>The server pill under the QR, like the Mac's "Ready".</summary>
    public string ServerText => _app.Core.Link.IsListening ? "Ready" : "Stopped";
    public bool IsReady => _app.Core.Link.IsListening;
    public bool HasStatus => Status.Length > 0;
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    [RelayCommand]
    public void Refresh()
    {
        var core = _app.Core;
        QrText = core.BuildQr();
        QrImage = RenderQr(QrText);
        DesktopName = core.Link.DesktopName;
        Port = core.Link.Port;
        var addrs = NetworkInfo.GetLocalIPv4();
        Addresses = addrs.Count == 0 ? "No network connection" : string.Join("\n", addrs.Select(a => $"{a.Address}   ({a.InterfaceName})"));
        DiscoveryMode = core.Mdns is null && core.Udp is null
            ? "Discovery off"
            : $"mDNS: {core.Mdns?.Mode ?? "off"} · UDP presence: {(core.Udp is null ? "off" : "on")}";
        RefreshStatus();
    }

    void RefreshStatus()
    {
        var link = _app.Core.Link;
        IsConnected = link.IsConnected;
        ConnectionStatus = link.IsConnected && link.Device is { } d ? $"Connected to {d.Name}" : "Open H.I.B on your phone and scan this code";
        OnPropertyChanged(nameof(ServerText));
        OnPropertyChanged(nameof(IsReady));
    }

    void RefreshPhones()
    {
        Phones.Clear();
        foreach (var p in _app.Core.Udp?.Phones ?? Array.Empty<DiscoveredPhone>())
            Phones.Add(new DiscoveredPhoneItem(p.Name, string.Join(", ", p.Ips)) { IpList = p.Ips });
        OnPropertyChanged(nameof(HasPhones));
        OnPropertyChanged(nameof(NoPhones));
    }

    public static Bitmap? RenderQr(string text)
    {
        try
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(10, new byte[] { 9, 12, 33, 255 }, new byte[] { 255, 255, 255, 255 }, true);
            return ImageUtil.FromBytes(png);
        }
        catch { return null; }
    }

    [RelayCommand] void AskReset() => ConfirmReset = true;
    [RelayCommand] void CancelReset() => ConfirmReset = false;

    [RelayCommand]
    void Reset()
    {
        ConfirmReset = false;
        _app.Core.ResetPairing();
        Refresh();
        Status = "New pairing key created. Scan the new code on your phone.";
    }

    [RelayCommand] void ToggleQrText() => ShowQrText = !ShowQrText;

    /// <summary>Asks one phone found on the network to connect (its H.I.B app must be open).</summary>
    [RelayCommand]
    async Task ConnectTo(DiscoveredPhoneItem? phone)
    {
        if (phone is null || phone.IpList.Count == 0) return;
        Status = $"Asking {phone.Name} to connect…";
        var ok = await HIB.Core.Discovery.WakeUpClient.WakeAsync(phone.IpList, _app.Core.Link.Port, _app.Core.Link.DesktopName);
        Status = ok ? $"{phone.Name} answered. If it doesn't connect, scan the code once." : $"{phone.Name} didn't answer. Scan the code instead.";
    }

    [RelayCommand]
    async Task CopyCode()
    {
        var cb = _app.MainWindow?.Clipboard;
        if (cb is null) return;
        await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, QrText);
        Status = "Pairing code copied.";
    }

    [RelayCommand]
    async Task Wake()
    {
        Status = "Asking the phone to connect…";
        var ok = await _app.Core.WakePhoneAsync();
        Status = ok ? "Phone answered." : "No answer over HTTP (UDP wake-up sent).";
    }

    [RelayCommand]
    async Task AddFirewallRule()
    {
        Status = "Waiting for Windows to approve…";
        var ok = await Task.Run(() => FirewallHelper.AddRules(_app.Core.Link.Port));
        Status = ok ? "Firewall rules added." : "Firewall rules were not added (cancelled or failed).";
    }
}

// =====================================================================
// Settings
// =====================================================================

public partial class SettingsViewModel : ViewModelBase
{
    readonly AppController _app;
    bool _loading = true;

    public SettingsViewModel(AppController app)
    {
        _app = app;
        var s = app.Core.Settings;
        NotificationToasts = s.NotificationToasts;
        SilentToasts = s.SilentToasts;
        ToastSeconds = s.ToastSeconds;
        CallPopups = s.CallPopups;
        ClipboardSync = s.ClipboardSync;
        ClipboardImages = s.ClipboardImages;
        AutoOpenLinks = s.AutoOpenLinks;
        StartMinimized = s.StartMinimized;
        CloseToTray = s.CloseToTray;
        Discovery = s.Discovery;
        StartWithWindows = AutoStart.IsEnabled;
        NewName = s.DesktopName ?? "";
        _loading = false;
        RefreshFlick();
        app.Flick.Picker.TeachingStepChanged += (_, _, _) => Ui.Post(RefreshFlick);
        LogText = string.Join("\n", app.Core.LogLines.TakeLast(200));
        app.Core.LogAdded += line => Ui.Post(() =>
        {
            var lines = (LogText + "\n" + line).Split('\n');
            LogText = string.Join("\n", lines.TakeLast(200));
        });
    }

    [ObservableProperty] public partial bool NotificationToasts { get; set; }
    [ObservableProperty] public partial bool SilentToasts { get; set; }
    [ObservableProperty] public partial int ToastSeconds { get; set; }
    [ObservableProperty] public partial bool CallPopups { get; set; }
    [ObservableProperty] public partial bool ClipboardSync { get; set; }
    [ObservableProperty] public partial bool ClipboardImages { get; set; }
    [ObservableProperty] public partial bool AutoOpenLinks { get; set; }
    [ObservableProperty] public partial bool StartMinimized { get; set; }
    [ObservableProperty] public partial bool CloseToTray { get; set; }
    [ObservableProperty] public partial bool Discovery { get; set; }
    [ObservableProperty] public partial bool StartWithWindows { get; set; }
    [ObservableProperty] public partial string NewName { get; set; } = "";
    [ObservableProperty] public partial string NameStatus { get; set; } = "";
    [ObservableProperty] public partial string FlickStatus { get; set; } = "";
    [ObservableProperty] public partial bool IsTeaching { get; set; }
    [ObservableProperty] public partial string LogText { get; set; } = "";
    public bool AutoStartSupported => AutoStart.IsSupported;
    public string DataDir => _app.Core.Options.DataDir;
    public string DesktopName => _app.Core.Link.DesktopName;
    public string DownloadsPath => AppPaths.DownloadsDir;

    /// <summary>The top-edge Flick shelf (SPACE.md §5).</summary>
    public bool HasFlickShelfSetting => true;
    public bool ShowFlickShelf
    {
        get => _app.Core.Settings.ShowFlickShelf;
        set
        {
            if (_app.Core.Settings.ShowFlickShelf == value) return;
            _app.Core.Settings.ShowFlickShelf = value;
            _app.Core.Settings.Save();
            OnPropertyChanged();
        }
    }

    partial void OnNotificationToastsChanged(bool value) => Save(s => s.NotificationToasts = value);
    partial void OnSilentToastsChanged(bool value) => Save(s => s.SilentToasts = value);
    partial void OnToastSecondsChanged(int value) => Save(s => s.ToastSeconds = Math.Clamp(value, 2, 60));
    partial void OnCallPopupsChanged(bool value) => Save(s => s.CallPopups = value);
    partial void OnClipboardSyncChanged(bool value) => Save(s => s.ClipboardSync = value);
    partial void OnClipboardImagesChanged(bool value) => Save(s => s.ClipboardImages = value);
    partial void OnAutoOpenLinksChanged(bool value) => Save(s => s.AutoOpenLinks = value);
    partial void OnStartMinimizedChanged(bool value) => Save(s => s.StartMinimized = value);
    partial void OnCloseToTrayChanged(bool value) => Save(s => s.CloseToTray = value);

    partial void OnDiscoveryChanged(bool value)
    {
        if (_loading) return;
        Save(s => s.Discovery = value);
        _app.Core.RestartDiscovery();
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;
        if (!AutoStart.Set(value)) NameStatus = "Could not change Start with Windows";
    }

    void Save(Action<AppSettings> apply)
    {
        if (_loading) return;
        apply(_app.Core.Settings);
        _app.Core.Settings.Save();
    }

    [RelayCommand]
    void Rename()
    {
        var name = NewName.Trim();
        if (name.Length == 0) return;
        _app.Core.Rename(name);
        NameStatus = "Renamed. Re-scan the QR on the phone so it reconnects automatically under the new name.";
    }

    void RefreshFlick()
    {
        var f = _app.Flick;
        IsTeaching = f.Picker.IsTeaching;
        var screens = f.ScreenCount;
        FlickStatus = IsTeaching
            ? "Teaching… point the phone at the highlighted screen and flick anything."
            : screens < 2
                ? "Only one screen connected — flicks land on it."
                : $"{f.TaughtCount} of {screens} screens taught. Untaught: swipe up-left/up-right picks the neighbouring screen.";
    }

    [RelayCommand] void TeachScreens() { _app.Flick.StartTeaching(); RefreshFlick(); }
    [RelayCommand] void StopTeaching() { _app.Flick.CancelTeaching(); RefreshFlick(); }
    [RelayCommand] void ForgetScreens() { _app.Flick.Picker.ForgetAims(); RefreshFlick(); }
    [RelayCommand] void OpenDataFolder() => PlatformShell.Open(_app.Core.Options.DataDir);
}

// =====================================================================
// Settings sections that share SettingsViewModel (like the Mac's per-section settings views)
// =====================================================================

/// <summary>"This PC": name, startup, discovery, data folder, diagnostics.</summary>
public sealed class GeneralSettingsViewModel(SettingsViewModel settings) : ViewModelBase
{
    public SettingsViewModel S { get; } = settings;
}

/// <summary>"Notifications": pop-ups and call pop-ups on this PC.</summary>
public sealed class AlertSettingsViewModel(SettingsViewModel settings) : ViewModelBase
{
    public SettingsViewModel S { get; } = settings;
}

/// <summary>"Flick": links, several screens and the Flick shelf.</summary>
public sealed class FlickSettingsViewModel(SettingsViewModel settings) : ViewModelBase
{
    public SettingsViewModel S { get; } = settings;
}

// =====================================================================
// About
// =====================================================================

public partial class AboutViewModel : ViewModelBase
{
    public string Version => "Version " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "4.1.0");
    public string Maker => "Made by Haitham Hamid";
    public Bitmap? Icon { get; } = ImageUtil.AppIcon();
    public string Platform => $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} · .NET {Environment.Version}";
}
