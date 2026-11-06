param(
    [Parameter(Mandatory=$true)][string]$MappingPath,
    [Parameter(Mandatory=$true)][string]$ModelBackupDirectory,
    [string]$CatalogPath = (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'SolidWorksAssetExporter\interfaces.json')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ModelBackupDirectory = (Resolve-Path -LiteralPath $ModelBackupDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $ModelBackupDirectory 'SUCCESS.txt'))) { throw 'Model migration must complete successfully before updating the catalogue.' }
$mapping = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $MappingPath).Path) | ConvertFrom-Json
$rename = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::Ordinal)
foreach ($property in $mapping.PSObject.Properties) { $rename.Add($property.Name, [string]$property.Value) }
$CatalogPath = [IO.Path]::GetFullPath($CatalogPath)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($CatalogPath))
$gate = [IO.File]::Open($CatalogPath + '.lock', [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $existed = [IO.File]::Exists($CatalogPath)
    $catalog = if ($existed) { [IO.File]::ReadAllText($CatalogPath) | ConvertFrom-Json } else { [PSCustomObject]@{ schema_version = '1.0'; interfaces = @() } }
    if ($catalog.schema_version -ne '1.0' -or $null -eq $catalog.interfaces -or $catalog.interfaces -is [string]) { throw 'Invalid catalogue; original file preserved.' }
    $names = New-Object 'System.Collections.Generic.SortedSet[string]' ([StringComparer]::Ordinal)
    $renamed = 0
    foreach ($raw in $catalog.interfaces) {
        if ($raw -isnot [string] -or [string]::IsNullOrWhiteSpace($raw) -or $raw -match '[;；\x00-\x1f\x7f]') { throw 'Invalid interface name; original file preserved.' }
        $name = $raw.Trim()
        if ($rename.ContainsKey($name)) { $name = $rename[$name]; $renamed++ }
        [void]$names.Add($name)
    }
    foreach ($name in $rename.Values) { [void]$names.Add($name) }
    $catalog.interfaces = @($names)
    $backup = Join-Path $ModelBackupDirectory 'interface-catalog-before.json'
    if ($existed) { [IO.File]::Copy($CatalogPath, $backup, $false) }
    $temporary = $CatalogPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, ($catalog | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
        if ($existed) { [IO.File]::Replace($temporary, $CatalogPath, [NullString]::Value) } else { [IO.File]::Move($temporary, $CatalogPath) }
        $check = [IO.File]::ReadAllText($CatalogPath) | ConvertFrom-Json
        if (@($check.interfaces).Count -ne $names.Count -or @($check.interfaces | Where-Object { -not $names.Contains($_) }).Count -gt 0) { throw 'Catalogue readback mismatch.' }
        $result = [PSCustomObject]@{ path = $CatalogPath; existed = $existed; backup = $backup; renamed = $renamed; total = $names.Count; sha256 = (Get-FileHash -LiteralPath $CatalogPath).Hash }
        [IO.File]::WriteAllText((Join-Path $ModelBackupDirectory 'catalogue-result.json'), ($result | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
        Write-Output ('CATALOGUE_UPDATED total=' + $names.Count + ' renamed=' + $renamed)
    }
    finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}
finally { $gate.Dispose() }
