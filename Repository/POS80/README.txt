DRIVER: POS-80 (Impressoras Genéricas 80mm)
============================================================

PAPEL PADRÃO : 80mm / área imprimível 72mm
CONEXÕES     : USB
VID/PID      : 0483:5750 (ou variantes - veja abaixo)
ARQUIVO INF  : POS80.inf
NOME DRIVER  : POS-80 Printer

SOBRE ESTE MODELO
------------------
"POS-80" é nome genérico para impressoras térmicas 80mm fabricadas
por diversas empresas chinesas sem marca específica. VID/PID comuns:

  0483:5750  → Chip STM32
  0416:5012  → Chip Winbond
  1A86:7523  → Chip CH340 (porta COM virtual)
  0DD4:0200  → Diversas OEM

COMO OBTER O DRIVER
--------------------
Opção 1 (recomendada): Driver nativo Windows
  - Conecte USB → Windows instala "USB Printing Support" automaticamente
  - Adicione impressora manual → use "Generic / Text Only" apenas para teste

Opção 2: CH340 (se aparecer como COM)
  - https://www.wch-ic.com/downloads/CH341SER_EXE.html

Opção 3: Driver ESC/POS compatível
  - Baixe o EPSON APD e selecione modelo compatível (TM-T20X)
  - Funciona com a maioria das impressoras ESC/POS genéricas

INSTALAÇÃO
-----------
  pnputil /add-driver "POS80.inf" /install
