namespace PrinterMode.Core.Models;

/// <summary>
/// A USB/PnP device the app resolved as the most likely physical printer among all
/// connected devices — independent of any hardcoded VID/PID in the catalog (which may
/// be a template placeholder). Vid/Pid here are the REAL ids parsed from the device's
/// InstanceId; Port is the device's bound COM port when it enumerates as virtual-serial.
/// </summary>
public record DetectedUsbDevice(
    string InstanceId,
    string? Vid,
    string? Pid,
    string? Port,
    string FriendlyName,
    string Status,
    string DeviceClass,
    string BusName = "")
{
    /// <summary>Best human name: the driver friendly name, or the USB bus-reported product
    /// string when the device is undriven (shows in "Unspecified" with an empty FriendlyName).</summary>
    public string BestName =>
        !string.IsNullOrWhiteSpace(FriendlyName) ? FriendlyName :
        !string.IsNullOrWhiteSpace(BusName) ? BusName : "(sem nome)";
}
