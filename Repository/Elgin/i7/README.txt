DRIVER: Elgin i7
============================================================

PAPEL PADRÃO : 80mm / área imprimível 72mm
CONEXÕES     : USB · Serial
VID/PID      : 0DD4:00FF
ARQUIVO INF  : ElginI7.inf
NOME DRIVER  : Elgin i7

COMO OBTER O DRIVER OFICIAL
----------------------------
https://elgin.com.br → Impressoras → i7 → Downloads → Driver Windows
Serial: 115200,8,N,1

ARQUIVOS NECESSÁRIOS NESTA PASTA
----------------------------------
  ElginI7.inf          ← obrigatório (INF principal)
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
