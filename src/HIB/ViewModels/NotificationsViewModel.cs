using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HIB.Core.Phone;
using HIB.Services;

namespace HIB.ViewModels;

public sealed partial class NotificationItemViewModel : ObservableObject
{
    readonly NotificationsViewModel _owner;

    public NotificationItemViewModel(NotificationsViewModel owner, PhoneNotification n, Bitmap? icon)
    {
        _owner = owner;
        Model = n;
        Icon = icon;
        foreach (var a in n.Actions.Where(a => !a.IsReply))
            Buttons.Add(new ToastButton(a.Name, () => _ = owner.RunAction(this, a.Name)));
        ReplyAction = n.Actions.FirstOrDefault(a => a.IsReply)?.Name;
    }

    public PhoneNotification Model { get; set; }
    public string Id => Model.Id;
    public string App => string.IsNullOrWhiteSpace(Model.App) ? Model.Package : Model.App;
    public string Title => Model.Title;
    /// <summary>"WhatsApp - Ali", like the Mac card's headline.</summary>
    public string Headline => string.IsNullOrWhiteSpace(Model.Title) ? App : $"{App} - {Model.Title}";
    public string Body => Model.Body;
    public bool HasBody => !string.IsNullOrWhiteSpace(Model.Body);
    public string Time => Model.Received.ToString("HH:mm");
    public bool IsSilent => Model.IsSilent;
    public Bitmap? Icon { get; }
    public bool HasIcon => Icon is not null;
    public string Initial => string.IsNullOrEmpty(App) ? "?" : App[..1].ToUpperInvariant();
    public ObservableCollection<ToastButton> Buttons { get; } = new();
    public bool HasButtons => Buttons.Count > 0;
    public string? ReplyAction { get; }
    public bool CanReply => ReplyAction is not null;
    public bool HasActions => HasButtons || CanReply;
    public string ReplyPlaceholder => (ReplyAction ?? "Reply") + "…";
    public bool HasProgress => Model.ProgressMax is > 0 || Model.ProgressIndeterminate;
    public double ProgressValue => Model.ProgressMax is > 0 ? 100.0 * (Model.Progress ?? 0) / Model.ProgressMax.Value : 0;
    public bool IsIndeterminate => Model.ProgressIndeterminate;

    [ObservableProperty] public partial string ReplyText { get; set; } = "";
    /// <summary>The reply field is open (the Mac shows a reply button first).</summary>
    [ObservableProperty] public partial bool IsReplying { get; set; }

    [RelayCommand] Task Dismiss() => _owner.DismissAsync(this);
    [RelayCommand] void Hide() => _owner.Hide(this);
    [RelayCommand] void StartReply() => IsReplying = true;
    [RelayCommand] void Open() => _owner.Open(this);

    [RelayCommand]
    async Task SendReply()
    {
        var text = ReplyText.Trim();
        if (text.Length == 0 || ReplyAction is null) return;
        ReplyText = "";
        IsReplying = false;
        await _owner.RunAction(this, ReplyAction, text);
    }

    [RelayCommand] Task MuteApp() => _owner.MuteAppAsync(this);
}

/// <summary>One app's notifications in the stacked view: the newest shows, "Show N more" opens the rest.</summary>
public sealed partial class NotificationGroupViewModel : ObservableObject
{
    readonly List<NotificationItemViewModel> _all;

    public NotificationGroupViewModel(string key, string title, List<NotificationItemViewModel> items, bool expanded)
    {
        Key = key;
        Title = title;
        _all = items;
        IsExpanded = expanded;
        Fill();
    }

    public string Key { get; }
    public string Title { get; }
    public ObservableCollection<NotificationItemViewModel> Visible { get; } = new();
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    public bool HasMore => _all.Count > 1;
    public string MoreText => IsExpanded ? "Show less" : $"Show {_all.Count - 1} more";
    public string MoreIcon => IsExpanded ? Icons.ChevronUp : Icons.ChevronDown;
    public event Action<NotificationGroupViewModel>? Toggled;

    partial void OnIsExpandedChanged(bool value)
    {
        Fill();
        OnPropertyChanged(nameof(MoreText));
        OnPropertyChanged(nameof(MoreIcon));
    }

    void Fill()
    {
        Visible.Clear();
        foreach (var n in IsExpanded ? _all : _all.Take(1)) Visible.Add(n);
    }

    [RelayCommand]
    void Toggle()
    {
        IsExpanded = !IsExpanded;
        Toggled?.Invoke(this);
    }
}

public partial class NotificationsViewModel : ViewModelBase
{
    readonly AppController _app;
    readonly HashSet<string> _expanded = new();

    public NotificationsViewModel(AppController app)
    {
        _app = app;
        var hub = app.Core.Hub;
        hub.NotificationPosted += n => Ui.Post(() => Upsert(n));
        hub.NotificationRemoved += id => Ui.Post(() => Remove(id));
        foreach (var n in hub.Notifications) Upsert(n);
    }

    /// <summary>Newest first.</summary>
    public ObservableCollection<NotificationItemViewModel> Items { get; } = new();
    /// <summary>Grouped by app (the Mac's "notification stacks").</summary>
    public ObservableCollection<NotificationGroupViewModel> Groups { get; } = new();
    public bool IsEmpty => Items.Count == 0;
    public int Count => Items.Count;
    public string CountText => Items.Count == 1 ? "1 notification" : $"{Items.Count} notifications";
    [ObservableProperty] public partial string Status { get; set; } = "";
    /// <summary>Stacked by app (default) or one flat list.</summary>
    [ObservableProperty] public partial bool IsStacked { get; set; } = true;
    public bool IsFlat => !IsStacked;
    public string StackIcon => IsStacked ? Icons.Layers : Icons.Rows3;
    public string StackTip => IsStacked ? "Show every notification" : "Group by app";

    public event Action<int>? CountChanged;
    int _unseen;
    bool _visible;

    partial void OnIsStackedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsFlat));
        OnPropertyChanged(nameof(StackIcon));
        OnPropertyChanged(nameof(StackTip));
    }

    public void MarkSeen()
    {
        _visible = true;
        _unseen = 0;
        CountChanged?.Invoke(0);
    }

    public void MarkHidden() => _visible = false;

    void Upsert(PhoneNotification n)
    {
        var icon = ImageUtil.FromFile(_app.Core.Hub.IconFor(n.Package));
        var vm = new NotificationItemViewModel(this, n, icon);
        var existing = Items.FirstOrDefault(i => i.Id == n.Id);
        if (existing is not null)
        {
            var idx = Items.IndexOf(existing);
            Items[idx] = vm;
        }
        else
        {
            Items.Insert(0, vm);
            if (Items.Count > 300) Items.RemoveAt(Items.Count - 1);
            if (!_visible) CountChanged?.Invoke(++_unseen);
        }
        Changed();
    }

    void Remove(string id)
    {
        var item = Items.FirstOrDefault(i => i.Id == id);
        if (item is not null) Items.Remove(item);
        Changed();
    }

    void Changed()
    {
        RebuildGroups();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CountText));
    }

    void RebuildGroups()
    {
        Groups.Clear();
        foreach (var g in Items.GroupBy(i => string.IsNullOrEmpty(i.Model.Package) ? i.App : i.Model.Package))
        {
            var items = g.ToList();
            var group = new NotificationGroupViewModel(g.Key, items[0].App, items, _expanded.Contains(g.Key));
            group.Toggled += t => { if (t.IsExpanded) _expanded.Add(t.Key); else _expanded.Remove(t.Key); };
            Groups.Add(group);
        }
    }

    internal async Task DismissAsync(NotificationItemViewModel item)
    {
        Remove(item.Id);
        await _app.Core.Hub.DismissNotificationAsync(item.Id);
    }

    /// <summary>Hides it here only; it stays on the phone.</summary>
    internal void Hide(NotificationItemViewModel item) => Remove(item.Id);

    internal void Open(NotificationItemViewModel item)
    {
        if (item.CanReply) item.IsReplying = true;
    }

    internal async Task RunAction(NotificationItemViewModel item, string action, string? text = null)
    {
        var ok = await _app.Core.Hub.SendNotificationActionAsync(item.Id, action, text);
        Status = ok ? $"Sent “{action}” to {item.App}" : "Phone is not connected";
    }

    internal async Task MuteAppAsync(NotificationItemViewModel item)
    {
        if (string.IsNullOrEmpty(item.Model.Package)) return;
        var ok = await _app.Core.Hub.ToggleAppNotificationsAsync(item.Model.Package, false);
        Status = ok ? $"Stopped forwarding notifications from {item.App}" : "Phone is not connected";
    }

    [RelayCommand] void ToggleStacks() => IsStacked = !IsStacked;

    /// <summary>Clear all: dismisses them on the phone too (the Mac's "Clear" button).</summary>
    [RelayCommand]
    async Task ClearAll()
    {
        var ids = Items.Select(i => i.Id).ToList();
        Items.Clear();
        Changed();
        foreach (var id in ids) await _app.Core.Hub.DismissNotificationAsync(id);
    }

    [RelayCommand]
    void ClearLocal()
    {
        _app.Core.Hub.ClearNotificationsLocal();
        Items.Clear();
        Changed();
    }
}
