Set oShell = CreateObject("WScript.Shell")
oShell.Run "schtasks /Run /TN ""PrinterMode""", 0, False
