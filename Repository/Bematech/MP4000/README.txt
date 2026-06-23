DRIVER: Bematech MP-4000 TH
============================================================

PAPEL PADRÃO : 80mm / área imprimível 72mm
CONEXÕES     : USB · Serial · TCP/IP
VID/PID      : 0FE6:8120
ARQUIVO INF  : MP4000.inf
NOME DRIVER  : Bematech MP-4000 TH

COMO OBTER O DRIVER OFICIAL
----------------------------
https://bematech.com.br → Suporte → Downloads → MP-4000 TH
Suporta guilhotina parcial e total.

ARQUIVOS NECESSÁRIOS NESTA PASTA
----------------------------------
  MP4000.inf          ← obrigatório (INF principal)
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
