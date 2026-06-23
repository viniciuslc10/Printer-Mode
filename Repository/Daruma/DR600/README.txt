DRIVER: Daruma DR600
============================================================

PAPEL PADRÃO : 80mm / fiscal/não-fiscal ECF
CONEXÕES     : USB · Serial RS-232
VID/PID      : N/A:N/A
ARQUIVO INF  : DR600.inf
NOME DRIVER  : Daruma DR600

COMO OBTER O DRIVER OFICIAL
----------------------------
https://daruma.com.br → Suporte → ECF → DR-600 → Downloads → Driver
ECF: use também o SAT Daruma. Serial: 9600,8,N,1

ARQUIVOS NECESSÁRIOS NESTA PASTA
----------------------------------
  DR600.inf          ← obrigatório (INF principal)
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
