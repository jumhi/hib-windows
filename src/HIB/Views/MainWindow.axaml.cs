using System;
using Avalonia.Controls;
using Avalonia.Input;
using HIB.Services;

namespace HIB.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Icon = ImageUtil.WindowIcon();
        // Drop files anywhere on the window to send them to the phone (or the computer picked on Files).
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = !FileDrag.IsDraggingOut && FileDrag.LocalPaths(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        });
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (FileDrag.IsDraggingOut) return;
            var paths = FileDrag.LocalPaths(e);
            if (paths.Count > 0) AppController.Current?.SendDroppedFiles(paths);
        });
    }

    /// <summary>When true, closing the window really closes it (used by Quit).</summary>
    public bool AllowClose { get; set; }

    /// <summary>Returns true to hide instead of closing.</summary>
    public Func<bool>? HideOnClose { get; set; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose && HideOnClose?.Invoke() == true)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
