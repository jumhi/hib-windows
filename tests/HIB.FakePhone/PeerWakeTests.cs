using HIB.Core.Space;

namespace HIB.FakePhone;

/// <summary>
/// The peer link across a sleep. On 2026-11-24/25 the Mac logged "no frames from PC-INTERN for 30 s" 104 times, each within
/// ~1 s of the Mac waking (pmset Wake/DarkWake): the Mac had been asleep, and its keepalive measured the sleep as silence.
/// This PC's keepalive judged silence the same way, so a PC resuming from sleep dropped a healthy link on its first tick.
/// </summary>
public class PeerWakeTests
{
    [Fact]
    public async Task Waking_from_sleep_is_not_mistaken_for_a_silent_peer()
    {
        long offsetTicks = 0;
        var space = RoamTests.FastSpace();
        space.Peer.Clock = () => DateTime.UtcNow + TimeSpan.FromTicks(Interlocked.Read(ref offsetTicks));
        await using var host = new TestHost(name: "Office PC", space: space);
        host.Core.SpaceEngine.SetSpaceKey(RoamTests.SpaceKey);
        await using var mac = await RawMac.DialAsync(host.Core.Link.Port, RoamTests.SpaceKey);
        Assert.True(await Eventually.TrueAsync(() => host.Core.SpaceEngine.Network.IsLinked("A Mac")));

        // The PC sleeps for 5 minutes: its clock jumps, the Mac (still there) answers pings as soon as it's asked.
        Interlocked.Exchange(ref offsetTicks, TimeSpan.FromMinutes(5).Ticks);
        await Task.Delay(2500); // several keepalive ticks (500 ms) after the wake
        Assert.True(host.Core.SpaceEngine.Network.IsLinked("A Mac"));
        Assert.DoesNotContain(host.Core.LogLines, l => l.Contains("silent for"));
        Assert.Contains(host.Core.LogLines, l => l.Contains("[peer] woke after"));
    }

    [Fact]
    public async Task A_peer_that_stays_silent_after_a_wake_is_still_dropped()
    {
        long offsetTicks = 0;
        var space = RoamTests.FastSpace();
        space.Peer.Clock = () => DateTime.UtcNow + TimeSpan.FromTicks(Interlocked.Read(ref offsetTicks));
        await using var host = new TestHost(name: "Office PC", space: space);
        host.Core.SpaceEngine.SetSpaceKey(RoamTests.SpaceKey);
        await using var mac = await RawMac.DialAsync(host.Core.Link.Port, RoamTests.SpaceKey);
        Assert.True(await Eventually.TrueAsync(() => host.Core.SpaceEngine.Network.IsLinked("A Mac")));
        mac.Mute = true; // gone quiet for real (no pongs, nothing)
        Interlocked.Exchange(ref offsetTicks, TimeSpan.FromMinutes(5).Ticks);
        Assert.True(await Eventually.TrueAsync(() => !host.Core.SpaceEngine.Network.IsLinked("A Mac"), TimeSpan.FromSeconds(10)));
    }
}
