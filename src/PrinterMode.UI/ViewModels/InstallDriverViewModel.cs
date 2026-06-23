using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrinterMode.Core.Enums;
using PrinterMode.Core.Interfaces;
using PrinterMode.Core.Models;

namespace PrinterMode.UI.ViewModels;

public partial class InstallDriverViewModel : ObservableObject
{
    private readonly IDriverRepository _repository;
    private readonly IDriverInstaller _installer;
    private readonly ILogService _log;

    // ── Selection ──────────────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<string> _manufacturers = [];
    [ObservableProperty] private string? _selectedManufacturer;
    [ObservableProperty] private ObservableCollection<DriverInfo> _availableDrivers = [];
    [ObservableProperty] private DriverInfo? _selectedDriver;
    [ObservableProperty] private ObservableCollection<PaperConfig> _availablePapers = [];
    [ObservableProperty] private PaperConfig? _selectedPaper;

    // ── Connection ─────────────────────────────────────────────────────────────
    [ObservableProperty] private PrinterDevice? _targetDevice;
    [ObservableProperty] private ConnectionType _selectedConnectionType = ConnectionType.USB;
    [ObservableProperty] private string _portName = "USB001";
    [ObservableProperty] private string _ipAddress = "192.168.0.100";
    [ObservableProperty] private int _networkPort = 9100;
    [ObservableProperty] private int _baudRate = 9600;
    [ObservableProperty] private string _printerName = string.Empty;
    [ObservableProperty] private bool _setAsDefault;

    // ── State ──────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private string _statusMessage = "Selecione o fabricante e modelo da impressora.";
    [ObservableProperty] private bool _isSuccess;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private ObservableCollection<string> _installLog = [];

    public bool IsUsb => SelectedConnectionType == ConnectionType.USB;
    public bool IsSerial => SelectedConnectionType == ConnectionType.Serial;
    public bool IsNetwork => SelectedConnectionType == ConnectionType.Network;

    public static IReadOnlyList<int> BaudRates => [9600, 19200, 38400, 57600, 115200];

    public InstallDriverViewModel(IDriverRepository repository, IDriverInstaller installer, ILogService log)
    {
        _repository = repository;
        _installer = installer;
        _log = log;

        _ = LoadManufacturersAsync();
    }

    private async Task LoadManufacturersAsync()
    {
        var list = await _repository.GetManufacturersAsync();
        Manufacturers = new ObservableCollection<string>(list);
    }

    partial void OnSelectedManufacturerChanged(string? value)
    {
        if (value == null) return;
        _ = LoadDriversForManufacturerAsync(value);
    }

    private async Task LoadDriversForManufacturerAsync(string manufacturer)
    {
        var drivers = await _repository.GetDriversByManufacturerAsync(manufacturer);
        AvailableDrivers = new ObservableCollection<DriverInfo>(drivers);
        SelectedDriver = null;
    }

    partial void OnSelectedDriverChanged(DriverInfo? value)
    {
        if (value == null) return;

        AvailablePapers = new ObservableCollection<PaperConfig>(value.SupportedPapers.Count > 0
            ? value.SupportedPapers
            : [value.DefaultPaper]);

        SelectedPaper = AvailablePapers.FirstOrDefault();
        PrinterName = $"{value.Manufacturer} {value.Model}";

        if (value.DefaultSerial != null)
            BaudRate = value.DefaultSerial.BaudRate;
    }

    partial void OnSelectedConnectionTypeChanged(ConnectionType value)
    {
        OnPropertyChanged(nameof(IsUsb));
        OnPropertyChanged(nameof(IsSerial));
        OnPropertyChanged(nameof(IsNetwork));
    }

    public void PreloadFromDashboard(PrinterDevice? device, DriverInfo? autoDriver)
    {
        TargetDevice = device;

        if (device != null)
        {
            SelectedConnectionType = device.ConnectionType;
            PortName = device.PortName ?? "USB001";
            if (device.IpAddress != null)
            {
                IpAddress = device.IpAddress;
                NetworkPort = device.NetworkPort;
            }
        }

        if (autoDriver != null)
        {
            SelectedManufacturer = autoDriver.Manufacturer;
            _ = LoadDriversForManufacturerAsync(autoDriver.Manufacturer).ContinueWith(_ =>
            {
                SelectedDriver = AvailableDrivers.FirstOrDefault(d => d.Id == autoDriver.Id);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task InstallAsync(CancellationToken ct)
    {
        if (SelectedDriver == null || SelectedPaper == null)
        {
            StatusMessage = "Selecione o fabricante, modelo e tipo de papel.";
            return;
        }

        if (string.IsNullOrWhiteSpace(PrinterName))
        {
            StatusMessage = "Informe um nome para a impressora.";
            return;
        }

        IsInstalling = true;
        IsSuccess = false;
        HasError = false;
        InstallLog.Clear();
        StatusMessage = "Iniciando instalação...";

        var request = new InstallRequest
        {
            Driver = SelectedDriver,
            Device = TargetDevice ?? new PrinterDevice(),
            PrinterName = PrinterName,
            Paper = SelectedPaper,
            ConnectionType = SelectedConnectionType,
            PortName = SelectedConnectionType == ConnectionType.Network
                ? $"IP_{IpAddress}"
                : PortName,
            IpAddress = IpAddress,
            NetworkPort = NetworkPort,
            SerialConfig = SelectedConnectionType == ConnectionType.Serial
                ? new SerialConfig { BaudRate = BaudRate }
                : null,
            SetAsDefault = SetAsDefault
        };

        var progress = new Progress<string>(msg =>
        {
            StatusMessage = msg;
            InstallLog.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        });

        try
        {
            var result = await _installer.InstallAsync(request, progress, ct);

            foreach (var step in result.Steps)
                if (!InstallLog.Any(l => l.Contains(step)))
                    InstallLog.Add($"  ✓ {step}");

            if (result.Success)
            {
                IsSuccess = true;
                StatusMessage = result.Message;
                InstallLog.Add($"✅ {result.Message}");
            }
            else
            {
                HasError = true;
                StatusMessage = result.Message;
                InstallLog.Add($"❌ {result.Message}");
                if (result.ErrorDetails != null)
                    InstallLog.Add($"   Detalhe: {result.ErrorDetails}");
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Instalação cancelada.";
            InstallLog.Add("⚠ Cancelado pelo usuário.");
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = $"Erro: {ex.Message}";
            InstallLog.Add($"❌ {ex.Message}");
            _log.Error("Installation error", ex);
        }
        finally
        {
            IsInstalling = false;
        }
    }

    [RelayCommand]
    private async Task TestPrintAsync()
    {
        if (string.IsNullOrWhiteSpace(PrinterName)) return;

        StatusMessage = "Enviando página de teste...";
        var result = await _installer.TestPrintAsync(PrinterName);
        StatusMessage = result.Message;
        InstallLog.Add($"[{DateTime.Now:HH:mm:ss}] {result.Message}");
    }
}
