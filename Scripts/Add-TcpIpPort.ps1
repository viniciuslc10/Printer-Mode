# Add-TcpIpPort.ps1
# Cria uma porta TCP/IP padrão para impressora de rede
# Uso: .\Add-TcpIpPort.ps1 -PortName "IP_192.168.0.100" -IpAddress "192.168.0.100" -Port 9100

param(
    [Parameter(Mandatory=$true)] [string]$PortName,
    [Parameter(Mandatory=$true)] [string]$IpAddress,
    [Parameter(Mandatory=$false)] [int]$Port = 9100
)

$existing = Get-WmiObject -Query "SELECT * FROM Win32_TCPIPPrinterPort WHERE Name='$PortName'"
if ($existing) {
    Write-Host "Porta '$PortName' já existe." -ForegroundColor Yellow
    exit 0
}

$wmiPort = ([wmiclass]"Win32_TCPIPPrinterPort").CreateInstance()
$wmiPort.Name        = $PortName
$wmiPort.HostAddress = $IpAddress
$wmiPort.PortNumber  = $Port
$wmiPort.Protocol    = 1   # 1=RAW, 2=LPR
$wmiPort.SNMPEnabled = $false
$wmiPort.Put() | Out-Null

Write-Host "Porta TCP/IP '$PortName' criada → $IpAddress:$Port" -ForegroundColor Green
