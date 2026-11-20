using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using HIB.Services;

namespace HIB.Views;

/// <summary>"Set up mirroring" (see <see cref="HIB.ViewModels.AdbSetupViewModel"/>).</summary>
public partial class AdbSetupWindow : Window
{
    public AdbSetupWindow()
    {
        InitializeComponent();
        Icon = ImageUtil.WindowIcon();
        WindowDecorationProperties.SetElementRole(TitleStrip, WindowDecorationsElementRole.TitleBar);
    }
}
