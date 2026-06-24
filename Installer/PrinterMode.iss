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
#define BuildDir     "..\src\PrinterMode.UI\bin\Release\net8.0-windows\win-x64\publish"

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
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "portuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na Área de Trabalho"; \
  GroupDescription: "Ícones adicionais:"; Flags: unchecked

[Files]
; Todos os arquivos publicados (inclui .NET runtime, DLLs, Repository, Config)
Source: "{#BuildDir}\*"; DestDir: "{app}"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
; Cria pasta de logs vazia
Name: "{app}\Logs"

[Icons]
Name: "{group}\{#AppName}";                  Filename: "{app}\{#AppExeName}"
Name: "{group}\Desinstalar {#AppName}";      Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";            Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Opção para abrir o PrinterMode ao final da instalação
Filename: "{app}\{#AppExeName}"; \
  Description: "Abrir {#AppName} agora"; \
  Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Remove a pasta de logs ao desinstalar
Type: filesandordirs; Name: "{app}\Logs"

[Code]
function InitializeSetup(): Boolean;
begin
  // Windows 10 build 17763 (1809) ou superior
  if not CheckWin32Version(10, 0) then
  begin
    MsgBox('PrinterMode requer Windows 10 ou superior.', mbError, MB_OK);
    Result := False;
  end
  else
    Result := True;
end;
