DRIVER: Gertec G250
============================================================

PAPEL PADRÃO : 80mm / área imprimível 72mm
CONEXÕES     : USB · TCP/IP
VID/PID      : 1753:0800
ARQUIVO INF  : PrinterDriver\x64\GAPrinterv1x64.INF (x64) / PrinterDriver\x86\GAPrinterv1x86.INF (x86)
NOME DRIVER  : GA-C80250 Series

ORIGEM
------
Extraído do instalador oficial "GA-Printer Driver v1.1" (o mesmo pacote que
o instalador Gertec_G250_Driver_v1.1.exe instala como programa no Windows).
Este pacote NÃO possui .cat (não assinado digitalmente) — instalação via
pnputil/Add-PrinterDriver funciona normalmente em máquinas sem imposição
estrita de assinatura de driver de impressora.

ARQUIVOS NESTA PASTA
---------------------
  PrinterDriver\x64\GAPrinterv1x64.INF   ← INF principal (64-bit)
  PrinterDriver\x86\GAPrinterv1x86.INF   ← INF principal (32-bit)
  *.GPD                                   ← perfis de modelo (GAC80250.GPD = G250)
  *.dll                                   ← binários UNIDRV/OEM

O INF lista vários modelos ("GA-C80250 Series", "GA-E200 Series",
"GA-L300 Series", "GA-S300 Series"). O G250 corresponde a
"GA-C80250 Series" (GAC80250.GPD).

INSTALAÇÃO MANUAL (PowerShell como Administrador)
--------------------------------------------------
  pnputil /add-driver "PrinterDriver\x64\GAPrinterv1x64.INF" /install
  Add-PrinterDriver -Name "GA-C80250 Series" -InfPath "PrinterDriver\x64\GAPrinterv1x64.INF"
