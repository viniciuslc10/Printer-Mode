using PrinterMode.Core.Models;

namespace PrinterMode.Core.Interfaces;

public interface IDriverRepository
{
    Task<DriverCatalog> LoadCatalogAsync();
    Task<IReadOnlyList<DriverInfo>> GetAllDriversAsync();
    Task<IReadOnlyList<string>> GetManufacturersAsync();
    Task<IReadOnlyList<DriverInfo>> GetDriversByManufacturerAsync(string manufacturer);
    Task<DriverInfo?> FindByVidPidAsync(string vendorId, string productId);
    Task<DriverInfo?> GetByIdAsync(string id);
    string ResolveDriverPath(DriverInfo driver);
    string ResolveInfPath(DriverInfo driver);
    bool DriverFilesExist(DriverInfo driver);
    string? ResolveInstallerPath(DriverInfo driver);
}
