Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$repositoryRoot = Get-RepositoryRoot
$compiler = Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'
$outputDirectory = Join-Path $repositoryRoot 'src\SolidWorksAssetExporter.AddIn\bin\Release'
$program = Join-Path $outputDirectory 'InspectAssetVersions.exe'
$source = Join-Path $PSScriptRoot 'InspectAssetVersions.cs'
$references = @(
    (Join-Path $outputDirectory 'SolidWorksAssetExporter.AddIn.dll'),
    (Join-Path $outputDirectory 'SolidWorksAssetExporter.Core.dll'),
    (Join-Path $repositoryRoot 'third_party\solidworks\SolidWorks.Interop.sldworks.dll'),
    (Join-Path $repositoryRoot 'third_party\solidworks\SolidWorks.Interop.swconst.dll')
)
foreach ($path in @($compiler, $source) + $references) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required file is missing: $path. Run scripts/build-addin.ps1 -Configuration Release first."
    }
}

$referenceArguments = $references | ForEach-Object { '/reference:' + $_ }
& $compiler /nologo /target:exe /platform:x64 /out:$program $referenceArguments $source
if ($LASTEXITCODE -ne 0) { throw "Asset version inspector compilation failed with exit code $LASTEXITCODE." }

& $program
exit $LASTEXITCODE
