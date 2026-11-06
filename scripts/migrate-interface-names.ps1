param([ValidateSet('Inspect','Plan','Apply')][string]$Mode = 'Inspect', [string]$RootAssembly, [string]$InventoryPath, [string]$MappingPath, [string]$PlanPath, [string]$BackupDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$build = Join-Path $repo 'src\SolidWorksAssetExporter.AddIn\bin\Release'
$program = Join-Path $build 'MigrateInterfaceNames.exe'
$compiler = Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'
$references = @('SolidWorksAssetExporter.AddIn.dll', 'SolidWorksAssetExporter.Core.dll', 'SolidWorks.Interop.sldworks.dll', 'SolidWorks.Interop.swconst.dll') | ForEach-Object { '/reference:' + (Join-Path $build $_) }
& $compiler /nologo /codepage:65001 /target:exe /platform:x64 /reference:System.Runtime.Serialization.dll /reference:Microsoft.CSharp.dll $references /out:$program (Join-Path $PSScriptRoot 'MigrateInterfaceNames.cs')
if ($LASTEXITCODE -ne 0) { throw 'Migration helper compilation failed.' }
if ($Mode -eq 'Inspect') { & $program --inspect $RootAssembly $InventoryPath }
elseif ($Mode -eq 'Plan') { & $program --plan $InventoryPath $MappingPath $PlanPath }
else { & $program --apply $PlanPath $BackupDirectory }
exit $LASTEXITCODE
