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
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
