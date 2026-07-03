using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PrinterMode.Core.Interfaces;
using PrinterMode.DriverManager;
using PrinterMode.NetworkDiscovery;
using PrinterMode.UI.Services;
using PrinterMode.UI.ViewModels;
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

        // On startup: enable LPD and start the discovery listener so other PCs can
        // find and connect to shared printers on this machine automatically.
        var printerService = Services.GetRequiredService<IWindowsPrinterService>();
        _ = printerService.EnableLpdServiceAsync(CancellationToken.None);
        printerService.StartDiscoveryListener();
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
