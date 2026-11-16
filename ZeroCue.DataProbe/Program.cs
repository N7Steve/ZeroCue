using Avalonia;
using System;
using ZeroCue.DataProbe.Services;
using ZeroCue.DataProbe.Services.Drivers;

namespace ZeroCue.DataProbe;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // The elevated driver helper must not initialise the UI, load libusb or
        // touch user settings; it only runs the requested driver operation.
        if (ElevatedDriverHelper.IsHelperInvocation(args))
        {
            return ElevatedDriverHelper.Run(args);
        }

        using var singleInstance = SingleInstanceGuard.TryAcquire();
        if (singleInstance == null)
        {
            SingleInstanceGuard.SignalExistingInstance();
            return 0;
        }

        SingleInstanceGuard.Current = singleInstance;

        ZeroCuePaths.ConfigureNativeLibrarySearchPath();
        ZeroCueLog.Initialize();

        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            ZeroCueLog.Communication($"[FATAL] Unhandled exception: {e.ExceptionObject}");
            // Never leave a mapped key held down in Windows when the process dies.
            try { ScufDeviceService.Instance.ReleaseAllSyntheticOutputs("unhandled-exception"); } catch { }
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            ZeroCueLog.Communication($"[TASK-ERROR] Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
