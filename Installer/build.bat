@echo off
setlocal
chcp 65001 > nul

:: ============================================================
:: build.bat — Gera o instalador PrinterModeSetup_x.x.x.exe
:: Execute como Administrador não é necessário aqui,
:: mas o dotnet e o Inno Setup devem estar instalados.
:: ============================================================

set "PROJECT=..\src\PrinterMode.UI\PrinterMode.UI.csproj"
set "PUBLISH_DIR=..\src\PrinterMode.UI\bin\Release\net8.0-windows\win-x64\publish"
set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"

echo.
echo  ================================================
echo   PrinterMode ^| Build do Instalador
echo  ================================================
echo.

:: ── 1. Verificar dependências ─────────────────────────────
where dotnet >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [ERRO] .NET SDK nao encontrado.
    echo        Baixe em: https://dotnet.microsoft.com/download
    pause & exit /b 1
)

if not exist "%ISCC%" (
    echo [ERRO] Inno Setup 6 nao encontrado em:
    echo        %ISCC%
    echo        Baixe em: https://jrsoftware.org/isinfo.php
    pause & exit /b 1
)

:: ── 2. Publicar o projeto ─────────────────────────────────
echo [1/2] Publicando aplicacao (self-contained, win-x64)...
echo.

dotnet publish "%PROJECT%" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=false ^
  -p:PublishReadyToRun=true ^
  -o "%PUBLISH_DIR%"

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERRO] Falha ao publicar. Veja mensagens acima.
    pause & exit /b 1
)

echo.
echo  Publicacao concluida em: %PUBLISH_DIR%
echo.

:: ── 3. Compilar o instalador ──────────────────────────────
echo [2/2] Compilando instalador com Inno Setup...
echo.

"%ISCC%" "PrinterMode.iss"

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERRO] Falha ao criar o instalador. Veja mensagens acima.
    pause & exit /b 1
)

echo.
echo  ================================================
echo   Instalador gerado com sucesso!
echo   Pasta:   Installer\Output\
echo   Arquivo: PrinterModeSetup_1.0.0.exe
echo  ================================================
echo.
pause
