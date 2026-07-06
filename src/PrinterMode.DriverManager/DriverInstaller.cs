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

        // Enable LPD service in the background for all connection types so other PCs
        // can always find and connect to this printer via LPD (port 515, no password).
        // CancellationToken.None: must not be cancelled when the install flow finishes.
        _ = _printerService.EnableLpdServiceAsync(CancellationToken.None);

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
            IReadOnlyList<string> portsBeforeInstall = [];    // snapshot for diff-based USB port detection
            IReadOnlyList<string> printersBeforeInstall = []; // snapshot for new-printer port detection

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

                    // Snapshot ports AND printers BEFORE the installer runs.
                    // Ports: diff catches new USB/vendor-specific port names (BEMATECHUSB001, etc.).
                    // Printers: diff catches printer queues auto-created by the installer EXE —
                    //   the installer knows the correct port; we read it from the new queue.
                    bool needsUsbPort = request.ConnectionType == ConnectionType.USB;
                    if (needsUsbPort)
                    {
                        portsBeforeInstall   = await _printerService.GetAvailablePortsAsync(ct);
                        printersBeforeInstall = await _printerService.GetInstalledPrintersAsync(ct);
                    }

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

                    if (needsUsbPort)
                    {
                        // Many printer installers launch their own Spooler restart as a post-install
                        // step and exit the main EXE before that restart completes. Wait 8 seconds
                        // to let that internal restart settle, then check whether the installer
                        // already created the port — BEFORE touching the Spooler ourselves.
                        progress?.Report("Aguardando conclusão da instalação do driver...");
                        await Task.Delay(8000, ct);

                        var earlyPort = await _printerService.FindBestUsbPortAsync(ct)
                            ?? await _printerService.FindNewPortSinceSnapshotAsync(portsBeforeInstall, ct)
                            ?? await _printerService.FindUsbPortFromRegistryAsync(ct)
                            ?? await _printerService.FindPortFromNewPrinterAsync(printersBeforeInstall, ct);
                        if (earlyPort != null)
                        {
                            discoveredUsbPort = earlyPort;
                            _log.Info($"USB port found from installer's own setup: '{earlyPort}'");
                        }
                        else
                        {
                            // Port not yet created — restart Spooler ourselves so USB Monitor can
                            // re-enumerate and register any connected USB printer device.
                            progress?.Report("Preparando serviço de impressão USB...");
                            await _printerService.RestartSpoolerAsync(ct);
                            await Task.Delay(5000, ct);

                            earlyPort = await _printerService.FindBestUsbPortAsync(ct)
                                ?? await _printerService.FindNewPortSinceSnapshotAsync(portsBeforeInstall, ct)
                                ?? await _printerService.FindUsbPortFromRegistryAsync(ct)
                                ?? await _printerService.FindPortFromNewPrinterAsync(printersBeforeInstall, ct);
                            if (earlyPort != null)
                            {
                                discoveredUsbPort = earlyPort;
                                _log.Info($"USB port found after spooler restart: '{earlyPort}'");
                            }
                            else
                            {
                                // Port still not created — cycle the exact USB device (by VID/PID) to force
                                // Windows to re-run driver matching now that the real INF is staged.
                                // IMPORTANT: must restart Spooler AFTER cycling the device — USB Monitor
                                // only creates port entries during Spooler startup, not mid-run.
                                if (!string.IsNullOrEmpty(request.Driver.VendorId) &&
                                    !string.IsNullOrEmpty(request.Driver.ProductId))
                                {
                                    _log.Info($"USB port missing — targeted VID/PID re-enumeration ({request.Driver.VendorId}/{request.Driver.ProductId})");
                                    progress?.Report("Forçando reconhecimento do dispositivo USB...");
                                    await _printerService.ReEnumerateDeviceByVidPidAsync(
                                        request.Driver.VendorId!, request.Driver.ProductId!, ct);
                                    // Restart Spooler so USB Monitor re-initialises and registers the port.
                                    await _printerService.RestartSpoolerAsync(ct);
                                    await Task.Delay(6000, ct);
                                    discoveredUsbPort = await _printerService.FindBestUsbPortAsync(ct)
                                        ?? await _printerService.FindNewPortSinceSnapshotAsync(portsBeforeInstall, ct)
                                        ?? await _printerService.FindUsbPortFromRegistryAsync(ct)
                                        ?? await _printerService.FindPortFromNewPrinterAsync(printersBeforeInstall, ct);
                                    if (discoveredUsbPort != null)
                                        _log.Info($"USB port found after VID/PID re-enumeration + Spooler restart: '{discoveredUsbPort}'");
                                }

                                // If still missing, try pnputil with the real DriverStore INF then restart
                                // Spooler. pnputil /install forces usbprint.sys to bind to the device;
                                // the subsequent Spooler restart causes USB Monitor to create the port.
                                if (discoveredUsbPort == null)
                                {
                                    var storeInfsEarly = FindNewDriverStoreInfs(storeSnapBefore, request.Driver);
                                    var bestStoreInf = storeInfsEarly.FirstOrDefault();
                                    if (bestStoreInf != null)
                                    {
                                        _log.Info($"USB port missing — pnputil /install with DriverStore INF: '{bestStoreInf}'");
                                        await InstallViaPnpUtilAsync(bestStoreInf, ct);
                                        await _printerService.RestartSpoolerAsync(ct);
                                        await Task.Delay(6000, ct);
                                        discoveredUsbPort = await _printerService.FindBestUsbPortAsync(ct)
                                            ?? await _printerService.FindNewPortSinceSnapshotAsync(portsBeforeInstall, ct)
                                            ?? await _printerService.FindUsbPortFromRegistryAsync(ct)
                                            ?? await _printerService.FindPortFromNewPrinterAsync(printersBeforeInstall, ct);
                                        if (discoveredUsbPort != null)
                                            _log.Info($"USB port found after DriverStore pnputil + Spooler restart: '{discoveredUsbPort}'");
                                    }
                                }

                                // Try Windows built-in usbprint.inf to force usbprint.sys to bind to
                                // any USB Class 07 (printer class) device. Safe — it is a signed
                                // Microsoft driver and will only install on devices that match.
                                if (discoveredUsbPort == null)
                                {
                                    var winInf = Path.Combine(
                                        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                        "inf", "usbprint.inf");
                                    if (File.Exists(winInf))
                                    {
                                        _log.Info("USB port missing — trying built-in usbprint.inf to bind usbprint.sys");
                                        await InstallViaPnpUtilAsync(winInf, ct);
                                        await _printerService.RestartSpoolerAsync(ct);
                                        await Task.Delay(6000, ct);
                                        discoveredUsbPort = await _printerService.FindBestUsbPortAsync(ct)
                                            ?? await _printerService.FindNewPortSinceSnapshotAsync(portsBeforeInstall, ct)
                                            ?? await _printerService.FindUsbPortFromRegistryAsync(ct)
                                            ?? await _printerService.FindPortFromNewPrinterAsync(printersBeforeInstall, ct);
                                        if (discoveredUsbPort != null)
                                            _log.Info($"USB port found after usbprint.inf + Spooler restart: '{discoveredUsbPort}'");
                                    }
                                }

                                // Broad re-enumeration fallback
                                if (discoveredUsbPort == null)
                                {
                                    _log.Info("USB port still not registered — broad device re-enumeration...");
                                    await _printerService.ReEnumerateUsbPrinterDevicesAsync(ct);
                                    await _printerService.RestartSpoolerAsync(ct);
                                    await Task.Delay(5000, ct);
                                    discoveredUsbPort = await _printerService.FindBestUsbPortAsync(ct)
                                        ?? await _printerService.FindNewPortSinceSnapshotAsync(portsBeforeInstall, ct)
                                        ?? await _printerService.FindUsbPortFromRegistryAsync(ct)
                                        ?? await _printerService.FindPortFromNewPrinterAsync(printersBeforeInstall, ct);
                                    if (discoveredUsbPort != null)
                                        _log.Info($"USB port found after broad re-enum + Spooler restart: '{discoveredUsbPort}'");
                                }

                                // Check if usbprint.sys is now bound even though no port is visible yet
                                if (discoveredUsbPort == null && !string.IsNullOrEmpty(request.Driver.VendorId))
                                {
                                    var forced = await _printerService.ForceUsbPortFromUsbPrintAsync(
                                        request.Driver.VendorId!, request.Driver.ProductId ?? "", ct);
                                    if (forced != null)
                                    {
                                        discoveredUsbPort = forced;
                                        _log.Info($"Early block nuclear: ForceUsbPortFromUsbPrint returned '{forced}'");
                                    }
                                }
                            }
                        }
                    }
                    string? resolvedSilent = null;

                    // ── Quick driver-list check (one PowerShell call) ─────────────────────
                    // Network and serial installers register the driver directly with the
                    // Print Spooler — they never create a PnP print queue, so they won't
                    // appear in Win32_Printer. Get-PrinterDriver reflects them immediately.
                    progress?.Report("Verificando driver instalado...");
                    var quickList = await _printerService.GetInstalledDriversAsync(ct);
                    resolvedSilent = ResolveActualDriverName(request.Driver, quickList)
                        ?? quickList.Except(driversBefore, StringComparer.OrdinalIgnoreCase)
                                    .FirstOrDefault(d => !IsSystemDriver(d));
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
                        if (needsUsbPort && discoveredUsbPort == null)
                            discoveredUsbPort = await _printerService.FindBestUsbPortAsync(ct)
                                ?? await _printerService.FindNewPortSinceSnapshotAsync(portsBeforeInstall, ct)
                                ?? await _printerService.FindUsbPortFromRegistryAsync(ct)
                                ?? await _printerService.FindPortFromNewPrinterAsync(printersBeforeInstall, ct);
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
                                ?? driversAfterSilent.Except(driversBefore, StringComparer.OrdinalIgnoreCase)
                                                     .FirstOrDefault(d => !IsSystemDriver(d));
                            if (resolvedSilent != null)
                                _log.Info($"Phase2 poll {poll + 1}: driver='{resolvedSilent}'");

                            var (autoDriver2, autoPort2) = await _printerService.FindAutoInstalledPrinterInfoAsync(
                                request.Driver.Manufacturer, request.Driver.Model, ct);
                            resolvedSilent ??= autoDriver2;
                            if (needsUsbPort && !string.IsNullOrEmpty(autoPort2))
                                discoveredUsbPort ??= autoPort2;

                            if (needsUsbPort && discoveredUsbPort == null)
                                discoveredUsbPort = await _printerService.FindBestUsbPortAsync(ct)
                                    ?? await _printerService.FindNewPortSinceSnapshotAsync(portsBeforeInstall, ct)
                                    ?? await _printerService.FindUsbPortFromRegistryAsync(ct)
                                    ?? await _printerService.FindPortFromNewPrinterAsync(printersBeforeInstall, ct);
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
                            ?? driversAfterStore.Except(driversBefore, StringComparer.OrdinalIgnoreCase)
                                               .FirstOrDefault(d => !IsSystemDriver(d));

                        var (storeAutoDriver, storeAutoPort) = await _printerService.FindAutoInstalledPrinterInfoAsync(
                            request.Driver.Manufacturer, request.Driver.Model, ct);
                        resolvedSilent ??= storeAutoDriver;
                        if (needsUsbPort && !string.IsNullOrEmpty(storeAutoPort))
                            discoveredUsbPort ??= storeAutoPort;
                        if (needsUsbPort && discoveredUsbPort == null)
                            discoveredUsbPort = await _printerService.FindNewPortSinceSnapshotAsync(portsBeforeInstall, ct)
                                ?? await _printerService.FindUsbPortFromRegistryAsync(ct)
                                ?? await _printerService.FindPortFromNewPrinterAsync(printersBeforeInstall, ct);

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
                    steps.Add(portCreated
                        ? $"Porta TCP/IP criada: {portName} → {request.IpAddress}:{request.NetworkPort}"
                        : $"Falha ao criar porta TCP/IP: {portName} → {request.IpAddress}:{request.NetworkPort}");
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
                    // ── Adopt the printer the installer already created ────────────────────
                    // The manufacturer's installer usually creates a working printer queue by
                    // itself — with the correct driver AND port already wired to the device.
                    // That pairing is authoritative: instead of hunting for a port and probing
                    // driver names, we simply reuse what the installer put in the spooler. This
                    // is the most reliable path and sidesteps the whole "port not found" problem.
                    if (printersBeforeInstall.Count > 0)
                    {
                        var (newPrinterName, newPrinterDriver, newPrinterPort) =
                            await _printerService.FindNewlyCreatedPrinterAsync(printersBeforeInstall, ct);
                        if (!string.IsNullOrEmpty(newPrinterPort))
                        {
                            await _printerService.EnsurePortRegisteredAsync(newPrinterPort, ct);
                            discoveredUsbPort = newPrinterPort;
                            if (!string.IsNullOrEmpty(newPrinterDriver))
                                detectedDriverName = newPrinterDriver;
                            steps.Add($"Impressora criada pelo instalador adotada: {newPrinterName} (porta {newPrinterPort})");
                            _log.Info($"Adopting installer-created printer '{newPrinterName}': " +
                                      $"driver='{newPrinterDriver}' port='{newPrinterPort}'");
                        }
                    }

                    // ── Resolve the ACTUAL connected device ────────────────────────────────
                    // The catalog VID/PID comes from a reference TEMPLATE and may not match the
                    // real hardware (the earlier failures showed "VID_20D1&PID_7008 NÃO detectado"
                    // — no connected device had those ids). So instead of trusting the catalog,
                    // scan every connected USB device and pick the one that IS the printer (by
                    // name/class/port), then adopt its REAL VID/PID + COM port for every step below.
                    progress?.Report("Localizando a impressora conectada...");
                    var deviceHints = new List<string> { request.Driver.Manufacturer, request.Driver.Model };
                    deviceHints.AddRange(request.Driver.AllDriverNames());
                    var resolvedDevice = await _printerService.ResolvePrinterUsbDeviceAsync(
                        deviceHints, request.Driver.VendorId, request.Driver.ProductId, ct);
                    if (resolvedDevice != null)
                    {
                        steps.Add($"Dispositivo detectado: {resolvedDevice.FriendlyName}");
                        _log.Info($"Resolved connected device: '{resolvedDevice.FriendlyName}' " +
                                  $"vid={resolvedDevice.Vid} pid={resolvedDevice.Pid} port={resolvedDevice.Port} " +
                                  $"(catalog was {request.Driver.VendorId}/{request.Driver.ProductId})");
                        // Adopt the real ids so all VID/PID-based steps target the right hardware.
                        if (!string.IsNullOrEmpty(resolvedDevice.Vid))
                        {
                            request.Driver.VendorId = resolvedDevice.Vid;
                            request.Driver.ProductId = resolvedDevice.Pid;
                        }
                        // If the device already exposes a COM port (CDC virtual-serial), use it —
                        // unless we already adopted the installer-created printer's port above.
                        if (discoveredUsbPort == null && !string.IsNullOrEmpty(resolvedDevice.Port))
                        {
                            await _printerService.EnsurePortRegisteredAsync(resolvedDevice.Port, ct);
                            discoveredUsbPort = resolvedDevice.Port;
                            _log.Info($"USB case: using resolved device COM port '{resolvedDevice.Port}'");
                        }
                    }

                    // Highest-priority, device-keyed lookup: ask the physical device (VID/PID)
                    // which port IT is bound to, straight from its PnP registry node. This is
                    // the most reliable source and — critically — it catches CDC / virtual-serial
                    // POS printers (common for Gertec/Elgin/Bematech) that enumerate as a COMx
                    // device and NEVER get a USB001 spooler port. Without this, every USB-port
                    // hunt below searches for a port that will never exist on such machines.
                    if (discoveredUsbPort == null &&
                        !string.IsNullOrEmpty(request.Driver.VendorId) &&
                        !string.IsNullOrEmpty(request.Driver.ProductId))
                    {
                        var devicePort = await _printerService.FindDevicePortByVidPidAsync(
                            request.Driver.VendorId!, request.Driver.ProductId!, ct);
                        if (!string.IsNullOrEmpty(devicePort))
                        {
                            // A COMx port must be registered with the spooler before Add-Printer
                            // will accept it; monitor-managed ports (USBnnn) already are.
                            await _printerService.EnsurePortRegisteredAsync(devicePort, ct);
                            discoveredUsbPort = devicePort;
                            _log.Info($"USB case: device-keyed port resolved immediately: '{devicePort}'");
                        }
                    }

                    // Quick poll (3×1.5s) — check USB/DOT4/WSD ports registered with the Spooler
                    // and any port already assigned by PnP auto-printer creation.
                    for (int p = 0; p < 3 && discoveredUsbPort == null; p++)
                    {
                        if (p > 0) await Task.Delay(1500, ct);
                        discoveredUsbPort = await _printerService.FindBestUsbPortAsync(ct);
                        if (discoveredUsbPort == null)
                        {
                            var (_, pnpPort) = await _printerService.FindAutoInstalledPrinterInfoAsync(
                                request.Driver.Manufacturer, request.Driver.Model, ct);
                            // Accept any port the auto-installed printer uses — including vendor-specific
                            // names (GA001, GERTECUSB001, etc.) that don't start with USB/DOT4/WSD.
                            if (!string.IsNullOrEmpty(pnpPort) &&
                                !pnpPort.StartsWith("IP_",  StringComparison.OrdinalIgnoreCase) &&
                                !pnpPort.StartsWith("LPR_", StringComparison.OrdinalIgnoreCase) &&
                                !pnpPort.Equals("FILE:", StringComparison.OrdinalIgnoreCase) &&
                                !pnpPort.Equals("PORTPROMPT:", StringComparison.OrdinalIgnoreCase))
                                discoveredUsbPort = pnpPort;
                        }
                        if (discoveredUsbPort == null)
                            discoveredUsbPort = await _printerService.FindAnyUsbPrinterPortAsync(ct);
                        if (discoveredUsbPort == null)
                            discoveredUsbPort = await _printerService.FindNewPortSinceSnapshotAsync(portsBeforeInstall, ct)
                                ?? await _printerService.FindUsbPortFromRegistryAsync(ct);
                    }

                    if (discoveredUsbPort == null)
                    {
                        // Port still not registered — full recovery sequence.
                        progress?.Report("Detectando porta USB...");

                        // 1) Targeted VID/PID device cycle + Spooler restart.
                        //    Cycling the device forces Windows to re-run driver matching; the
                        //    subsequent Spooler restart causes USB Monitor to create the port entry.
                        if (!string.IsNullOrEmpty(request.Driver.VendorId) &&
                            !string.IsNullOrEmpty(request.Driver.ProductId))
                        {
                            _log.Info($"Recovery: VID/PID re-enumeration ({request.Driver.VendorId}/{request.Driver.ProductId})");
                            progress?.Report("Associando driver ao dispositivo USB...");
                            await _printerService.ReEnumerateDeviceByVidPidAsync(
                                request.Driver.VendorId!, request.Driver.ProductId!, ct);
                            await _printerService.RestartSpoolerAsync(ct);
                            await Task.Delay(6000, ct);
                            discoveredUsbPort = await _printerService.FindDevicePortByVidPidAsync(
                                    request.Driver.VendorId!, request.Driver.ProductId!, ct)
                                ?? await _printerService.FindBestUsbPortAsync(ct)
                                ?? await _printerService.FindAnyUsbPrinterPortAsync(ct)
                                ?? await _printerService.FindNewPortSinceSnapshotAsync(portsBeforeInstall, ct)
                                ?? await _printerService.FindUsbPortFromRegistryAsync(ct)
                                ?? await _printerService.FindPortFromNewPrinterAsync(printersBeforeInstall, ct);
                            if (discoveredUsbPort != null)
                            {
                                await _printerService.EnsurePortRegisteredAsync(discoveredUsbPort, ct);
                                _log.Info($"Recovery VID/PID + Spooler restart: port found '{discoveredUsbPort}'");
                            }
                        }

                        // 2) DriverStore INF pnputil + Spooler restart.
                        //    pnputil /install forces usbprint.sys to bind; Spooler restart creates port.
                        if (discoveredUsbPort == null)
                        {
                            var storeInfs = FindNewDriverStoreInfs(new HashSet<string>(), request.Driver);
                            var bestInf = storeInfs.FirstOrDefault();
                            if (bestInf != null)
                            {
                                _log.Info($"Recovery: pnputil /add-driver /install with DriverStore INF '{bestInf}'");
                                progress?.Report("Associando driver ao dispositivo USB via DriverStore...");
                                await InstallViaPnpUtilAsync(bestInf, ct);
                                await _printerService.RestartSpoolerAsync(ct);
                                await Task.Delay(6000, ct);
                                discoveredUsbPort = await _printerService.FindBestUsbPortAsync(ct)
                                    ?? await _printerService.FindAnyUsbPrinterPortAsync(ct)
                                    ?? await _printerService.FindNewPortSinceSnapshotAsync(portsBeforeInstall, ct)
                                    ?? await _printerService.FindUsbPortFromRegistryAsync(ct)
                                    ?? await _printerService.FindPortFromNewPrinterAsync(printersBeforeInstall, ct);
                                if (discoveredUsbPort != null)
                                    _log.Info($"Recovery DriverStore INF + Spooler restart: port found '{discoveredUsbPort}'");
                            }
                        }

                        // 2b) Try built-in usbprint.inf to force usbprint.sys binding.
                        if (discoveredUsbPort == null)
                        {
                            var winInf = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                "inf", "usbprint.inf");
                            if (File.Exists(winInf))
                            {
                                _log.Info("Recovery: pnputil with built-in usbprint.inf");
                                await InstallViaPnpUtilAsync(winInf, ct);
                                await _printerService.RestartSpoolerAsync(ct);
                                await Task.Delay(6000, ct);
                                discoveredUsbPort = await _printerService.FindBestUsbPortAsync(ct)
                                    ?? await _printerService.FindNewPortSinceSnapshotAsync(portsBeforeInstall, ct)
                                    ?? await _printerService.FindUsbPortFromRegistryAsync(ct)
                                    ?? await _printerService.FindPortFromNewPrinterAsync(printersBeforeInstall, ct);
                                if (discoveredUsbPort != null)
                                    _log.Info($"Recovery usbprint.inf: port found '{discoveredUsbPort}'");
                            }
                        }

                        // 3) Broad USB re-enumeration (disable+enable all USB printer/unknown devices).
                        if (discoveredUsbPort == null)
                        {
                            await _printerService.ReEnumerateUsbPrinterDevicesAsync(ct);
                            await Task.Delay(3000, ct);
                        }
                        await _printerService.RestartSpoolerAsync(ct);

                        for (int p = 0; p < 4 && discoveredUsbPort == null; p++)
                        {
                            await Task.Delay(3000, ct);
                            discoveredUsbPort = await _printerService.FindBestUsbPortAsync(ct)
                                ?? await _printerService.FindNewPortSinceSnapshotAsync(portsBeforeInstall, ct)
                                ?? await _printerService.FindUsbPortFromRegistryAsync(ct);
                            if (discoveredUsbPort == null)
                            {
                                var (_, pnpPort2) = await _printerService.FindAutoInstalledPrinterInfoAsync(
                                    request.Driver.Manufacturer, request.Driver.Model, ct);
                                if (!string.IsNullOrEmpty(pnpPort2) &&
                                    !pnpPort2.StartsWith("IP_",  StringComparison.OrdinalIgnoreCase) &&
                                    !pnpPort2.StartsWith("LPR_", StringComparison.OrdinalIgnoreCase) &&
                                    !pnpPort2.Equals("FILE:", StringComparison.OrdinalIgnoreCase) &&
                                    !pnpPort2.Equals("PORTPROMPT:", StringComparison.OrdinalIgnoreCase))
                                    discoveredUsbPort = pnpPort2;
                            }
                            discoveredUsbPort ??= await _printerService.FindAnyUsbPrinterPortAsync(ct);
                            discoveredUsbPort ??= await _printerService.FindNewPortSinceSnapshotAsync(portsBeforeInstall, ct);
                            discoveredUsbPort ??= await _printerService.FindUsbPortFromRegistryAsync(ct);
                            discoveredUsbPort ??= await _printerService.FindPortFromNewPrinterAsync(printersBeforeInstall, ct);
                            if (discoveredUsbPort == null &&
                                !string.IsNullOrEmpty(request.Driver.VendorId) &&
                                !string.IsNullOrEmpty(request.Driver.ProductId))
                                discoveredUsbPort = await _printerService.FindDevicePortByVidPidAsync(
                                    request.Driver.VendorId!, request.Driver.ProductId!, ct);
                        }
                    }

                    // Last resort: registry check, diff, then well-known USB port names
                    if (discoveredUsbPort == null)
                    {
                        discoveredUsbPort = await _printerService.FindUsbPortFromRegistryAsync(ct)
                            ?? await _printerService.FindNewPortSinceSnapshotAsync(portsBeforeInstall, ct);
                    }
                    if (discoveredUsbPort == null)
                    {
                        _log.Warning("USB port not auto-detected. Probing all available Spooler ports...");
                        var availablePorts = await _printerService.GetAvailablePortsAsync(ct);
                        _log.Info($"All Spooler ports: [{string.Join(", ", availablePorts)}]");

                        // Priority 1: well-known USB/DOT4 names that should always work
                        foreach (var wellKnown in new[] { "USB001", "USB002", "USB003", "DOT4USB001", "DOT4USB002" })
                        {
                            if (availablePorts.Contains(wellKnown, StringComparer.OrdinalIgnoreCase))
                            {
                                discoveredUsbPort = wellKnown;
                                _log.Info($"USB port fallback (well-known): '{wellKnown}'");
                                break;
                            }
                        }

                        // Priority 2: any port that appeared since the pre-install snapshot
                        // (catches vendor-specific port names like GA001, GERTECUSB001, etc.)
                        if (discoveredUsbPort == null && portsBeforeInstall.Count > 0)
                        {
                            var newPort = availablePorts.FirstOrDefault(p =>
                                !portsBeforeInstall.Contains(p, StringComparer.OrdinalIgnoreCase) &&
                                !p.StartsWith("IP_",      StringComparison.OrdinalIgnoreCase) &&
                                !p.StartsWith("LPR_",     StringComparison.OrdinalIgnoreCase) &&
                                !p.StartsWith("COM",      StringComparison.OrdinalIgnoreCase) &&
                                !p.StartsWith("LPT",      StringComparison.OrdinalIgnoreCase) &&
                                !p.Equals("FILE:",        StringComparison.OrdinalIgnoreCase) &&
                                !p.Equals("PORTPROMPT:", StringComparison.OrdinalIgnoreCase));
                            if (newPort != null)
                            {
                                discoveredUsbPort = newPort;
                                _log.Info($"USB port fallback (new since snapshot): '{newPort}'");
                            }
                        }
                    }

                    // Nuclear option: if usbprint.sys IS already bound to the device (detectable
                    // via HKLM\...\Enum\USBPRINT) but the Spooler just hasn't created the port
                    // entry yet, trigger a dedicated Spooler restart with an extended wait window.
                    if (discoveredUsbPort == null && !string.IsNullOrEmpty(request.Driver.VendorId))
                    {
                        progress?.Report("Aguardando reconhecimento USB pelo Windows...");
                        var forcedPort = await _printerService.ForceUsbPortFromUsbPrintAsync(
                            request.Driver.VendorId!, request.Driver.ProductId ?? "", ct);
                        if (forcedPort != null)
                        {
                            _log.Info($"Nuclear option: ForceUsbPortFromUsbPrint returned '{forcedPort}'");
                            discoveredUsbPort = forcedPort;
                        }
                    }

                    // If still null after everything: do one final VID/PID cycle + long Spooler restart.
                    // This covers edge cases where the previous Spooler restart didn't give enough time.
                    if (discoveredUsbPort == null && !string.IsNullOrEmpty(request.Driver.VendorId))
                    {
                        _log.Info("Final attempt: VID/PID cycle + 10s Spooler restart...");
                        progress?.Report("Última tentativa de reconhecimento USB...");
                        await _printerService.ReEnumerateDeviceByVidPidAsync(
                            request.Driver.VendorId!, request.Driver.ProductId ?? "", ct);
                        await _printerService.RestartSpoolerAsync(ct);
                        await Task.Delay(10000, ct);
                        discoveredUsbPort = await _printerService.FindDevicePortByVidPidAsync(
                                request.Driver.VendorId!, request.Driver.ProductId ?? "", ct)
                            ?? await _printerService.FindBestUsbPortAsync(ct)
                            ?? await _printerService.FindUsbPortFromRegistryAsync(ct)
                            ?? await _printerService.FindNewPortSinceSnapshotAsync(portsBeforeInstall, ct)
                            ?? await _printerService.FindAnyUsbPrinterPortAsync(ct)
                            ?? await _printerService.FindPortFromNewPrinterAsync(printersBeforeInstall, ct);
                        if (discoveredUsbPort != null)
                        {
                            await _printerService.EnsurePortRegisteredAsync(discoveredUsbPort, ct);
                            _log.Info($"Final attempt found port: '{discoveredUsbPort}'");
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

            // Validate the selected USB port is actually registered with the Print Spooler.
            // A port found via the USB Monitor registry may not yet appear in Win32_PrinterPort
            // — there is a race between the registry being updated and the Spooler enumerating
            // the port. We wait up to ~18 s (6×3 s), then do a forced Spooler restart.
            // Without this wait, AddPrinterAsync fails for ALL driver names (the error looks like
            // "driver not accepted" but the real cause is "port does not exist in Spooler").
            if (request.ConnectionType == ConnectionType.USB && discoveredUsbPort != null)
            {
                var spoolerPorts = await _printerService.GetAvailablePortsAsync(ct);

                // COMx / generic local ports (CDC virtual-serial thermal printers) are not
                // created by the USB Monitor — waiting for it is pointless. Register the port
                // directly with the spooler so Add-Printer accepts it.
                bool isMonitorManaged =
                    portName.StartsWith("USB",  StringComparison.OrdinalIgnoreCase) ||
                    portName.StartsWith("DOT4", StringComparison.OrdinalIgnoreCase) ||
                    portName.StartsWith("WSD",  StringComparison.OrdinalIgnoreCase);
                if (!isMonitorManaged && !spoolerPorts.Contains(portName, StringComparer.OrdinalIgnoreCase))
                {
                    _log.Info($"Port '{portName}' is a local/COM port — registering it with the spooler.");
                    progress?.Report("Registrando porta da impressora no Windows...");
                    if (await _printerService.EnsurePortRegisteredAsync(portName, ct))
                    {
                        steps.Add($"Porta registrada: {portName}");
                        spoolerPorts = await _printerService.GetAvailablePortsAsync(ct);
                    }
                }

                if (isMonitorManaged && !spoolerPorts.Contains(portName, StringComparer.OrdinalIgnoreCase))
                {
                    _log.Warning($"Port '{portName}' not yet in Spooler — waiting for USB Monitor sync...");
                    progress?.Report("Aguardando porta USB ser registrada no Windows...");
                    string? confirmedPort = null;
                    for (int w = 0; w < 6 && confirmedPort == null; w++)
                    {
                        if (w > 0) await Task.Delay(3000, ct);
                        confirmedPort = await _printerService.FindBestUsbPortAsync(ct)
                            ?? await _printerService.FindNewPortSinceSnapshotAsync(portsBeforeInstall, ct);
                        if (confirmedPort == null)
                        {
                            // Also re-check whether the original portName is now visible
                            var updated = await _printerService.GetAvailablePortsAsync(ct);
                            if (updated.Contains(portName, StringComparer.OrdinalIgnoreCase))
                                confirmedPort = portName;
                        }
                    }
                    if (confirmedPort == null)
                    {
                        _log.Info("USB port still absent after polling — forcing final Spooler restart to flush USB Monitor.");
                        progress?.Report("Reiniciando serviço de impressão para registrar porta USB...");
                        await _printerService.RestartSpoolerAsync(ct);
                        await Task.Delay(5000, ct);
                        confirmedPort = await _printerService.FindBestUsbPortAsync(ct)
                            ?? await _printerService.FindNewPortSinceSnapshotAsync(portsBeforeInstall, ct);
                        if (confirmedPort == null)
                        {
                            // Check original portName one last time
                            var finalPorts = await _printerService.GetAvailablePortsAsync(ct);
                            if (finalPorts.Contains(portName, StringComparer.OrdinalIgnoreCase))
                                confirmedPort = portName;
                        }
                    }
                    if (confirmedPort != null)
                    {
                        portName = confirmedPort;
                        discoveredUsbPort = confirmedPort;
                        _log.Info($"USB port confirmed in Spooler: '{portName}'");
                        steps.Add($"Porta USB confirmada: {portName}");
                    }
                    else
                    {
                        _log.Warning($"USB port absent from Spooler after all waits — proceeding but AddPrinterAsync may fail.");
                        discoveredUsbPort = null; // mark as unconfirmed so the error path shows the right message
                    }
                }
            }

            // Step 3: Create the printer (always fresh — delete any existing ghost first)
            progress?.Report("Configurando impressora no Windows...");

            // The user explicitly clicked Install, so we always want a clean printer with the
            // correct driver and port. Any existing printer with this name is either a ghost
            // (in the spooler but invisible in the UI) or an old install with possibly wrong
            // driver/port. Delete it unconditionally so the create path always runs.
            var printerExists = await _printerService.PrinterExistsAsync(request.PrinterName, ct);
            if (printerExists)
            {
                _log.Info($"Printer '{request.PrinterName}' already registered in spooler — removing for clean reinstall.");
                progress?.Report("Removendo impressora existente para reinstalação limpa...");
                await _printerService.DeletePrinterAsync(request.PrinterName, ct);
                await Task.Delay(1500, ct);
            }

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
                        // Any non-system driver that appeared after install (unknown name) goes first,
                        // then all known candidate names.
                        // System/Microsoft drivers are explicitly excluded — they can appear as
                        // "new" when the pre-install snapshot was incomplete due to a slow spooler.
                        var freshNew = driversAtInstallStart.Count > 0
                            ? freshDrivers
                                .Except(driversAtInstallStart, StringComparer.OrdinalIgnoreCase)
                                .Where(d => !IsSystemDriver(d))
                                .ToList()
                            : freshDrivers.Where(d => !IsSystemDriver(d)).ToList();
                        var knownCandidates = request.Driver.AllDriverNames()
                            .Where(n => !string.IsNullOrWhiteSpace(n) && !IsSystemDriver(n))
                            .ToList();
                        driverNamesToTry = [..freshNew, ..knownCandidates];
                        _log.Info($"Probing {driverNamesToTry.Count} driver names ({freshNew.Count} newly detected + {knownCandidates.Count} known): [{string.Join(", ", driverNamesToTry)}]");
                    }
                }

                if (driverNamesToTry.Count == 0)
                {
                    _log.Error("No driver names to try — driver detection produced no usable names.");
                    var allVisible = await _printerService.GetInstalledDriversAsync(ct);
                    return InstallResult.Fail(
                        "Não foi possível detectar o driver instalado pelo Windows.",
                        $"Drivers visíveis: [{string.Join(", ", allVisible.Take(15))}]\n" +
                        "Verifique o nome do driver em: Gerenciamento de Impressão → Drivers.",
                        steps);
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
                    // Last resort: try every installed non-system driver not already in the candidate list.
                    // This handles cases where the actual Windows driver name is completely unknown —
                    // not in the catalog and not discoverable via diff (e.g. driver was already present
                    // on this machine under a different name, or the snapshot was taken on a restarting spooler).
                    var allNow = await _printerService.GetInstalledDriversAsync(ct);
                    var untried = allNow
                        .Where(d => !IsSystemDriver(d) &&
                                    !driverNamesToTry.Contains(d, StringComparer.OrdinalIgnoreCase))
                        .ToList();

                    if (untried.Count > 0)
                    {
                        _log.Info($"Last resort: trying {untried.Count} additional drivers not in catalog: [{string.Join(", ", untried)}]");
                        progress?.Report("Tentando drivers alternativos instalados...");
                        foreach (var candidateName in untried)
                        {
                            _log.Info($"Last resort AddPrinterAsync: driver='{candidateName}'");
                            if (await _printerService.AddPrinterAsync(request.PrinterName, candidateName, portName, ct))
                            {
                                printerAdded = true;
                                usedDriverName = candidateName;
                                _log.Info($"Last resort succeeded with driver: '{candidateName}'");
                                break;
                            }
                        }
                    }
                }

                // Genuine last-ditch retry: the physical device may have bound its port only
                // now (late CDC/usbprint enumeration). Ask the device directly one more time,
                // register that port, and retry every driver name on it before giving up.
                if (!printerAdded && request.ConnectionType == ConnectionType.USB &&
                    !string.IsNullOrEmpty(request.Driver.VendorId) &&
                    !string.IsNullOrEmpty(request.Driver.ProductId))
                {
                    var lateDevicePort = await _printerService.FindDevicePortByVidPidAsync(
                        request.Driver.VendorId!, request.Driver.ProductId!, ct);
                    if (!string.IsNullOrEmpty(lateDevicePort) &&
                        !lateDevicePort.Equals(portName, StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Info($"Last-ditch: device now reports port '{lateDevicePort}' — registering and retrying.");
                        progress?.Report("Última tentativa na porta do dispositivo...");
                        await _printerService.EnsurePortRegisteredAsync(lateDevicePort, ct);
                        foreach (var candidateName in driverNamesToTry)
                        {
                            if (await _printerService.AddPrinterAsync(request.PrinterName, candidateName, lateDevicePort, ct))
                            {
                                printerAdded = true;
                                usedDriverName = candidateName;
                                portName = lateDevicePort;
                                discoveredUsbPort = lateDevicePort;
                                _log.Info($"Last-ditch succeeded: driver='{candidateName}' port='{lateDevicePort}'");
                                break;
                            }
                        }
                    }
                }

                // Guaranteed final fallback: no vendor driver could be used, but a real port
                // exists — so create the printer with the built-in "Generic / Text Only" driver.
                // For an ESC/POS thermal printer this prints receipts/text fine; the queue can be
                // switched to the vendor driver later. The user asked: if one driver fails, pull
                // the other so the printer is created no matter what.
                if (!printerAdded && !string.IsNullOrEmpty(portName))
                {
                    var portsNow = await _printerService.GetAvailablePortsAsync(ct);
                    if (portsNow.Contains(portName, StringComparer.OrdinalIgnoreCase))
                    {
                        progress?.Report("Criando impressora com driver genérico (Generic / Text Only)...");
                        if (await _printerService.EnsureGenericTextDriverAsync(ct) &&
                            await _printerService.AddPrinterAsync(request.PrinterName, "Generic / Text Only", portName, ct))
                        {
                            printerAdded = true;
                            usedDriverName = "Generic / Text Only";
                            steps.Add("Impressora criada com driver genérico (Generic / Text Only) — troque pelo driver do fabricante depois, se desejar.");
                            _log.Info($"Fallback succeeded: printer created with 'Generic / Text Only' on port '{portName}'");
                        }
                    }
                }

                if (!printerAdded)
                {
                    _log.Error($"AddPrinterAsync failed for all candidates. port='{portName}' tried=[{string.Join(", ", driverNamesToTry)}]");

                    // Diagnose root cause: a missing port causes Add-Printer to fail for ALL
                    // driver names, masquerading as "driver not accepted". Check the port first.
                    if (request.ConnectionType == ConnectionType.USB)
                    {
                        var currentPorts = await _printerService.GetAvailablePortsAsync(ct);
                        bool portMissing = !currentPorts.Contains(portName, StringComparer.OrdinalIgnoreCase);
                        if (portMissing || discoveredUsbPort == null)
                        {
                            _log.Error($"Root cause: port '{portName}' absent from Spooler. Available: [{string.Join(", ", currentPorts.Take(10))}]");
                            // Gather live device state + a full dump of connected USB devices so
                            // the failure is fully diagnosable without physical access to the PC.
                            var diag = await _printerService.GetUsbDeviceDiagnosticsAsync(
                                request.Driver.VendorId ?? "", request.Driver.ProductId ?? "", ct);
                            var deviceList = await _printerService.ListConnectedUsbDevicesAsync(ct);
                            _log.Error($"Device diagnostics: {diag}");
                            _log.Error($"Connected USB devices:\n{deviceList}");
                            return InstallResult.Fail(
                                "Porta USB não encontrada no Windows.",
                                $"{diag}\n\n" +
                                $"Dispositivos USB conectados:\n{deviceList}\n\n" +
                                "Verifique se a impressora está conectada e ligada. Desconecte e reconecte o cabo USB e tente novamente. " +
                                "Se o problema persistir, o driver do fabricante pode não ter vinculado a impressora ao Windows.",
                                steps);
                        }
                    }

                    return InstallResult.Fail(
                        "Falha ao criar impressora no Windows.",
                        $"Nenhum driver foi aceito pelo Windows. Nomes tentados: {string.Join(", ", driverNamesToTry)}\n" +
                        "Verifique o nome exato do driver em: Painel de Controle → Gerenciamento de Impressão → Drivers.",
                        steps);
                }

                steps.Add($"Impressora criada: {request.PrinterName} (driver: {usedDriverName})");
                _log.Info($"Printer created: {request.PrinterName} with driver '{usedDriverName}'");

                // Verify the printer was actually registered by the Spooler.
                // AddPrinterAsync can return success (exit code 0) while the Spooler
                // processes the request asynchronously — a brief wait + check catches this.
                await Task.Delay(1000, ct);
                var verified = await _printerService.PrinterExistsAsync(request.PrinterName, ct);
                if (!verified)
                {
                    _log.Error($"Post-install verification failed: '{request.PrinterName}' not found after creation.");
                    return InstallResult.Fail(
                        $"Impressora '{request.PrinterName}' não foi encontrada no Windows após a instalação.",
                        "O Windows pode ter rejeitado a criação silenciosamente. Tente reinstalar o driver manualmente.",
                        steps);
                }
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

            // Auto-share the printer so LPD can expose it to other PCs on the network.
            // Share name: printer name sanitized (letters/digits/hyphens only, max 30 chars).
            var shareName = ToShareName(request.PrinterName);
            _ = _printerService.SharePrinterAsync(request.PrinterName, shareName, CancellationToken.None);

            progress?.Report("Instalação concluída com sucesso!");
            _log.Info($"Installation complete for {request.PrinterName}, sharing as '{shareName}'");

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
        if (string.IsNullOrWhiteSpace(shareName))
            return InstallResult.Fail(
                "Informe o nome do compartilhamento da impressora.",
                "Clique em Buscar e depois preencha o campo 'Nome do compartilhamento'.", steps);

        // Install the correct driver locally before connecting.
        // TryInstallSharedDriverAsync finds the driver using multiple strategies and
        // returns the matched DriverInfo so ResolveDriverForSharedAsync can use it.
        var sharedDriverInfo = await TryInstallSharedDriverAsync(request, progress, ct);
        if (sharedDriverInfo != null && string.IsNullOrWhiteSpace(request.Driver?.DriverName))
            request.Driver = sharedDriverInfo;

        // ── Path 1: LPD/LPR (primary) ─────────────────────────────────────────────
        // LPD (port 515) requires no authentication — works regardless of Windows
        // account differences between PCs. Our app enables LPD automatically on install.

        progress?.Report($"Verificando LPD em {host}...");
        bool lpdUp = await _printerService.IsLpdAvailableAsync(host, ct);

        if (lpdUp)
        {
            progress?.Report("LPD disponível — criando porta LPR...");
            var lprPortName = $"LPR_{host.Replace('.', '_')}_{shareName}";
            bool portCreated = await _printerService.CreateLprPortAsync(lprPortName, host, shareName, ct);

            if (portCreated)
            {
                steps.Add($"Porta LPR criada: {lprPortName}");

                var driverName = await ResolveDriverForSharedAsync(request, ct);
                var printerDisplayName = string.IsNullOrWhiteSpace(request.PrinterName) ? shareName : request.PrinterName;

                progress?.Report($"Adicionando impressora '{printerDisplayName}' (driver: {driverName})...");
                bool added = await _printerService.AddPrinterAsync(printerDisplayName, driverName, lprPortName, ct);

                if (added)
                {
                    steps.Add($"Impressora criada via LPD: '{printerDisplayName}' driver='{driverName}'");
                    if (request.SetAsDefault)
                    {
                        await _printerService.SetDefaultPrinterAsync(printerDisplayName, ct);
                        steps.Add("Definida como impressora padrão.");
                    }
                    progress?.Report("Impressora instalada via LPD com sucesso!");
                    return InstallResult.Ok(
                        $"Impressora '{printerDisplayName}' instalada via LPD com sucesso!",
                        printerDisplayName, steps);
                }

                _log.Warning($"LPD port created but AddPrinterAsync failed (driver='{driverName}'). Falling back to SMB.");
            }
            else
            {
                _log.Warning("CreateLprPortAsync failed. Falling back to SMB.");
            }
        }
        else
        {
            _log.Info($"LPD not available on {host}:515. Trying SMB.");
        }

        // ── Path 2: SMB fallback ──────────────────────────────────────────────────
        // Used when LPD is not available (host doesn't have PrinterMode installed yet)
        // or when the LPR port creation failed.

        var connectionName = $@"\\{host}\{shareName}";
        progress?.Report($"Tentando conexão SMB: {connectionName}...");
        _log.Info($"Trying SMB: {connectionName}");

        var (smbOk, smbError) = await _printerService.AddSharedPrinterInternalAsync(connectionName, ct);
        if (smbOk)
        {
            steps.Add($"Conectado via SMB: {connectionName}");
            if (request.SetAsDefault)
            {
                await _printerService.SetDefaultPrinterAsync(shareName, ct);
                steps.Add("Definida como impressora padrão.");
            }
            progress?.Report("Impressora compartilhada conectada com sucesso!");
            return InstallResult.Ok($"Impressora conectada: {connectionName}", request.PrinterName, steps);
        }

        var errorDetail = string.IsNullOrEmpty(smbError) ? "Acesso negado." : smbError;
        _log.Warning($"SMB also failed for '{connectionName}': {errorDetail}");

        return InstallResult.Fail(
            $"Não foi possível conectar à impressora em '{host}'.",
            $"LPD (porta 515): {(lpdUp ? "porta aberta mas falhou ao criar porta LPR" : "não respondeu")}.\n" +
            $"SMB: {errorDetail}\n\n" +
            $"Certifique-se que o PrinterMode está instalado e a impressora instalada no computador '{host}'. " +
            $"O LPD é ativado automaticamente durante a instalação.",
            steps);
    }

    private async Task<string> ResolveDriverForSharedAsync(InstallRequest request, CancellationToken ct)
    {
        var allInstalled = await _printerService.GetInstalledDriversAsync(ct);

        // 1. If we have the matched DriverInfo (set by TryInstallSharedDriverAsync), use it
        //    with fuzzy matching — handles cases where the installed name differs slightly.
        if (request.Driver != null && !string.IsNullOrWhiteSpace(request.Driver.DriverName))
        {
            var fuzzy = ResolveActualDriverName(request.Driver, allInstalled);
            if (fuzzy != null) return fuzzy;
        }

        // 2. Exact match against the driver name reported by PC-A
        if (!string.IsNullOrWhiteSpace(request.SharedDriverName))
        {
            var remoteMatch = allInstalled.FirstOrDefault(d =>
                d.Equals(request.SharedDriverName, StringComparison.OrdinalIgnoreCase));
            if (remoteMatch != null) return remoteMatch;
        }

        // 3. First non-system driver as last resort
        var nonSystem = allInstalled.FirstOrDefault(d =>
            !d.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) &&
            !d.Contains("OneNote", StringComparison.OrdinalIgnoreCase) &&
            !d.Contains("Fax", StringComparison.OrdinalIgnoreCase) &&
            !d.Contains("XPS", StringComparison.OrdinalIgnoreCase) &&
            !d.Contains("PDF", StringComparison.OrdinalIgnoreCase));

        return nonSystem ?? "Generic / Text Only";
    }

    // Finds the matching DriverInfo in the local repository using multiple strategies,
    // runs its silent installer if the driver is not yet installed, and returns the
    // DriverInfo so the caller can pass it to ResolveDriverForSharedAsync.
    private async Task<DriverInfo?> TryInstallSharedDriverAsync(
        InstallRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        var driverName  = request.SharedDriverName;
        var displayName = request.SharedDisplayName;
        var shareName   = request.SharedPrinterName ?? "";

        var allDrivers = await _repository.GetAllDriversAsync();

        DriverInfo? match = null;

        // Strategy 0: the model the user explicitly selected in the UI for this shared
        // printer. This is the most reliable signal — it is the driver they intend to use —
        // so honor it first and install exactly that driver on this (client) computer.
        if (request.Driver != null &&
            (!string.IsNullOrWhiteSpace(request.Driver.Id) ||
             !string.IsNullOrWhiteSpace(request.Driver.InstallerExe)))
        {
            match = allDrivers.FirstOrDefault(d =>
                        d.Id.Equals(request.Driver.Id, StringComparison.OrdinalIgnoreCase))
                    ?? (request.Driver.HasInstaller ? request.Driver : null);
        }

        // Strategy 1: match by Windows driver name reported from PC-A
        if (match == null && !string.IsNullOrEmpty(driverName))
            match = allDrivers.FirstOrDefault(d =>
                d.AllDriverNames().Any(n => n.Equals(driverName, StringComparison.OrdinalIgnoreCase)));

        // Strategy 2: match by display name from discovery (e.g. "Gertec G250")
        if (match == null && !string.IsNullOrEmpty(displayName))
            match = allDrivers.FirstOrDefault(d =>
                d.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase));

        // Strategy 3: match shareName against DisplayName with spaces removed
        // ToShareName("Gertec G250") == "GertecG250" == "Gertec G250".Replace(" ","")
        if (match == null && !string.IsNullOrEmpty(shareName))
        {
            var shareNorm = shareName.ToLowerInvariant();
            match = allDrivers.FirstOrDefault(d =>
                d.DisplayName.Replace(" ", "").Equals(shareNorm, StringComparison.OrdinalIgnoreCase));
        }

        if (match == null)
        {
            _log.Info($"No matching driver found in repository for shared printer " +
                      $"(driverName='{driverName}', displayName='{displayName}', shareName='{shareName}').");
            return null;
        }

        // Check if already installed
        var allInstalled = await _printerService.GetInstalledDriversAsync(ct);
        bool alreadyInstalled = allInstalled.Any(d =>
            match.AllDriverNames().Any(n => n.Equals(d, StringComparison.OrdinalIgnoreCase)));

        if (alreadyInstalled)
        {
            _log.Info($"Driver '{match.DisplayName}' already installed locally.");
            return match;
        }

        if (!match.HasInstaller || !_repository.DriverFilesExist(match))
        {
            _log.Info($"Driver '{match.DisplayName}' found in repo but no installer available.");
            return match; // still return so ResolveDriverForSharedAsync can use its DriverName
        }

        var exePath = _repository.ResolveInstallerPath(match);
        if (exePath == null) return match;

        progress?.Report($"Instalando driver '{match.DisplayName}'...");
        _log.Info($"Installing driver for shared printer: '{match.DisplayName}'");

        var silentArgs = match.InstallerArgs;
        if (string.IsNullOrEmpty(silentArgs))
            silentArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = silentArgs,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync(ct);
            _log.Info($"Shared driver installer exit: {proc.ExitCode}");

            if (proc.ExitCode == 0 || proc.ExitCode == 3010 || proc.ExitCode == 1641)
                await Task.Delay(3000, ct);
        }
        catch (Exception ex)
        {
            _log.Warning($"Shared driver install failed (non-fatal): {ex.Message}");
        }

        return match;
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

            // Priority 0 (highest): INF content contains the exact VID/PID of the device.
            // This catches INFs with generic names (e.g., "thermal_pos.inf") that would be
            // missed by the name/class checks below. A real installer can stage its INF with
            // any file name — VID/PID matching is the only reliable indicator.
            if (!string.IsNullOrEmpty(driver.VendorId) && !string.IsNullOrEmpty(driver.ProductId))
            {
                var byVidPid = newFiles.Where(f =>
                {
                    try
                    {
                        var content = File.ReadAllText(f, System.Text.Encoding.Latin1);
                        return content.IndexOf($"VID_{driver.VendorId}", StringComparison.OrdinalIgnoreCase) >= 0
                            && content.IndexOf($"PID_{driver.ProductId}", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    catch { return false; }
                }).ToList();
                if (byVidPid.Count > 0) return byVidPid;
            }

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
            // Drain stdout and stderr concurrently before waiting for exit to avoid
            // pipe deadlock when pnputil writes more than the 4 KB pipe buffer.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var output = await stdoutTask;
            await stderrTask;
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

    // Returns true for built-in Windows printer drivers that should never be treated
    // as a "newly installed" third-party driver during diff-based detection.
    private static bool IsSystemDriver(string name) =>
        name.Contains("Microsoft",     StringComparison.OrdinalIgnoreCase) ||
        name.Contains("OneNote",       StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Fax",           StringComparison.OrdinalIgnoreCase) ||
        name.Contains("XPS",           StringComparison.OrdinalIgnoreCase) ||
        name.Contains("PDF",           StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Remote Desktop", StringComparison.OrdinalIgnoreCase);

    private static string ToShareName(string printerName)
    {
        var name = new string(printerName
            .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
            .ToArray());
        if (name.Length > 30) name = name[..30];
        return name.Length > 0 ? name : "Impressora";
    }
}
