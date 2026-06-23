using System.Management;
using PrinterMode.Core.Enums;
using PrinterMode.Core.Interfaces;
using PrinterMode.Core.Models;

namespace PrinterMode.NetworkDiscovery;

public class InstalledPrinterDetector
{
    private readonly ILogService _log;

    public InstalledPrinterDetector(ILogService log) => _log = log;

    public Task<IReadOnlyList<PrinterDevice>> DetectAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var devices = new List<PrinterDevice>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"SELECT * FROM Win32_Printer WHERE Local=True");

                foreach (ManagementObject obj in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();

                    var name = obj["Name"]?.ToString() ?? "Printer";
                    var portName = obj["PortName"]?.ToString() ?? string.Empty;
                    var driverName = obj["DriverName"]?.ToString();
                    var status = Convert.ToInt32(obj["PrinterStatus"] ?? 0);
                    var network = Convert.ToBoolean(obj["Network"] ?? false);

                    if (network) continue;

                    var connectionType = DetermineConnectionType(portName);

                    devices.Add(new PrinterDevice
                    {
                        Id = $"INSTALLED_{name}",
                        DisplayName = name,
                        ConnectionType = connectionType,
                        Status = MapPrinterStatus(status),
                        DriverStatus = DriverStatus.Installed,
                        PortName = portName,
                        InstalledDriverName = driverName
                    });

                    _log.Info($"Installed printer: {name} on {portName} using {driverName}");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Error("Error detecting installed printers", ex);
            }

            return (IReadOnlyList<PrinterDevice>)devices;
        }, ct);
    }

    private static ConnectionType DetermineConnectionType(string portName)
    {
        if (portName.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
            return ConnectionType.USB;
        if (portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            return ConnectionType.Serial;
        if (portName.StartsWith("IP_", StringComparison.OrdinalIgnoreCase) ||
            portName.StartsWith("192.", StringComparison.OrdinalIgnoreCase) ||
            portName.Contains("TCP", StringComparison.OrdinalIgnoreCase))
            return ConnectionType.Network;
        return ConnectionType.Unknown;
    }

    private static PrinterStatus MapPrinterStatus(int status) => status switch
    {
        3 => PrinterStatus.Connected,   // Idle
        4 => PrinterStatus.Connected,   // Printing
        5 => PrinterStatus.Error,       // Warming Up
        _ => PrinterStatus.Disconnected
    };
}
