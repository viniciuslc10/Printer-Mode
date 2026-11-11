using System.IO;
using System.Runtime.InteropServices;
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

    // Confirmed root cause of print jobs stuck on the client, unresolved by any client-side
    // fix: the LPD/RAW/discovery listeners only exist while THIS process is running. Closing
    // the main window used to fully exit the app (WPF's default ShutdownMode is
    // OnLastWindowClose) — a completely normal thing for someone to do after "finishing" the
    // install on the server PC, which silently kills printer sharing for every client. The
    // window's X button now only hides it; the process (and its listeners) keeps running via
    // this tray icon, with an explicit "Sair" to actually exit.
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private bool _reallyExit;

    // Kept referenced for the whole process lifetime so it's never garbage-collected —
    // releasing it early would let a second launch acquire it and start a second instance.
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Confirmed root cause of "two windows open" in the field: nothing stopped a second
        // launch (desktop icon clicked again, Start Menu, the --minimized autostart racing a
        // manual open) from spinning up a whole second process — its own window AND its own
        // LPD/RAW/discovery listeners competing for the same ports as the first instance.
        // The window hiding instead of closing (ShutdownMode.OnExplicitShutdown, below) makes
        // this easy to trigger by accident: the app looks "closed" but is still running.
        _singleInstanceMutex = new Mutex(true, "Global\\PrinterMode_SingleInstance_B6F1E2A4", out var createdNew);
        if (!createdNew)
        {
            BringExistingInstanceToFront();
            Shutdown();
            return;
        }

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
        printerService.StartLpdServer();

        bool silentMode = e.Args.Contains("--minimized");

        // Never quit just because a window closed — only via the tray icon's "Sair" or
        // Windows shutdown/logoff. This is what keeps LPD/RAW/discovery alive in the
        // background regardless of whether the main window is open.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        CreateTrayIcon();

        if (!silentMode)
        {
            // Register ourselves to auto-start silently (--minimized) on next Windows login.
            RegisterWindowsStartup();
            ShowMainWindow();
        }
    }

    private void CreateTrayIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppDirectory, "Assets", "icon.ico");
            var icon = File.Exists(iconPath)
                ? new System.Drawing.Icon(iconPath)
                : System.Drawing.SystemIcons.Application;

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Abrir PrinterMode", null, (_, _) => ShowMainWindow());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Sair", null, (_, _) =>
            {
                _reallyExit = true;
                _trayIcon?.Dispose();
                Shutdown();
            });

            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = icon,
                Text = "PrinterMode — compartilhamento ativo",
                Visible = true,
                ContextMenuStrip = menu
            };
            _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
        }
        catch (Exception ex)
        {
            Services.GetService<ILogService>()?.Warning($"CreateTrayIcon failed: {ex.Message}");
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow == null)
        {
            _mainWindow = new MainWindow();
            _mainWindow.Closing += (_, args) =>
            {
                // Hide instead of closing, unless the user picked "Sair" from the tray icon.
                if (_reallyExit) return;
                args.Cancel = true;
                _mainWindow!.Hide();
            };
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    // Brings the already-running instance's window to front (even if it was hidden by the
    // "close = hide to tray" behavior) instead of this second launch creating its own.
    // FindWindow matches by exact title regardless of visibility, so a hidden MainWindow is
    // still found; ShowWindow/SetForegroundWindow then un-hides and focuses it.
    private static void BringExistingInstanceToFront()
    {
        try
        {
            var hwnd = NativeMethods.FindWindow(null, "PrinterMode");
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                NativeMethods.SetForegroundWindow(hwnd);
            }
        }
        catch { /* best-effort — worst case the user just sees nothing happen */ }
    }

    private static class NativeMethods
    {
        public const int SW_RESTORE = 9;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
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
        services.AddSingleton<IAutoDetectService, AutoDetectService>();
        services.AddTransient<SimpleViewModel>();
    }
}
