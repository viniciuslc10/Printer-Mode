# 🖨 PrinterMode

Aplicativo desktop Windows para instalação e configuração automática de impressoras térmicas e não-térmicas, utilizando sempre os **drivers oficiais do fabricante**.

> **Windows 10 / 11 · .NET 8 · WPF · Requer Administrador**

---

## 📦 Download

> Em breve: `PrinterModeSetup_1.0.0.exe` na aba [Releases](../../releases)

Para compilar manualmente, veja a seção [Como compilar](#como-compilar).

---

## ✨ Funcionalidades

- Detecção automática de impressoras via **USB**, **Serial (COM)**, **TCP/IP** e **rede compartilhada**
- Identificação por **VID/PID** com auto-match no catálogo de drivers
- Instalação do driver oficial via `pnputil /add-driver` (sem drivers genéricos)
- Criação automática de portas **USB**, **COM** e **TCP/IP** no Windows
- Configuração de **tamanho de papel** térmico por modelo
- A impressora aparece normalmente em **Dispositivos e Impressoras**
- Compatível com qualquer software (Word, Bloco de Notas, ERP, navegadores)
- Catálogo de drivers extensível **sem recompilar** o programa

---

## 🖨 Impressoras suportadas

| Fabricante | Modelos |
|---|---|
| **Bematech** | MP-4200 TH · MP-2800 TH · MP-4000 TH · MP-5100 TH · MP-100S TH |
| **Elgin** | i9 · i8 · i7 · i5 · i3 |
| **Epson** | TM-T20X · TM-T88V · TM-T88VI · TM-T20III · TM-T82X |
| **Gertec** | G250 · G500 |
| **Tanca** | TP-550 · TP-650 |
| **Tomate** | MT-508 · MT-609 |
| **Diebold** | IM453 · IM333 |
| **Daruma** | DR800 · DR700 · DR600 |
| **POS-58** | Genérica 58mm (USB) |
| **POS-80** | Genérica 80mm (USB) |
| **Zebra** | ZD220 |

> Adicionar novos modelos não exige recompilar — veja [Adicionar novo modelo](#adicionar-novo-modelo).

---

## 📁 Estrutura do repositório

```
PrinterMode/
│
├── src/                          ← Código-fonte C# .NET 8
│   ├── PrinterMode.UI/           ← Interface WPF (janela principal)
│   ├── PrinterMode.Core/         ← Modelos e interfaces
│   ├── PrinterMode.DriverManager/← Catálogo + instalação de drivers
│   ├── PrinterMode.WindowsPrinter/← Integração Windows (WMI / PrintUI)
│   └── PrinterMode.NetworkDiscovery/ ← Detecção USB/COM/TCP-IP
│
├── Repository/                   ← Repositório de drivers
│   ├── drivers.json              ← Catálogo mestre (VID/PID, papel, porta)
│   ├── Bematech/
│   │   ├── MP4200/
│   │   │   ├── README.txt        ← Onde baixar o driver oficial
│   │   │   └── MP4200.inf        ← ⚠ TEMPLATE — substitua pelo driver real
│   │   └── MP2800/ ...
│   ├── Elgin/  ...
│   ├── Epson/  ...
│   └── (demais fabricantes)
│
├── Config/
│   └── settings.json
│
├── Scripts/
│   ├── Install-Driver.ps1        ← Instalação manual via PowerShell
│   ├── Add-TcpIpPort.ps1         ← Cria porta TCP/IP manualmente
│   └── Verify-Repository.ps1     ← Verifica quais drivers estão faltando
│
├── Installer/
│   └── PrinterMode.iss           ← Script Inno Setup (gera o .exe instalador)
│
└── PrinterMode.sln               ← Solução Visual Studio
```

---

## 🔌 Onde colocar os drivers baixados

> Os arquivos `.inf` que estão nas pastas são **templates de referência**.  
> Você precisa substituí-los pelos **arquivos reais** baixados do site do fabricante.

### Passo a passo

**1. Verifique quais drivers estão faltando** (PowerShell como Administrador):

```powershell
.\Scripts\Verify-Repository.ps1
```

Saída esperada:
```
[ TEMPLATE ] Bematech MP-4200 TH  → Substitua pelo driver oficial. Veja README.txt
[ TEMPLATE ] Elgin i9             → Substitua pelo driver oficial. Veja README.txt
```

---

**2. Baixe o driver no site do fabricante**

Cada pasta tem um `README.txt` com a URL exata. Exemplos:

| Fabricante | URL de download |
|---|---|
| Bematech | https://bematech.com.br → Suporte → Downloads |
| Elgin | https://elgin.com.br → Impressoras → (modelo) → Downloads |
| Epson | https://epson.com.br → Suporte → (modelo) → Drivers → APD |
| Gertec | https://gertec.com.br → Suporte → Softwares |
| Tanca | https://tanca.com.br → Suporte → Downloads |
| Tomate / Multilaser | https://multilaser.com.br → Suporte |
| Diebold | https://dieboldnixdorf.com → Support → POS Printers |
| Daruma | https://daruma.com.br → Suporte → Downloads |
| Zebra | https://zebra.com/us/en/support-downloads → ZDesigner |

---

**3. Extraia e copie para a pasta correta**

Exemplo para **Bematech MP-4200 TH**:

```
Você baixou:  Bematech_Driver_v1.9_Win10_x64.zip
Extraiu para: C:\Downloads\Bematech_MP4200\

Copie os arquivos para:
  Repository\Bematech\MP4200\
    ├── MP4200.inf      ← arquivo INF do pacote baixado
    ├── MP4200.cat      ← catálogo de assinaturas
    └── *.dll / *.sys   ← demais arquivos do pacote
```

> **Atenção ao nome do `.inf`**: deve corresponder ao campo `"infFile"` no `drivers.json`.  
> Se o fabricante usar outro nome (ex: `bema4200.inf`), renomeie para `MP4200.inf`  
> **ou** atualize o campo `"infFile"` no `drivers.json`.

---

**4. Verifique novamente**

```powershell
.\Scripts\Verify-Repository.ps1
```

```
[   OK    ] Bematech MP-4200 TH   ← driver real instalado
```

---

## 🛠 Como compilar

### Pré-requisitos

- Windows 10/11 (64-bit)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022 ou Rider (opcional)
- [Inno Setup 6.x](https://jrsoftware.org/isinfo.php) — para gerar o instalador

### Compilar

```powershell
# Publicar como executável auto-contido (sem .NET no cliente)
dotnet publish src\PrinterMode.UI\PrinterMode.UI.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o publish\
```

### Gerar instalador `.exe`

Após adicionar os drivers reais ao `Repository\`:

```powershell
iscc Installer\PrinterMode.iss
# Saída: Installer\Output\PrinterModeSetup_1.0.0.exe
```

---

## ➕ Adicionar novo modelo

Não é necessário recompilar. Basta:

1. Criar a pasta em `Repository\<Fabricante>\<Modelo>\`
2. Copiar os arquivos `.inf` e demais assets do driver
3. Adicionar a entrada no `Repository\drivers.json`:

```json
{
  "id": "fabricante_modelo",
  "manufacturer": "Fabricante",
  "model": "Modelo X",
  "vid": "XXXX",
  "pid": "YYYY",
  "driverFolder": "Fabricante\\Modelo",
  "infFile": "modelo.inf",
  "driverName": "Nome do driver no Windows",
  "version": "1.0",
  "defaultPaper": {
    "name": "Thermal 80mm",
    "widthMm": 80.0,
    "printableWidthMm": 72.0,
    "isAutoLength": true,
    "marginLeftMm": 4.0,
    "marginRightMm": 4.0
  },
  "supportedPapers": [
    { "name": "Thermal 80mm", "widthMm": 80.0, "printableWidthMm": 72.0, "isAutoLength": true }
  ],
  "supportedPorts": ["USB", "TCP/IP"]
}
```

---

## 🔍 Como encontrar o VID/PID da sua impressora

1. Conecte a impressora via USB
2. Abra **Gerenciador de Dispositivos** (`devmgmt.msc`)
3. Localize a impressora → botão direito → **Propriedades**
4. Aba **Detalhes** → Propriedade: **IDs de hardware**
5. Procure o valor no formato `USB\VID_XXXX&PID_YYYY`

---

## 🏗 Arquitetura

```
PrinterMode.UI              ← WPF, MVVM (CommunityToolkit), DI
├── Views/                  ← Dashboard · Instalar Driver · Impressoras
├── ViewModels/             ← DashboardVM · InstallDriverVM · PrinterListVM
└── Services/LogService.cs

PrinterMode.Core            ← Sem dependências externas
├── Models/                 ← PrinterDevice · DriverInfo · PaperConfig · InstallRequest
└── Interfaces/             ← IPrinterDetector · IDriverRepository · IDriverInstaller

PrinterMode.NetworkDiscovery← System.Management (WMI)
├── UsbPrinterDetector      ← Detecta USB por VID/PID via Win32_PnPEntity
├── SerialPortDetector      ← Lista portas COM disponíveis
├── NetworkPrinterDetector  ← TCP/IP e impressoras compartilhadas
└── InstalledPrinterDetector← Win32_Printer (já instaladas)

PrinterMode.DriverManager
├── DriverRepository        ← Lê drivers.json, resolve caminhos
└── DriverInstaller         ← pnputil + PrintUIEntry + configuração de papel

PrinterMode.WindowsPrinter  ← WMI + rundll32 printui.dll
├── WindowsPrinterService   ← Cria portas, adiciona impressora, configura papel
└── (integra com Dispositivos e Impressoras do Windows)
```

---

## ⚠ Requisitos de execução

- **Windows 10 ou Windows 11** (x64)
- **Executar como Administrador** — necessário para instalar drivers e criar portas
- O manifesto `app.manifest` já configura `requireAdministrator` (UAC automático)

---

## 📋 Licença

MIT License — veja [LICENSE](LICENSE)
