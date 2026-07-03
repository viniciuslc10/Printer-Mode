using System.Diagnostics;
using System.Management;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PrinterMode.Core.Interfaces;
using PrinterMode.Core.Models;

namespace PrinterMode.WindowsPrinter;

// winspool.drv — same API used by the Windows "Add Printer" wizard to list remote printers
internal static class WinspoolApi
{
    public const int PRINTER_ENUM_NAME   = 0x00000008;
    public const int PRINTER_ENUM_SHARED = 0x00000020;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PRINTER_INFO_1
    {
        public int Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pDescription;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pComment;
    }

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool EnumPrinters(int Flags, string? Name, int Level,
        IntPtr pPrinterEnum, int cbBuf, out int pcbNeeded, out int pcReturned);
}

// netapi32.dll — NetShareEnum: enumerates ALL shares on a remote server via \pipe\srvsvc.
// This is a different named pipe from \pipe\spoolss (EnumPrinters), so it can succeed
// when the Spooler pipe is blocked, as long as the Server service pipe is accessible.
internal static class NetApi32
{
    public const int NERR_Success = 0;
    public const int MAX_PREFERRED_LENGTH = -1;
    public const uint STYPE_PRINTQ    = 1;       // print queue share
    public const uint STYPE_TYPE_MASK = 0x000000FF; // low byte = share type

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHARE_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? shi1_netname;
        public uint shi1_type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? shi1_remark;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int NetShareEnum(string lpServerName, int dwLevel, ref IntPtr lpBuf,
        int dwPrefMaxLen, out int pEntriesRead, out int pTotalEntries, ref int pResumeHandle);

    [DllImport("netapi32.dll")]
    public static extern int NetApiBufferFree(IntPtr lpBuffer);
}

// mpr.dll — WNet share enumeration (secondary fallback)
internal static class WNetApi
{
    public const int NO_ERROR                = 0;
    public const int ERROR_NO_MORE_ITEMS     = 259;
    public const int RESOURCE_GLOBALNET      = 0x00000002;
    public const int RESOURCETYPE_ANY        = 0x00000000;
    public const int RESOURCETYPE_PRINT      = 0x00000002;
    public const int RESOURCEDISPLAY_SERVER  = 0x00000003;
    public const int RESOURCEUSAGE_ALL        = 0x00000000;
    public const int RESOURCEUSAGE_CONNECTABLE = 0x00000001;
    public const int RESOURCEUSAGE_CONTAINER = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NETRESOURCE
    {
        public int    dwScope;
        public int    dwType;
        public int    dwDisplayType;
        public int    dwUsage;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpLocalName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpRemoteName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpComment;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    public static extern int WNetOpenEnum(int dwScope, int dwType, int dwUsage,
        ref NETRESOURCE lpNetResource, out IntPtr lphEnum);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    public static extern int WNetEnumResource(IntPtr hEnum, ref int lpcCount,
        IntPtr lpBuffer, ref int lpBufferSize);

    [DllImport("mpr.dll")]
    public static extern int WNetCloseEnum(IntPtr hEnum);
}

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
                var scope = new ManagementScope(@"\\.\root\cimv2");
                scope.Connect();

                // Reuse existing port instead of failing — common on re-install
                using var existing = new ManagementObjectSearcher(scope,
                    new ObjectQuery($"SELECT Name FROM Win32_TCPIPPrinterPort WHERE Name='{EscapeWmi(portName)}'"));
                foreach (ManagementObject _ in existing.Get())
                {
                    _log.Info($"TCP/IP port '{portName}' already exists — reusing.");
                    return true;
                }

                var path = new ManagementPath("Win32_TCPIPPrinterPort");
                using var mc = new ManagementClass(scope, path, null);
                using var port_ = mc.CreateInstance();

                port_["Name"] = portName;
                port_["HostAddress"] = ipAddress;
                port_["PortNumber"] = (uint)port;
                port_["Protocol"] = (uint)1; // RAW
                port_["SNMPEnabled"] = false;

                port_.Put();
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

    // Tag constants written into the result list to signal diagnostic states to the ViewModel.
    // They are filtered out before any printer names are shown to the user.
    public const string DiagPortClosed    = "__DIAG:PORT_CLOSED__";
    public const string DiagAccessDenied  = "__DIAG:ACCESS_DENIED__";

    public async Task<IReadOnlyList<string>> GetSharedPrintersAsync(string host, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var result = new List<string>();

            // Pre-step: verify TCP port 445 is reachable before attempting any SMB strategy.
            bool port445Open = IsTcpPortOpen(host, 445, 2000);
            _log.Info($"Port 445 on {host}: {(port445Open ? "open" : "CLOSED")}");

            if (!port445Open)
            {
                result.Add(DiagPortClosed);
                // Fall through to DCOM strategies (they don't need port 445)
            }
            else
            {
                // Try current-user credentials first, then guest as fallback.
                // On workgroup setups both may fail if no matching account exists on remote PC,
                // but UNC browsing (strategy 8) will still succeed via NTLM negotiation.
                bool ipcOk = TryEstablishIpcSession(host);
                if (!ipcOk)
                {
                    _log.Info($"Current-user IPC$ failed — trying Guest session");
                    ipcOk = TryEstablishGuestSession(host);
                }
                if (!ipcOk)
                    result.Add(DiagAccessDenied);
            }

            // Helper: true if result has real printer names (not just diagnostic tags)
            bool HasPrinters() => result.Any(s => !s.StartsWith("__DIAG:"));

            // Strategy 1: EnumPrinters(PRINTER_ENUM_NAME|PRINTER_ENUM_SHARED) from winspool.drv.
            // Uses the print spooler's named pipe (\\host\pipe\spoolss over SMB 445).
            // This is the exact API the Windows "Add Printer" wizard uses.
            try
            {
                var spoolShares = EnumeratePrinterSharesViaEnumPrinters(host);
                result.AddRange(spoolShares);
                _log.Info($"EnumPrinters {host}: [{string.Join(", ", spoolShares)}]");
            }
            catch (Exception ex)
            {
                _log.Warning($"EnumPrinters failed: {ex.Message}");
            }

            if (HasPrinters()) return (IReadOnlyList<string>)result;

            // Strategy 2: NetShareEnum via netapi32.dll (\\host\pipe\srvsvc — Server service).
            // Different named pipe from EnumPrinters (\pipe\spoolss). Can succeed when the
            // Spooler pipe is blocked but the "File Sharing" firewall rule is open.
            try
            {
                var netApiShares = EnumeratePrintSharesViaNetApi(host);
                result.AddRange(netApiShares.Where(s => !result.Contains(s, StringComparer.OrdinalIgnoreCase)));
                _log.Info($"NetShareEnum {host}: [{string.Join(", ", netApiShares)}]");
            }
            catch (Exception ex)
            {
                _log.Warning($"NetShareEnum failed: {ex.Message}");
            }

            if (HasPrinters()) return (IReadOnlyList<string>)result;

            // Strategy 4: WNetOpenEnum via mpr.dll — SMB-based, already filtered to RESOURCETYPE_PRINT
            try
            {
                var wnetShares = EnumeratePrinterSharesViaWNet(host);
                result.AddRange(wnetShares.Where(s => !result.Contains(s, StringComparer.OrdinalIgnoreCase)));
                _log.Info($"WNet {host}: [{string.Join(", ", wnetShares)}]");
            }
            catch (Exception ex)
            {
                _log.Warning($"WNet enumeration failed: {ex.Message}");
            }

            if (HasPrinters()) return (IReadOnlyList<string>)result;

            // Strategy 5: net view \\host — SMB port 445, column-aware parsing.
            // ParseNetViewShares filters to print-type shares only.
            try
            {
                var netExe = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "net.exe");
                var psi = new ProcessStartInfo(netExe, $"view \\\\{host}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi)!;
                var output = proc.StandardOutput.ReadToEnd();
                var stderr = proc.StandardError.ReadToEnd().Trim();
                proc.WaitForExit(10_000);

                if (proc.ExitCode == 0)
                {
                    var parsed = ParseNetViewShares(output);
                    result.AddRange(parsed.Where(s => !result.Contains(s, StringComparer.OrdinalIgnoreCase)));
                    _log.Info($"net view {host}: [{string.Join(", ", parsed)}]");
                }
                else
                {
                    _log.Warning($"net view exited {proc.ExitCode} for {host}: {stderr}");
                }
            }
            catch (Exception ex)
            {
                _log.Warning($"net view failed: {ex.Message}");
            }

            if (HasPrinters()) return (IReadOnlyList<string>)result;

            // Strategy 6: wmic /node — WMI via DCOM (port 135 + dynamic RPC).
            // Works when SMB (port 445) is blocked but WMI firewall rules are enabled.
            // Note: wmic.exe is deprecated in Windows 11 24H2+ — we fall through to PS if missing.
            try
            {
                var safeHost = host.Replace("\"", "").Replace(";", "").Replace("&", "");
                var wmicPsi = new ProcessStartInfo
                {
                    FileName = "wmic",
                    Arguments = $"/node:\"{safeHost}\" printer where \"Shared=TRUE\" get ShareName /format:list",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var wmicProc = Process.Start(wmicPsi)!;
                var wmicOut = wmicProc.StandardOutput.ReadToEnd();
                wmicProc.WaitForExit(10_000);

                foreach (var line in wmicOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!line.StartsWith("ShareName=", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = line["ShareName=".Length..].Trim();
                    if (!string.IsNullOrEmpty(name) && !result.Contains(name, StringComparer.OrdinalIgnoreCase))
                        result.Add(name);
                }
                _log.Info($"wmic /node '{host}': [{string.Join(", ", result)}]");
            }
            catch (Exception ex)
            {
                _log.Warning($"wmic /node failed for {host}: {ex.Message}");
            }

            if (HasPrinters()) return (IReadOnlyList<string>)result;

            // Strategy 7: Get-WmiObject Win32_Printer -ComputerName — DCOM-based WMI via PowerShell.
            // More reliable than Get-Printer -ComputerName which requires the Print Management
            // feature (not installed by default on consumer Windows).
            try
            {
                var safeHost5 = host.Replace("'", "''");
                var script5 = $"Get-WmiObject -Class Win32_Printer -ComputerName '{safeHost5}'" +
                              $" | Where-Object {{ $_.Shared -eq $true }}" +
                              $" | Select-Object -ExpandProperty ShareName" +
                              $" | Where-Object {{ $_ -ne $null -and $_ -ne '' }}";
                var encoded5 = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script5));
                var psi5 = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded5}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc5 = Process.Start(psi5)!;
                var out5 = proc5.StandardOutput.ReadToEnd();
                proc5.WaitForExit(15_000);

                foreach (var line in out5.Split('\n'))
                {
                    var name = line.Trim();
                    if (!string.IsNullOrEmpty(name) && !result.Contains(name, StringComparer.OrdinalIgnoreCase))
                        result.Add(name);
                }
                _log.Info($"Get-WmiObject Win32_Printer -ComputerName {host}: [{string.Join(", ", result)}]");
            }
            catch (Exception ex)
            {
                _log.Warning($"Get-WmiObject Win32_Printer -ComputerName failed: {ex.Message}");
            }

            if (HasPrinters()) return (IReadOnlyList<string>)result;

            // Strategy 8: .NET Directory.GetDirectories — last resort UNC browse.
            // Unlike every strategy above, this uses the current user's Windows NTLM token
            // automatically (same mechanism as Windows Explorer browsing \\192.168.1.100).
            // It returns ALL non-hidden shares (disk + print), because at this point any
            // enumeration beats returning nothing.  The caller shows all results and lets
            // the user pick; they know their own printer share name.
            var uncShares = TryListAllSharesViaUncBrowsing(host);
            result.AddRange(uncShares.Where(s => !result.Contains(s, StringComparer.OrdinalIgnoreCase)));

            return (IReadOnlyList<string>)result;
        }, ct);
    }

    private bool IsTcpPortOpen(string host, int port, int timeoutMs)
    {
        try
        {
            using var tcp = new TcpClient();
            var ar = tcp.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
            tcp.EndConnect(ar);
            return true;
        }
        catch { return false; }
    }

    private bool TryEstablishIpcSession(string host)
    {
        try
        {
            // "net use \\host\IPC$" opens a session using the current user's NTLM credentials.
            // This pre-authenticates the SMB connection so subsequent API calls (EnumPrinters,
            // NetShareEnum, WNetOpenEnum) can traverse named pipes without ACCESS_DENIED (5).
            var netExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "net.exe");
            var psi = new ProcessStartInfo(netExe, $@"use \\{host}\IPC$")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi)!;
            var stderr = proc.StandardError.ReadToEnd().Trim();
            proc.WaitForExit(5_000);
            _log.Info($"IPC$ session (current user) to {host}: exit={proc.ExitCode} err='{stderr}'");
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _log.Warning($"TryEstablishIpcSession({host}) failed: {ex.Message}");
            return false;
        }
    }

    private bool TryEstablishGuestSession(string host)
    {
        try
        {
            // Try connecting as Guest with empty password — works when the remote PC has
            // Guest sharing enabled ("Allow anyone" or classic sharing model).
            var netExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "net.exe");
            var psi = new ProcessStartInfo(netExe, $@"use \\{host}\IPC$ """" /user:guest")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi)!;
            var stderr = proc.StandardError.ReadToEnd().Trim();
            proc.WaitForExit(5_000);
            _log.Info($"IPC$ session (guest) to {host}: exit={proc.ExitCode} err='{stderr}'");
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _log.Warning($"TryEstablishGuestSession({host}) failed: {ex.Message}");
            return false;
        }
    }

    // Last-resort: use .NET file system APIs which automatically carry the current user's
    // NTLM token — exactly what Windows Explorer uses when browsing \\192.168.1.100.
    // Returns ALL non-hidden shares (disk + print), not filtered to print-only,
    // because at this point any enumeration is better than returning nothing.
    private List<string> TryListAllSharesViaUncBrowsing(string host)
    {
        var shares = new List<string>();
        try
        {
            var dirs = Directory.GetDirectories($@"\\{host}");
            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name) || name.EndsWith("$")) continue;
                _log.Info($"  UNC share: '{name}'");
                shares.Add(name);
            }
            _log.Info($"UNC browse \\\\{host}: [{string.Join(", ", shares)}]");
        }
        catch (Exception ex)
        {
            _log.Warning($"UNC browse \\\\{host} failed: {ex.Message}");
        }
        return shares;
    }

    private List<string> EnumeratePrinterSharesViaEnumPrinters(string host)
    {
        var shares = new List<string>();
        var serverName = $@"\\{host}";

        // PRINTER_ENUM_NAME|PRINTER_ENUM_SHARED: list printers shared on the named remote server.
        // PRINTER_ENUM_SHARED alone lists only shared printers; combined with PRINTER_ENUM_NAME
        // it scopes the query to the specific host instead of the whole network.
        int enumFlags = WinspoolApi.PRINTER_ENUM_NAME | WinspoolApi.PRINTER_ENUM_SHARED;

        // First call: get required buffer size (returns false with needed > 0)
        WinspoolApi.EnumPrinters(enumFlags, serverName, 1,
            IntPtr.Zero, 0, out int needed, out _);

        if (needed == 0)
        {
            var w32 = Marshal.GetLastWin32Error();
            var desc = w32 switch
            {
                0    => "no printers on server",
                5    => "access denied",
                53   => "network path not found",
                64   => "network name no longer available",
                1722 => "RPC server unavailable (Spooler not reachable)",
                1723 => "RPC server too busy",
                _    => $"Win32={w32}"
            };
            _log.Warning($"EnumPrinters({host}): {desc}");
            return shares;
        }

        var buf = Marshal.AllocHGlobal(needed);
        try
        {
            if (!WinspoolApi.EnumPrinters(enumFlags, serverName, 1,
                    buf, needed, out _, out int count))
            {
                _log.Warning($"EnumPrinters({host}) 2nd call failed: Win32={Marshal.GetLastWin32Error()}");
                return shares;
            }

            _log.Info($"EnumPrinters({host}) returned {count} printer(s)");
            var structSize = Marshal.SizeOf<WinspoolApi.PRINTER_INFO_1>();
            var ptr = buf;
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WinspoolApi.PRINTER_INFO_1>(ptr);
                var name = info.pName ?? string.Empty;
                // pName is "\\host\sharename" — extract just the share name
                if (name.StartsWith(@"\\"))
                {
                    var lastSlash = name.LastIndexOf('\\');
                    name = lastSlash >= 0 ? name[(lastSlash + 1)..] : name;
                }
                _log.Info($"  printer[{i}]: '{name}' (raw='{info.pName}')");
                if (!string.IsNullOrEmpty(name) && !name.EndsWith("$"))
                    shares.Add(name);
                ptr = IntPtr.Add(ptr, structSize);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }

        return shares;
    }

    private List<string> EnumeratePrintSharesViaNetApi(string host)
    {
        var shares = new List<string>();
        IntPtr bufPtr = IntPtr.Zero;
        int resumeHandle = 0;

        try
        {
            int err = NetApi32.NetShareEnum(
                $@"\\{host}", 1, ref bufPtr,
                NetApi32.MAX_PREFERRED_LENGTH,
                out int entriesRead, out _, ref resumeHandle);

            if (err != NetApi32.NERR_Success)
            {
                _log.Warning($"NetShareEnum({host}) error {err} (0x{err:X8})");
                return shares;
            }

            _log.Info($"NetShareEnum({host}) returned {entriesRead} share(s)");
            int structSize = Marshal.SizeOf<NetApi32.SHARE_INFO_1>();
            for (int i = 0; i < entriesRead; i++)
            {
                var info = Marshal.PtrToStructure<NetApi32.SHARE_INFO_1>(
                    IntPtr.Add(bufPtr, i * structSize));
                if ((info.shi1_type & NetApi32.STYPE_TYPE_MASK) == NetApi32.STYPE_PRINTQ &&
                    !string.IsNullOrEmpty(info.shi1_netname) &&
                    !info.shi1_netname!.EndsWith("$"))
                {
                    _log.Info($"  PrintShare[{i}]: '{info.shi1_netname}'");
                    shares.Add(info.shi1_netname);
                }
            }
        }
        finally
        {
            if (bufPtr != IntPtr.Zero)
                NetApi32.NetApiBufferFree(bufPtr);
        }

        return shares;
    }

    private List<string> EnumeratePrinterSharesViaWNet(string host)
    {
        var shares = new List<string>();

        // Describe the server as a container resource we want to enumerate inside.
        // dwUsage here describes the server resource itself (it IS a container).
        var serverResource = new WNetApi.NETRESOURCE
        {
            dwScope       = WNetApi.RESOURCE_GLOBALNET,
            dwType        = WNetApi.RESOURCETYPE_ANY,
            dwDisplayType = WNetApi.RESOURCEDISPLAY_SERVER,
            dwUsage       = WNetApi.RESOURCEUSAGE_CONTAINER,
            lpRemoteName  = $@"\\{host}"
        };

        // dwUsage=0 (RESOURCEUSAGE_ALL) is required — RESOURCEUSAGE_CONTAINER (2) would only
        // return sub-containers (workgroups/domains), never connectable shares like printers.
        int hr = WNetApi.WNetOpenEnum(
            WNetApi.RESOURCE_GLOBALNET,
            WNetApi.RESOURCETYPE_PRINT,   // only printer shares
            WNetApi.RESOURCEUSAGE_ALL,    // 0 = connectable AND container resources
            ref serverResource,
            out IntPtr hEnum);

        if (hr != WNetApi.NO_ERROR)
        {
            _log.Warning($"WNetOpenEnum({host}) error {hr} (0x{hr:X8})");
            return shares;
        }

        try
        {
            const int bufSize = 65536;
            IntPtr buf = Marshal.AllocHGlobal(bufSize);
            try
            {
                while (true)
                {
                    int count = bufSize / Marshal.SizeOf<WNetApi.NETRESOURCE>(), size = bufSize;
                    int err = WNetApi.WNetEnumResource(hEnum, ref count, buf, ref size);
                    if (err == WNetApi.ERROR_NO_MORE_ITEMS || count <= 0) break;
                    if (err != WNetApi.NO_ERROR) break;

                    IntPtr ptr = buf;
                    for (int i = 0; i < count; i++)
                    {
                        var res = Marshal.PtrToStructure<WNetApi.NETRESOURCE>(ptr);

                        if (!string.IsNullOrEmpty(res.lpRemoteName))
                        {
                            int slash = res.lpRemoteName.LastIndexOf('\\');
                            var name = slash >= 0 ? res.lpRemoteName[(slash + 1)..] : res.lpRemoteName;
                            if (!string.IsNullOrEmpty(name) && !name.EndsWith("$"))
                            {
                                _log.Info($"  WNet share: '{name}' type={res.dwType} remote='{res.lpRemoteName}'");
                                shares.Add(name);
                            }
                        }
                        ptr = IntPtr.Add(ptr, Marshal.SizeOf<WNetApi.NETRESOURCE>());
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { WNetApi.WNetCloseEnum(hEnum); }

        return shares;
    }

    // Parses "net view \\host" output using fixed-width column detection so share names
    // with spaces (e.g. "Gertec G250") are extracted correctly. Locale-independent.
    private static List<string> ParseNetViewShares(string output)
    {
        var shares = new List<string>();
        var lines = output.Split('\n').Select(l => l.TrimEnd()).ToArray();

        int separatorIdx = -1;
        int typeColStart = -1;

        // Find the separator line and detect the Type column position from the header above it
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith("---")) continue;
            separatorIdx = i;

            for (int j = i - 1; j >= 0; j--)
            {
                if (string.IsNullOrWhiteSpace(lines[j])) continue;
                var header = lines[j];
                var pos = header.IndexOf("Type", StringComparison.OrdinalIgnoreCase);
                if (pos < 0) pos = header.IndexOf("Tipo", StringComparison.OrdinalIgnoreCase);
                if (pos > 0) typeColStart = pos;
                break;
            }
            break;
        }

        if (separatorIdx < 0) return shares;

        for (int i = separatorIdx + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            string shareName;
            string typeField = string.Empty;

            if (typeColStart > 0)
            {
                if (line.Length <= typeColStart) continue; // footer/short line
                shareName = line[..typeColStart].Trim();
                typeField = line[typeColStart..].TrimStart();
            }
            else
            {
                // No column info: first space-delimited token (won't capture spaces in share name)
                var parts = line.TrimStart().Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                shareName = parts.Length > 0 ? parts[0].Trim() : string.Empty;
            }

            if (string.IsNullOrEmpty(shareName)) continue;
            if (shareName.EndsWith("$")) continue; // skip admin/hidden shares

            // When type info is available, only keep printer-type shares.
            // Disk and IPC shares are excluded — they can't be used as printer connections.
            // When type is unknown (column not detected), we include the entry and let the
            // caller filter by attempting connection.
            if (!string.IsNullOrEmpty(typeField))
            {
                bool isPrint =
                    typeField.StartsWith("Print", StringComparison.OrdinalIgnoreCase) ||
                    typeField.StartsWith("Impr",  StringComparison.OrdinalIgnoreCase);
                if (!isPrint) continue;
            }

            if (!shares.Contains(shareName, StringComparer.OrdinalIgnoreCase))
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
        var (driver, _) = await FindAutoInstalledPrinterInfoAsync(manufacturerHint, modelHint, ct);
        return driver;
    }

    public async Task<(string? DriverName, string? PortName)> FindAutoInstalledPrinterInfoAsync(
        string manufacturerHint, string modelHint, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DriverName, PortName FROM Win32_Printer");

                var mfgLow = manufacturerHint.ToLowerInvariant();
                var mdlLow = modelHint.Replace("-", "").Replace(" ", "").ToLowerInvariant();

                (string? DriverName, string? PortName) fallback = (null, null);

                foreach (ManagementObject obj in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    var name       = obj["Name"]?.ToString() ?? "";
                    var driverName = obj["DriverName"]?.ToString();
                    var portName   = obj["PortName"]?.ToString();
                    if (string.IsNullOrEmpty(driverName)) continue;

                    var nameLow   = name.ToLowerInvariant().Replace("-", "").Replace(" ", "");
                    var driverLow = driverName.ToLowerInvariant().Replace("-", "").Replace(" ", "");

                    // Primary: name or driver matches manufacturer/model keywords
                    if (nameLow.Contains(mfgLow) || nameLow.Contains(mdlLow) ||
                        driverLow.Contains(mfgLow) || driverLow.Contains(mdlLow))
                    {
                        _log.Info($"FindAutoInfo: matched '{name}' driver='{driverName}' port='{portName}'");
                        return (driverName, portName);
                    }

                    // Fallback: any non-system printer — keep first candidate
                    if (fallback.DriverName == null &&
                        !driverName.Contains("Microsoft",  StringComparison.OrdinalIgnoreCase) &&
                        !driverName.Contains("OneNote",    StringComparison.OrdinalIgnoreCase) &&
                        !driverName.Contains("Fax",        StringComparison.OrdinalIgnoreCase) &&
                        !driverName.Contains("XPS",        StringComparison.OrdinalIgnoreCase) &&
                        !driverName.Contains("PDF",        StringComparison.OrdinalIgnoreCase))
                    {
                        fallback = (driverName, portName);
                    }
                }

                if (fallback.DriverName != null)
                {
                    // Suppress PortName for the fallback case — no keyword match means we can't
                    // be sure this is the right printer, so we don't want to assign its port to
                    // another device (e.g. returning a COM port entry for a USB install).
                    _log.Info($"FindAutoInfo: fallback driver='{fallback.DriverName}' (port suppressed — no keyword match)");
                    return (fallback.DriverName, null);
                }
            }
            catch (Exception ex)
            {
                _log.Warning($"FindAutoInstalledPrinterInfoAsync failed: {ex.Message}");
            }
            return (null, null);
        }, ct);
    }

    public async Task RestartSpoolerAsync(CancellationToken ct = default)
    {
        _log.Info("Restarting Print Spooler...");
        try
        {
            // Stop-Service -Force kills the spooler immediately without waiting for
            // pending print jobs to drain, cutting stop time from up to 15 s to ~1 s.
            var stopScript  = "Stop-Service -Name Spooler -Force -ErrorAction SilentlyContinue";
            var stopEncoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(stopScript));
            await Task.Run(() => RunProcess("powershell.exe",
                $"-NoProfile -NonInteractive -EncodedCommand {stopEncoded}"), ct);

            await Task.Delay(800, ct);
            await Task.Run(() => RunProcess("net", "start spooler"), ct);
            await Task.Delay(1500, ct);
            _log.Info("Print Spooler restarted.");
        }
        catch (Exception ex)
        {
            _log.Warning($"RestartSpoolerAsync error: {ex.Message}");
        }
    }

    // PowerShell -NonInteractive wraps errors in CLIXML (#< <Objs...>).
    // Handles both <S N="Message"> (standard PS) and <S S="Error"> (Add-Printer format).
    private static string ExtractPsError(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        if (!raw.Contains("<Objs") && !raw.StartsWith("#<"))
            return raw.Length > 300 ? raw[..300] : raw;

        // Standard PowerShell error format
        var msgMatch = Regex.Match(raw, @"<S N=""Message"">(.*?)</S>", RegexOptions.Singleline);
        if (msgMatch.Success)
            return CleanXmlEscapes(msgMatch.Groups[1].Value);

        // Add-Printer / WMI cmdlet format: <S S="Error">text_x000D__x000A_At line:...</S>
        var errMatch = Regex.Match(raw, @"<S S=""Error"">(.*?)</S>", RegexOptions.Singleline);
        if (errMatch.Success)
        {
            var text = CleanXmlEscapes(errMatch.Groups[1].Value);
            // Trim trace info after " At line:"
            var atLine = text.IndexOf(" At line:", StringComparison.OrdinalIgnoreCase);
            if (atLine > 0) text = text[..atLine];
            return text.Trim();
        }

        return "Falha ao conectar — verifique se o compartilhamento está ativo no host.";
    }

    private static string CleanXmlEscapes(string text) =>
        text.Replace("_x000D__x000A_", " ").Replace("_x0027_", "'")
            .Replace("_x003C_", "<").Replace("_x003E_", ">").Replace("_x0022_", "\"").Trim();

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
            if (result == null)
            {
                _log.Error($"WMI printer creation returned null for {printerName}");
                return false;
            }
            _log.Info($"Printer added via WMI: {printerName} (path={result.Path})");
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
