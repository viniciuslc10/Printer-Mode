using PrinterMode.Core.Enums;

namespace PrinterMode.Core.Models;

/// <summary>
/// One candidate way to reach the printer the user selected in the catalog, found by
/// IAutoDetectService without the user choosing a connection type or typing a port/IP.
/// Carries exactly the fields InstallRequest needs, so the caller can populate a request
/// directly from the chosen target with no further lookup.
/// </summary>
public class InstallTarget
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required ConnectionType ConnectionType { get; init; }
    public string? PortName { get; init; }
    public string? IpAddress { get; init; }
    public int NetworkPort { get; init; } = 9100;
    public string? SharedHost { get; init; }
    public string? SharedPrinterName { get; init; }
    public string? SharedDriverName { get; init; }
    public string? SharedDisplayName { get; init; }
}
