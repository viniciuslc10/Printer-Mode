DRIVER: Diebold IM333
============================================================

PAPEL PADRÃO : 58mm / área imprimível 50mm
CONEXÕES     : USB
VID/PID      : 05F9:4101
ARQUIVO INF  : DieboldIM333.inf
NOME DRIVER  : Diebold IM333

COMO OBTER O DRIVER OFICIAL
----------------------------
https://dieboldnixdorf.com → Support → POS Printers → IM333 → Windows Driver
Driver: Diebold_IM333_WinDrv.zip.

ARQUIVOS NECESSÁRIOS NESTA PASTA
----------------------------------
  DieboldIM333.inf          ← obrigatório (INF principal)
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
