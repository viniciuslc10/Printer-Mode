DRIVER: Bematech MP-100S TH
============================================================

PAPEL PADRÃO : 58mm / área imprimível 50mm
CONEXÕES     : Serial RS-232
VID/PID      : 0FE6:8100
ARQUIVO INF  : MP100S.inf
NOME DRIVER  : Bematech MP-100S TH

COMO OBTER O DRIVER OFICIAL
----------------------------
https://bematech.com.br → Suporte → Downloads → MP-100S TH
Apenas serial. Configure: 9600,8,N,1 via Gerenciador de Dispositivos.

ARQUIVOS NECESSÁRIOS NESTA PASTA
----------------------------------
  MP100S.inf          ← obrigatório (INF principal)
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
