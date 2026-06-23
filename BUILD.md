# Como compilar e distribuir o PrinterMode

## Pré-requisitos

- **Windows 10/11** (64-bit)
- **.NET 8 SDK** → https://dotnet.microsoft.com/download/dotnet/8.0
- **Visual Studio 2022** ou **Rider** (opcional, pode usar CLI)
- **Inno Setup 6.x** (para gerar o instalador) → https://jrsoftware.org/isinfo.php

---

## 1. Compilar o projeto

```powershell
# Publicar como executável auto-contido (sem precisar de .NET instalado no cliente)
dotnet publish src\PrinterMode.UI\PrinterMode.UI.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -o publish\

# Ou publicar como arquivo único (maior, mas distribui em 1 .exe)
dotnet publish src\PrinterMode.UI\PrinterMode.UI.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish\
```

---

## 2. Adicionar drivers ao repositório

Antes de gerar o instalador, adicione os drivers oficiais:

```
Repository\
├── Bematech\
│   ├── MP4200\
│   │   ├── MP4200.inf        ← driver oficial
│   │   ├── MP4200.cat
│   │   └── *.dll
│   └── MP2800\
│       └── MP2800.inf
├── Elgin\
│   ├── i9\  └── ElginI9.inf
│   └── i8\  └── ElginI8.inf
├── Epson\
│   ├── TM-T20X\ └── EPST20X.inf
│   └── TM-T88V\ └── EPST88V.inf
├── Daruma\
│   └── DR800\ └── DR800.inf
└── Zebra\
    └── ZD220\ └── ZD220.inf
```

---

## 3. Gerar o instalador .exe

```powershell
# Com Inno Setup instalado:
iscc Installer\PrinterMode.iss
# Saída: Installer\Output\PrinterModeSetup_1.0.0.exe
```

---

## 4. Estrutura de instalação no cliente

```
C:\Program Files\PrinterMode\
├── PrinterMode.exe
├── Repository\
│   ├── drivers.json          ← catálogo mestre
│   ├── Bematech\MP4200\
│   ├── Elgin\i9\
│   ├── Epson\TM-T20X\
│   └── ...
├── Config\
│   └── settings.json
├── Scripts\
│   ├── Install-Driver.ps1
│   └── Add-TcpIpPort.ps1
└── Logs\
    └── printermode_YYYYMMDD.log
```

---

## 5. Executar como Administrador

O `app.manifest` já configura `requireAdministrator`. O Windows solicitará UAC ao abrir o programa.

Para desenvolvimento/debug no Visual Studio, abra o VS como Administrador.

---

## 6. Adicionar novo fabricante/modelo

1. Criar pasta em `Repository\<Fabricante>\<Modelo>\`
2. Copiar os arquivos `.inf` e assets do driver oficial
3. Adicionar entrada em `Repository\drivers.json`
4. Distribuir a pasta atualizada (não precisa recompilar o programa)
