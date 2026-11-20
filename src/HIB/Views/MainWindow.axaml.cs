using System;
using Avalonia.Controls;
using HIB.Services;

namespace HIB.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Icon = ImageUtil.WindowIcon();
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
