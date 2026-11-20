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
        Assert.Contains("• Open Set up mirroring", text);
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

/// <summary>Why a mirror click failed (scrcpy 5 output), where the window may go, and when to say "taking long".</summary>
public class MirrorDiagnosticsTests
{
    [Fact]
    public void ExtractError_PrefersTheSpecificErrorOverGenericFollowUps()
    {
        const string output = """
            scrcpy 5.0.1 <https://github.com/Genymobile/scrcpy>
            INFO: Using adb: C:\Program Files\H.I.B\tools\adb.exe
            INFO: Connecting to 10.107.0.205:41234...
            INFO: Connected to 10.107.0.205:41234
            [server] INFO: Device: [samsung] samsung SM-S948B (Android 16)
            ERROR: Could not create renderer: Failed to create D3D11 device
            ERROR: Server connection failed
            """;
        Assert.Equal("Could not create renderer: Failed to create D3D11 device", ScrcpyOutput.ExtractError(output));
    }

    [Fact]
    public void ExtractError_ReadsServerExceptionsAndAdbErrors()
    {
        const string server = "[server] INFO: Device: x\r\n[server] ERROR: Exception on thread Thread[video,5,main]\r\n"
            + "java.lang.IllegalArgumentException: Could not find a video encoder for h265\r\n"
            + "\tat com.genymobile.scrcpy.video.SurfaceEncoder.streamCapture(SurfaceEncoder.java:80)\r\n"
            + "ERROR: Demuxer error\r\nERROR: Server connection failed\r\n";
        Assert.Equal("Could not find a video encoder for h265", ScrcpyOutput.ExtractError(server));

        const string encoder = "[server] ERROR: Encoding error: android.media.MediaCodec$CodecException: Error 0xfffffff4\nERROR: Demuxer error";
        Assert.Equal("Encoding error: android.media.MediaCodec$CodecException: Error 0xfffffff4", ScrcpyOutput.ExtractError(encoder));

        const string adb = "INFO: Connecting to 10.107.0.205:41234...\nadb.exe: error: device unauthorized.\nThis adb server's $ADB_VENDOR_KEYS is not set\nERROR: Server connection failed";
        Assert.Equal("device unauthorized.", ScrcpyOutput.ExtractError(adb));
        Assert.True(ScrcpyOutput.IndicatesPairing(adb));

        Assert.Equal("Server connection failed", ScrcpyOutput.ExtractError("INFO: x\nERROR: Server connection failed"));
        Assert.Null(ScrcpyOutput.ExtractError("INFO: Renderer: direct3d11\nINFO: Texture: 1080x2340"));
        Assert.Null(ScrcpyOutput.ExtractError(""));
        Assert.False(ScrcpyOutput.IndicatesPairing("ERROR: Could not open video stream"));
    }

    [Fact]
    public void LastMeaningfulLine_SkipsStackFramesAndBlanks()
    {
        Assert.Equal("[server] INFO: Device: x", ScrcpyOutput.LastMeaningfulLine("INFO: a\n[server] INFO: Device: x\n\n  \n"));
        Assert.Equal("java.lang.X: y", ScrcpyOutput.LastMeaningfulLine("java.lang.X: y\n\tat a.b(c.java:1)\n\tat d.e(f.java:2)"));
        Assert.Null(ScrcpyOutput.LastMeaningfulLine(" \n"));
    }

    [Fact]
    public void WindowLines_MatchScrcpy5()
    {
        Assert.True(ScrcpyOutput.IsWindowShownLine("INFO: Texture: 1080x2340"));
        Assert.True(ScrcpyOutput.IsWindowShownLine("INFO: Texture (D3D11VA): 1080x2340"));
        Assert.False(ScrcpyOutput.IsWindowShownLine("INFO: Renderer: direct3d11"));
        Assert.False(ScrcpyOutput.IsWindowShownLine("[server] INFO: Device: x"));
        Assert.True(ScrcpyOutput.IsWindowCreatedLine("INFO: Renderer: direct3d11"));
        Assert.False(ScrcpyOutput.IsWindowCreatedLine("INFO: Texture: 1x1"));
    }

    static readonly ScreenArea Primary = new(0, 0, 1920, 1040);
    static readonly ScreenArea LeftMonitor = new(-1280, 0, 0, 984);

    [Fact]
    public void WindowPosition_IsClampedToAScreenThatExists()
    {
        // Comfortably on a screen: unchanged (also on a monitor left of the primary).
        Assert.Equal((300, 200), MirrorWindowPlacement.Clamp(300, 200, [Primary]));
        Assert.Equal((-1000, 100), MirrorWindowPlacement.Clamp(-1000, 100, [Primary, LeftMonitor]));
        // That monitor was unplugged: pulled onto the nearest one that exists.
        Assert.Equal((0, 100), MirrorWindowPlacement.Clamp(-1000, 100, [Primary]));
        // Past the right/bottom edge: keep enough on screen to grab the window.
        Assert.Equal((1920 - MirrorWindowPlacement.MinVisible, 1040 - MirrorWindowPlacement.MinVisible), MirrorWindowPlacement.Clamp(3000, 2000, [Primary]));
        // y = 0 would put the title bar above the screen.
        Assert.Equal((10, MirrorWindowPlacement.TitleBar), MirrorWindowPlacement.Clamp(10, 0, [Primary]));
        // No screen information: leave it alone.
        Assert.Equal((5000, 5000), MirrorWindowPlacement.Clamp(5000, 5000, []));
        Assert.Equal((5000, 5000), MirrorWindowPlacement.Clamp(5000, 5000, null));
    }

    [Fact]
    public void BuildArguments_ClampsTheSavedManualPosition()
    {
        var s = new MirrorSettings(new InMemorySettingsStore()) { ManualPosition = true, ManualPositionCoords = [2600, 300] };
        var args = MirrorService.BuildArguments(new MirrorRequest(MirrorKind.Mirror, "S26"), s, null, "a:1", [Primary]);
        Assert.Contains($"--window-x={1920 - MirrorWindowPlacement.MinVisible}", args);
        Assert.Contains("--window-y=300", args);
        // Without screens (tests, non-Windows) the saved value passes through.
        Assert.Contains("--window-x=2600", MirrorService.BuildArguments(new MirrorRequest(MirrorKind.Mirror, "S26"), s, null, "a:1"));
    }

    [Fact]
    public void Watchdog_WarnsOnceWhenNoWindowAfter12Seconds()
    {
        var at = MirrorWatchdog.SlowAfter;
        Assert.Equal(TimeSpan.FromSeconds(12), at);
        Assert.False(MirrorWatchdog.ShouldWarn(at - TimeSpan.FromMilliseconds(1), running: true, windowShown: false, alreadyWarned: false));
        Assert.True(MirrorWatchdog.ShouldWarn(at, running: true, windowShown: false, alreadyWarned: false));
        Assert.False(MirrorWatchdog.ShouldWarn(at * 2, running: true, windowShown: true, alreadyWarned: false));
        Assert.False(MirrorWatchdog.ShouldWarn(at * 2, running: false, windowShown: false, alreadyWarned: false));
        Assert.False(MirrorWatchdog.ShouldWarn(at * 2, running: true, windowShown: false, alreadyWarned: true));
    }

    [Fact]
    public void DescribeExit_GivesTheRealReasonNeverTheConsole()
    {
        var fail = MirrorService.DescribeExit(1, "INFO: Connecting to 1.2.3.4:5...\nERROR: Could not connect to 1.2.3.4:5\nERROR: Server connection failed", false, false);
        Assert.True(fail.IsProblem);
        Assert.Equal("Mirroring couldn't start", fail.Title);
        Assert.Equal("Could not connect to 1.2.3.4:5", fail.Reason);

        var silent = MirrorService.DescribeExit(1, "", false, false);
        Assert.True(silent.IsProblem);
        Assert.Contains("code 1", silent.Reason);
        Assert.DoesNotContain("console", silent.Reason, StringComparison.OrdinalIgnoreCase);

        var noWindow = MirrorService.DescribeExit(0, "INFO: Renderer: direct3d11", false, false);
        Assert.True(noWindow.IsProblem);
        Assert.Contains("Renderer", noWindow.Reason);

        Assert.False(MirrorService.DescribeExit(0, "INFO: Texture: 1x1", true, false).IsProblem); // user closed it
        Assert.False(MirrorService.DescribeExit(-1, "", false, stopRequested: true).IsProblem);  // H.I.B closed it
        Assert.Equal("The phone disconnected", MirrorService.DescribeExit(2, "INFO: Texture: 1x1", true, false).Title);

        var pair = MirrorService.DescribeExit(1, "error: device unauthorized.", false, false);
        Assert.True(pair.NeedsPairing);
        Assert.Equal("device unauthorized.", pair.Reason);
    }

    [Fact]
    public void ScrcpyAdbPath_NeverHasASpaceOnWindows()
    {
        var tools = Path.Combine(Path.GetTempPath(), "Program Files", "H.I.B", "tools");
        var spaced = Path.Combine(tools, "adb.exe");
        var scrcpy = Path.Combine(tools, "scrcpy.exe");
        Assert.Equal(@"C:\PROGRA~1\H.I.B\tools\adb.exe",
            AdbLocator.AdbEnvironment(spaced, scrcpy, _ => @"C:\PROGRA~1\H.I.B\tools\adb.exe")["ADB"]);
        // No 8.3 name: leave ADB unset so scrcpy runs the adb.exe next to itself.
        Assert.False(AdbLocator.AdbEnvironment(spaced, scrcpy, _ => null).ContainsKey("ADB"));
        // …but only when that adb really is next to scrcpy.
        Assert.Equal(spaced, AdbLocator.AdbEnvironment(spaced, Path.Combine(Path.GetTempPath(), "x", "scrcpy.exe"), _ => null)["ADB"]);
        Assert.Equal("/opt/tools/adb", AdbLocator.AdbEnvironment("/opt/tools/adb", null, _ => "unused")["ADB"]);
        Assert.Equal("/Users/a b/adb", AdbLocator.AdbEnvironment("/Users/a b/adb", null, null)["ADB"]); // not Windows
    }

    [Fact]
    public void ConnectionLoss_IsRecognised()
    {
        Assert.True(ScrcpyOutput.IndicatesAdbConnectionLost("INFO: Connecting to 10.107.0.205:41234...\nERROR: Could not connect to 10.107.0.205:41234"));
        Assert.True(ScrcpyOutput.IndicatesAdbConnectionLost("error: device '10.107.0.205:41234' not found"));
        Assert.True(ScrcpyOutput.IndicatesAdbConnectionLost("failed to connect to '10.107.0.205:41234': Connection refused"));
        Assert.False(ScrcpyOutput.IndicatesAdbConnectionLost("ERROR: Could not create renderer"));
    }

    [Fact]
    public void AdbExplain_IsTheRealConnectReason()
    {
        Assert.Contains("still running", AdbService.Explain(new AdbConnectResult(false, null, AdbService.BusyOutput, null, null)));
        var refused = new AdbConnectResult(false, null, "10.107.0.205:41234: failed to connect to '10.107.0.205:41234': connection refused",
            AdbService.Diagnosis(["connection refused"]), "x");
        var text = AdbService.Explain(refused);
        Assert.StartsWith("The phone refused the connection", text);
        Assert.Contains("adb said: 10.107.0.205:41234: failed to connect", text);
        Assert.Equal("cannot connect", AdbService.Explain(new AdbConnectResult(false, null, "\ncannot connect\n", null, null)));
        Assert.Equal("", AdbService.Explain(new AdbConnectResult(true, "a:1", "connected to a:1", null, null)));
    }
}

/// <summary>hib.log: one rotation at the size cap, tails across both files, and no keys ever.</summary>
public class LogFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hib-log-tests", Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Rotates_Once_AndTailsAcrossBothFiles()
    {
        var path = Path.Combine(_dir, "logs", "hib.log");
        using var log = new RollingLogFile(path, maxBytes: 4096);
        for (var i = 0; i < 200; i++) log.Write("scrcpy", $"line {i:000} INFO: something happened here");

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".1"));
        Assert.Equal(new[] { "hib.log", "hib.log.1" }, Directory.GetFiles(Path.GetDirectoryName(path)!).Select(f => Path.GetFileName(f)).Order().ToArray());
        Assert.True(new FileInfo(path).Length <= 4096);
        Assert.True(new FileInfo(path + ".1").Length <= 4096);
        Assert.Contains("line 199", File.ReadAllText(path));

        var tail = log.ReadTail(6000);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(tail) <= 6000);
        Assert.EndsWith("line 199 INFO: something happened here\n", tail);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} \[scrcpy\] line \d{3}", tail); // starts on a whole line
        var current = File.ReadAllText(path);
        Assert.True(tail.Length > current.Length);                                      // reached into hib.log.1
        Assert.Equal(current.Length, log.ReadTail(1_000_000).Length - File.ReadAllText(path + ".1").Length);
    }

    [Fact]
    public void Entries_AreStampedAndMultiLineIndented()
    {
        var text = RollingLogFile.Format(new DateTime(2026, 11, 20, 14, 3, 22, 123), "adb", "\nconnected to 1.2.3.4:5\r\nsecond\n");
        Assert.Equal("2026-11-20 14:03:22.123 [adb] connected to 1.2.3.4:5\n    second\n", text);
    }

    [Fact]
    public void Keys_AndTokens_NeverReachTheFile()
    {
        var pairingKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i * 13 + 1)).ToArray());
        const string gemini = "AIzaSyD-1234567890abcdefghijklmnopqrstuv";
        const string registered = "my-private-space-token-xyz";
        LogScrubber.AddSecret(registered);

        var path = Path.Combine(_dir, "hib.log");
        using (var log = new RollingLogFile(path))
        {
            log.Write("app", $"QR: airsync://10.0.0.2:6996?name=PC&key={pairingKey}");
            log.Write("app", $"{{\"type\":\"desktopsList\",\"data\":{{\"space\":{{\"key\":\"{pairingKey}\",\"v\":1}}}}}}");
            log.Write("trace", $"POST https://generativelanguage.googleapis.com/v1beta/models/x:generateContent?key={gemini}");
            log.Write("trace", $"x-goog-api-key: {gemini}");
            log.Write("trace", "Authorization: Bearer sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUV");
            log.Write("trace", "raw " + pairingKey + " and " + registered);
            log.Write("scrcpy", "--keyboard=uhid --max-size=1200; Space key changed; adb-R5GL2171V0J-kngrgU._adb-tls-connect._tcp");
        }
        var file = File.ReadAllText(path);
        Assert.DoesNotContain(pairingKey, file);
        Assert.DoesNotContain(gemini, file);
        Assert.DoesNotContain("AIza", file);
        Assert.DoesNotContain("sk-ant", file);
        Assert.DoesNotContain(registered, file);
        Assert.Contains("key=[redacted]", file);
        Assert.Contains("\"key\":\"[redacted]\"", file);
        // Ordinary diagnostics survive.
        Assert.Contains("--keyboard=uhid --max-size=1200; Space key changed; adb-R5GL2171V0J-kngrgU._adb-tls-connect._tcp", file);
        Assert.Contains("generativelanguage.googleapis.com", file);
        Assert.Equal("/Users/haithamhamid/Library/Application/Support/HIB/x", LogScrubber.Scrub("/Users/haithamhamid/Library/Application/Support/HIB/x"));
    }

    [Fact]
    public void HibLog_IsOffUntilStarted_AndFollowsHibDataDir()
    {
        Assert.False(HibLog.IsStarted); // tests never write the user's log
        Assert.Equal("", HibLog.Tail());
        Assert.Equal("", new RollingLogFile(Path.Combine(_dir, "missing.log")).ReadTail(1000));
        var old = Environment.GetEnvironmentVariable("HIB_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("HIB_DATA_DIR", _dir);
            Assert.Equal(Path.Combine(_dir, "logs", "hib.log"), HibLog.DefaultPath());
            Environment.SetEnvironmentVariable("HIB_DATA_DIR", null);
            var p = HibLog.DefaultPath();
            Assert.EndsWith(Path.Combine("H.I.B", "logs", "hib.log"), p);
        }
        finally { Environment.SetEnvironmentVariable("HIB_DATA_DIR", old); }
    }
}

/// <summary>
/// The whole scrcpy pipeline against a fake <c>scrcpy</c> script (never the real phone, never real adb):
/// the real error reaches MirrorExited, the window-open line is seen, the watchdog fires, a dead port is forgotten.
/// </summary>
public class MirrorProcessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hib-mirror-tests", Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private (MirrorService mirror, AdbService adb) Setup(string scrcpyBody)
    {
        Directory.CreateDirectory(_dir);
        void Script(string name, string body)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Script("adb", "echo 'fake adb' 1>&2; exit 1"); // must never be needed
        Script("scrcpy", scrcpyBody);
        var locator = new AdbLocator { BundledToolsDirectory = _dir };
        var adb = new AdbService(locator, new InMemorySettingsStore());
        adb.MarkConnected("10.0.0.5", 41000, "connected to 10.0.0.5:41000");
        return (new MirrorService(adb, new InMemorySettingsStore()), adb);
    }

    private static async Task<T> Within<T>(TaskCompletionSource<T> tcs, int seconds = 10)
        => await tcs.Task.WaitAsync(TimeSpan.FromSeconds(seconds));

    [Fact]
    public async Task Failure_ReportsScrcpysRealError_AndForgetsTheDeadPort()
    {
        if (OperatingSystem.IsWindows()) return;
        var (mirror, adb) = Setup("""
            echo "scrcpy 5.0.1 <https://github.com/Genymobile/scrcpy>"
            echo "INFO: Connecting to $(echo "$@" | sed 's/.*--tcpip=\([^ ]*\).*/\1/')..."
            echo "ERROR: Could not connect to 10.0.0.5:41000" 1>&2
            echo "ERROR: Server connection failed" 1>&2
            exit 1
            """);
        var exited = new TaskCompletionSource<MirrorExit>(TaskCreationOptions.RunContinuationsAsynchronously);
        mirror.MirrorExited += e => exited.TrySetResult(e);

        var session = await mirror.StartMirrorAsync("S26");
        Assert.NotNull(session);
        Assert.Contains("--tcpip=10.0.0.5:41000", session!.Arguments);
        var exit = await Within(exited);

        Assert.True(exit.IsProblem);
        Assert.Equal(1, exit.ExitCode);
        Assert.Equal("Mirroring couldn't start", exit.Title);
        Assert.Equal("Could not connect to 10.0.0.5:41000", exit.Reason);
        Assert.Equal(exit.Reason, mirror.LastError);
        Assert.Contains("INFO: Connecting to 10.0.0.5:41000...", exit.Output);
        Assert.False(adb.IsConnected); // the next click reconnects with fresh ports
        Assert.Empty(mirror.Sessions);
    }

    [Fact]
    public async Task WindowLine_RaisesWindowOpened_AndClosingByHibIsNotAProblem()
    {
        if (OperatingSystem.IsWindows()) return;
        var (mirror, adb) = Setup("""
            echo "INFO: Renderer: metal"
            echo "INFO: Texture: 1080x2340"
            exec sleep 30
            """);
        var opened = new TaskCompletionSource<MirrorSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<MirrorExit>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = false;
        mirror.MirrorWindowOpened += s => opened.TrySetResult(s);
        mirror.MirrorExited += e => exited.TrySetResult(e);
        mirror.MirrorSlowToOpen += _ => slow = true;

        var session = await mirror.StartMirrorAsync("S26");
        var shown = await Within(opened);
        Assert.Same(session, shown);
        Assert.True(shown.WindowCreated);
        Assert.True(shown.WindowShown);
        Assert.True(mirror.IsMirroring);
        mirror.SlowAfter = TimeSpan.FromMilliseconds(50); // the window is up: no "taking long"
        await Task.Delay(600);
        Assert.False(slow);

        mirror.Stop(session!);
        var exit = await Within(exited);
        Assert.False(exit.IsProblem);
        Assert.True(adb.IsConnected);
    }

    [Fact]
    public async Task NoWindow_WarnsTakingLong_WithTheLastLine()
    {
        if (OperatingSystem.IsWindows()) return;
        var (mirror, _) = Setup("""
            echo "INFO: Connecting to 10.0.0.5:41000..."
            echo "[server] INFO: Device: [samsung] samsung SM-S948B (Android 16)"
            exec sleep 30
            """);
        var slow = new TaskCompletionSource<MirrorSlow>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<MirrorExit>(TaskCreationOptions.RunContinuationsAsynchronously);
        mirror.MirrorSlowToOpen += e => slow.TrySetResult(e);
        mirror.MirrorExited += e => exited.TrySetResult(e);
        mirror.SlowAfter = TimeSpan.FromHours(1);

        var session = await mirror.StartMirrorAsync("S26");
        Assert.NotNull(session);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!session!.OutputText.Contains("[server] INFO: Device") && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.False(slow.Task.IsCompleted);
        mirror.SlowAfter = TimeSpan.FromMilliseconds(100); // "12 s later"
        var warn = await Within(slow);
        Assert.Same(session, warn.Session);
        Assert.Equal("[server] INFO: Device: [samsung] samsung SM-S948B (Android 16)", warn.LastLine);
        mirror.StopAll();
        Assert.False((await Within(exited)).IsProblem);
    }
}
