@echo off
setlocal EnableExtensions DisableDelayedExpansion
PowerShell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-codex.ps1" %*
set "SETUP_EXIT=%ERRORLEVEL%"
echo.
pause
exit /b %SETUP_EXIT%
