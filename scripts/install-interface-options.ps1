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
    $fields = @('connection_interface', 'accepts_interfaces', 'slots_num')
    if ($file -eq 'asem.asmprp') { $fields += 'assembly_version' }
    foreach ($field in $fields) {
        $nodes = $xml.SelectNodes("//Control[@PropName='$field']")
        if ($nodes.Count -eq 0) { continue }
        if ($nodes.Count -ne 1 -or $nodes[0].GetAttribute('ApplyTo') -ne 'Global') {
            throw "Expected one file-level control for $field in $path; original files preserved."
        }
        $nodes[0].SetAttribute('ReadOnly', 'True')
        if (-not $nodes[0].GetAttribute('Label').EndsWith('（插件维护）')) {
            $nodes[0].SetAttribute('Label', $field + '（插件维护）')
        }
    }
    $assetVersion = $xml.SelectNodes("//Control[@PropName='asset_version']")
    if ($assetVersion.Count -gt 1 -or ($assetVersion.Count -eq 1 -and $assetVersion[0].GetAttribute('ApplyTo') -ne 'Global')) {
        throw "Expected one file-level asset_version control in $path; original files preserved."
    }
    if ($assetVersion.Count -eq 1) {
        $robot = $xml.SelectSingleNode("//Control[@PropName='class']/Data/Item[@PropValue='robot']")
        if ($null -eq $robot) {
            $assetVersion[0].SetAttribute('ReadOnly', 'True')
            $assetVersion[0].SetAttribute('Label', 'asset_version（插件维护）')
        }
        else {
            foreach ($link in @($xml.SelectNodes("//ChildControl[@PropName='asset_version']"))) {
                if ($link.ParentNode -ne $robot) { [void]$link.ParentNode.RemoveChild($link) }
            }
            $link = $robot.SelectSingleNode("ChildControl[@PropName='asset_version']")
            if ($null -eq $link) {
                $link = $xml.CreateElement('ChildControl')
                $link.SetAttribute('PropName', 'asset_version')
                [void]$robot.AppendChild($link)
            }
            $link.SetAttribute('Visibility', 'IsChecked=true')
            $assetVersion[0].SetAttribute('Label', 'Robot 外部引用版本（asset_version）')
        }
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
    Write-Output 'Refresh the SOLIDWORKS Custom Properties pane to load the updated templates.'
}
catch { Write-Warning ('Update interrupted; files already updated retain their changes. Backups: ' + $BackupDirectory); throw }
finally { $gate.Dispose() }
