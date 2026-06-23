DRIVER: Diebold IM453
============================================================

PAPEL PADRÃO : 80mm / área imprimível 72mm
CONEXÕES     : USB · TCP/IP
VID/PID      : 05F9:4100
ARQUIVO INF  : DieboldIM453.inf
NOME DRIVER  : Diebold IM453

COMO OBTER O DRIVER OFICIAL
----------------------------
https://dieboldnixdorf.com → Support → POS Printers → IM453 → Windows Driver
Driver: Diebold_IM453_WinDrv.zip. ESC/POS compatível.

ARQUIVOS NECESSÁRIOS NESTA PASTA
----------------------------------
  DieboldIM453.inf          ← obrigatório (INF principal)
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
