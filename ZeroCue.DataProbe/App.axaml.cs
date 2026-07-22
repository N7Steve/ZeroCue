using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;
using ZeroCue.DataProbe.Services;
namespace ZeroCue.DataProbe;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var service = ScufDeviceService.Instance;
        LocalizationService.SetLanguage(service.LanguageCode);
        ChangeTheme(service.ThemeName);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            if (WindowsStartupService.ShouldStartMinimized(desktop.Args))
            {
                mainWindow.WindowState = Avalonia.Controls.WindowState.Minimized;
            }

            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void ChangeTheme(string themeName)
    {
        if (Current?.Resources.MergedDictionaries.Count > 0)
        {
            Current.Resources.MergedDictionaries.Clear();
            var newTheme = new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://ZeroCue.DataProbe/App.axaml"))
            {
                Source = new Uri($"avares://ZeroCue.DataProbe/Themes/{themeName}.axaml")
            };
            Current.Resources.MergedDictionaries.Add(newTheme);
        }
    }
}
