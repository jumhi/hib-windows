using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using HIB.Core.Remote;
using HIB.Services;
using HIB.ViewModels;
using HIB.Views;

namespace HIB.FakePhone;

/// <summary>Phone Remote in the app (SPACE.md §15.4): the "Controlled from" pill with Stop, and Settings › Phone remote.</summary>
public class PhoneRemoteUiTests
{
    static Task PumpUntil(Func<bool> cond, int ms = 4000) => RoamUiTests.PumpUntil(cond, ms);

    sealed class Setup : IAsyncDisposable
    {
        public required TestHost Host;
        public required MainWindow Window;
        public required AppController Controller;
        public required MainViewModel Main;
        public required FakePhone Phone;
        public required FakeScreenFactory Screens;

        public static async Task<Setup> StartAsync(bool managed = false)
        {
            var screens = new FakeScreenFactory();
            var host = new TestHost(name: "PC-INTERN", remote: new PhoneRemoteOptions
            {
                Pipelines = () => screens,
                System = () => new FakeRemoteSystem { Join = (managed, false) },
                TailscaleAddrs = () => new[] { "100.101.102.103" },
            });
            var window = new MainWindow { Width = 1100, Height = 760 };
            var controller = new AppController(host.Core, null) { WindowOverride = window };
            var screen = FakeScreen.Make(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), 1, true);
            controller.Screens.Override = () => new[] { screen };
            controller.Point.PublishScreens();
            var main = new MainViewModel(controller);
            window.DataContext = main;
            window.Show();
            var phone = new FakePhone(host.Core.BuildQr()) { Name = "Haitham's S26 Ultra" };
            await phone.ConnectAsync();
            await phone.SendDeviceAsync();
            await phone.WaitForAsync("macInfo");
            await PumpUntil(() => main.IsPaired);
            return new Setup { Host = host, Window = window, Controller = controller, Main = main, Phone = phone, Screens = screens };
        }

        public string Text => string.Join("\n", Window.GetVisualDescendantsText());

        public async ValueTask DisposeAsync()
        {
            Controller.Remote.Dispose();
            Window.AllowClose = true;
            Window.Close();
            await Phone.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task The_pill_shows_while_the_phone_views_and_Stop_ends_it()
    {
        await using var s = await Setup.StartAsync();
        Assert.Null(s.Controller.Remote.Pill);
        await using var c = new FakeScreenClient(s.Phone.Key);
        await c.ConnectAsync(s.Host.Core.Link.Port);
        await c.OpenAsync();
        await PumpUntil(() => s.Controller.Remote.Pill is { IsVisible: true });
        var pill = s.Controller.Remote.Pill!;
        Assert.Equal("Controlled from Haitham's S26 Ultra", pill.Text);
        Assert.Equal("Stop", pill.StopButton.Content);
        // Top centre of the main display.
        await PumpUntil(() => pill.ClientSize.Width > 0 && pill.Position.X == (int)Math.Round((1920 - pill.ClientSize.Width) / 2));
        Assert.Equal((int)Math.Round((1920 - pill.ClientSize.Width) / 2), pill.Position.X);
        Assert.Equal(0, pill.Position.Y);

        pill.StopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(await c.WaitClosedAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(4012, (int?)c.CloseStatus);
        await PumpUntil(() => s.Controller.Remote.Pill is null);
        Assert.Null(s.Controller.Remote.Pill);
    }

    [AvaloniaFact]
    public async Task Settings_phone_remote_has_the_same_words_and_switches()
    {
        await using var s = await Setup.StartAsync();
        s.Main.Navigate("remote");
        await PumpUntil(() => s.Text.Contains("Allow viewing and control"));
        var text = s.Text;
        Assert.Contains("Phone remote", text);
        Assert.Contains("Allow viewing and control", text);
        Assert.Contains("Unlock from phone", text);
        Assert.Contains(PhoneRemoteSettingsViewModel.NotYetText, text);
        Assert.Contains("Stay reachable when locked", text);
        Assert.Contains(PhoneRemoteSettingsViewModel.StayAwakeText, text);
        Assert.Contains("Nobody is viewing this PC", text);
        Assert.Contains("Tailscale on this PC: 100.101.102.103", text);
        Assert.DoesNotContain("Not Found", text);

        var vm = (PhoneRemoteSettingsViewModel)s.Main.CurrentContent!;
        Assert.True(vm.Allowed);
        Assert.False(vm.StayReachable); // off by default on Windows (Unlock from phone isn't set up here)
        vm.Allowed = false;
        vm.StayReachable = true;
        Assert.False(s.Host.Core.Remote.Allowed);
        Assert.True(s.Host.Core.Remote.StayReachable);
        Assert.False(s.Host.Core.Settings.RemoteAllowed);
        Assert.True(s.Host.Core.Settings.RemoteStayReachable);
        vm.Allowed = true;
    }

    [AvaloniaFact]
    public async Task A_managed_pc_explains_why_unlock_isnt_offered()
    {
        await using var s = await Setup.StartAsync(managed: true);
        s.Main.Navigate("remote");
        await PumpUntil(() => s.Text.Contains("organisation"));
        Assert.Contains(PhoneRemoteSettingsViewModel.ManagedText, s.Text);
    }
}
