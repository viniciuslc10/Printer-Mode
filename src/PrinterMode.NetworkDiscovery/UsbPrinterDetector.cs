using System.Management;
using PrinterMode.Core.Enums;
using PrinterMode.Core.Interfaces;
using PrinterMode.Core.Models;

namespace PrinterMode.NetworkDiscovery;

public class UsbPrinterDetector
{
    private readonly ILogService _log;

    public UsbPrinterDetector(ILogService log) => _log = log;

    public Task<IReadOnlyList<PrinterDevice>> DetectAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var devices = new List<PrinterDevice>();
            try
            {
                // Query USB printers via WMI
                using var searcher = new ManagementObjectSearcher(
                    @"SELECT * FROM Win32_PnPEntity WHERE ClassGuid='{4d36e979-e325-11ce-bfc1-08002be10318}'");

                foreach (ManagementObject obj in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();

                    var deviceId = obj["DeviceID"]?.ToString() ?? string.Empty;
                    var name = obj["Name"]?.ToString() ?? "USB Printer";
                    var status = obj["Status"]?.ToString();

                    if (!deviceId.Contains("USB", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var (vid, pid) = ExtractVidPid(deviceId);

                    devices.Add(new PrinterDevice
                    {
                        Id = deviceId,
                        DisplayName = name,
                        VendorId = vid,
                        ProductId = pid,
                        ConnectionType = ConnectionType.USB,
                        Status = status == "OK" ? PrinterStatus.Connected : PrinterStatus.Error,
                        DriverStatus = DetermineDriverStatus(obj),
                        PortName = ResolveUsbPort(deviceId),
                        DevicePath = deviceId
                    });

                    _log.Info($"USB printer detected: {name} (VID:{vid} PID:{pid})");
                }

                // Also query USB ports associated with printers
                AppendUsbPrinterPorts(devices, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Error("Error detecting USB printers", ex);
            }

            return (IReadOnlyList<PrinterDevice>)devices;
        }, ct);
    }

    private void AppendUsbPrinterPorts(List<PrinterDevice> existing, CancellationToken ct)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"SELECT * FROM Win32_USBControllerDevice");

            foreach (ManagementObject obj in searcher.Get())
            {
                ct.ThrowIfCancellationRequested();

                var dependent = obj["Dependent"]?.ToString() ?? string.Empty;
                if (dependent.Contains("USB", StringComparison.OrdinalIgnoreCase) &&
                    dependent.Contains("PRINT", StringComparison.OrdinalIgnoreCase))
                {
                    var (vid, pid) = ExtractVidPid(dependent);
                    if (vid != null && !existing.Any(d => d.VendorId == vid && d.ProductId == pid))
                    {
                        existing.Add(new PrinterDevice
                        {
                            Id = dependent,
                            DisplayName = $"USB Printer (VID:{vid} PID:{pid})",
                            VendorId = vid,
                            ProductId = pid,
                            ConnectionType = ConnectionType.USB,
                            Status = PrinterStatus.Connected,
                            DriverStatus = DriverStatus.Unknown,
                            DevicePath = dependent
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"USB controller query: {ex.Message}");
        }
    }

    private static (string? vid, string? pid) ExtractVidPid(string deviceId)
    {
        string? vid = null, pid = null;

        var vidIndex = deviceId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        if (vidIndex >= 0)
        {
            var start = vidIndex + 4;
            var end = deviceId.IndexOfAny(['&', '\\', ' '], start);
            vid = end > start ? deviceId[start..end] : deviceId[start..];
        }

        var pidIndex = deviceId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (pidIndex >= 0)
        {
            var start = pidIndex + 4;
            var end = deviceId.IndexOfAny(['&', '\\', ' '], start);
            pid = end > start ? deviceId[start..end] : deviceId[start..];
        }

        return (vid?.ToUpperInvariant(), pid?.ToUpperInvariant());
    }

    private static DriverStatus DetermineDriverStatus(ManagementObject obj)
    {
        var configManagerCode = obj["ConfigManagerErrorCode"];
        if (configManagerCode == null) return DriverStatus.Unknown;

        return Convert.ToInt32(configManagerCode) switch
        {
            0 => DriverStatus.Installed,
            28 => DriverStatus.NotInstalled,
            _ => DriverStatus.Error
        };
    }

    private static string? ResolveUsbPort(string deviceId)
    {
        // USB printer ports follow pattern USB00X
        // This is a heuristic; real mapping requires querying printer ports
        if (deviceId.Contains("USBPRINT", StringComparison.OrdinalIgnoreCase))
            return "USB001";
        return null;
    }
}
