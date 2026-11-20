using System;
using Avalonia.Controls;
using Avalonia.Threading;
using HIB.ViewModels;

namespace HIB.Views;

public partial class AppsView : UserControl
{
    AppsViewModel? _vm;

    public AppsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.FocusRequested -= FocusSearch;
            _vm = DataContext as AppsViewModel;
            if (_vm is not null) _vm.FocusRequested += FocusSearch;
        };
        // Like the Mac: typing goes straight into the search bar when the tab opens.
        AttachedToVisualTree += (_, _) => FocusSearch();
    }

    void FocusSearch() => Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Background);
}
