using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using HIB.Services;
using HIB.ViewModels;

namespace HIB.Views;

public partial class FilesView : UserControl
{
    PointerPressedEventArgs? _press;
    Point _pressPos;

    public FilesView() => InitializeComponent();

    void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _press = e;
        _pressPos = e.GetPosition(this);
    }

    // Drag a received file straight out of H.I.B into any app.
    async void OnCardMoved(object? sender, PointerEventArgs e)
    {
        if (_press is null || sender is not Control { DataContext: FileItemViewModel item } card) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _press = null; return; }
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _pressPos.X) + Math.Abs(p.Y - _pressPos.Y) < 8) return;
        var press = _press;
        _press = null;
        try { await FileDrag.StartAsync(card, press, item.Path); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"File drag failed: {ex.Message}"); }
    }
}
