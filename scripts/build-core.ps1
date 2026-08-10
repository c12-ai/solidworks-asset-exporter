param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

. (Join-Path $PSScriptRoot 'Common.ps1')
$repoRoot = Get-RepositoryRoot
$msbuild = Get-MSBuildPath
$testsProject = Join-Path $repoRoot 'tests\SolidWorksAssetExporter.Core.Tests\SolidWorksAssetExporter.Core.Tests.csproj'

& $msbuild $testsProject /t:Rebuild "/p:Configuration=$Configuration" /v:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$testExecutable = Join-Path $repoRoot "tests\SolidWorksAssetExporter.Core.Tests\bin\$Configuration\SolidWorksAssetExporter.Core.Tests.exe"
& $testExecutable
exit $LASTEXITCODE
