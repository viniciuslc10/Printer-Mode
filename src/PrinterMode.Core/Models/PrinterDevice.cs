using PrinterMode.Core.Enums;

namespace PrinterMode.Core.Models;

public class PrinterDevice
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? VendorId { get; set; }
    public string? ProductId { get; set; }
    public ConnectionType ConnectionType { get; set; }
    public PrinterStatus Status { get; set; }
    public DriverStatus DriverStatus { get; set; }
    public string? PortName { get; set; }
    public string? IpAddress { get; set; }
    public int NetworkPort { get; set; } = 9100;
    public string? DevicePath { get; set; }
    public string? InstalledDriverName { get; set; }
    public DateTime DetectedAt { get; set; } = DateTime.Now;

    public string StatusDescription => Status switch
    {
        PrinterStatus.Connected => "Conectada",
        PrinterStatus.Disconnected => "Desconectada",
        PrinterStatus.NotFound => "Não encontrada",
        PrinterStatus.Error => "Erro",
        _ => "Desconhecido"
    };

    public string DriverStatusDescription => DriverStatus switch
    {
        DriverStatus.NotInstalled => "Driver não instalado",
        DriverStatus.Installed => "Driver instalado",
        DriverStatus.Installing => "Instalando...",
        DriverStatus.Error => "Erro no driver",
        _ => "Desconhecido"
    };

    public string ConnectionDescription => ConnectionType switch
    {
        ConnectionType.USB => $"USB ({PortName})",
        ConnectionType.Serial => $"Serial ({PortName})",
        ConnectionType.Network => $"TCP/IP ({IpAddress}:{NetworkPort})",
        ConnectionType.Shared => $"Compartilhada ({DisplayName})",
        _ => "Desconhecida"
    };
}
