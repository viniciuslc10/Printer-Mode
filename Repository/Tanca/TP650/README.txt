DRIVER: Tanca TP-650
============================================================

PAPEL PADRÃO : 80mm / área imprimível 72mm
CONEXÕES     : USB · TCP/IP · Bluetooth
VID/PID      : 0493:8761
ARQUIVO INF  : TancaTP650.inf
NOME DRIVER  : Tanca TP-650

COMO OBTER O DRIVER OFICIAL
----------------------------
https://tanca.com.br → Suporte → Downloads → TP-650 → Driver
Inclui driver Bluetooth. Tanca_TP650_Driver_v1.2_Win10.zip

ARQUIVOS NECESSÁRIOS NESTA PASTA
----------------------------------
  TancaTP650.inf          ← obrigatório (INF principal)
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
