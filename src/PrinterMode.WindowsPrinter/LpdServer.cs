using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using PrinterMode.Core.Interfaces;

namespace PrinterMode.WindowsPrinter;

// Minimal RFC 1179 LPD server that runs inside the app on port 515.
// Receives raw print jobs from LPR clients (Windows LPR ports on PC-B)
// and forwards them to the matching local Windows printer via RAW printing.
// No Windows optional features or reboots required.
internal sealed class LpdServer
{
    private readonly ILogService _log;

    public LpdServer(ILogService log) => _log = log;

    public void Start()
    {
        // Run netsh firewall commands on a background thread so they don't block the
        // caller (which is the UI startup thread). The listener is started immediately;
        // connections already allowed by an existing rule work while the rule is updated.
        _ = Task.Run(OpenFirewallPort);
        _ = Task.Run(ListenAsync);
        _log.Info("PrinterMode internal LPD server started on port 515.");
    }

    private static void OpenFirewallPort()
    {
        try
        {
            Run("netsh", "advfirewall firewall delete rule name=\"PrinterMode LPD\"");
            Run("netsh",
                "advfirewall firewall add rule name=\"PrinterMode LPD\" " +
                "dir=in action=allow protocol=tcp localport=515 profile=any");
        }
        catch { }
    }

    private async Task ListenAsync()
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Any, 515);
            listener.Start();

            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = HandleClientAsync(client);
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"LPD server listen error: {ex.Message}");
        }
        finally
        {
            listener?.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        client.ReceiveTimeout = 15_000;
        client.SendTimeout = 10_000;

        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var header = await ReadLineAsync(stream);
                if (header == null || header.Length == 0) return;

                byte cmd = (byte)header[0];
                string queue = header.Length > 1 ? header[1..].Trim() : "";

                switch (cmd)
                {
                    case 0x02: // Receive a printer job
                        await ReceiveJobAsync(stream, queue);
                        break;
                    case 0x03: // Send queue state (short)
                    case 0x04: // Send queue state (long)
                        await SendQueueStateAsync(stream, queue);
                        break;
                    default:   // Other commands: ACK and ignore
                        await stream.WriteAsync(new byte[] { 0x00 });
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Info($"LPD client handler (non-fatal): {ex.Message}");
        }
    }

    private async Task ReceiveJobAsync(NetworkStream stream, string queueName)
    {
        // ACK the receive-job command
        await stream.WriteAsync(new byte[] { 0x00 });

        byte[]? printData = null;

        while (true)
        {
            var line = await ReadLineAsync(stream);
            if (line == null || line.Length == 0) break;

            byte subCmd = (byte)line[0];
            // Format: \xNN count filename
            var parts = line[1..].Trim().Split(' ', 2);
            if (!int.TryParse(parts[0], out int count) || count <= 0) break;

            // ACK the subcommand
            await stream.WriteAsync(new byte[] { 0x00 });

            // Read exactly 'count' bytes
            var data = await ReadExactAsync(stream, count);

            // Read the terminating null byte sent by the client
            var term = new byte[1];
            try { await stream.ReadAsync(term); } catch { }

            if (subCmd == 0x03) // Data file (\x03) = actual print content
                printData = data;

            // ACK receipt of file
            await stream.WriteAsync(new byte[] { 0x00 });
        }

        if (printData != null && printData.Length > 0)
        {
            var printerName = ResolvePrinterByQueue(queueName);
            if (printerName != null)
            {
                bool ok = RawPrint.Send(printerName, printData);
                _log.Info($"LPD job: queue='{queueName}' → '{printerName}' {printData.Length}B ok={ok}");
            }
            else
            {
                _log.Warning($"LPD job: queue '{queueName}' has no matching printer.");
            }
        }
    }

    private async Task SendQueueStateAsync(NetworkStream stream, string queue)
    {
        var sb = new StringBuilder();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, ShareName FROM Win32_Printer WHERE Shared = True");
            foreach (ManagementObject p in searcher.Get())
            {
                var share = p["ShareName"]?.ToString() ?? "";
                var name  = p["Name"]?.ToString() ?? share;
                if (string.IsNullOrWhiteSpace(queue) ||
                    queue.Equals(share, StringComparison.OrdinalIgnoreCase) ||
                    queue.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"{name} ({share}): Ready, no jobs.");
                }
            }
        }
        catch { }

        if (sb.Length == 0) sb.AppendLine("No printer queues.");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
    }

    private string? ResolvePrinterByQueue(string queueName)
    {
        try
        {
            // 1. Match by Windows share name
            using var s1 = new ManagementObjectSearcher(
                $"SELECT Name FROM Win32_Printer WHERE ShareName='{EscWmi(queueName)}'");
            foreach (ManagementObject p in s1.Get())
                return p["Name"]?.ToString();

            // 2. Match by printer display name
            using var s2 = new ManagementObjectSearcher(
                $"SELECT Name FROM Win32_Printer WHERE Name='{EscWmi(queueName)}'");
            foreach (ManagementObject p in s2.Get())
                return p["Name"]?.ToString();

            // 3. First shared printer (single-printer scenario)
            using var s3 = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_Printer WHERE Shared=True");
            foreach (ManagementObject p in s3.Get())
                return p["Name"]?.ToString();

            // 4. Any non-system printer
            using var s4 = new ManagementObjectSearcher("SELECT Name FROM Win32_Printer");
            foreach (ManagementObject p in s4.Get())
            {
                var n = p["Name"]?.ToString() ?? "";
                if (!n.Contains("Microsoft") && !n.Contains("OneNote") &&
                    !n.Contains("Fax") && !n.Contains("XPS") && !n.Contains("PDF"))
                    return n;
            }

            return null;
        }
        catch (Exception ex)
        {
            _log.Warning($"ResolvePrinterByQueue failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
    {
        var buf = new byte[count];
        int received = 0;
        while (received < count)
        {
            int read = await stream.ReadAsync(buf.AsMemory(received, count - received));
            if (read == 0) break;
            received += read;
        }
        return buf;
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream)
    {
        var bytes = new List<byte>(128);
        var single = new byte[1];

        while (true)
        {
            int n = await stream.ReadAsync(single);
            if (n == 0) break;
            if (single[0] == 0x0A) break; // LF = end of line
            if (single[0] != 0x0D)         // skip CR
                bytes.Add(single[0]);
        }

        return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static string EscWmi(string v) => v.Replace("'", "\\'").Replace("\\", "\\\\");

    private static void Run(string exe, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
        { UseShellExecute = false, CreateNoWindow = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(8_000);
    }
}

// Raw P/Invoke printing — sends byte[] directly to a Windows printer spooler.
internal static class RawPrint
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DOCINFO
    {
        public string pDocName;
        public string? pOutputFile;
        public string pDataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern int StartDocPrinter(IntPtr h, int level, ref DOCINFO info);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr h);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr h, byte[] buf, int len, out int written);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr h);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr h);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr h);

    public static bool Send(string printerName, byte[] data)
    {
        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero)) return false;
        try
        {
            var doc = new DOCINFO { pDocName = "LPD Job", pDataType = "RAW" };
            if (StartDocPrinter(handle, 1, ref doc) <= 0) return false;
            StartPagePrinter(handle);
            WritePrinter(handle, data, data.Length, out _);
            EndPagePrinter(handle);
            EndDocPrinter(handle);
            return true;
        }
        finally
        {
            ClosePrinter(handle);
        }
    }
}
