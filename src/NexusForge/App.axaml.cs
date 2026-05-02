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
        try
        {
            AvaloniaXamlLoader.Load(this);
        }
        catch (Exception ex)
        {
            CrashLogger.LogException("App.Initialize/AvaloniaXamlLoader", ex);
            throw;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // NB: this method is intentionally NOT async void anymore. Async-void on the
        // Avalonia init lifecycle silently terminates the process on any exception
        // (no managed handler can catch it before the dispatcher dies). Run startup
        // synchronously here, then fire-and-forget the post-init background tasks.
        try
        {
            InitializeFrameworkInternal();
        }
        catch (Exception ex)
        {
            CrashLogger.LogException("App.OnFrameworkInitializationCompleted", ex);
            throw;
        }
        finally
        {
            base.OnFrameworkInitializationCompleted();
        }
    }

    private void InitializeFrameworkInternal()
    {
        AntiTamper.Verify();

        if (!IntegrityGuard.IsValid())
        {
            CrashLogger.WriteLine("IntegrityGuard reported invalid — exiting.");
            Environment.Exit(IntegrityGuard.Probe());
            return;
        }

        _services = ConfigureServices();

        CleanupTempFolders();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainViewModel = _services.GetRequiredService<MainViewModel>();
            var mainWindow = new MainWindow(mainViewModel);

            // Show splash, then reveal main window when splash finishes.
            var splash = new SplashWindow();
            desktop.MainWindow = splash;

            splash.Closed += (_, _) =>
            {
                desktop.MainWindow = mainWindow;
                mainWindow.Show();
            };

            // Auto-update runs in the background after a short settle period. Wrapped
            // tightly so a network/parse blowup never bubbles to the dispatcher.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(4000);
                    var updater = _services.GetRequiredService<AutoUpdateService>();
                    bool shouldRestart = await updater.CheckAndApplyUpdateAsync();
                    if (shouldRestart)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            CrashLogger.WriteLine("AutoUpdate: applying update, exiting cleanly.");
                            CrashLogger.MarkCleanExit();
                            Environment.Exit(0);
                        });
                    }
                }
                catch (Exception ex)
                {
                    CrashLogger.LogException("AutoUpdate background task", ex);
                }
            });
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            CrashLogger.MarkCleanExit();
            CleanupTempFolders();
        };
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
        services.AddSingleton<BarProbeService>();
        services.AddSingleton<PciEnumService>();
        services.AddSingleton<AutoUpdateService>();

        services.AddSingleton<BoardInfoViewModel>();
        services.AddSingleton<FlashViewModel>();
        services.AddSingleton<DriverViewModel>();
        services.AddSingleton<DmaTestViewModel>();
        services.AddSingleton<BarProbeViewModel>();
        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider();
    }
}
