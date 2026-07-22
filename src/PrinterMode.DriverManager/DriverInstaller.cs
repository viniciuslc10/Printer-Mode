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
            bool usbInterfacePathAttempted = false;           // surfaced in the failure message
            string? usbInterfacePathFound = null;             // so the user sees this, not just the log
            string? usbPortRegisterError = null;              // the REAL Windows error, not a guess

            // ── Resolve the REAL connected device BEFORE the driver install step ───────────
            // The catalog VID/PID can be a wrong placeholder (confirmed: GertecG250.inf's
            // "20D1/7008" doesn't match any connected device — the real G250 enumerates as
            // VID_1753&PID_0800). If this resolution only happened later (at port-creation
            // time), every VID/PID-dependent step during driver install — DriverStore INF
            // search by content, device re-enumeration — would still target the wrong id.
            // Resolving here, first, means the ENTIRE install (driver + port) uses the real ids.
            if (request.ConnectionType == ConnectionType.USB)
            {
                var earlyHints = new List<string> { request.Driver.Manufacturer, request.Driver.Model };
                earlyHints.AddRange(request.Driver.AllDriverNames());
                var earlyResolved = await _printerService.ResolvePrinterUsbDeviceAsync(
                    earlyHints, request.Driver.VendorId, request.Driver.ProductId, ct);
                if (earlyResolved != null && !string.IsNullOrEmpty(earlyResolved.Vid))
                {
                    if (!earlyResolved.Vid.Equals(request.Driver.VendorId, StringComparison.OrdinalIgnoreCase))
                        _log.Info($"Catalog VID/PID corrected: {request.Driver.VendorId}/{request.Driver.ProductId} " +
                                  $"-> {earlyResolved.Vid}/{earlyResolved.Pid} (device: '{earlyResolved.FriendlyName}')");
                    request.Driver.VendorId = earlyResolved.Vid;
                    request.Driver.ProductId = earlyResolved.Pid;
                }
            }

            // ── Register the real print driver INF shipped in the Repository, if present ──
            // Vendor silent installers frequently only stage the USB device driver / service
            // and never call Add-PrinterDriver themselves — confirmed on the Gertec G250: the
            // "GA-Printer Driver" program installs fully, yet Get-PrinterDriver never shows it.
            // When the catalog ships a real (non-placeholder) INF for this model, register it
            // directly instead of relying on the installer EXE + DriverStore/ProgramFiles search
            // to happen to find it. This result takes priority over any name guessed later.
            string? repoRegisteredDriverName = null;
            var repoInfPath = ResolveRealInfPath(request.Driver);
            if (repoInfPath != null)
            {
                progress?.Report("Registrando driver de impressão oficial...");

                // Some catalog entries ship a self-signed OEM certificate (e.g. Elgin i7/i9,
                // Bematech MP4200 — confirmed "CN=Printer", not chained to a public root).
                // Importing it first (idempotent — importing an already-trusted cert is a
                // harmless no-op) makes the package's already-valid catalog trusted, so the
                // normal pnputil/Add-PrinterDriver calls below succeed like any signed driver —
                // no unsigned-driver override needed for this family.
                if (!string.IsNullOrEmpty(request.Driver.DriverCertFile))
                {
                    var certPath = Path.Combine(Path.GetDirectoryName(repoInfPath)!, request.Driver.DriverCertFile);
                    if (await _printerService.TryTrustCertificateAsync(certPath, ct))
                        _log.Info($"Trusted OEM certificate '{certPath}'.");
                }

                // Stage via pnputil FIRST: Add-PrinterDriver on an unsigned OEM package (this
                // one has no .cat file) commonly fails unless the INF was already staged into
                // the DriverStore via pnputil /add-driver /install. Do this unconditionally,
                // then attempt the real Spooler registration.
                var (pnpOk, pnpOutput) = await InstallViaPnpUtilWithOutputAsync(repoInfPath, ct);

                var (registeredName, registerError) = await _printerService.TryRegisterPrintDriverFromInfWithReasonAsync(
                    repoInfPath, request.Driver.AllDriverNames().ToList(), ct);
                repoRegisteredDriverName = registeredName;

                // Confirmed root cause (real Windows text, not a guess): this specific package
                // has no digital signature at all, and pnputil/Add-PrinterDriver refuse that
                // headlessly with no silent override at all. Tried the classic printui.dll "/ia"
                // install-from-INF flow first (goes through the interactive Add Printer wizard
                // path) — confirmed in the field it returns instantly without ever showing a
                // dialog on current Windows, so it cannot be relied on. AddPrinterDriverEx
                // (winspool.drv), called directly with APD_INSTALL_WARNED_DRIVER, is the actual
                // documented Win32 mechanism for this: it installs an unsigned driver headlessly,
                // asserting the same consent a human would give by clicking "Install this driver
                // software anyway" — this app already has that consent (running elevated,
                // launched by the user). Only trigger it when the failure actually looks like a
                // missing-signature problem, and only when the catalog declares the data/
                // dependent files this driver needs.
                bool looksUnsigned = !pnpOk &&
                    (pnpOutput.Contains("assinatura", StringComparison.OrdinalIgnoreCase) ||
                     pnpOutput.Contains("signature", StringComparison.OrdinalIgnoreCase) ||
                     pnpOutput.Contains("signed", StringComparison.OrdinalIgnoreCase));

                if (repoRegisteredDriverName == null && looksUnsigned &&
                    !string.IsNullOrEmpty(request.Driver.DriverDataFile))
                {
                    var driverFileDir = Path.GetDirectoryName(repoInfPath)!;
                    var dataFilePath = Path.Combine(driverFileDir, request.Driver.DriverDataFile);
                    var dependentPaths = request.Driver.DriverDependentFiles
                        .Select(f => Path.Combine(driverFileDir, f)).ToList();

                    steps.Add("⚠ Driver sem assinatura digital — registrando com consentimento já concedido (não requer nenhuma tela nem clique)...");
                    _log.Info($"Unsigned driver detected ('{pnpOutput.Trim()}') — registering via AddPrinterDriverEx/APD_INSTALL_WARNED_DRIVER.");
                    progress?.Report("Registrando driver não assinado...");

                    var (win32Ok, win32Error) = await _printerService.TryRegisterUnsignedPrintDriverAsync(
                        request.Driver.DriverName, dataFilePath, dependentPaths, ct);
                    if (win32Ok)
                    {
                        repoRegisteredDriverName = request.Driver.DriverName;
                        _log.Info($"AddPrinterDriverEx succeeded for '{request.Driver.DriverName}'.");
                    }
                    else
                    {
                        registerError = win32Error ?? registerError;
                        _log.Warning($"AddPrinterDriverEx failed for '{request.Driver.DriverName}': {win32Error}");
                    }
                }

                if (repoRegisteredDriverName != null)
                {
                    steps.Add($"Driver de impressão registrado: {repoRegisteredDriverName}");
                    _log.Info($"Driver registered directly from repository INF '{repoInfPath}': '{repoRegisteredDriverName}'");
                }
                else
                {
                    // Surfaced to the user (not just the log) — this is the real Windows/PowerShell
                    // error, not a guess, so if it fails again we know exactly why instead of
                    // silently falling back to Generic / Text Only.
                    steps.Add($"⚠ Driver oficial não registrado ({registerError ?? "motivo desconhecido"}) — usando driver genérico como último recurso.");
                    _log.Warning($"Could not register driver directly from '{repoInfPath}': {registerError}");
                }
            }

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
                        // Timed explicitly: the vendor installer itself can take anywhere from a
                        // few seconds to well over a minute (file extraction, driver signing
                        // checks, DriverStore staging) — logging this tells us definitively
                        // whether a slow install is our code's overhead or the vendor EXE's own
                        // runtime, which we cannot speed up.
                        var installSw = System.Diagnostics.Stopwatch.StartNew();
                        using var proc = Process.Start(psi)!;
                        await proc.WaitForExitAsync(ct);
                        installSw.Stop();
                        _log.Info($"Silent installer exit: {proc.ExitCode} (took {installSw.Elapsed.TotalSeconds:F1}s)");

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
                        // step and exit the main EXE before that restart completes. Wait, then check
                        // whether the installer already created the port. Escalating recovery
                        // (device re-enumeration, DriverStore INF, usbprint.inf, broad re-enum) is
                        // NOT duplicated here — it happens once, in the USB port-creation step below,
                        // to avoid running the same multi-second mechanisms twice per install.
                        progress?.Report("Aguardando conclusão da instalação do driver...");
                        await Task.Delay(3000, ct);

                        discoveredUsbPort = await _printerService.FindBestUsbPortAsync(ct)
                            ?? await _printerService.FindNewPortSinceSnapshotAsync(portsBeforeInstall, ct)
                            ?? await _printerService.FindUsbPortFromRegistryAsync(ct)
                            ?? await _printerService.FindPortFromNewPrinterAsync(printersBeforeInstall, ct);
                        if (discoveredUsbPort != null)
                            _log.Info($"USB port found from installer's own setup: '{discoveredUsbPort}'");
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
                    // Gated ONLY on the driver name (its real purpose here) — NOT on the port.
                    // Port detection is handled thoroughly in Step 2; coupling this loop's exit
                    // to "port found" meant it ran to its FULL 7.5s every time for any device
                    // whose port isn't found this way, even after the driver resolved instantly.
                    for (int q = 0; q < 3 && resolvedSilent == null; q++)
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

                    // ── Phase 2: Restart Spooler only if Phase 1 didn't resolve the driver ──
                    // Gated ONLY on the driver name, same reasoning as Phase 1. If the driver
                    // is already known, this entire phase (and its Spooler restart) is skipped.
                    IReadOnlyList<string> driversAfterSilent = driversBefore;
                    if (resolvedSilent == null)
                    {
                        progress?.Report("Reiniciando serviço de impressão para carregar o driver...");
                        await _printerService.RestartSpoolerAsync(ct);

                        for (int poll = 0; poll < 3 && resolvedSilent == null; poll++)
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
                        string? printDriverInfPath = null;
                        foreach (var stagedInf in newInfFiles)
                        {
                            _log.Info($"Found staged inf in DriverStore: '{stagedInf}'");
                            var pnpOk = await InstallViaPnpUtilAsync(stagedInf, ct);
                            if (pnpOk)
                            {
                                printDriverInfPath = stagedInf;
                                break;
                            }
                        }

                        // If DriverStore had nothing, some vendor packages (confirmed here:
                        // "GA-Printer Driver" installs as a PROGRAM, not just staged into the
                        // DriverStore) drop their files under Program Files instead. Check there
                        // too — same targeted, name-matched search, no blind full-disk scan.
                        if (printDriverInfPath == null)
                        {
                            var pfInfs = FindPrintDriverInfInProgramFiles(request.Driver);
                            foreach (var pfInf in pfInfs)
                            {
                                _log.Info($"Found candidate print-driver inf in Program Files: '{pfInf}'");
                                if (await InstallViaPnpUtilAsync(pfInf, ct))
                                {
                                    printDriverInfPath = pfInf;
                                    break;
                                }
                            }
                        }

                        // pnputil only binds the driver to the matching HARDWARE — it does not
                        // register it as a Print Spooler driver, so its name never appears in
                        // Get-PrinterDriver / becomes usable by Add-Printer on its own. If the
                        // staged INF is a genuine printer-class package, explicitly register it
                        // under one of this model's known names so the branded name is usable
                        // instead of falling back to Generic/Text Only.
                        if (printDriverInfPath != null)
                        {
                            var registeredName = await _printerService.TryRegisterPrintDriverFromInfAsync(
                                printDriverInfPath, request.Driver.AllDriverNames().ToList(), ct);
                            if (registeredName != null)
                            {
                                resolvedSilent = registeredName;
                                _log.Info($"Registered print driver '{registeredName}' from '{printDriverInfPath}'.");
                            }
                        }

                        await Task.Delay(3000, ct);
                        var driversAfterStore = await _printerService.GetInstalledDriversAsync(ct);
                        resolvedSilent ??= ResolveActualDriverName(request.Driver, driversAfterStore)
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

            // The direct repository-INF registration is authoritative — it's a confirmed real
            // driver name, not a guess — so it overrides whatever name Step 1 detected/guessed.
            if (repoRegisteredDriverName != null)
                detectedDriverName = repoRegisteredDriverName;

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

                    // Normalize: "COM3" (config/mode form) vs "COM3:" (spooler port form).
                    var rawCom = request.PortName!.Trim().TrimEnd(':');
                    var spoolerCom = rawCom + ":";

                    // 1) Apply baud/parity/data settings to the COM port (uses the no-colon form).
                    await ConfigureSerialPortAsync(rawCom, request.SerialConfig, ct);
                    // 2) Register the COM port with the Print Spooler so Add-Printer accepts it.
                    //    (COM1:–COM4: usually exist already; higher COM numbers need registering.)
                    await _printerService.EnsurePortRegisteredAsync(spoolerCom, ct);

                    portName = spoolerCom;
                    portCreated = true;
                    steps.Add($"Porta serial configurada e registrada: {portName}");
                    _log.Info($"Serial: using registered spooler port '{portName}'");
                    break;

                case ConnectionType.USB:
                default:
                    // Fast, single-pass USB detection. Earlier versions ran up to 6 escalating
                    // recovery rounds (disable/enable, DriverStore pnputil, usbprint.inf, broad
                    // re-enum, full reinstall, 10s final attempt) — each with its own Spooler
                    // restart and multi-second polls, totaling minutes. Live diagnostics proved
                    // most of that is dead weight for a device that never becomes a standard
                    // printer-class USB port no matter how many times it's cycled. This keeps
                    // only the checks that can plausibly find/create a port, capped at ~15s total.

                    // 0) THE definitive fix for the recurring "Interface USBPRINT: não
                    //    verificada" symptom: discoveredUsbPort may already be non-null here,
                    //    set by Step 1's needsUsbPort check or Phase 1/2 (all BEFORE this switch
                    //    runs) — every one of those calls FindUsbPortFromRegistryAsync, which
                    //    reads the USB Monitor's registry key and can return a leftover entry
                    //    from an earlier attempt that never became a real Win32_PrinterPort.
                    //    An unverified value here silently skips step 5 below (the USBPRINT
                    //    interface mechanism, the one actually meant to solve this) since it only
                    //    runs while the port is still null. Verify against the LIVE Spooler port
                    //    list now, before anything else in this case runs.
                    if (discoveredUsbPort != null)
                    {
                        var livePortsAtStart = await _printerService.GetAvailablePortsAsync(ct);
                        if (!livePortsAtStart.Contains(discoveredUsbPort, StringComparer.OrdinalIgnoreCase))
                        {
                            _log.Warning($"Discarding unverified port '{discoveredUsbPort}' carried over from driver-install phase — not in live Spooler list.");
                            discoveredUsbPort = null;
                        }
                    }

                    // 1) User-selected port wins — instant, no detection needed.
                    if (!string.IsNullOrWhiteSpace(request.PortName))
                    {
                        await _printerService.EnsurePortRegisteredAsync(request.PortName!, ct);
                        discoveredUsbPort = request.PortName;
                        steps.Add($"Porta selecionada manualmente: {request.PortName}");
                    }

                    // 2) Resolve the ACTUAL connected device (instant) so the real VID/PID is
                    //    used below — the catalog's may be a placeholder. Adopt its COM port now
                    //    if it has one (CDC/virtual-serial printers).
                    progress?.Report("Localizando a impressora conectada...");
                    var deviceHints = new List<string> { request.Driver.Manufacturer, request.Driver.Model };
                    deviceHints.AddRange(request.Driver.AllDriverNames());
                    var resolvedDevice = await _printerService.ResolvePrinterUsbDeviceAsync(
                        deviceHints, request.Driver.VendorId, request.Driver.ProductId, ct);
                    if (resolvedDevice != null)
                    {
                        steps.Add($"Dispositivo detectado: {resolvedDevice.FriendlyName}");
                        if (!string.IsNullOrEmpty(resolvedDevice.Vid))
                        {
                            request.Driver.VendorId = resolvedDevice.Vid;
                            request.Driver.ProductId = resolvedDevice.Pid;
                        }
                        if (discoveredUsbPort == null && !string.IsNullOrEmpty(resolvedDevice.Port))
                        {
                            await _printerService.EnsurePortRegisteredAsync(resolvedDevice.Port, ct);
                            discoveredUsbPort = resolvedDevice.Port;
                        }
                    }

                    // 3) Adopt a printer the installer created for itself (instant WMI query).
                    if (discoveredUsbPort == null && printersBeforeInstall.Count > 0)
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
                        }
                    }

                    // 4) Standard USB port, if one already exists. VERIFIED against the live
                    //    Spooler port list before being accepted — FindUsbPortFromRegistryAsync
                    //    reads the USB Monitor's registry key, which can hold a leftover entry
                    //    from an earlier attempt that never actually became a real Win32_PrinterPort
                    //    entry. An unverified value here would silently skip step 5 below (it only
                    //    runs while discoveredUsbPort is null) — exactly the phantom-port bug
                    //    already fixed once for print monitors; the registry path has the same risk.
                    var candidatePort = await _printerService.FindBestUsbPortAsync(ct)
                        ?? await _printerService.FindUsbPortFromRegistryAsync(ct);
                    if (!string.IsNullOrEmpty(candidatePort))
                    {
                        var realPortsCheck = await _printerService.GetAvailablePortsAsync(ct);
                        if (realPortsCheck.Contains(candidatePort, StringComparer.OrdinalIgnoreCase))
                            discoveredUsbPort = candidatePort;
                        else
                            _log.Warning($"Discarded unverified candidate port '{candidatePort}' — not in live Spooler port list.");
                    }

                    // 5) PRIMARY MECHANISM — the raw USBPRINT device-interface path.
                    //    usbprint.sys registers this interface as soon as it binds the device
                    //    (confirmed here: service=usbprint, problem=0), INDEPENDENTLY of whether
                    //    the Spooler ever creates a USB001 port — which is exactly the step that
                    //    fails on this machine. We read the interface's device path straight from
                    //    the registry and register it as a Local Port, giving the printer a real,
                    //    writable port to the device WITHOUT needing the Spooler to create USB001.
                    if (discoveredUsbPort == null &&
                        !string.IsNullOrEmpty(request.Driver.VendorId) &&
                        !string.IsNullOrEmpty(request.Driver.ProductId))
                    {
                        usbInterfacePathAttempted = true;
                        usbInterfacePathFound = await _printerService.FindUsbPrintDeviceInterfacePathAsync(
                            request.Driver.VendorId!, request.Driver.ProductId!, ct);
                        if (!string.IsNullOrEmpty(usbInterfacePathFound))
                        {
                            // Try pre-registering it as a Local Port, but do NOT gate on success:
                            // Add-PrinterPort has its own (stricter) name validation and can reject
                            // a raw device-interface path outright, while Add-Printer -PortName
                            // (called later, in Step 3) creates an ad-hoc local port implicitly as
                            // part of printer creation and may accept the very same string. Blocking
                            // here on Add-PrinterPort's success meant Add-Printer was NEVER even
                            // attempted with this path — the real, decisive test never ran.
                            var (preRegistered, registerError) = await _printerService.TryRegisterPortWithReasonAsync(usbInterfacePathFound, ct);
                            discoveredUsbPort = usbInterfacePathFound;
                            usbPortRegisterError = registerError;
                            steps.Add(preRegistered
                                ? "Porta criada a partir da interface USBPRINT do dispositivo."
                                : $"Porta da interface USBPRINT: Add-PrinterPort falhou ({registerError}); Add-Printer tentará criá-la diretamente.");
                            _log.Info($"Using raw USBPRINT device interface path as port: '{usbInterfacePathFound}' (pre-registered={preRegistered}, error='{registerError}')");
                        }
                    }

                    // 6) Last resort (~6s): fresh Spooler + device cycle, then re-read the
                    //    interface path (usbprint may register it only after this) and any port.
                    if (discoveredUsbPort == null &&
                        !string.IsNullOrEmpty(request.Driver.VendorId) &&
                        !string.IsNullOrEmpty(request.Driver.ProductId))
                    {
                        progress?.Report("Associando driver ao dispositivo USB...");
                        await _printerService.RestartSpoolerAsync(ct);
                        await _printerService.ReEnumerateDeviceByVidPidAsync(
                            request.Driver.VendorId!, request.Driver.ProductId!, ct);
                        await Task.Delay(3000, ct);
                        usbInterfacePathFound ??= await _printerService.FindUsbPrintDeviceInterfacePathAsync(
                            request.Driver.VendorId!, request.Driver.ProductId!, ct);
                        if (!string.IsNullOrEmpty(usbInterfacePathFound) &&
                            await _printerService.EnsurePortRegisteredAsync(usbInterfacePathFound, ct))
                            discoveredUsbPort = usbInterfacePathFound;
                        discoveredUsbPort ??= await _printerService.FindBestUsbPortAsync(ct)
                            ?? await _printerService.FindUsbPortFromRegistryAsync(ct)
                            ?? await _printerService.FindPortFromNewPrinterAsync(printersBeforeInstall, ct);
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
                    _log.Warning($"Port '{portName}' not yet in Spooler — quick re-check...");
                    progress?.Report("Aguardando porta USB ser registrada no Windows...");
                    string? confirmedPort = null;
                    for (int w = 0; w < 3 && confirmedPort == null; w++)
                    {
                        if (w > 0) await Task.Delay(2000, ct);
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
            // driver/port. Delete unconditionally — do NOT gate this on PrinterExistsAsync
            // (Win32_Printer/WMI), which has repeatedly lagged behind reality in this
            // environment; skipping the delete because a stale check said "doesn't exist" is
            // exactly how a leftover object from an earlier run survives alongside the new one,
            // producing two printers with the identical name. Deleting a printer that doesn't
            // exist is a harmless no-op, so just always try, then verify and retry once.
            _log.Info($"Removing any existing printer named '{request.PrinterName}' before clean install.");
            progress?.Report("Removendo impressora existente para reinstalação limpa...");
            await _printerService.DeletePrinterAsync(request.PrinterName, ct);
            await Task.Delay(1500, ct);
            if (await _printerService.PrinterExistsAsync(request.PrinterName, ct))
            {
                // Still there (or a second stale duplicate) — one more pass.
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
                string? lastAddPrinterError = null;
                foreach (var candidateName in driverNamesToTry)
                {
                    _log.Info($"Trying AddPrinterAsync with driver: '{candidateName}'");
                    progress?.Report($"Criando impressora (driver: {candidateName})...");
                    var (ok, err) = await _printerService.TryAddPrinterWithReasonAsync(request.PrinterName, candidateName, portName, ct);
                    if (ok)
                    {
                        printerAdded = true;
                        usedDriverName = candidateName;
                        break;
                    }
                    lastAddPrinterError = err;
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
                //
                // IMPORTANT: do NOT gate this on GetAvailablePortsAsync (WMI) containing the port.
                // That WMI query has the SAME lag already confirmed for PortExists() — a port that
                // genuinely exists (Add-PrinterPort said so) can still be invisible to WMI for a
                // while. Add-Printer's own errors so far have been about the DRIVER, never the
                // port, which is the real signal that the port itself is fine. Gating here on a
                // known-stale WMI check was silently skipping this entire fallback.
                if (!printerAdded && !string.IsNullOrEmpty(portName))
                {
                    progress?.Report("Criando impressora com driver genérico (Generic / Text Only)...");
                    var genericDriverReady = await _printerService.EnsureGenericTextDriverAsync(ct);
                    var (genericOk, genericError) = await _printerService.TryAddPrinterWithReasonAsync(
                        request.PrinterName, "Generic / Text Only", portName, ct);
                    if (genericDriverReady && genericOk)
                    {
                        printerAdded = true;
                        usedDriverName = "Generic / Text Only";
                        steps.Add("Impressora criada com driver genérico (Generic / Text Only) — troque pelo driver do fabricante depois, se desejar.");
                        _log.Info($"Fallback succeeded: printer created with 'Generic / Text Only' on port '{portName}'");
                    }
                    else
                    {
                        lastAddPrinterError = genericError ?? lastAddPrinterError;
                        _log.Warning($"Generic/Text Only fallback also failed: driverReady={genericDriverReady} error={genericError}");
                    }
                }

                if (!printerAdded)
                {
                    _log.Error($"AddPrinterAsync failed for all candidates. port='{portName}' tried=[{string.Join(", ", driverNamesToTry)}]");

                    // If Add-Printer's own error clearly names the DRIVER as the problem (now
                    // readable — CLIXML decoding was fixed), trust that over guessing "port
                    // missing": GetAvailablePortsAsync (WMI) can lag behind a port that was
                    // genuinely just created, exactly like PortExists() did for Add-PrinterPort.
                    // Misreporting this as "porta não encontrada" sent every previous test down
                    // the wrong path — the actual, fixable issue is a driver NAME mismatch.
                    bool errorMentionsDriver = !string.IsNullOrEmpty(lastAddPrinterError) &&
                        (lastAddPrinterError.Contains("driver", StringComparison.OrdinalIgnoreCase));
                    if (errorMentionsDriver)
                    {
                        var installedNow = await _printerService.GetInstalledDriversAsync(ct);
                        _log.Error($"Root cause: driver name mismatch. Tried=[{string.Join(", ", driverNamesToTry)}] " +
                                   $"Installed=[{string.Join(", ", installedNow.Take(20))}]");
                        return InstallResult.Fail(
                            "O driver não foi reconhecido pelo Windows pelo nome esperado.",
                            $"Erro do Windows: {lastAddPrinterError}\n\n" +
                            $"Nomes tentados: {string.Join(", ", driverNamesToTry)}\n" +
                            $"Drivers instalados no Windows: {string.Join(", ", installedNow.Take(20))}\n\n" +
                            "O driver do fabricante foi instalado, mas o nome exato registrado no Windows " +
                            "não corresponde a nenhum dos nomes conhecidos para este modelo. Compare a lista " +
                            "acima e ajuste o nome do driver no catálogo (drivers.json) se necessário.",
                            steps);
                    }

                    // Otherwise, diagnose as a missing port — a missing port also causes
                    // Add-Printer to fail for ALL driver names, masquerading as "driver not
                    // accepted", so this remains a real, distinct failure mode to check.
                    if (request.ConnectionType == ConnectionType.USB)
                    {
                        var currentPorts = await _printerService.GetAvailablePortsAsync(ct);
                        bool portMissing = !currentPorts.Contains(portName, StringComparer.OrdinalIgnoreCase);
                        if (portMissing || discoveredUsbPort == null)
                        {
                            // Re-resolve the device fresh right before giving up: earlier resolution
                            // (at port-creation time) may have missed the device if it was mid
                            // re-enumeration, or adoption may not have applied for any other reason.
                            // This guarantees the error always reflects the REAL connected device,
                            // never the catalog's (possibly wrong) placeholder VID/PID.
                            var lastHints = new List<string> { request.Driver.Manufacturer, request.Driver.Model };
                            lastHints.AddRange(request.Driver.AllDriverNames());
                            var lastResolve = await _printerService.ResolvePrinterUsbDeviceAsync(
                                lastHints, request.Driver.VendorId, request.Driver.ProductId, ct);
                            if (lastResolve != null && !string.IsNullOrEmpty(lastResolve.Vid))
                            {
                                request.Driver.VendorId = lastResolve.Vid;
                                request.Driver.ProductId = lastResolve.Pid;
                                _log.Info($"Final re-resolve before failing: real device is " +
                                          $"'{lastResolve.FriendlyName}' vid={lastResolve.Vid} pid={lastResolve.Pid}");
                            }

                            _log.Error($"Root cause: port '{portName}' absent from Spooler. Available: [{string.Join(", ", currentPorts.Take(10))}]");
                            // Gather live device state + a full dump of connected USB devices so
                            // the failure is fully diagnosable without physical access to the PC.
                            var diag = await _printerService.GetUsbDeviceDiagnosticsAsync(
                                request.Driver.VendorId ?? "", request.Driver.ProductId ?? "", ct);
                            var deviceList = await _printerService.ListConnectedUsbDevicesAsync(ct);
                            var monitorsNowFinal = await _printerService.GetPrintMonitorsAsync(ct);
                            var monitorInfo = $"Monitores de porta registrados no Windows: {string.Join(", ", monitorsNowFinal)}.";
                            var interfaceInfo = !usbInterfacePathAttempted
                                ? "Interface USBPRINT: não verificada (VID/PID indisponível)."
                                : usbInterfacePathFound != null
                                    ? $"Interface USBPRINT: {usbInterfacePathFound}\nErro real do Windows ao registrar como porta: {usbPortRegisterError ?? "(Add-Printer também rejeitou; sem mensagem de erro capturada)"}"
                                    : "Interface USBPRINT: nenhuma encontrada para este dispositivo.";
                            _log.Error($"Device diagnostics: {diag}");
                            _log.Error($"Connected USB devices:\n{deviceList}");
                            _log.Error($"Print monitors: {monitorInfo}");
                            _log.Error($"USBPRINT interface: {interfaceInfo}");
                            return InstallResult.Fail(
                                "Porta USB não encontrada no Windows.",
                                $"{diag}\n{monitorInfo}\n{interfaceInfo}\n" +
                                $"Erro real do Add-Printer: {lastAddPrinterError ?? "(não tentado)"}\n\n" +
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

                // Final port upgrade: confirmed by direct testing that the raw USBPRINT device-
                // interface path (used as a last-resort port so the printer could be created at
                // all) does NOT actually work for real print I/O — the printer exists but a test
                // page fails. A real USB001-style port, by contrast, DOES print correctly. That
                // port can appear in Win32_PrinterPort slightly AFTER our own detection ran (the
                // same WMI-lag pattern seen throughout this session), so re-check now, after the
                // printer/driver settle, and switch to it automatically if one has since appeared
                // — this is exactly what manually changing the port in Properties > Ports does.
                bool usingRawInterfacePath = request.ConnectionType == ConnectionType.USB &&
                    portName.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase);
                if (usingRawInterfacePath)
                {
                    string? realUsbPort = null;
                    for (int i = 0; i < 3 && realUsbPort == null; i++)
                    {
                        if (i > 0) await Task.Delay(2000, ct);
                        // PowerShell's Get-PrinterPort first — bypasses the WMI Win32_PrinterPort
                        // lag confirmed repeatedly in this environment. WMI kept as fallback.
                        realUsbPort = await _printerService.FindBestUsbPortViaPowerShellAsync(ct)
                            ?? await _printerService.FindBestUsbPortAsync(ct);
                    }
                    if (realUsbPort != null)
                    {
                        _log.Info($"Real USB port '{realUsbPort}' found after printer creation — switching from raw interface path '{portName}'.");
                        if (await _printerService.UpdatePrinterPortAsync(request.PrinterName, realUsbPort, ct))
                        {
                            portName = realUsbPort;
                            steps.Add($"Porta atualizada para {realUsbPort} (porta USB padrão do Windows, testada e confiável para impressão).");
                        }
                    }
                    else
                    {
                        _log.Warning("No standard USB port found to upgrade to — printer remains on the raw interface path port. " +
                                     "If a test print fails, manually switch the port to USB001 (or the correct one) in printer Properties > Ports.");
                        steps.Add("⚠ Porta em uso não é a USB padrão do Windows — se a impressão falhar, troque manualmente para USB001 em Propriedades → Portas.");
                    }
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

        var printerDisplayName = string.IsNullOrWhiteSpace(request.PrinterName) ? shareName : request.PrinterName;

        // Remove any existing local printer with this name unconditionally before
        // (re)connecting — same reasoning as the main USB/Serial/Network install path:
        // an existing printer here keeps its LPR port "in use", which stops
        // CreateLprPortAsync from being able to remove and recreate a stale port (e.g. one
        // created before the queue-name/byte-counting fix existed) with the correct settings.
        await _printerService.DeletePrinterAsync(printerDisplayName, ct);

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
            _log.Info($"LPD not available on {host}:515. Trying RAW fallback.");
        }

        // ── Path 1.5: RAW fallback ────────────────────────────────────────────────
        // Some networks block TCP 515 specifically at the switch/router level — confirmed
        // in the field: Windows Firewall fully disabled, no antivirus, ping succeeds
        // instantly, yet a direct TCP test on 515 still fails. Port 9876 (discovery) is
        // already proven to work on the same network, so ask the server (via that already-
        // working channel) which arbitrary port its RAW listener for this printer is on,
        // and connect there instead — RAW printing has no protocol tied to a fixed port.
        if (!lpdUp)
        {
            progress?.Report($"Tentando porta RAW alternativa em {host}...");
            var discovered = await _printerService.GetRemoteSharedPrintersAsync(host, ct);
            var match = discovered
                .Select(l => l.Split('|'))
                .FirstOrDefault(f => f.Length >= 4 && f[0].Equals(shareName, StringComparison.OrdinalIgnoreCase));

            if (match != null && int.TryParse(match[3], out var rawPort))
            {
                var rawPortName = $"RAW_{host.Replace('.', '_')}_{shareName}";
                bool rawPortCreated = await _printerService.CreateTcpIpPortAsync(rawPortName, host, rawPort, ct);
                if (rawPortCreated)
                {
                    steps.Add($"Porta RAW criada: {rawPortName} ({host}:{rawPort})");

                    var driverName = await ResolveDriverForSharedAsync(request, ct);

                    progress?.Report($"Adicionando impressora '{printerDisplayName}' (driver: {driverName})...");
                    bool added = await _printerService.AddPrinterAsync(printerDisplayName, driverName, rawPortName, ct);

                    if (added)
                    {
                        steps.Add($"Impressora criada via RAW: '{printerDisplayName}' driver='{driverName}'");
                        if (request.SetAsDefault)
                        {
                            await _printerService.SetDefaultPrinterAsync(printerDisplayName, ct);
                            steps.Add("Definida como impressora padrão.");
                        }
                        progress?.Report("Impressora instalada via RAW com sucesso!");
                        return InstallResult.Ok(
                            $"Impressora '{printerDisplayName}' instalada via RAW (porta {rawPort}) com sucesso!",
                            printerDisplayName, steps);
                    }
                    _log.Warning($"RAW port created but AddPrinterAsync failed (driver='{driverName}'). Falling back to SMB.");
                }
                else
                {
                    _log.Warning("CreateTcpIpPortAsync (RAW) failed. Falling back to SMB.");
                }
            }
            else
            {
                _log.Info($"Discovery on {host} didn't return a RAW port for share '{shareName}'. Trying SMB.");
            }
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
            $"RAW (porta alternativa): não encontrada ou falhou.\n" +
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

    // Picks the architecture-appropriate INF shipped in the Repository for this driver, if the
    // catalog declares one (placeholder/template INFs are skipped by DriverFilesExist/callers
    // treating a missing real driver as "no repo INF" — here we just check the file exists).
    private string? ResolveRealInfPath(DriverInfo driver)
    {
        var infFile = !Environment.Is64BitOperatingSystem && !string.IsNullOrEmpty(driver.InfFileX86)
            ? driver.InfFileX86
            : driver.InfFile;
        if (string.IsNullOrEmpty(infFile)) return null;

        var path = Path.Combine(_repository.ResolveDriverPath(driver), infFile);
        return File.Exists(path) ? path : null;
    }

    private IReadOnlyList<string> FindPrintDriverInfInProgramFiles(DriverInfo driver)
    {
        // Some vendor packages (confirmed here: "GA-Printer Driver" shows up as an installed
        // PROGRAM, not just a device driver) install their own files under Program Files
        // rather than staging through the DriverStore — meaning FindNewDriverStoreInfs, which
        // only looks in DriverStore\FileRepository, would never see a print-driver INF that
        // exists there. Targeted scan: only recurse into top-level folders whose NAME already
        // matches the manufacturer/model (fast — avoids a blind full-disk INF search).
        var mfgLower = driver.Manufacturer.ToLowerInvariant();
        var modelLower = driver.Model.Replace("-", "").Replace(" ", "").ToLowerInvariant();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        }.Where(Directory.Exists).Distinct();

        var results = new List<string>();
        foreach (var root in roots)
        {
            IEnumerable<string> topDirs;
            try { topDirs = Directory.GetDirectories(root); }
            catch { continue; }

            foreach (var dir in topDirs)
            {
                var dirName = Path.GetFileName(dir).ToLowerInvariant();
                if (!dirName.Contains(mfgLower) && !dirName.Contains(modelLower) &&
                    !dirName.Contains("ga-printer") && !dirName.Contains("gaprinter"))
                    continue;

                try
                {
                    results.AddRange(Directory.GetFiles(dir, "*.inf", SearchOption.AllDirectories));
                }
                catch (Exception ex)
                {
                    _log.Warning($"FindPrintDriverInfInProgramFiles: could not scan '{dir}': {ex.Message}");
                }
            }
        }

        if (results.Count > 0)
            _log.Info($"FindPrintDriverInfInProgramFiles: found [{string.Join(", ", results)}]");
        return results;
    }

    private async Task<bool> InstallViaPnpUtilAsync(string infPath, CancellationToken ct)
    {
        var (ok, _) = await InstallViaPnpUtilWithOutputAsync(infPath, ct);
        return ok;
    }

    private async Task<(bool ok, string output)> InstallViaPnpUtilWithOutputAsync(string infPath, CancellationToken ct)
    {
        if (!File.Exists(infPath))
        {
            _log.Warning($"pnputil fallback: inf not found at '{infPath}'");
            return (false, "Arquivo INF não encontrado.");
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

            return (proc.ExitCode == 0 || proc.ExitCode == 3010, output);
        }
        catch (Exception ex)
        {
            _log.Warning($"pnputil fallback failed: {ex.Message}");
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
