using PrinterMode.Core.Models;

namespace PrinterMode.Core.Interfaces;

public interface IPrinterDetector
{
    Task<IReadOnlyList<PrinterDevice>> DetectAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PrinterDevice>> DetectUsbAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PrinterDevice>> DetectSerialAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PrinterDevice>> DetectNetworkAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PrinterDevice>> DetectInstalledAsync(CancellationToken ct = default);
}
