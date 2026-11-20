using HIB.Features.Abstractions;
using HIB.Features.Adb;
using HIB.Features.Mirror;
using HIB.Features.PhoneAgent;
using SkiaSharp;

namespace HIB.Features.Tests;

public class AdbParsingTests
{
    [Fact]
    public void WiredDevices_SkipWirelessAndMdnsSerials()
    {
        const string output = """
            List of devices attached
            10.107.0.205:46375     device product:m3qxxx model:SM_S948B device:m3q transport_id:1475
            adb-R5GL2171V0J-kngrgU._adb-tls-connect._tcp device product:m3qxxx model:SM_S948B device:m3q transport_id:1442
            R5GL2171V0J            device usb:1-1 product:m3qxxx model:SM_S948B device:m3q transport_id:3
            emulator-5554          offline
            """;
        var d = Assert.Single(AdbService.ParseWiredDevices(output));
        Assert.Equal(("R5GL2171V0J", "SM S948B"), (d.Serial, d.Model));
    }

    [Fact]
    public void MdnsServices_Parse()
    {
        const string output = "List of discovered mdns services\nadb-R5GL2171V0J-kngrgU\t_adb-tls-connect._tcp\t10.107.0.205:46375\nadb-wireless-123456\t_adb-tls-pairing._tcp.\t10.107.0.205:37001\n";
        var list = AdbService.ParseMdnsServices(output);
        Assert.Equal(2, list.Count);
        Assert.Equal(new AdbMdnsService("adb-R5GL2171V0J-kngrgU", "_adb-tls-connect._tcp", "10.107.0.205", 46375), list[0]);
        Assert.Equal("_adb-tls-pairing._tcp", list[1].Type);
        Assert.Equal(new[] { 46375, 37001 }, AdbService.ParsePortsForIp(output, "10.107.0.205"));
        Assert.Empty(AdbService.ParsePortsForIp(output, "10.0.0.9"));
    }

    [Fact]
    public void FailureDiagnosis_MapsAdbOutput()
    {
        Assert.Contains("refused", AdbService.Diagnosis(["1.2.3.4:5555: failed to connect to '1.2.3.4:5555': connection refused"])!);
        var text = AdbService.BuildFailureText(["x: failed to authenticate to x"]);
        Assert.StartsWith("What happened:\nYour PC isn't authorized", text);
        Assert.Contains("• Pair your device", text);
    }

    [Fact]
    public void PairingQr_IsAndroidFormat()
        => Assert.Equal("WIFI:T:ADB;S:adb-wireless-123456;P:12345678;;", AdbPairingService.BuildQrText("adb-wireless-123456", "12345678"));

    [Fact]
    public void CurrentSerial_PrefersWiredThenWireless()
    {
        var adb = new AdbService(settings: new InMemorySettingsStore());
        Assert.Null(adb.CurrentSerial);
        adb.MarkConnected("10.0.0.5", 41000, "ok");
        Assert.Equal("10.0.0.5:41000", adb.CurrentSerial);
        adb.SelectedWiredSerial = "R5GL";
        Assert.Equal("10.0.0.5:41000", adb.CurrentSerial);
        adb.WiredAdbEnabled = true;
        Assert.Equal("R5GL", adb.CurrentSerial);
    }

    [Fact]
    public void Locator_AdbEnvironment_PrependsAdbDir()
    {
        var adbPath = Path.Combine(Path.GetTempPath(), "x", OperatingSystem.IsWindows() ? "adb.exe" : "adb");
        var env = AdbLocator.AdbEnvironment(adbPath);
        Assert.Equal(adbPath, env["ADB"]);
        Assert.StartsWith(Path.GetDirectoryName(adbPath)! + Path.PathSeparator, env["PATH"]);
    }
}

public class MirrorArgumentTests
{
    [Fact]
    public void AudioOnly_MatchesMac()
    {
        Assert.Equal(["-s", "1.2.3.4:5555", "--no-video", "--no-control", "--no-window", "--audio-source=playback", "--audio-codec=opus", "--audio-buffer=80"],
                     PhoneAudioBridge.BuildArguments("1.2.3.4:5555", false));
        Assert.Equal("--audio-dup", PhoneAudioBridge.BuildArguments("x", true)[^1]);
    }

    [Fact]
    public void PlainMirror_Wireless()
    {
        var s = new MirrorSettings(new InMemorySettingsStore());
        var args = MirrorService.BuildArguments(new MirrorRequest(MirrorKind.Mirror, "Haitham's S26"), s, null, "10.0.0.5:41000");
        Assert.Equal(["--window-title=Haitham S26", "--video-bit-rate=4M", "--video-codec=h265", "--no-power-on", "--tcpip=10.0.0.5:41000", "--max-size=1200"], args);
    }

    [Fact]
    public void SingleApp_UsesNewDisplayAndStartApp()
    {
        var s = new MirrorSettings(new InMemorySettingsStore()) { ContinueApp = true, StayAwake = true, DirectKeyInput = true };
        var args = MirrorService.BuildArguments(new MirrorRequest(MirrorKind.App, "S26", "com.whatsapp", "WhatsApp"), s, "R5GL", "unused");
        Assert.Equal("--window-title=WhatsApp · S26", args[0]);
        Assert.Contains("--serial=R5GL", args);
        Assert.Contains("--stay-awake", args);
        Assert.Contains("--keyboard=uhid", args);
        Assert.Equal(["--new-display=/192", "--start-app=com.whatsapp", "--no-vd-system-decorations", "-x", "--no-vd-destroy-content"], args.TakeLast(5).ToArray());
        Assert.DoesNotContain(args, a => a.StartsWith("--max-size"));
    }

    [Fact]
    public void Camera_HasNoControlOptions()
    {
        var s = new MirrorSettings(new InMemorySettingsStore()) { StayAwake = true, TurnScreenOff = true };
        var args = MirrorService.BuildArguments(new MirrorRequest(MirrorKind.Camera, "S26", CameraFacing: "front"), s, null, "a:1");
        Assert.Equal("--window-title=Galaxy Camera · S26", args[0]);
        Assert.Contains("--camera-facing=front", args);
        Assert.Contains("--no-control", args);
        Assert.DoesNotContain("--stay-awake", args);
        Assert.DoesNotContain(args, a => a.StartsWith("--new-display") || a.StartsWith("--max-size"));
    }
}

public class PhoneAgentAdbTests
{
    private const string Xml = """
        <?xml version='1.0' encoding='UTF-8' standalone='yes' ?><hierarchy rotation="0">
        <node index="0" text="" class="android.widget.FrameLayout" content-desc="" clickable="false" bounds="[0,0][1440,3120]">
          <node index="0" text="Search" class="android.widget.EditText" password="false" clickable="true" focused="true" bounds="[100,200][1340,320]" />
          <node index="1" text="hunter2" class="android.widget.EditText" password="true" clickable="true" bounds="[100,400][1340,520]" />
          <node index="2" text="" class="android.view.View" content-desc="Chats &quot;all&quot;" clickable="false" bounds="[0,600][720,700]" />
          <node index="3" text="" class="android.view.View" clickable="false" bounds="[0,800][720,900]" />
          <node index="4" text="Zero" class="android.widget.TextView" clickable="false" bounds="[10,10][10,50]" />
        </node></hierarchy>
        """;

    [Fact]
    public void UiTree_CompactsHidesPasswordsAndScales()
    {
        var lines = PhoneAgentAdb.CompactUiTree("junk before " + Xml, 2.0).Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("EditText text=\"Search\" clickable,editable,focused center=(360,130) bounds=[50,100,670,160]", lines[0]);
        Assert.Equal("EditText password-field clickable,editable center=(360,230) bounds=[50,200,670,260]", lines[1]);
        Assert.DoesNotContain("hunter2", string.Join("\n", lines));
        Assert.Equal("View desc=\"Chats 'all'\" center=(180,325) bounds=[0,300,360,350]", lines[2]);
        Assert.Equal("", PhoneAgentAdb.CompactUiTree("ERROR: null root node returned by UiTestAutomationBridge.", 1));
    }

    [Theory]
    [InlineData("hello world", "input text 'hello%sworld'")]
    [InlineData("it's", "input text 'it'\\''s'")]
    [InlineData("a;b && rm -rf /", "input text 'a;b%s&&%srm%s-rf%s/'")]
    [InlineData("مرحبا", null)]
    [InlineData("tab\there", null)]
    public void TypeCommand_QuotesForDeviceShell(string text, string? expected)
        => Assert.Equal(expected, PhoneAgentAdb.BuildTypeCommand(text));

    [Fact]
    public void ParseBounds() => Assert.Equal((-5, 10, 300, 400), PhoneAgentAdb.ParseBounds("[-5,10][300,400]"));

    [Fact]
    public void ProcessScreenshot_DownscalesToMax1280()
    {
        using var bmp = new SKBitmap(1440, 3120);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(SKColors.CornflowerBlue);
        using var png = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
        var screen = PhoneAgentAdb.ProcessScreenshot(png.ToArray())!;
        Assert.Equal((591, 1280), (screen.Width, screen.Height));
        Assert.Equal(1440.0 / 591, screen.Scale, 6);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, screen.Jpeg[..2]);
        using var thumb = SKBitmap.Decode(screen.Thumbnail);
        Assert.Equal((101, 220), (thumb.Width, thumb.Height));
        Assert.Null(PhoneAgentAdb.ProcessScreenshot("not an image"u8.ToArray()));
    }

    [Fact]
    public void AppList_FormatsLikeTheMac()
    {
        var entries = new Dictionary<string, string> { ["com.whatsapp"] = "WhatsApp", ["com.zzz"] = "", ["com.android.chrome"] = "Chrome" };
        Assert.Equal("Chrome — com.android.chrome\ncom.zzz\nWhatsApp — com.whatsapp", PhoneAgentController.FormatAppList(entries, ""));
        Assert.Equal("WhatsApp — com.whatsapp", PhoneAgentController.FormatAppList(entries, "WHATS"));
        Assert.StartsWith("No installed app matched “x1”", PhoneAgentController.FormatAppList(entries, "x1"));
    }
}

/// <summary>
/// Read-only checks against a real phone. Opt-in: set HIB_LIVE_ADB=1 (and optionally HIB_ADB_SERIAL).
/// Only screencap and uiautomator dump are run — never taps, keys, text or broadcasts.
/// </summary>
public class LiveAdbReadOnlyTests
{
    [Fact]
    public async Task Screencap_And_UiDump_OnConnectedPhone()
    {
        if (Environment.GetEnvironmentVariable("HIB_LIVE_ADB") != "1") return;
        var locator = new AdbLocator();
        var adbPath = locator.AdbPath;
        Assert.NotNull(adbPath);
        var adbService = new AdbService(locator, new InMemorySettingsStore());
        var serial = Environment.GetEnvironmentVariable("HIB_ADB_SERIAL");
        if (string.IsNullOrEmpty(serial))
        {
            var r = await adbService.RunAsync(["devices"]);
            serial = r.StdOutText.Split('\n').Skip(1).Select(l => l.Split('\t')).FirstOrDefault(p => p.Length == 2 && p[1].Trim() == "device")?[0];
        }
        Assert.False(string.IsNullOrEmpty(serial));
        var adb = new PhoneAgentAdb(adbPath!, serial!);
        var screen = await adb.CaptureScreenAsync(includeTree: true);
        Assert.True(screen.Width > 0 && screen.Height > 0 && Math.Max(screen.Width, screen.Height) <= 1280);
        Assert.True(screen.Scale >= 1);
        Assert.True(screen.Jpeg.Length > 1000);
        Console.WriteLine($"[live] {serial}: {screen.Width}x{screen.Height} scale={screen.Scale:F3} jpeg={screen.Jpeg.Length}B tree-lines={screen.UiTree.Split('\n').Length}");
        Console.WriteLine(string.Join("\n", screen.UiTree.Split('\n').Take(8)));
    }
}
