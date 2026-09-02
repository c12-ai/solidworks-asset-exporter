@echo off
setlocal EnableExtensions DisableDelayedExpansion

fltmc >nul 2>&1
if errorlevel 1 (
    echo ERROR: Administrator privileges are required.
    echo Right-click Install.cmd and select "Run as administrator".
    goto :error
)

tasklist /FI "IMAGENAME eq SLDWORKS.exe" 2>nul | find /I "SLDWORKS.exe" >nul
if not errorlevel 1 (
    echo ERROR: Close SOLIDWORKS before installing the add-in.
    goto :error
)

set "PAYLOAD=%~dp0payload"
set "INSTALL_DIR=%ProgramData%\SolidWorksAssetExporter"
set "REGASM=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"
set "STARTUP_SCRIPT=%~dp0set-interactive-user-startup.ps1"

if not exist "%REGASM%" (
    echo ERROR: 64-bit .NET Framework RegAsm.exe was not found.
    goto :error
)
if not exist "%STARTUP_SCRIPT%" (
    echo ERROR: Installation package is missing set-interactive-user-startup.ps1.
    goto :error
)

for %%F in (
    SolidWorksAssetExporter.AddIn.dll
    SolidWorksAssetExporter.Core.dll
    SolidWorks.Interop.sldworks.dll
    SolidWorks.Interop.swconst.dll
    SolidWorks.Interop.swpublished.dll
) do (
    if not exist "%PAYLOAD%\%%F" (
        echo ERROR: Installation package is missing %%F.
        goto :error
    )
)

if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"
if errorlevel 1 goto :copy_error

for %%F in (
    SolidWorksAssetExporter.AddIn.dll
    SolidWorksAssetExporter.Core.dll
    SolidWorks.Interop.sldworks.dll
    SolidWorks.Interop.swconst.dll
    SolidWorks.Interop.swpublished.dll
) do (
    copy /Y "%PAYLOAD%\%%F" "%INSTALL_DIR%\%%F" >nul
    if errorlevel 1 goto :copy_error
)

"%REGASM%" "%INSTALL_DIR%\SolidWorksAssetExporter.AddIn.dll" /codebase /tlb:"%INSTALL_DIR%\SolidWorksAssetExporter.AddIn.tlb"
if errorlevel 1 (
    echo ERROR: COM registration failed.
    goto :error
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%STARTUP_SCRIPT%" -Mode Enable
if errorlevel 1 (
    echo ERROR: Failed to enable the add-in for the interactive Windows user.
    goto :error
)

echo.
echo SOLIDWORKS Asset Exporter was installed successfully.
echo Restart SOLIDWORKS. The add-in has been enabled for the interactive Windows user.
goto :success

:copy_error
echo ERROR: Failed to copy files to "%INSTALL_DIR%".
echo Ensure SOLIDWORKS is closed and try again.

:error
if /I not "%~1"=="/quiet" pause
exit /b 1

:success
if /I not "%~1"=="/quiet" pause
exit /b 0
