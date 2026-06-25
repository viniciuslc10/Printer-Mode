@echo off
setlocal enabledelayedexpansion

:: ============================================================
:: build.bat — Gera PrinterModeSetup_1.0.0.exe
:: Execute dentro da pasta Installer\ (duplo clique ou cmd)
:: ============================================================

set "PROJECT=..\src\PrinterMode.UI\PrinterMode.UI.csproj"
set "PUBLISH_DIR=%~dp0publish"
set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"

echo.
echo  ================================================
echo   PrinterMode - Build do Instalador
echo  ================================================
echo.
echo  Pasta de trabalho: %~dp0
echo  Publicando para:   %PUBLISH_DIR%
echo.

:: ── 1. Verificar .NET SDK ─────────────────────────────────
dotnet --version >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [ERRO] .NET SDK nao encontrado.
    echo        Reinicie o computador e tente novamente.
    echo        Download: https://dotnet.microsoft.com/download
    pause & exit /b 1
)
for /f "tokens=*" %%v in ('dotnet --version') do echo .NET SDK: %%v

:: ── 2. Verificar Inno Setup ───────────────────────────────
if not exist "!ISCC!" (
    echo.
    echo [ERRO] Inno Setup nao encontrado em:
    echo        !ISCC!
    echo        Download: https://jrsoftware.org/isinfo.php
    pause & exit /b 1
)
echo Inno Setup: encontrado
echo.

:: ── 3. Limpar publish anterior ────────────────────────────
if exist "%PUBLISH_DIR%" (
    echo Limpando publish anterior...
    rmdir /s /q "%PUBLISH_DIR%"
)

:: ── 4. Publicar o projeto ─────────────────────────────────
echo [1/2] Publicando aplicacao...
echo.

dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o "%PUBLISH_DIR%"

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERRO] Falha no dotnet publish.
    pause & exit /b 1
)

if not exist "%PUBLISH_DIR%\PrinterMode.exe" (
    echo.
    echo [ERRO] PrinterMode.exe nao gerado em %PUBLISH_DIR%
    pause & exit /b 1
)
echo.
echo  Publicacao OK: %PUBLISH_DIR%
echo.

:: ── 5. Compilar instalador ────────────────────────────────
echo [2/2] Compilando instalador com Inno Setup...
echo.

cd /d "%~dp0"
"!ISCC!" "PrinterMode.iss"
set "EXIT=%ERRORLEVEL%"

echo.
if %EXIT% NEQ 0 (
    echo [ERRO] Inno Setup falhou com codigo %EXIT%.
    echo        Abra PrinterMode.iss no Inno Setup Compiler para ver o erro.
) else (
    echo  ================================================
    echo   SUCESSO! Arquivo gerado em:
    echo   %~dp0Output\PrinterModeSetup_1.0.0.exe
    echo  ================================================
)
echo.
pause
