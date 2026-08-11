@echo off
setlocal EnableExtensions DisableDelayedExpansion

fltmc >nul 2>&1
if errorlevel 1 (
    echo ERROR: Administrator privileges are required.
    echo Right-click Uninstall.cmd and select "Run as administrator".
    goto :error
)

tasklist /FI "IMAGENAME eq SLDWORKS.exe" 2>nul | find /I "SLDWORKS.exe" >nul
if not errorlevel 1 (
    echo ERROR: Close SOLIDWORKS before uninstalling the add-in.
    goto :error
)

set "INSTALL_DIR=%ProgramData%\SolidWorksAssetExporter"
set "ADDIN=%INSTALL_DIR%\SolidWorksAssetExporter.AddIn.dll"
set "TYPELIB=%INSTALL_DIR%\SolidWorksAssetExporter.AddIn.tlb"
set "REGASM=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"

if exist "%ADDIN%" (
    if not exist "%REGASM%" (
        echo ERROR: 64-bit .NET Framework RegAsm.exe was not found.
        goto :error
    )
    "%REGASM%" "%ADDIN%" /unregister /tlb:"%TYPELIB%"
    if errorlevel 1 (
        echo ERROR: COM unregistration failed.
        goto :error
    )
)

if exist "%INSTALL_DIR%" rmdir /S /Q "%INSTALL_DIR%"
if exist "%INSTALL_DIR%" (
    echo ERROR: Failed to remove "%INSTALL_DIR%".
    goto :error
)

echo.
echo SOLIDWORKS Asset Exporter was uninstalled successfully.
echo User settings and exported data were not removed.
goto :success

:error
if /I not "%~1"=="/quiet" pause
exit /b 1

:success
if /I not "%~1"=="/quiet" pause
exit /b 0
