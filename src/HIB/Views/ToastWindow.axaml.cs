using Avalonia.Controls;
using Avalonia.Input;
using HIB.Services;

namespace HIB.Views;

public partial class ToastWindow : Window
{
    public ToastWindow()
    {
        InitializeComponent();
        Icon = ImageUtil.WindowIcon();
    }

    void OnContentClicked(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is ToastViewModel vm && vm.OnClick is not null) vm.ClickCommand.Execute(null);
    }
}
