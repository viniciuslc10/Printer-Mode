namespace PrinterMode.Core.Models;

public class DriverInfo
{
    public string Id { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? VendorId { get; set; }
    public string? ProductId { get; set; }
    public string DriverFolder { get; set; } = string.Empty;
    public string InfFile { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string? Version { get; set; }
    public PaperConfig DefaultPaper { get; set; } = new();
    public List<PaperConfig> SupportedPapers { get; set; } = [];
    public SerialConfig? DefaultSerial { get; set; }
    public List<string> SupportedPorts { get; set; } = [];
    public string? Notes { get; set; }

    // Optional .exe installer (preferred over pnputil when present)
    public string? InstallerExe { get; set; }
    public string? InstallerArgs { get; set; }

    public bool HasInstaller => !string.IsNullOrEmpty(InstallerExe);
    public string DisplayName => $"{Manufacturer} {Model}";
}
