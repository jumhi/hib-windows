using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HIB.Views;

namespace HIB.Services;

public sealed partial class ToastButton : ObservableObject
{
    public ToastButton(string label, Action action, string kind = "")
    {
        Label = label;
        Kind = kind;
        Command = new RelayCommand(action);
    }

    public string Label { get; }
    /// <summary>"primary", "danger" or "" (neutral).</summary>
    public string Kind { get; }
    public bool IsPrimary => Kind == "primary";
    public bool IsDanger => Kind == "danger";
    public bool IsNeutral => Kind is not ("primary" or "danger");
    public IRelayCommand Command { get; }
}

public sealed partial class ToastViewModel : ObservableObject
{
    public string Key { get; init; } = Guid.NewGuid().ToString();
    [ObservableProperty] public partial string Caption { get; set; } = "";
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Body { get; set; } = "";
    [ObservableProperty] public partial Bitmap? Icon { get; set; }
    [ObservableProperty] public partial Bitmap? Image { get; set; }
    [ObservableProperty] public partial bool HasReply { get; set; }
    [ObservableProperty] public partial string ReplyText { get; set; } = "";
    [ObservableProperty] public partial string ReplyPlaceholder { get; set; } = "Reply…";
    [ObservableProperty] public partial bool IsCall { get; set; }
    public ObservableCollection<ToastButton> Buttons { get; } = new();

    public bool HasIcon => Icon is not null;
    public bool HasImage => Image is not null;
    public bool HasBody => !string.IsNullOrWhiteSpace(Body);
    public bool HasButtons => Buttons.Count > 0;

    /// <summary>Stays until closed (calls).</summary>
    public bool Persistent { get; init; }
    public int Seconds { get; init; } = 6;
    public Action<string>? OnReply { get; init; }
    /// <summary>When set, the toast can be dragged out as this file (into Explorer, WhatsApp, Outlook…).</summary>
    public string? DragFilePath { get; init; }
    public bool IsDraggable => !string.IsNullOrEmpty(DragFilePath);
    public Action? OnClick { get; init; }
    /// <summary>Called when the user presses ×.</summary>
    public Action? OnDismiss { get; init; }

    internal event Action? CloseRequested;
    public void Close() => CloseRequested?.Invoke();

    [RelayCommand]
    void SendReply()
    {
        var text = ReplyText.Trim();
        if (text.Length == 0) return;
        OnReply?.Invoke(text);
        Close();
    }

    [RelayCommand]
    void Dismiss()
    {
        OnDismiss?.Invoke();
        Close();
    }

    [RelayCommand]
    void Click()
    {
        OnClick?.Invoke();
        Close();
    }

    partial void OnIconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasIcon));
    partial void OnImageChanged(Bitmap? value) => OnPropertyChanged(nameof(HasImage));
    partial void OnBodyChanged(string value) => OnPropertyChanged(nameof(HasBody));
}

/// <summary>Custom bottom-right toast popups that stack upwards and auto-dismiss.</summary>
public sealed class ToastManager
{
    const double Margin = 12;
    const int MaxVisible = 5;
    readonly List<(ToastWindow window, ToastViewModel vm, Screen? screen)> _open = new();
    readonly Func<Window?> _anchor;

    public ToastManager(Func<Window?> anchor) => _anchor = anchor;

    /// <summary>Currently open toast windows (UI thread only).</summary>
    public IReadOnlyList<Window> OpenToasts => _open.Select(t => (Window)t.window).ToList();

    public void Show(ToastViewModel vm, Screen? screen = null) => Dispatcher.UIThread.Post(() => ShowCore(vm, screen));

    public void Close(string key) => Dispatcher.UIThread.Post(() =>
    {
        foreach (var t in _open.Where(t => t.vm.Key == key).ToList()) t.window.Close();
    });

    void ShowCore(ToastViewModel vm, Screen? screen)
    {
        // Replace a toast with the same key (e.g. an updated notification).
        foreach (var existing in _open.Where(t => t.vm.Key == vm.Key).ToList()) existing.window.Close();
        while (_open.Count(t => !t.vm.Persistent) >= MaxVisible)
            _open.First(t => !t.vm.Persistent).window.Close();

        var window = new ToastWindow { DataContext = vm };
        var entry = (window, vm, screen);
        _open.Add(entry);
        DispatcherTimer? timer = null;
        vm.CloseRequested += () => window.Close();
        window.Closed += (_, _) =>
        {
            timer?.Stop();
            _open.RemoveAll(t => t.window == window);
            Layout();
        };
        if (!vm.Persistent)
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(2, vm.Seconds)) };
            timer.Tick += (_, _) =>
            {
                if (window.IsPointerOver || window.IsKeyboardFocusWithin) return; // keep while hovered / typing
                timer.Stop();
                window.Close();
            };
            timer.Start();
        }
        window.Opacity = 0;
        window.Show();
        window.SizeChanged += (_, _) => Layout();
        Dispatcher.UIThread.Post(() => { Layout(); window.Opacity = 1; }, DispatcherPriority.Background);
    }

    Screen? DefaultScreen()
    {
        var anchor = _anchor();
        var screens = anchor?.Screens;
        if (screens is null) return null;
        var cursor = GlobalCursor.Position;
        if (cursor is { } c)
        {
            var under = screens.All.FirstOrDefault(s => s.Bounds.Contains(c));
            if (under is not null) return under;
        }
        return screens.Primary ?? screens.All.FirstOrDefault();
    }

    void Layout()
    {
        var fallback = DefaultScreen();
        foreach (var group in _open.GroupBy(t => t.screen ?? fallback))
        {
            var screen = group.Key;
            if (screen is null) continue;
            var scale = screen.Scaling;
            var wa = screen.WorkingArea;
            var y = wa.Bottom - Margin * scale;
            // Newest at the bottom, older ones pushed up.
            foreach (var (window, _, _) in group.Reverse())
            {
                var w = (window.Bounds.Width > 0 ? window.Bounds.Width : window.Width) * scale;
                var h = (window.Bounds.Height > 0 ? window.Bounds.Height : 120) * scale;
                y -= h;
                window.Position = new PixelPoint((int)(wa.Right - w - Margin * scale), (int)y);
                y -= 8 * scale;
            }
        }
    }
}
