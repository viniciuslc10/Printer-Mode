using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrinterMode.Core.Interfaces;
using PrinterMode.Core.Models;

namespace PrinterMode.UI.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly IPrinterDetector _detector;
    private readonly IDriverRepository _repository;
    private readonly ILogService _log;

    public event EventHandler? InstallRequested;

    [ObservableProperty]
    private ObservableCollection<PrinterDevice> _detectedDevices = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedDevice))]
    private PrinterDevice? _selectedDevice;

    [ObservableProperty]
    private DriverInfo? _autoDetectedDriver;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _scanStatus = "Clique em Escanear para detectar impressoras.";

    [ObservableProperty]
    private string _lastScanTime = string.Empty;

    public bool HasSelectedDevice => SelectedDevice != null;

    public DashboardViewModel(IPrinterDetector detector, IDriverRepository repository, ILogService log)
    {
        _detector = detector;
        _repository = repository;
        _log = log;
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ScanAsync(CancellationToken ct)
    {
        IsScanning = true;
        ScanStatus = "Escaneando dispositivos...";
        DetectedDevices.Clear();
        AutoDetectedDriver = null;

        try
        {
            var devices = await _detector.DetectAllAsync(ct);

            foreach (var device in devices)
            {
                DetectedDevices.Add(device);

                // Try auto-detect driver from VID/PID
                if (device.VendorId != null && device.ProductId != null)
                {
                    var driver = await _repository.FindByVidPidAsync(device.VendorId, device.ProductId);
                    if (driver != null)
                    {
                        device.DisplayName = driver.DisplayName;
                        _log.Info($"Auto-matched {device.DisplayName} to driver {driver.Id}");
                    }
                }
            }

            ScanStatus = DetectedDevices.Count == 0
                ? "Nenhuma impressora encontrada."
                : $"{DetectedDevices.Count} impressora(s) detectada(s).";

            LastScanTime = $"Última varredura: {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "Varredura cancelada.";
        }
        catch (Exception ex)
        {
            ScanStatus = $"Erro ao escanear: {ex.Message}";
            _log.Error("Scan failed", ex);
        }
        finally
        {
            IsScanning = false;
        }
    }

    partial void OnSelectedDeviceChanged(PrinterDevice? value)
    {
        if (value?.VendorId == null) return;

        _ = Task.Run(async () =>
        {
            AutoDetectedDriver = await _repository.FindByVidPidAsync(
                value.VendorId!, value.ProductId ?? string.Empty);
        });
    }

    [RelayCommand]
    private void RequestInstall()
    {
        InstallRequested?.Invoke(this, EventArgs.Empty);
    }
}
