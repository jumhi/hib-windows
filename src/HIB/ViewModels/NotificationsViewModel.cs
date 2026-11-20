using System;
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
    public string Body => Model.Body;
    public bool HasBody => !string.IsNullOrWhiteSpace(Model.Body);
    public string Time => Model.Received.ToString("HH:mm");
    public Bitmap? Icon { get; }
    public bool HasIcon => Icon is not null;
    public string Initial => string.IsNullOrEmpty(App) ? "?" : App[..1].ToUpperInvariant();
    public ObservableCollection<ToastButton> Buttons { get; } = new();
    public string? ReplyAction { get; }
    public bool CanReply => ReplyAction is not null;
    public bool HasProgress => Model.ProgressMax is > 0 || Model.ProgressIndeterminate;
    public double ProgressValue => Model.ProgressMax is > 0 ? 100.0 * (Model.Progress ?? 0) / Model.ProgressMax.Value : 0;
    public bool IsIndeterminate => Model.ProgressIndeterminate;

    [ObservableProperty] public partial string ReplyText { get; set; } = "";

    [RelayCommand] Task Dismiss() => _owner.DismissAsync(this);

    [RelayCommand]
    async Task SendReply()
    {
        var text = ReplyText.Trim();
        if (text.Length == 0 || ReplyAction is null) return;
        ReplyText = "";
        await _owner.RunAction(this, ReplyAction, text);
    }

    [RelayCommand] Task MuteApp() => _owner.MuteAppAsync(this);
}

public partial class NotificationsViewModel : ViewModelBase
{
    readonly AppController _app;

    public NotificationsViewModel(AppController app)
    {
        _app = app;
        var hub = app.Core.Hub;
        hub.NotificationPosted += n => Ui.Post(() => Upsert(n));
        hub.NotificationRemoved += id => Ui.Post(() => Remove(id));
        foreach (var n in hub.Notifications) Upsert(n);
    }

    public ObservableCollection<NotificationItemViewModel> Items { get; } = new();
    public bool IsEmpty => Items.Count == 0;
    [ObservableProperty] public partial string Status { get; set; } = "";

    public event Action<int>? CountChanged;
    int _unseen;
    bool _visible;

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
        OnPropertyChanged(nameof(IsEmpty));
    }

    void Remove(string id)
    {
        var item = Items.FirstOrDefault(i => i.Id == id);
        if (item is not null) Items.Remove(item);
        OnPropertyChanged(nameof(IsEmpty));
    }

    internal async Task DismissAsync(NotificationItemViewModel item)
    {
        Remove(item.Id);
        await _app.Core.Hub.DismissNotificationAsync(item.Id);
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

    [RelayCommand]
    async Task ClearAll()
    {
        var ids = Items.Select(i => i.Id).ToList();
        Items.Clear();
        OnPropertyChanged(nameof(IsEmpty));
        foreach (var id in ids) await _app.Core.Hub.DismissNotificationAsync(id);
    }

    [RelayCommand]
    void ClearLocal()
    {
        _app.Core.Hub.ClearNotificationsLocal();
        Items.Clear();
        OnPropertyChanged(nameof(IsEmpty));
    }
}
