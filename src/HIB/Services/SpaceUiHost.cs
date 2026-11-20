// H.I.B Space on the desktop: the Flick shelf, Explorer "Send to" shortcuts, the --send-to pipe, and
// "Send to…" feedback. Created once by AppController for the real desktop app (not in headless tests).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using HIB.Core.Platform;
using HIB.Core.Space;
using HIB.Features.Mirror;
using HIB.Views.Shelf;

namespace HIB.Services;

/// <summary>Adapts HIB.Features' scrcpy <see cref="PhoneAudioBridge"/> to the Space engine's player (SPACE.md §3.5).</summary>
public sealed class PhoneAudioPlayer : IPhoneAudioPlayer
{
    readonly PhoneAudioBridge _bridge;

    public PhoneAudioPlayer(PhoneAudioBridge bridge)
    {
        _bridge = bridge;
        bridge.PropertyChanged += OnChanged;
    }

    public bool IsActive => _bridge.IsActive;
    public bool IsStarting => _bridge.IsStarting;
    public bool CanStart => _bridge.CanStart;
    public string? LastError => _bridge.LastError;
    public event Action? StateChanged;

    // The bridge raises PropertyChanged for bound UI, so drive it from the UI thread.
    public void Start() => Dispatcher.UIThread.Post(_bridge.Start);
    public void Stop() => Dispatcher.UIThread.Post(_bridge.Stop);

    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PhoneAudioBridge.IsActive) or nameof(PhoneAudioBridge.IsStarting)) StateChanged?.Invoke();
    }
}

public sealed class SpaceUiHost : IDisposable
{
    readonly AppController _app;
    readonly CancellationTokenSource _cts = new();
    readonly DispatcherTimer _shortcutDebounce;
    FlickShelfWindow? _shelf;

    public SpaceUiHost(AppController app)
    {
        _app = app;
        _shortcutDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _shortcutDebounce.Tick += (_, _) =>
        {
            _shortcutDebounce.Stop();
            SyncShortcuts();
        };
        // After the main window exists (AppController is built before it).
        Dispatcher.UIThread.Post(Start, DispatcherPriority.Background);
    }

    public FlickShelfWindow? Shelf => _shelf;

    void Start()
    {
        var space = _app.Core.Space;
        try
        {
            _shelf = new FlickShelfWindow(_app, SendAsync);
            _shelf.Start();
            // Dev aid: HIB_SHELF_PEEK=1 opens the shelf at start (for screenshots without a real drag).
            if (Environment.GetEnvironmentVariable("HIB_SHELF_PEEK") is "1") DispatcherTimer.RunOnce(() => _shelf.Expand(), TimeSpan.FromSeconds(3));
        }
        catch (Exception ex) { _app.Core.AddLog("Flick shelf failed: " + ex.Message); }

        _ = SendToPipe.ServeAsync(req => Dispatcher.UIThread.Post(() => _ = HandleAsync(req)), _cts.Token);
        if (SendToRequest.Pending is { } pending)
        {
            SendToRequest.Pending = null;
            _ = HandleAsync(pending);
        }

        if (SendToShortcuts.IsSupported)
        {
            space.Changed += () => Dispatcher.UIThread.Post(() => { _shortcutDebounce.Stop(); _shortcutDebounce.Start(); });
            _shortcutDebounce.Start();
        }
    }

    void SyncShortcuts()
    {
        var devices = _app.Core.Space.Devices.Select(d => (d.Id, d.Name)).ToList();
        var exe = PlatformShell.ExecutablePath;
        _ = Task.Run(() => SendToShortcuts.Sync(devices, exe));
    }

    /// <summary>A --send-to request: wait (up to 3 min) until the device is reachable, then send.</summary>
    async Task HandleAsync(SendToRequest req)
    {
        var files = req.Files.Where(f => File.Exists(f) || Directory.Exists(f)).ToList();
        if (files.Count == 0) return;
        var space = _app.Core.Space;
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        var waitingShown = false;
        while (space.Find(req.DeviceId) is not { CanReceive: true })
        {
            if (DateTime.UtcNow > until)
            {
                Toast("Send to " + (space.Find(req.DeviceId)?.Name ?? req.DeviceId), $"{space.Find(req.DeviceId)?.Name ?? req.DeviceId} isn't reachable", "Turn it on and try again.", 10, null);
                _app.Toasts.Close("sendto-wait");
                return;
            }
            if (!waitingShown)
            {
                waitingShown = true;
                _app.Toasts.Show(new ToastViewModel
                {
                    Key = "sendto-wait",
                    Persistent = true,
                    Caption = "Send to " + (space.Find(req.DeviceId)?.Name ?? req.DeviceId),
                    Title = files.Count == 1 ? $"Waiting to send {Path.GetFileName(files[0])}" : $"Waiting to send {files.Count} files",
                    Body = "It goes as soon as the device is linked.",
                    Icon = ImageUtil.AppIcon(),
                });
            }
            await Task.Delay(1000);
        }
        _app.Toasts.Close("sendto-wait");
        await SendAsync(req.DeviceId, files);
    }

    /// <summary>
    /// "Send to…" with feedback: a progress toast while it goes, then where it landed (or why not).
    /// Uses <see cref="SpaceDirectory.SendFilesAsync"/>: direct link, through the phone, or the phone itself.
    /// </summary>
    public async Task SendAsync(string deviceId, IReadOnlyList<string> paths)
    {
        var space = _app.Core.Space;
        var target = space.Find(deviceId);
        var name = target?.Name ?? deviceId;
        var key = "space-send:" + Guid.NewGuid().ToString("N");
        var label = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} files";
        _app.Toasts.Show(new ToastViewModel
        {
            Key = key,
            Persistent = true,
            Caption = "Send to " + name,
            Title = "Sending " + label,
            Body = target?.Route switch
            {
                SpaceRoute.Direct => "Direct",
                SpaceRoute.ViaPhone => "Through phone",
                _ => "",
            },
            Icon = ImageUtil.AppIcon(),
        });
        IReadOnlyList<HIB.Core.Phone.SendResult> results;
        try { results = await Task.Run(() => space.SendFilesAsync(deviceId, paths)); }
        catch (Exception ex) { results = new[] { new HIB.Core.Phone.SendResult("", label, false, ex.Message) }; }
        _app.Toasts.Close(key);
        var ok = results.Count(r => r.Ok);
        var failed = results.Where(r => !r.Ok).ToList();
        if (failed.Count == 0)
        {
            Toast("Send to " + name, ok == 1 ? "Sent " + results[0].Name : $"Sent {ok} files",
                target?.IsPhone == true ? "Saved in Downloads/H.I.B on the phone" : $"It landed on {name}", 5, null);
        }
        else
        {
            Toast("Send to " + name, ok == 0 ? (failed.Count == 1 ? "Couldn't send " + failed[0].Name : $"Couldn't send {failed.Count} files") : $"Sent {ok}, {failed.Count} failed",
                failed[0].Error ?? "", 9, null);
        }
    }

    void Toast(string caption, string title, string body, int seconds, Action? onClick)
        => _app.Toasts.Show(new ToastViewModel { Caption = caption, Title = title, Body = body, Icon = ImageUtil.AppIcon(), Seconds = seconds, OnClick = onClick });

    public void Dispose()
    {
        _cts.Cancel();
        _shortcutDebounce.Stop();
        try { _shelf?.Close(); } catch { }
    }
}
