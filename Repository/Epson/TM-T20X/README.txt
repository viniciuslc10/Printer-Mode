DRIVER: Epson TM-T20X
============================================================

PAPEL PADRÃO : 80mm / área imprimível 72mm
CONEXÕES     : USB · Serial · TCP/IP
VID/PID      : 04B8:0202
ARQUIVO INF  : EPST20X.inf
NOME DRIVER  : EPSON TM-T20X

COMO OBTER O DRIVER OFICIAL
----------------------------
https://epson.com.br → Suporte → Impressoras → TM-T20X → Drivers
Baixe o EPSON Advanced Printer Driver (APD) v4.x ou superior. Arquivo: APD_451EU.exe

ARQUIVOS NECESSÁRIOS NESTA PASTA
----------------------------------
  EPST20X.inf          ← obrigatório (INF principal)
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
