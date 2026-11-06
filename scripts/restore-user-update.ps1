param([Parameter(Mandatory=$true)][string]$BackupFile)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Get-Process SLDWORKS -ErrorAction SilentlyContinue) { throw 'Close SOLIDWORKS before restoring the add-in.' }
$expected = 'Registry::HKEY_CURRENT_USER\Software\Classes\CLSID\{b5ec0c01-12dd-4afa-88ee-42e1178ba63d}\InprocServer32'
$records = @(Get-Content -LiteralPath $BackupFile -Raw | ConvertFrom-Json)
foreach ($r in $records) {
    if ($r.Path -ne $expected -and -not $r.Path.StartsWith($expected + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Backup contains an unexpected registry path.' }
}
$previous = Join-Path (Split-Path -Parent $BackupFile) 'previous-payload'
if (Test-Path -LiteralPath $previous) {
    $install = Join-Path $env:LOCALAPPDATA 'SolidWorksAssetExporter\Addin'
    foreach ($f in Get-ChildItem -LiteralPath $previous -File) { Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $install $f.Name) -Force }
}
foreach ($r in $records) { Set-ItemProperty -LiteralPath $r.Path -Name CodeBase -Value $r.CodeBase }
Write-Output 'Restored the previous per-user add-in registration.'
