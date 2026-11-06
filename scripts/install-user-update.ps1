param([string]$PayloadDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Get-Process SLDWORKS -ErrorAction SilentlyContinue) { throw 'Close SOLIDWORKS before updating the add-in.' }
if ([string]::IsNullOrWhiteSpace($PayloadDirectory)) {
    $PayloadDirectory = Join-Path $PSScriptRoot 'payload'
    if (-not (Test-Path -LiteralPath $PayloadDirectory)) {
        $PayloadDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\SolidWorksAssetExporter.AddIn\bin\Release'
    }
}
$clsid = '{b5ec0c01-12dd-4afa-88ee-42e1178ba63d}'
$machineKey = 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\SolidWorks\Addins\' + $clsid
if (-not (Test-Path -LiteralPath $machineKey)) { throw 'An existing machine registration is required. For a first install, run Install.cmd as administrator.' }
$userKey = 'Registry::HKEY_CURRENT_USER\Software\Classes\CLSID\' + $clsid + '\InprocServer32'
if (-not (Test-Path -LiteralPath $userKey)) { throw 'This update requires an existing per-user COM registration. Use Install.cmd for a first install.' }
$install = Join-Path $env:LOCALAPPDATA 'SolidWorksAssetExporter\Addin'
$backup = Join-Path $env:LOCALAPPDATA ('SolidWorksAssetExporter\UpdateBackups\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$files = @('SolidWorksAssetExporter.AddIn.dll','SolidWorksAssetExporter.Core.dll','SolidWorks.Interop.sldworks.dll','SolidWorks.Interop.swconst.dll','SolidWorks.Interop.swpublished.dll')
foreach ($name in $files) {
    if (-not (Test-Path -LiteralPath (Join-Path $PayloadDirectory $name) -PathType Leaf)) { throw "Missing payload: $name" }
}
New-Item -ItemType Directory -Path $backup -Force | Out-Null
$registrations = @($userKey) + @(Get-ChildItem -LiteralPath $userKey | ForEach-Object { 'Registry::' + $_.Name })
$saved = @($registrations | ForEach-Object {
    $value = Get-ItemProperty -LiteralPath $_
    [PSCustomObject]@{ Path = $_; CodeBase = $value.CodeBase }
})
$saved | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backup 'com-codebases.json') -Encoding UTF8
if (Test-Path -LiteralPath $install) { Copy-Item -LiteralPath $install -Destination (Join-Path $backup 'previous-payload') -Recurse }
New-Item -ItemType Directory -Path $install -Force | Out-Null
foreach ($name in $files) {
    $source = Join-Path $PayloadDirectory $name
    $target = Join-Path $install $name
    Copy-Item -LiteralPath $source -Destination $target -Force
    if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $target).Hash) { throw "Hash mismatch: $name" }
}
$codebase = ([Uri](Join-Path $install 'SolidWorksAssetExporter.AddIn.dll')).AbsoluteUri
foreach ($registration in $registrations) { Set-ItemProperty -LiteralPath $registration -Name CodeBase -Value $codebase }
Write-Output "Installed user update: $install"
Write-Output "Rollback registration backup: $backup\com-codebases.json"
