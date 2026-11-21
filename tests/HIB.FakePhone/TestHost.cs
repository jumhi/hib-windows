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
        HIB.Core.Space.SpaceOptions? space = null, string? root = null, HIB.Core.Roam.RoamOptions? roam = null)
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
            // Tests never capture or inject real input.
            Roam = roam ?? new HIB.Core.Roam.RoamOptions { Platform = () => new HIB.Core.Roam.NullRoamPlatform() },
        });
        Core.Settings.DesktopName = name ?? "Haitham’s PC & Co?=x+y";
        Core.Settings.Save();
        Clipboard = new FakeClipboard();
        Core.AttachClipboard(Clipboard, TimeSpan.FromMilliseconds(200));
        Core.Start();
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
