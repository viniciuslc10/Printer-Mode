using System.Text.Json.Serialization;

namespace PrinterMode.Core.Models;

public class DriverInfo
{
    public string Id { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("vid")]
    public string? VendorId { get; set; }

    [JsonPropertyName("pid")]
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

    // "innosetup" | "winrar-sfx" | "epson-apd" | "ui-only" | "manual" — controls silent install strategy
    // "manual": no bundled installer; user must download from DownloadUrl
    public string? InstallerType { get; set; }

    // For "winrar-sfx": filename of the silent setup binary inside the extracted archive
    public string? SilentSetupExe { get; set; }

    // URL to download the driver when InstallerType == "manual"
    public string? DownloadUrl { get; set; }

    // All driver names the installer may register in Win32_PrinterDriver.
    // The first entry is the preferred name; extras are fallback aliases.
    public List<string> WindowsDriverNames { get; set; } = [];

    public bool HasInstaller => !string.IsNullOrEmpty(InstallerExe);
    public string DisplayName => $"{Manufacturer} {Model}";

    /// <summary>Returns all names to try when matching or creating the printer driver.</summary>
    public IEnumerable<string> AllDriverNames()
    {
        yield return DriverName;
        foreach (var n in WindowsDriverNames)
            if (!string.Equals(n, DriverName, StringComparison.OrdinalIgnoreCase))
                yield return n;
    }
}
