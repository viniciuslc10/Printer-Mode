using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrinterMode.Core.Interfaces;

namespace PrinterMode.UI.ViewModels;

public partial class PrinterListViewModel : ObservableObject
{
    private readonly IWindowsPrinterService _printerService;
    private readonly IDriverInstaller _installer;
    private readonly ILogService _log;

    [ObservableProperty] private ObservableCollection<string> _installedPrinters = [];
    [ObservableProperty] private string? _selectedPrinter;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public PrinterListViewModel(IWindowsPrinterService printerService, IDriverInstaller installer, ILogService log)
    {
        _printerService = printerService;
        _installer = installer;
        _log = log;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            var printers = await _printerService.GetInstalledPrintersAsync(ct);
            InstalledPrinters = new ObservableCollection<string>(printers);
            StatusMessage = $"{printers.Count} impressora(s) instalada(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erro: {ex.Message}";
            _log.Error("Failed to load printers", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    [RelayCommand]
    private async Task TestPrintAsync()
    {
        if (SelectedPrinter == null) return;
        StatusMessage = "Enviando página de teste...";
        var result = await _installer.TestPrintAsync(SelectedPrinter);
        StatusMessage = result.Message;
    }

    [RelayCommand]
    private async Task DeletePrinterAsync()
    {
        if (SelectedPrinter == null) return;

        StatusMessage = $"Removendo {SelectedPrinter}...";
        var ok = await _installer.UninstallAsync(SelectedPrinter);

        if (ok)
        {
            InstalledPrinters.Remove(SelectedPrinter);
            SelectedPrinter = null;
            StatusMessage = "Impressora removida.";
        }
        else
        {
            StatusMessage = "Falha ao remover impressora.";
        }
    }
}
