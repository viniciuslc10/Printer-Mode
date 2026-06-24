using PrinterMode.Core.Models;

namespace PrinterMode.Core.Interfaces;

public interface IWindowsPrinterService
{
    Task<bool> CreateTcpIpPortAsync(string portName, string ipAddress, int port, CancellationToken ct = default);
    Task<bool> CreateUsbPortAsync(string portName, CancellationToken ct = default);
    Task<bool> AddPrinterAsync(string printerName, string driverName, string portName, CancellationToken ct = default);
    Task<bool> SetPaperFormAsync(string printerName, PaperConfig paper, CancellationToken ct = default);
    Task<bool> SetDefaultPrinterAsync(string printerName, CancellationToken ct = default);
    Task<bool> DeletePrinterAsync(string printerName, CancellationToken ct = default);
    Task<bool> PrintTestPageAsync(string printerName, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetInstalledPrintersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetInstalledDriversAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetAvailablePortsAsync(CancellationToken ct = default);
    Task<string?> FindBestUsbPortAsync(CancellationToken ct = default);
}
