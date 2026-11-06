param([Parameter(Mandatory=$true)][string]$RunDirectory)
$ErrorActionPreference='Stop'
$run=[IO.Path]::GetFullPath($RunDirectory)
$plan=Get-Content -LiteralPath (Join-Path $run 'plan-v4.json') -Raw|ConvertFrom-Json
$manifest=Get-Content -LiteralPath (Join-Path $run 'source-manifest.json') -Raw|ConvertFrom-Json
$prepared=Get-Content -LiteralPath (Join-Path $run 'prepared.json') -Raw|ConvertFrom-Json
if(-not(Test-Path -LiteralPath (Join-Path $run 'result-v4.json.SUCCESS.txt'))){throw 'Native verification incomplete'}
$destination=[IO.Path]::GetFullPath($plan.DestinationDirectory).TrimEnd('\')
if($destination -ine 'C:\Users\C12\Desktop\1103'){throw 'Unexpected destination'}
if(Get-Process -Name SLDWORKS -ErrorAction SilentlyContinue){throw 'SOLIDWORKS is running; check loaded destination models before publication'}
$existing=@{}
foreach($original in $manifest){
    if([IO.Path]::GetDirectoryName($original.Source) -ine $destination){throw 'Unexpected manifest path'}
    if((Get-FileHash -LiteralPath $original.Source -Algorithm SHA256).Hash -ne $original.Sha256){throw "Original changed: $($original.Name)"}
    if((Get-FileHash -LiteralPath (Join-Path $run ('original-backup\'+$original.Name)) -Algorithm SHA256).Hash -ne $original.Sha256){throw 'Backup mismatch'}
    $existing[$original.Name]=$original.Sha256
}
$changes=@()
foreach($property in $prepared.PSObject.Properties){
    $name=$property.Name
    if([IO.Path]::GetFileName($name) -cne $name -or [IO.Path]::GetExtension($name) -ine '.SLDASM'){throw 'Unexpected publication file'}
    $target=Join-Path $destination $name
    $source=Join-Path $plan.Directory $name
    if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $property.Value){throw "Prepared file changed: $name"}
    if(-not $existing.ContainsKey($name) -and (Test-Path -LiteralPath $target)){throw "New target already exists: $name"}
    if($existing.ContainsKey($name) -and $existing[$name] -eq $property.Value){continue}
    $changes += [pscustomobject]@{Name=$name;Source=$source;Destination=$target;Before=$existing[$name];After=$property.Value;NewFile=(-not $existing.ContainsKey($name))}
}
$journal=Join-Path $run 'published.jsonl'
if(Test-Path -LiteralPath $journal){throw 'Publication journal already exists; inspect before resuming'}
$atomicBackup=Join-Path $run 'atomic-backup'
New-Item -ItemType Directory -Path $atomicBackup|Out-Null
foreach($change in ($changes|Sort-Object @{Expression={if($_.Name -eq $plan.RootName){2}elseif($_.NewFile){0}else{1}}},Name)){
    if($change.NewFile){[IO.File]::Copy($change.Source,$change.Destination,$false)}
    else{
        if((Get-FileHash -LiteralPath $change.Destination -Algorithm SHA256).Hash -ne $change.Before){throw "Destination changed: $($change.Name)"}
        $temporary=Join-Path $destination ('.workstation-'+[Guid]::NewGuid().ToString('N')+'.SLDASM')
        [IO.File]::Copy($change.Source,$temporary,$false)
        if((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $change.After){throw 'Temporary file mismatch'}
        [IO.File]::Replace($temporary,$change.Destination,(Join-Path $atomicBackup $change.Name))
    }
    if((Get-FileHash -LiteralPath $change.Destination -Algorithm SHA256).Hash -ne $change.After){throw 'Publication hash mismatch'}
    $change|ConvertTo-Json -Compress|Add-Content -LiteralPath $journal -Encoding utf8
}
foreach($original in $manifest){
    $change=$changes|Where-Object Name -eq $original.Name
    $expected=if($change){$change.After}else{$original.Sha256}
    if((Get-FileHash -LiteralPath $original.Source -Algorithm SHA256).Hash -ne $expected){throw "Final file mismatch: $($original.Name)"}
}
$summary='ASSEMBLIES_PUBLISHED='+$changes.Count+' NEW_ASSEMBLIES='+@($changes|Where-Object NewFile).Count+' ORIGINAL_PART_FILES_UNCHANGED='+@($manifest|Where-Object Name -like '*.SLDPRT').Count
$summary|Set-Content -LiteralPath (Join-Path $run 'published.SUCCESS.txt') -Encoding utf8
$summary
