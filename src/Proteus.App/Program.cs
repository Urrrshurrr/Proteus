using Avalonia;
using Proteus.Core;

namespace Proteus.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Nothing should ever fail silently: log it, and show it if the window is up.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Write("Unhandled error (the app had to close)", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write("Unobserved background error", e.Exception);
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            AppLog.Write("Proteus stopped because of an error", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
