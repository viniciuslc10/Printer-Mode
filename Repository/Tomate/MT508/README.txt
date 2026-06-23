DRIVER: Tomate MT-508
============================================================

PAPEL PADRÃO : 58mm / área imprimível 48mm
CONEXÕES     : USB
VID/PID      : 0416:5011
ARQUIVO INF  : TomateMT508.inf
NOME DRIVER  : Tomate MT-508

COMO OBTER O DRIVER OFICIAL
----------------------------
https://www.multilaser.com.br → Suporte → Impressoras → MT-508 → Drivers
Tomate_MT508_Driver_Win10.zip. Chip Winbond VID=0416.

ARQUIVOS NECESSÁRIOS NESTA PASTA
----------------------------------
  TomateMT508.inf          ← obrigatório (INF principal)
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
