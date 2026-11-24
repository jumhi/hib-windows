using Avalonia;
using Avalonia.Headless.XUnit;
using HIB.Core.Roam;
using HIB.Core.Space;
using HIB.ViewModels;
using HIB.Views.Roam;

namespace HIB.FakePhone;

/// <summary>Roam round 2 in the app (SPACE.md §12): the Devices row's speeds, latency and "Get log", the pills, and Settings.</summary>
public class RoamRound2UiTests
{
    const string MacName = RoamUiTests.MacName;

    static Task PumpUntil(Func<bool> cond, int ms = 4000) => RoamUiTests.PumpUntil(cond, ms);

    [AvaloniaFact]
    public async Task Devices_row_sets_speeds_and_natural_scrolling_shows_latency_and_gets_the_macs_log()
    {
        await using var s = await RoamUiTests.Setup.StartAsync();
        s.Main.Navigate("devices");
        await PumpUntil(() => s.MacCard?.HasRoam == true);
        var card = s.MacCard!;
        Assert.Equal(1, card.RoamPointerSpeed);
        Assert.Equal("1×", card.RoamPointerSpeedText);
        Assert.False(card.RoamNaturalScroll);

        card.RoamPointerSpeed = 1.5;
        card.RoamScrollSpeed = 0.75;
        card.RoamNaturalScroll = true;
        await PumpUntil(() => s.MacCard!.RoamPointerSpeedText == "1.5×" && s.MacCard.RoamNaturalScroll);
        card = s.MacCard!;
        Assert.Equal("0.75×", card.RoamScrollSpeedText);
        var status = s.Host.Core.Roam.StatusFor(MacName)!;
        Assert.Equal((1.5, 0.75, true), (status.PointerSpeed, status.ScrollSpeed, status.NaturalScroll));

        // §12.3 a slow Wi-Fi link: the latency and the tip.
        var link = s.Host.Core.SpaceEngine.Network.Get(MacName)!;
        for (var i = 0; i < LatencyMeter.Samples; i++) link.Latency.Add(38);
        await PumpUntil(() => s.MacCard!.RoamLatencyText == "Latency 38 ms");
        Assert.True(s.MacCard!.RoamSlowLink);
        await PumpUntil(() => string.Join("\n", s.Window.GetVisualDescendantsText()).Contains("5 GHz"), 1500);
        var text = string.Join("\n", s.Window.GetVisualDescendantsText());
        Assert.Contains("Wi-Fi is adding delay — 5 GHz or a cable to one computer helps", text);
        Assert.Contains("Pointer speed", text);
        Assert.Contains("Natural scrolling", text);
        Assert.Contains("Get log", text);

        // §12.2 Get log: peerDiag over the link, saved by name next to this PC's logs, shown in its folder.
        var dir = Path.Combine(s.Host.Root, "logs");
        PeerDiagnostics.DirectoryOverride = dir;
        string? revealed = null;
        DeviceCardViewModel.RevealLog = p => revealed = p;
        try
        {
            s.MacCard!.GetLogCommand.Execute(null);
            await PumpUntil(() => s.Mac.Of("peerDiag").Count == 1);
            await s.Mac.SendAsync("peerDiagLog", new { version = "4.2.0", text = "[roam] Roam keys (10 s): captured 2 · sent 2\n" });
            await PumpUntil(() => revealed is not null);
            Assert.Equal(Path.Combine(dir, "Haitham’s MacBook.log"), revealed);
            Assert.Contains("captured 2 · sent 2", File.ReadAllText(revealed!));
            Assert.Equal("Saved Haitham’s MacBook.log with this PC's logs", s.MacCard!.Status);
        }
        finally
        {
            PeerDiagnostics.DirectoryOverride = null;
            DeviceCardViewModel.RevealLog = HIB.Core.Platform.PlatformShell.RevealInFolder;
        }
    }

    [AvaloniaFact]
    public async Task Pills_say_game_mode_the_lock_and_admin_apps_where_the_user_looks()
    {
        await using var s = await RoamUiTests.Setup.StartAsync();
        // The Mac's game holds its cursor: crossing over says so beside the edge it went through.
        await s.Mac.SendRawAsync("""{"type":"ctlTargetState","data":{"mode":"locked","app":"Cyberpunk2077"}}""");
        await PumpUntil(() => s.Host.Core.Roam.StatusFor(MacName) is { RemoteGame: RoamGameMode.Locked });
        s.Input.Raise(RoamInput.Pointer(1925, 540, false));
        await PumpUntil(() => s.Controller.Roam.Pill is { Pill.Kind: RoamPillKind.GameMode });
        var pill = s.Controller.Roam.Pill!;
        Assert.Equal($"Game mode · the mouse stays on {MacName} · Scroll Lock or Ctrl+Alt+Shift+L to leave", pill.Text);
        await PumpUntil(() => pill.Position.X > 1000);
        Assert.InRange(pill.Position.X, 1300, 1700); // against the right edge (the capsule is ~500 px wide)
        Assert.InRange(pill.Position.Y, 400, 560);

        // Scroll Lock leaves the game: home, the pill goes.
        s.Input.Raise(RoamInput.Hot(RoamHotkey.Lock));
        await PumpUntil(() => s.Controller.Roam.Pill is null);
        Assert.Equal(RoamMode.Home, s.Host.Core.Roam.Mode);

        // Scroll Lock at home: locked to this PC.
        s.Input.Raise(RoamInput.Hot(RoamHotkey.Lock));
        await PumpUntil(() => s.Controller.Roam.Pill is { Pill.Kind: RoamPillKind.Locked });
        Assert.Equal("Locked to Office PC", s.Controller.Roam.Pill!.Text);
        Assert.True(s.Host.Core.Roam.UserLock);
        s.Input.Raise(RoamInput.Pointer(1925, 540, false));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(RoamMode.Home, s.Host.Core.Roam.Mode);

        // The admin warning sits at the bottom of the screen, above the taskbar.
        s.Controller.Roam.ShowPill(new RoamPill(RoamPillKind.AdminApp, UipiGuard.PillText));
        await PumpUntil(() => s.Controller.Roam.Pill is { Pill.Kind: RoamPillKind.AdminApp } p && p.Position.Y > 800);
        var admin = s.Controller.Roam.Pill!;
        Assert.Equal(UipiGuard.PillText, admin.Text);
        Assert.InRange(admin.Position.Y, 880, 1000); // 64 points above the bottom
        s.Controller.Roam.ShowPill(null);
        Assert.Null(s.Controller.Roam.Pill);
        Assert.Equal(TimeSpan.FromSeconds(1.6), RoamPillWindow.LifetimeOf(RoamPillKind.Gate));
    }

    [AvaloniaFact]
    public async Task Settings_turn_game_mode_and_lock_together_off_and_on()
    {
        await using var s = await RoamUiTests.Setup.StartAsync();
        s.Main.Navigate("general");
        await PumpUntil(() => string.Join("\n", s.Window.GetVisualDescendantsText()).Contains("Lock together"), 2000);
        var text = string.Join("\n", s.Window.GetVisualDescendantsText());
        Assert.Contains("Game mode · automatic", text);
        Assert.Contains("Ctrl+Alt+Shift+arrows", text);
        Assert.True(s.Main.Settings.RoamGameMode);
        Assert.True(s.Main.Settings.RoamLockTogether);
        s.Main.Settings.RoamGameMode = false;
        s.Main.Settings.RoamLockTogether = false;
        Assert.False(s.Host.Core.Roam.Options.GameMode);
        Assert.False(s.Host.Core.Roam.Options.LockTogether);
        Assert.True(await Eventually.TrueAsync(() => s.Mac.Of("ctlTargetState").Count >= 1));
        s.Main.Settings.RoamGameMode = true;
        Assert.True(s.Host.Core.Roam.Options.GameMode);
    }
}
