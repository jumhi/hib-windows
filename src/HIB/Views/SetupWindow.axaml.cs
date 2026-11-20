using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using HIB.Services;
using HIB.ViewModels;

namespace HIB.Views;

/// <summary>H.I.B Setup / Uninstall H.I.B (see <see cref="SetupViewModel"/>).</summary>
public partial class SetupWindow : Window
{
    public SetupWindow()
    {
        InitializeComponent();
        Icon = ImageUtil.WindowIcon();
        WindowDecorationProperties.SetElementRole(TitleStrip, WindowDecorationsElementRole.TitleBar);
    }

    public SetupWindow(SetupViewModel vm) : this()
    {
        DataContext = vm;
        Title = vm.IsUninstall ? "Uninstall H.I.B" : "H.I.B Setup";
        vm.Close = Close;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Don't leave half an install behind: the window stays while files are being copied.
        if (DataContext is SetupViewModel { IsWorking: true } && !e.IsProgrammatic) e.Cancel = true;
        base.OnClosing(e);
    }
}
