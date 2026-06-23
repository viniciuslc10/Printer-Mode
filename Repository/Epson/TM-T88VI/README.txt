DRIVER: Epson TM-T88VI
============================================================

PAPEL PADRÃO : 80mm / área imprimível 72mm
CONEXÕES     : USB · TCP/IP
VID/PID      : 04B8:0E28
ARQUIVO INF  : EPST88VI.inf
NOME DRIVER  : EPSON TM-T88VI

COMO OBTER O DRIVER OFICIAL
----------------------------
https://epson.com.br → Suporte → Impressoras → TM-T88VI → Drivers
EPSON APD v4.54 ou superior. Arquivo: APD_454EU.exe. Suporta NFC e impressão simultânea.

ARQUIVOS NECESSÁRIOS NESTA PASTA
----------------------------------
  EPST88VI.inf          ← obrigatório (INF principal)
  *.cat                   ← catálogo de assinaturas digitais
  *.dll / *.sys           ← binários do driver (se existirem)
  *.gpd / *.ppd / *.cfg  ← perfis de configuração

INSTALAÇÃO MANUAL (PowerShell como Administrador)
--------------------------------------------------
  pnputil /add-driver "/home/user/Printer-Mode/Repository$folder$inf" /install

ATENÇÃO
--------
  Não utilize drivers genéricos do Windows.
  Sempre use o driver oficial do fabricante listado acima.
