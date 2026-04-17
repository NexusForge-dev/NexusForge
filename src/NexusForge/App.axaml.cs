using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusForge.Helpers;
using NexusForge.Models;
using NexusForge.Services;
using NexusForge.ViewModels;
using NexusForge.Views;

namespace NexusForge;

public class App : Application
{
    private IServiceProvider? _services;
    public IServiceProvider? Services => _services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        AntiTamper.Verify();

        if (!IntegrityGuard.IsValid())
        {
            Environment.Exit(IntegrityGuard.Probe());
        }

        _services = ConfigureServices();

        CleanupTempFolders();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainViewModel = _services.GetRequiredService<MainViewModel>();
            desktop.MainWindow = new MainWindow(mainViewModel);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(2000);

                    var updater = _services.GetRequiredService<AutoUpdateService>();
                    bool shouldRestart = await updater.CheckAndApplyUpdateAsync();
                    if (shouldRestart)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            Environment.Exit(0);
                        });
                    }
                }
                catch { }
            });
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupTempFolders();

        base.OnFrameworkInitializationCompleted();
    }

    private static void CleanupTempFolders()
    {
        try
        {
            var tempDir = Path.GetTempPath();
            foreach (var dir in Directory.GetDirectories(tempDir, "nf_*"))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        catch { }
    }

    private static IServiceProvider ConfigureServices()
    {
        var settings = new AppSettings();

        var services = new ServiceCollection();

        services.AddSingleton(settings);

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        services.AddSingleton<LogService>();
        services.AddSingleton<NativeJtagService>();
        services.AddSingleton<BoardDetectionService>();
        services.AddSingleton<FlashService>();
        services.AddSingleton<FtdiDriverService>();
        services.AddSingleton<DriverService>();
        services.AddSingleton<DmaTestService>();
        services.AddSingleton<AutoUpdateService>();

        services.AddSingleton<BoardInfoViewModel>();
        services.AddSingleton<FlashViewModel>();
        services.AddSingleton<DriverViewModel>();
        services.AddSingleton<DmaTestViewModel>();
        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider();
    }
}
