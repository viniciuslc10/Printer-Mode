using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using PrinterMode.Core.Interfaces;
using PrinterMode.DriverManager;
using PrinterMode.NetworkDiscovery;
using PrinterMode.UI.Services;
using PrinterMode.UI.ViewModels;
using PrinterMode.UI.Views;
using PrinterMode.WindowsPrinter;

namespace PrinterMode.UI;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    private static string AppDirectory => AppContext.BaseDirectory;
    private static string RepositoryPath => Path.Combine(AppDirectory, "Repository");
    private static string LogsPath => Path.Combine(AppDirectory, "Logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        DispatcherUnhandledException += (_, args) =>
        {
            var log = Services.GetService<ILogService>();
            log?.Error("Unhandled UI exception", args.Exception);
            MessageBox.Show(
                $"Erro inesperado:\n{args.Exception.Message}",
                "PrinterMode - Erro",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        // Always activate LPD and start discovery listener on any startup mode.
        var printerService = Services.GetRequiredService<IWindowsPrinterService>();
        _ = printerService.EnableLpdServiceAsync(CancellationToken.None);
        printerService.StartDiscoveryListener();

        bool silentMode = e.Args.Contains("--minimized");

        if (silentMode)
        {
            // Background mode: LPD + discovery listener only, no window shown.
            // Keeps running so other PCs can always discover printers automatically.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }
        else
        {
            // Normal mode: register ourselves to auto-start silently on next Windows login,
            // then show the main window.
            RegisterWindowsStartup();
            var mainWindow = new MainWindow();
            mainWindow.Show();
        }
    }

    private static void RegisterWindowsStartup()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return;

            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.SetValue("PrinterMode", $"\"{exePath}\" --minimized");
        }
        catch { }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ILogService>(_ => new LogService(LogsPath));
        services.AddSingleton<IDriverRepository>(sp =>
            new DriverRepository(RepositoryPath, sp.GetRequiredService<ILogService>()));
        services.AddSingleton<IWindowsPrinterService, WindowsPrinterService>();
        services.AddSingleton<IPrinterDetector, PrinterDetectorService>();
        services.AddSingleton<IDriverInstaller, DriverInstaller>();
        services.AddTransient<SimpleViewModel>();
    }
}
