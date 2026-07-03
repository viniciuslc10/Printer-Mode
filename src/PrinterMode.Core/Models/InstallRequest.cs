using PrinterMode.Core.Enums;

namespace PrinterMode.Core.Models;

public class InstallRequest
{
    public DriverInfo Driver { get; set; } = null!;
    public PrinterDevice? Device { get; set; }
    public string PrinterName { get; set; } = string.Empty;
    public PaperConfig Paper { get; set; } = null!;
    public ConnectionType ConnectionType { get; set; }
    public string? PortName { get; set; }
    public string? IpAddress { get; set; }
    public int NetworkPort { get; set; } = 9100;
    public SerialConfig? SerialConfig { get; set; }
    public bool SetAsDefault { get; set; } = false;
    public bool SkipDriverInstall { get; set; } = false;
    public string? SharedHost { get; set; }
    // The share name as configured on the host (may differ from PrinterName)
    public string? SharedPrinterName { get; set; }
    // Windows driver name as reported by the remote PC via discovery port
    public string? SharedDriverName { get; set; }
}
