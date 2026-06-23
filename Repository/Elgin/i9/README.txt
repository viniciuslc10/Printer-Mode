DRIVER: Elgin i9
============================================================

PAPEL PADRÃO : 80mm / área imprimível 72mm
CONEXÕES     : USB · TCP/IP
VID/PID      : 0DD4:0101
ARQUIVO INF  : ElginI9.inf
NOME DRIVER  : Elgin i9

COMO OBTER O DRIVER OFICIAL
----------------------------
https://elgin.com.br → Impressoras → i9 → Downloads → Driver Windows
Pacote Elgin_Driver_i9_v3.0_x64.zip. Inclui utilitário de configuração.

ARQUIVOS NECESSÁRIOS NESTA PASTA
----------------------------------
  ElginI9.inf          ← obrigatório (INF principal)
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
