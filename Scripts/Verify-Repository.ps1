# Verify-Repository.ps1
# Verifica se todos os drivers do catálogo existem no repositório
# Indica quais são templates (precisam ser substituídos) e quais são drivers reais

param(
    [string]$RepositoryPath = "$PSScriptRoot\..\Repository",
    [string]$CatalogFile    = "$PSScriptRoot\..\Repository\drivers.json"
)

$catalog = Get-Content $CatalogFile -Raw | ConvertFrom-Json

$ok      = 0
$missing = 0
$template= 0

Write-Host "`nPRINTERMODE - VERIFICAÇÃO DO REPOSITÓRIO DE DRIVERS" -ForegroundColor Cyan
Write-Host ("=" * 60)

foreach ($driver in $catalog.drivers) {
    $infPath = Join-Path $RepositoryPath $driver.driverFolder | Join-Path -ChildPath $driver.infFile

    if (-not (Test-Path $infPath)) {
        Write-Host "[ AUSENTE ] $($driver.manufacturer) $($driver.model)" -ForegroundColor Red
        Write-Host "            → $infPath"
        $missing++
        continue
    }

    # Detecta se é template (contém o aviso)
    $content = Get-Content $infPath -Raw -ErrorAction SilentlyContinue
    $isTemplate = $content -match "TEMPLATE DE REFERÊNCIA"

    if ($isTemplate) {
        Write-Host "[ TEMPLATE ] $($driver.manufacturer) $($driver.model)" -ForegroundColor Yellow
        Write-Host "             → Substitua pelo driver oficial. Veja README.txt"
        $template++
    } else {
        Write-Host "[   OK    ] $($driver.manufacturer) $($driver.model)" -ForegroundColor Green
        $ok++
    }
}

Write-Host "`n$(("=" * 60))"
Write-Host "RESUMO:"
Write-Host "  OK (driver real instalado) : $ok" -ForegroundColor Green
Write-Host "  Template (precisa substituir): $template" -ForegroundColor Yellow
Write-Host "  Ausente                     : $missing" -ForegroundColor Red
Write-Host ""

if ($template -gt 0) {
    Write-Host "PRÓXIMO PASSO: Baixe os drivers oficiais conforme indicado nos" -ForegroundColor Cyan
    Write-Host "               arquivos README.txt dentro de cada pasta do fabricante." -ForegroundColor Cyan
}
