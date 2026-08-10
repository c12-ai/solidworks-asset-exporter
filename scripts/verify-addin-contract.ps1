param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

. (Join-Path $PSScriptRoot 'Common.ps1')
$repoRoot = Get-RepositoryRoot
$msbuild = Get-MSBuildPath
$project = Join-Path $repoRoot 'src\SolidWorksAssetExporter.AddIn\SolidWorksAssetExporter.AddIn.csproj'

& $msbuild $project /t:Rebuild "/p:Configuration=$Configuration" /p:UseSolidWorksStubs=true /v:minimal
exit $LASTEXITCODE
