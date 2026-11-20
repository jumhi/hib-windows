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
        // One copy per user (a second copy could not bind port 6996 anyway). Test instances (HIB_PORT) are exempt.
        if (Environment.GetEnvironmentVariable("HIB_PORT") is null)
        {
            _singleInstance = new Mutex(true, "HIB-Desktop-SingleInstance", out var first);
            if (!first) return 0;
        }
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
