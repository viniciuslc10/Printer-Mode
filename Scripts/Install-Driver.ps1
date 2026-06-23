# Install-Driver.ps1
# Uso: .\Install-Driver.ps1 -InfPath "C:\...\driver.inf" -PrinterName "Bematech MP-4200" -PortName "USB001"
# Requer execução como Administrador

param(
    [Parameter(Mandatory=$true)]
    [string]$InfPath,

    [Parameter(Mandatory=$true)]
    [string]$PrinterName,

    [Parameter(Mandatory=$false)]
    [string]$PortName = "USB001",

    [Parameter(Mandatory=$false)]
    [string]$IpAddress,

    [Parameter(Mandatory=$false)]
    [int]$TcpPort = 9100,

    [Parameter(Mandatory=$false)]
    [string]$ConnectionType = "USB",   # USB | Serial | Network

    [Parameter(Mandatory=$false)]
    [switch]$SetAsDefault
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step([string]$msg) {
    Write-Host "[*] $msg" -ForegroundColor Cyan
}
function Write-Ok([string]$msg) {
    Write-Host "[✓] $msg" -ForegroundColor Green
}
function Write-Fail([string]$msg) {
    Write-Host "[✗] $msg" -ForegroundColor Red
    exit 1
}

# 1. Verificar privilégios
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Fail "Este script requer privilégios de Administrador."
}

# 2. Instalar driver via PnPUtil
Write-Step "Instalando driver: $InfPath"
$result = & pnputil.exe /add-driver "$InfPath" /install 2>&1
if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 3010) {
    Write-Fail "Falha ao instalar driver. Código: $LASTEXITCODE`n$result"
}
Write-Ok "Driver instalado via PnPUtil."

# 3. Criar porta
if ($ConnectionType -eq "Network") {
    Write-Step "Criando porta TCP/IP: $IpAddress:$TcpPort"
    $portName = "IP_${IpAddress}"
    $wmiPort = ([wmiclass]"Win32_TCPIPPrinterPort").CreateInstance()
    $wmiPort.Name        = $portName
    $wmiPort.HostAddress = $IpAddress
    $wmiPort.PortNumber  = $TcpPort
    $wmiPort.Protocol    = 1
    $wmiPort.SNMPEnabled = $false
    $wmiPort.Put() | Out-Null
    $PortName = $portName
    Write-Ok "Porta TCP/IP criada: $PortName"
}

# 4. Adicionar impressora via PrintUI
Write-Step "Adicionando impressora: $PrinterName na porta $PortName"
$driverName = (Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\Print\Environments\Windows x64\Drivers\Version-3\*" |
               Where-Object { $_.PSChildName -match "." } |
               Select-Object -Last 1).PSChildName

# Usar rundll32 printui
$args = "/if /b `"$PrinterName`" /r `"$PortName`" /m `"$driverName`""
$proc = Start-Process -FilePath "rundll32.exe" -ArgumentList "printui.dll,PrintUIEntry $args" -Wait -PassThru
if ($proc.ExitCode -ne 0) {
    # Fallback via WMI
    Write-Step "Tentando via WMI..."
    $printer = ([wmiclass]"Win32_Printer").CreateInstance()
    $printer.Name       = $PrinterName
    $printer.DriverName = $driverName
    $printer.PortName   = $PortName
    $printer.Shared     = $false
    $printer.Put() | Out-Null
}
Write-Ok "Impressora '$PrinterName' adicionada."

# 5. Definir como padrão
if ($SetAsDefault) {
    Write-Step "Definindo como padrão..."
    $wmiPrinter = Get-WmiObject -Query "SELECT * FROM Win32_Printer WHERE Name='$PrinterName'"
    $wmiPrinter.SetDefaultPrinter() | Out-Null
    Write-Ok "Impressora padrão definida."
}

Write-Ok "Instalação concluída! A impressora '$PrinterName' está disponível em Dispositivos e Impressoras."
