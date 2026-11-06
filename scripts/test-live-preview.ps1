param(
    [string]$DocumentPath = '',
    [switch]$ListOnly,
    [switch]$DirtyOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$buildDirectory = Join-Path $repositoryRoot 'src\SolidWorksAssetExporter.AddIn\bin\Release'
$compiler = Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'
$program = Join-Path $buildDirectory 'LivePreviewProbe.exe'
$references = @(
    (Join-Path $buildDirectory 'SolidWorksAssetExporter.AddIn.dll'),
    (Join-Path $buildDirectory 'SolidWorksAssetExporter.Core.dll'),
    (Join-Path $repositoryRoot 'third_party\solidworks\SolidWorks.Interop.sldworks.dll'),
    (Join-Path $repositoryRoot 'third_party\solidworks\SolidWorks.Interop.swconst.dll')
)
$referenceArguments = $references | ForEach-Object { '/reference:' + $_ }
& $compiler /nologo /codepage:65001 /target:exe /platform:x64 /out:$program $referenceArguments `
    (Join-Path $PSScriptRoot 'LivePreviewProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Live preview probe compilation failed.' }
if ($ListOnly) { & $program --list }
elseif ($DirtyOnly) { & $program --dirty }
else { & $program $DocumentPath }
exit $LASTEXITCODE
