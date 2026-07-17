; ============================================================
; PrinterMode — Inno Setup Script
; Gera: Output\PrinterModeSetup_1.0.0.exe
; Requisito: Inno Setup 6.x  https://jrsoftware.org/isinfo.php
; Build:  build.bat   (ou  ISCC.exe PrinterMode.iss)
; ============================================================

#define AppName      "PrinterMode"
#define AppVersion   "1.0.1"
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

[UninstallDelete]
Type: filesandordirs; Name: "{app}\Logs"
