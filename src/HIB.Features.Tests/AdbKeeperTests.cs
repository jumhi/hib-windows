using HIB.Features.Abstractions;
using HIB.Features.Adb;

namespace HIB.Features.Tests;

/// <summary>ADB that stays connected (docs/SPACE.md §13): the keeper's state machine against a fake adb and a fake clock.</summary>
public class AdbKeeperTests
{
    const string Ip = "10.0.0.5";

    sealed class Rig
    {
        public readonly FakeAdb Fake = new();
        public readonly AdbService Adb;
        public readonly InMemorySettingsStore Settings = new();
        public DateTime Now = new(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc);
        public bool Link = true;
        public bool Enabled = true;
        public AdbSetupState? Setup = new(true, true, 42789, null, null);
        public List<string> DevicePorts = new();
        public int AskedForPorts;
        public readonly AdbKeeper Keeper;
        /// <summary>Ports that accept <c>adb connect</c>; anything else gets <see cref="Refusal"/>.</summary>
        public HashSet<int> Open = new();
        public string Refusal = "failed to connect to '10.0.0.5:42789': Connection timed out";
        public bool GetStateOk = true;

        public Rig()
        {
            Adb = Fake.Service();
            Adb.ConnectPause = TimeSpan.Zero;
            Fake.Handler = args =>
            {
                if (args[0] == "connect")
                {
                    var port = int.Parse(args[1].Split(':')[1]);
                    return Open.Contains(port) ? FakeAdb.Ok($"connected to {args[1]}") : FakeAdb.Ok(Refusal);
                }
                if (args.Count >= 3 && args[2] == "get-state") return GetStateOk ? FakeAdb.Ok("device\n") : FakeAdb.Err("error: device not found");
                return FakeAdb.Ok("");
            };
            Keeper = new AdbKeeper(Adb, new AdbKeeperContext
            {
                Enabled = () => Enabled,
                LinkUp = () => Link,
                PhoneIp = () => Ip,
                PhoneKey = () => "Haitham's S26 Ultra",
                Setup = () => Setup,
                DevicePorts = () => DevicePorts,
                AskForPorts = () => { AskedForPorts++; return Task.CompletedTask; },
            }, Settings, () => Now);
        }

        public List<string> Connects => Fake.Commands.Where(c => c.StartsWith("connect ")).ToList();
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
        public async Task At(double seconds) { Advance(seconds); await Keeper.TickAsync(); }
    }

    [Fact]
    public async Task Never_set_up_and_wireless_debugging_off_means_nothing_runs()
    {
        var r = new Rig { Setup = new AdbSetupState(true, false, null, null, null) };
        await r.Keeper.TriggerAsync(AdbKeeperTrigger.LinkUp);
        await r.At(2);
        await r.At(60);
        Assert.False(r.Keeper.Wanted);
        Assert.Equal(AdbKeeperState.Off, r.Keeper.State);
        Assert.Equal("", r.Keeper.Status);
        Assert.Empty(r.Fake.Commands);
    }

    [Fact]
    public async Task Link_up_connects_to_the_phones_port_quietly_and_remembers_it()
    {
        var r = new Rig();
        r.Open.Add(42789);
        var toasts = 0;
        r.Adb.ConnectionFailed += _ => toasts++;
        await r.Keeper.TriggerAsync(AdbKeeperTrigger.LinkUp);
        await r.Keeper.TickAsync();
        Assert.Empty(r.Connects); // waits a moment for the phone's reports
        await r.At(AdbKeeper.LinkUpGrace.TotalSeconds);
        Assert.Equal(["connect 10.0.0.5:42789"], r.Connects);
        Assert.True(r.Adb.IsConnected);
        Assert.Equal(AdbKeeperState.Connected, r.Keeper.State);
        Assert.Equal("ADB · connected", r.Keeper.Status);
        Assert.True(r.Keeper.EverWorked);
        Assert.Equal([42789], r.Keeper.Remembered);
        Assert.Equal(0, toasts);
    }

    [Fact]
    public async Task Retries_every_10_s_backing_off_to_60_s_without_toasts()
    {
        var r = new Rig();
        var toasts = 0;
        r.Adb.ConnectionFailed += _ => toasts++;
        await r.Keeper.TriggerAsync(AdbKeeperTrigger.ConnectPort);
        Assert.Single(r.Connects);
        Assert.Equal(AdbKeeperState.Reconnecting, r.Keeper.State);
        Assert.Equal("ADB · reconnecting…", r.Keeper.Status);

        // The gaps between attempts: 10, 20, 40, 60, 60 s.
        foreach (var gap in new[] { 10, 20, 40, 60, 60 })
        {
            var before = r.Connects.Count;
            await r.At(gap - 1);
            Assert.Equal(before, r.Connects.Count); // not yet
            await r.At(1);
            Assert.Equal(before + 1, r.Connects.Count);
        }
        Assert.Equal(AdbKeeper.MaxRetry, r.Keeper.CurrentDelay);
        Assert.Equal(0, toasts);
        Assert.NotNull(r.Keeper.Detail);
    }

    [Theory]
    [InlineData(AdbKeeperTrigger.Network)]
    [InlineData(AdbKeeperTrigger.ConnectPort)]
    [InlineData(AdbKeeperTrigger.Ports)]
    [InlineData(AdbKeeperTrigger.Manual)]
    public async Task Any_trigger_resets_the_backoff_and_tries_at_once(AdbKeeperTrigger trigger)
    {
        var r = new Rig();
        await r.Keeper.TriggerAsync(AdbKeeperTrigger.ConnectPort);
        await r.At(10);
        await r.At(20);
        Assert.Equal(3, r.Connects.Count);
        Assert.Equal(TimeSpan.FromSeconds(60), r.Keeper.CurrentDelay);
        await r.At(5); // mid-wait
        await r.Keeper.TriggerAsync(trigger);
        Assert.Equal(4, r.Connects.Count);
        Assert.Equal(TimeSpan.FromSeconds(20), r.Keeper.CurrentDelay); // reset to 10, then one failure
        await r.At(10);
        Assert.Equal(5, r.Connects.Count);
    }

    [Fact]
    public async Task A_network_change_waits_for_things_to_settle_then_triggers()
    {
        var r = new Rig();
        await r.Keeper.TriggerAsync(AdbKeeperTrigger.ConnectPort);
        await r.At(10);
        await r.At(20); // next try due in 40 s
        var before = r.Connects.Count;
        r.Keeper.NetworkChanged();
        r.Keeper.NetworkChanged(); // events come in bursts
        await r.At(1);
        Assert.Equal(before, r.Connects.Count);
        await r.At(AdbKeeper.NetworkSettle.TotalSeconds);
        Assert.Equal(before + 1, r.Connects.Count);
    }

    [Fact]
    public async Task A_new_connect_port_goes_first_then_the_remembered_ones()
    {
        var r = new Rig();
        r.Open.Add(42789);
        await r.Keeper.TriggerAsync(AdbKeeperTrigger.ConnectPort);
        Assert.True(r.Adb.IsConnected);

        // Wireless debugging restarted on the phone: a new port. The old connection no longer answers.
        r.Open.Clear();
        r.Open.Add(40111);
        r.GetStateOk = false;
        r.Setup = new AdbSetupState(true, true, 40111, null, null);
        await r.Keeper.TriggerAsync(AdbKeeperTrigger.ConnectPort);
        Assert.Contains("-s 10.0.0.5:42789 get-state", r.Fake.Commands);
        Assert.Equal("connect 10.0.0.5:40111", r.Connects[^1]);
        Assert.True(r.Adb.IsConnected);
        Assert.Equal(40111, r.Adb.Port);
        Assert.Equal([40111, 42789], r.Keeper.Remembered);

        Assert.Equal(["5555", "40111", "42789"], AdbKeeper.PortsFor(new AdbSetupState(true, true, 5555, null, null), ["1234"], [40111, 42789, 5555]));
        Assert.Equal(["1234", "40111"], AdbKeeper.PortsFor(null, ["1234", "x"], [40111]));
        Assert.Empty(AdbKeeper.PortsFor(new AdbSetupState(true, false, 5555, null, null), ["1234"], [40111]));
    }

    [Fact]
    public async Task Remembered_ports_are_tried_when_the_phone_reports_none()
    {
        var r = new Rig();
        r.Open.Add(42789);
        await r.Keeper.TriggerAsync(AdbKeeperTrigger.ConnectPort);
        r.Adb.MarkLost("test");
        // An older phone app: no adbSetupState, no ports in the device message. The port that worked is tried.
        r.Setup = null;
        r.DevicePorts.Clear();
        Assert.True(r.Keeper.Wanted); // ADB worked with this phone before
        await r.Keeper.TriggerAsync(AdbKeeperTrigger.Network);
        Assert.Equal("connect 10.0.0.5:42789", r.Connects[^1]);
        Assert.True(r.Adb.IsConnected);
    }

    [Theory]
    [InlineData("failed to connect to '10.0.0.5:42789': No route to host")]
    [InlineData("cannot connect to 10.0.0.5:42789: No connection could be made because the target machine actively refused it. (10061)")]
    [InlineData("cannot connect to 10.0.0.5:42789: A socket operation was attempted to an unreachable host. (10065)")]
    public async Task No_route_while_the_phone_answers_restarts_the_adb_server_once(string refusal)
    {
        var r = new Rig { Refusal = refusal };
        r.Fake.Handler = Wrap(r, after: args =>
        {
            // The fresh server (started by H.I.B) reaches the phone.
            if (args[0] == "start-server") r.Open.Add(42789);
        });
        await r.Keeper.TriggerAsync(AdbKeeperTrigger.ConnectPort);
        var cmds = r.Fake.Commands;
        Assert.Equal(["connect 10.0.0.5:42789", "kill-server", "start-server", "connect 10.0.0.5:42789"], cmds);
        Assert.True(r.Adb.IsConnected);
        Assert.Equal(1, r.Keeper.ServerRestarts);
    }

    [Fact]
    public async Task The_server_is_restarted_only_once_per_outage()
    {
        var r = new Rig { Refusal = "failed to connect to '10.0.0.5:42789': No route to host" };
        await r.Keeper.TriggerAsync(AdbKeeperTrigger.ConnectPort);
        await r.At(10);
        await r.At(20);
        await r.At(40);
        Assert.Equal(1, r.Fake.Commands.Count(c => c == "kill-server"));
        Assert.Equal(1, r.Keeper.ServerRestarts);
        Assert.Equal(5, r.Connects.Count); // 1 + the retry after the restart + 3 backoff tries

        // A new network is a new outage: one more restart is allowed.
        await r.Keeper.TriggerAsync(AdbKeeperTrigger.Network);
        Assert.Equal(2, r.Keeper.ServerRestarts);
    }

    [Fact]
    public async Task Other_failures_never_restart_the_server()
    {
        var r = new Rig { Refusal = "failed to authenticate to 10.0.0.5:42789" };
        await r.Keeper.TriggerAsync(AdbKeeperTrigger.ConnectPort);
        await r.At(10);
        Assert.DoesNotContain("kill-server", r.Fake.Commands);
        Assert.Equal(0, r.Keeper.ServerRestarts);
        Assert.Contains("paired", r.Keeper.Detail);
    }

    [Fact]
    public async Task Link_down_user_disconnect_or_preference_off_stop_the_keeper()
    {
        var r = new Rig();
        r.Open.Add(42789);
        await r.Keeper.TriggerAsync(AdbKeeperTrigger.ConnectPort);
        Assert.Equal(AdbKeeperState.Connected, r.Keeper.State);

        await r.Adb.DisconnectAsync(userInitiated: true);
        var before = r.Connects.Count;
        await r.At(10);
        await r.At(60);
        Assert.Equal(before, r.Connects.Count);
        Assert.Equal(AdbKeeperState.Off, r.Keeper.State);

        // Connect (the manual nudge) clears it, and the keeper takes over again.
        var result = await r.Keeper.ConnectNowAsync();
        Assert.True(result!.Success);
        r.Adb.MarkLost("phone walked away");
        r.Link = false;
        await r.At(60);
        Assert.Equal(AdbKeeperState.Off, r.Keeper.State);
        r.Link = true;
        r.Enabled = false;
        await r.At(60);
        Assert.Equal(AdbKeeperState.Off, r.Keeper.State);
        Assert.Equal(before + 1, r.Connects.Count);
    }

    [Fact]
    public async Task While_connected_a_health_check_catches_a_dead_connection()
    {
        var r = new Rig();
        r.Open.Add(42789);
        await r.Keeper.TriggerAsync(AdbKeeperTrigger.ConnectPort);
        await r.At(30);
        Assert.DoesNotContain(r.Fake.Commands, c => c.Contains("get-state"));
        await r.At(30);
        Assert.Contains("-s 10.0.0.5:42789 get-state", r.Fake.Commands);
        Assert.True(r.Adb.IsConnected);

        r.GetStateOk = false;
        r.Open.Clear();
        await r.At(60);
        Assert.False(r.Adb.IsConnected);
        Assert.Equal(AdbKeeperState.Reconnecting, r.Keeper.State);
        Assert.Equal(2, r.Connects.Count); // reconnect tried at once
    }

    [Fact]
    public async Task An_older_phone_app_is_asked_for_fresh_ports_after_a_failure()
    {
        var r = new Rig { Setup = null };
        r.DevicePorts.Add("37000");
        Assert.True(r.Keeper.Wanted); // the phone reports ports: Wireless debugging is on
        await r.Keeper.TriggerAsync(AdbKeeperTrigger.Ports);
        Assert.Equal("connect 10.0.0.5:37000", Assert.Single(r.Connects));
        Assert.Equal(1, r.AskedForPorts);
    }

    [Fact]
    public async Task Waits_while_someone_else_is_connecting()
    {
        var r = new Rig();
        var paused = true;
        var keeper = new AdbKeeper(r.Adb, new AdbKeeperContext
        {
            Enabled = () => true, LinkUp = () => true, PhoneIp = () => Ip, PhoneKey = () => "p",
            Setup = () => r.Setup, Paused = () => paused,
        }, new InMemorySettingsStore(), () => r.Now);
        await keeper.TriggerAsync(AdbKeeperTrigger.ConnectPort);
        Assert.Empty(r.Connects);
        Assert.Equal(AdbKeeperState.Reconnecting, keeper.State);
        paused = false;
        await keeper.TickAsync();
        Assert.Single(r.Connects);
    }

    [Fact]
    public void Unreachable_wording_covers_mac_linux_and_windows()
    {
        Assert.True(AdbKeeper.MeansUnreachable("failed to connect to '1.2.3.4:5555': No route to host"));
        Assert.True(AdbKeeper.MeansUnreachable("failed to connect to '1.2.3.4:5555': Connection refused"));
        Assert.True(AdbKeeper.MeansUnreachable("No connection could be made because the target machine actively refused it. (10061)"));
        Assert.False(AdbKeeper.MeansUnreachable("failed to authenticate to 1.2.3.4:5555"));
        Assert.False(AdbKeeper.MeansUnreachable("Connection timed out"));
        Assert.False(AdbKeeper.MeansUnreachable(null));
    }

    static Func<IReadOnlyList<string>, ProcessResult> Wrap(Rig r, Action<IReadOnlyList<string>> after)
    {
        var inner = r.Fake.Handler;
        return args =>
        {
            var result = inner(args);
            after(args);
            return result;
        };
    }
}
