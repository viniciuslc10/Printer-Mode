DRIVER: Epson TM-T88V
============================================================

PAPEL PADRÃO : 80mm / área imprimível 72mm
CONEXÕES     : USB · Serial · TCP/IP
VID/PID      : 04B8:0202
ARQUIVO INF  : EPST88V.inf
NOME DRIVER  : EPSON TM-T88V

COMO OBTER O DRIVER OFICIAL
----------------------------
https://epson.com.br → Suporte → Impressoras → TM-T88V → Drivers
EPSON APD v4.x. Arquivo: APD_451EU.exe. Velocidade: 300mm/s.

ARQUIVOS NECESSÁRIOS NESTA PASTA
----------------------------------
  EPST88V.inf          ← obrigatório (INF principal)
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
