using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HIB.Services;
using HIB.ViewModels;
using HIB.Views;

namespace HIB.FakePhone;

/// <summary>Renders the Webcam and Calls pages headlessly (no phone, no ADB) so XAML and compiled
/// bindings are exercised, and checks the dialer's validation reaches the service.</summary>
public class WebcamCallsUiTests
{
    [AvaloniaFact]
    public async Task Webcam_and_calls_pages_render()
    {
        await using var host = new TestHost();
        var window = new MainWindow { Width = 1080, Height = 760 };
        var controller = new AppController(host.Core, null) { WindowOverride = window };
        using var features = new FeatureHost(host.Core, controller);
        controller.Features = features;
        var main = new MainViewModel(controller);
        window.DataContext = main;
        window.Show();

        Assert.Contains(main.NavItems, n => n.Key == "webcam");
        Assert.Contains(main.NavItems, n => n.Key == "calls");

        main.Navigate("webcam");
        Render(window);
        var webcam = Assert.IsType<WebcamViewModel>(main.CurrentContent);
        Assert.False(webcam.UsbCanStart);   // nothing checked / no ADB
        webcam.FacingIndex = 1;
        Assert.Equal(HIB.Features.Camera.CameraFacing.Back, features.Camera.Options.Facing);

        main.Navigate("calls");
        Render(window);
        var calls = Assert.IsType<CallsViewModel>(main.CurrentContent);
        calls.Number = "123; reboot";
        Assert.False(calls.CanCall);
        await calls.CallCommand.ExecuteAsync(null);
        Assert.Null(features.Calls.Status);  // invalid numbers never reach the service
        calls.Number = "+966 50 123 4567";
        await calls.CallCommand.ExecuteAsync(null);
        Assert.Contains("ADB", features.Calls.Status);

        window.AllowClose = true;
        window.Close();
    }

    static void Render(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Assert.NotNull(w.CaptureRenderedFrame());
        var text = string.Join("\n", w.GetVisualDescendantsText());
        Assert.DoesNotContain("Not Found", text);
    }
}

static class VisualTextExtensions
{
    public static IEnumerable<string> GetVisualDescendantsText(this Window w) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(w).OfType<TextBlock>().Select(t => t.Text ?? "");
}
