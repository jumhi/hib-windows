using System.Text;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HIB.Features;
using HIB.Features.Adb;
using HIB.Features.Mirror;
using HIB.Services;
using HIB.ViewModels;
using HIB.Views;

namespace HIB.FakePhone;

/// <summary>
/// SPACE.md §8 end to end with the fake phone: <c>adbSetupState</c> parsing per link, <c>adbSetupRequest</c>, the
/// connect port preferred over stale <c>adbPorts</c>, Mirror opening "Set up mirroring" instead of a dead end, and
/// the card walking Developer options → Wireless debugging → QR → code → ready. adb is answered by a fake: nothing
/// runs and nothing touches a phone. Set HIB_DESIGN_DIR to save docs/design/windows-adb-setup-*.png.
/// </summary>
public class AdbSetupUiTests
{
    static readonly string? ShotDir = Environment.GetEnvironmentVariable("HIB_DESIGN_DIR");

    static async Task Pump(int ms = 300)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    static async Task UntilAsync(Func<bool> cond, int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(cond());
    }

    static void Shot(Avalonia.Controls.Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (ShotDir is null) return;
        Directory.CreateDirectory(ShotDir);
        frame!.Save(Path.Combine(ShotDir, name + ".png"));
    }

    static object State(bool dev, bool wifi, int? connect = null, int? pairPort = null, string? pairName = null)
        => new Dictionary<string, object?>
        {
            ["developerOptions"] = dev, ["wirelessDebugging"] = wifi, ["connectPort"] = connect,
            ["pairingPort"] = pairPort, ["pairingName"] = pairName,
        };

    /// <summary>A fake adb: records commands; "connect" works only once paired.</summary>
    sealed class FakeAdb
    {
        readonly List<string> _commands = new();
        public bool Paired;
        public IReadOnlyList<string> Commands { get { lock (_commands) return _commands.ToList(); } }

        public Task<ProcessResult> Run(IReadOnlyList<string> args, CancellationToken ct)
        {
            lock (_commands) _commands.Add(string.Join(' ', args));
            string Out(string s) => s;
            var text = args[0] switch
            {
                "connect" when Paired => Out($"connected to {args[1]}"),
                "connect" => Out($"failed to authenticate to {args[1]}"),
                "pair" => Out($"Successfully paired to {args[1]} [guid=adb-R5GL2171V0J-kngrgU]"),
                _ => "",
            };
            if (args[0] == "pair") Paired = true;
            return Task.FromResult(new ProcessResult(0, Encoding.UTF8.GetBytes(text), "", false));
        }
    }

    [AvaloniaFact]
    public async Task Phone_reports_its_adb_setup_and_the_card_walks_it_to_ready()
    {
        await using var host = new TestHost();
        var controller = new AppController(host.Core, null);
        using var features = new FeatureHost(host.Core, controller);
        controller.Features = features;
        features.AdbAutoConnect = false;
        var fake = new FakeAdb();
        features.Adb.CommandRunner = fake.Run; // nothing real runs

        await using var phone = new FakePhone(host.Core.BuildQr()) { Name = "Haitham's S26 Ultra" };
        await phone.ConnectAsync();
        await phone.SendDeviceAsync();
        await phone.WaitForAsync("macInfo");
        await Pump();
        Assert.Null(host.Core.Hub.AdbSetup);

        // Parsed and stored for this link.
        await phone.SendAsync("adbSetupState", State(dev: false, wifi: false));
        await UntilAsync(() => host.Core.Hub.AdbSetup is { DeveloperOptions: false });
        Assert.Equal(new AdbSetupState(false, false, null, null, null), features.Pairing.SetupState);
        Assert.Equal("192.168.1.33", features.Pairing.PhoneIp);

        // Mirror with ADB not ready → the card opens (never a dead end), remembering what to start.
        var session = await features.Launcher.MirrorAsync();
        Assert.Null(session);
        await Pump();
        Assert.True(features.Setup.IsOpen);
        var vm = features.Setup.Current!;
        var window = features.Setup.Window!;
        window.SetRenderScaling(2);
        Assert.Equal(MirrorKind.Mirror, vm.Then?.Kind);
        Assert.Empty(fake.Commands); // the phone said what's missing: no adb attempts
        var watch = await phone.WaitForAsync("adbSetupRequest");
        Assert.True(watch.Data.GetProperty("watch").GetBoolean());

        await UntilAsync(() => vm.IsDeveloperStep);
        Assert.Equal("Turn on Developer options", vm.Title);
        Assert.True(vm.DeveloperItem.IsCurrent);
        await Pump();
        Shot(window, "windows-adb-setup-developer");

        await vm.ShowDeveloperOptionsCommand.ExecuteAsync(null);
        var open = await phone.WaitForAsync("adbSetupRequest", match: m => m.Data.TryGetProperty("open", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.String);
        Assert.Equal("developer", open.Data.GetProperty("open").GetString());

        // Developer options on → Wireless debugging (with a live tick on step 1).
        await phone.SendAsync("adbSetupState", State(dev: true, wifi: false));
        await UntilAsync(() => vm.IsWirelessStep);
        Assert.True(vm.DeveloperItem.IsDone);
        Assert.True(vm.WirelessItem.IsCurrent);
        await Pump();
        Shot(window, "windows-adb-setup-wireless");
        await vm.OpenWirelessDebuggingCommand.ExecuteAsync(null);
        await phone.WaitForAsync("adbSetupRequest", match: m => m.Data.TryGetProperty("open", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.String && o.GetString() == "wireless");

        // Wireless debugging on: H.I.B tries the reported port (not the stale adbPorts) → not paired → the QR.
        await phone.SendAsync("adbSetupState", State(dev: true, wifi: true, connect: 42789));
        await UntilAsync(() => vm.IsQrStep && vm.QrImage is not null);
        Assert.Contains("connect 192.168.1.33:42789", fake.Commands);
        Assert.DoesNotContain(fake.Commands, c => c.Contains("37145") || c.Contains("40211"));
        Assert.True(vm.WirelessItem.IsDone);
        Assert.True(vm.PairItem.IsCurrent);
        Assert.StartsWith("WIFI:T:ADB;S:adb-wireless-", features.Pairing.QrText);
        await Pump();
        Shot(window, "windows-adb-setup-qr");

        // "Use a pairing code instead": a single 6-digit box; the port comes from the phone.
        vm.UseCodeCommand.Execute(null);
        await UntilAsync(() => vm.IsCodeStep);
        await phone.SendAsync("adbSetupState", State(dev: true, wifi: true, connect: 42789, pairPort: 41555, pairName: "adb-R5GL2171V0J-kngrgU"));
        await UntilAsync(() => vm.PhoneShowsCode);
        await Pump();
        Shot(window, "windows-adb-setup-code");
        vm.Code = "48 29 13"; // typed (or pasted) — six digits pair at once
        await UntilAsync(() => vm.IsReady, 10000);
        Assert.Contains("pair 192.168.1.33:41555 482913", fake.Commands);
        Assert.Equal("connect 192.168.1.33:42789", fake.Commands[^1]);
        Assert.True(features.Adb.IsConnected);
        Assert.True(vm.PairItem.IsDone);
        Assert.Equal("Mirroring is ready", vm.Title);
        Assert.Equal("Mirror", vm.ReadyButtonText);
        await Pump();
        Shot(window, "windows-adb-setup-ready");
        Assert.DoesNotContain(fake.Commands, c => c.StartsWith("mdns"));

        // Closing stops the phone's fast reporting.
        features.Setup.Close();
        await Pump();
        await phone.WaitForAsync("adbSetupRequest", match: m => m.Data.TryGetProperty("watch", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.False);
        Assert.False(features.Setup.IsOpen);

        // The link dropping forgets the report (the next phone sends its own).
        await phone.DisposeAsync();
        await UntilAsync(() => host.Core.Hub.AdbSetup is null && features.Pairing.SetupState is null);
    }

    [AvaloniaFact]
    public async Task Auto_connect_prefers_the_reported_connect_port()
    {
        await using var host = new TestHost();
        var controller = new AppController(host.Core, null);
        using var features = new FeatureHost(host.Core, controller);
        controller.Features = features;
        features.AdbAutoConnect = false;
        var fake = new FakeAdb { Paired = true };
        features.Adb.CommandRunner = fake.Run;
        await using var phone = new FakePhone(host.Core.BuildQr());
        await phone.ConnectAsync();
        await phone.SendDeviceAsync(); // adbPorts 37145, 40211 (stale)
        await phone.WaitForAsync("macInfo");

        // Older phone app (no adbSetupState): the device message's ports.
        var r = await features.AutoConnectAdbAsync(silent: false, notify: false);
        Assert.True(r!.Success);
        Assert.Equal("connect 192.168.1.33:37145", fake.Commands.Last(c => c.StartsWith("connect")));
        await features.Adb.DisconnectAsync();

        // The phone reports Wireless debugging off: no attempts, no mDNS, a plain reason.
        await phone.SendAsync("adbSetupState", State(dev: true, wifi: false));
        await UntilAsync(() => host.Core.Hub.AdbSetup is { WirelessDebugging: false });
        var count = fake.Commands.Count;
        r = await features.AutoConnectAdbAsync(silent: false, notify: false);
        Assert.False(r!.Success);
        Assert.Equal("Wireless debugging is off on the phone.", r.Output);
        Assert.Equal(count, fake.Commands.Count);

        // On, with its own port: that port only.
        await phone.SendAsync("adbSetupState", State(dev: true, wifi: true, connect: 42789));
        await UntilAsync(() => host.Core.Hub.AdbSetup is { ConnectPort: 42789 });
        r = await features.AutoConnectAdbAsync(silent: false, notify: false);
        Assert.True(r!.Success);
        Assert.Equal("connect 192.168.1.33:42789", fake.Commands.Last(c => c.StartsWith("connect")));
        Assert.DoesNotContain(fake.Commands, c => c.StartsWith("mdns"));

        // adbSetupRequest goes out as the contract says.
        await host.Core.Hub.RequestAdbSetupAsync("wireless", true);
        var req = await phone.WaitForAsync("adbSetupRequest");
        Assert.Equal("wireless", req.Data.GetProperty("open").GetString());
        Assert.True(req.Data.GetProperty("watch").GetBoolean());
    }
}
