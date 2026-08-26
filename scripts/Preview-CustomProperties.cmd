@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0sync-custom-properties.ps1" -Schema "%~dp0custom-properties.schema.csv"
echo.
echo Preview finished. No SOLIDWORKS files were modified or saved.
pause

