using System.Diagnostics;
using System.Security.Principal;
using PrinterMode.Core.Enums;
using PrinterMode.Core.Interfaces;
using PrinterMode.Core.Models;

namespace PrinterMode.DriverManager;

public class DriverInstaller : IDriverInstaller
{
    private readonly IDriverRepository _repository;
    private readonly IWindowsPrinterService _printerService;
    private readonly ILogService _log;

    public DriverInstaller(IDriverRepository repository, IWindowsPrinterService printerService, ILogService log)
    {
        _repository = repository;
        _printerService = printerService;
        _log = log;
    }

    public async Task<InstallResult> InstallAsync(
        InstallRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var steps = new List<string>();

        if (!IsAdministrator())
        {
            return InstallResult.Fail(
                "Privilégios de administrador são necessários para instalar drivers.",
                "Execute o PrinterMode como Administrador.");
        }

        if (!_repository.DriverFilesExist(request.Driver))
        {
            return InstallResult.Fail(
                $"Arquivos do driver não encontrados para {request.Driver.DisplayName}.",
                $"Pasta esperada: {_repository.ResolveDriverPath(request.Driver)}");
        }

        try
        {
            // Step 1: Install driver — skip if already installed or prefer .exe, fallback to pnputil
            bool driverInstalled;

            if (request.SkipDriverInstall)
            {
                driverInstalled = true;
                steps.Add($"Driver já instalado: {request.Driver.DriverName}");
                _log.Info($"Skipping driver install (already installed): {request.Driver.DriverName}");
                progress?.Report("Driver já instalado, criando impressora...");
            }
            else if (request.Driver.HasInstaller)
            {
                var exePath = _repository.ResolveInstallerPath(request.Driver)!;
                progress?.Report($"Instalando driver ({request.Driver.InstallerExe})...");
                var exeResult = await RunExeInstallerAsync(exePath, request.Driver.InstallerArgs ?? "/S", ct, progress);

                if (!exeResult.success)
                {
                    // Fallback to pnputil if exe fails and inf exists
                    var infPath2 = _repository.ResolveInfPath(request.Driver);
                    if (File.Exists(infPath2))
                    {
                        _log.Warning($"EXE installer failed, trying pnputil: {infPath2}");
                        progress?.Report("Tentando via PnPUtil...");
                        var fallback = await RunPnpUtilAsync(infPath2, ct);
                        driverInstalled = fallback.success;
                        if (!driverInstalled)
                            return InstallResult.Fail("Falha ao instalar driver.", fallback.output, steps);
                        steps.Add($"Driver instalado via pnputil (fallback): {infPath2}");
                    }
                    else
                    {
                        return InstallResult.Fail("Falha ao instalar driver.", exeResult.output, steps);
                    }
                }
                else
                {
                    // Verify the driver actually landed in Windows after the installer ran
                    progress?.Report("Verificando instalação do driver...");
                    var drivers = await _printerService.GetInstalledDriversAsync(ct);
                    var found = drivers.Any(d =>
                        d.Contains(request.Driver.Manufacturer, StringComparison.OrdinalIgnoreCase) ||
                        d.Contains(request.Driver.Model, StringComparison.OrdinalIgnoreCase) ||
                        d.Equals(request.Driver.DriverName, StringComparison.OrdinalIgnoreCase));

                    if (!found)
                    {
                        _log.Warning($"Installer ran but driver '{request.Driver.DriverName}' not found in system.");
                        return InstallResult.Fail(
                            "O instalador foi executado, mas o driver não foi encontrado no Windows.",
                            "Conclua a instalação manualmente e tente novamente. O PrinterMode irá detectar que o driver já está instalado.",
                            steps);
                    }

                    driverInstalled = true;
                    steps.Add($"Driver instalado via instalador: {request.Driver.InstallerExe}");
                    _log.Info($"Driver installed via EXE: {exePath}");
                }
            }
            else
            {
                progress?.Report("Instalando driver via PnPUtil...");
                var infPath = _repository.ResolveInfPath(request.Driver);
                var pnpResult = await RunPnpUtilAsync(infPath, ct);

                if (!pnpResult.success)
                    return InstallResult.Fail("Falha ao instalar driver.", pnpResult.output, steps);

                driverInstalled = true;
                steps.Add($"Driver instalado via pnputil: {infPath}");
                _log.Info($"Driver installed via pnputil: {infPath}");
            }

            // Step 2: Create the port
            progress?.Report("Criando porta de impressão...");
            string portName;
            bool portCreated;

            switch (request.ConnectionType)
            {
                case ConnectionType.Network:
                    portName = request.PortName ?? $"IP_{request.IpAddress}";
                    portCreated = await _printerService.CreateTcpIpPortAsync(
                        portName, request.IpAddress!, request.NetworkPort, ct);
                    steps.Add($"Porta TCP/IP criada: {portName} → {request.IpAddress}:{request.NetworkPort}");
                    break;

                case ConnectionType.Serial:
                    portName = request.PortName ?? "COM1";
                    await ConfigureSerialPortAsync(portName, request.SerialConfig, ct);
                    portCreated = true;
                    steps.Add($"Porta serial configurada: {portName}");
                    break;

                case ConnectionType.USB:
                default:
                    portName = request.PortName ?? "USB001";
                    portCreated = true;
                    steps.Add($"Porta USB: {portName}");
                    break;
            }

            if (!portCreated)
                return InstallResult.Fail($"Falha ao criar porta {portName}.", null, steps);

            // Step 3: Add printer to Windows
            // Resolve actual installed driver name — catalog name may differ from what the
            // installer registered (e.g. "Gertec G250" vs "G-250 Thermal Printer")
            progress?.Report("Adicionando impressora ao Windows...");
            var installedDrivers = await _printerService.GetInstalledDriversAsync(ct);
            var resolvedDriverName = ResolveActualDriverName(request.Driver, installedDrivers);
            _log.Info($"Resolved driver name: '{resolvedDriverName}' (catalog: '{request.Driver.DriverName}')");

            var printerAdded = await _printerService.AddPrinterAsync(
                request.PrinterName, resolvedDriverName, portName, ct);

            if (!printerAdded)
                return InstallResult.Fail("Falha ao criar impressora no Windows.", null, steps);

            steps.Add($"Impressora criada: {request.PrinterName}");
            _log.Info($"Printer created: {request.PrinterName}");

            // Step 4: Configure paper
            progress?.Report("Configurando tamanho de papel...");
            await _printerService.SetPaperFormAsync(request.PrinterName, request.Paper, ct);
            steps.Add($"Papel configurado: {request.Paper.DisplayName}");

            // Step 5: Set as default if requested
            if (request.SetAsDefault)
            {
                await _printerService.SetDefaultPrinterAsync(request.PrinterName, ct);
                steps.Add("Definida como impressora padrão.");
            }

            progress?.Report("Instalação concluída com sucesso!");
            _log.Info($"Installation complete for {request.PrinterName}");

            return InstallResult.Ok(
                $"Impressora '{request.PrinterName}' instalada com sucesso!",
                request.PrinterName,
                steps);
        }
        catch (OperationCanceledException)
        {
            return InstallResult.Fail("Instalação cancelada pelo usuário.", null, steps);
        }
        catch (Exception ex)
        {
            _log.Error("Unexpected error during installation", ex);
            return InstallResult.Fail($"Erro inesperado: {ex.Message}", ex.ToString(), steps);
        }
    }

    public async Task<bool> UninstallAsync(string printerName, CancellationToken ct = default)
    {
        try
        {
            _log.Info($"Uninstalling printer: {printerName}");
            return await _printerService.DeletePrinterAsync(printerName, ct);
        }
        catch (Exception ex)
        {
            _log.Error($"Error uninstalling {printerName}", ex);
            return false;
        }
    }

    public async Task<bool> IsDriverInstalledAsync(string driverName, CancellationToken ct = default)
    {
        var drivers = await _printerService.GetInstalledDriversAsync(ct);
        return drivers.Any(d => d.Equals(driverName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<InstallResult> TestPrintAsync(string printerName, CancellationToken ct = default)
    {
        try
        {
            _log.Info($"Test print on: {printerName}");
            var ok = await _printerService.PrintTestPageAsync(printerName, ct);
            return ok
                ? InstallResult.Ok("Página de teste enviada.", printerName)
                : InstallResult.Fail("Falha ao imprimir página de teste.");
        }
        catch (Exception ex)
        {
            _log.Error("Test print error", ex);
            return InstallResult.Fail(ex.Message, ex.ToString());
        }
    }

    private async Task<(bool success, string output)> RunExeInstallerAsync(
        string exePath, string args, CancellationToken ct, IProgress<string>? progress = null)
    {
        // Try silent flags in order — stop at first success.
        // /S                              = NSIS (Nullsoft)
        // /VERYSILENT /SUPPRESSMSGBOXES   = Inno Setup
        // /silent                         = Epson APD and others
        // /q                              = MSI-wrapped installers
        var silentCandidates = new[] { args, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", "/silent", "/q" }
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct()
            .ToArray();

        foreach (var currentArgs in silentCandidates)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = currentArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _log.Info($"Trying EXE installer (silent) args: '{currentArgs}'");
                using var process = Process.Start(psi)!;
                await process.WaitForExitAsync(ct);
                _log.Info($"EXE exit {process.ExitCode} with args '{currentArgs}'");

                if (process.ExitCode == 0 || process.ExitCode == 3010 || process.ExitCode == 1641)
                    return (true, $"Instalador concluído silenciosamente (código {process.ExitCode}).");

                _log.Warning($"Silent flag '{currentArgs}' failed (exit {process.ExitCode}), trying next...");
            }
            catch (Exception ex)
            {
                _log.Warning($"EXE installer error with args '{currentArgs}': {ex.Message}");
            }
        }

        // All silent flags failed — open the installer with UI and wait for the user to finish.
        _log.Warning($"No silent flag worked. Opening installer with UI: {exePath}");
        progress?.Report("⚠ Conclua a instalação do driver na janela que abriu e clique em OK ao terminar...");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true
            };

            using var process = Process.Start(psi)!;
            await process.WaitForExitAsync(ct);
            _log.Info($"UI installer exit code: {process.ExitCode}");

            if (process.ExitCode == 0 || process.ExitCode == 3010 || process.ExitCode == 1641)
                return (true, "Instalação concluída pelo usuário.");

            return (false, $"Instalador retornou código {process.ExitCode}.");
        }
        catch (Exception ex)
        {
            _log.Error("Failed to run EXE installer with UI", ex);
            return (false, ex.Message);
        }
    }

    private string ResolveActualDriverName(DriverInfo driver, IReadOnlyList<string> installedDrivers)
    {
        // Exact match first
        if (installedDrivers.Any(d => d.Equals(driver.DriverName, StringComparison.OrdinalIgnoreCase)))
            return driver.DriverName;

        // Match by model name within the driver string
        var byModel = installedDrivers.FirstOrDefault(d =>
            d.Contains(driver.Model, StringComparison.OrdinalIgnoreCase));
        if (byModel != null) return byModel;

        // Match by manufacturer name
        var byManufacturer = installedDrivers.FirstOrDefault(d =>
            d.Contains(driver.Manufacturer, StringComparison.OrdinalIgnoreCase));
        if (byManufacturer != null) return byManufacturer;

        // Fallback to catalog name (will use whatever was registered)
        return driver.DriverName;
    }

    private async Task<(bool success, string output)> RunPnpUtilAsync(string infPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pnputil.exe",
            Arguments = $"/add-driver \"{infPath}\" /install",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(psi)!;
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            var error = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            _log.Info($"pnputil exit code: {process.ExitCode}");
            _log.Debug($"pnputil output: {output}");

            if (process.ExitCode != 0 && process.ExitCode != 3010) // 3010 = reboot required but driver installed
            {
                _log.Error($"pnputil error: {error}");
                return (false, error);
            }

            return (true, output);
        }
        catch (Exception ex)
        {
            _log.Error("Failed to run pnputil", ex);
            return (false, ex.Message);
        }
    }

    private async Task ConfigureSerialPortAsync(string portName, SerialConfig? config, CancellationToken ct)
    {
        if (config == null) return;

        var args = $"mode {portName}: BAUD={config.BaudRate} PARITY={config.Parity[0]} DATA={config.DataBits} STOP=1";
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {args}",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(psi)!;
            await process.WaitForExitAsync(ct);
            _log.Info($"Serial port configured: {portName} {config.DisplayName}");
        }
        catch (Exception ex)
        {
            _log.Warning($"Could not configure serial port {portName}: {ex.Message}");
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
