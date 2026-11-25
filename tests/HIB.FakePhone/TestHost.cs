using HIB.Core;
using HIB.Core.Phone;

namespace HIB.FakePhone;

/// <summary>In-memory clipboard standing in for the desktop clipboard.</summary>
public sealed class FakeClipboard : IClipboardBridge
{
    public string? Text;
    public byte[]? Image;
    public int Sets;
    public Task<string?> GetTextAsync() => Task.FromResult(Text);
    public Task<byte[]?> GetImagePngAsync() => Task.FromResult(Image);
    public Task SetTextAsync(string text) { Text = text; Image = null; Sets++; return Task.CompletedTask; }
    public Task SetImageAsync(byte[] imageBytes) { Image = imageBytes; Text = null; Sets++; return Task.CompletedTask; }
    public uint? ChangeCount => null;
}

/// <summary>
/// Starts an in-process desktop core on a test port (HIB_PORT base, default 16996) with
/// discovery off and a throw-away data/downloads directory.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    static int _nextPort = int.TryParse(Environment.GetEnvironmentVariable("HIB_PORT"), out var p) ? p : 16996;

    public TestHost(TimeSpan? pingInterval = null, TimeSpan? activityTimeout = null, string? name = null,
        HIB.Core.Space.SpaceOptions? space = null, string? root = null, HIB.Core.Roam.RoamOptions? roam = null,
        Func<HIB.Core.Act.IDesktopControl>? desktop = null, Func<HIB.Core.Act.ISystemStats>? stats = null, TimeSpan? deckInterval = null,
        HIB.Core.Remote.PhoneRemoteOptions? remote = null)
    {
        Root = root ?? Path.Combine(Path.GetTempPath(), "hib-tests", Guid.NewGuid().ToString("N")[..8]);
        Downloads = Path.Combine(Root, "Downloads");
        Directory.CreateDirectory(Downloads);
        var port = NextFreePort();
        Core = new HibCore(new HibCoreOptions
        {
            DataDir = Path.Combine(Root, "data"),
            Port = port,
            EnableDiscovery = false,
            DownloadsDir = () => Downloads,
            PingInterval = pingInterval,
            ActivityTimeout = activityTimeout,
            Space = space ?? new HIB.Core.Space.SpaceOptions(),
            // Tests never capture or inject real input, nor touch Mouse Keys, keep the PC awake or lock it.
            Roam = WithoutSystem(roam ?? new HIB.Core.Roam.RoamOptions { Platform = () => new HIB.Core.Roam.NullRoamPlatform() }),
            // …nor press keys, lock the screen or change the volume (Point to act, SPACE.md §11.3).
            Desktop = desktop ?? (() => new HIB.Core.Act.NullDesktopControl()),
            Stats = stats ?? (() => new HIB.Core.Act.SystemStats()),
            DeckInterval = deckInterval,
            // …nor capture the screen, keep the PC awake or watch its focus (Phone Remote, SPACE.md §15).
            Remote = remote ?? new HIB.Core.Remote.PhoneRemoteOptions
            {
                Pipelines = () => new HIB.Core.Remote.NullScreenPipelineFactory(),
                System = () => new HIB.Core.Remote.NullRemoteSystem(),
                TailscaleAddrs = () => Array.Empty<string>(),
            },
        });
        Core.Settings.DesktopName = name ?? "Haitham’s PC & Co?=x+y";
        Core.Settings.Save();
        Clipboard = new FakeClipboard();
        Core.AttachClipboard(Clipboard, TimeSpan.FromMilliseconds(200));
        Core.Start();
    }

    /// <summary>The real system services (SPACE.md §12) are replaced unless the test brought its own fake.</summary>
    static HIB.Core.Roam.RoamOptions WithoutSystem(HIB.Core.Roam.RoamOptions o)
    {
        if (o.System.Method == ((Func<HIB.Core.Roam.IRoamSystem>)HIB.Core.Roam.RoamSystems.CreateDefault).Method)
            o.System = () => new HIB.Core.Roam.NullRoamSystem();
        return o;
    }

    static int NextFreePort()
    {
        while (true)
        {
            var port = Interlocked.Increment(ref _nextPort) - 1;
            try
            {
                var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, port);
                probe.Start();
                probe.Stop();
                return port;
            }
            catch (System.Net.Sockets.SocketException) { /* in use (e.g. a running H.I.B on 16996) */ }
        }
    }

    public string Root { get; }
    public string Downloads { get; }
    public HibCore Core { get; }
    public FakeClipboard Clipboard { get; }

    public async Task<FakePhone> ConnectPhoneAsync(bool handshake = true)
    {
        var phone = new FakePhone(Core.BuildQr());
        await phone.ConnectAsync();
        if (handshake)
        {
            await phone.SendDeviceAsync();
            await phone.WaitForAsync("macInfo");
        }
        return phone;
    }

    /// <summary>Keep the data folder on dispose (to restart a core on it).</summary>
    public bool KeepRoot { get; set; }

    public ValueTask DisposeAsync()
    {
        Core.Dispose();
        if (!KeepRoot) try { Directory.Delete(Root, true); } catch { }
        return ValueTask.CompletedTask;
    }
}

public static class Eventually
{
    public static async Task<bool> TrueAsync(Func<bool> cond, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < until)
        {
            if (cond()) return true;
            await Task.Delay(50);
        }
        return cond();
    }
}
