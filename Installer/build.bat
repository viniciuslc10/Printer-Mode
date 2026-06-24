@echo off
setlocal

:: ============================================================
:: build.bat — Gera o instalador PrinterModeSetup_1.0.0.exe
:: Coloque dentro da pasta Installer\ e execute como Administrador
:: ============================================================

set "PROJECT=..\src\PrinterMode.UI\PrinterMode.UI.csproj"
set "PUBLISH_DIR=..\src\PrinterMode.UI\bin\Release\net8.0-windows\win-x64\publish"
set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"

echo.
echo  ================================================
echo   PrinterMode - Build do Instalador
echo  ================================================
echo.

:: ── 1. Verificar .NET SDK ─────────────────────────────────
echo Verificando .NET SDK...
dotnet --version
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERRO] .NET SDK nao encontrado no PATH.
    echo        Reinicie o computador apos instalar o SDK e tente novamente.
    echo        Baixe em: https://dotnet.microsoft.com/download
    echo.
    pause
    exit /b 1
)

:: ── 2. Verificar Inno Setup ───────────────────────────────
echo Verificando Inno Setup...
if not exist "%ISCC%" (
    echo.
    echo [ERRO] Inno Setup 6 nao encontrado em:
    echo        %ISCC%
    echo        Baixe em: https://jrsoftware.org/isinfo.php
    echo.
    pause
    exit /b 1
)
echo Inno Setup encontrado.
echo.

:: ── 3. Publicar o projeto ─────────────────────────────────
echo [1/2] Publicando aplicacao (self-contained, win-x64)...
echo       Destino: %PUBLISH_DIR%
echo.

dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o "%PUBLISH_DIR%"

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERRO] Falha ao publicar. Veja mensagens acima.
    echo.
    pause
    exit /b 1
)

echo.
echo  Publicacao concluida. Verificando arquivos...
if not exist "%PUBLISH_DIR%\PrinterMode.exe" (
    echo [ERRO] PrinterMode.exe nao encontrado em %PUBLISH_DIR%
    echo        O publish pode ter falhado silenciosamente.
    pause
    exit /b 1
)
echo  PrinterMode.exe encontrado. OK.
echo.

:: ── 4. Compilar o instalador ──────────────────────────────
echo [2/2] Compilando instalador com Inno Setup...
echo.

"%ISCC%" "PrinterMode.iss"
set ISCC_EXIT=%ERRORLEVEL%

echo.
if %ISCC_EXIT% NEQ 0 (
    echo [ERRO] Inno Setup retornou erro %ISCC_EXIT%.
    echo        Abra PrinterMode.iss no Inno Setup Compiler para ver o erro detalhado.
) else (
    echo  ================================================
    echo   Instalador gerado com sucesso!
    echo   Arquivo: Installer\Output\PrinterModeSetup_1.0.0.exe
    echo  ================================================
)
echo.
pause
