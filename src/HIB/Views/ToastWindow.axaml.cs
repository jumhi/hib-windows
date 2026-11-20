using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using HIB.Services;

namespace HIB.Views;

public partial class ToastWindow : Window
{
    PointerPressedEventArgs? _press;
    Point _pressPos;
    bool _dragged;

    public ToastWindow()
    {
        InitializeComponent();
        Icon = ImageUtil.WindowIcon();
    }

    void OnContentPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _press = e;
        _pressPos = e.GetPosition(this);
        _dragged = false;
    }

    // Like the Mac's Flick card: drag a received file out of the toast straight into Explorer,
    // WhatsApp Desktop, Outlook, a browser upload box…
    async void OnContentMoved(object? sender, PointerEventArgs e)
    {
        if (_press is null || _dragged) return;
        if (DataContext is not ToastViewModel { DragFilePath: { Length: > 0 } path }) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _press = null; return; }
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _pressPos.X) + Math.Abs(p.Y - _pressPos.Y) < 8) return;
        _dragged = true;
        var press = _press;
        _press = null;
        try
        {
            await FileDrag.StartAsync(this, press, path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Toast drag failed: {ex.Message}");
        }
    }

    void OnContentClicked(object? sender, PointerReleasedEventArgs e)
    {
        _press = null;
        if (_dragged) { _dragged = false; return; }
        if (DataContext is ToastViewModel vm && vm.OnClick is not null) vm.ClickCommand.Execute(null);
    }
}
