DRIVER: Bematech MP-2800 TH
============================================================

PAPEL PADRÃO : 58mm / área imprimível 50mm
CONEXÕES     : USB · Serial
VID/PID      : 0FE6:811F
ARQUIVO INF  : MP2800.inf
NOME DRIVER  : Bematech MP-2800 TH

COMO OBTER O DRIVER OFICIAL
----------------------------
https://bematech.com.br → Suporte → Downloads → MP-2800 TH
Baud rate padrão: 9600,8,N,1

ARQUIVOS NECESSÁRIOS NESTA PASTA
----------------------------------
  MP2800.inf          ← obrigatório (INF principal)
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
