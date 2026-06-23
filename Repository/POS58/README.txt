DRIVER: POS-58 (Impressoras Genéricas 58mm)
============================================================

PAPEL PADRÃO : 58mm / área imprimível 48mm
CONEXÕES     : USB
VID/PID      : 0483:5720 (ou variantes - veja abaixo)
ARQUIVO INF  : POS58.inf
NOME DRIVER  : POS-58 Printer

SOBRE ESTE MODELO
------------------
"POS-58" é um nome genérico usado por dezenas de fabricantes chineses.
Os VID/PID mais comuns são:

  0483:5720  → Chip STMicroelectronics STM32
  0416:5011  → Chip Winbond
  1A86:7523  → Chip CH340 (aparece como COM virtual)
  1FC9:2016  → Chip NXP
  20D1:7008  → Diversas marcas brancas

COMO OBTER O DRIVER
--------------------
Opção 1 (recomendada): Driver USB CDC padrão do Windows
  - Windows 10/11 detecta automaticamente como "USB Printing Support"
  - Se não detectar, use: pnputil /add-driver POS58.inf /install

Opção 2: Se aparecer como porta COM (CH340):
  - Baixe o driver CH340: https://www.wch-ic.com/downloads/CH341SER_EXE.html
  - Instale e use a porta COM virtual criada

Opção 3: Driver genérico ESC/POS
  - Use o driver "Generic / Text Only" como último recurso
  - Configure papel 58mm manualmente nas propriedades

ARQUIVOS NECESSÁRIOS NESTA PASTA
----------------------------------
  POS58.inf     ← INF genérico USB de impressora
  *.cat         ← catálogo de assinaturas

INSTALAÇÃO
-----------
  pnputil /add-driver "POS58.inf" /install
