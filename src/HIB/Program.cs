using System;
using System.Threading;
using Avalonia;

namespace HIB;

sealed class Program
{
    static Mutex? _singleInstance;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static int Main(string[] args)
    {
        // Explorer "Send to": hand the files to the running H.I.B over its pipe (SPACE.md §5).
        var sendTo = HIB.Services.SendToRequest.Parse(args);
        if (sendTo is not null && HIB.Services.SendToPipe.TryForward(sendTo)) return 0;

        // One copy per user (a second copy could not bind port 6996 anyway). Test instances (HIB_PORT) are exempt.
        if (Environment.GetEnvironmentVariable("HIB_PORT") is null)
        {
            _singleInstance = new Mutex(true, "HIB-Desktop-SingleInstance", out var first);
            if (!first)
            {
                // Still starting up: give its pipe a moment.
                for (var i = 0; sendTo is not null && i < 10; i++)
                {
                    if (HIB.Services.SendToPipe.TryForward(sendTo, 800)) break;
                    Thread.Sleep(400);
                }
                return 0;
            }
        }
        // Not running: start normally and send once the device is linked.
        HIB.Services.SendToRequest.Pending = sendTo;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
