using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrinterMode.Core.Enums;
using PrinterMode.Core.Interfaces;
using PrinterMode.Core.Models;

namespace PrinterMode.UI.ViewModels;

public partial class SimpleViewModel : ObservableObject
{
    private readonly IDriverRepository _repository;
    private readonly IDriverInstaller _installer;
    private readonly ILogService _log;

    public ObservableCollection<string> Manufacturers { get; } = [];
    public ObservableCollection<DriverInfo> Models { get; } = [];
    public ObservableCollection<PaperConfig> Papers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(HasManufacturer))]
    private string? _selectedManufacturer;

    public bool HasManufacturer => SelectedManufacturer != null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private DriverInfo? _selectedModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNetworkFields))]
    [NotifyPropertyChangedFor(nameof(ShowSerialFields))]
    private bool _connectionUsb = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNetworkFields))]
    [NotifyPropertyChangedFor(nameof(ShowSerialFields))]
    private bool _connectionNetwork;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNetworkFields))]
    [NotifyPropertyChangedFor(nameof(ShowSerialFields))]
    private bool _connectionSerial;

    [ObservableProperty] private string _ipAddress = string.Empty;
    [ObservableProperty] private int _networkPort = 9100;
    [ObservableProperty] private string _comPort = "COM1";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private string _printerName = string.Empty;

    [ObservableProperty] private PaperConfig? _selectedPaper;
    [ObservableProperty] private bool _setAsDefault;

    [ObservableProperty] private string _statusText = "Selecione o fabricante e modelo para continuar.";
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private bool _showSuccess;
    [ObservableProperty] private bool _showError;

    public bool CanInstall => SelectedModel != null && !string.IsNullOrWhiteSpace(PrinterName) && !IsInstalling;
    public bool ShowNetworkFields => ConnectionNetwork;
    public bool ShowSerialFields => ConnectionSerial;

    public SimpleViewModel(IDriverRepository repository, IDriverInstaller installer, ILogService log)
    {
        _repository = repository;
        _installer = installer;
        _log = log;
    }

    public async Task LoadAsync()
    {
        var manufacturers = await _repository.GetManufacturersAsync();
        foreach (var m in manufacturers)
            Manufacturers.Add(m);
    }

    partial void OnSelectedManufacturerChanged(string? value)
    {
        Models.Clear();
        SelectedModel = null;
        if (value == null) return;
        _ = LoadModelsAsync(value);
    }

    private async Task LoadModelsAsync(string manufacturer)
    {
        var drivers = await _repository.GetDriversByManufacturerAsync(manufacturer);
        foreach (var d in drivers)
            Models.Add(d);
    }

    partial void OnSelectedModelChanged(DriverInfo? value)
    {
        Papers.Clear();
        SelectedPaper = null;
        ShowSuccess = false;
        ShowError = false;

        if (value == null)
        {
            PrinterName = string.Empty;
            StatusText = "Selecione o fabricante e modelo para continuar.";
            return;
        }

        PrinterName = value.DisplayName;

        var papers = value.SupportedPapers.Count > 0 ? value.SupportedPapers : [value.DefaultPaper];
        foreach (var p in papers)
            Papers.Add(p);
        SelectedPaper = Papers.FirstOrDefault();

        // Auto-select connection type from driver capabilities
        if (value.SupportedPorts.Any(p => p.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || p.Equals("Serial", StringComparison.OrdinalIgnoreCase)))
        { ConnectionUsb = false; ConnectionNetwork = false; ConnectionSerial = true; }
        else if (value.SupportedPorts.Any(p => p.Contains("TCP", StringComparison.OrdinalIgnoreCase) || p.Contains("Network", StringComparison.OrdinalIgnoreCase) || p.Contains("IP", StringComparison.OrdinalIgnoreCase)))
        { ConnectionUsb = false; ConnectionNetwork = true; ConnectionSerial = false; }
        else
        { ConnectionUsb = true; ConnectionNetwork = false; ConnectionSerial = false; }

        StatusText = $"Modelo: {value.DisplayName}. Clique em Instalar para continuar.";
    }

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanInstall))]
    private async Task InstallAsync(CancellationToken ct)
    {
        IsInstalling = true;
        ShowSuccess = false;
        ShowError = false;
        OnPropertyChanged(nameof(CanInstall));

        try
        {
            var driver = SelectedModel!;
            var paper = SelectedPaper ?? driver.DefaultPaper;

            StatusText = "Verificando driver instalado...";
            var alreadyInstalled = await _installer.IsDriverInstalledAsync(driver.DriverName, ct);

            var connType = ConnectionNetwork ? ConnectionType.Network
                         : ConnectionSerial ? ConnectionType.Serial
                         : ConnectionType.USB;

            var request = new InstallRequest
            {
                Driver = driver,
                PrinterName = PrinterName,
                ConnectionType = connType,
                IpAddress = ConnectionNetwork ? IpAddress : null,
                NetworkPort = ConnectionNetwork ? NetworkPort : 9100,
                PortName = ConnectionSerial ? ComPort : null,
                Paper = paper,
                SetAsDefault = SetAsDefault,
                SkipDriverInstall = alreadyInstalled
            };

            var result = await _installer.InstallAsync(
                request,
                new Progress<string>(msg => StatusText = msg),
                ct);

            if (result.Success)
            {
                ShowSuccess = true;
                StatusText = result.Message;
            }
            else
            {
                ShowError = true;
                StatusText = string.IsNullOrWhiteSpace(result.ErrorDetails)
                    ? result.Message
                    : $"{result.Message}\n{result.ErrorDetails}";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Instalação cancelada.";
        }
        catch (Exception ex)
        {
            ShowError = true;
            StatusText = $"Erro: {ex.Message}";
            _log.Error("Install error", ex);
        }
        finally
        {
            IsInstalling = false;
            OnPropertyChanged(nameof(CanInstall));
        }
    }
}
