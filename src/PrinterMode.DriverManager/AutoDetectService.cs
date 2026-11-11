using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PrinterMode.Core.Enums;
using PrinterMode.Core.Interfaces;
using PrinterMode.Core.Models;

namespace PrinterMode.DriverManager;

/// <summary>
/// Reuses the existing, field-tested matching building blocks (the USB name-hint scorer
/// behind ResolvePrinterUsbDeviceAsync, and the per-host shared-printer discovery query on
/// port 9876) to answer one question the install flow didn't ask before: given a catalog
/// model, how is it actually reachable right now, without the user picking USB/Serial/
/// Network/Shared or typing a port/IP? This service only DISCOVERS targets — it never
/// changes how a driver is installed once one is chosen (that stays in DriverInstaller,
/// completely unchanged).
/// </summary>
public class AutoDetectService : IAutoDetectService
{
    private readonly IWindowsPrinterService _printerService;
    private readonly ILogService _log;

    // A device needs at least this much signal (a real name-hint match, or the catalog's
    // own VID, worth 100/60 respectively in ScoreUsbDevicesAsync) to count as a plausible
    // candidate — filters out USB "Ports" noise (score 45) with no actual relation to the
    // selected model.
    private const int UsbCandidateThreshold = 60;

    private const int DiscoveryPort = 9876; // PrinterMode's own shared-printer query port
    private const int RawPrintPort = 9100;  // standard JetDirect/RAW port most network thermal printers use
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromMilliseconds(300);
    private const int MaxParallelScans = 64;

    public AutoDetectService(IWindowsPrinterService printerService, ILogService log)
    {
        _printerService = printerService;
        _log = log;
    }

    public async Task<IReadOnlyList<InstallTarget>> DetectAsync(DriverInfo driver, CancellationToken ct = default)
    {
        var targets = new List<InstallTarget>();

        // USB scoring already weighs manufacturer/model/driver-name hints together with
        // class/VID/port signals (ScoreUsbDevicesAsync, untouched) — a bare manufacturer
        // hint there is safe. Matching a shared printer on the network has none of those
        // other signals, so it leans entirely on the name string — a bare 2-letter model
        // like Elgin's "i7"/"i8"/"i9" (the exact sibling-model mixup already fixed in
        // ResolveActualDriverName) or the manufacturer alone would too easily match the
        // wrong shared printer. Use only the fuller driver-name aliases there, and the
        // model string only when it is long enough to be distinctive on its own.
        var usbHints = new List<string> { driver.Manufacturer, driver.Model };
        usbHints.AddRange(driver.AllDriverNames());

        var sharedHints = new List<string>(driver.AllDriverNames());
        if (driver.Model.Length >= 4) sharedHints.Add(driver.Model);

        var local = await DetectLocalAsync(driver, usbHints, ct);
        targets.AddRange(local);

        // Only bother scanning the network for connection types this model actually supports —
        // a serial-only thermal printer, for instance, has no business on a network scan.
        var supportsNetwork = driver.SupportedPorts.Count == 0 ||
            driver.SupportedPorts.Any(p => p.Equals("TCP/IP", StringComparison.OrdinalIgnoreCase));
        if (supportsNetwork)
        {
            var network = await DetectNetworkAsync(driver, sharedHints, ct);
            targets.AddRange(network);
        }

        _log.Info($"AutoDetect: {targets.Count} candidate(s) for '{driver.DisplayName}'.");
        return targets;
    }

    private async Task<IReadOnlyList<InstallTarget>> DetectLocalAsync(
        DriverInfo driver, IReadOnlyList<string> hints, CancellationToken ct)
    {
        var scored = await _printerService.ScoreUsbDevicesAsync(hints, driver.VendorId, driver.ProductId, ct);
        var results = new List<InstallTarget>();

        foreach (var (device, score) in scored)
        {
            if (score < UsbCandidateThreshold) continue;

            // A device that already has a bound COM port is a USB-to-serial printer (or a
            // "Ports" class device) — reachable as Serial; otherwise treat it as a plain USB
            // printer (the installer resolves/creates the actual USB print port itself).
            bool isSerialLike = device.DeviceClass.Equals("Ports", StringComparison.OrdinalIgnoreCase)
                && device.Port != null;
            // EnumerateUsbDevices reports a serial port as "COM3:" (spooler form); the rest
            // of the app (PortEntry, ConfigureSerialPortAsync) uses the bare "COM3" form —
            // DriverInstaller's Serial case normalizes either way, but strip it here too so
            // the picker shows "COM3", not "COM3:".
            var displayPort = device.Port?.TrimEnd(':');

            results.Add(new InstallTarget
            {
                Title = "Local — conectada neste PC",
                Subtitle = isSerialLike ? $"Serial, porta {displayPort}" : "USB",
                ConnectionType = isSerialLike ? ConnectionType.Serial : ConnectionType.USB,
                // USB always installs on the port the spooler itself resolves/creates
                // (same "Automático" convention the manual flow already uses) — never a
                // guessed port name. Only Serial gets an explicit PortName, because that's
                // a real, already-bound COM port the device is sitting on.
                PortName = isSerialLike ? displayPort : null
            });

            // One local candidate is enough signal — a second "Ports" sibling for the same
            // physical device (common: composite USB-serial adapters enumerate twice) would
            // just be noise in the picker.
            break;
        }

        return results;
    }

    private async Task<IReadOnlyList<InstallTarget>> DetectNetworkAsync(
        DriverInfo driver, IReadOnlyList<string> hints, CancellationToken ct)
    {
        var results = new List<InstallTarget>();
        var subnetHosts = GetLocalSubnetHosts();
        if (subnetHosts.Count == 0) return results;

        // Two independent scans over the same host list, in parallel: one for other PCs
        // running PrinterMode (discovery port 9876), one for printers with their own
        // network card (raw/JetDirect port 9100). A host open on 9876 is never also listed
        // as a bare "rede direta" result — it already gets a proper shared-printer entry.
        using var throttle = new SemaphoreSlim(MaxParallelScans);

        var discoveryHits = new List<string>();
        var rawHits = new List<string>();
        var discoveryLock = new object();
        var rawLock = new object();

        var tasks = subnetHosts.Select(async host =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                var discoveryOpen = await IsPortOpenAsync(host, DiscoveryPort, ct);
                if (discoveryOpen) lock (discoveryLock) discoveryHits.Add(host);
                else
                {
                    var rawOpen = await IsPortOpenAsync(host, RawPrintPort, ct);
                    if (rawOpen) lock (rawLock) rawHits.Add(host);
                }
            }
            finally { throttle.Release(); }
        });
        await Task.WhenAll(tasks);

        foreach (var host in discoveryHits)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<string> shares;
            try { shares = await _printerService.GetRemoteSharedPrintersAsync(host, ct); }
            catch { continue; }

            foreach (var entry in shares)
            {
                var parts = entry.Split('|');
                var shareName = parts.Length > 0 ? parts[0] : entry;
                var displayName = parts.Length > 1 ? parts[1] : shareName;
                var driverName = parts.Length > 2 && !string.IsNullOrEmpty(parts[2]) ? parts[2] : null;

                if (!MatchesModel(displayName, driverName, hints)) continue;

                results.Add(new InstallTarget
                {
                    Title = $"Compartilhada — {host}",
                    Subtitle = "Rede local, via PrinterMode",
                    ConnectionType = ConnectionType.Shared,
                    SharedHost = host,
                    SharedPrinterName = shareName,
                    SharedDriverName = driverName,
                    SharedDisplayName = displayName
                });
            }
        }

        foreach (var host in rawHits)
        {
            results.Add(new InstallTarget
            {
                Title = $"Rede direta — {host}",
                Subtitle = "Impressora com IP próprio",
                ConnectionType = ConnectionType.Network,
                IpAddress = host,
                NetworkPort = RawPrintPort
            });
        }

        return results;
    }

    private static bool MatchesModel(string displayName, string? driverName, IReadOnlyList<string> hints)
    {
        var haystack = $"{displayName} {driverName}".ToLowerInvariant();
        return hints.Any(h => !string.IsNullOrWhiteSpace(h) && h.Trim().Length >= 2 &&
                              haystack.Contains(h.ToLowerInvariant().Trim()));
    }

    private static async Task<bool> IsPortOpenAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port, ct).AsTask();
            var timeoutTask = Task.Delay(ScanTimeout, ct);
            var completed = await Task.WhenAny(connectTask, timeoutTask);
            return completed == connectTask && client.Connected;
        }
        catch { return false; }
    }

    // Enumerates every host address in this PC's own /24 (the common case for a store/
    // office LAN) from each active, non-loopback IPv4 network interface. Deliberately
    // narrow in scope (no multi-subnet or VLAN traversal) — broadening it is a deployment-
    // specific follow-up, not something to guess at here.
    private static List<string> GetLocalSubnetHosts()
    {
        var hosts = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var bytes = ua.Address.GetAddressBytes();
                    if (bytes[0] == 127) continue;

                    for (int i = 1; i <= 254; i++)
                    {
                        if (i == bytes[3]) continue; // skip this PC itself
                        hosts.Add($"{bytes[0]}.{bytes[1]}.{bytes[2]}.{i}");
                    }
                    return hosts.Distinct().ToList(); // first usable NIC is enough
                }
            }
        }
        catch { /* best-effort — an empty list just skips the network scan */ }

        return hosts;
    }
}
