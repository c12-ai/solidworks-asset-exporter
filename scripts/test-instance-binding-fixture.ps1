param([Parameter(Mandatory=$true)][string]$OutputDirectory, [switch]$Export, [switch]$Existing, [switch]$Extended, [switch]$Mutations, [switch]$Proximity, [switch]$RenameReopen, [switch]$InteractionCreate, [string]$InteractionSelect = '')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$build = Join-Path $repoRoot 'src\SolidWorksAssetExporter.AddIn\bin\Release'
$compiler = Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'
$program = Join-Path $build 'InstanceBindingFixtureProbe.exe'
$references = @('SolidWorksAssetExporter.AddIn.dll','SolidWorksAssetExporter.Core.dll','SolidWorks.Interop.sldworks.dll','SolidWorks.Interop.swconst.dll') | ForEach-Object { '/reference:' + (Join-Path $build $_) }
& $compiler /nologo /codepage:65001 /target:exe /platform:x64 /reference:System.Windows.Forms.dll $references /out:$program (Join-Path $PSScriptRoot 'InstanceBindingFixtureProbe.cs') (Join-Path $PSScriptRoot 'InstanceBindingLiveChecks.cs') (Join-Path $PSScriptRoot 'InstanceBindingMutationChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fixture probe compilation failed.' }
if ($InteractionCreate) { & $program $OutputDirectory --interaction-create } elseif ($InteractionSelect) { & $program $OutputDirectory --interaction-select $InteractionSelect } elseif ($RenameReopen) { & $program $OutputDirectory --rename-reopen } elseif ($Proximity) { & $program $OutputDirectory --proximity } elseif ($Mutations) { & $program $OutputDirectory --mutations } elseif ($Extended) { & $program $OutputDirectory --extended --export } elseif ($Existing) { & $program $OutputDirectory --existing } elseif ($Export) { & $program $OutputDirectory --export } else { & $program $OutputDirectory }
exit $LASTEXITCODE
