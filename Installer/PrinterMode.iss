; ============================================================
; PrinterMode — Inno Setup Script
; Gera: Output\PrinterModeSetup_1.0.0.exe
; Requisito: Inno Setup 6.x  https://jrsoftware.org/isinfo.php
; Build:  build.bat   (ou  ISCC.exe PrinterMode.iss)
; ============================================================

#define AppName      "PrinterMode"
#define AppVersion   "1.0.0"
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
Filename: "{app}\{#AppExeName}"; \
  Description: "Abrir {#AppName} agora"; \
  Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallDelete]
Type: filesandordirs; Name: "{app}\Logs"
