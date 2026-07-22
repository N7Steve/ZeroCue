using Avalonia;
using System;
using ZeroCue.DataProbe.Services;

namespace ZeroCue.DataProbe;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        ZeroCuePaths.ConfigureNativeLibrarySearchPath();
        ZeroCueLog.Initialize();

        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            ZeroCueLog.Communication($"[FATAL] Unhandled exception: {e.ExceptionObject}");
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            ZeroCueLog.Communication($"[TASK-ERROR] Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            ZeroCueLog.Shutdown();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
