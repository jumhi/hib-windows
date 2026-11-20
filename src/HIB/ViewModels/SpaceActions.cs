using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using HIB.Core.Space;
using HIB.Services;

namespace HIB.ViewModels;

/// <summary>
/// "Send to…" actions shared by the phone column and the Devices tab. Everything goes through
/// <see cref="SpaceDirectory"/> (HibCore.Space), which picks the route (phone link, direct, through phone).
/// </summary>
public static class SpaceActions
{
    /// <summary>Opens the file picker. Returns local paths (empty when cancelled).</summary>
    public static async Task<IReadOnlyList<string>> PickFilesAsync(AppController app, string title)
    {
        var storage = app.MainWindow?.StorageProvider;
        if (storage is null) return Array.Empty<string>();
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = true });
        return files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
    }

    /// <summary>Sends files to a device with a progress toast and a result toast. Returns how many arrived.</summary>
    public static async Task<int> SendFilesAsync(AppController app, SpaceDevice device, IReadOnlyList<string> paths)
    {
        var files = paths.Where(File.Exists).ToList();
        var folders = paths.Count - files.Count;
        if (files.Count == 0)
        {
            app.Toasts.Show(new ToastViewModel { Caption = "Send to " + device.Name, Title = folders > 0 ? "Folders can't be sent" : "Nothing to send", Icon = ImageUtil.AppIcon() });
            return 0;
        }
        var key = "space-send:" + Guid.NewGuid().ToString("N");
        var what = files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} files";
        app.Toasts.Show(new ToastViewModel
        {
            Key = key, Persistent = true, Caption = "Send to " + device.Name, Title = "Sending " + what,
            Body = AppController.FormatSize(files.Sum(f => new FileInfo(f).Length)) + (device.RouteText is { Length: > 0 } r ? " · " + r : ""),
            Icon = ImageUtil.AppIcon(),
        });
        IReadOnlyList<HIB.Core.Phone.SendResult> results;
        try { results = await app.Core.Space.SendFilesAsync(device.Id, files); }
        catch (Exception ex) { results = files.Select(f => new HIB.Core.Phone.SendResult("", Path.GetFileName(f), false, ex.Message)).ToList(); }
        var ok = results.Count(r => r.Ok);
        var failed = results.FirstOrDefault(r => !r.Ok);
        Ui.Post(() =>
        {
            app.Toasts.Close(key);
            app.Toasts.Show(new ToastViewModel
            {
                Caption = "Send to " + device.Name,
                Title = ok == files.Count ? $"Sent {what}" : ok == 0 ? $"Couldn't send {what}" : $"Sent {ok} of {files.Count} files",
                Body = failed is not null ? failed.Error ?? "" : device.IsPhone ? "Saved in Downloads/H.I.B on the phone" : $"It landed on {device.Name}",
                Icon = ImageUtil.AppIcon(),
                Seconds = failed is null ? 5 : 8,
            });
        });
        return ok;
    }

    /// <summary>
    /// Sends what's on this PC's clipboard: files are sent as files, text (or a link) as a text flick,
    /// an image as a picture. Returns a short status line.
    /// </summary>
    public static async Task<string> SendClipboardAsync(AppController app, SpaceDevice device)
    {
        var cb = app.MainWindow?.Clipboard;
        if (cb is null) return "The clipboard isn't available.";
        if (!device.CanReceive) return $"{device.Name} isn't reachable right now.";

        try
        {
            var items = await cb.TryGetFilesAsync();
            var paths = items?.Select(i => i.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)).Select(p => p!).ToList();
            if (paths is { Count: > 0 })
            {
                var n = await SendFilesAsync(app, device, paths);
                return n > 0 ? $"Sent {n} file{(n == 1 ? "" : "s")} from the clipboard" : "Couldn't send the copied files";
            }
        }
        catch { /* not files */ }

        string? text = null;
        try { text = await cb.TryGetTextAsync(); } catch { }
        if (!string.IsNullOrEmpty(text))
        {
            var ok = await app.Core.Space.SendTextAsync(device.Id, text);
            // The phone also takes text straight onto its clipboard (works with any phone version).
            if (!ok && device.IsPhone) ok = await app.Core.Hub.SendClipboardTextAsync(text);
            return ok ? $"Clipboard sent to {device.Name}" : $"Couldn't send the clipboard to {device.Name}";
        }

        try
        {
            using var bmp = await cb.TryGetBitmapAsync();
            if (bmp is not null)
            {
                using var ms = new MemoryStream();
                bmp.Save(ms);
                var png = ms.ToArray();
                if (device.IsPhone)
                    return await app.Core.Hub.SendClipboardImageAsync(png) ? "Picture sent to the phone's clipboard" : "Couldn't send the picture";
                var tmp = Path.Combine(Path.GetTempPath(), $"Clipboard {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
                await File.WriteAllBytesAsync(tmp, png);
                return await SendFilesAsync(app, device, new[] { tmp }) > 0 ? $"Picture sent to {device.Name}" : "Couldn't send the picture";
            }
        }
        catch { }
        return "The clipboard is empty.";
    }

    /// <summary>The phone as a Space device (falls back to a stand-in while the directory has no phone).</summary>
    public static SpaceDevice Phone(AppController app)
    {
        var space = app.Core.Space;
        return space.Devices.FirstOrDefault(d => d.IsPhone)
               ?? new SpaceDevice("phone", app.Core.Link.Device?.Name ?? "Phone", SpaceDeviceKind.Phone, "Android phone",
                   app.Core.Link.IsConnected, false, app.Core.Link.IsConnected ? SpaceRoute.PhoneLink : SpaceRoute.Unavailable, null, false);
    }
}
