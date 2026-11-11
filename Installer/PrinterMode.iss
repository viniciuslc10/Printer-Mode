; ============================================================
; PrinterMode — Inno Setup Script
; Gera: Output\PrinterModeSetup_1.0.0.exe
; Requisito: Inno Setup 6.x  https://jrsoftware.org/isinfo.php
; Build:  build.bat   (ou  ISCC.exe PrinterMode.iss)
; ============================================================

#define AppName      "PrinterMode"
#define AppVersion   "1.4.1"
#define AppPublisher "PrinterMode"
#define AppURL       "https://github.com/viniciuslc10/printer-mode"
#define AppExeName   "PrinterMode.exe"
#define BuildDir     "publish"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes

SetupIconFile=..\src\PrinterMode.UI\Assets\icon.ico

OutputDir=.\Output
OutputBaseFilename=PrinterModeSetup_{#AppVersion}

Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

; Requer administrador para instalar drivers
PrivilegesRequired=admin

MinVersion=10.0.17763
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na Área de Trabalho"; \
  GroupDescription: "Ícones adicionais:"

[Files]
Source: "{#BuildDir}\*"; DestDir: "{app}"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

Source: "..\Repository\*"; DestDir: "{app}\Repository"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

Source: "..\Config\*"; DestDir: "{app}\Config"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

Source: "..\Scripts\*"; DestDir: "{app}\Scripts"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{app}\Logs"

[Registry]
; The app writes this Run key itself at every startup (WindowsPrinterService/App.xaml.cs
; RegisterWindowsStartup) so it keeps sharing printers in the background after login —
; it is never installed through [Files]/[Icons], so Inno's normal uninstall tracking never
; sees it. uninsdeletevalue removes it explicitly on uninstall; without this, a leftover
; "PrinterMode" entry would still try to launch the (now missing) exe at every login.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "PrinterMode"; \
  Flags: uninsdeletevalue

[Icons]
Name: "{group}\{#AppName}";             Filename: "{app}\{#AppExeName}"
Name: "{group}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";       Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Open inbound firewall rules for the LPD (515) and Discovery (9876) servers at install
; time — guarantees printer sharing between PCs works even if the app is closed right
; after install without being reopened. The app also (re)opens these itself at every
; startup as a safety net, but doing it here too means it's not conditional on that.
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""PrinterMode LPD"""; \
  Flags: runhidden; StatusMsg: "Configurando firewall..."
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""PrinterMode LPD"" dir=in action=allow protocol=tcp localport=515 profile=any"; \
  Flags: runhidden
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""PrinterMode Discovery"""; \
  Flags: runhidden
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""PrinterMode Discovery"" dir=in action=allow protocol=tcp localport=9876 profile=any"; \
  Flags: runhidden

Filename: "{app}\{#AppExeName}"; \
  Description: "Abrir {#AppName} agora"; \
  Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
; Confirmed root cause of the leftover folder: PrinterMode keeps running in the
; background after its window is closed (tray icon, ShutdownMode.OnExplicitShutdown —
; needed so printer sharing keeps working) — so when Control Panel's uninstall runs,
; PrinterMode.exe is usually still alive and holds an open handle on itself, which blocks
; Windows from deleting it (and therefore the whole {app} folder) even with
; [UninstallDelete] below. [UninstallRun] entries execute in order, BEFORE any files are
; removed, so killing the process (and anything it spawned, /T) here first guarantees the
; exe is unlocked by the time deletion happens.
Filename: "taskkill"; Parameters: "/F /IM {#AppExeName} /T"; \
  Flags: runhidden; RunOnceId: "KillPrinterModeProcess"

; Mirror [Run]'s firewall rules: remove them on uninstall so no stale allow-rule for an
; app that's gone stays open on the machine.
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""PrinterMode LPD"""; \
  Flags: runhidden; RunOnceId: "RemoveLpdFirewallRule"
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""PrinterMode Discovery"""; \
  Flags: runhidden; RunOnceId: "RemoveDiscoveryFirewallRule"

[UninstallDelete]
; The app also writes files the installer never put there at install time (logs, drivers
; it staged/extracted at runtime, cached repository state) — those aren't in Inno's
; uninstall file list, so without this the whole {app} folder would survive, non-empty,
; under Program Files after "uninstalling". Deleting the entire tree guarantees a clean
; removal regardless of what the app wrote into it while it ran.
Type: filesandordirs; Name: "{app}"
