using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using HIB.Core.Phone;
using HIB.Core.Platform;

namespace HIB.Services;

public static class ImageUtil
{
    public static Bitmap? FromBytes(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch { return null; }
    }

    public static Bitmap? FromFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try { return new Bitmap(path); } catch { return null; }
    }

    public static Bitmap? AppIcon()
    {
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://HIB/Assets/hib-256.png"));
            return new Bitmap(s);
        }
        catch { return null; }
    }

    public static WindowIcon? WindowIcon()
    {
        try { return new WindowIcon(AssetLoader.Open(new Uri("avares://HIB/Assets/hib.ico"))); }
        catch { return null; }
    }
}

public static class Ui
{
    public static void Post(Action a) => Dispatcher.UIThread.Post(a);
}

/// <summary>Desktop clipboard via Avalonia (all calls hop to the UI thread).</summary>
public sealed class AvaloniaClipboardBridge : IClipboardBridge
{
    readonly Func<TopLevel?> _topLevel;

    public AvaloniaClipboardBridge(Func<TopLevel?> topLevel) => _topLevel = topLevel;

    IClipboard? Clipboard => _topLevel()?.Clipboard;

    public uint? ChangeCount => NativeClipboard.SequenceNumber;

    public Task<string?> GetTextAsync() => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        var cb = Clipboard;
        if (cb is null) return null;
        try { return await cb.TryGetTextAsync(); } catch { return null; }
    });

    public Task<byte[]?> GetImagePngAsync() => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        var cb = Clipboard;
        if (cb is null) return null;
        try
        {
            using var bmp = await cb.TryGetBitmapAsync();
            if (bmp is null) return null;
            using var ms = new MemoryStream();
#pragma warning disable CS0618
            bmp.Save(ms);
#pragma warning restore CS0618
            return ms.ToArray();
        }
        catch { return null; }
    });

    public Task SetTextAsync(string text) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        var cb = Clipboard;
        if (cb is not null) await cb.SetTextAsync(text);
    });

    public Task SetImageAsync(byte[] imageBytes) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        var cb = Clipboard;
        var bmp = ImageUtil.FromBytes(imageBytes);
        if (cb is not null && bmp is not null) await cb.SetBitmapAsync(bmp);
    });
}

/// <summary>Global cursor position (physical pixels) — Windows only; null elsewhere.</summary>
public static partial class GlobalCursor
{
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT p);

    public static PixelPoint? Position
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return null;
            try { return GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : null; }
            catch { return null; }
        }
    }
}
