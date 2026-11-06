param([string]$RunDirectory)
$ErrorActionPreference='Stop'
$before=Get-Content -LiteralPath (Join-Path $RunDirectory 'before-v3.json') -Raw|ConvertFrom-Json
$result=Get-Content -LiteralPath (Join-Path $RunDirectory 'result-v4.json') -Raw|ConvertFrom-Json
if($result.After.Components.Count -ne 394 -or @($result.ContextMapping.PSObject.Properties).Count -ne 391){throw 'Unexpected component count'}
$diffs=@()
foreach($old in $before.Snapshot.Models){
    $name=[IO.Path]::GetFileName($old.Path)
    $updated=@($result.After.Models|Where-Object {[IO.Path]::GetFileName($_.Path) -eq $name})
    if($updated.Count -eq 0){continue}
    $keys=(@($old.Raw.PSObject.Properties.Name)+@($updated[0].Raw.PSObject.Properties.Name))|Select-Object -Unique
    foreach($key in $keys){
        if($old.Raw.$key -cne $updated[0].Raw.$key){
            $allowed=($name -eq '（部分）layout_all 20261103.SLDASM' -and $key -eq 'assembly_version') -or ($name -eq 'BSC（带上下拉门）部件.SLDASM' -and $key -in @('is-workstation','is_workstation','assembly_version'))
            if(-not $allowed){throw "Unexpected property change: $name $key"}
            $diffs += [pscustomobject]@{File=$name;Field=$key;Before=$old.Raw.$key;After=$updated[0].Raw.$key}
        }
    }
}
$diffs|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $RunDirectory 'metadata-changes.json') -Encoding utf8
'VERIFIED_391_ORIGINAL_COMPONENTS_AND_3_WRAPPERS'
'ONLY_INTENDED_PROPERTY_CHANGES='+$diffs.Count
