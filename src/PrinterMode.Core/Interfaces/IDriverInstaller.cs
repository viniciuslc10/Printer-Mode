using PrinterMode.Core.Models;

namespace PrinterMode.Core.Interfaces;

public interface IDriverInstaller
{
    Task<InstallResult> InstallAsync(InstallRequest request, IProgress<string>? progress = null, CancellationToken ct = default);
    Task<bool> UninstallAsync(string printerName, CancellationToken ct = default);
    Task<bool> IsDriverInstalledAsync(string driverName, CancellationToken ct = default);
    Task<InstallResult> TestPrintAsync(string printerName, CancellationToken ct = default);
}
