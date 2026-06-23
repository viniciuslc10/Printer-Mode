using PrinterMode.Core.Interfaces;
using PrinterMode.Core.Models;

namespace PrinterMode.NetworkDiscovery;

public class PrinterDetectorService : IPrinterDetector
{
    private readonly UsbPrinterDetector _usbDetector;
    private readonly SerialPortDetector _serialDetector;
    private readonly NetworkPrinterDetector _networkDetector;
    private readonly InstalledPrinterDetector _installedDetector;
    private readonly ILogService _log;

    public PrinterDetectorService(ILogService log)
    {
        _log = log;
        _usbDetector = new UsbPrinterDetector(log);
        _serialDetector = new SerialPortDetector(log);
        _networkDetector = new NetworkPrinterDetector(log);
        _installedDetector = new InstalledPrinterDetector(log);
    }

    public async Task<IReadOnlyList<PrinterDevice>> DetectAllAsync(CancellationToken ct = default)
    {
        _log.Info("Starting full printer detection...");

        var tasks = new[]
        {
            DetectUsbAsync(ct),
            DetectSerialAsync(ct),
            DetectNetworkAsync(ct),
            DetectInstalledAsync(ct)
        };

        var results = await Task.WhenAll(tasks);
        var all = results.SelectMany(r => r).ToList();

        // Merge duplicates: prefer installed entries
        var merged = MergeDevices(all);

        _log.Info($"Detection complete: {merged.Count} device(s) found.");
        return merged;
    }

    public Task<IReadOnlyList<PrinterDevice>> DetectUsbAsync(CancellationToken ct = default) =>
        _usbDetector.DetectAsync(ct);

    public Task<IReadOnlyList<PrinterDevice>> DetectSerialAsync(CancellationToken ct = default) =>
        _serialDetector.DetectAsync(ct);

    public Task<IReadOnlyList<PrinterDevice>> DetectNetworkAsync(CancellationToken ct = default) =>
        _networkDetector.DetectAsync(ct);

    public Task<IReadOnlyList<PrinterDevice>> DetectInstalledAsync(CancellationToken ct = default) =>
        _installedDetector.DetectAsync(ct);

    private static IReadOnlyList<PrinterDevice> MergeDevices(IEnumerable<PrinterDevice> devices)
    {
        var merged = new Dictionary<string, PrinterDevice>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in devices)
        {
            var key = device.PortName ?? device.Id;

            if (!merged.TryGetValue(key, out var existing))
            {
                merged[key] = device;
                continue;
            }

            // Prefer the installed entry (has driver info)
            if (device.InstalledDriverName != null && existing.InstalledDriverName == null)
                merged[key] = device;
        }

        return merged.Values.ToList();
    }
}
