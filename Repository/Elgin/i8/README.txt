DRIVER: Elgin i8
============================================================

PAPEL PADRÃO : 80mm / área imprimível 72mm
CONEXÕES     : USB · TCP/IP
VID/PID      : 0DD4:0100
ARQUIVO INF  : ElginI8.inf
NOME DRIVER  : Elgin i8

COMO OBTER O DRIVER OFICIAL
----------------------------
https://elgin.com.br → Impressoras → i8 → Downloads → Driver Windows
Elgin_Driver_i8_v2.5_x64.zip

ARQUIVOS NECESSÁRIOS NESTA PASTA
----------------------------------
  ElginI8.inf          ← obrigatório (INF principal)
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
