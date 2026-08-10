param([string]$InstallDirectory = (Join-Path $env:ProgramData 'SolidWorksAssetExporter'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script from an elevated PowerShell window.' }

$install = [IO.Path]::GetFullPath($InstallDirectory)
$root = [IO.Path]::GetPathRoot($install)
if ([string]::IsNullOrWhiteSpace($install) -or $install -eq $root -or $install.Length -lt 10) { throw 'Refusing to remove an unsafe install path.' }
$addin = Join-Path $install 'SolidWorksAssetExporter.AddIn.dll'
$regasm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
if (Test-Path -LiteralPath $addin) {
    & $regasm $addin /unregister
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (Test-Path -LiteralPath $install) { Remove-Item -LiteralPath $install -Recurse -Force }
Write-Host "Uninstalled SOLIDWORKS Asset Exporter from $install"
