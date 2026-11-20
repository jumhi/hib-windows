using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using HIB.Core;
using HIB.Core.Phone;
using HIB.Core.Platform;

namespace HIB.Services;

/// <summary>
/// The glue between the UI-free core and the desktop UI: toasts, call popups, flick landing,
/// window/tray handling. One instance, created in App.
/// </summary>
public sealed class AppController
{
    readonly IClassicDesktopStyleApplicationLifetime? _lifetime;
    DateTime _connectedAt = DateTime.MinValue;
    DateTime _lastCallSignal = DateTime.MinValue;
    DispatcherTimer? _callWatchdog;

    public AppController(HibCore core, IClassicDesktopStyleApplicationLifetime? lifetime)
    {
        Core = core;
        _lifetime = lifetime;
        Toasts = new ToastManager(() => MainWindow);
        Flick = new FlickPresenter(core.FlickPicker, () => MainWindow);

        core.Link.ConnectionChanged += (_, connected) =>
        {
            if (connected) _connectedAt = DateTime.UtcNow;
            else Ui.Post(() => Toasts.Close("call"));
        };
        core.Hub.NotificationPosted += n => Ui.Post(() => OnNotification(n));
        core.Hub.NotificationRemoved += id => Ui.Post(() => Toasts.Close("n:" + id));
        core.Hub.CallChanged += c => Ui.Post(() => OnCall(c));
        core.Hub.CallProgress += _ => _lastCallSignal = DateTime.UtcNow;
        core.Hub.FlickStarted += f => Ui.Post(() => OnFlick(f));
        core.Files.Received += f => Ui.Post(() => OnFileReceived(f));
        core.Files.Failed += (id, reason) => Ui.Post(() => OnFileFailed(id, reason));
    }

    public HibCore Core { get; }

    /// <summary>ADB / mirroring / Find My / Phone Agent services (null in core-only tests).</summary>
    public FeatureHost? Features { get; set; }
    public ToastManager Toasts { get; }
    public FlickPresenter Flick { get; }
    public Window? MainWindow => WindowOverride ?? _lifetime?.MainWindow;

    /// <summary>Used when there is no desktop lifetime (headless UI tests).</summary>
    public Window? WindowOverride { get; set; }

    /// <summary>Set by the main view model so toasts can jump to a page.</summary>
    public Action<string>? Navigate { get; set; }

    // ---------------- window / app ----------------

    public void ShowMainWindow(string? page = null)
    {
        var w = MainWindow;
        if (w is null) return;
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        if (page is not null) Navigate?.Invoke(page);
    }

    bool _quitting;

    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        try { Features?.Dispose(); } catch { }
        try { Core.Dispose(); } catch { }
        _lifetime?.Shutdown();
    }

    // ---------------- notifications ----------------

    void OnNotification(PhoneNotification n)
    {
        var s = Core.Settings;
        if (!s.NotificationToasts || n.Ongoing) return;
        if (n.IsSilent && !s.SilentToasts) return;
        if (DateTime.UtcNow - _connectedAt < TimeSpan.FromSeconds(4)) return; // backlog burst on connect

        var vm = new ToastViewModel
        {
            Key = "n:" + n.Id,
            Caption = string.IsNullOrWhiteSpace(n.App) ? "Phone" : n.App,
            Title = string.IsNullOrWhiteSpace(n.Title) ? n.App : n.Title,
            Body = n.Body,
            Icon = ImageUtil.FromFile(Core.Hub.IconFor(n.Package)),
            Seconds = s.ToastSeconds,
            OnClick = () => ShowMainWindow("notifications"),
            OnDismiss = () => _ = Core.Hub.DismissNotificationAsync(n.Id),
            OnReply = text => _ = Core.Hub.SendNotificationActionAsync(n.Id, n.Actions.FirstOrDefault(a => a.IsReply)?.Name ?? "Reply", text),
        };
        var reply = n.Actions.FirstOrDefault(a => a.IsReply);
        if (reply is not null)
        {
            vm.HasReply = true;
            vm.ReplyPlaceholder = reply.Name + "…";
        }
        foreach (var a in n.Actions.Where(a => !a.IsReply).Take(3))
            vm.Buttons.Add(new ToastButton(a.Name, () => { _ = Core.Hub.SendNotificationActionAsync(n.Id, a.Name); vm.Close(); }));
        Toasts.Show(vm);
    }

    // ---------------- calls ----------------

    void OnCall(CallInfo c)
    {
        _lastCallSignal = DateTime.UtcNow;
        if (c.IsOver)
        {
            Toasts.Close("call");
            _callWatchdog?.Stop();
            if (c.State == "missed")
                Toasts.Show(new ToastViewModel
                {
                    Caption = "Missed call",
                    Title = c.DisplayName,
                    Body = c.DisplayName != c.DisplayNumber ? c.DisplayNumber : "",
                    Icon = ImageUtil.FromBytes(c.ContactPhoto) ?? ImageUtil.AppIcon(),
                    Seconds = 15,
                });
            return;
        }
        if (!Core.Settings.CallPopups) return;

        var vm = new ToastViewModel
        {
            Key = "call",
            Persistent = true,
            IsCall = true,
            Caption = c.IsRinging ? (c.IsIncoming ? "Incoming call" : "Calling…") : "On a call",
            Title = c.DisplayName,
            Body = c.DisplayName != c.DisplayNumber ? c.DisplayNumber : "",
            Icon = ImageUtil.AppIcon(),
            Image = ImageUtil.FromBytes(c.ContactPhoto),
        };
        if (c.IsRinging && c.IsIncoming)
        {
            vm.Buttons.Add(new ToastButton("Answer", () => { _ = Core.Hub.CallControlAsync("accept"); }, "primary"));
            vm.Buttons.Add(new ToastButton("Decline", () => { _ = Core.Hub.CallControlAsync("decline"); vm.Close(); }, "danger"));
        }
        else
        {
            vm.Buttons.Add(new ToastButton("End call", () => { _ = Core.Hub.CallControlAsync("end"); vm.Close(); }, "danger"));
        }
        Toasts.Show(vm);
        StartCallWatchdog();
    }

    void StartCallWatchdog()
    {
        _callWatchdog?.Stop();
        _callWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _callWatchdog.Tick += (_, _) =>
        {
            // call_progress arrives every 5 s while ringing/off-hook; give up after 20 s of silence.
            if (DateTime.UtcNow - _lastCallSignal > TimeSpan.FromSeconds(20))
            {
                Toasts.Close("call");
                _callWatchdog?.Stop();
            }
        };
        _callWatchdog.Start();
    }

    // ---------------- flick + files ----------------

    void OnFlick(FlickInfo f)
    {
        var screen = Flick.PickFor(f);
        switch (f.Kind)
        {
            case "link":
                var url = f.Text?.Trim();
                var safe = PlatformShell.IsSafeUrl(url);
                if (safe && Core.Settings.AutoOpenLinks) PlatformShell.Open(url!);
                var linkToast = new ToastViewModel
                {
                    Caption = "Flick",
                    Title = safe && Core.Settings.AutoOpenLinks ? "Opened link" : "Link from phone",
                    Body = url ?? "",
                    Icon = ImageUtil.AppIcon(),
                    Image = ImageUtil.FromBytes(f.Thumbnail),
                };
                if (safe && !Core.Settings.AutoOpenLinks)
                    linkToast.Buttons.Add(new ToastButton("Open", () => PlatformShell.Open(url!), "primary"));
                linkToast.Buttons.Add(new ToastButton("Copy", () => _ = CopyFromPhoneAsync(url ?? "")));
                Toasts.Show(linkToast, screen);
                break;

            case "text":
                var text = f.Text ?? "";
                _ = CopyFromPhoneAsync(text);
                Toasts.Show(new ToastViewModel
                {
                    Caption = "Flick",
                    Title = "Text copied",
                    Body = text.Length > 300 ? text[..300] + "…" : text,
                    Icon = ImageUtil.AppIcon(),
                }, screen);
                break;

            default:
                Toasts.Show(new ToastViewModel
                {
                    Key = "flick:" + f.Id,
                    Caption = "Flick",
                    Title = "Receiving " + (string.IsNullOrWhiteSpace(f.Name) ? f.Kind : f.Name),
                    Body = f.Size > 0 ? FormatSize(f.Size) : "",
                    Icon = ImageUtil.AppIcon(),
                    Image = ImageUtil.FromBytes(f.Thumbnail),
                    Seconds = 30,
                }, screen);
                break;
        }
    }

    async Task CopyFromPhoneAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        // Copy without bouncing it back to the phone.
        Core.Clipboard?.SuppressNextLocalChange(text);
        var top = MainWindow;
        if (top?.Clipboard is { } cb) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, text);
    }

    void OnFileReceived(ReceivedFile f)
    {
        Toasts.Close("flick:" + f.FlickId);
        var isImage = f.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                      || f.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                      || f.Name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase);
        var vm = new ToastViewModel
        {
            Caption = f.FlickId is not null ? "Flick" : "File received",
            Title = f.Name,
            Body = $"{FormatSize(f.Size)} · saved to {Path.GetFileName(Path.GetDirectoryName(f.Path))}",
            Icon = ImageUtil.AppIcon(),
            Image = isImage && f.Size < 30_000_000 ? ImageUtil.FromFile(f.Path) : null,
            Seconds = 10,
            OnClick = () => PlatformShell.Open(f.Path),
        };
        vm.Buttons.Add(new ToastButton("Open", () => { PlatformShell.Open(f.Path); vm.Close(); }, "primary"));
        vm.Buttons.Add(new ToastButton("Show in folder", () => { PlatformShell.RevealInFolder(f.Path); vm.Close(); }));
        Toasts.Show(vm, Flick.LandingScreen(f.FlickId));
    }

    void OnFileFailed(string id, string reason)
    {
        Toasts.Close("flick:" + id);
        if (reason == "Disconnected") return;
        Toasts.Show(new ToastViewModel { Caption = "File transfer", Title = "Transfer failed", Body = reason, Icon = ImageUtil.AppIcon() });
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {units[i]}";
    }
}
