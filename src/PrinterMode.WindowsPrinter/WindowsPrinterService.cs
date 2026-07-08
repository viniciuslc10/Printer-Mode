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

// winspool.drv — AddPrinterDriverEx, called directly (not via the Add-PrinterDriver cmdlet).
// APD_INSTALL_WARNED_DRIVER tells the Spooler API the caller already knows this driver is
// unsigned and accepts installing it anyway, WITHOUT the interactive "Windows can't verify
// the publisher" dialog — this is the documented, sanctioned way to install an OEM print
// driver package that has no catalog file at all, for a caller (like this app, run elevated
// by the user) that has already obtained the user's informed consent out of band. It does
// NOT touch any system-wide signing/security policy — it only accepts this one driver.
internal static class WinspoolDriverApi
{
    public const uint APD_COPY_ALL_FILES = 0x00000004;
    public const uint APD_INSTALL_WARNED_DRIVER = 0x00008000;

    [StructLayout(LayoutKind.Sequential)]
    public struct DRIVER_INFO_3
    {
        public uint cVersion;
        public IntPtr pName;
        public IntPtr pEnvironment;
        public IntPtr pDriverPath;
        public IntPtr pDataFile;
        public IntPtr pConfigFile;
        public IntPtr pHelpFile;
        public IntPtr pDependentFiles;
        public IntPtr pMonitorName;
        public IntPtr pDefaultDataType;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool AddPrinterDriverEx(string? pName, uint level, IntPtr pDriverInfo, uint dwFileCopyFlags);
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
        var (ok, _) = await TryAddPrinterWithReasonAsync(printerName, driverName, portName, ct);
        return ok;
    }

    public async Task<(bool ok, string? error)> TryAddPrinterWithReasonAsync(
        string printerName, string driverName, string portName, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            // PowerShell Add-Printer is the most reliable method on Windows 10/11.
            // WMI is kept as fallback. Neither shows system error dialogs on failure.
            var (psOk, psError) = AddPrinterViaPowerShellWithReason(printerName, driverName, portName);
            if (psOk) return (true, (string?)null);

            _log.Warning($"PowerShell Add-Printer failed ({psError}), trying WMI fallback.");
            var (wmiOk, wmiError) = AddPrinterViaWmiWithReason(printerName, driverName, portName);
            var combined = wmiOk ? null : $"PowerShell: {psError} | WMI: {wmiError}";
            return (wmiOk, combined);
        }, ct);
    }

    public async Task<string?> TryRegisterPrintDriverFromInfAsync(
        string infPath, IReadOnlyList<string> candidateNames, CancellationToken ct = default)
    {
        var (name, _) = await TryRegisterPrintDriverFromInfWithReasonAsync(infPath, candidateNames, ct);
        return name;
    }

    public async Task<(string? name, string? error)> TryRegisterPrintDriverFromInfWithReasonAsync(
        string infPath, IReadOnlyList<string> candidateNames, CancellationToken ct = default)
    {
        // pnputil /add-driver only stages a driver into the PnP DriverStore and binds it to
        // the matching HARDWARE — it does NOT register a Print Spooler driver entry. A
        // package that includes a real printer-class INF still needs an explicit
        // Add-PrinterDriver call for its driver name to ever appear in Get-PrinterDriver /
        // become usable by Add-Printer -DriverName. This tries each known candidate name
        // against the given INF and returns the one that Windows actually accepts, if any,
        // along with the real error text for the last rejected candidate so the caller can
        // show the user WHY (unsigned driver blocked, wrong architecture, etc) instead of a
        // silent fallback to Generic / Text Only.
        if (string.IsNullOrWhiteSpace(infPath) || !File.Exists(infPath))
            return (null, "Arquivo INF não encontrado.");
        if (candidateNames.Count == 0)
            return (null, "Nenhum nome de driver candidato informado.");

        return await Task.Run(() =>
        {
            string? lastError = null;
            foreach (var name in candidateNames.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                try
                {
                    var script =
                        $"try {{ Add-PrinterDriver -Name '{name.Replace("'", "''")}' " +
                        $"-InfPath '{infPath.Replace("'", "''")}' -ErrorAction Stop; 'OK' }} " +
                        $"catch {{ \"ERR:$($_.Exception.Message)\" }}";
                    var enc = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -NonInteractive -EncodedCommand {enc}",
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    using var proc = Process.Start(psi)!;
                    var outTask = proc.StandardOutput.ReadToEndAsync();
                    var errTask = proc.StandardError.ReadToEndAsync();
                    proc.WaitForExit(20_000);
                    var stdout = outTask.GetAwaiter().GetResult().Trim();
                    errTask.GetAwaiter().GetResult();

                    if (stdout.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Info($"TryRegisterPrintDriverFromInf: registered '{name}' from '{infPath}'.");
                        return (name, (string?)null);
                    }
                    lastError = $"'{name}': {ExtractPsError(stdout)}";
                    _log.Info($"TryRegisterPrintDriverFromInf: {lastError}");
                }
                catch (Exception ex)
                {
                    lastError = $"'{name}': {ex.Message}";
                    _log.Warning($"TryRegisterPrintDriverFromInfAsync('{name}'): {ex.Message}");
                }
            }
            return (null, lastError);
        }, ct);
    }

    public async Task<(bool ok, string? error)> TryRegisterUnsignedPrintDriverAsync(
        string driverName, string dataFilePath, IReadOnlyList<string> dependentFilePaths, CancellationToken ct = default)
    {
        // Confirmed in the field: rundll32 printui.dll,PrintUIEntry /ia returns instantly
        // (exit=0) without ever showing the classic "unsigned driver" dialog — that legacy
        // printui.dll flow is unreliable on current Windows and cannot be trusted.
        //
        // AddPrinterDriverEx (winspool.drv), called directly, is the documented Win32 API the
        // Print Spooler itself uses. Its APD_INSTALL_WARNED_DRIVER flag exists specifically to
        // install a driver that would otherwise trigger the "not digitally signed" warning,
        // WITHOUT showing any dialog — the caller (this app, already running elevated because
        // the user launched it as Administrator) is asserting the same consent a human would
        // give by clicking "Install this driver software anyway". It changes no system-wide
        // signing/security policy; it only accepts this one driver package.
        //
        // pDriverPath/pConfigFile/pHelpFile reference the SYSTEM's own UNIDRV.DLL/UNIDRVUI.DLL/
        // UNIDRV.HLP (already present — confirmed by "Microsoft enhanced Point and Print
        // compatibility driver" already using them) rather than the vendor's bundled copies, to
        // avoid touching Microsoft-signed shared components.
        return await Task.Run(() =>
        {
            if (!File.Exists(dataFilePath))
                return (false, (string?)$"Arquivo de dados do driver não encontrado: {dataFilePath}");

            var handles = new List<IntPtr>();
            var infoPtr = IntPtr.Zero;
            try
            {
                IntPtr Alloc(string s)
                {
                    var p = Marshal.StringToHGlobalUni(s);
                    handles.Add(p);
                    return p;
                }

                IntPtr AllocMultiSz(IEnumerable<string> items)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var s in items.Where(File.Exists))
                    {
                        sb.Append(s);
                        sb.Append('\0');
                    }
                    sb.Append('\0');
                    var p = Marshal.StringToHGlobalUni(sb.ToString());
                    handles.Add(p);
                    return p;
                }

                var info = new WinspoolDriverApi.DRIVER_INFO_3
                {
                    cVersion = 3,
                    pName = Alloc(driverName),
                    pEnvironment = Alloc("Windows x64"),
                    pDriverPath = Alloc("UNIDRV.DLL"),
                    pDataFile = Alloc(dataFilePath),
                    pConfigFile = Alloc("UNIDRVUI.DLL"),
                    pHelpFile = Alloc("UNIDRV.HLP"),
                    pDependentFiles = AllocMultiSz(dependentFilePaths),
                    pMonitorName = IntPtr.Zero,
                    pDefaultDataType = Alloc("RAW")
                };

                infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinspoolDriverApi.DRIVER_INFO_3>());
                Marshal.StructureToPtr(info, infoPtr, false);

                bool ok = WinspoolDriverApi.AddPrinterDriverEx(
                    null, 3, infoPtr,
                    WinspoolDriverApi.APD_COPY_ALL_FILES | WinspoolDriverApi.APD_INSTALL_WARNED_DRIVER);

                if (ok)
                {
                    _log.Info($"AddPrinterDriverEx (APD_INSTALL_WARNED_DRIVER): registered '{driverName}' from '{dataFilePath}'.");
                    return (true, (string?)null);
                }

                var err = Marshal.GetLastWin32Error();
                var msg = new System.ComponentModel.Win32Exception(err).Message;
                _log.Warning($"AddPrinterDriverEx failed for '{driverName}': Win32 error {err} ({msg})");
                return (false, (string?)$"AddPrinterDriverEx: erro {err} — {msg}");
            }
            catch (Exception ex)
            {
                _log.Warning($"TryRegisterUnsignedPrintDriverAsync failed: {ex.Message}");
                return (false, (string?)ex.Message);
            }
            finally
            {
                foreach (var p in handles)
                    if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
                if (infoPtr != IntPtr.Zero) Marshal.FreeHGlobal(infoPtr);
            }
        }, ct);
    }

    public async Task<bool> TryTrustCertificateAsync(string certPath, CancellationToken ct = default)
    {
        // Some OEM POS-printer packages sign their own catalog with a self-signed cert
        // (confirmed pattern: "CN=Printer", not chained to any public root) and ship that same
        // cert as an .cer file for the installer to import. Importing it into both Trusted Root
        // and Trusted Publisher makes Add-PrinterDriver accept the (already legitimately signed,
        // just not publicly trusted) catalog normally — no unsigned-driver override needed, and
        // this only trusts this one specific certificate, not driver signing in general.
        return await Task.Run(() =>
        {
            if (!File.Exists(certPath))
            {
                _log.Warning($"TryTrustCertificateAsync: cert not found at '{certPath}'.");
                return false;
            }

            try
            {
                var path = certPath.Replace("'", "''");
                var script =
                    $"Import-Certificate -FilePath '{path}' -CertStoreLocation Cert:\\LocalMachine\\Root -ErrorAction Stop | Out-Null; " +
                    $"Import-Certificate -FilePath '{path}' -CertStoreLocation Cert:\\LocalMachine\\TrustedPublisher -ErrorAction Stop | Out-Null";
                var enc = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -EncodedCommand {enc}",
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var proc = Process.Start(psi)!;
                var stderrTask = proc.StandardError.ReadToEndAsync();
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                proc.WaitForExit(20_000);
                var stderr = stderrTask.GetAwaiter().GetResult();
                stdoutTask.GetAwaiter().GetResult();
                var ok = proc.ExitCode == 0;
                _log.Info($"TryTrustCertificateAsync('{certPath}'): exit={proc.ExitCode}" +
                          (ok ? "" : $" error={ExtractPsError(stderr)}"));
                return ok;
            }
            catch (Exception ex)
            {
                _log.Warning($"TryTrustCertificateAsync failed: {ex.Message}");
                return false;
            }
        }, ct);
    }

    public async Task<string?> FindBestUsbPortAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                // USB, DOT4 and WSD ports registered with the print spooler.
                // WSD (Web Services for Devices) is used by modern Windows 10/11 for USB printers
                // alongside traditional USB001/DOT4USB001 names.
                // DOT4 is used by some HP and Epson models (IEEE 1284.4 protocol).
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name FROM Win32_PrinterPort WHERE Name LIKE 'USB%' OR Name LIKE 'DOT4%' OR Name LIKE 'WSD%'");

                string? best = null;
                foreach (ManagementObject obj in searcher.Get())
                {
                    var name = obj["Name"]?.ToString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        best = name;
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

    public async Task<string?> FindBestUsbPortViaPowerShellAsync(CancellationToken ct = default)
    {
        // Same query as FindBestUsbPortAsync, but through Get-PrinterPort (PrintManagement
        // module) instead of the legacy WMI Win32_PrinterPort class. Confirmed repeatedly in
        // this exact environment: Win32_PrinterPort can lag well behind reality — a port that
        // genuinely exists (verified by hand in Printer Properties) was invisible to it. The
        // same lesson already applied to driver detection (GetInstalledDriversAsync moved to
        // Get-PrinterDriver for this reason) is applied here for ports.
        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -Command " +
                        "\"Get-PrinterPort | Where-Object { $_.Name -like 'USB*' -or $_.Name -like 'DOT4*' -or $_.Name -like 'WSD*' } " +
                        "| Select-Object -First 1 -ExpandProperty Name\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi)!;
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(15_000);
                var best = string.IsNullOrEmpty(output) ? null : output;
                _log.Info($"FindBestUsbPortViaPowerShellAsync: '{best ?? "none"}'");
                return best;
            }
            catch (Exception ex)
            {
                _log.Warning($"FindBestUsbPortViaPowerShellAsync: {ex.Message}");
                return null;
            }
        }, ct);
    }

    public async Task<string?> FindNewPortSinceSnapshotAsync(
        IReadOnlyList<string> portsBefore, CancellationToken ct = default)
    {
        // Returns the first port registered in Win32_PrinterPort that was NOT in portsBefore.
        // Unlike FindBestUsbPortAsync, this catches vendor-specific port names (BEMATECHUSB001,
        // ELGINUSB001, etc.) that don't match the USB%/DOT4%/WSD% prefixes.
        // Returns null if portsBefore is empty — an empty snapshot means we cannot diff.
        if (portsBefore.Count == 0)
            return null;

        return await Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PrinterPort");
                foreach (ManagementObject obj in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    var name = obj["Name"]?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;
                    if (IsNonHardwarePort(name)) continue;
                    if (!portsBefore.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        _log.Info($"FindNewPortSinceSnapshot: new port detected '{name}'");
                        return name;
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                _log.Warning($"FindNewPortSinceSnapshotAsync failed: {ex.Message}");
                return null;
            }
        }, ct);
    }

    public async Task<string?> FindUsbPortFromRegistryAsync(CancellationToken ct = default)
    {
        // The USB Print Monitor writes port names to the registry as soon as a USB
        // printer device is matched. This key updates faster than Win32_PrinterPort
        // (which lags the WMI cache), so it catches ports that WMI hasn't surfaced yet.
        // Works for standard USB001 and vendor-specific names (BEMATECHUSB001 etc.).
        return await Task.Run(() =>
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Print\Monitors\USB Monitor\Ports");
                if (key == null) return null;

                var first = key.GetSubKeyNames()
                    .FirstOrDefault(s => !string.IsNullOrEmpty(s));
                if (first != null)
                    _log.Info($"FindUsbPortFromRegistry: found '{first}'");
                return first;
            }
            catch (Exception ex)
            {
                _log.Warning($"FindUsbPortFromRegistryAsync: {ex.Message}");
                return null;
            }
        }, ct);
    }

    public async Task<IReadOnlyList<string>> GetPrintMonitorsAsync(CancellationToken ct = default)
    {
        // Lists every Print Monitor registered with the Spooler. Many POS/thermal-printer
        // vendors (Gertec included) ship their OWN monitor DLL instead of relying on the
        // generic "USB Monitor" — it creates its OWN vendor-specific port, NOT USB001, and
        // does so only through ITS OWN mechanism (which may not fire automatically the way
        // usbprint.sys's port creation does). Diffing this list before/after the vendor's
        // driver install reveals whether such a custom monitor exists.
        return await Task.Run(() =>
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Print\Monitors");
                if (key == null) return (IReadOnlyList<string>)Array.Empty<string>();
                return (IReadOnlyList<string>)key.GetSubKeyNames();
            }
            catch (Exception ex)
            {
                _log.Warning($"GetPrintMonitorsAsync: {ex.Message}");
                return (IReadOnlyList<string>)Array.Empty<string>();
            }
        }, ct);
    }

    public async Task<IReadOnlyList<string>> GetPortsForMonitorAsync(string monitorName, CancellationToken ct = default)
    {
        // Reads the port list a SPECIFIC monitor has registered, from
        // Control\Print\Monitors\<name>\Ports — the same registry pattern the standard "USB
        // Monitor" uses (see FindUsbPortFromRegistryAsync), generalized to any vendor monitor.
        // This lets us find a port created by a custom vendor monitor even when it never
        // shows up via the generic USB-port detection paths.
        if (string.IsNullOrWhiteSpace(monitorName))
            return Array.Empty<string>();

        return await Task.Run(() =>
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Control\Print\Monitors\{monitorName}\Ports");
                if (key == null) return (IReadOnlyList<string>)Array.Empty<string>();
                var ports = key.GetSubKeyNames().Where(s => !string.IsNullOrEmpty(s)).ToList();
                if (ports.Count > 0)
                    _log.Info($"GetPortsForMonitor('{monitorName}'): found [{string.Join(", ", ports)}]");
                return (IReadOnlyList<string>)ports;
            }
            catch (Exception ex)
            {
                _log.Warning($"GetPortsForMonitorAsync('{monitorName}'): {ex.Message}");
                return (IReadOnlyList<string>)Array.Empty<string>();
            }
        }, ct);
    }

    public async Task<string?> FindDevicePortByVidPidAsync(string vid, string pid, CancellationToken ct = default)
    {
        // Reads the port the PHYSICAL device (VID/PID) is actually bound to, straight from
        // its own PnP registry node. This is the single most reliable source because it is
        // keyed to the exact hardware — not inferred from the spooler's global port list.
        //
        // Many POS / thermal printers (Gertec, Elgin, Bematech, Epson TM…) enumerate as a
        // CDC / virtual-serial device, NOT a USB printing-class device. For those there is
        // never a USB001 spooler port — Windows assigns them a COMx port and stores it under
        //   Enum\USB\VID_xxxx&PID_xxxx\<instance>\Device Parameters\PortName   (e.g. "COM3").
        // Composite devices expose the same value under a &MI_0x child. This method finds it.
        if (string.IsNullOrEmpty(vid) || string.IsNullOrEmpty(pid))
            return null;

        return await Task.Run(() =>
        {
            try
            {
                using var usbEnum = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Enum\USB");
                if (usbEnum == null) return null;

                foreach (var devKey in usbEnum.GetSubKeyNames())
                {
                    ct.ThrowIfCancellationRequested();
                    // Match VID_xxxx&PID_xxxx in any form, including composite (…&MI_00).
                    if (devKey.IndexOf($"VID_{vid}", StringComparison.OrdinalIgnoreCase) < 0 ||
                        devKey.IndexOf($"PID_{pid}", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    using var dev = usbEnum.OpenSubKey(devKey);
                    if (dev == null) continue;
                    foreach (var instId in dev.GetSubKeyNames())
                    {
                        using var inst = dev.OpenSubKey(instId);
                        using var devParams = inst?.OpenSubKey("Device Parameters");
                        var portName = devParams?.GetValue("PortName")?.ToString();
                        if (!string.IsNullOrEmpty(portName))
                        {
                            // Spooler serial/parallel ports use the canonical colon form
                            // ("COM3:", "LPT1:"); the device stores it without the colon.
                            // Normalize so Add-Printer -PortName matches the registered port.
                            if ((portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                 portName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                                !portName.EndsWith(":"))
                                portName += ":";
                            _log.Info($"FindDevicePortByVidPid: device '{devKey}' bound to port '{portName}'");
                            return portName;
                        }
                    }
                }
                _log.Info($"FindDevicePortByVidPid: no PortName under VID_{vid}&PID_{pid} (not a virtual-serial device or not bound yet).");
                return null;
            }
            catch (Exception ex)
            {
                _log.Warning($"FindDevicePortByVidPidAsync: {ex.Message}");
                return null;
            }
        }, ct);
    }

    public async Task<string?> FindUsbPrintDeviceInterfacePathAsync(string vid, string pid, CancellationToken ct = default)
    {
        // usbprint.sys registers a device interface (GUID_DEVINTERFACE_USBPRINT =
        // {28d78fad-5a12-11d1-ae5b-0000f803a8c2}) for every USB printer-class device it binds
        // to — this exists as soon as the driver binds, independently of whether the Spooler's
        // USB Print Monitor ever creates a named port (the step that keeps failing). Windows
        // stores every such interface's symbolic-link path directly in the registry under
        //   Control\DeviceClasses\{GUID}\<encoded-path>
        // where the subkey name IS the device path with '\' encoded as '#' and the leading
        // "\\?\" written as "##?#". Reading it from the registry is far more reliable than a
        // SetupDi P/Invoke and needs no external process. The reconstructed "\\?\USB#VID..."
        // path can be registered as a Local Port and written to directly.
        if (string.IsNullOrEmpty(vid) || string.IsNullOrEmpty(pid))
            return null;

        return await Task.Run(() =>
        {
            try
            {
                const string guid = "{28d78fad-5a12-11d1-ae5b-0000f803a8c2}";
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Control\DeviceClasses\{guid}");
                if (key == null)
                {
                    _log.Info("FindUsbPrintDeviceInterfacePath: USBPRINT DeviceClasses key absent (usbprint.sys not exposing any printer interface).");
                    return null;
                }

                string? Reconstruct(string sub) =>
                    sub.StartsWith("##?#") ? @"\\?\" + sub.Substring(4) : null;

                // Prefer the exact VID/PID match; fall back to the only USBPRINT interface present.
                string? fallback = null;
                foreach (var sub in key.GetSubKeyNames())
                {
                    ct.ThrowIfCancellationRequested();
                    var path = Reconstruct(sub);
                    if (path == null) continue;

                    // Is this interface currently linked/active?
                    bool linked = true;
                    using (var ctrl = key.OpenSubKey($@"{sub}\#\Control"))
                    {
                        if (ctrl?.GetValue("Linked") is int iv) linked = iv != 0;
                    }
                    if (!linked) continue;

                    fallback ??= path;
                    if (sub.IndexOf($"VID_{vid}", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        sub.IndexOf($"PID_{pid}", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _log.Info($"FindUsbPrintDeviceInterfacePath: matched VID/PID -> '{path}'");
                        return path;
                    }
                }

                if (fallback != null)
                    _log.Info($"FindUsbPrintDeviceInterfacePath: no VID/PID match; using only linked USBPRINT interface -> '{fallback}'");
                else
                    _log.Info($"FindUsbPrintDeviceInterfacePath: no linked USBPRINT interface for VID_{vid}&PID_{pid}.");
                return fallback;
            }
            catch (Exception ex)
            {
                _log.Warning($"FindUsbPrintDeviceInterfacePathAsync: {ex.Message}");
                return null;
            }
        }, ct);
    }

    public async Task<bool> EnsurePortRegisteredAsync(string portName, CancellationToken ct = default)
    {
        var (ok, _) = await TryRegisterPortWithReasonAsync(portName, ct);
        return ok;
    }

    public async Task<(bool ok, string? error)> TryRegisterPortWithReasonAsync(string portName, CancellationToken ct = default)
    {
        // Guarantees the given port exists in the Print Spooler so Add-Printer accepts it, and
        // returns the ACTUAL Windows/PowerShell error text when it fails. The previous version
        // ran Add-PrinterPort via the generic RunProcess helper, which redirects but never reads
        // stdout/stderr — so the exact reason Add-PrinterPort rejects a name (invalid characters,
        // name too long, access denied, monitor doesn't support it, etc.) was silently discarded.
        // Without it we were guessing blindly; this makes the real cause visible.
        if (string.IsNullOrWhiteSpace(portName))
            return (false, "nome de porta vazio");

        // COMx/LPTx use the canonical colon form the spooler expects; any other name
        // (including a raw device interface path like "\\?\USB#VID_...") is registered exactly
        // as given — appending ":" to a device path would create the wrong name.
        bool isComOrLpt =
            Regex.IsMatch(portName, @"^COM\d+:?$", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(portName, @"^LPT\d+:?$", RegexOptions.IgnoreCase);
        var registerName = isComOrLpt
            ? (portName.EndsWith(":") ? portName : portName + ":")
            : portName;

        // Enumerate all ports and compare in C# (never a WQL WHERE on the raw name — a device
        // interface path contains backslashes, which are WQL escape characters and would make
        // the query throw, wrongly reporting the port as unregisterable).
        bool PortExists()
        {
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_PrinterPort");
                foreach (ManagementObject obj in s.Get())
                {
                    var n = obj["Name"]?.ToString();
                    if (!string.IsNullOrEmpty(n) &&
                        (n.Equals(portName, StringComparison.OrdinalIgnoreCase) ||
                         n.Equals(registerName, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
            }
            catch (Exception ex) { _log.Warning($"EnsurePortRegistered/PortExists: {ex.Message}"); }
            return false;
        }

        return await Task.Run(() =>
        {
            try
            {
                if (PortExists())
                {
                    _log.Info($"EnsurePortRegistered: '{registerName}' already present in spooler.");
                    return (true, (string?)null);
                }

                // USB Monitor ports can't be added by hand — they must come from usbprint.sys.
                if (portName.StartsWith("USB", StringComparison.OrdinalIgnoreCase) ||
                    portName.StartsWith("DOT4", StringComparison.OrdinalIgnoreCase) ||
                    portName.StartsWith("WSD", StringComparison.OrdinalIgnoreCase))
                {
                    _log.Info($"EnsurePortRegistered: '{portName}' is a monitor-managed port — cannot add manually.");
                    return (false, "porta gerenciada pelo USB Monitor — não pode ser criada manualmente");
                }

                var escaped = registerName.Replace("'", "''");
                var script = $"try {{ Add-PrinterPort -Name '{escaped}' -ErrorAction Stop; 'OK' }} catch {{ \"ERR:$($_.Exception.Message)\" }}";
                var enc = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -EncodedCommand {enc}",
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var proc = Process.Start(psi)!;
                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();
                proc.WaitForExit(15_000);
                var stdout = outTask.GetAwaiter().GetResult().Trim();
                var stderr = errTask.GetAwaiter().GetResult().Trim();
                _log.Info($"Add-PrinterPort '{registerName}': stdout='{stdout}' stderr='{stderr}'");

                if (PortExists())
                {
                    _log.Info($"EnsurePortRegistered: registered '{registerName}' in spooler.");
                    return (true, (string?)null);
                }

                var reason = stdout.StartsWith("ERR:", StringComparison.OrdinalIgnoreCase)
                    ? stdout[4..]
                    : (!string.IsNullOrWhiteSpace(stderr) ? stderr : stdout);
                if (string.IsNullOrWhiteSpace(reason)) reason = "Add-PrinterPort não confirmou a criação (sem mensagem de erro).";

                // "The specified port already exists" means the port genuinely IS usable —
                // it was created (this run or an earlier one) through a code path (the newer
                // PrintManagement module's own backing store) that Win32_PrinterPort's WMI
                // provider had not yet reflected when PortExists() checked. Trust Add-PrinterPort's
                // own signal over a WMI query that can lag behind it; treat this as success.
                if (reason.Contains("already exist", StringComparison.OrdinalIgnoreCase) ||
                    reason.Contains("já existe", StringComparison.OrdinalIgnoreCase))
                {
                    _log.Info($"EnsurePortRegistered: '{registerName}' reported as already existing by Add-PrinterPort (WMI hadn't caught up) — treating as registered.");
                    return (true, (string?)null);
                }

                _log.Warning($"EnsurePortRegistered: '{registerName}' still not present after Add-PrinterPort. Reason: {reason}");
                return (false, reason);
            }
            catch (Exception ex)
            {
                _log.Warning($"EnsurePortRegisteredAsync: {ex.Message}");
                return (false, ex.Message);
            }
        }, ct);
    }

    public async Task<string> GetUsbDeviceDiagnosticsAsync(string vid, string pid, CancellationToken ct = default)
    {
        // Produces a human-readable snapshot of device + spooler state for the failure
        // message and log — so a "still not working" report is diagnosable without the PC.
        // Answers: is the device connected? which service/driver is bound? what is its
        // status/problem code? does a COM port exist for it? which spooler ports exist?
        return await Task.Run(() =>
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                // IMPORTANT: enumerate ALL matching nodes, not just the first. A composite USB
                // device's TOP-LEVEL node (VID_xxxx&PID_yyyy) commonly reports a generic
                // "class=USB" even when the actual printer FUNCTION — with its own bound driver
                // and port — lives on a CHILD interface node (VID_xxxx&PID_yyyy&MI_00/01/...).
                // Only checking the first match (the parent) would completely miss that child
                // and wrongly conclude "no printer-class binding" when one exists one level down.
                var script =
                    $"$all = @(Get-PnpDevice -ErrorAction SilentlyContinue | " +
                    $"  Where-Object {{ $_.InstanceId -match 'VID_{vid}&PID_{pid}' }}); " +
                    $"if ($all.Count -gt 0) {{ " +
                    $"  foreach ($d in $all) {{ " +
                    $"    $svc  = (Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data; " +
                    $"    $prob = (Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName 'DEVPKEY_Device_ProblemCode' -ErrorAction SilentlyContinue).Data; " +
                    $"    Write-Output \"PRESENT|status=$($d.Status)|class=$($d.Class)|service=$svc|problem=$prob|name=$($d.FriendlyName)|id=$($d.InstanceId)\" " +
                    $"  }} " +
                    $"}} else {{ Write-Output 'ABSENT' }}";
                var enc = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -EncodedCommand {enc}",
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var proc = Process.Start(psi)!;
                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();
                proc.WaitForExit(15_000);
                var stdout = outTask.GetAwaiter().GetResult().Trim();
                errTask.GetAwaiter().GetResult();

                if (stdout.StartsWith("ABSENT", StringComparison.OrdinalIgnoreCase))
                    sb.Append($"Dispositivo VID_{vid}&PID_{pid}: NÃO detectado pelo Windows (verifique cabo/porta USB). ");
                else if (stdout.Contains("PRESENT", StringComparison.OrdinalIgnoreCase))
                {
                    // Show EVERY matching node (parent + composite children) — the printer
                    // function with the real port binding is often a child interface, not
                    // the top-level device, so a single line is not enough to diagnose this.
                    var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(l => l.StartsWith("PRESENT", StringComparison.OrdinalIgnoreCase));
                    int n = 0;
                    foreach (var line in lines)
                    {
                        n++;
                        var pipe = line.IndexOf('|');
                        var details = pipe >= 0 && pipe + 1 < line.Length ? line[(pipe + 1)..] : line;
                        sb.Append($"[Nó {n}: {details}] ");
                    }
                }
                else if (!string.IsNullOrEmpty(stdout))
                    sb.Append($"Estado do dispositivo: {stdout}. ");

                // usbprint.sys bound?
                using (var usbprint = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Enum\USBPRINT"))
                {
                    sb.Append((usbprint != null && usbprint.GetSubKeyNames().Length > 0)
                        ? "usbprint.sys: ativo. "
                        : "usbprint.sys: não vinculado. ");
                }
            }
            catch (Exception ex)
            {
                _log.Warning($"GetUsbDeviceDiagnosticsAsync: {ex.Message}");
            }
            return sb.ToString();
        }, ct);
    }

    private List<DetectedUsbDevice> EnumerateUsbDevices(CancellationToken ct)
    {
        // One PowerShell round-trip: list every present PnP device with its InstanceId,
        // class, status and friendly name. Parsed into records (VID/PID from the InstanceId,
        // COM port from a "(COMx)" suffix in the name). Used by both the resolver and the
        // diagnostics dump. Delimiter '~|~' avoids collision with names containing '|'.
        var result = new List<DetectedUsbDevice>();
        try
        {
            // For every present device we also read DEVPKEY_Device_BusReportedDeviceDesc — the
            // USB product string (e.g. "Gertec G250"). This is CRUCIAL for undriven devices that
            // sit in "Unspecified": their FriendlyName is empty and the only recognizable name
            // lives in the bus-reported description. Delimiter '~|~' avoids '|' collisions.
            var script =
                "Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | ForEach-Object { " +
                "$bus=''; try { $bus = (Get-PnpDeviceProperty -InstanceId $_.InstanceId " +
                "-KeyName 'DEVPKEY_Device_BusReportedDeviceDesc' -ErrorAction SilentlyContinue).Data } catch {}; " +
                "$n = \"$($_.FriendlyName)\" -replace '[\\r\\n]',' '; " +
                "$b = \"$bus\" -replace '[\\r\\n]',' '; " +
                "Write-Output \"$($_.InstanceId)~|~$($_.Class)~|~$($_.Status)~|~$n~|~$b\" }";
            var enc = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -EncodedCommand {enc}",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var proc = Process.Start(psi)!;
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            proc.WaitForExit(30_000);
            var stdout = outTask.GetAwaiter().GetResult();
            errTask.GetAwaiter().GetResult();

            foreach (var rawLine in stdout.Split('\n'))
            {
                ct.ThrowIfCancellationRequested();
                var line = rawLine.Trim();
                if (line.Length == 0) continue;
                var parts = line.Split(new[] { "~|~" }, StringSplitOptions.None);
                if (parts.Length < 4) continue;

                var instanceId = parts[0].Trim();
                var cls = parts[1].Trim();
                var status = parts[2].Trim();
                var name = parts[3].Trim();
                var busName = parts.Length >= 5 ? parts[4].Trim() : "";

                string? vid = null, pid = null, port = null;
                var mVid = Regex.Match(instanceId, @"VID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
                if (mVid.Success) vid = mVid.Groups[1].Value.ToUpperInvariant();
                var mPid = Regex.Match(instanceId, @"PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
                if (mPid.Success) pid = mPid.Groups[1].Value.ToUpperInvariant();
                // COM port can be in either the friendly name or the bus description.
                var mCom = Regex.Match($"{name} {busName}", @"\((COM\d+)\)", RegexOptions.IgnoreCase);
                if (mCom.Success) port = mCom.Groups[1].Value.ToUpperInvariant() + ":";

                result.Add(new DetectedUsbDevice(instanceId, vid, pid, port, name, status, cls, busName));
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"EnumerateUsbDevices: {ex.Message}");
        }
        return result;
    }

    public async Task<DetectedUsbDevice?> ResolvePrinterUsbDeviceAsync(
        IReadOnlyList<string> nameHints, string? catalogVid, string? catalogPid, CancellationToken ct = default)
    {
        // Finds the REAL connected printer among all USB devices, independent of the
        // catalog VID/PID (which may be a template placeholder no device matches). Scores
        // each device by name hints, class, an existing COM port, and the catalog VID as a
        // soft signal — then returns the best. If the winner is a composite parent with no
        // port, its child's COM port (same VID/PID) is attached.
        return await Task.Run(() =>
        {
            var devices = EnumerateUsbDevices(ct);
            if (devices.Count == 0) return null;

            string[] exclude =
            {
                "hub", "host controller", "root ", "composite device", "mass storage",
                "keyboard", "mouse", "webcam", "camera", "audio", "speaker", "microphone",
                "bluetooth", "wireless", "wi-fi", "wifi", "network adapter", "ethernet",
                "card reader", "biometric", "fingerprint", "monitor", "graphics", "display adapter"
            };
            string[] generic =
            {
                "print", "impress", "pos", "thermal", "termic", "térmic", "receipt",
                "escpos", "esc/pos", "ticket", "cupom", "serial", "ga-printer", "ga printer"
            };

            var hints = nameHints
                .Where(h => !string.IsNullOrWhiteSpace(h) && h.Trim().Length >= 2)
                .Select(h => h.ToLowerInvariant().Trim())
                .Distinct()
                .ToList();

            DetectedUsbDevice? best = null;
            int bestScore = 0;
            foreach (var d in devices)
            {
                // Match against BOTH the driver friendly name AND the USB bus-reported name,
                // because an undriven printer ("Unspecified") only carries its name in the latter.
                var nameLc = $"{d.FriendlyName} {d.BusName}".ToLowerInvariant();
                if (exclude.Any(x => nameLc.Contains(x))) continue;

                int score = 0;
                if (hints.Any(h => nameLc.Contains(h))) score += 100;
                if (!string.IsNullOrEmpty(catalogVid) && d.Vid != null &&
                    d.Vid.Equals(catalogVid, StringComparison.OrdinalIgnoreCase)) score += 60;
                if (d.DeviceClass.Equals("Printer", StringComparison.OrdinalIgnoreCase) ||
                    d.DeviceClass.Equals("PrintQueue", StringComparison.OrdinalIgnoreCase)) score += 60;
                if (d.DeviceClass.Equals("Ports", StringComparison.OrdinalIgnoreCase)) score += 45;
                if (d.Port != null) score += 40;
                if (generic.Any(g => nameLc.Contains(g))) score += 50;
                // Undriven USB device ("Unspecified": no class, error/unknown status, has a VID).
                // This is exactly what a connected-but-not-installed printer looks like.
                bool undrivenUsb = d.Vid != null &&
                    (string.IsNullOrEmpty(d.DeviceClass) ||
                     d.Status.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
                     d.Status.Equals("Unknown", StringComparison.OrdinalIgnoreCase));
                if (undrivenUsb) score += 25;

                if (score > bestScore) { bestScore = score; best = d; }
            }

            if (best == null || bestScore <= 0)
            {
                _log.Warning("ResolvePrinterUsbDevice: no printer-like USB device found among connected devices.");
                return null;
            }

            var chosen = best;
            // If the winner has no port but a sibling with the same VID/PID does, adopt it.
            if (chosen.Port == null && chosen.Vid != null)
            {
                var sibling = devices.FirstOrDefault(d =>
                    d.Port != null && d.Vid == chosen.Vid &&
                    (chosen.Pid == null || d.Pid == chosen.Pid));
                if (sibling != null)
                    chosen = chosen with { Port = sibling.Port };
            }

            _log.Info($"ResolvePrinterUsbDevice: best='{chosen.FriendlyName}' score={bestScore} " +
                      $"vid={chosen.Vid} pid={chosen.Pid} port={chosen.Port} class={chosen.DeviceClass} " +
                      $"status={chosen.Status} id={chosen.InstanceId}");
            return chosen;
        }, ct);
    }

    public async Task<string> ListConnectedUsbDevicesAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var devices = EnumerateUsbDevices(ct);
            if (devices.Count == 0) return "nenhum dispositivo USB listado";
            // Show REAL USB devices (those with a VID) first — the printer is one of them —
            // and among those the undriven ones first, since that's what a connected-but-not-
            // installed printer looks like. System devices (no VID) are least relevant.
            var relevant = devices
                .Where(d =>
                {
                    var n = $"{d.FriendlyName} {d.BusName}".ToLowerInvariant();
                    return !n.Contains("hub") && !n.Contains("host controller") && !n.Contains("root ");
                })
                .OrderByDescending(d => d.Vid != null)
                .ThenByDescending(d => string.IsNullOrEmpty(d.DeviceClass) ||
                                       d.Status.Equals("Error", StringComparison.OrdinalIgnoreCase))
                .Take(25)
                .Select(d =>
                {
                    var vp = d.Vid != null ? $"VID_{d.Vid}&PID_{d.Pid}" : "sem-VID";
                    var p = d.Port != null ? $" {d.Port}" : "";
                    var cls = string.IsNullOrEmpty(d.DeviceClass) ? "SemDriver" : d.DeviceClass;
                    return $"• {d.BestName} [{vp} class={cls} status={d.Status}{p}]";
                });
            return string.Join("\n", relevant);
        }, ct);
    }

    // Ports that are purely software (not a physical device connection) and should
    // never be returned as the result of a USB-printer-port diff.
    private static bool IsNonHardwarePort(string name) =>
        name.Equals("FILE:", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("PORTPROMPT:", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("SHRFAX", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("IP_",    StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("LPR_",   StringComparison.OrdinalIgnoreCase);

    public async Task<string?> FindAnyUsbPrinterPortAsync(CancellationToken ct = default)
    {
        // Last-resort: find a USB/DOT4/WSD port already assigned to ANY printer in the
        // spooler — not filtered by manufacturer/model. Used when pnputil /scan-devices
        // caused Windows to auto-create a printer whose name doesn't contain our keywords.
        return await Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT PortName FROM Win32_Printer WHERE PortName LIKE 'USB%' " +
                    "OR PortName LIKE 'DOT4%' OR PortName LIKE 'WSD%'");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var port = obj["PortName"]?.ToString();
                    if (!string.IsNullOrEmpty(port))
                    {
                        _log.Info($"FindAnyUsbPrinterPortAsync: found '{port}' on an existing printer");
                        return port;
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                _log.Warning($"FindAnyUsbPrinterPortAsync failed: {ex.Message}");
                return null;
            }
        }, ct);
    }

    public async Task TriggerPnpScanAsync(CancellationToken ct = default)
    {
        // Force Windows to re-enumerate connected USB devices and register printer ports.
        // Equivalent to "Scan for hardware changes" in Device Manager. After a silent
        // driver install, the USB print monitor port (USB001 etc.) may not yet exist in
        // the spooler — this call causes Windows to detect the device and create it.
        await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = "/scan-devices",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi)!;
                proc.WaitForExit(15_000);
                _log.Info($"pnputil /scan-devices exit={proc.ExitCode}");
            }
            catch (Exception ex)
            {
                _log.Warning($"TriggerPnpScanAsync failed (non-fatal): {ex.Message}");
            }
        }, ct);
    }

    public async Task ReEnumerateUsbPrinterDevicesAsync(CancellationToken ct = default)
    {
        // Software equivalent of unplugging and replugging the USB cable.
        // Targets two categories:
        //   1. Printer-class USB devices — already matched to a driver (driver update/reinstall).
        //   2. Unknown/Error USB devices with a VID — unmatched printer awaiting driver binding
        //      (the common fresh-install case where the driver was just installed but Windows
        //      hasn't yet associated it with the connected device).
        // Disabling then re-enabling forces the PnP Manager to re-run driver matching, which
        // causes usbprint.sys to create the spooler port (USB001 etc.).
        await Task.Run(() =>
        {
            try
            {
                // Targets all USB devices that could be printers:
                // 1. Printer-class USB (any status — a device bound to a generic driver with
                //    Status=OK is exactly what we need to force-rebind after a driver install).
                // 2. Any USB VID device that is NOT a hub, controller, storage, HID, or audio
                //    device — catches printers in Unknown/Error state and custom USB classes.
                var script =
                    "$printerDevs = @(Get-PnpDevice -Class Printer -ErrorAction SilentlyContinue | " +
                    "  Where-Object { $_.InstanceId -like 'USB\\*' });" +
                    "$usbCandidates = @(Get-PnpDevice -ErrorAction SilentlyContinue | " +
                    "  Where-Object { $_.InstanceId -like 'USB\\VID_*' -and " +
                    "    $_.FriendlyName -notlike '*Hub*' -and $_.FriendlyName -notlike '*Controller*' -and " +
                    "    $_.FriendlyName -notlike '*Storage*' -and $_.FriendlyName -notlike '*Mass Storage*' -and " +
                    "    $_.FriendlyName -notlike '*Keyboard*' -and $_.FriendlyName -notlike '*Mouse*' -and " +
                    "    $_.FriendlyName -notlike '*Camera*' -and $_.FriendlyName -notlike '*Audio*' -and " +
                    "    $_.FriendlyName -notlike '*Bluetooth*' });" +
                    "$allDevs = @(@($printerDevs) + @($usbCandidates) | Sort-Object InstanceId -Unique);" +
                    "if ($allDevs.Count -gt 0) {" +
                    "  $allDevs | ForEach-Object {" +
                    "    Disable-PnpDevice -InstanceId $_.InstanceId -Confirm:$false -ErrorAction SilentlyContinue" +
                    "  };" +
                    "  Start-Sleep -Seconds 2;" +
                    "  $allDevs | ForEach-Object {" +
                    "    Enable-PnpDevice -InstanceId $_.InstanceId -Confirm:$false -ErrorAction SilentlyContinue" +
                    "  }" +
                    "};" +
                    "pnputil /scan-devices | Out-Null";

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
                var stderrTask = proc.StandardError.ReadToEndAsync();
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                proc.WaitForExit(20_000);
                stderrTask.GetAwaiter().GetResult();
                stdoutTask.GetAwaiter().GetResult();
                _log.Info($"ReEnumerateUsbPrinterDevices exit={proc.ExitCode}");
            }
            catch (Exception ex)
            {
                _log.Warning($"ReEnumerateUsbPrinterDevicesAsync failed (non-fatal): {ex.Message}");
            }
        }, ct);
    }

    public async Task<string?> ForceUsbPortFromUsbPrintAsync(string vid, string pid, CancellationToken ct = default)
    {
        // When usbprint.sys is already bound to the device (USBPRINT\ node exists in PnP),
        // the USB Print Monitor should create the spooler port on the next Spooler start.
        // This method: (a) detects whether usbprint.sys is bound, (b) triggers a Spooler
        // restart if it is, (c) waits up to 12 s for the port to appear, and (d) returns the
        // port name — or null if usbprint.sys is not yet bound.
        return await Task.Run(() =>
        {
            try
            {
                // Check USBPRINT PnP tree — only populated when usbprint.sys is loaded for a device.
                using var usbprintEnum = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Enum\USBPRINT");
                if (usbprintEnum == null)
                {
                    _log.Info($"ForceUsbPortFromUsbPrint: USBPRINT registry key absent — usbprint.sys not bound for any device.");
                    return null;
                }

                bool deviceFound = false;
                foreach (var hwId in usbprintEnum.GetSubKeyNames())
                {
                    if (hwId.IndexOf($"VID_{vid}", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        hwId.IndexOf($"VID{vid}", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        deviceFound = true;
                        _log.Info($"ForceUsbPortFromUsbPrint: found USBPRINT device '{hwId}' — usbprint.sys IS bound.");
                        break;
                    }
                }

                // Even without VID match, if USBPRINT has ANY entry it might be our printer
                if (!deviceFound && usbprintEnum.GetSubKeyNames().Length > 0)
                {
                    deviceFound = true;
                    _log.Info($"ForceUsbPortFromUsbPrint: USBPRINT has entries (no VID match, using first).");
                }

                if (!deviceFound) return null;

                // usbprint.sys is bound but Spooler may not have created the port yet.
                // Restart Spooler so USB Monitor enumerates USBPRINT devices and creates ports.
                _log.Info("ForceUsbPortFromUsbPrint: usbprint.sys bound — restarting Spooler to flush USB Monitor.");
                var stopScript = "Stop-Service -Name Spooler -Force -ErrorAction SilentlyContinue";
                var stopEnc = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(stopScript));
                RunProcess("powershell.exe", $"-NoProfile -NonInteractive -EncodedCommand {stopEnc}");
                Thread.Sleep(1500);
                RunProcess("net.exe", "start spooler");
                Thread.Sleep(8000); // extended wait — USB Monitor enumeration can be slow

                // Check USB Monitor registry for the new port.
                using var monPorts = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Print\Monitors\USB Monitor\Ports");
                if (monPorts != null)
                {
                    var portName = monPorts.GetSubKeyNames().FirstOrDefault(s => !string.IsNullOrEmpty(s));
                    if (portName != null)
                    {
                        _log.Info($"ForceUsbPortFromUsbPrint: port '{portName}' found in USB Monitor registry.");
                        return portName;
                    }
                }

                _log.Info("ForceUsbPortFromUsbPrint: no port in USB Monitor registry after restart.");
                return null;
            }
            catch (Exception ex)
            {
                _log.Warning($"ForceUsbPortFromUsbPrintAsync: {ex.Message}");
                return null;
            }
        }, ct);
    }

    public async Task ReEnumerateDeviceByVidPidAsync(string vid, string pid, CancellationToken ct = default)
    {
        // Targeted disable/enable of the exact USB device (by VID/PID) to force Windows to
        // re-run driver matching. After the installer EXE stages the real INF, the device is
        // already connected but Windows hasn't bound usbprint.sys yet. Cycling the device via
        // PnP forces the match and causes the USB Print Monitor to create the spooler port.
        await Task.Run(() =>
        {
            try
            {
                var script =
                    $"$dev = Get-PnpDevice -ErrorAction SilentlyContinue | " +
                    $"  Where-Object {{ $_.InstanceId -match 'VID_{vid}&PID_{pid}' }} | Select-Object -First 1; " +
                    $"if ($dev) {{ " +
                    $"  Write-Output \"Found: $($dev.InstanceId)\"; " +
                    $"  Disable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction SilentlyContinue; " +
                    $"  Start-Sleep -Seconds 2; " +
                    $"  Enable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction SilentlyContinue; " +
                    $"  Start-Sleep -Seconds 2; " +
                    $"  pnputil /scan-devices | Out-Null " +
                    $"}} else {{ Write-Output 'Device not found' }}";
                var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var proc = Process.Start(psi)!;
                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();
                proc.WaitForExit(20_000);
                var stdout = outTask.GetAwaiter().GetResult();
                errTask.GetAwaiter().GetResult();
                _log.Info($"ReEnumerateDeviceByVidPid({vid},{pid}) exit={proc.ExitCode} out='{stdout.Trim()}'");
            }
            catch (Exception ex) { _log.Warning($"ReEnumerateDeviceByVidPidAsync: {ex.Message}"); }
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
            // printui.dll's PrintUIEntry /dl shows a NATIVE, BLOCKING message box ("Não é
            // possível remover a impressora...") when the named printer doesn't exist — this
            // froze the automated install waiting for a manual OK click, exactly the kind of
            // manual intervention this app must never require. Remove-Printer with
            // -ErrorAction SilentlyContinue is fully silent and idempotent (no error, no
            // dialog, whether or not the printer exists), so it's now the only mechanism used.
            try
            {
                var script = $"Remove-Printer -Name '{EscapePs(printerName)}' -ErrorAction SilentlyContinue";
                var enc = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -EncodedCommand {enc}",
                    UseShellExecute = false, CreateNoWindow = true
                };
                using var proc = Process.Start(psi)!;
                proc.WaitForExit(15_000);
                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                _log.Warning($"DeletePrinterAsync (Remove-Printer) failed for '{printerName}': {ex.Message}");
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
            // Get-PrinterPort (PrintManagement module) first. Confirmed in the field: on some
            // machines Win32_PrinterPort itself throws "Classe inválida" (invalid class) —
            // not a lag, the WMI class is simply broken/unregistered there — silently returning
            // an empty list from the WMI path on every single call. Every caller here treats an
            // empty list as "no ports exist at all", so a genuinely real, already-verified port
            // (e.g. a printer auto-installed on USB001) gets wrongly discarded as unverified,
            // which was traced as the actual root cause of a full port-resolution failure.
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -Command \"(Get-PrinterPort).Name\"",
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi)!;
                var stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(15_000);
                var psPorts = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(l => l.Length > 0)
                    .ToList();
                if (psPorts.Count > 0)
                    return (IReadOnlyList<string>)psPorts;
            }
            catch (Exception ex)
            {
                _log.Warning($"GetAvailablePortsAsync (PowerShell) failed, falling back to WMI: {ex.Message}");
            }

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
    public const string DiagLpdAvailable  = "__DIAG:LPD_AVAILABLE__";

    public async Task<IReadOnlyList<string>> GetSharedPrintersAsync(string host, CancellationToken ct = default)
    {
        var result = new List<string>();

        // Step 1 — reachability. Port 445 (SMB) is required by every strategy below.
        // Fast 1.5s probe so an unreachable host fails almost immediately.
        bool port445Open = await RunWithTimeoutAsync(
            () => IsTcpPortOpen(host, 445, 1500), 2500, false, "port445");
        _log.Info($"Port 445 on {host}: {(port445Open ? "open" : "CLOSED")}");

        // Check LPD port 515 in parallel — this doesn't need SMB auth and works on any network.
        bool lpdAvailable = await RunWithTimeoutAsync(
            () => IsTcpPortOpen(host, 515, 1500), 2500, false, "port515");
        _log.Info($"Port 515 (LPD) on {host}: {(lpdAvailable ? "open" : "closed")}");
        if (lpdAvailable)
            result.Add(DiagLpdAvailable);

        if (!port445Open)
        {
            // SMB unavailable — return diagnostic so the ViewModel can offer LPD path.
            result.Add(DiagPortClosed);
            return result;
        }

        // Step 2 — authenticate the SMB session with the current user's credentials so the
        // enumeration APIs below don't get ACCESS_DENIED. Bounded at 5s by net.exe itself.
        bool ipcOk = await RunWithTimeoutAsync(
            () => TryEstablishIpcSession(host), 6000, false, "ipc-session");

        bool HasPrinters() => result.Any(s => !s.StartsWith("__DIAG:"));

        // Strategy A — EnumPrinters (winspool.drv, \pipe\spoolss). The exact API the Windows
        // "Add Printer" wizard uses. Wrapped in a 5s timeout so a stalled RPC can't hang the UI.
        var spoolShares = await RunWithTimeoutAsync(
            () => EnumeratePrinterSharesViaEnumPrinters(host), 5000, new List<string>(), "EnumPrinters");
        result.AddRange(spoolShares);
        if (HasPrinters()) return result;

        // Strategy B — NetShareEnum (netapi32.dll, \pipe\srvsvc). Different named pipe;
        // succeeds when file sharing is on but the spooler pipe is blocked.
        var netApiShares = await RunWithTimeoutAsync(
            () => EnumeratePrintSharesViaNetApi(host), 5000, new List<string>(), "NetShareEnum");
        result.AddRange(netApiShares.Where(s => !result.Contains(s, StringComparer.OrdinalIgnoreCase)));
        if (HasPrinters()) return result;

        // Strategy C — .NET UNC browse (Directory.GetDirectories). Uses the current user's
        // NTLM token automatically, exactly like Explorer browsing \\host. Returns all
        // non-hidden shares (disk + print) — at this point any result beats nothing.
        // Hard 6s timeout: this call has no native timeout and is the main hang risk.
        var uncShares = await RunWithTimeoutAsync(
            () => TryListAllSharesViaUncBrowsing(host), 6000, new List<string>(), "UNC-browse");
        result.AddRange(uncShares.Where(s => !result.Contains(s, StringComparer.OrdinalIgnoreCase)));
        if (HasPrinters()) return result;

        // Nothing found. If we never got an authenticated session, surface access-denied so the
        // ViewModel can give sharing/permission guidance instead of a generic message.
        if (!ipcOk)
            result.Add(DiagAccessDenied);

        return result;
    }

    // Runs a (potentially blocking) synchronous function on a background thread and gives up
    // after timeoutMs. If it times out, the background work is abandoned (it can't be force-
    // killed) but the caller returns immediately with defaultValue so the UI never hangs.
    private async Task<T> RunWithTimeoutAsync<T>(Func<T> func, int timeoutMs, T defaultValue, string label)
    {
        try
        {
            var task = Task.Run(func);
            var completed = await Task.WhenAny(task, Task.Delay(timeoutMs));
            if (completed == task)
                return await task;

            _log.Warning($"{label} timed out after {timeoutMs}ms — skipping");
            return defaultValue;
        }
        catch (Exception ex)
        {
            _log.Warning($"{label} failed: {ex.Message}");
            return defaultValue;
        }
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
            // NetShareEnum) can traverse named pipes without ACCESS_DENIED (5).
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

    public async Task<bool> PrinterExistsAsync(string printerName, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            // Get-Printer (PrintManagement module) reflects the live Spooler state
            // immediately; Win32_Printer (WMI) has repeatedly been confirmed to lag behind it
            // in this environment. Try PowerShell first, WMI only as a fallback if it fails
            // to run at all (e.g. module not present).
            try
            {
                var script = $"if (Get-Printer -Name '{EscapePs(printerName)}' -ErrorAction SilentlyContinue) " +
                             "{ 'YES' } else { 'NO' }";
                var enc = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -EncodedCommand {enc}",
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi)!;
                var stdout = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(15_000);
                if (stdout.Equals("YES", StringComparison.OrdinalIgnoreCase)) return true;
                if (stdout.Equals("NO", StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch (Exception ex)
            {
                _log.Warning($"PrinterExistsAsync (PowerShell) failed, falling back to WMI: {ex.Message}");
            }

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

    public async Task<string?> GetPrinterDriverAsync(string printerName, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT DriverName FROM Win32_Printer WHERE Name='{EscapeWmi(printerName)}'");
                foreach (ManagementObject obj in searcher.Get())
                    return obj["DriverName"]?.ToString();
                return null;
            }
            catch (Exception ex)
            {
                _log.Warning($"GetPrinterDriverAsync failed: {ex.Message}");
                return null;
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
                    var fbPort = fallback.PortName;
                    _log.Info($"FindAutoInfo: fallback driver='{fallback.DriverName}' port='{fbPort ?? "none"}'");
                    return (fallback.DriverName, fbPort);
                }
            }
            catch (Exception ex)
            {
                _log.Warning($"FindAutoInstalledPrinterInfoAsync failed: {ex.Message}");
            }
            return (null, null);
        }, ct);
    }

    public async Task<string?> FindPortFromNewPrinterAsync(
        IReadOnlyList<string> printersBefore, CancellationToken ct = default)
    {
        // Returns the port of the FIRST newly-created printer (not in printersBefore).
        // Catches cases where the driver installer creates a printer queue automatically —
        // the installer knows the correct port name even when our detection doesn't.
        if (printersBefore.Count == 0) return null;
        return await Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, PortName FROM Win32_Printer");
                foreach (ManagementObject obj in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    var name = obj["Name"]?.ToString() ?? "";
                    var portName = obj["PortName"]?.ToString();
                    if (string.IsNullOrEmpty(portName)) continue;
                    if (printersBefore.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

                    if (name.Contains("Microsoft",  StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("OneNote",    StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Fax",        StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("XPS",        StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("PDF",        StringComparison.OrdinalIgnoreCase)) continue;

                    if (portName.StartsWith("IP_",       StringComparison.OrdinalIgnoreCase) ||
                        portName.StartsWith("LPR_",      StringComparison.OrdinalIgnoreCase) ||
                        portName.Equals("FILE:",         StringComparison.OrdinalIgnoreCase) ||
                        portName.Equals("PORTPROMPT:",   StringComparison.OrdinalIgnoreCase)) continue;

                    _log.Info($"FindPortFromNewPrinter: new printer '{name}' port='{portName}'");
                    return portName;
                }
                return null;
            }
            catch (Exception ex)
            {
                _log.Warning($"FindPortFromNewPrinterAsync: {ex.Message}");
                return null;
            }
        }, ct);
    }

    public async Task<(string? Name, string? DriverName, string? PortName)> FindNewlyCreatedPrinterAsync(
        IReadOnlyList<string> printersBefore, CancellationToken ct = default)
    {
        // Returns the FULL identity (name + driver + port) of the printer the manufacturer's
        // installer created automatically — the authoritative, known-good driver/port pairing.
        // We adopt these instead of guessing a port and probing driver names: whatever the
        // installer put in the spooler already works with the physical device.
        if (printersBefore.Count == 0)
            return (null, null, null);

        return await Task.Run<(string?, string?, string?)>(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DriverName, PortName FROM Win32_Printer");
                foreach (ManagementObject obj in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    var name = obj["Name"]?.ToString() ?? "";
                    var driver = obj["DriverName"]?.ToString();
                    var portName = obj["PortName"]?.ToString();
                    if (string.IsNullOrEmpty(portName)) continue;
                    if (printersBefore.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

                    if (name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("OneNote",   StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Fax",       StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("XPS",       StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("PDF",       StringComparison.OrdinalIgnoreCase)) continue;

                    if (portName.StartsWith("IP_",     StringComparison.OrdinalIgnoreCase) ||
                        portName.StartsWith("LPR_",    StringComparison.OrdinalIgnoreCase) ||
                        portName.Equals("FILE:",       StringComparison.OrdinalIgnoreCase) ||
                        portName.Equals("PORTPROMPT:", StringComparison.OrdinalIgnoreCase)) continue;

                    _log.Info($"FindNewlyCreatedPrinter: '{name}' driver='{driver}' port='{portName}'");
                    return (name, driver, portName);
                }
                return (null, null, null);
            }
            catch (Exception ex)
            {
                _log.Warning($"FindNewlyCreatedPrinterAsync: {ex.Message}");
                return (null, null, null);
            }
        }, ct);
    }

    public async Task<bool> EnsureGenericTextDriverAsync(CancellationToken ct = default)
    {
        // Guarantees the built-in "Generic / Text Only" print driver is registered with the
        // spooler so Add-Printer can use it. It ships inbox with Windows but sometimes needs
        // Add-PrinterDriver to surface it. Used as the guaranteed final fallback so a printer
        // is always created (raw/ESC-POS text) even when no vendor driver could be used.
        return await Task.Run(() =>
        {
            try
            {
                bool Present()
                {
                    using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_PrinterDriver");
                    foreach (ManagementObject d in s.Get())
                    {
                        var n = d["Name"]?.ToString() ?? "";
                        if (n.StartsWith("Generic / Text Only", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    return false;
                }

                if (Present())
                {
                    _log.Info("EnsureGenericTextDriver: already registered.");
                    return true;
                }

                var script = "try { Add-PrinterDriver -Name 'Generic / Text Only' -ErrorAction Stop; 'OK' } " +
                             "catch { $_.Exception.Message }";
                var enc = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
                RunProcess("powershell.exe", $"-NoProfile -NonInteractive -EncodedCommand {enc}");

                var ok = Present();
                _log.Info($"EnsureGenericTextDriver: registered={ok}");
                return ok;
            }
            catch (Exception ex)
            {
                _log.Warning($"EnsureGenericTextDriverAsync: {ex.Message}");
                return false;
            }
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

        // Neither known CLIXML shape matched (e.g. it was only a progress/verbose record,
        // not an error) — this is a generic helper used by every PowerShell call in this
        // class (Add-Printer, port registration, driver registration, shared connections),
        // so the fallback must not assume any one of those contexts. Returning a fixed
        // "share/host" message here was misleading in every other call site.
        return raw.Length > 300 ? raw[..300] : raw;
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

    private (bool ok, string? error) AddPrinterViaPowerShellWithReason(string printerName, string driverName, string portName)
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
            // Drain both streams concurrently — not draining stdout while stderr is read
            // can deadlock when the pipe buffer fills (typically at 4 KB).
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            process.WaitForExit(30_000);
            var stderr = stderrTask.GetAwaiter().GetResult().Trim();
            stdoutTask.GetAwaiter().GetResult(); // discard stdout

            var cleanError = ExtractPsError(stderr);
            _log.Info($"PowerShell Add-Printer exit={process.ExitCode} error='{cleanError}'");
            return (process.ExitCode == 0, process.ExitCode == 0 ? null : (string.IsNullOrWhiteSpace(cleanError) ? $"exit code {process.ExitCode}" : cleanError));
        }
        catch (Exception ex)
        {
            _log.Error($"PowerShell Add-Printer exception: {ex.Message}");
            return (false, ex.Message);
        }
    }

    private (bool ok, string? error) AddPrinterViaWmiWithReason(string printerName, string driverName, string portName)
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
                return (false, "Put() retornou null");
            }
            _log.Info($"Printer added via WMI: {printerName} (path={result.Path})");
            return (true, null);
        }
        catch (ManagementException ex)
        {
            _log.Error($"WMI printer creation failed: ErrorCode={ex.ErrorCode} Message='{ex.Message}'");
            return (false, $"{ex.ErrorCode}: {ex.Message}");
        }
        catch (Exception ex)
        {
            _log.Error($"WMI printer creation unexpected error: {ex.Message}");
            return (false, ex.Message);
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

    // ──────────────────────────────────────────────────────────────────────────────
    // LPD / LPR support
    // LPD (port 515) doesn't require Windows credentials — it bypasses the SMB
    // authentication barrier that blocks shared-printer discovery on workgroups.
    // ──────────────────────────────────────────────────────────────────────────────

    public async Task EnableLpdServiceAsync(CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            try
            {
                // If already running, nothing to do.
                if (LpdServiceIsRunning())
                {
                    _log.Info("LPD service already running.");
                    return;
                }

                // If service binary exists but is just stopped, start it immediately
                // (avoids the slow DISM path when the feature is already installed).
                if (LpdServiceExists())
                {
                    _log.Info("LPD service installed but not running — starting.");
                    LpdServiceStart();
                    if (LpdServiceIsRunning()) return;
                }

                // Feature not installed: use DISM — more reliable than PowerShell
                // Enable-WindowsOptionalFeature, works offline without internet.
                _log.Info("Enabling LPD-Service feature via DISM...");
                var dismPsi = new ProcessStartInfo
                {
                    FileName = "dism.exe",
                    Arguments = "/Online /Enable-Feature /FeatureName:LPD-Service /All /NoRestart /Quiet",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var dism = Process.Start(dismPsi)!)
                {
                    dism.WaitForExit(120_000);
                    _log.Info($"DISM LPD-Service exit={dism.ExitCode}");
                }

                // PowerShell fallback if DISM failed (e.g. in some ARM/Server editions).
                if (!LpdServiceExists())
                {
                    _log.Info("DISM did not install service — trying PowerShell fallback.");
                    var script =
                        "Enable-WindowsOptionalFeature -Online -FeatureName 'LPD-Service' " +
                        "-All -NoRestart -ErrorAction SilentlyContinue | Out-Null";
                    var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
                    var psPsi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var ps = Process.Start(psPsi)!;
                    ps.WaitForExit(90_000);
                    _log.Info($"PowerShell LPD fallback exit={ps.ExitCode}");
                }

                // Set to auto-start and launch.
                LpdServiceStart();
                _log.Info(LpdServiceIsRunning() ? "LPD service started successfully." : "LPD service did not start.");
            }
            catch (Exception ex)
            {
                _log.Warning($"EnableLpdServiceAsync failed (non-fatal): {ex.Message}");
            }
        }, ct);
    }

    private static bool LpdServiceIsRunning()
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", "query LPDSVC")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(5_000);
            return p.StandardOutput.ReadToEnd().Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool LpdServiceExists()
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", "query LPDSVC")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(5_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static void LpdServiceStart()
    {
        try
        {
            var cfg = new ProcessStartInfo("sc.exe", "config LPDSVC start=auto")
            {
                UseShellExecute = false, CreateNoWindow = true
            };
            using var c = Process.Start(cfg)!;
            c.WaitForExit(5_000);

            var start = new ProcessStartInfo("sc.exe", "start LPDSVC")
            {
                UseShellExecute = false, CreateNoWindow = true
            };
            using var s = Process.Start(start)!;
            s.WaitForExit(10_000);
        }
        catch { }
    }

    public async Task<bool> IsLpdAvailableAsync(string host, CancellationToken ct = default)
    {
        return await RunWithTimeoutAsync(() => IsTcpPortOpen(host, 515, 1500), 2500, false, "IsLpdAvailable");
    }

    public async Task<bool> SharePrinterAsync(string printerName, string shareName, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                var safe = EscapePs(printerName);
                var safeShare = EscapePs(shareName);
                var script = $"Set-Printer -Name '{safe}' -Shared $true -ShareName '{safeShare}'";
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
                var shareStderrTask = proc.StandardError.ReadToEndAsync();
                var shareStdoutTask = proc.StandardOutput.ReadToEndAsync();
                proc.WaitForExit(15_000);
                shareStderrTask.GetAwaiter().GetResult();
                shareStdoutTask.GetAwaiter().GetResult();
                _log.Info($"SharePrinter '{printerName}' as '{shareName}': exit={proc.ExitCode}");
                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                _log.Warning($"SharePrinterAsync failed: {ex.Message}");
                return false;
            }
        }, ct);
    }

    private static string EscapePs(string value) =>
        value.Replace("'", "''");

    public async Task<bool> TryEnableLpdRemotelyAsync(string host, CancellationToken ct = default)
    {
        // Attempt to start LPDSVC on the remote machine via sc.exe.
        // Works when both PCs share the same admin credentials (e.g. same domain or same local account).
        // Fails silently when authentication is denied — caller checks IsLpdAvailableAsync afterwards.
        return await Task.Run(() =>
        {
            try
            {
                var cfg = new ProcessStartInfo("sc.exe", $@"\\{host} config LPDSVC start=auto")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
                };
                using (var c = Process.Start(cfg)!) c.WaitForExit(6_000);

                var start = new ProcessStartInfo("sc.exe", $@"\\{host} start LPDSVC")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
                };
                using var s = Process.Start(start)!;
                s.WaitForExit(8_000);
                // exit 0 = started, 1056 = already running — both count as success
                _log.Info($"TryEnableLpdRemotely {host}: sc exit={s.ExitCode}");
                return s.ExitCode == 0 || s.ExitCode == 1056;
            }
            catch (Exception ex)
            {
                _log.Info($"TryEnableLpdRemotely {host} failed (non-fatal): {ex.Message}");
                return false;
            }
        }, ct);
    }

    public async Task<bool> CreateLprPortAsync(string portName, string host, string queueName, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                var scope = new ManagementScope(@"\\.\root\cimv2");
                scope.Connect();

                // Reuse an existing LPR port rather than failing on re-install
                using var existing = new ManagementObjectSearcher(scope,
                    new ObjectQuery($"SELECT Name FROM Win32_TCPIPPrinterPort WHERE Name='{EscapeWmi(portName)}'"));
                foreach (ManagementObject _ in existing.Get())
                {
                    _log.Info($"LPR port '{portName}' already exists — reusing.");
                    return true;
                }

                var path = new ManagementPath("Win32_TCPIPPrinterPort");
                using var mc = new ManagementClass(scope, path, null);
                using var port = mc.CreateInstance();

                port["Name"] = portName;
                port["HostAddress"] = host;
                port["PortNumber"] = (uint)515;
                port["Protocol"] = (uint)2;        // 2 = LPR (vs 1 = RAW)
                port["Queue"] = queueName;          // LPD queue name = Windows share name
                port["SNMPEnabled"] = false;

                port.Put();
                _log.Info($"LPR port created: {portName} → {host}:515 queue='{queueName}'");
                return true;
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to create LPR port {portName}", ex);
                return false;
            }
        }, ct);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Built-in LPD server (port 515)
    // Starts our own RFC 1179 LPD server instead of relying on the Windows LPDSVC
    // optional feature (which requires a reboot after DISM install on many machines).
    // If port 515 is already bound (LPDSVC is running), we skip silently.
    // ──────────────────────────────────────────────────────────────────────────────

    public void StartLpdServer()
    {
        // Don't start if something else (e.g. LPDSVC) already owns port 515.
        if (IsPort515Bound())
        {
            _log.Info("Port 515 already in use (LPDSVC running) — skipping built-in LPD server.");
            return;
        }

        try
        {
            new LpdServer(_log).Start();
        }
        catch (Exception ex)
        {
            _log.Warning($"StartLpdServer failed: {ex.Message}");
        }
    }

    private static bool IsPort515Bound()
    {
        try
        {
            using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 515);
            probe.Start();
            probe.Stop();
            return false; // successfully bound → port was free
        }
        catch
        {
            return true; // bind failed → already in use
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // PrinterMode Discovery (port 9876)
    // A lightweight TCP listener that returns the list of shared printer names when
    // queried. Allows PC-B to discover printers on PC-A without SMB authentication.
    // Response format: one line per printer → "shareName|displayName"
    // ──────────────────────────────────────────────────────────────────────────────

    public const int DiscoveryPort = 9876;

    public void StartDiscoveryListener()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Allow port 9876 inbound so other PCs can query printer names.
                OpenDiscoveryFirewallRule();

                var listener = new System.Net.Sockets.TcpListener(
                    System.Net.IPAddress.Any, DiscoveryPort);
                listener.Start();
                _log.Info($"PrinterMode discovery listener started on port {DiscoveryPort}");

                while (true)
                {
                    try
                    {
                        var client = await listener.AcceptTcpClientAsync();
                        _ = HandleDiscoveryClientAsync(client);
                    }
                    catch { /* accept errors are non-fatal */ }
                }
            }
            catch (Exception ex)
            {
                _log.Warning($"Discovery listener failed: {ex.Message}");
            }
        });
    }

    private static void OpenDiscoveryFirewallRule()
    {
        try
        {
            // Delete any stale rule first (idempotent).
            var del = new ProcessStartInfo("netsh",
                "advfirewall firewall delete rule name=\"PrinterMode Discovery\"")
            { UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(del)!) p.WaitForExit(5_000);

            var add = new ProcessStartInfo("netsh",
                $"advfirewall firewall add rule name=\"PrinterMode Discovery\" " +
                $"dir=in action=allow protocol=tcp localport={DiscoveryPort} profile=any")
            { UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(add)!) p.WaitForExit(5_000);
        }
        catch { }
    }

    private async Task HandleDiscoveryClientAsync(System.Net.Sockets.TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true)
                { AutoFlush = true };

            // Return shared printers: "shareName|displayName|driverName" per line
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, ShareName, DriverName FROM Win32_Printer WHERE Shared = True");
            foreach (ManagementObject p in searcher.Get())
            {
                var share      = p["ShareName"]?.ToString();
                var name       = p["Name"]?.ToString();
                var driverName = p["DriverName"]?.ToString();
                if (!string.IsNullOrWhiteSpace(share))
                    await writer.WriteLineAsync($"{share}|{name ?? share}|{driverName ?? ""}");
            }
        }
        catch (Exception ex)
        {
            _log.Info($"Discovery client handler error (non-fatal): {ex.Message}");
        }
        finally
        {
            client.Close();
        }
    }

    public async Task<IReadOnlyList<string>> GetRemoteSharedPrintersAsync(
        string host, CancellationToken ct = default)
    {
        // Format returned: "shareName|displayName" per entry.
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(3000);

            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(host, DiscoveryPort, cts.Token);
            client.ReceiveTimeout = 3000;

            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

            var results = new List<string>();
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    results.Add(line.Trim());
            }

            _log.Info($"Discovery from {host}: {results.Count} printer(s) found");
            return results;
        }
        catch
        {
            return [];
        }
    }
}
