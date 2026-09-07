param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

. (Join-Path $PSScriptRoot 'Common.ps1')
$repoRoot = Get-RepositoryRoot
$msbuild = Get-MSBuildPath
$project = Join-Path $repoRoot 'src\SolidWorksAssetExporter.AddIn\SolidWorksAssetExporter.AddIn.csproj'
$contractOutput = Join-Path $repoRoot "src\SolidWorksAssetExporter.AddIn\bin\Contract-$Configuration"

# Keep the stub-based contract build out of bin\Debug and bin\Release. A stub
# assembly is suitable for API contract checks only; SOLIDWORKS cannot load it
# as a real ISwAddin implementation.
& $msbuild $project /t:Rebuild "/p:Configuration=$Configuration" /p:UseSolidWorksStubs=true "/p:OutDir=$contractOutput\" /v:minimal
exit $LASTEXITCODE
