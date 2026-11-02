' Windows Script Host is a GUI process: PowerShell starts with no console flash.
Option Explicit
Dim fso, shell, swapScript, command, result
Set fso = CreateObject("Scripting.FileSystemObject")
swapScript = fso.BuildPath(fso.GetParentFolderName(WScript.ScriptFullName), "Swap-Release.ps1")
If Not fso.FileExists(swapScript) Then WScript.Quit 2
Set shell = CreateObject("WScript.Shell")
command = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & swapScript & """ -ScheduledRun"
result = shell.Run(command, 0, True)
WScript.Quit result
