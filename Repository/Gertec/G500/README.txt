DRIVER: Gertec G500
============================================================

PAPEL PADRÃO : 80mm / área imprimível 72mm
CONEXÕES     : USB · TCP/IP
VID/PID      : 20D1:7009
ARQUIVO INF  : GertecG500.inf
NOME DRIVER  : Gertec G500

COMO OBTER O DRIVER OFICIAL
----------------------------
https://gertec.com.br → Suporte → Softwares → G500 → Driver Windows
Alta velocidade. Pacote G500_Driver_Win10.zip

ARQUIVOS NECESSÁRIOS NESTA PASTA
----------------------------------
  GertecG500.inf          ← obrigatório (INF principal)
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
