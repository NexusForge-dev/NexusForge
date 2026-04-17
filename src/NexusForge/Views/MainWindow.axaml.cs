using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using NexusForge.Services;
using NexusForge.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace NexusForge.Views;

public partial class MainWindow : Window
{
    private bool _cleanupDone;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_cleanupDone)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;

        Hide();

        var cleanupWindow = new ShutdownWindow();
        cleanupWindow.Show();

        await Task.Run(() =>
        {
            try
            {
                var app = (App)Avalonia.Application.Current!;
                var services = app.Services;
                if (services != null)
                {
                    try { services.GetService<NativeJtagService>()?.Dispose(); } catch { }
                    try { services.GetService<DmaTestService>()?.Cleanup(); } catch { }
                }
            }
            catch { }

            SpawnCleanupProcess();
        });

        await Task.Delay(2000);

        cleanupWindow.Close();
        _cleanupDone = true;
        Dispatcher.UIThread.Post(() => Close());
    }

    private static void SpawnCleanupProcess()
    {
        try
        {
            var pid = Environment.ProcessId;
            var tempDir = Path.GetTempPath().TrimEnd('\\');
            var batPath = Path.Combine(tempDir, $"nf_cleanup_{pid}.bat");

            var bat = $"""
                @echo off
                :wait
                tasklist /FI "PID eq {pid}" 2>nul | findstr /I "{pid}" >nul
                if not errorlevel 1 (
                    ping -n 2 127.0.0.1 >nul
                    goto :wait
                )
                ping -n 2 127.0.0.1 >nul
                for /d %%d in ("{tempDir}\nf_*") do rd /s /q "%%d" 2>nul
                for /d %%d in ("{tempDir}\drv_*") do rd /s /q "%%d" 2>nul
                del /f /q "{batPath}" 2>nul
                """;

            File.WriteAllText(batPath, bat);

            Process.Start(new ProcessStartInfo
            {
                FileName = batPath,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            })?.Dispose();
        }
        catch { }
    }
}

public class ShutdownWindow : Window
{
    public ShutdownWindow()
    {
        Title = "NexusForge";
        Width = 280;
        Height = 120;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SystemDecorations = SystemDecorations.None;
        Background = new SolidColorBrush(Color.Parse("#0D1117"));
        Topmost = true;

        Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#161B22")),
            BorderBrush = new SolidColorBrush(Color.Parse("#30363D")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(24, 20),
            Child = new StackPanel
            {
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Shutting down...",
                        Foreground = new SolidColorBrush(Color.Parse("#E6EDF3")),
                        FontSize = 14,
                        FontWeight = FontWeight.SemiBold,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                    },
                    new ProgressBar
                    {
                        IsIndeterminate = true,
                        Width = 200,
                        Height = 4,
                        Foreground = new SolidColorBrush(Color.Parse("#00D4AA")),
                        Background = new SolidColorBrush(Color.Parse("#21262D")),
                        CornerRadius = new CornerRadius(2),
                        MinHeight = 4
                    },
                    new TextBlock
                    {
                        Text = "Please wait...",
                        Foreground = new SolidColorBrush(Color.Parse("#6E7681")),
                        FontSize = 11,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                    }
                }
            }
        };
    }
}
