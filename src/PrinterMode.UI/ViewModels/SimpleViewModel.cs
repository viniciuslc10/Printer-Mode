using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
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
    private readonly IWindowsPrinterService _printerService;
    private readonly ILogService _log;

    public ObservableCollection<string> Manufacturers { get; } = [];
    public ObservableCollection<DriverInfo> Models { get; } = [];
    public ObservableCollection<PaperConfig> Papers { get; } = [];
    public ObservableCollection<PortEntry> ComPorts { get; } = [];
    public ObservableCollection<PortEntry> UsbPorts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(HasManufacturer))]
    private string? _selectedManufacturer;

    public bool HasManufacturer => SelectedManufacturer != null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private DriverInfo? _selectedModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNetworkFields))]
    [NotifyPropertyChangedFor(nameof(ShowSerialFields))]
    [NotifyPropertyChangedFor(nameof(ShowSharedFields))]
    [NotifyPropertyChangedFor(nameof(ShowUsbPortSelector))]
    private bool _connectionUsb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNetworkFields))]
    [NotifyPropertyChangedFor(nameof(ShowSerialFields))]
    [NotifyPropertyChangedFor(nameof(ShowSharedFields))]
    [NotifyPropertyChangedFor(nameof(ShowUsbPortSelector))]
    private bool _connectionNetwork;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNetworkFields))]
    [NotifyPropertyChangedFor(nameof(ShowSerialFields))]
    [NotifyPropertyChangedFor(nameof(ShowSharedFields))]
    [NotifyPropertyChangedFor(nameof(ShowUsbPortSelector))]
    private bool _connectionSerial;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNetworkFields))]
    [NotifyPropertyChangedFor(nameof(ShowSerialFields))]
    [NotifyPropertyChangedFor(nameof(ShowSharedFields))]
    [NotifyPropertyChangedFor(nameof(ShowUsbPortSelector))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _connectionShared;

    [ObservableProperty] private string _ipAddress = string.Empty;
    [ObservableProperty] private int _networkPort = 9100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private string _sharedHost = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private string _sharedPrinterName = string.Empty;
    [ObservableProperty] private ObservableCollection<string> _sharedPrinterList = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSharedPrinters))]
    private string? _selectedSharedPrinter;
    private string? _discoveredDriverName;
    private string? _discoveredDisplayName;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSharedPrinters))]
    private bool _isSearchingShared;
    public bool HasSharedPrinters => SharedPrinterList.Count > 0;
    [ObservableProperty] private PortEntry? _selectedComPort;
    [ObservableProperty] private PortEntry? _selectedUsbPort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUsbPortSelector))]
    private bool _hasUsbPorts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private string _printerName = string.Empty;

    [ObservableProperty] private PaperConfig? _selectedPaper;
    [ObservableProperty] private bool _setAsDefault;

    [ObservableProperty] private string _statusText = "Selecione o fabricante e modelo para continuar.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _isInstalling;
    [ObservableProperty] private bool _showSuccess;
    [ObservableProperty] private bool _showError;

    public bool CanInstall => !IsInstalling && (
        ConnectionShared
            ? !string.IsNullOrWhiteSpace(SharedHost) && !string.IsNullOrWhiteSpace(SharedPrinterName)
            : SelectedModel != null && !string.IsNullOrWhiteSpace(PrinterName));
    public bool ShowNetworkFields => ConnectionNetwork;
    public bool ShowSerialFields => ConnectionSerial;
    public bool ShowSharedFields => ConnectionShared;
    // USB installs on the standard USB001 port automatically — no manual choice needed.
    // The selector only appears if the system already has real USB printer ports listed.
    public bool ShowUsbPortSelector => ConnectionUsb && HasUsbPorts;

    public SimpleViewModel(IDriverRepository repository, IDriverInstaller installer, IWindowsPrinterService printerService, ILogService log)
    {
        _repository = repository;
        _installer = installer;
        _printerService = printerService;
        _log = log;
    }

    public async Task LoadAsync()
    {
        var manufacturers = await _repository.GetManufacturersAsync();
        foreach (var m in manufacturers)
            Manufacturers.Add(m);

        await RefreshPortsAsync();
    }

    [RelayCommand]
    private async Task RefreshPortsAsync()
    {
        ComPorts.Clear();
        UsbPorts.Clear();

        var comPorts = await _printerService.GetSerialPortsWithNamesAsync();
        foreach (var p in comPorts)
            ComPorts.Add(p);
        SelectedComPort = ComPorts.FirstOrDefault();

        // USB uses the standard spooler port (USB001) that the USB Print Monitor creates —
        // that's how these printers install, so no manual port choice is needed for USB.
        // (COM ports live on the Serial tab; IP on the Network tab.)
        var usbPorts = await _printerService.GetUsbPrinterPortsWithNamesAsync();
        foreach (var p in usbPorts)
            UsbPorts.Add(p);
        HasUsbPorts = UsbPorts.Count > 0;
        SelectedUsbPort = UsbPorts.FirstOrDefault();
    }

    partial void OnSelectedSharedPrinterChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value))
            SharedPrinterName = value;
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

        StatusText = $"Modelo: {value.DisplayName}. Clique em Instalar para continuar.";
    }

    partial void OnConnectionSharedChanged(bool value)
    {
        if (value)
            StatusText = "Digite o IP/host e clique em Buscar para encontrar as impressoras compartilhadas.";
        else if (SelectedModel == null)
            StatusText = "Selecione o fabricante e modelo para continuar.";
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
            var driver = SelectedModel ?? new DriverInfo();
            var paper = SelectedPaper ?? (SelectedModel?.DefaultPaper ?? new PaperConfig { Name = "Padrão", WidthMm = 80 });

            var connType = ConnectionNetwork ? ConnectionType.Network
                         : ConnectionSerial ? ConnectionType.Serial
                         : ConnectionShared ? ConnectionType.Shared
                         : ConnectionType.USB;

            // For shared printers, use the share name as the printer name (model not required)
            var printerNameForRequest = ConnectionShared
                ? SharedPrinterName.Trim()
                : PrinterName;

            var request = new InstallRequest
            {
                Driver = driver,
                PrinterName = printerNameForRequest,
                ConnectionType = connType,
                IpAddress = ConnectionNetwork ? IpAddress : null,
                NetworkPort = ConnectionNetwork ? NetworkPort : 9100,
                SharedHost = ConnectionShared ? SharedHost : null,
                SharedPrinterName = ConnectionShared ? SharedPrinterName : null,
                SharedDriverName = ConnectionShared ? _discoveredDriverName : null,
                SharedDisplayName = ConnectionShared ? _discoveredDisplayName : null,
                // For USB, a non-empty selected port means "use exactly this port"; the
                // "Automático" entry has an empty PortName → null → app auto-detects.
                PortName = ConnectionSerial ? SelectedComPort?.PortName
                         : ConnectionUsb && !string.IsNullOrEmpty(SelectedUsbPort?.PortName) ? SelectedUsbPort!.PortName
                         : null,
                Paper = paper,
                SetAsDefault = SetAsDefault,
                SkipDriverInstall = false
            };

            var result = await _installer.InstallAsync(
                request,
                new Progress<string>(msg => StatusText = msg),
                ct);

            if (result.Success)
            {
                ShowSuccess = true;
                StatusText = result.Message;
                // Refresh ports after install — a new USB port may have been registered
                _ = RefreshPortsAsync();
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

    [RelayCommand]
    private async Task SearchSharedPrintersAsync()
    {
        var host = SharedHost.Trim();
        if (string.IsNullOrEmpty(host)) return;

        IsSearchingShared = true;
        SharedPrinterList.Clear();
        StatusText = $"Conectando com '{host}'...";
        try
        {
            var hostReachable = await PingHostAsync(host);
            if (!hostReachable)
            {
                StatusText = $"Computador '{host}' não encontrado na rede.\n" +
                             "Verifique o IP, se o computador está ligado e na mesma rede.";
                return;
            }

            // Fast path: query the PrinterMode discovery port (9876) on the remote PC.
            // Returns "shareName|displayName" per line — no authentication required.
            StatusText = $"Buscando impressoras em '{host}'...";
            var discovered = await _printerService.GetRemoteSharedPrintersAsync(host);

            if (discovered.Count > 0)
            {
                // Parse "shareName|displayName|driverName" format
                var first = discovered[0].Split('|');
                var shareName   = first[0];
                var displayName = first.Length > 1 ? first[1] : first[0];
                _discoveredDriverName  = first.Length > 2 && !string.IsNullOrEmpty(first[2]) ? first[2] : null;
                _discoveredDisplayName = displayName;

                SharedPrinterList.Clear();
                foreach (var entry in discovered)
                    SharedPrinterList.Add(entry.Split('|')[0]); // store shareName

                // Setting SelectedSharedPrinter triggers OnSelectedSharedPrinterChanged
                // which sets SharedPrinterName — keeping the two in sync.
                SelectedSharedPrinter = shareName;
                SharedPrinterName = shareName;

                StatusText = $"✓ Impressora encontrada: '{displayName}'.\n" +
                             $"Clique em Instalar para conectar.";
                return;
            }

            // Fallback: check LPD port (works even when PrinterMode app is closed on PC-A,
            // since LPDSVC auto-starts with Windows after the first activation).
            StatusText = $"Verificando serviço LPD em '{host}'...";
            bool lpdAvailable = await _printerService.IsLpdAvailableAsync(host);

            if (!lpdAvailable)
            {
                // Try remote sc.exe activation (works when credentials match).
                StatusText = $"Ativando serviço LPD em '{host}'...";
                await _printerService.TryEnableLpdRemotelyAsync(host);
                await Task.Delay(2000);
                lpdAvailable = await _printerService.IsLpdAvailableAsync(host);
            }

            if (lpdAvailable)
            {
                StatusText = $"✓ Serviço LPD ativo em '{host}'.\n\n" +
                             $"Digite o nome do compartilhamento no campo abaixo e clique em Instalar.\n\n" +
                             $"Para encontrar o nome: no computador '{host}' → clique direito na impressora → " +
                             $"Propriedades → aba Compartilhamento → campo 'Nome do compartilhamento'.";
            }
            else
            {
                StatusText = $"Serviço LPD não está ativo no computador '{host}'.\n\n" +
                             $"Abra o PrinterMode como Administrador no computador '{host}'. " +
                             $"O LPD é ativado automaticamente ao abrir o aplicativo.";
            }
        }
        finally
        {
            IsSearchingShared = false;
            OnPropertyChanged(nameof(HasSharedPrinters));
        }
    }

    private static async Task<bool> PingHostAsync(string host)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 2000);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }
}
