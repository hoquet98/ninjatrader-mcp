' Launch the NT8 breach monitor hidden (no console). Target of the scheduled task.
CreateObject("WScript.Shell").Run """C:\Program Files\Python313\pythonw.exe"" ""C:\Users\Administrator\nt8-mcp\nt8_ingest\breach_monitor.py""", 0, False
