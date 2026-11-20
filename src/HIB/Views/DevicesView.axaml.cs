using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using HIB.Services;
using HIB.ViewModels;

namespace HIB.Views;

/// <summary>Devices tab: each device card takes dropped files and sends them to that device.</summary>
public partial class DevicesView : UserControl
{
    public DevicesView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearTargets());
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    static DeviceCardViewModel? CardAt(object? source)
    {
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
            if (v is Border { DataContext: DeviceCardViewModel card } b && b.Classes.Contains("devcard"))
                return card;
        return null;
    }

    void ClearTargets(DeviceCardViewModel? except = null)
    {
        if (DataContext is not DevicesViewModel vm) return;
        foreach (var c in vm.Devices.Where(c => c != except)) c.IsDropTarget = false;
    }

    void OnDragOver(object? sender, DragEventArgs e)
    {
        var card = CardAt(e.Source);
        ClearTargets(card);
        if (card is null || FileDrag.IsDraggingOut || FileDrag.LocalPaths(e).Count == 0) return;
        card.IsDropTarget = true;
        e.DragEffects = card.CanReceive ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object? sender, DragEventArgs e)
    {
        var card = CardAt(e.Source);
        ClearTargets();
        if (card is null || FileDrag.IsDraggingOut) return;
        var paths = FileDrag.LocalPaths(e);
        if (paths.Count == 0) return;
        e.Handled = true;
        _ = card.DropAsync(paths);
    }
}
