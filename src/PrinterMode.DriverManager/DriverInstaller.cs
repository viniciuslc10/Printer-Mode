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
            // installerType controls the strategy:
            //   "innosetup" / "epson-apd" / default → single silent EXE run using installerArgs
            //   "winrar-sfx"  → extract SFX to temp, run Silent_Setup.exe inside
            //   "ui-only"     → open installer with UI (Daruma and similar proprietary setups)
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
                var exePath = _repository.ResolveInstallerPath(request.Driver);
                var installerType = request.Driver.InstallerType ?? "exe";

                if (!request.Driver.HasInstaller || exePath == null)
                    return InstallResult.Fail("Nenhum instalador encontrado para este driver.", null, steps);

                bool needsUiInstall = installerType == "ui-only";

                // ── WinRAR SFX: extract to temp, kill auto-launched setup, run Silent_Setup.exe ──
                if (installerType == "winrar-sfx" && !string.IsNullOrEmpty(request.Driver.SilentSetupExe))
                {
                    var sfxResult = await InstallWinRarSfxAsync(exePath, request.Driver.SilentSetupExe!, ct, progress);
                    if (sfxResult.success)
                    {
                        await Task.Delay(2000, ct);
                        var driversAfterSfx = await _printerService.GetInstalledDriversAsync(ct);
                        var resolvedSfx = ResolveActualDriverName(request.Driver, driversAfterSfx);
                        if (resolvedSfx != null)
                        {
                            driverInstalled = true;
                            steps.Add($"Driver instalado via WinRAR SFX: {request.Driver.InstallerExe}");
                            _log.Info($"Driver installed via WinRAR SFX. Name: '{resolvedSfx}'");
                        }
                        else
                        {
                            _log.Warning("WinRAR SFX ran but driver not found, falling back to UI install.");
                            needsUiInstall = true;
                        }
                    }
                    else
                    {
                        _log.Warning($"WinRAR SFX extraction failed ({sfxResult.output}), falling back to UI install.");
                        needsUiInstall = true;
                    }
                }
                // ── Silent EXE (innosetup / epson-apd / default) ──────────────────────────
                else if (installerType != "ui-only" && installerType != "winrar-sfx")
                {
                    var silentArgs = request.Driver.InstallerArgs;
                    if (string.IsNullOrEmpty(silentArgs))
                        silentArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";

                    progress?.Report("Instalando driver silenciosamente...");
                    _log.Info($"Running silent EXE installer: '{exePath}' args='{silentArgs}'");

                    var psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = silentArgs,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    try
                    {
                        using var proc = Process.Start(psi)!;
                        await proc.WaitForExitAsync(ct);
                        _log.Info($"Silent installer exit: {proc.ExitCode}");

                        if (proc.ExitCode != 0 && proc.ExitCode != 3010 && proc.ExitCode != 1641)
                        {
                            return InstallResult.Fail(
                                $"Instalador retornou código de erro {proc.ExitCode}.",
                                $"Tente instalar manualmente: {request.Driver.InstallerExe}", steps);
                        }
                    }
                    catch (Exception ex)
                    {
                        return InstallResult.Fail($"Falha ao executar instalador: {ex.Message}", null, steps);
                    }

                    await Task.Delay(2500, ct);
                    var driversAfterSilent = await _printerService.GetInstalledDriversAsync(ct);
                    var resolvedSilent = ResolveActualDriverName(request.Driver, driversAfterSilent);
                    if (resolvedSilent != null)
                    {
                        driverInstalled = true;
                        steps.Add($"Driver instalado silenciosamente: {request.Driver.InstallerExe}");
                        _log.Info($"Driver installed silently. Name: '{resolvedSilent}'");
                    }
                    else
                    {
                        var list = string.Join(", ", driversAfterSilent.Take(10));
                        _log.Error($"Silent install exited 0 but driver not found. Installed: [{list}]");
                        return InstallResult.Fail(
                            "O instalador foi executado, mas o driver não foi encontrado no Windows.",
                            $"Drivers instalados: {list}", steps);
                    }
                }

                // ── UI installer: open with wizard, wait for user to complete ─────────────
                if (!driverInstalled && needsUiInstall)
                {
                    progress?.Report($"⚠ Siga as instruções do instalador {request.Driver.DisplayName} na janela que abrir...");
                    _log.Info($"Opening UI installer for {request.Driver.DisplayName}: {exePath}");

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
                        return InstallResult.Fail(
                            "O instalador foi executado, mas o driver não foi encontrado no Windows.",
                            $"Drivers instalados: {list}", steps);
                    }

                    driverInstalled = true;
                    steps.Add($"Driver instalado via assistente visual: {request.Driver.InstallerExe}");
                    _log.Info($"Driver installed via UI. Name: '{resolvedUi}'");
                }

                if (!driverInstalled)
                    return InstallResult.Fail("Não foi possível instalar o driver.", null, steps);
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

    private async Task<(bool success, string output)> InstallWinRarSfxAsync(
        string sfxPath, string silentSetupExe, CancellationToken ct, IProgress<string>? progress = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"PM_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            progress?.Report("Extraindo pacote do driver...");

            // WinRAR SFX: -y auto-confirm, -d<path> extract destination, -s suppress dialogs
            var extractPsi = new ProcessStartInfo
            {
                FileName = sfxPath,
                Arguments = $"-y -s -d\"{tempDir}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var extractProc = Process.Start(extractPsi)!;

            // Wait up to 30s for extraction; the SFX may also auto-launch the interactive Setup.exe
            var completed = await Task.Run(() => extractProc.WaitForExit(30_000), ct);
            if (!completed)
                extractProc.Kill();

            // Kill any interactive Setup.exe spawned from within the extracted temp dir
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var mainModulePath = proc.MainModule?.FileName;
                    if (mainModulePath?.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase) == true &&
                        !mainModulePath.Contains("Silent_Setup", StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Info($"Killing auto-launched interactive setup: {mainModulePath}");
                        proc.Kill();
                    }
                }
                catch { }
            }

            // Find and run Silent_Setup.exe in the extracted folder
            var silentExePath = Directory
                .GetFiles(tempDir, silentSetupExe, SearchOption.AllDirectories)
                .FirstOrDefault();

            if (silentExePath == null)
            {
                _log.Warning($"'{silentSetupExe}' not found in extracted SFX at '{tempDir}'");
                return (false, $"{silentSetupExe} não encontrado após extração");
            }

            progress?.Report("Instalando driver silenciosamente...");
            _log.Info($"Running WinRAR SFX silent setup: '{silentExePath}'");

            var silentPsi = new ProcessStartInfo
            {
                FileName = silentExePath,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var silentProc = Process.Start(silentPsi)!;
            await silentProc.WaitForExitAsync(ct);
            _log.Info($"Silent_Setup.exe exit: {silentProc.ExitCode}");

            if (silentProc.ExitCode == 0 || silentProc.ExitCode == 3010 || silentProc.ExitCode == 1641)
                return (true, $"Silent_Setup exit {silentProc.ExitCode}");

            return (false, $"Silent_Setup retornou código {silentProc.ExitCode}");
        }
        catch (Exception ex)
        {
            _log.Error($"WinRAR SFX install failed: {ex.Message}");
            return (false, ex.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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
