using System.Text.Json;
using System.Text.Json.Serialization;
using PrinterMode.Core.Interfaces;
using PrinterMode.Core.Models;

namespace PrinterMode.DriverManager;

public class DriverRepository : IDriverRepository
{
    private readonly string _repositoryRoot;
    private readonly ILogService _log;
    private DriverCatalog? _catalog;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public DriverRepository(string repositoryRoot, ILogService log)
    {
        _repositoryRoot = repositoryRoot;
        _log = log;
    }

    public async Task<DriverCatalog> LoadCatalogAsync()
    {
        if (_catalog != null) return _catalog;

        var catalogPath = Path.Combine(_repositoryRoot, "drivers.json");
        if (!File.Exists(catalogPath))
        {
            _log.Warning($"Catalog not found at {catalogPath}. Creating empty catalog.");
            _catalog = new DriverCatalog();
            return _catalog;
        }

        try
        {
            var json = await File.ReadAllTextAsync(catalogPath);
            _catalog = JsonSerializer.Deserialize<DriverCatalog>(json, JsonOptions)
                       ?? new DriverCatalog();

            _log.Info($"Catalog loaded: {_catalog.Drivers.Count} driver(s) from {catalogPath}");
        }
        catch (Exception ex)
        {
            _log.Error("Failed to load driver catalog", ex);
            _catalog = new DriverCatalog();
        }

        return _catalog;
    }

    public async Task<IReadOnlyList<DriverInfo>> GetAllDriversAsync()
    {
        var catalog = await LoadCatalogAsync();
        return catalog.Drivers.AsReadOnly();
    }

    public async Task<IReadOnlyList<string>> GetManufacturersAsync()
    {
        var drivers = await GetAllDriversAsync();
        return drivers.Select(d => d.Manufacturer)
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .OrderBy(m => m)
                      .ToList();
    }

    public async Task<IReadOnlyList<DriverInfo>> GetDriversByManufacturerAsync(string manufacturer)
    {
        var drivers = await GetAllDriversAsync();
        return drivers.Where(d => d.Manufacturer.Equals(manufacturer, StringComparison.OrdinalIgnoreCase))
                      .ToList();
    }

    public async Task<DriverInfo?> FindByVidPidAsync(string vendorId, string productId)
    {
        var drivers = await GetAllDriversAsync();
        return drivers.FirstOrDefault(d =>
            d.VendorId?.Equals(vendorId, StringComparison.OrdinalIgnoreCase) == true &&
            d.ProductId?.Equals(productId, StringComparison.OrdinalIgnoreCase) == true);
    }

    public async Task<DriverInfo?> GetByIdAsync(string id)
    {
        var drivers = await GetAllDriversAsync();
        return drivers.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public string ResolveDriverPath(DriverInfo driver)
        => Path.Combine(_repositoryRoot, driver.DriverFolder);

    public string ResolveInfPath(DriverInfo driver)
        => Path.Combine(_repositoryRoot, driver.DriverFolder, driver.InfFile);

    public bool DriverFilesExist(DriverInfo driver)
    {
        var infPath = ResolveInfPath(driver);
        return File.Exists(infPath);
    }

    public void InvalidateCache() => _catalog = null;
}
