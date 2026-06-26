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
        // PowerShell Get-PrinterDriver queries the Print Spooler directly — no WMI cache lag.
        // WMI Win32_PrinterDriver can take 10-30 seconds to reflect a newly installed driver.
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"Get-PrinterDriver | Select-Object -ExpandProperty Name\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            var drivers = output
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (drivers.Count > 0)
            {
                _log.Info($"Installed printer drivers (PS): [{string.Join(", ", drivers)}]");
                return drivers;
            }

            // Empty result — Print Spooler may be restarting; fall through to WMI
            _log.Warning("PowerShell Get-PrinterDriver returned empty list, trying WMI...");
        }
        catch (Exception ex)
        {
            _log.Warning($"PowerShell Get-PrinterDriver failed ({ex.Message}), falling back to WMI");
        }

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
                    // Win32_PrinterDriver.Name compound key: "DriverName,Version,Environment"
                    var name = raw.Split(',')[0].Trim();
                    if (!string.IsNullOrEmpty(name) && !drivers.Contains(name, StringComparer.OrdinalIgnoreCase))
                        drivers.Add(name);
                }
            }
            catch (Exception ex)
            {
                _log.Error("WMI fallback also failed to list printer drivers", ex);
            }

            _log.Info($"Installed printer drivers (WMI): [{string.Join(", ", drivers)}]");
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

    public async Task<(bool ok, string error)> AddSharedPrinterInternalAsync(string connectionName, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            // Method 1: Add-Printer -ConnectionName (PrintManagement module)
            var (ok1, err1) = TryAddPrinterViaPs(connectionName);
            if (ok1) return (true, string.Empty);

            _log.Warning($"Add-Printer failed ('{err1}'), trying WScript.Network...");

            // Method 2: WScript.Network.AddWindowsPrinterConnection (COM/legacy — often succeeds when PS fails)
            var (ok2, err2) = TryAddPrinterViaWScript(connectionName);
            if (ok2) return (true, string.Empty);

            var finalError = string.IsNullOrEmpty(err1) ? err2 : err1;
            _log.Error($"Both methods failed for '{connectionName}'. Error: {finalError}");
            return (false, finalError);
        }, ct);
    }

    private (bool ok, string error) TryAddPrinterViaPs(string connectionName)
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
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            _log.Info($"TryAddPrinterViaPs: '{connectionName}'");
            using var proc = Process.Start(psi)!;
            var stderr = proc.StandardError.ReadToEnd();
            proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(30_000);
            var cleanError = ExtractPsError(stderr.Trim());
            _log.Info($"Add-Printer exit={proc.ExitCode} error='{cleanError}'");
            return (proc.ExitCode == 0, cleanError);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private (bool ok, string error) TryAddPrinterViaWScript(string connectionName)
    {
        try
        {
            var safe = connectionName.Replace("'", "''");
            var script = $"(New-Object -ComObject WScript.Network).AddWindowsPrinterConnection('{safe}')";
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
            _log.Info($"TryAddPrinterViaWScript: '{connectionName}'");
            using var proc = Process.Start(psi)!;
            var stderr = proc.StandardError.ReadToEnd();
            proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(30_000);
            var cleanError = ExtractPsError(stderr.Trim());
            _log.Info($"WScript.Network exit={proc.ExitCode} error='{cleanError}'");
            return (proc.ExitCode == 0, cleanError);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<bool> AddSharedPrinterAsync(string connectionName, CancellationToken ct = default)
    {
        var (ok, _) = await AddSharedPrinterInternalAsync(connectionName, ct);
        return ok;
    }

    public async Task<IReadOnlyList<string>> GetSharedPrintersAsync(string host, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var result = new List<string>();

            // Strategy 1: net view \\host — uses only SMB port 445 (standard Windows sharing).
            // Works whenever "File and Printer Sharing" is enabled, no WMI/RPC required.
            try
            {
                var psi = new ProcessStartInfo("net", $"view \\\\{host}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi)!;
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(10_000);

                if (proc.ExitCode == 0)
                {
                    var parsed = ParseNetViewShares(output);
                    result.AddRange(parsed);
                    _log.Info($"net view {host}: [{string.Join(", ", parsed)}]");
                }
                else
                {
                    _log.Warning($"net view exited {proc.ExitCode} for {host}");
                }
            }
            catch (Exception ex)
            {
                _log.Warning($"net view failed: {ex.Message}");
            }

            if (result.Count > 0)
                return (IReadOnlyList<string>)result;

            // Strategy 2: Get-Printer -ComputerName (needs WMI/RPC — may be blocked by firewall)
            try
            {
                var script = $"Get-Printer -ComputerName '{host.Replace("'", "''")}' | Select-Object -ExpandProperty ShareName | Where-Object {{ $_ -ne $null -and $_ -ne '' }}";
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
                using var proc = Process.Start(psi)!;
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(10_000);

                foreach (var line in output.Split('\n'))
                {
                    var name = line.Trim();
                    if (!string.IsNullOrEmpty(name) && !result.Contains(name, StringComparer.OrdinalIgnoreCase))
                        result.Add(name);
                }
                _log.Info($"Get-Printer -ComputerName {host}: [{string.Join(", ", result)}]");
            }
            catch (Exception ex)
            {
                _log.Warning($"Get-Printer -ComputerName failed: {ex.Message}");
            }

            return (IReadOnlyList<string>)result;
        }, ct);
    }

    // Parses "net view \\host" output and returns non-admin share names.
    // Works across locales — relies on the "---" separator line rather than column headers.
    private static List<string> ParseNetViewShares(string output)
    {
        var shares = new List<string>();
        bool inData = false;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;

            // The "------" separator marks the beginning of data rows
            if (line.TrimStart().StartsWith("---"))
            {
                inData = true;
                continue;
            }

            if (!inData) continue;

            // "The command completed successfully" / "O comando foi concluído" — end of data
            if (!line.StartsWith(" ") && char.IsLetter(line[0])) break;

            // Extract share name (first whitespace-delimited token)
            var parts = line.TrimStart().Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            var shareName = parts[0].Trim();
            if (string.IsNullOrEmpty(shareName)) continue;

            // Skip hidden/admin shares (end with $)
            if (shareName.EndsWith("$")) continue;

            shares.Add(shareName);
        }

        return shares;
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

    public async Task<string?> FindDriverNameFromAutoInstalledPrinterAsync(
        string manufacturerHint, string modelHint, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                // Search Win32_Printer for any printer that Windows auto-installed
                // that matches our manufacturer or model keywords
                using var searcher = new ManagementObjectSearcher("SELECT Name, DriverName FROM Win32_Printer");
                var mfgLow = manufacturerHint.ToLowerInvariant();
                var mdlLow = modelHint.Replace("-", "").Replace(" ", "").ToLowerInvariant();

                foreach (ManagementObject obj in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    var name = obj["Name"]?.ToString() ?? "";
                    var driverName = obj["DriverName"]?.ToString();
                    if (string.IsNullOrEmpty(driverName)) continue;

                    var nameLow = name.ToLowerInvariant().Replace("-", "").Replace(" ", "");
                    if (nameLow.Contains(mfgLow) || nameLow.Contains(mdlLow))
                    {
                        _log.Info($"Found auto-installed printer '{name}' using driver '{driverName}'");
                        return driverName;
                    }
                }

                // Broader fallback: any printer on a USB port that isn't a well-known system printer
                using var usbSearcher = new ManagementObjectSearcher(
                    "SELECT Name, DriverName, PortName FROM Win32_Printer WHERE PortName LIKE 'USB%'");
                foreach (ManagementObject obj in usbSearcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    var driverName = obj["DriverName"]?.ToString();
                    var name = obj["Name"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(driverName)) continue;
                    if (driverName.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                    if (driverName.Contains("OneNote", StringComparison.OrdinalIgnoreCase)) continue;

                    _log.Info($"Found USB printer '{name}' using driver '{driverName}' — using as fallback");
                    return driverName;
                }
            }
            catch (Exception ex)
            {
                _log.Warning($"FindDriverNameFromAutoInstalledPrinterAsync failed: {ex.Message}");
            }
            return null;
        }, ct);
    }

    public async Task RestartSpoolerAsync(CancellationToken ct = default)
    {
        _log.Info("Restarting Print Spooler to ensure driver is loaded...");
        try
        {
            await Task.Run(() =>
            {
                RunProcess("net", "stop spooler");
            }, ct);

            await Task.Delay(2000, ct);

            await Task.Run(() =>
            {
                RunProcess("net", "start spooler");
            }, ct);

            await Task.Delay(3000, ct);
            _log.Info("Print Spooler restarted.");
        }
        catch (Exception ex)
        {
            _log.Warning($"RestartSpoolerAsync error: {ex.Message}");
        }
    }

    // PowerShell -NonInteractive wraps errors in CLIXML (#< <Objs...>).
    // This extracts the human-readable Message field. If not CLIXML, returns the raw text.
    private static string ExtractPsError(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        // Not CLIXML — plain text error, return as-is (truncated to avoid wall of text)
        if (!raw.Contains("<Objs") && !raw.StartsWith("#<"))
            return raw.Length > 300 ? raw[..300] : raw;

        var match = Regex.Match(raw, @"<S N=""Message"">(.*?)</S>", RegexOptions.Singleline);
        if (match.Success)
        {
            var msg = match.Groups[1].Value
                .Replace("_x000D__x000A_", " ")
                .Replace("_x0027_", "'")
                .Replace("_x003C_", "<")
                .Replace("_x003E_", ">")
                .Trim();
            return msg;
        }

        // Last resort: extract any readable text from CLIXML
        var fallback = Regex.Match(raw, @"<S[^>]*>(.*?)</S>", RegexOptions.Singleline);
        if (fallback.Success)
        {
            var msg = Regex.Replace(fallback.Groups[1].Value, @"_x[0-9A-Fa-f]{4}_", " ").Trim();
            if (!string.IsNullOrWhiteSpace(msg)) return msg;
        }

        return "Falha ao conectar — verifique se o compartilhamento está ativo no host.";
    }

    private static void RunProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(15_000);
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
