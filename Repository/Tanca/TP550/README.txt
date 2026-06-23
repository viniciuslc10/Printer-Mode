DRIVER: Tanca TP-550
============================================================

PAPEL PADRÃO : 80mm / área imprimível 72mm
CONEXÕES     : USB · Serial · TCP/IP
VID/PID      : 0493:8760
ARQUIVO INF  : TancaTP550.inf
NOME DRIVER  : Tanca TP-550

COMO OBTER O DRIVER OFICIAL
----------------------------
https://tanca.com.br → Suporte → Downloads → TP-550 → Driver
Tanca_TP550_Driver_v1.0_Win10.zip. Serial: 115200,8,N,1

ARQUIVOS NECESSÁRIOS NESTA PASTA
----------------------------------
  TancaTP550.inf          ← obrigatório (INF principal)
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
