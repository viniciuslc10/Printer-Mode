using System.IO.Ports;
using System.Management;
using PrinterMode.Core.Enums;
using PrinterMode.Core.Interfaces;
using PrinterMode.Core.Models;

namespace PrinterMode.NetworkDiscovery;

public class SerialPortDetector
{
    private readonly ILogService _log;

    public SerialPortDetector(ILogService log) => _log = log;

    public Task<IReadOnlyList<PrinterDevice>> DetectAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var devices = new List<PrinterDevice>();
            try
            {
                var ports = SerialPort.GetPortNames();
                foreach (var port in ports)
                {
                    ct.ThrowIfCancellationRequested();

                    var description = GetPortDescription(port);
                    devices.Add(new PrinterDevice
                    {
                        Id = $"COM_{port}",
                        DisplayName = string.IsNullOrEmpty(description) ? port : $"{port} - {description}",
                        ConnectionType = ConnectionType.Serial,
                        Status = PrinterStatus.Connected,
                        DriverStatus = DriverStatus.Unknown,
                        PortName = port
                    });

                    _log.Info($"Serial port detected: {port} ({description})");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Error("Error detecting serial ports", ex);
            }

            return (IReadOnlyList<PrinterDevice>)devices;
        }, ct);
    }

    private string GetPortDescription(string portName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%{portName}%'");

            foreach (ManagementObject obj in searcher.Get())
            {
                return obj["Name"]?.ToString() ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Could not get description for {portName}: {ex.Message}");
        }

        return string.Empty;
    }
}
