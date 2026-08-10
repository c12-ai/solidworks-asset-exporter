param(
    [string]$BuildDirectory,
    [string]$InstallDirectory = (Join-Path $env:ProgramData 'SolidWorksAssetExporter')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    $BuildDirectory = Join-Path $repoRoot 'src\SolidWorksAssetExporter.AddIn\bin\Release'
}
$build = (Resolve-Path -LiteralPath $BuildDirectory).Path
$addin = Join-Path $build 'SolidWorksAssetExporter.AddIn.dll'
$core = Join-Path $build 'SolidWorksAssetExporter.Core.dll'
if (-not (Test-Path -LiteralPath $addin) -or -not (Test-Path -LiteralPath $core)) { throw 'Build output is incomplete. Run scripts/build-addin.ps1 first.' }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script from an elevated PowerShell window.' }

$install = [IO.Path]::GetFullPath($InstallDirectory)
New-Item -ItemType Directory -Path $install -Force | Out-Null
Copy-Item -LiteralPath $addin -Destination $install -Force
Copy-Item -LiteralPath $core -Destination $install -Force
Get-ChildItem -LiteralPath $build -Filter 'SolidWorks.Interop.*.dll' -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $install -Force
}

$regasm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
if (-not (Test-Path -LiteralPath $regasm)) { throw '.NET Framework 64-bit RegAsm.exe was not found.' }
$installedAddin = Join-Path $install 'SolidWorksAssetExporter.AddIn.dll'
$typeLibrary = Join-Path $install 'SolidWorksAssetExporter.AddIn.tlb'
& $regasm $installedAddin /codebase "/tlb:$typeLibrary"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Installed SOLIDWORKS Asset Exporter to $install"
