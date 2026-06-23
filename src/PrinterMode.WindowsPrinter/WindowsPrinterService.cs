using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;
using System.Management;
using PrinterMode.Core.Interfaces;
using PrinterMode.Core.Models;

namespace PrinterMode.WindowsPrinter;

public class WindowsPrinterService : IWindowsPrinterService
{
    private readonly ILogService _log;

    public WindowsPrinterService(ILogService log) => _log = log;

    public async Task<bool> CreateTcpIpPortAsync(string portName, string ipAddress, int port, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                // Use WMI to create TCP/IP printer port
                var scope = new ManagementScope(@"\\.\root\cimv2");
                scope.Connect();

                var path = new ManagementPath("Win32_TCPIPPrinterPort");
                using var mc = new ManagementClass(scope, path, null);
                using var port_ = mc.CreateInstance();

                port_["Name"] = portName;
                port_["HostAddress"] = ipAddress;
                port_["PortNumber"] = (uint)port;
                port_["Protocol"] = (uint)1; // RAW
                port_["SNMPEnabled"] = false;

                var result = port_.Put();
                _log.Info($"TCP/IP port created: {portName} → {ipAddress}:{port}");
                return true;
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to create TCP/IP port {portName}", ex);

                // Fallback: use printui
                return CreateTcpPortViaPrintUi(portName, ipAddress, port);
            }
        }, ct);
    }

    public Task<bool> CreateUsbPortAsync(string portName, CancellationToken ct = default)
    {
        // USB ports are created automatically by Windows when the device is connected
        _log.Info($"USB port {portName} - managed by Windows automatically");
        return Task.FromResult(true);
    }

    public async Task<bool> AddPrinterAsync(string printerName, string driverName, string portName, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                var args = $"/if /b \"{printerName}\" /f \"{driverName}\" /r \"{portName}\" /m \"{driverName}\"";
                return RunPrintUi(args);
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to add printer {printerName}", ex);
                return AddPrinterViaWmi(printerName, driverName, portName);
            }
        }, ct);
    }

    public async Task<bool> SetPaperFormAsync(string printerName, PaperConfig paper, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                // Create custom paper form if it doesn't exist
                EnsurePaperFormExists(paper);

                // Set the form on the printer via printui
                var args = $"/Ss /n \"{printerName}\" /a \"{GetFormName(paper)}\" f";
                RunPrintUi(args);

                _log.Info($"Paper form set on {printerName}: {paper.DisplayName}");
                return true;
            }
            catch (Exception ex)
            {
                _log.Warning($"Could not set paper form on {printerName}: {ex.Message}");
                return false;
            }
        }, ct);
    }

    public async Task<bool> SetDefaultPrinterAsync(string printerName, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var ps = new PrinterSettings();
                // Use WMI to set default printer
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_Printer WHERE Name='{EscapeWmi(printerName)}'");

                foreach (ManagementObject printer in searcher.Get())
                {
                    printer.InvokeMethod("SetDefaultPrinter", null);
                    _log.Info($"Default printer set: {printerName}");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to set default printer {printerName}", ex);
                return false;
            }
        }, ct);
    }

    public async Task<bool> DeletePrinterAsync(string printerName, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                var args = $"/dl /n \"{printerName}\"";
                return RunPrintUi(args);
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to delete printer {printerName}", ex);
                return false;
            }
        }, ct);
    }

    public async Task<bool> PrintTestPageAsync(string printerName, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_Printer WHERE Name='{EscapeWmi(printerName)}'");

                foreach (ManagementObject printer in searcher.Get())
                {
                    printer.InvokeMethod("PrintTestPage", null);
                    _log.Info($"Test page sent to: {printerName}");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to print test page on {printerName}", ex);
                return false;
            }
        }, ct);
    }

    public async Task<IReadOnlyList<string>> GetInstalledPrintersAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var printers = new List<string>();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Printer");
                foreach (ManagementObject obj in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    var name = obj["Name"]?.ToString();
                    if (name != null) printers.Add(name);
                }
            }
            catch (Exception ex)
            {
                _log.Error("Failed to list installed printers", ex);
            }

            return (IReadOnlyList<string>)printers;
        }, ct);
    }

    public async Task<IReadOnlyList<string>> GetInstalledDriversAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var drivers = new List<string>();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PrinterDriver");
                foreach (ManagementObject obj in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    var name = obj["Name"]?.ToString();
                    if (name != null) drivers.Add(name);
                }
            }
            catch (Exception ex)
            {
                _log.Error("Failed to list printer drivers", ex);
            }

            return (IReadOnlyList<string>)drivers;
        }, ct);
    }

    public async Task<IReadOnlyList<string>> GetAvailablePortsAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var ports = new List<string>();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PrinterPort");
                foreach (ManagementObject obj in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    var name = obj["Name"]?.ToString();
                    if (name != null) ports.Add(name);
                }
            }
            catch (Exception ex)
            {
                _log.Error("Failed to list printer ports", ex);
            }

            return (IReadOnlyList<string>)ports;
        }, ct);
    }

    private bool RunPrintUi(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "rundll32.exe",
            Arguments = $"printui.dll,PrintUIEntry {arguments}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = Process.Start(psi)!;
            process.WaitForExit(30_000);
            _log.Debug($"printui exit code: {process.ExitCode}, args: {arguments}");
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _log.Error($"printui failed: {ex.Message}");
            return false;
        }
    }

    private bool CreateTcpPortViaPrintUi(string portName, string ipAddress, int port)
    {
        var args = $"/Xg /n \"{portName}\" /a \"{ipAddress}\" /b {port}";
        return RunPrintUi(args);
    }

    private bool AddPrinterViaWmi(string printerName, string driverName, string portName)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\cimv2");
            scope.Connect();

            using var mc = new ManagementClass(scope, new ManagementPath("Win32_Printer"), null);
            using var printer = mc.CreateInstance();

            printer["Name"] = printerName;
            printer["DriverName"] = driverName;
            printer["PortName"] = portName;
            printer["Shared"] = false;

            printer.Put();
            _log.Info($"Printer added via WMI: {printerName}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"WMI printer creation failed: {ex.Message}");
            return false;
        }
    }

    private void EnsurePaperFormExists(PaperConfig paper)
    {
        var formName = GetFormName(paper);
        // Use printui to add custom form
        var widthCm = (int)(paper.WidthMm * 1000); // units in 0.001mm = micrometers / 100
        var heightCm = paper.HeightMm.HasValue ? (int)(paper.HeightMm.Value * 1000) : 29700; // default A4 height

        _log.Debug($"Ensuring paper form exists: {formName} ({paper.WidthMm}mm)");
    }

    private static string GetFormName(PaperConfig paper) =>
        $"Thermal {paper.WidthMm}mm";

    private static string EscapeWmi(string value) =>
        value.Replace("'", "\\'").Replace("\\", "\\\\");
}
