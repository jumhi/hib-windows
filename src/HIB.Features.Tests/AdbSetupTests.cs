using System.Text;
using System.Text.Json;
using HIB.Features.Abstractions;
using HIB.Features.Adb;

namespace HIB.Features.Tests;

/// <summary>Answers adb commands for the tests: nothing runs, nothing touches a phone.</summary>
internal sealed class FakeAdb
{
    private readonly List<string> _commands = new();
    public Func<IReadOnlyList<string>, ProcessResult> Handler { get; set; } = _ => Ok("");
    public IReadOnlyList<string> Commands { get { lock (_commands) return _commands.ToList(); } }

    public static ProcessResult Ok(string stdout) => new(0, Encoding.UTF8.GetBytes(stdout), "", false);
    public static ProcessResult Err(string stderr, int code = 1) => new(code, [], stderr, false);

    public AdbService Service()
    {
        var adb = new AdbService(settings: new InMemorySettingsStore())
        {
            CommandRunner = (args, _) =>
            {
                lock (_commands) _commands.Add(string.Join(' ', args));
                return Task.FromResult(Handler(args));
            },
        };
        return adb;
    }
}

public class AdbSetupStateTests
{
    static AdbSetupState? Parse(string json) => AdbSetupState.Parse(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Parses_the_phone_report()
    {
        Assert.Equal(new AdbSetupState(true, false, 42789, null, null),
            Parse("""{"developerOptions":true,"wirelessDebugging":false,"connectPort":42789,"pairingPort":null,"pairingName":null}"""));
        Assert.Equal(new AdbSetupState(true, true, 42789, 37001, "adb-wireless-123456"),
            Parse("""{"developerOptions":true,"wirelessDebugging":true,"connectPort":42789,"pairingPort":37001,"pairingName":"adb-wireless-123456"}"""));
    }

    [Fact]
    public void Parsing_is_tolerant()
    {
        // Strings for numbers and booleans, 0 / out-of-range ports, blank names, missing fields.
        Assert.Equal(new AdbSetupState(true, true, 40211, null, null),
            Parse("""{"developerOptions":"true","wirelessDebugging":1,"connectPort":"40211","pairingPort":0,"pairingName":"  "}"""));
        Assert.Equal(new AdbSetupState(false, false, null, null, null), Parse("{}"));
        Assert.Null(Parse("[]"));
        Assert.Null(Parse("""{"connectPort":70000}""")!.ConnectPort);
    }

    [Theory]
    // phone, adb, dev, wifi, connect, pairPort, oldApp, busy, needsPair → step
    [InlineData(true, true, false, false, null, null, false, false, false, AdbSetupStep.Ready)]
    [InlineData(false, false, true, true, 1, null, false, false, false, AdbSetupStep.Phone)]
    [InlineData(true, false, null, null, null, null, false, false, false, AdbSetupStep.Checking)]
    [InlineData(true, false, null, null, null, null, true, false, false, AdbSetupStep.Pair)]
    [InlineData(true, false, false, false, null, null, false, false, false, AdbSetupStep.DeveloperOptions)]
    [InlineData(true, false, true, false, null, null, false, false, false, AdbSetupStep.WirelessDebugging)]
    [InlineData(true, false, true, true, 42789, null, false, false, false, AdbSetupStep.Connecting)]
    [InlineData(true, false, true, true, 42789, null, false, false, true, AdbSetupStep.Pair)]
    [InlineData(true, false, true, true, 42789, 37001, false, false, false, AdbSetupStep.Pair)]
    [InlineData(true, false, true, true, 42789, 37001, false, true, true, AdbSetupStep.Connecting)]
    public void The_card_shows_the_right_step(bool phone, bool adb, bool? dev, bool? wifi, int? connect, int? pairPort, bool oldApp, bool busy, bool needsPair, AdbSetupStep expected)
    {
        var state = dev is null ? null : new AdbSetupState(dev.Value, wifi ?? false, connect, pairPort, pairPort is null ? null : "x");
        Assert.Equal(expected, AdbSetupFlow.StepFor(phone, adb, state, oldApp, busy, needsPair));
    }

    [Fact]
    public void Ticks_follow_the_report()
    {
        Assert.False(AdbSetupFlow.DeveloperDone(null, false));
        Assert.True(AdbSetupFlow.DeveloperDone(new(true, false, null, null, null), false));
        Assert.False(AdbSetupFlow.WirelessDone(new(true, false, null, null, null), false));
        Assert.True(AdbSetupFlow.WirelessDone(new(true, true, null, null, null), false));
        Assert.True(AdbSetupFlow.WirelessDone(null, adbConnected: true));
    }

    [Fact]
    public void ConnectPort_beats_stale_adbPorts_and_off_means_no_ports()
    {
        string[] stale = ["37145", "40211"];
        Assert.Equal(["42789"], AdbSetupFlow.PortsToTry(new(true, true, 42789, null, null), stale));
        Assert.Empty(AdbSetupFlow.PortsToTry(new(true, false, 42789, null, null), stale));
        Assert.Equal(stale, AdbSetupFlow.PortsToTry(new(true, true, null, null, null), stale));
        Assert.Equal(stale, AdbSetupFlow.PortsToTry(null, stale)); // older phone app
    }

    [Fact]
    public void Service_names_match_like_nsd()
    {
        Assert.True(AdbSetupFlow.SameServiceName("adb-wireless-123456", "adb-wireless-123456"));
        Assert.True(AdbSetupFlow.SameServiceName("adb-wireless-123456 (2)", "adb-wireless-123456"));
        Assert.False(AdbSetupFlow.SameServiceName("adb-R5GL-xyz", "adb-wireless-123456"));
        Assert.False(AdbSetupFlow.SameServiceName(null, "adb-wireless-123456"));
        Assert.True(AdbSetupFlow.MeansNotPaired("failed to authenticate to 10.107.0.205:42789"));
        Assert.True(AdbSetupFlow.MeansNotPaired("failed to connect to '10.107.0.205:42789': Connection refused"));
        Assert.False(AdbSetupFlow.MeansNotPaired("failed to connect to '10.107.0.205:42789': No route to host"));
    }
}

public class AdbPairingFlowTests
{
    const string PhoneIp = "10.107.0.205";

    static (FakeAdb fake, AdbService adb, AdbPairingService pairing) Setup()
    {
        var fake = new FakeAdb();
        fake.Handler = args => args[0] switch
        {
            "pair" => FakeAdb.Ok($"Successfully paired to {args[1]} [guid=adb-R5GL2171V0J-kngrgU]"),
            "connect" => FakeAdb.Ok($"connected to {args[1]}"),
            _ => FakeAdb.Ok(""),
        };
        var adb = fake.Service();
        var pairing = new AdbPairingService(adb) { PhoneIp = PhoneIp, ConnectPortWait = TimeSpan.FromSeconds(3), PairingPortWait = TimeSpan.FromSeconds(3) };
        return (fake, adb, pairing);
    }

    static async Task Until(Func<bool> cond, int ms = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond() && DateTime.UtcNow < end) await Task.Delay(20);
        Assert.True(cond());
    }

    [Fact]
    public async Task Qr_pairs_as_soon_as_the_phone_reports_our_service_name_then_connects()
    {
        var (fake, adb, pairing) = Setup();
        pairing.UpdateSetupState(new AdbSetupState(true, true, 42789, null, null));
        var run = pairing.StartPairingAsync();
        await Until(() => pairing.QrText.Length > 0);
        Assert.Equal($"WIFI:T:ADB;S:{pairing.ServiceName};P:{pairing.Password};;", pairing.QrText);
        Assert.Equal(AdbPairingPhase.WaitingForPhone, pairing.Phase);

        // Another pairing service (the code screen, someone else's QR): ignored.
        pairing.UpdateSetupState(new AdbSetupState(true, true, 42789, 39999, "adb-R5GL-other"));
        await Task.Delay(150);
        Assert.DoesNotContain(fake.Commands, c => c.StartsWith("pair"));

        // Our name: adb pair right away at the reported port, then connect on connectPort.
        pairing.UpdateSetupState(new AdbSetupState(true, true, 42789, 37001, pairing.ServiceName));
        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains($"pair {PhoneIp}:37001 {pairing.Password}", fake.Commands);
        Assert.Contains($"connect {PhoneIp}:42789", fake.Commands);
        Assert.True(adb.IsConnected);
        Assert.Equal((PhoneIp, 42789), (adb.ConnectedIp, adb.Port));
        Assert.Equal(AdbPairingPhase.Connected, pairing.Phase);
        // The phone reported everything: mDNS never ran.
        Assert.DoesNotContain(fake.Commands, c => c.StartsWith("mdns"));
    }

    [Fact]
    public async Task Code_flow_uses_the_reported_pairing_port_and_only_needs_six_digits()
    {
        var (fake, adb, pairing) = Setup();
        Assert.False(await pairing.PairWithCodeAsync("12 34"));
        Assert.Empty(fake.Commands);

        pairing.UpdateSetupState(new AdbSetupState(true, true, 42789, 41555, "adb-R5GL2171V0J-kngrgU"));
        Assert.True(await pairing.PairWithCodeAsync("482 913"));
        Assert.Equal([$"pair {PhoneIp}:41555 482913", $"connect {PhoneIp}:42789"], fake.Commands);
        Assert.True(adb.IsConnected);
    }

    [Fact]
    public async Task Code_flow_waits_for_the_phone_to_open_its_pairing_code_screen()
    {
        var (fake, _, pairing) = Setup();
        pairing.UpdateSetupState(new AdbSetupState(true, true, 42789, null, null));
        var run = pairing.PairWithCodeAsync("123456");
        await Task.Delay(200);
        Assert.Empty(fake.Commands);
        pairing.UpdateSetupState(new AdbSetupState(true, true, 42789, 38000, "adb-x"));
        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal($"pair {PhoneIp}:38000 123456", fake.Commands[0]);
    }

    [Fact]
    public async Task Connect_waits_for_the_port_the_phone_reports_after_pairing()
    {
        var (fake, adb, pairing) = Setup();
        pairing.UpdateSetupState(new AdbSetupState(true, true, null, 41555, "adb-x"));
        var run = pairing.PairWithCodeAsync("123456");
        await Until(() => fake.Commands.Any(c => c.StartsWith("pair")));
        await Task.Delay(150);
        Assert.DoesNotContain(fake.Commands, c => c.StartsWith("connect"));
        pairing.UpdateSetupState(new AdbSetupState(true, true, 43111, null, null));
        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains($"connect {PhoneIp}:43111", fake.Commands);
        Assert.Equal(43111, adb.Port);
    }

    [Fact]
    public async Task Old_phone_app_without_adbSetupState_falls_back_to_mdns()
    {
        var (fake, adb, pairing) = Setup();
        string? name = null;
        fake.Handler = args => (args[0], args.Count > 1 ? args[1] : "") switch
        {
            ("mdns", _) when name is not null => FakeAdb.Ok(
                $"List of discovered mdns services\n{name}\t_adb-tls-pairing._tcp\t192.168.1.33:37001\nadb-R5GL-x\t_adb-tls-connect._tcp\t192.168.1.33:40211\n"),
            ("mdns", _) => FakeAdb.Ok("List of discovered mdns services\n"),
            ("pair", var a) => FakeAdb.Ok($"Successfully paired to {a}"),
            ("connect", var a) => FakeAdb.Ok($"connected to {a}"),
            _ => FakeAdb.Ok(""),
        };
        Assert.Null(pairing.SetupState);
        var run = pairing.StartPairingAsync();
        await Until(() => fake.Commands.Any(c => c == "mdns services"));
        name = pairing.ServiceName; // the phone scanned the QR
        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains($"pair 192.168.1.33:37001 {pairing.Password}", fake.Commands);
        Assert.Contains("connect 192.168.1.33:40211", fake.Commands);
        Assert.True(adb.IsConnected);
    }

    [Fact]
    public async Task A_wrong_code_says_so()
    {
        var (fake, adb, pairing) = Setup();
        fake.Handler = args => args[0] == "pair" ? FakeAdb.Ok("Failed: Wrong password or connection was dropped.") : FakeAdb.Ok("");
        pairing.UpdateSetupState(new AdbSetupState(true, true, 42789, 41555, "adb-x"));
        Assert.False(await pairing.PairWithCodeAsync("000000"));
        Assert.Contains("code didn't work", pairing.Status);
        Assert.DoesNotContain(fake.Commands, c => c.StartsWith("connect"));
        Assert.False(adb.IsConnected);
        Assert.DoesNotContain("000000", adb.ConsoleLog); // the code isn't kept in the console
    }
}

public class AdbSetupSessionTests
{
    const string PhoneIp = "10.107.0.205";

    sealed class Rig
    {
        public readonly FakeAdb Fake = new();
        public readonly AdbService Adb;
        public readonly AdbPairingService Pairing;
        public readonly List<(string? Open, bool Watch)> Requests = new();
        public bool PhoneConnected = true;
        public bool Paired;
        public readonly AdbSetupSession Session;

        public Rig()
        {
            Fake.Handler = args => args[0] switch
            {
                "connect" when Paired => FakeAdb.Ok($"connected to {args[1]}"),
                "connect" => FakeAdb.Ok($"failed to authenticate to {args[1]}"),
                "pair" => Pair(args[1]),
                _ => FakeAdb.Ok(""),
            };
            Adb = Fake.Service();
            Pairing = new AdbPairingService(Adb) { PhoneIp = PhoneIp, ConnectPortWait = TimeSpan.FromSeconds(3) };
            Session = new AdbSetupSession(Adb, Pairing, (open, watch) => { lock (Requests) Requests.Add((open, watch)); return Task.CompletedTask; },
                () => PhoneConnected) { OldAppAfter = TimeSpan.FromSeconds(30) };
        }

        ProcessResult Pair(string address)
        {
            Paired = true;
            return FakeAdb.Ok($"Successfully paired to {address}");
        }
    }

    static async Task Until(Func<bool> cond, int ms = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond() && DateTime.UtcNow < end) await Task.Delay(20);
        Assert.True(cond());
    }

    [Fact]
    public async Task Walks_from_developer_options_to_ready_as_the_phone_reports()
    {
        var r = new Rig();
        using var session = r.Session;
        await session.StartAsync();
        Assert.Equal((null, true), r.Requests[0]);  // watch: the phone reports within ~1 s
        Assert.Equal(AdbSetupStep.Checking, session.Step);

        r.Pairing.UpdateSetupState(new AdbSetupState(false, false, null, null, null));
        Assert.Equal(AdbSetupStep.DeveloperOptions, session.Step);
        await session.OpenOnPhoneAsync("developer");
        Assert.Equal(("developer", true), r.Requests[^1]);

        r.Pairing.UpdateSetupState(new AdbSetupState(true, false, null, null, null));
        Assert.Equal(AdbSetupStep.WirelessDebugging, session.Step);
        Assert.True(session.DeveloperDone);
        Assert.False(session.WirelessDone);
        await session.OpenOnPhoneAsync("wireless");
        Assert.Equal(("wireless", true), r.Requests[^1]);

        // Wireless debugging on: try its port; "failed to authenticate" → this PC isn't paired → the QR.
        r.Pairing.UpdateSetupState(new AdbSetupState(true, true, 42789, null, null));
        await Until(() => session.Step == AdbSetupStep.Pair && r.Pairing.QrText.Length > 0);
        Assert.Contains($"connect {PhoneIp}:42789", r.Fake.Commands);
        Assert.True(session.WirelessDone);
        Assert.False(session.PairedDone);

        // The phone scanned it: pair, then connect by itself.
        r.Pairing.UpdateSetupState(new AdbSetupState(true, true, 42789, 37001, r.Pairing.ServiceName));
        await Until(() => session.Step == AdbSetupStep.Ready);
        Assert.Contains($"pair {PhoneIp}:37001 {r.Pairing.Password}", r.Fake.Commands);
        Assert.Equal($"connect {PhoneIp}:42789", r.Fake.Commands[^1]);
        Assert.True(session.PairedDone);
        Assert.True(r.Adb.IsConnected);
        Assert.DoesNotContain(r.Fake.Commands, c => c.StartsWith("mdns"));

        session.Dispose();
        Assert.Equal((null, false), r.Requests[^1]); // stop the fast reporting
    }

    [Fact]
    public async Task Already_paired_connects_straight_away()
    {
        var r = new Rig { Paired = true };
        using var session = r.Session;
        await session.StartAsync();
        r.Pairing.UpdateSetupState(new AdbSetupState(true, true, 42789, null, null));
        await Until(() => session.Step == AdbSetupStep.Ready);
        Assert.Equal([$"connect {PhoneIp}:42789"], r.Fake.Commands);
        Assert.Equal("", r.Pairing.QrText); // no QR needed
    }

    [Fact]
    public async Task Code_mode_pairs_with_the_typed_digits()
    {
        var r = new Rig();
        using var session = r.Session;
        await session.StartAsync();
        r.Pairing.UpdateSetupState(new AdbSetupState(true, true, 42789, null, null));
        await Until(() => session.Step == AdbSetupStep.Pair);
        session.UseCode(true);
        Assert.True(session.CodeMode);
        Assert.False(session.PhoneShowsPairingCode);
        r.Pairing.UpdateSetupState(new AdbSetupState(true, true, 42789, 41555, "adb-R5GL-x"));
        Assert.True(session.PhoneShowsPairingCode);
        Assert.True(await session.SubmitCodeAsync("654321"));
        await Until(() => session.Step == AdbSetupStep.Ready);
        Assert.Contains($"pair {PhoneIp}:41555 654321", r.Fake.Commands);
    }

    [Fact]
    public async Task No_phone_and_old_phone_app()
    {
        var r = new Rig { PhoneConnected = false };
        using var session = r.Session;
        await session.StartAsync();
        Assert.Equal(AdbSetupStep.Phone, session.Step);

        var old = new Rig();
        using var oldSession = old.Session;
        oldSession.OldAppAfter = TimeSpan.Zero; // it never sends adbSetupState
        await oldSession.StartAsync();
        Assert.Equal(AdbSetupStep.Pair, oldSession.Step);
        Assert.True(oldSession.OldPhoneApp);
        await Until(() => old.Fake.Commands.Contains("mdns services")); // the QR is found the old way
    }
}

/// <summary>SPACE.md §16.3: the app-ops granter also lets the phone read its own log and show its 1×1 overlay.</summary>
public class AppOpsGranterTests
{
    [Fact]
    public async Task Grants_clipboard_logs_and_overlay_best_effort_and_logs_each()
    {
        var fake = new FakeAdb();
        var adb = fake.Service();
        adb.MarkConnected("10.0.0.5", 41000, "ok");
        var granter = new AppOpsGranter(adb);
        var log = new List<string>();
        granter.Log += log.Add;
        // READ_LOGS fails (an older Android): the others still run.
        fake.Handler = a => string.Join(' ', a).Contains("READ_LOGS") ? FakeAdb.Err("not a changeable permission") : FakeAdb.Ok("");

        Assert.True(await granter.GrantAsync()); // READ_CLIPBOARD is what decides
        Assert.Equal(new[]
        {
            "-s 10.0.0.5:41000 shell appops set com.sameerasw.airsync READ_CLIPBOARD allow",
            "-s 10.0.0.5:41000 shell pm grant com.sameerasw.airsync android.permission.READ_LOGS",
            "-s 10.0.0.5:41000 shell appops set com.sameerasw.airsync SYSTEM_ALERT_WINDOW allow",
        }, fake.Commands);
        Assert.Equal(3, log.Count);
        Assert.All(log, l => Assert.StartsWith("[clip] ", l));
        Assert.Contains(log, l => l.Contains("READ_LOGS") && l.Contains("failed"));
        Assert.Equal(3, AppOpsGranter.Commands.Count);
    }
}
