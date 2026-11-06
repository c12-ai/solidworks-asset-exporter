param([switch]$Window, [switch]$LoadAddin, [switch]$BuildInfo, [switch]$Open, [switch]$Documents, [switch]$Selection, [switch]$SaveFixture, [switch]$RecoveryCopies, [switch]$InvokeBindings, [int]$FixtureAssetVersion = -1, [string]$FixtureSelection = '', [string]$DocumentPath = '')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$build = Join-Path $repoRoot 'src\SolidWorksAssetExporter.AddIn\bin\Release'
$compiler = Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'
$program = Join-Path $build 'InstanceBindingLiveProbe.exe'
$references = @('SolidWorksAssetExporter.AddIn.dll', 'SolidWorksAssetExporter.Core.dll') | ForEach-Object { '/reference:' + (Join-Path $build $_) }
$references += '/reference:' + (Join-Path $repoRoot 'third_party\solidworks\SolidWorks.Interop.sldworks.dll')
$references += '/reference:' + (Join-Path $repoRoot 'third_party\solidworks\SolidWorks.Interop.swconst.dll')
$references += '/reference:' + (Join-Path $repoRoot 'third_party\solidworks\SolidWorks.Interop.swpublished.dll')
& $compiler /nologo /codepage:65001 /target:exe /platform:x64 /reference:System.Windows.Forms.dll $references /out:$program (Join-Path $PSScriptRoot 'InstanceBindingLiveProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Live probe compilation failed.' }
if ($InvokeBindings) { & $program --invoke-bindings } elseif ($FixtureAssetVersion -ge 0) { & $program --fixture-version $FixtureAssetVersion } elseif ($RecoveryCopies) { & $program --recovery-copies } elseif ($SaveFixture) { & $program --save-fixture } elseif ($FixtureSelection) {
    & $program --select-fixture $DocumentPath $FixtureSelection
} elseif ($Selection) { & $program --selection } elseif ($BuildInfo) { & $program --build-info } elseif ($Open) { & $program --open $DocumentPath } elseif ($Documents) { & $program --documents } elseif ($LoadAddin) { & $program --load-addin } elseif ($Window) { & $program --window $DocumentPath } else { & $program }
exit $LASTEXITCODE
