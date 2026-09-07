param(
    [string]$TargetDocumentPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$repositoryRoot = Get-RepositoryRoot
$compilerCandidates = @(
    (Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'),
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($compiler)) { throw '64-bit .NET Framework csc.exe was not found.' }

$outputDirectory = Join-Path $repositoryRoot 'src\SolidWorksAssetExporter.AddIn\bin\Release'
$program = Join-Path $outputDirectory 'InspectAssetClasses.exe'
$source = Join-Path $PSScriptRoot 'InspectAssetClasses.cs'
$references = @(
    (Join-Path $outputDirectory 'SolidWorksAssetExporter.AddIn.dll'),
    (Join-Path $outputDirectory 'SolidWorksAssetExporter.Core.dll'),
    (Join-Path $repositoryRoot 'third_party\solidworks\SolidWorks.Interop.sldworks.dll'),
    (Join-Path $repositoryRoot 'third_party\solidworks\SolidWorks.Interop.swconst.dll')
)
foreach ($path in @($source) + $references) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required file is missing: $path. Run scripts/build-addin.ps1 -Configuration Release first."
    }
}

$referenceArguments = $references | ForEach-Object { '/reference:' + $_ }
& $compiler /nologo /target:exe /platform:x64 /out:$program $referenceArguments $source
if ($LASTEXITCODE -ne 0) { throw "Asset class inspector compilation failed with exit code $LASTEXITCODE." }

& $program $TargetDocumentPath
exit $LASTEXITCODE
