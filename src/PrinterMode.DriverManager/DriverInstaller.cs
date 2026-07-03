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

        // Shared printer: no driver install, no port — just connect via UNC path
        if (request.ConnectionType == ConnectionType.Shared)
            return await ConnectSharedPrinterAsync(request, progress, ct);

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
            string? detectedDriverName = null; // actual Windows driver name found after install
            IReadOnlyList<string> driversAtInstallStart = []; // snapshot for diff-based detection in Step 3
            string? discoveredUsbPort = null; // populated during Step 1 polling so Step 2 doesn't race

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

                if (installerType == "manual")
                {
                    var url = string.IsNullOrEmpty(request.Driver.DownloadUrl)
                        ? "site do fabricante"
                        : request.Driver.DownloadUrl;
                    return InstallResult.Fail(
                        $"O driver {request.Driver.DisplayName} precisa ser instalado manualmente.",
                        $"Baixe e instale o driver em: {url}\nDepois abra o PrinterMode novamente.",
                        steps);
                }

                if (!request.Driver.HasInstaller || exePath == null)
                    return InstallResult.Fail("Nenhum instalador encontrado para este driver.", null, steps);

                bool needsUiInstall = installerType == "ui-only";

                // Snapshot drivers before install for all paths (diff-based detection)
                driversAtInstallStart = await _printerService.GetInstalledDriversAsync(ct);

                // ── WinRAR SFX: extract to temp, kill auto-launched setup, run Silent_Setup.exe ──
                if (installerType == "winrar-sfx" && !string.IsNullOrEmpty(request.Driver.SilentSetupExe))
                {
                    var sfxResult = await InstallWinRarSfxAsync(exePath, request.Driver.SilentSetupExe!, ct, progress);
                    if (sfxResult.success)
                    {
                        await Task.Delay(4000, ct);
                        var driversAfterSfx = await _printerService.GetInstalledDriversAsync(ct);
                        var resolvedSfx = ResolveActualDriverName(request.Driver, driversAfterSfx)
                            ?? driversAfterSfx.Except(driversAtInstallStart, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                        if (resolvedSfx != null)
                        {
                            detectedDriverName = resolvedSfx;
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

                    // driversBefore reuses the snapshot taken above (no second WMI call)
                    var driversBefore = driversAtInstallStart;
                    var storeSnapBefore = SnapshotDriverStore();

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

                    // Brief pause for installer to finish writing files/registry.
                    await Task.Delay(1000, ct);

                    bool needsUsbPort = request.ConnectionType == ConnectionType.USB;
                    string? resolvedSilent = null;

                    // ── Quick driver-list check (one PowerShell call) ─────────────────────
                    // Network and serial installers register the driver directly with the
                    // Print Spooler — they never create a PnP print queue, so they won't
                    // appear in Win32_Printer. Get-PrinterDriver reflects them immediately.
                    progress?.Report("Verificando driver instalado...");
                    var quickList = await _printerService.GetInstalledDriversAsync(ct);
                    resolvedSilent = ResolveActualDriverName(request.Driver, quickList)
                        ?? quickList.Except(driversBefore, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                    if (resolvedSilent != null)
                        _log.Info($"Quick driver-list check found: '{resolvedSilent}'");

                    // ── Phase 1: Fast WMI check — no Spooler restart ──────────────────────
                    // Win32_Printer already contains the driver name AND port name that PnP
                    // assigned. Query it directly (no PowerShell process spawn → fast).
                    // 5 polls × 1.5 s ≈ 7-8 s max. Covers the common USB plug-and-play case.
                    for (int q = 0; q < 5 && (resolvedSilent == null || (needsUsbPort && discoveredUsbPort == null)); q++)
                    {
                        if (q > 0) await Task.Delay(1500, ct);

                        var (autoDriver, autoPort) = await _printerService.FindAutoInstalledPrinterInfoAsync(
                            request.Driver.Manufacturer, request.Driver.Model, ct);

                        if (autoDriver != null)
                        {
                            resolvedSilent ??= autoDriver;
                            if (needsUsbPort && !string.IsNullOrEmpty(autoPort))
                                discoveredUsbPort ??= autoPort;
                            _log.Info($"Phase1 poll {q + 1}: driver='{autoDriver}' port='{autoPort ?? "n/a"}'");
                        }
                    }

                    // ── Phase 2: Restart Spooler only if Phase 1 didn't resolve ──────────
                    IReadOnlyList<string> driversAfterSilent = driversBefore;
                    if (resolvedSilent == null || (needsUsbPort && discoveredUsbPort == null))
                    {
                        if (resolvedSilent == null)
                        {
                            progress?.Report("Reiniciando serviço de impressão para carregar o driver...");
                            await _printerService.RestartSpoolerAsync(ct);
                        }
                        else
                        {
                            progress?.Report("Aguardando porta USB...");
                        }

                        for (int poll = 0; poll < 6 && (resolvedSilent == null || (needsUsbPort && discoveredUsbPort == null)); poll++)
                        {
                            await Task.Delay(3000, ct);

                            driversAfterSilent = await _printerService.GetInstalledDriversAsync(ct);
                            resolvedSilent ??= ResolveActualDriverName(request.Driver, driversAfterSilent)
                                ?? driversAfterSilent.Except(driversBefore, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                            if (resolvedSilent != null)
                                _log.Info($"Phase2 poll {poll + 1}: driver='{resolvedSilent}'");

                            var (autoDriver2, autoPort2) = await _printerService.FindAutoInstalledPrinterInfoAsync(
                                request.Driver.Manufacturer, request.Driver.Model, ct);
                            resolvedSilent ??= autoDriver2;
                            if (needsUsbPort && !string.IsNullOrEmpty(autoPort2))
                                discoveredUsbPort ??= autoPort2;

                            if (needsUsbPort && discoveredUsbPort == null)
                                discoveredUsbPort = await _printerService.FindBestUsbPortAsync(ct);
                        }
                    }

                    if (resolvedSilent == null)
                    {
                        // DriverStore + pnputil fallback
                        _log.Warning("Driver not detected after all phases. Searching DriverStore...");
                        progress?.Report("Buscando driver no DriverStore do Windows...");
                        var newInfFiles = FindNewDriverStoreInfs(storeSnapBefore, request.Driver);
                        foreach (var stagedInf in newInfFiles)
                        {
                            _log.Info($"Found staged inf in DriverStore: '{stagedInf}'");
                            var pnpOk = await InstallViaPnpUtilAsync(stagedInf, ct);
                            if (pnpOk) break;
                        }

                        await Task.Delay(3000, ct);
                        var driversAfterStore = await _printerService.GetInstalledDriversAsync(ct);
                        resolvedSilent = ResolveActualDriverName(request.Driver, driversAfterStore)
                            ?? driversAfterStore.Except(driversBefore, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

                        var (storeAutoDriver, storeAutoPort) = await _printerService.FindAutoInstalledPrinterInfoAsync(
                            request.Driver.Manufacturer, request.Driver.Model, ct);
                        resolvedSilent ??= storeAutoDriver;
                        if (needsUsbPort && !string.IsNullOrEmpty(storeAutoPort))
                            discoveredUsbPort ??= storeAutoPort;

                        if (resolvedSilent != null)
                            _log.Info($"Driver found after DriverStore/pnputil: '{resolvedSilent}'");
                    }

                    if (resolvedSilent != null)
                    {
                        detectedDriverName = resolvedSilent;
                        driverInstalled = true;
                        steps.Add($"Driver detectado: '{resolvedSilent}'");
                        _log.Info($"Driver name confirmed: '{resolvedSilent}'");
                    }
                    else
                    {
                        // Detection failed but EXE exited 0 — proceed to probe all candidate names.
                        var driversVisible = await _printerService.GetInstalledDriversAsync(ct);
                        var list = string.Join(", ", driversVisible.Take(10));
                        _log.Warning($"Detection failed after all stages. Drivers visible: [{list}]. Probing all candidate names.");
                        detectedDriverName = null;
                        driverInstalled = true;
                        steps.Add("Driver instalado mas nome não confirmado — testando nomes na criação...");
                    }
                }

                // ── UI installer: open with wizard, wait for user to complete ─────────────
                if (!driverInstalled && needsUiInstall)
                {
                    progress?.Report($"⚠ Siga as instruções do instalador {request.Driver.DisplayName} na janela que abrir...");
                    _log.Info($"Opening UI installer for {request.Driver.DisplayName}: {exePath}");

                    var driversBeforeUi = await _printerService.GetInstalledDriversAsync(ct);

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
                    await Task.Delay(5000, ct);

                    var driversAfterUi = await _printerService.GetInstalledDriversAsync(ct);
                    var resolvedUi = ResolveActualDriverName(request.Driver, driversAfterUi)
                        ?? driversAfterUi.Except(driversBeforeUi, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

                    if (resolvedUi == null)
                    {
                        var list = string.Join(", ", driversAfterUi.Take(10));
                        return InstallResult.Fail(
                            "O instalador foi executado, mas o driver não foi encontrado no Windows.",
                            $"Drivers instalados: {list}", steps);
                    }

                    detectedDriverName = resolvedUi;
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
                    if (string.IsNullOrWhiteSpace(request.IpAddress))
                        return InstallResult.Fail(
                            "Endereço IP não informado para conexão de rede.",
                            "Informe o IP da impressora no campo correspondente.", steps);
                    portName = request.PortName ?? $"IP_{request.IpAddress}";
                    portCreated = await _printerService.CreateTcpIpPortAsync(
                        portName, request.IpAddress, request.NetworkPort, ct);
                    steps.Add($"Porta TCP/IP criada: {portName} → {request.IpAddress}:{request.NetworkPort}");
                    break;

                case ConnectionType.Serial:
                    if (string.IsNullOrWhiteSpace(request.PortName))
                        return InstallResult.Fail(
                            "Porta serial não selecionada.",
                            "Selecione a porta COM da impressora antes de instalar.", steps);
                    portName = request.PortName!;
                    await ConfigureSerialPortAsync(portName, request.SerialConfig, ct);
                    portCreated = true;
                    steps.Add($"Porta serial configurada: {portName}");
                    break;

                case ConnectionType.USB:
                default:
                    // Quick poll (3×1s) — check both USB ports registered with the Spooler
                    // and any port already assigned by PnP auto-printer creation.
                    for (int p = 0; p < 3 && discoveredUsbPort == null; p++)
                    {
                        discoveredUsbPort = await _printerService.FindBestUsbPortAsync(ct);
                        if (discoveredUsbPort == null)
                        {
                            var (_, pnpPort) = await _printerService.FindAutoInstalledPrinterInfoAsync(
                                request.Driver.Manufacturer, request.Driver.Model, ct);
                            if (!string.IsNullOrEmpty(pnpPort) && pnpPort.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
                                discoveredUsbPort = pnpPort;
                        }
                        if (discoveredUsbPort == null) await Task.Delay(1000, ct);
                    }

                    if (discoveredUsbPort == null)
                    {
                        // Port not yet registered — restart Spooler so USB devices are
                        // re-enumerated and the port (USB001/USB002/…) gets registered.
                        progress?.Report("Detectando porta USB...");
                        await _printerService.RestartSpoolerAsync(ct);
                        for (int p = 0; p < 5 && discoveredUsbPort == null; p++)
                        {
                            await Task.Delay(2000, ct);
                            discoveredUsbPort = await _printerService.FindBestUsbPortAsync(ct);
                            if (discoveredUsbPort == null)
                            {
                                var (_, pnpPort2) = await _printerService.FindAutoInstalledPrinterInfoAsync(
                                    request.Driver.Manufacturer, request.Driver.Model, ct);
                                if (!string.IsNullOrEmpty(pnpPort2) && pnpPort2.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
                                    discoveredUsbPort = pnpPort2;
                            }
                        }
                    }

                    portName = request.PortName ?? discoveredUsbPort ?? "USB001";
                    portCreated = true;
                    steps.Add($"Porta USB: {portName}");
                    _log.Info($"USB port selected: {portName} (discovered: {discoveredUsbPort ?? "none"})");
                    break;
            }

            if (!portCreated)
                return InstallResult.Fail($"Falha ao criar porta {portName}.", null, steps);

            // Step 3: Add printer or update port if it already exists
            progress?.Report("Configurando impressora no Windows...");

            var printerExists = await _printerService.PrinterExistsAsync(request.PrinterName, ct);

            if (printerExists)
            {
                // If we have a confirmed USB port, update it. If not (port was not found
                // even after Spooler restart), keep the existing port that Windows PnP
                // assigned — changing it to "USB001" fallback would break the printer.
                if (request.ConnectionType == ConnectionType.USB && discoveredUsbPort == null)
                {
                    _log.Info($"Printer '{request.PrinterName}' exists; USB port not found — keeping PnP-assigned port.");
                    steps.Add($"Impressora '{request.PrinterName}' já configurada (porta preservada).");
                }
                else
                {
                    _log.Info($"Printer '{request.PrinterName}' already exists, updating port to '{portName}'.");
                    progress?.Report($"Impressora já existe, atualizando porta para '{portName}'...");

                    var updated = await _printerService.UpdatePrinterPortAsync(request.PrinterName, portName, ct);
                    if (!updated)
                    {
                        return InstallResult.Fail(
                            $"Falha ao atualizar a porta da impressora '{request.PrinterName}'.",
                            "Verifique as permissões e tente novamente.",
                            steps);
                    }

                    steps.Add($"Porta atualizada: {request.PrinterName} → {portName}");
                    _log.Info($"Printer port updated: {request.PrinterName} → {portName}");
                }
            }
            else
            {
                // Build ordered list of driver names to try.
                // When detectedDriverName is set we use it first; otherwise probe all known names.
                List<string> driverNamesToTry;
                if (detectedDriverName != null)
                {
                    driverNamesToTry = [detectedDriverName];
                }
                else
                {
                    // Do one final fresh lookup — by now the Spooler may have settled
                    var freshDrivers = await _printerService.GetInstalledDriversAsync(ct);
                    var freshResolved = ResolveActualDriverName(request.Driver, freshDrivers);
                    if (freshResolved != null)
                    {
                        _log.Info($"Fresh lookup found driver: '{freshResolved}'");
                        driverNamesToTry = [freshResolved];
                    }
                    else
                    {
                        // Any driver that appeared after install (unknown name) goes first,
                        // then all known candidate names
                        var freshNew = driversAtInstallStart.Count > 0
                            ? freshDrivers
                                .Except(driversAtInstallStart, StringComparer.OrdinalIgnoreCase)
                                .ToList()
                            : [];
                        var knownCandidates = request.Driver.AllDriverNames().ToList();
                        driverNamesToTry = [..freshNew, ..knownCandidates];
                        _log.Info($"Probing {driverNamesToTry.Count} driver names ({freshNew.Count} newly detected + {knownCandidates.Count} known): [{string.Join(", ", driverNamesToTry)}]");
                    }
                }

                bool printerAdded = false;
                string usedDriverName = driverNamesToTry[0];
                foreach (var candidateName in driverNamesToTry)
                {
                    _log.Info($"Trying AddPrinterAsync with driver: '{candidateName}'");
                    progress?.Report($"Criando impressora (driver: {candidateName})...");
                    if (await _printerService.AddPrinterAsync(request.PrinterName, candidateName, portName, ct))
                    {
                        printerAdded = true;
                        usedDriverName = candidateName;
                        break;
                    }
                }

                if (!printerAdded)
                {
                    _log.Error($"AddPrinterAsync failed for all candidates. port='{portName}' tried=[{string.Join(", ", driverNamesToTry)}]");

                    // Diagnose: check if the USB port exists — a missing port causes Add-Printer
                    // to fail for ALL driver names, producing a misleading "driver not accepted" error.
                    if (request.ConnectionType == ConnectionType.USB && discoveredUsbPort == null)
                    {
                        return InstallResult.Fail(
                            "Porta USB não encontrada no Windows.",
                            "Verifique se a impressora está conectada e ligada. Desconecte e reconecte o cabo USB e tente novamente.",
                            steps);
                    }

                    return InstallResult.Fail(
                        "Falha ao criar impressora no Windows.",
                        $"Nenhum driver foi aceito pelo Windows. Nomes tentados: {string.Join(", ", driverNamesToTry)}\n" +
                        "Verifique o nome exato do driver em: Painel de Controle → Gerenciamento de Impressão → Drivers.",
                        steps);
                }

                steps.Add($"Impressora criada: {request.PrinterName} (driver: {usedDriverName})");
                _log.Info($"Printer created: {request.PrinterName} with driver '{usedDriverName}'");
            }

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

            // Enable LPD service so other PCs on the network can connect to this printer
            // without needing SMB credentials (LPD uses port 515, no authentication).
            _ = _printerService.EnableLpdServiceAsync(ct);

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

    private async Task<InstallResult> ConnectSharedPrinterAsync(
        InstallRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        var steps = new List<string>();

        var host = (request.SharedHost ?? "").Trim().TrimStart('\\');
        if (string.IsNullOrEmpty(host))
            return InstallResult.Fail(
                "Informe o nome ou IP do computador que compartilha a impressora.", null, steps);

        var shareName = (request.SharedPrinterName ?? "").Trim();

        // ── Path 1: SMB (Windows share connection) ────────────────────────────────
        // Try to connect via \\host\share. This is the standard approach but requires
        // that SMB auth succeeds (same account on both PCs, or password sharing disabled).

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(shareName))
            candidates.Add(shareName);

        if (candidates.Count == 0)
        {
            progress?.Report($"Descobrindo impressoras em {host}...");
            var discovered = await _printerService.GetSharedPrintersAsync(host, ct);
            candidates.AddRange(discovered.Where(n => !n.StartsWith("__DIAG:") && !string.IsNullOrWhiteSpace(n)));
        }

        if (candidates.Count == 0)
            candidates.Add(request.PrinterName);

        string? lastSmbError = null;
        string? connectedViaSmbAs = null;

        foreach (var candidate in candidates)
        {
            var connectionName = $@"\\{host}\{candidate}";
            progress?.Report($"Conectando via SMB: {connectionName}...");
            _log.Info($"Trying SMB: {connectionName}");

            var (ok, psError) = await _printerService.AddSharedPrinterInternalAsync(connectionName, ct);
            if (ok)
            {
                connectedViaSmbAs = connectionName;
                steps.Add($"Conectado via SMB: {connectionName}");
                break;
            }
            lastSmbError = string.IsNullOrEmpty(psError) ? "Acesso negado." : psError;
            _log.Warning($"SMB failed for '{connectionName}': {lastSmbError}");
        }

        if (connectedViaSmbAs != null)
        {
            if (request.SetAsDefault)
            {
                var part = connectedViaSmbAs.TrimStart('\\').Split('\\').LastOrDefault() ?? request.PrinterName;
                await _printerService.SetDefaultPrinterAsync(part, ct);
                steps.Add("Definida como impressora padrão.");
            }
            progress?.Report("Impressora compartilhada conectada com sucesso!");
            return InstallResult.Ok($"Impressora conectada: {connectedViaSmbAs}", request.PrinterName, steps);
        }

        // ── Path 2: LPD/LPR fallback ─────────────────────────────────────────────
        // SMB failed (typically: access denied / authentication). LPD (port 515) exposes
        // Windows shared printers without any password. When our app installed the printer
        // on the host PC it automatically enabled the Windows LPD service for this reason.

        if (string.IsNullOrWhiteSpace(shareName))
        {
            return InstallResult.Fail(
                "Não foi possível conectar via SMB e o nome do compartilhamento não foi informado para tentar LPD.",
                $"SMB: {lastSmbError}\n\n" +
                "Clique em Buscar para tentar descobrir o nome, ou informe-o manualmente no campo 'Nome do compartilhamento'.",
                steps);
        }

        progress?.Report($"SMB negado — tentando LPD (porta 515) em {host}...");
        bool lpdUp = await _printerService.IsLpdAvailableAsync(host, ct);

        if (!lpdUp)
        {
            return InstallResult.Fail(
                "Conexão via SMB e LPD falharam.",
                $"SMB: {lastSmbError}\n" +
                $"LPD (porta 515): não respondeu em {host}.\n\n" +
                "No computador com a impressora, execute o PrinterMode e instale a impressora. " +
                "O aplicativo habilita o serviço LPD automaticamente durante a instalação.",
                steps);
        }

        // Create an LPR port (Protocol=2, queue=shareName) and add the printer locally.
        // We need a local driver — use whatever is already installed on this machine that
        // matches; fall back to "Generic / Text Only" if nothing is found.
        progress?.Report("LPD disponível — instalando via LPR...");

        var lprPortName = $"LPR_{host.Replace('.', '_')}_{shareName}";
        bool portCreated = await _printerService.CreateLprPortAsync(lprPortName, host, shareName, ct);
        if (!portCreated)
            return InstallResult.Fail("Falha ao criar porta LPR.", null, steps);

        steps.Add($"Porta LPR criada: {lprPortName}");

        // Resolve driver: prefer the one from the request, then any installed non-system driver,
        // then fall back to the Windows built-in "Generic / Text Only".
        string? driverName = null;
        if (!string.IsNullOrWhiteSpace(request.Driver?.DriverName))
            driverName = request.Driver.DriverName;

        if (driverName == null)
        {
            var (foundDriver, _) = await _printerService.FindAutoInstalledPrinterInfoAsync(
                request.Driver?.Manufacturer ?? "", request.Driver?.Model ?? "", ct);
            driverName = foundDriver;
        }

        if (driverName == null)
        {
            var allDrivers = await _printerService.GetInstalledDriversAsync(ct);
            driverName = allDrivers.FirstOrDefault(d =>
                !d.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) &&
                !d.Contains("OneNote", StringComparison.OrdinalIgnoreCase) &&
                !d.Contains("Fax", StringComparison.OrdinalIgnoreCase) &&
                !d.Contains("XPS", StringComparison.OrdinalIgnoreCase) &&
                !d.Contains("PDF", StringComparison.OrdinalIgnoreCase));
        }

        driverName ??= "Generic / Text Only";

        var printerDisplayName = string.IsNullOrWhiteSpace(request.PrinterName) ? shareName : request.PrinterName;
        progress?.Report($"Adicionando impressora '{printerDisplayName}' (driver: {driverName})...");

        bool added = await _printerService.AddPrinterAsync(printerDisplayName, driverName, lprPortName, ct);
        if (!added)
            return InstallResult.Fail($"Falha ao criar a impressora via LPR com driver '{driverName}'.", null, steps);

        steps.Add($"Impressora criada via LPD: '{printerDisplayName}' driver='{driverName}'");

        if (request.SetAsDefault)
        {
            await _printerService.SetDefaultPrinterAsync(printerDisplayName, ct);
            steps.Add("Definida como impressora padrão.");
        }

        progress?.Report("Impressora instalada via LPD com sucesso!");
        return InstallResult.Ok(
            $"Impressora '{printerDisplayName}' instalada via LPD (sem senha necessária).",
            printerDisplayName, steps);
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

    private static HashSet<string> SnapshotDriverStore()
    {
        var storeRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "DriverStore", "FileRepository");
        if (!Directory.Exists(storeRoot)) return [];
        try
        {
            return Directory.GetFiles(storeRoot, "*.inf", SearchOption.AllDirectories)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch { return []; }
    }

    private IReadOnlyList<string> FindNewDriverStoreInfs(HashSet<string> before, DriverInfo driver)
    {
        var storeRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "DriverStore", "FileRepository");
        if (!Directory.Exists(storeRoot)) return [];
        try
        {
            var after = Directory.GetFiles(storeRoot, "*.inf", SearchOption.AllDirectories);
            var mfgLower = driver.Manufacturer.ToLowerInvariant();
            var modelLower = driver.Model.Replace("-", "").Replace(" ", "").ToLowerInvariant();

            var newFiles = after.Where(f => !before.Contains(f)).ToList();

            // Priority 1: new .inf whose path/name matches manufacturer or model
            var byName = newFiles.Where(f =>
            {
                var dir = Path.GetFileName(Path.GetDirectoryName(f) ?? "").ToLowerInvariant();
                var name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                return dir.Contains(mfgLower) || dir.Contains(modelLower)
                    || name.Contains(mfgLower) || name.Contains(modelLower);
            }).ToList();

            if (byName.Count > 0) return byName;

            // Priority 2: any new .inf that declares Class=Printer (any manufacturer)
            var byClass = newFiles.Where(f =>
            {
                try
                {
                    var content = File.ReadAllText(f, System.Text.Encoding.Latin1);
                    return content.Contains("Class=Printer", StringComparison.OrdinalIgnoreCase)
                        || content.Contains("Class = Printer", StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }).ToList();

            return byClass;
        }
        catch { return []; }
    }

    private async Task<bool> InstallViaPnpUtilAsync(string infPath, CancellationToken ct)
    {
        if (!File.Exists(infPath))
        {
            _log.Warning($"pnputil fallback: inf not found at '{infPath}'");
            return false;
        }

        try
        {
            _log.Info($"pnputil /add-driver \"{infPath}\" /install");
            var psi = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = $"/add-driver \"{infPath}\" /install",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync(ct);
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            _log.Info($"pnputil exit: {proc.ExitCode}. Output: {output.Trim()}");

            return proc.ExitCode == 0 || proc.ExitCode == 3010;
        }
        catch (Exception ex)
        {
            _log.Warning($"pnputil fallback failed: {ex.Message}");
            return false;
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
