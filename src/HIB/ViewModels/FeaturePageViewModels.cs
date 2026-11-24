using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HIB.Core.Phone;
using HIB.Core.Platform;
using HIB.Features.Adb;
using HIB.Features.FindMy;
using HIB.Features.Mirror;
using HIB.Features.PhoneAgent;
using HIB.Features.PhoneSearch;
using HIB.Services;

namespace HIB.ViewModels;

/// <summary>byte[] (PNG/JPEG) → Bitmap for bindings.</summary>
public sealed class BytesToBitmapConverter : IValueConverter
{
    public static readonly BytesToBitmapConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => ImageUtil.FromBytes(value as byte[]);
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Phone Agent step kind → accent brush.</summary>
public sealed class StepKindBrushConverter : IValueConverter
{
    public static readonly StepKindBrushConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        PhoneAgentStepKind.User => new SolidColorBrush(Color.Parse("#20D9FF")),
        PhoneAgentStepKind.Action => new SolidColorBrush(Color.Parse("#7FB7FF")),
        PhoneAgentStepKind.Done => new SolidColorBrush(Color.Parse("#BAFF38")),
        PhoneAgentStepKind.Error => new SolidColorBrush(Color.Parse("#FF5C70")),
        PhoneAgentStepKind.Question or PhoneAgentStepKind.Confirmation => new SolidColorBrush(Color.Parse("#FFC14D")),
        _ => new SolidColorBrush(Color.Parse("#8E95B2")),
    };
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public static class StepKindText
{
    public static readonly FuncValueConverter<PhoneAgentStepKind, string> Label = new(k => k switch
    {
        PhoneAgentStepKind.User => "YOU",
        PhoneAgentStepKind.Thought => "THINKING",
        PhoneAgentStepKind.Action => "ACTION",
        PhoneAgentStepKind.Question => "QUESTION",
        PhoneAgentStepKind.Confirmation => "APPROVAL",
        PhoneAgentStepKind.Done => "DONE",
        PhoneAgentStepKind.Error => "ERROR",
        _ => "INFO",
    });

    public static readonly FuncValueConverter<string?, bool> NotEmpty = new(s => !string.IsNullOrWhiteSpace(s));
}

// =====================================================================
// Phone (ADB, mirroring, apps, audio)
// =====================================================================

public sealed partial class PhoneAppItem : ObservableObject
{
    public PhoneAppItem(PhoneApp app) { App = app; Icon = ImageUtil.FromFile(app.IconPath); }
    public PhoneApp App { get; }
    public string Name => App.Name;
    public string Package => App.Package;
    public Bitmap? Icon { get; }
    public bool HasIcon => Icon is not null;
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();
    /// <summary>Notifications from this app aren't forwarded.</summary>
    public bool IsMuted => !App.Listening;
    public string MuteLabel => IsMuted ? "Unmute app" : "Mute app";
    /// <summary>First search match (Enter opens it).</summary>
    [ObservableProperty] public partial bool IsHighlighted { get; set; }
    [ObservableProperty] public partial bool IsLaunching { get; set; }

    /// <summary>The Apps tab that shows this tile (its context menu acts through it).</summary>
    internal AppsViewModel? Owner { get; set; }
    [RelayCommand] Task Open() => Owner?.LaunchCommand.ExecuteAsync(this) ?? Task.CompletedTask;
    [RelayCommand] Task ToggleMute() => Owner?.ToggleMuteCommand.ExecuteAsync(this) ?? Task.CompletedTask;
}

public partial class PhoneViewModel : ViewModelBase
{
    readonly AppController _app;
    readonly FeatureHost _f;

    public PhoneViewModel(AppController app, FeatureHost features)
    {
        _app = app;
        _f = features;
        Adb.PropertyChanged += (_, _) => Ui.Post(Refresh);
        features.Keeper.PropertyChanged += (_, _) => Ui.Post(Refresh);
        Mirror.PropertyChanged += (_, _) => Ui.Post(Refresh);
        app.Core.Hub.AppsChanged += () => Ui.Post(LoadApps);
        app.Core.Link.ConnectionChanged += (_, _) => Ui.Post(Refresh);
        AutoConnect = features.AdbAutoConnect;
        LoadApps();
        Refresh();
    }

    public AdbService Adb => _f.Adb;
    public AdbPairingService Pairing => _f.Pairing;
    public MirrorService Mirror => _f.Mirror;
    public PhoneAudioBridge Audio => _f.Audio;

    [ObservableProperty] public partial bool ToolsMissing { get; set; }
    [ObservableProperty] public partial string ToolsText { get; set; } = "";
    [ObservableProperty] public partial string AdbStatus { get; set; } = "";
    [ObservableProperty] public partial bool PhoneConnected { get; set; }
    [ObservableProperty] public partial bool AutoConnect { get; set; }
    [ObservableProperty] public partial string AppFilter { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "";
    public ObservableCollection<PhoneAppItem> Apps { get; } = new();
    public bool HasApps => Apps.Count > 0;

    partial void OnAutoConnectChanged(bool value) => _f.AdbAutoConnect = value;
    partial void OnAppFilterChanged(string value) => LoadApps();

    void Refresh()
    {
        var adbPath = Adb.AdbPath;
        var scrcpy = Adb.Locator.ScrcpyPath;
        ToolsMissing = adbPath is null || scrcpy is null;
        ToolsText = ToolsMissing
            ? $"H.I.B's {(adbPath is null ? "adb" : "")}{(adbPath is null && scrcpy is null ? " and " : "")}{(scrcpy is null ? "scrcpy" : "")} "
              + "should be in its tools folder but isn't. Run H.I.B-Setup.exe again to repair it, then press Check again."
            : $"adb: {adbPath}\nscrcpy: {scrcpy}";
        PhoneConnected = _app.Core.Link.IsConnected;
        // SPACE.md §13: quiet — "ADB · connected" / "ADB · reconnecting…", with the last reason, never a toast.
        var keeper = _f.Keeper;
        AdbStatus = Adb.IsConnected ? $"ADB · connected · {(Adb.Mode == AdbConnectionMode.Wired ? Adb.CurrentSerial : $"{Adb.ConnectedIp}:{Adb.Port}")}"
            : keeper.State == AdbKeeperState.Reconnecting ? keeper.Status + (keeper.Detail is { Length: > 0 } why ? " · " + why.Split('\n')[0] : "")
            : Adb.IsConnecting ? "Connecting…"
            : Adb.LastResult is { Length: > 0 } r ? $"Not connected · {r}" : "Not connected";
    }

    void LoadApps()
    {
        var filter = AppFilter.Trim();
        var apps = _app.Core.Hub.Apps.Values
            .Where(a => filter.Length == 0 || a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || a.Package.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.SystemApp).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(200);
        Apps.Clear();
        foreach (var a in apps) Apps.Add(new PhoneAppItem(a));
        OnPropertyChanged(nameof(HasApps));
    }

    [RelayCommand]
    void CheckTools()
    {
        Adb.Locator.Refresh();
        Refresh();
    }

    [RelayCommand]
    async Task Connect()
    {
        if (_app.Core.Link.Device is null) { _f.Setup.Show(); return; }
        Status = "Connecting…";
        var r = await _f.AutoConnectAdbAsync(silent: false, notify: false);
        if (r is { Success: true }) { Status = "ADB connected."; return; }
        // Not a dead end: the setup card shows what the phone still needs (or pairs this PC).
        Status = r is null ? "" : AdbService.Explain(r);
        _f.Setup.Show(Status);
    }

    [RelayCommand] Task Disconnect() => Adb.DisconnectAsync(userInitiated: true);

    /// <summary>"Set up mirroring": Developer options → Wireless debugging → pair, driven by the phone.</summary>
    [RelayCommand] void SetUpMirroring() => _f.Setup.Show();

    [RelayCommand]
    async Task MirrorScreen()
    {
        Status = "Starting mirroring…";
        var s = await _f.Launcher.MirrorAsync();
        Status = s is null ? Problem("Couldn't start mirroring.") : "Mirroring in a separate window.";
    }

    [RelayCommand]
    async Task DesktopMode()
    {
        Status = "Starting desktop mode…";
        var s = await _f.Launcher.DesktopAsync();
        Status = s is null ? Problem("Couldn't start desktop mode.") : "Desktop mode opened.";
    }

    string Problem(string fallback) => _f.Launcher.LastProblem is { Length: > 0 } why ? why : fallback;

    [RelayCommand] void StopMirroring() => Mirror.StopAll();

    [RelayCommand]
    async Task OpenApp(PhoneAppItem item)
    {
        Status = $"Opening {item.Name}…";
        var s = await _f.Launcher.AppAsync(item.Package, item.Name);
        Status = s is null ? Problem($"Couldn't open {item.Name}.") : $"{item.Name} opened in its own window.";
    }

    [RelayCommand] void ToggleAudio() => Audio.Toggle();
}

// =====================================================================
// Find My
// =====================================================================

public partial class FindMyViewModel : ViewModelBase
{
    readonly AppController _app;
    readonly FeatureHost _f;

    public FindMyViewModel(AppController app, FeatureHost f)
    {
        _app = app;
        _f = f;
        FindMy.PropertyChanged += (_, _) => Ui.Post(Refresh);
        FindMy.Relay.PropertyChanged += (_, _) => Ui.Post(Refresh);
        Ring.PropertyChanged += (_, _) => Ui.Post(Refresh);
        app.Core.Link.ConnectionChanged += (_, _) => Ui.Post(Refresh);
        Refresh();
    }

    public FindMyService FindMy => _f.FindMy;
    public HIB.Features.Ring.RingService Ring => _f.Ring;

    [ObservableProperty] public partial string RouteText { get; set; } = "";
    [ObservableProperty] public partial string RelayText { get; set; } = "";
    [ObservableProperty] public partial string LocationText { get; set; } = "No location yet";
    [ObservableProperty] public partial string LocationDetail { get; set; } = "";
    [ObservableProperty] public partial bool HasLocation { get; set; }
    [ObservableProperty] public partial string MessageText { get; set; } = "";
    [ObservableProperty] public partial string RingLabel { get; set; } = "Ring";

    void Refresh()
    {
        RouteText = FindMy.NearbyRoute switch
        {
            FindMyRoute.Nearby => "Phone is nearby (connected to H.I.B)",
            FindMyRoute.Adb => "Phone reachable over ADB",
            FindMyRoute.Internet => "Phone is away — commands go over the internet relay",
            _ => "Phone can't be reached right now",
        };
        var relay = FindMy.Relay;
        RelayText = relay.IsAvailable
            ? $"Internet relay ready{(relay.IsSubscribed ? " · listening" : "")} · {relay.Settings.NormalizedServer}"
            : "Internet relay not set up yet — connect the phone to H.I.B once.";
        if (relay.LastError is { Length: > 0 } err) RelayText += $"\nLast error: {err}";
        RingLabel = Ring.IsRinging ? "Stop ringing" : "Ring";
        if (FindMy.LastLocation is { } loc)
        {
            HasLocation = true;
            LocationText = $"{loc.Lat:0.00000}, {loc.Lon:0.00000}";
            var age = DateTimeOffset.Now - loc.Date.ToLocalTime();
            var ago = age.TotalMinutes < 1 ? "just now" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago" : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago" : loc.Date.ToLocalTime().ToString("g");
            LocationDetail = $"±{loc.AccuracyM:0} m · {ago}{(loc.BatteryPct is { } b ? $" · battery {b}%" : "")}{(loc.NetworkName is { Length: > 0 } n ? $" · {n}" : "")}";
        }
    }

    [RelayCommand] void ToggleRing() => Ring.Toggle();
    [RelayCommand] void Locate() => FindMy.Locate();
    [RelayCommand] void Lock() => FindMy.Lock();
    [RelayCommand] void TestRelay() => FindMy.TestRelay();

    [RelayCommand]
    void ShowMessage()
    {
        var text = MessageText.Trim();
        if (text.Length == 0) return;
        FindMy.ShowMessage(text.Length > 500 ? text[..500] : text);
        MessageText = "";
    }

    [RelayCommand]
    void OpenMap()
    {
        if (FindMy.LastLocation is { } loc)
            PlatformShell.Open(FormattableString.Invariant($"https://www.google.com/maps?q={loc.Lat},{loc.Lon}"));
    }
}

// =====================================================================
// Phone Agent
// =====================================================================

public partial class PhoneAgentViewModel : ViewModelBase
{
    readonly AppController _app;
    readonly FeatureHost _f;

    public PhoneAgentViewModel(AppController app, FeatureHost f)
    {
        _app = app;
        _f = f;
        ProviderIndex = Agent.Settings.Provider == PhoneAgentProvider.Claude ? 1 : 0;
        var choice = Agent.Settings.GeminiModelChoice;
        ModelOptions = PhoneAgentGeminiModel.Presets.Select(p => p.Label).Append("Custom model…").ToArray();
        var idx = Array.FindIndex(PhoneAgentGeminiModel.Presets, p => p.Id == choice);
        ModelIndex = choice == PhoneAgentGeminiModel.CustomTag ? ModelOptions.Length - 1 : Math.Max(0, idx);
        CustomModel = Agent.Settings.GeminiCustomModel;
        Agent.PropertyChanged += (_, e) => Ui.Post(() => OnAgentChanged(e));
        Agent.Steps.CollectionChanged += (_, _) => Ui.Post(() => OnPropertyChanged(nameof(HasSteps)));
        app.Features!.Adb.PropertyChanged += (_, _) => Ui.Post(() => OnPropertyChanged(nameof(AdbReady)));
        Agent.RefreshKeyState();
        UpdateKeyText();
    }

    public PhoneAgentController Agent => _f.Agent;
    public string[] ProviderOptions { get; } = { "Gemini (free tier)", "Claude" };
    public string[] ModelOptions { get; }
    public bool HasSteps => Agent.Steps.Count > 0;
    public bool AdbReady => _f.Adb.IsConnected;
    public bool IsGemini => ProviderIndex == 0;
    public bool IsCustomModel => IsGemini && ModelIndex == ModelOptions.Length - 1;
    public bool HasQuestion => Agent.PendingQuestion is not null;
    public bool HasConfirmation => Agent.PendingConfirmation is not null;
    public string UsageText => Agent.Usage.Free || IsGemini
        ? $"{Agent.ActionCount} actions · {Agent.Usage.Input + Agent.Usage.Output:N0} tokens (free tier)"
        : $"{Agent.ActionCount} actions · ${Agent.Usage.CostUsd:0.000}";

    [ObservableProperty] public partial int ProviderIndex { get; set; }
    [ObservableProperty] public partial int ModelIndex { get; set; }
    [ObservableProperty] public partial string CustomModel { get; set; } = "";
    [ObservableProperty] public partial string ApiKey { get; set; } = "";
    [ObservableProperty] public partial string KeyStatus { get; set; } = "";
    [ObservableProperty] public partial string Answer { get; set; } = "";
    [ObservableProperty] public partial bool ShowSettings { get; set; }

    public string KeyLinkText => IsGemini ? "Get a free key" : "Get a Claude key";

    void OnAgentChanged(PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Agent.PendingQuestion): OnPropertyChanged(nameof(HasQuestion)); break;
            case nameof(Agent.PendingConfirmation): OnPropertyChanged(nameof(HasConfirmation)); break;
            case nameof(Agent.Usage):
            case nameof(Agent.ActionCount): OnPropertyChanged(nameof(UsageText)); break;
            case nameof(Agent.HasApiKey): UpdateKeyText(); break;
        }
    }

    partial void OnProviderIndexChanged(int value)
    {
        Agent.Settings.Provider = value == 1 ? PhoneAgentProvider.Claude : PhoneAgentProvider.Gemini;
        Agent.RefreshKeyState();
        OnPropertyChanged(nameof(IsGemini));
        OnPropertyChanged(nameof(IsCustomModel));
        OnPropertyChanged(nameof(KeyLinkText));
        OnPropertyChanged(nameof(UsageText));
        ApiKey = "";
        UpdateKeyText();
    }

    partial void OnModelIndexChanged(int value)
    {
        if (ModelOptions is null) return;
        Agent.Settings.GeminiModelChoice = value >= 0 && value < PhoneAgentGeminiModel.Presets.Length
            ? PhoneAgentGeminiModel.Presets[value].Id
            : PhoneAgentGeminiModel.CustomTag;
        OnPropertyChanged(nameof(IsCustomModel));
    }

    partial void OnCustomModelChanged(string value) => Agent.Settings.GeminiCustomModel = value;

    void UpdateKeyText()
    {
        var provider = Agent.Settings.Provider;
        KeyStatus = Agent.Keys.HasKey(provider)
            ? $"{provider.KeyName()} saved on this PC (encrypted for your Windows account)."
            : $"No {provider.KeyName()} yet.";
    }

    [RelayCommand] void Run() => Agent.Run();
    [RelayCommand] void Stop() => Agent.Stop();
    [RelayCommand] void Approve() => Agent.ResolveConfirmation(true);
    [RelayCommand] void Deny() => Agent.ResolveConfirmation(false);
    [RelayCommand] void ToggleSettings() => ShowSettings = !ShowSettings;

    [RelayCommand]
    void SendAnswer()
    {
        Agent.AnswerQuestion(Answer.Trim().Length == 0 ? null : Answer.Trim());
        Answer = "";
    }

    [RelayCommand] void SkipQuestion() => Agent.AnswerQuestion(null);

    [RelayCommand]
    void SaveKey()
    {
        var key = ApiKey.Trim();
        if (key.Length == 0) return;
        var provider = Agent.Settings.Provider;
        KeyStatus = Agent.Keys.Save(key, provider) ? "Saved." : "Couldn't save the key.";
        ApiKey = "";
        Agent.RefreshKeyState();
        UpdateKeyText();
    }

    [RelayCommand]
    void RemoveKey()
    {
        Agent.Keys.Delete(Agent.Settings.Provider);
        Agent.RefreshKeyState();
        UpdateKeyText();
    }

    [RelayCommand]
    async Task TestKey()
    {
        var provider = Agent.Settings.Provider;
        var key = ApiKey.Trim().Length > 0 ? ApiKey.Trim() : Agent.Keys.Load(provider);
        if (string.IsNullOrEmpty(key)) { KeyStatus = "Paste a key first."; return; }
        KeyStatus = "Testing…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            if (provider == PhoneAgentProvider.Gemini)
            {
                using var c = new GeminiClient(key, Agent.Settings.GeminiModel, Agent.GeminiQuota, s => Ui.Post(() => KeyStatus = s));
                await c.TestKeyAsync(cts.Token);
            }
            else
            {
                using var c = new ClaudeClient(key);
                await c.TestKeyAsync(cts.Token);
            }
            KeyStatus = "The key works.";
        }
        catch (PhoneAgentApiException ex) { KeyStatus = ex.Kind == PhoneAgentApiErrorKind.InvalidKey ? "That key was rejected." : ex.Message; }
        catch (Exception ex) { KeyStatus = "Test failed: " + ex.Message; }
    }

    [RelayCommand]
    void GetKey() => PlatformShell.Open(IsGemini ? "https://aistudio.google.com/apikey" : "https://console.anthropic.com/settings/keys");
}

// =====================================================================
// Search phone
// =====================================================================

public sealed partial class SearchResultItem : ObservableObject
{
    readonly PhoneSearchClient _client;

    public SearchResultItem(PhoneSearchResult r, PhoneSearchClient client)
    {
        Result = r;
        _client = client;
        Thumbnail = ImageUtil.FromBytes(r.Thumbnail);
    }

    public PhoneSearchResult Result { get; }
    public string Title => Result.Title;
    public string Subtitle => Result.Subtitle;
    public Bitmap? Thumbnail { get; }
    public bool HasThumbnail => Thumbnail is not null;
    public string KindLabel => Result.Kind.ToString();
    public string Detail => string.Join(" · ", new[]
    {
        Result.Size > 0 ? AppController.FormatSize(Result.Size) : null,
        Result.Date?.ToLocalTime().ToString("d MMM yyyy"),
    }.Where(s => s is not null));
    public bool CanDownload => Result.FetchPath is not null && Result.Kind is PhoneSearchKind.Photos or PhoneSearchKind.Files;

    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial bool IsDownloading { get; set; }
    [ObservableProperty] public partial string? SavedPath { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    public bool IsSaved => SavedPath is not null;
    partial void OnSavedPathChanged(string? value) => OnPropertyChanged(nameof(IsSaved));

    [RelayCommand]
    async Task Download()
    {
        if (IsDownloading) return;
        IsDownloading = true;
        Status = "Downloading…";
        var path = await _client.FetchAsync(Result);
        IsDownloading = false;
        SavedPath = path;
        Status = path is null ? (_client.Errors.TryGetValue(Result.Id, out var e) ? e : "Download failed") : "Saved to Downloads\\H.I.B";
    }

    [RelayCommand] void Open() { if (SavedPath is not null) PlatformShell.Open(SavedPath); }
    [RelayCommand] void Reveal() { if (SavedPath is not null) PlatformShell.RevealInFolder(SavedPath); }
}

public partial class SearchPhoneViewModel : ViewModelBase
{
    readonly FeatureHost _f;
    string? _searchId;

    public SearchPhoneViewModel(AppController app, FeatureHost f)
    {
        _f = f;
        f.Search.ProgressChanged += id => Ui.Post(() =>
        {
            var item = Results.FirstOrDefault(r => r.Result.Id == id);
            if (item is not null && f.Search.Progress.TryGetValue(id, out var p)) item.Progress = p * 100;
        });
    }

    public ObservableCollection<SearchResultItem> Results { get; } = new();
    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial bool Photos { get; set; } = true;
    [ObservableProperty] public partial bool Files { get; set; } = true;
    [ObservableProperty] public partial bool Apps { get; set; } = true;
    [ObservableProperty] public partial bool Contacts { get; set; } = true;
    [ObservableProperty] public partial bool IsSearching { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "Search photos, files, apps and contacts on your phone.";

    [RelayCommand]
    void Search()
    {
        var q = Query.Trim();
        if (q.Length == 0) return;
        if (!_f.Search.IsPhoneConnected) { Status = "Connect the phone first."; return; }
        _f.Search.Cancel(_searchId);
        Results.Clear();
        var kinds = new[] { (Photos, PhoneSearchKind.Photos), (Files, PhoneSearchKind.Files), (Apps, PhoneSearchKind.Apps), (Contacts, PhoneSearchKind.Contacts) }
            .Where(k => k.Item1).Select(k => k.Item2).ToList();
        if (kinds.Count == 0) { Status = "Pick at least one kind."; return; }
        IsSearching = true;
        Status = "Searching…";
        string? id = null;
        id = _searchId = _f.Search.Search(q, (results, done) => Ui.Post(() =>
        {
            if (_searchId != id) return;
            foreach (var r in results)
                if (Results.All(x => x.Result.Id != r.Id)) Results.Add(new SearchResultItem(r, _f.Search));
            if (done)
            {
                IsSearching = false;
                Status = Results.Count == 0 ? "Nothing found." : $"{Results.Count} results";
            }
        }), kinds);
        if (id is null) { IsSearching = false; Status = "Couldn't send the search."; }
    }
}
