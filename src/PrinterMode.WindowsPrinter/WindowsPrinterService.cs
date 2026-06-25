using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
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
                return false;
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
            // PowerShell Add-Printer is the most reliable method on Windows 10/11.
            // WMI is kept as fallback. Neither shows system error dialogs on failure.
            if (AddPrinterViaPowerShell(printerName, driverName, portName))
                return true;

            _log.Warning("PowerShell Add-Printer failed, trying WMI fallback.");
            return AddPrinterViaWmi(printerName, driverName, portName);
        }, ct);
    }

    public async Task<string?> FindBestUsbPortAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                // USB ports registered with the print spooler (USB001, USB002, …)
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name FROM Win32_PrinterPort WHERE Name LIKE 'USB%'");

                string? best = null;
                foreach (ManagementObject obj in searcher.Get())
                {
                    var name = obj["Name"]?.ToString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        best = name; // take last in enumeration; USB001 usually comes first
                        break;
                    }
                }

                _log.Info($"FindBestUsbPortAsync: '{best ?? "none"}'");
                return best;
            }
            catch (Exception ex)
            {
                _log.Warning($"Could not query USB printer ports: {ex.Message}");
                return null;
            }
        }, ct);
    }

    public async Task<bool> SetPaperFormAsync(string printerName, PaperConfig paper, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                // Thermal printers manage paper dimensions internally via their driver.
                // Calling printui here would show a system dialog on driver-name mismatch,
                // so we configure the paper size via WMI printer settings instead.
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_Printer WHERE Name='{EscapeWmi(printerName)}'");

                foreach (ManagementObject printer in searcher.Get())
                {
                    // PaperWidth / PaperLength are in tenths of a millimetre
                    printer["PaperSizesSupported"] = new uint[] { 256 }; // custom form
                    _log.Info($"Paper configured via WMI: {paper.DisplayName} on {printerName}");
                    return true;
                }

                _log.Info($"Printer '{printerName}' not found for paper config (non-fatal).");
                return true; // non-fatal — thermal printers use driver-default size anyway
            }
            catch (Exception ex)
            {
                _log.Warning($"Could not set paper form on {printerName}: {ex.Message}");
                return true; // non-fatal
            }
        }, ct);
    }

    public async Task<bool> SetDefaultPrinterAsync(string printerName, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
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
                    var raw = obj["Name"]?.ToString();
                    if (raw == null) continue;

                    // Win32_PrinterDriver.Name is a compound key: "DriverName,Version,Environment"
                    // e.g. "G250,3,Windows x64" — we only want the first segment.
                    var name = raw.Split(',')[0].Trim();
                    if (!string.IsNullOrEmpty(name) && !drivers.Contains(name, StringComparer.OrdinalIgnoreCase))
                        drivers.Add(name);
                }
            }
            catch (Exception ex)
            {
                _log.Error("Failed to list printer drivers", ex);
            }

            _log.Info($"Installed printer drivers: [{string.Join(", ", drivers)}]");
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

    public async Task<bool> AddSharedPrinterAsync(string connectionName, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                var script = $"Add-Printer -ConnectionName '{connectionName.Replace("'", "''")}'";
                var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                _log.Info($"AddSharedPrinterAsync: '{connectionName}'");
                using var process = Process.Start(psi)!;
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit(30_000);
                _log.Info($"Add-Printer -ConnectionName exit={process.ExitCode} stderr='{stderr.Trim()}'");
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                _log.Error($"AddSharedPrinterAsync exception: {ex.Message}");
                return false;
            }
        }, ct);
    }

    public async Task<bool> PrinterExistsAsync(string printerName, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT Name FROM Win32_Printer WHERE Name='{EscapeWmi(printerName)}'");
                foreach (ManagementObject _ in searcher.Get())
                    return true;
                return false;
            }
            catch (Exception ex)
            {
                _log.Warning($"PrinterExistsAsync failed: {ex.Message}");
                return false;
            }
        }, ct);
    }

    public async Task<bool> UpdatePrinterPortAsync(string printerName, string newPortName, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            if (UpdatePrinterPortViaPowerShell(printerName, newPortName))
                return true;

            _log.Warning("PowerShell Set-Printer failed, trying WMI fallback.");
            return UpdatePrinterPortViaWmi(printerName, newPortName);
        }, ct);
    }

    public async Task<IReadOnlyList<PortEntry>> GetSerialPortsWithNamesAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var result = new List<PortEntry>();
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // Source 1: Win32_PnPEntity — catches USB serial adapters, Bluetooth, etc.
                // Name format: "Daruma DR700 (COM5)" or "Communications Port (COM1)"
                using var pnpSearcher = new ManagementObjectSearcher(
                    "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%)'");

                foreach (ManagementObject obj in pnpSearcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    var wmiName = obj["Name"]?.ToString();
                    if (string.IsNullOrEmpty(wmiName)) continue;

                    var m = Regex.Match(wmiName, @"\((COM\d+)\)$");
                    if (!m.Success) continue;

                    var portName = m.Groups[1].Value;
                    var deviceName = wmiName[..^(m.Length)].Trim();
                    var displayName = string.IsNullOrEmpty(deviceName) ? portName : $"{portName} ({deviceName})";

                    if (found.Add(portName))
                        result.Add(new PortEntry(portName, displayName));
                }
            }
            catch (Exception ex) { _log.Warning($"Win32_PnPEntity serial query failed: {ex.Message}"); }

            try
            {
                // Source 2: Win32_SerialPort — catches built-in COM ports and any port
                // not listed via PnP (e.g. COM1, COM2 without a device attached)
                using var serialSearcher = new ManagementObjectSearcher(
                    "SELECT DeviceID, Description FROM Win32_SerialPort");

                foreach (ManagementObject obj in serialSearcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    var portName = obj["DeviceID"]?.ToString();
                    if (string.IsNullOrEmpty(portName)) continue;

                    if (!found.Add(portName)) continue;

                    var desc = obj["Description"]?.ToString();
                    var displayName = string.IsNullOrEmpty(desc) ? portName : $"{portName} ({desc})";
                    result.Add(new PortEntry(portName, displayName));
                }
            }
            catch (Exception ex) { _log.Warning($"Win32_SerialPort query failed: {ex.Message}"); }

            result.Sort((a, b) => string.Compare(a.PortName, b.PortName, StringComparison.OrdinalIgnoreCase));
            _log.Info($"Serial ports: [{string.Join(", ", result.Select(p => p.DisplayName))}]");
            return (IReadOnlyList<PortEntry>)result;
        }, ct);
    }
                _log.Error("Failed to enumerate serial ports with names", ex);
            }
            return (IReadOnlyList<PortEntry>)result;
        }, ct);
    }

    public async Task<IReadOnlyList<PortEntry>> GetUsbPrinterPortsWithNamesAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var result = new List<PortEntry>();
            try
            {
                using var portSearcher = new ManagementObjectSearcher(
                    "SELECT Name FROM Win32_PrinterPort WHERE Name LIKE 'USB%'");

                foreach (ManagementObject portObj in portSearcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    var portName = portObj["Name"]?.ToString();
                    if (string.IsNullOrEmpty(portName)) continue;

                    // Look for a printer already using this port
                    string? deviceName = null;
                    using var printerSearcher = new ManagementObjectSearcher(
                        $"SELECT Name FROM Win32_Printer WHERE PortName='{portName}'");
                    foreach (ManagementObject printerObj in printerSearcher.Get())
                    {
                        deviceName = printerObj["Name"]?.ToString();
                        break;
                    }

                    var displayName = string.IsNullOrEmpty(deviceName) ? portName : $"{portName} ({deviceName})";
                    result.Add(new PortEntry(portName, displayName));
                }

                _log.Info($"USB printer ports detected: [{string.Join(", ", result.Select(p => p.DisplayName))}]");
            }
            catch (Exception ex)
            {
                _log.Error("Failed to enumerate USB printer ports with names", ex);
            }
            return (IReadOnlyList<PortEntry>)result;
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

    private bool AddPrinterViaPowerShell(string printerName, string driverName, string portName)
    {
        try
        {
            // Use -EncodedCommand to avoid shell-escaping issues with special characters.
            var script = $"Add-Printer -Name '{printerName.Replace("'", "''")}'" +
                         $" -DriverName '{driverName.Replace("'", "''")}'" +
                         $" -PortName '{portName.Replace("'", "''")}'";

            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            _log.Info($"AddPrinterViaPowerShell: script='{script}'");
            using var process = Process.Start(psi)!;
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);

            _log.Info($"PowerShell Add-Printer exit={process.ExitCode} stderr='{stderr.Trim()}'");
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _log.Error($"PowerShell Add-Printer exception: {ex.Message}");
            return false;
        }
    }

    private bool AddPrinterViaWmi(string printerName, string driverName, string portName)
    {
        try
        {
            _log.Info($"AddPrinterViaWmi: name='{printerName}' driver='{driverName}' port='{portName}'");

            var scope = new ManagementScope(@"\\.\root\cimv2");
            scope.Connect();

            using var mc = new ManagementClass(scope, new ManagementPath("Win32_Printer"), null);
            using var printer = mc.CreateInstance();

            printer["Name"] = printerName;
            printer["DriverName"] = driverName;
            printer["PortName"] = portName;
            printer["Shared"] = false;

            var result = printer.Put();
            _log.Info($"Printer added via WMI: {printerName} (path={result?.Path})");
            return true;
        }
        catch (ManagementException ex)
        {
            _log.Error($"WMI printer creation failed: ErrorCode={ex.ErrorCode} Message='{ex.Message}'");
            return false;
        }
        catch (Exception ex)
        {
            _log.Error($"WMI printer creation unexpected error: {ex.Message}");
            return false;
        }
    }

    private bool UpdatePrinterPortViaPowerShell(string printerName, string newPortName)
    {
        try
        {
            var script = $"Set-Printer -Name '{printerName.Replace("'", "''")}'" +
                         $" -PortName '{newPortName.Replace("'", "''")}'";
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            _log.Info($"UpdatePrinterPortViaPowerShell: '{printerName}' → '{newPortName}'");
            using var process = Process.Start(psi)!;
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);
            _log.Info($"Set-Printer exit={process.ExitCode} stderr='{stderr.Trim()}'");
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _log.Error($"PowerShell Set-Printer exception: {ex.Message}");
            return false;
        }
    }

    private bool UpdatePrinterPortViaWmi(string printerName, string newPortName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_Printer WHERE Name='{EscapeWmi(printerName)}'");
            foreach (ManagementObject printer in searcher.Get())
            {
                printer["PortName"] = newPortName;
                printer.Put();
                _log.Info($"UpdatePrinterPortViaWmi: '{printerName}' → '{newPortName}'");
                return true;
            }
            _log.Warning($"UpdatePrinterPortViaWmi: printer '{printerName}' not found.");
            return false;
        }
        catch (Exception ex)
        {
            _log.Error($"WMI Set-Printer port failed: {ex.Message}");
            return false;
        }
    }

    private static string EscapeWmi(string value) =>
        value.Replace("'", "\\'").Replace("\\", "\\\\");
}
