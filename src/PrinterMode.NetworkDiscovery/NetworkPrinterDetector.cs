using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PrinterMode.Core.Enums;
using PrinterMode.Core.Interfaces;
using PrinterMode.Core.Models;

namespace PrinterMode.NetworkDiscovery;

public class NetworkPrinterDetector
{
    private readonly ILogService _log;
    private static readonly int[] CommonPrinterPorts = [9100, 515, 631];

    public NetworkPrinterDetector(ILogService log) => _log = log;

    public Task<IReadOnlyList<PrinterDevice>> DetectAsync(CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            var devices = new List<PrinterDevice>();

            // Detect shared network printers from Windows
            await DetectSharedPrintersAsync(devices, ct);

            // Detect TCP/IP configured printers
            await DetectTcpIpPrintersAsync(devices, ct);

            return (IReadOnlyList<PrinterDevice>)devices;
        }, ct);
    }

    private async Task DetectSharedPrintersAsync(List<PrinterDevice> devices, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"SELECT * FROM Win32_Printer WHERE Network=True");

                foreach (ManagementObject obj in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();

                    var name = obj["Name"]?.ToString() ?? "Network Printer";
                    var portName = obj["PortName"]?.ToString();
                    var shareName = obj["ShareName"]?.ToString();

                    devices.Add(new PrinterDevice
                    {
                        Id = $"NET_{name}",
                        DisplayName = name,
                        ConnectionType = ConnectionType.Shared,
                        Status = PrinterStatus.Connected,
                        DriverStatus = DriverStatus.Installed,
                        PortName = portName
                    });

                    _log.Info($"Shared printer detected: {name}");
                }
            }
            catch (Exception ex)
            {
                _log.Error("Error detecting shared printers", ex);
            }
        }, ct);
    }

    private async Task DetectTcpIpPrintersAsync(List<PrinterDevice> devices, CancellationToken ct)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"SELECT * FROM Win32_TCPIPPrinterPort");

            var ports = new List<(string name, string host, int port)>();

            await Task.Run(() =>
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    var portName = obj["Name"]?.ToString() ?? string.Empty;
                    var hostAddress = obj["HostAddress"]?.ToString() ?? string.Empty;
                    var portNumber = Convert.ToInt32(obj["PortNumber"] ?? 9100);
                    ports.Add((portName, hostAddress, portNumber));
                }
            }, ct);

            var tasks = ports.Select(async p =>
            {
                ct.ThrowIfCancellationRequested();
                var reachable = await PingHostAsync(p.host, ct);

                devices.Add(new PrinterDevice
                {
                    Id = $"TCPIP_{p.host}_{p.port}",
                    DisplayName = $"TCP/IP Printer ({p.host}:{p.port})",
                    ConnectionType = ConnectionType.Network,
                    Status = reachable ? PrinterStatus.Connected : PrinterStatus.Disconnected,
                    DriverStatus = DriverStatus.Unknown,
                    PortName = p.name,
                    IpAddress = p.host,
                    NetworkPort = p.port
                });

                _log.Info($"TCP/IP printer: {p.host}:{p.port} - {(reachable ? "reachable" : "unreachable")}");
            });

            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Error("Error detecting TCP/IP printers", ex);
        }
    }

    public async Task<bool> PingHostAsync(string host, CancellationToken ct = default)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 1000);
            return reply.Status == IPStatus.Success;
        }
        catch { return false; }
    }

    public async Task<bool> TestPrinterPortAsync(string host, int port, CancellationToken ct = default)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port, ct).AsTask();
            var timeoutTask = Task.Delay(2000, ct);
            var completed = await Task.WhenAny(connectTask, timeoutTask);
            return completed == connectTask && !connectTask.IsFaulted;
        }
        catch { return false; }
    }
}
