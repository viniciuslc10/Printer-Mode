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
; Altere o GUID abaixo se criar outro produto (gere um novo em https://www.guidgenerator.com)
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}

; Instala em C:\Program Files\PrinterMode
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes

; Icone do instalador
SetupIconFile=..\src\PrinterMode.UI\Assets\icon.ico

; Saída
OutputDir=.\Output
OutputBaseFilename=PrinterModeSetup_{#AppVersion}

; Compressão máxima
Compression=lzma2/ultra64
SolidCompression=yes

; Visual moderno do assistente
WizardStyle=modern

; Requer administrador (necessário para instalar drivers)
PrivilegesRequired=admin

; Apenas Windows 10/11 de 64 bits
MinVersion=10.0.17763
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na Área de Trabalho"; \
  GroupDescription: "Ícones adicionais:"

[Files]
; Arquivos publicados pelo dotnet publish (.NET runtime, DLLs, exe)
Source: "{#BuildDir}\*"; DestDir: "{app}"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

; Repositorio de drivers
Source: "..\Repository\*"; DestDir: "{app}\Repository"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

; Configuracoes
Source: "..\Config\*"; DestDir: "{app}\Config"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

; Scripts PowerShell
Source: "..\Scripts\*"; DestDir: "{app}\Scripts"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
; Cria pasta de logs vazia
Name: "{app}\Logs"

[Icons]
; Atalhos invocam a tarefa agendada — sem prompt UAC ao abrir
Name: "{group}\{#AppName}";             Filename: "{sys}\schtasks.exe"; \
  Parameters: "/Run /TN ""{#AppName}"""; IconFilename: "{app}\{#AppExeName}"
Name: "{group}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";       Filename: "{sys}\schtasks.exe"; \
  Parameters: "/Run /TN ""{#AppName}"""; IconFilename: "{app}\{#AppExeName}"; \
  Tasks: desktopicon

[Run]
; Registra tarefa agendada que executa o app como SYSTEM/HighestPrivilege sem prompt UAC
Filename: "{sys}\schtasks.exe"; \
  Parameters: "/Create /F /RL HIGHEST /SC ONDEMAND /TN ""{#AppName}"" /TR """"""{app}\{#AppExeName}"""""""; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "Configurando execução como administrador..."
; Abre o PrinterMode ao final da instalação (via tarefa agendada, sem UAC)
Filename: "{sys}\schtasks.exe"; \
  Parameters: "/Run /TN ""{#AppName}"""; \
  Description: "Abrir {#AppName} agora"; \
  Flags: nowait postinstall skipifsilent runhidden

[UninstallRun]
; Remove a tarefa agendada ao desinstalar
Filename: "{sys}\schtasks.exe"; \
  Parameters: "/Delete /F /TN ""{#AppName}"""; \
  Flags: runhidden waituntilterminated

[UninstallDelete]
; Remove a pasta de logs ao desinstalar
Type: filesandordirs; Name: "{app}\Logs"
