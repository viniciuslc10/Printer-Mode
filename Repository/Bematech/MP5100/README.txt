DRIVER: Bematech MP-5100 TH
============================================================

PAPEL PADRÃO : 80mm / área imprimível 72mm
CONEXÕES     : USB · TCP/IP · Bluetooth
VID/PID      : 0FE6:8121
ARQUIVO INF  : MP5100.inf
NOME DRIVER  : Bematech MP-5100 TH

COMO OBTER O DRIVER OFICIAL
----------------------------
https://bematech.com.br → Suporte → Downloads → MP-5100 TH
Inclui driver BT e utilitário de configuração.

ARQUIVOS NECESSÁRIOS NESTA PASTA
----------------------------------
  MP5100.inf          ← obrigatório (INF principal)
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
