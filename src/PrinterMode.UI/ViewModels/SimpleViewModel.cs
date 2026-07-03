using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrinterMode.Core.Enums;
using PrinterMode.Core.Interfaces;
using PrinterMode.Core.Models;
using PrinterMode.WindowsPrinter;

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

        var usbPorts = await _printerService.GetUsbPrinterPortsWithNamesAsync();
        foreach (var p in usbPorts)
            UsbPorts.Add(p);
        HasUsbPorts = UsbPorts.Count > 0;
        SelectedUsbPort = UsbPorts.FirstOrDefault();
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
                PortName = ConnectionSerial ? SelectedComPort?.PortName
                         : ConnectionUsb && SelectedUsbPort != null ? SelectedUsbPort.PortName
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
        StatusText = $"Buscando impressoras em '{host}'...";
        try
        {
            var raw = await _printerService.GetSharedPrintersAsync(host);

            bool portClosed   = raw.Contains(WindowsPrinterService.DiagPortClosed);
            bool accessDenied = raw.Contains(WindowsPrinterService.DiagAccessDenied);

            // Real printer/share names — exclude diagnostic sentinel values
            var printers = raw.Where(p => !p.StartsWith("__DIAG:")).ToList();
            foreach (var p in printers)
                SharedPrinterList.Add(p);

            if (SharedPrinterList.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(SharedPrinterName))
                    SharedPrinterName = SharedPrinterList[0];

                var label = portClosed
                    ? $"{SharedPrinterList.Count} compartilhamento(s) encontrado(s) via navegação de rede."
                    : $"{SharedPrinterList.Count} impressora(s)/compartilhamento(s) encontrado(s).";
                StatusText = $"{label} Selecione e clique em Instalar.";
            }
            else
            {
                var hostReachable = await PingHostAsync(host);

                if (!hostReachable)
                {
                    StatusText = $"Host '{host}' não encontrado na rede.\n" +
                                 $"Verifique o IP, se o computador está ligado e se ambos estão na mesma rede.";
                    return;
                }

                if (portClosed)
                {
                    StatusText = $"Computador '{host}' encontrado, mas a porta 445 (SMB) está bloqueada.\n\n" +
                                 $"No computador '{host}' (com a impressora), abra o Painel de Controle → " +
                                 $"Central de Rede e Compartilhamento → Configurações de compartilhamento avançadas → " +
                                 $"ative 'Ativar descoberta de rede' e 'Ativar compartilhamento de arquivo e impressora'.\n\n" +
                                 $"Se souber o nome do compartilhamento, digite-o abaixo e clique em Instalar.";
                    return;
                }

                if (accessDenied)
                {
                    StatusText = $"Computador '{host}' encontrado e porta 445 aberta, mas acesso negado.\n\n" +
                                 $"No computador '{host}' (com a impressora):\n" +
                                 $"1. Painel de Controle → Opções de Pasta → aba Exibir → desative 'Usar Assistente de Compartilhamento'\n" +
                                 $"2. Central de Rede → perfil atual → mude para 'Rede Privada'\n" +
                                 $"3. Configurações de Compartilhamento → ative 'Desativar compartilhamento protegido por senha' (temporariamente)\n\n" +
                                 $"Ou anote o nome do compartilhamento (clique direito na impressora → Propriedades → aba Compartilhamento) " +
                                 $"e digite-o abaixo para instalar sem a descoberta automática.";
                    return;
                }

                // Connected and authenticated but genuinely no shared printers were found
                StatusText = $"Computador '{host}' acessível, mas nenhuma impressora compartilhada foi encontrada.\n\n" +
                             $"Verifique no computador '{host}':\n" +
                             $"1. Clique direito na impressora → Propriedades → aba 'Compartilhamento' → marque 'Compartilhar esta impressora'\n" +
                             $"2. Anote o 'Nome do compartilhamento' e digite-o no campo abaixo → clique em Instalar\n\n" +
                             $"O nome do compartilhamento é diferente do nome da impressora — verifique na aba Compartilhamento.";
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
