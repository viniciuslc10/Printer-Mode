DRIVER: Tomate MT-609
============================================================

PAPEL PADRÃO : 80mm / área imprimível 72mm
CONEXÕES     : USB · Bluetooth
VID/PID      : 0416:5012
ARQUIVO INF  : TomateMT609.inf
NOME DRIVER  : Tomate MT-609

COMO OBTER O DRIVER OFICIAL
----------------------------
https://www.multilaser.com.br → Suporte → Impressoras → MT-609 → Drivers
Tomate_MT609_Driver_Win10.zip. Inclui driver BT (Serial over BT).

ARQUIVOS NECESSÁRIOS NESTA PASTA
----------------------------------
  TomateMT609.inf          ← obrigatório (INF principal)
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
