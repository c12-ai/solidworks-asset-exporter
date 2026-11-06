param(
    [Parameter(Mandatory = $true)][string]$TemplateDirectory,
    [string]$CatalogPath = (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'SolidWorksAssetExporter\interfaces.json'),
    [string]$CandidatesPath,
    [string]$BackupDirectory,
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $CandidatesPath) {
    $CandidatesPath = Join-Path $PSScriptRoot 'interfaces\interface-candidates.json'
    if (-not (Test-Path -LiteralPath $CandidatesPath -PathType Leaf)) {
        $CandidatesPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'templates\interfaces\interface-candidates.json'
    }
}
if (-not $BackupDirectory) {
    $BackupDirectory = Join-Path $PSScriptRoot ('interface-backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
}
$TemplateDirectory = (Resolve-Path -LiteralPath $TemplateDirectory).Path
$CatalogPath = [IO.Path]::GetFullPath($CatalogPath)
$BackupDirectory = [IO.Path]::GetFullPath($BackupDirectory)
$utf8 = New-Object Text.UTF8Encoding($false)

function Read-Catalog([string]$path) {
    $data = [IO.File]::ReadAllText($path) | ConvertFrom-Json
    if ($data.schema_version -ne '1.0' -or $null -eq $data.interfaces -or $data.interfaces -is [string]) { throw "Unsupported catalogue: $path" }
    foreach ($value in $data.interfaces) {
        if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value) -or $value -ne $value.Trim() -or $value -match '[;；\x00-\x1f\x7f]') { throw "Invalid interface name in $path" }
    }
    return $data
}
function File-Hash([string]$path) {
    if (Test-Path -LiteralPath $path -PathType Leaf) { return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    return $null
}

$seed = Read-Catalog $CandidatesPath
$catalogHash = File-Hash $CatalogPath
$current = if ($catalogHash) { Read-Catalog $CatalogPath } else { [PSCustomObject]@{ schema_version = '1.0'; interfaces = @() } }
$names = New-Object 'System.Collections.Generic.SortedSet[string]' ([StringComparer]::Ordinal)
$caseNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($name in $current.interfaces) { [void]$names.Add($name); [void]$caseNames.Add($name) }
$added = 0
foreach ($name in $seed.interfaces) {
    # Preserve the user's spelling when only letter case differs from a candidate.
    if ($caseNames.Add($name)) { [void]$names.Add($name); $added++ }
}
$current.interfaces = @($names)
$plans = New-Object 'System.Collections.Generic.List[object]'

foreach ($file in @('part.prtprp', 'asem.asmprp')) {
    $path = Join-Path $TemplateDirectory $file
    $raw = [IO.File]::ReadAllText($path)
    $xml = New-Object Xml.XmlDocument
    $xml.PreserveWhitespace = $true
    $xml.LoadXml($raw)
    foreach ($field in @('connection_interface', 'accepts_interfaces')) {
        $nodes = $xml.SelectNodes("//Control[@PropName='$field']")
        if ($nodes.Count -ne 1 -or $nodes[0].GetAttribute('ApplyTo') -ne 'Global' -or $nodes[0].GetAttribute('Type') -ne 'TextBox') {
            throw "Expected one file-level textbox for $field in $path; original files preserved."
        }
        $nodes[0].SetAttribute('ReadOnly', 'True')
        $nodes[0].SetAttribute('Label', $field + '（插件维护）')
    }
    $updated = $xml.OuterXml
    if ($updated -ne $raw) { $plans.Add([PSCustomObject]@{ Path = $path; Hash = (File-Hash $path); Content = $updated }) }
}
if ($added -gt 0 -or -not $catalogHash) {
    $plans.Add([PSCustomObject]@{ Path = $CatalogPath; Hash = $catalogHash; Content = ($current | ConvertTo-Json -Depth 6) })
}
Write-Output "CANDIDATES=$(@($seed.interfaces).Count) ADDED=$added TOTAL=$($names.Count)"
foreach ($plan in $plans) { Write-Output ('UPDATE=' + $plan.Path) }
if (-not $Apply) { Write-Output 'PREVIEW_ONLY: no files or settings changed.'; exit 0 }
if ($plans.Count -eq 0) { Write-Output 'ALREADY_CURRENT: no files changed.'; exit 0 }

# Match the plug-in catalogue writer's lock; reject stale previews before any write.
$catalogParent = [IO.Path]::GetDirectoryName($CatalogPath)
[void][IO.Directory]::CreateDirectory($catalogParent)
$gate = [IO.File]::Open($CatalogPath + '.lock', [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    if ((File-Hash $CatalogPath) -ne $catalogHash) { throw 'Catalogue changed during preparation; please retry.' }
    foreach ($plan in $plans) {
        if ((File-Hash $plan.Path) -ne $plan.Hash) { throw "File changed during preparation: $($plan.Path)" }
        if (Test-Path -LiteralPath $plan.Path) {
            if ((Get-Item -LiteralPath $plan.Path).IsReadOnly) { throw "Read-only file: $($plan.Path)" }
        }
    }
    [void][IO.Directory]::CreateDirectory($BackupDirectory)
    $manifest = @()
    for ($i = 0; $i -lt $plans.Count; $i++) {
        $plan = $plans[$i]
        $backup = Join-Path $BackupDirectory ($i.ToString() + '-' + [IO.Path]::GetFileName($plan.Path))
        if ($plan.Hash) { [IO.File]::Copy($plan.Path, $backup, $false) }
        $manifest += [PSCustomObject]@{ path = $plan.Path; existed = [bool]$plan.Hash; sha256 = $plan.Hash; backup = $backup }
    }
    [IO.File]::WriteAllText((Join-Path $BackupDirectory 'manifest.json'), (ConvertTo-Json -InputObject $manifest -Depth 5), $utf8)
    foreach ($plan in $plans) {
        if ((File-Hash $plan.Path) -ne $plan.Hash) { throw "File changed before replacement: $($plan.Path)" }
        $temporary = $plan.Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        try {
            [IO.File]::WriteAllText($temporary, $plan.Content, $utf8)
            $expectedHash = File-Hash $temporary
            if ($plan.Hash) { [IO.File]::Replace($temporary, $plan.Path, [NullString]::Value) } else { [IO.File]::Move($temporary, $plan.Path) }
            if ((File-Hash $plan.Path) -ne $expectedHash) { throw "Write verification failed: $($plan.Path)" }
            Write-Output ('UPDATED=' + $plan.Path)
        }
        finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
    }
    Write-Output ('BACKUP=' + $BackupDirectory)
    Write-Output 'Close and reopen the interface dialog and Custom Properties pane to refresh. No model properties were changed.'
}
catch { Write-Warning ('Update interrupted; files already updated retain their changes. Backups: ' + $BackupDirectory); throw }
finally { $gate.Dispose() }
