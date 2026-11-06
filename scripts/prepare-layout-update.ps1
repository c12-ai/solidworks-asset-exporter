param([string]$RunDirectory)
$ErrorActionPreference = 'Stop'
$run = [IO.Path]::GetFullPath($RunDirectory)
$snapshot = Get-Content -LiteralPath (Join-Path $run 'current-snapshot\before.json') -Raw | ConvertFrom-Json
$latest = Get-Content -LiteralPath (Join-Path $run 'latest-properties.json') -Raw | ConvertFrom-Json
$manifest = Get-Content -LiteralPath (Join-Path $run 'latest-source-manifest.json') -Raw | ConvertFrom-Json
$stage = Join-Path $run 'organized-working'
$oldByName = @{}
foreach ($m in $snapshot.Models) { $oldByName[(Split-Path -Leaf $m.Path)] = $m }
$renames = @{
    '25ml移液管盒支架.SLDPRT' = '25ml离心管盒支架.SLDPRT'
    '电动助吸器.SLDPRT' = '电动助吸器简易建模.SLDPRT'
}
$updates = @()
foreach ($m in $latest.Models) {
    $name = Split-Path -Leaf $m.Path
    if ([IO.Path]::GetExtension($name) -ine '.SLDPRT') { continue }
    $oldName = if ($renames.ContainsKey($name)) { $renames[$name] } else { $name }
    if (-not $oldByName.ContainsKey($oldName)) { continue }
    $old = $oldByName[$oldName]
    if ($m.Created -cne $old.Created) { throw "Creation identity mismatch: $name" }
    $requiredConfigs = @($snapshot.Components | Where-Object Path -eq $old.Path | Select-Object -ExpandProperty Configuration -Unique)
    foreach ($config in $requiredConfigs) { if ($config -notin $m.Configurations) { throw "Missing referenced configuration $config in $name" } }
    $source = @($manifest | Where-Object { (Split-Path -Leaf $_.Source) -eq $name })
    if ($source.Count -ne 1) { throw "Ambiguous latest source: $name" }
    if ((Get-FileHash -LiteralPath $source[0].Source -Algorithm SHA256).Hash -ne $source[0].Sha256) { throw "Latest source changed: $name" }
    $updates += [pscustomobject]@{ Name=$oldName; LatestSource=$source[0].Source; LatestSha256=$source[0].Sha256; OriginalPath=$old.Path; Created=$old.Created; Renamed=($name -cne $oldName) }
}
$changedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($u in $updates) { [void]$changedPaths.Add($u.OriginalPath) }
$occ = @{}
foreach ($c in $snapshot.Components) { $occ[$c.Context] = $c }
foreach ($c in $snapshot.Components) {
    if ($c.Path -notin $updates.OriginalPath) { continue }
    $context = $c.Context
    while ($context.Contains('/')) {
        $context = $context.Substring(0,$context.LastIndexOf('/'))
        if (-not $occ.ContainsKey($context)) { throw "Missing parent occurrence: $context" }
        [void]$changedPaths.Add($occ[$context].Path)
    }
}
$versions = @{}
foreach ($path in $changedPaths) {
    $m = $oldByName[(Split-Path -Leaf $path)]
    if ($m.Raw.is_asset -notin '1','true' -or $m.Raw.class -eq 'robot') { continue }
    $version = 0
    if (-not [int]::TryParse($m.Raw.asset_version,[ref]$version) -or $version -le 0) { throw "Invalid asset version: $path" }
    $versions[(Split-Path -Leaf $path)] = ($version + 1).ToString()
}
$root = $oldByName[(Split-Path -Leaf $snapshot.Root)]
$rootVersion = 0
if (-not [int]::TryParse($root.Raw.assembly_version,[ref]$rootVersion) -or $rootVersion -le 0) { throw 'Invalid assembly version' }
$plan = [pscustomobject]@{ Directory=$stage; Snapshot=$snapshot; Updates=$updates; AssetVersions=$versions; AssemblyVersion=($rootVersion+1).ToString(); ExpectedBindingCount=17 }
$planPath = Join-Path $run 'update-plan.json'
if (Test-Path -LiteralPath $planPath) { throw 'An update plan already exists' }
$plan | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $planPath -Encoding utf8
foreach ($u in $updates) {
    $target = Join-Path $stage $u.Name
    Copy-Item -LiteralPath $u.LatestSource -Destination $target -Force
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $u.LatestSha256) { throw "Working-copy verification failed: $target" }
}
'PLANNED_PART_UPDATES=' + $updates.Count
'VERSIONED_ASSETS=' + $versions.Count
'ASSEMBLY_VERSION=' + $plan.AssemblyVersion
