; PrinterMode Inno Setup Script
; Gera: PrinterModeSetup.exe
; Requer: Inno Setup 6.x (https://jrsoftware.org/isinfo.php)
; Build: iscc PrinterMode.iss

#define AppName      "PrinterMode"
#define AppVersion   "1.0.0"
#define AppPublisher "PrinterMode"
#define AppURL       "https://github.com/viniciuslc10/printer-mode"
#define AppExeName   "PrinterMode.exe"
#define BuildDir     "..\src\PrinterMode.UI\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
LicenseFile=..\LICENSE
OutputDir=.\Output
OutputBaseFilename=PrinterModeSetup_{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
MinVersion=10.0.17763

; Visual customization
WizardImageFile=..\src\PrinterMode.UI\Assets\wizard.bmp
WizardSmallImageFile=..\src\PrinterMode.UI\Assets\wizard_small.bmp

[Languages]
Name: "portuguese"; MessagesFile: "compiler:Languages\Portuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Iniciar com o Windows"; GroupDescription: "Configurações:"; Flags: unchecked

[Files]
; Main executable and .NET runtime (self-contained publish)
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Driver Repository
Source: "..\Repository\*"; DestDir: "{app}\Repository"; Flags: ignoreversion recursesubdirs createallsubdirs

; Config
Source: "..\Config\*"; DestDir: "{app}\Config"; Flags: ignoreversion recursesubdirs createallsubdirs

; Scripts PowerShell
Source: "..\Scripts\*"; DestDir: "{app}\Scripts"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
; Create Logs directory
Name: "{app}\Logs"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Launch after install
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallDelete]
Type: filesandordirs; Name: "{app}\Logs"

[Code]
// Check Windows version on install
function InitializeSetup(): Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  if Version.Major < 10 then begin
    MsgBox('PrinterMode requer Windows 10 ou superior.', mbError, MB_OK);
    Result := False;
  end else
    Result := True;
end;
