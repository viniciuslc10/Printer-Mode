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
            // Step 1: Install driver
            // Strategy (in order):
            //   1. pnputil /add-driver <inf> /install  — always silent, works for most drivers
            //   2. EXE installer with silent flags      — for drivers that need the EXE
            //   3. EXE installer with UI visible        — last resort when silent flags are ignored
            bool driverInstalled;

            if (request.SkipDriverInstall)
            {
                driverInstalled = true;
                steps.Add($"Driver já instalado: {request.Driver.DriverName}");
                _log.Info($"Skipping driver install (already installed): {request.Driver.DriverName}");
                progress?.Report("Driver já instalado, criando impressora...");
            }
            else
            {
                driverInstalled = false;

                // ── 1. Try pnputil first (always silent, no wizard) ──────────────────────
                var infPath = _repository.ResolveInfPath(request.Driver);
                if (File.Exists(infPath))
                {
                    progress?.Report("Instalando driver silenciosamente...");
                    var pnpResult = await RunPnpUtilAsync(infPath, ct);

                    if (pnpResult.success)
                    {
                        await Task.Delay(1500, ct);
                        var driversAfterPnp = await _printerService.GetInstalledDriversAsync(ct);
                        var resolvedPnp = ResolveActualDriverName(request.Driver, driversAfterPnp);

                        if (resolvedPnp != null)
                        {
                            driverInstalled = true;
                            steps.Add($"Driver instalado via pnputil: {infPath}");
                            _log.Info($"Driver installed silently via pnputil. Name: '{resolvedPnp}'");
                        }
                        else
                        {
                            _log.Warning("pnputil succeeded but driver name not found in Win32_PrinterDriver. Will try EXE.");
                        }
                    }
                    else
                    {
                        _log.Warning($"pnputil failed ({pnpResult.output}). Will try EXE installer.");
                    }
                }

                // ── 2. Try EXE installer (silent flags) if pnputil didn't work ───────────
                if (!driverInstalled && request.Driver.HasInstaller)
                {
                    var exePath = _repository.ResolveInstallerPath(request.Driver)!;
                    progress?.Report($"Instalando driver ({request.Driver.InstallerExe})...");
                    var exeResult = await RunExeInstallerAsync(exePath, request.Driver.InstallerArgs ?? "/S", ct, progress);

                    if (exeResult.success)
                    {
                        await Task.Delay(3000, ct);
                        var driversAfterExe = await _printerService.GetInstalledDriversAsync(ct);
                        var resolvedExe = ResolveActualDriverName(request.Driver, driversAfterExe);

                        if (resolvedExe != null)
                        {
                            driverInstalled = true;
                            steps.Add($"Driver instalado via instalador: {request.Driver.InstallerExe}");
                            _log.Info($"Driver installed via EXE. Name: '{resolvedExe}'");
                        }
                        else
                        {
                            // ── 3. EXE ran but driver not found → open with UI as last resort ──
                            _log.Warning("EXE silent install succeeded but driver not found. Opening UI...");
                            progress?.Report("⚠ Conclua a instalação do driver na janela que abriu...");

                            try
                            {
                                using var uiProc = Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true })!;
                                await uiProc.WaitForExitAsync(ct);
                                _log.Info($"UI installer exit: {uiProc.ExitCode}");
                            }
                            catch (Exception ex)
                            {
                                _log.Warning($"UI installer failed to launch: {ex.Message}");
                            }

                            progress?.Report("Verificando driver após instalação...");
                            await Task.Delay(3000, ct);

                            var driversAfterUi = await _printerService.GetInstalledDriversAsync(ct);
                            var resolvedUi = ResolveActualDriverName(request.Driver, driversAfterUi);

                            if (resolvedUi == null)
                            {
                                var list = string.Join(", ", driversAfterUi.Take(10));
                                _log.Error($"Driver not found after UI install. Installed: [{list}]");
                                return InstallResult.Fail(
                                    "O instalador foi executado, mas o driver não foi encontrado no Windows.",
                                    $"Drivers instalados: {list}",
                                    steps);
                            }

                            driverInstalled = true;
                            steps.Add($"Driver instalado via instalador (UI): {request.Driver.InstallerExe}");
                            _log.Info($"Driver installed via UI. Name: '{resolvedUi}'");
                        }
                    }
                    else
                    {
                        return InstallResult.Fail("Falha ao instalar driver.", exeResult.output, steps);
                    }
                }

                if (!driverInstalled)
                    return InstallResult.Fail("Nenhum arquivo de driver encontrado (INF ou EXE).", null, steps);
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
                    // Find the actual USB port Windows registered for this printer.
                    // Windows creates USB001/USB002/… only when the device is detected.
                    var usbPort = await _printerService.FindBestUsbPortAsync(ct);
                    portName = request.PortName ?? usbPort ?? "USB001";
                    portCreated = true;
                    steps.Add($"Porta USB: {portName}");
                    _log.Info($"USB port selected: {portName} (discovered: {usbPort ?? "none"})");
                    break;
            }

            if (!portCreated)
                return InstallResult.Fail($"Falha ao criar porta {portName}.", null, steps);

            // Step 3: Add printer to Windows
            progress?.Report("Adicionando impressora ao Windows...");
            var installedDrivers = await _printerService.GetInstalledDriversAsync(ct);
            var resolvedDriverName = ResolveActualDriverName(request.Driver, installedDrivers);

            if (resolvedDriverName == null)
            {
                var list = string.Join(", ", installedDrivers.Take(10));
                _log.Error($"Cannot resolve driver name. Installed drivers: [{list}]");
                return InstallResult.Fail(
                    "Driver não encontrado no Windows para criar a impressora.",
                    $"Drivers instalados: {list}",
                    steps);
            }

            _log.Info($"Resolved driver name: '{resolvedDriverName}' (catalog: '{request.Driver.DriverName}')");

            var printerAdded = await _printerService.AddPrinterAsync(
                request.PrinterName, resolvedDriverName, portName, ct);

            if (!printerAdded)
            {
                _log.Error($"AddPrinterAsync failed. driver='{resolvedDriverName}' port='{portName}'");
                return InstallResult.Fail(
                    $"Falha ao criar impressora no Windows (driver: '{resolvedDriverName}').",
                    "Verifique se o driver está instalado e tente novamente.",
                    steps);
            }

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

    public async Task<bool> IsDriverInstalledAsync(DriverInfo driver, CancellationToken ct = default)
    {
        var installed = await _printerService.GetInstalledDriversAsync(ct);
        _log.Info($"Installed drivers: {string.Join(", ", installed)}");
        // Match against every known name (primary driverName + WindowsDriverNames aliases)
        return installed.Any(d =>
            driver.AllDriverNames().Any(known =>
                d.Equals(known, StringComparison.OrdinalIgnoreCase)));
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

    private string? ResolveActualDriverName(DriverInfo driver, IReadOnlyList<string> installedDrivers)
    {
        // 1. Exact match against every known driver name (primary + aliases)
        foreach (var known in driver.AllDriverNames())
        {
            var exact = installedDrivers.FirstOrDefault(d =>
                d.Equals(known, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }

        // 2. Any installed driver whose name contains the model string
        var byModel = installedDrivers.FirstOrDefault(d =>
            d.Contains(driver.Model, StringComparison.OrdinalIgnoreCase));
        if (byModel != null) return byModel;

        // 3. Any installed driver whose name contains the manufacturer string
        var byManufacturer = installedDrivers.FirstOrDefault(d =>
            d.Contains(driver.Manufacturer, StringComparison.OrdinalIgnoreCase));
        if (byManufacturer != null) return byManufacturer;

        // Not found — caller decides what to do
        return null;
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
