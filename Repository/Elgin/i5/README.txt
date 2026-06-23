DRIVER: Elgin i5
============================================================

PAPEL PADRÃO : 58mm / área imprimível 50mm
CONEXÕES     : USB
VID/PID      : 0DD4:00FE
ARQUIVO INF  : ElginI5.inf
NOME DRIVER  : Elgin i5

COMO OBTER O DRIVER OFICIAL
----------------------------
https://elgin.com.br → Impressoras → i5 → Downloads → Driver Windows
Modelo compacto 58mm.

ARQUIVOS NECESSÁRIOS NESTA PASTA
----------------------------------
  ElginI5.inf          ← obrigatório (INF principal)
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
