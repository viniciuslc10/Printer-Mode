DRIVER: Daruma DR700
============================================================

PAPEL PADRÃO : 80mm / 80 colunas
CONEXÕES     : USB · Serial
VID/PID      : N/A:N/A
ARQUIVO INF  : DR700.inf
NOME DRIVER  : Daruma DR700

COMO OBTER O DRIVER OFICIAL
----------------------------
https://daruma.com.br → Suporte → Impressoras → DR-700 → Downloads → Driver
Daruma_DR700_Driver_v1.0_Win10.zip. Serial: 9600,8,N,1

ARQUIVOS NECESSÁRIOS NESTA PASTA
----------------------------------
  DR700.inf          ← obrigatório (INF principal)
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
