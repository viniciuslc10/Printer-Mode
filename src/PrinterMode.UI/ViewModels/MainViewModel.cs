using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PrinterMode.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private object? _currentView;

    [ObservableProperty]
    private string _activeSection = "Dashboard";

    private readonly DashboardViewModel _dashboardVm;
    private readonly PrinterListViewModel _printerListVm;
    private readonly InstallDriverViewModel _installDriverVm;

    public MainViewModel(
        DashboardViewModel dashboardVm,
        PrinterListViewModel printerListVm,
        InstallDriverViewModel installDriverVm)
    {
        _dashboardVm = dashboardVm;
        _printerListVm = printerListVm;
        _installDriverVm = installDriverVm;

        // Wire install request from dashboard
        _dashboardVm.InstallRequested += OnInstallRequested;

        NavigateToDashboard();
    }

    [RelayCommand]
    private void NavigateToDashboard()
    {
        CurrentView = _dashboardVm;
        ActiveSection = "Dashboard";
    }

    [RelayCommand]
    private void NavigateToPrinters()
    {
        _ = _printerListVm.LoadAsync();
        CurrentView = _printerListVm;
        ActiveSection = "Impressoras";
    }

    [RelayCommand]
    private void NavigateToInstall()
    {
        CurrentView = _installDriverVm;
        ActiveSection = "Instalar";
    }

    private void OnInstallRequested(object? sender, EventArgs e)
    {
        _installDriverVm.PreloadFromDashboard(_dashboardVm.SelectedDevice, _dashboardVm.AutoDetectedDriver);
        NavigateToInstall();
    }
}
