param([string]$RunDirectory)
$ErrorActionPreference='Stop'
$run=[IO.Path]::GetFullPath($RunDirectory)
$plan=Get-Content -LiteralPath (Join-Path $run 'property-sync-plan.json') -Raw|ConvertFrom-Json
$resultPath=Join-Path $run 'property-sync-result.json'
if(-not (Test-Path -LiteralPath ($resultPath+'.SUCCESS.txt'))){throw 'Property-only validation has not completed'}
$result=Get-Content -LiteralPath $resultPath -Raw|ConvertFrom-Json
$manifest=Get-Content -LiteralPath (Join-Path $run 'latest-source-manifest.json') -Raw|ConvertFrom-Json
$destination=[IO.Path]::GetFullPath($plan.DestinationDirectory).TrimEnd('\')
if($destination -ine 'C:\Users\C12\Desktop\1103'){throw 'Unexpected destination directory'}
$backup=Join-Path $run 'property-sync-original-backup'
$changes=@()
foreach($name in $result.ChangedFiles){
    if([IO.Path]::GetFileName($name) -cne $name -or $name -notin $plan.Pairs.DestinationName){throw 'Unexpected target file'}
    $original=@($manifest|Where-Object{[IO.Path]::GetFileName($_.Source) -eq $name})
    $prepared=@($result.Models|Where-Object{[IO.Path]::GetFileName($_.Path) -eq $name})
    if($original.Count -ne 1 -or $prepared.Count -ne 1){throw 'Ambiguous manifest entry'}
    $target=Join-Path $destination $name
    $source=Join-Path $plan.Directory $name
    if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $original[0].Sha256){throw "Destination changed: $name"}
    if((Get-FileHash -LiteralPath (Join-Path $backup $name) -Algorithm SHA256).Hash -ne $original[0].Sha256){throw "Backup verification failed: $name"}
    if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $prepared[0].Sha256){throw "Prepared file changed: $name"}
    $changes += [pscustomobject]@{Name=$name;Source=$source;Destination=$target;Before=$original[0].Sha256;After=$prepared[0].Sha256}
}
$journal=Join-Path $run 'property-sync-published.jsonl'
if(Test-Path -LiteralPath $journal){throw 'A publication journal already exists; inspect before resuming'}
$atomicBackup=Join-Path $run 'property-sync-atomic-backup'
New-Item -ItemType Directory -Path $atomicBackup -Force|Out-Null
foreach($change in $changes){
    if((Get-FileHash -LiteralPath $change.Destination -Algorithm SHA256).Hash -ne $change.Before){throw "Destination changed before publication: $($change.Name)"}
    $temporary=Join-Path $destination ('.property-sync-'+[Guid]::NewGuid().ToString('N')+[IO.Path]::GetExtension($change.Name))
    Copy-Item -LiteralPath $change.Source -Destination $temporary
    if((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $change.After){throw 'Temporary file verification failed'}
    [IO.File]::Replace($temporary,$change.Destination,(Join-Path $atomicBackup $change.Name))
    if((Get-FileHash -LiteralPath $change.Destination -Algorithm SHA256).Hash -ne $change.After){throw "Published file verification failed: $($change.Name)"}
    $change|ConvertTo-Json -Compress|Add-Content -LiteralPath $journal -Encoding utf8
}
foreach($original in $manifest){
    $name=[IO.Path]::GetFileName($original.Source)
    $change=$changes|Where-Object Name -eq $name
    $expected=if($change){$change.After}else{$original.Sha256}
    if((Get-FileHash -LiteralPath $original.Source -Algorithm SHA256).Hash -ne $expected){throw "Final directory audit failed: $name"}
}
'PUBLISHED_PROPERTY_FILES='+$changes.Count
'VERIFIED_DESTINATION_FILES='+$manifest.Count
'PROPERTY_SYNC_PUBLICATION_PASSED'|Set-Content -LiteralPath (Join-Path $run 'property-sync-published.SUCCESS.txt') -Encoding utf8
