param(
    [string]$SolidWorksInteropDir,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)

. (Join-Path $PSScriptRoot 'Common.ps1')
$repoRoot = Get-RepositoryRoot
$defaultInterop = Join-Path $repoRoot 'third_party\solidworks'
if ([string]::IsNullOrWhiteSpace($SolidWorksInteropDir)) { $SolidWorksInteropDir = $defaultInterop }
$msbuild = Get-MSBuildPath
$interop = (Resolve-Path -LiteralPath $SolidWorksInteropDir).Path

foreach ($name in @('SolidWorks.Interop.sldworks.dll', 'SolidWorks.Interop.swconst.dll', 'SolidWorks.Interop.swpublished.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $interop $name))) { throw "Missing SOLIDWORKS Interop assembly: $name" }
}

$project = Join-Path $repoRoot 'src\SolidWorksAssetExporter.AddIn\SolidWorksAssetExporter.AddIn.csproj'
& $msbuild $project /t:Rebuild "/p:Configuration=$Configuration" "/p:SolidWorksInteropDir=$interop" /v:minimal
exit $LASTEXITCODE
