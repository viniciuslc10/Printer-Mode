namespace PrinterMode.Core.Models;

public class DriverCatalog
{
    public string Version { get; set; } = "1.0";
    public DateTime UpdatedAt { get; set; }
    public List<DriverInfo> Drivers { get; set; } = [];
}
