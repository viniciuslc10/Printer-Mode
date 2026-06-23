DRIVER: Bematech MP-4200 TH
============================================================

PAPEL PADRÃO : 80mm / área imprimível 72mm
CONEXÕES     : USB · Serial · TCP/IP
VID/PID      : 0FE6:811E
ARQUIVO INF  : MP4200.inf
NOME DRIVER  : Bematech MP-4200 TH

COMO OBTER O DRIVER OFICIAL
----------------------------
https://bematech.com.br → Suporte → Downloads → MP-4200 TH → Windows 10/11
Pacote: Bematech_Driver_v1.9_Win10_x64.zip

ARQUIVOS NECESSÁRIOS NESTA PASTA
----------------------------------
  MP4200.inf          ← obrigatório (INF principal)
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
