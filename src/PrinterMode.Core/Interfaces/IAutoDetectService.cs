using PrinterMode.Core.Models;

namespace PrinterMode.Core.Interfaces;

/// <summary>
/// Finds how the selected printer model is actually reachable right now — connected
/// locally (USB/COM) or available on the network (shared via another PrinterMode instance,
/// or a network-direct printer with its own IP) — so the user never has to pick a
/// connection type or type a port/IP by hand. Returns every plausible candidate: the
/// caller decides what to do with 0 (fall back to manual), 1 (proceed) or many (ask the
/// user which one) results.
/// </summary>
public interface IAutoDetectService
{
    Task<IReadOnlyList<InstallTarget>> DetectAsync(DriverInfo driver, CancellationToken ct = default);
}
