using PrinterMode.Core.Models;

namespace PrinterMode.Core.Interfaces;

public interface IWindowsPrinterService
{
    Task<bool> CreateTcpIpPortAsync(string portName, string ipAddress, int port, CancellationToken ct = default);
    Task<bool> CreateUsbPortAsync(string portName, CancellationToken ct = default);
    Task<bool> AddPrinterAsync(string printerName, string driverName, string portName, CancellationToken ct = default);
    Task<bool> PrinterExistsAsync(string printerName, CancellationToken ct = default);
    Task<bool> UpdatePrinterPortAsync(string printerName, string newPortName, CancellationToken ct = default);
    Task<bool> AddSharedPrinterAsync(string connectionName, CancellationToken ct = default);
    Task<(bool ok, string error)> AddSharedPrinterInternalAsync(string connectionName, CancellationToken ct);
    Task<bool> SetPaperFormAsync(string printerName, PaperConfig paper, CancellationToken ct = default);
    Task<bool> SetDefaultPrinterAsync(string printerName, CancellationToken ct = default);
    Task<bool> DeletePrinterAsync(string printerName, CancellationToken ct = default);
    Task<bool> PrintTestPageAsync(string printerName, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetInstalledPrintersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetInstalledDriversAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetAvailablePortsAsync(CancellationToken ct = default);
    Task<string?> FindBestUsbPortAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PortEntry>> GetSerialPortsWithNamesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PortEntry>> GetUsbPrinterPortsWithNamesAsync(CancellationToken ct = default);
    Task<string?> FindDriverNameFromAutoInstalledPrinterAsync(string manufacturerHint, string modelHint, CancellationToken ct = default);
    Task<(string? DriverName, string? PortName)> FindAutoInstalledPrinterInfoAsync(string manufacturerHint, string modelHint, CancellationToken ct = default);
    Task RestartSpoolerAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetSharedPrintersAsync(string host, CancellationToken ct = default);
    Task<bool> CreateLprPortAsync(string portName, string host, string queueName, CancellationToken ct = default);
    Task EnableLpdServiceAsync(CancellationToken ct = default);
    Task<bool> TryEnableLpdRemotelyAsync(string host, CancellationToken ct = default);
    Task<bool> IsLpdAvailableAsync(string host, CancellationToken ct = default);
    Task<bool> SharePrinterAsync(string printerName, string shareName, CancellationToken ct = default);
}
