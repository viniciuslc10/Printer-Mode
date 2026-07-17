using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using PrinterMode.UI.ViewModels;

namespace PrinterMode.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var vm = App.Services.GetRequiredService<SimpleViewModel>();
        DataContext = vm;
        Loaded += async (_, _) => await vm.LoadAsync();

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (version != null)
            VersionText.Text = $"v{version.Major}.{version.Minor}.{version.Build}";

        // WindowStyle="None" + AllowsTransparency="True" windows overflow past the screen
        // edges (covering the taskbar) when maximized — a well-known WPF quirk since the
        // OS auto-fit logic that normally accounts for the taskbar is tied to the native
        // chrome this window doesn't have. Clamp explicitly to the monitor's work area.
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Maximized)
            {
                MaxHeight = SystemParameters.WorkArea.Height;
                MaxWidth = SystemParameters.WorkArea.Width;
            }
            else
            {
                MaxHeight = double.PositiveInfinity;
                MaxWidth = double.PositiveInfinity;
            }
        };
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
