DRIVER: Epson TM-T20III
============================================================

PAPEL PADRÃO : 80mm / área imprimível 72mm
CONEXÕES     : USB · TCP/IP
VID/PID      : 04B8:0232
ARQUIVO INF  : EPST20III.inf
NOME DRIVER  : EPSON TM-T20III

COMO OBTER O DRIVER OFICIAL
----------------------------
https://epson.com.br → Suporte → Impressoras → TM-T20III → Drivers
EPSON APD v4.5x. Arquivo: APD_454EU.exe. Sucessora da TM-T20X.

ARQUIVOS NECESSÁRIOS NESTA PASTA
----------------------------------
  EPST20III.inf          ← obrigatório (INF principal)
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
