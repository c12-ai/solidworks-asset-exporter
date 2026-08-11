param(
    [string]$BuildDirectory,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[vV]?\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$repoRoot = Get-RepositoryRoot
if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    $BuildDirectory = Join-Path $repoRoot 'src\SolidWorksAssetExporter.AddIn\bin\Release'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts'
}

$build = (Resolve-Path -LiteralPath $BuildDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$packageName = "SolidWorksAssetExporter-$Version"
$packageRoot = Join-Path $output $packageName
$payload = Join-Path $packageRoot 'payload'

$payloadFiles = @(
    'SolidWorksAssetExporter.AddIn.dll',
    'SolidWorksAssetExporter.Core.dll',
    'SolidWorks.Interop.sldworks.dll',
    'SolidWorks.Interop.swconst.dll',
    'SolidWorks.Interop.swpublished.dll'
)
foreach ($name in $payloadFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $build $name) -PathType Leaf)) {
        throw "Build output is missing $name. Run scripts/build-addin.ps1 first."
    }
}

if (Test-Path -LiteralPath $packageRoot) { throw "Release package directory already exists: $packageRoot" }
New-Item -ItemType Directory -Path $payload -Force | Out-Null
foreach ($name in $payloadFiles) {
    Copy-Item -LiteralPath (Join-Path $build $name) -Destination $payload
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.cmd') -Destination (Join-Path $packageRoot 'Install.cmd')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.cmd') -Destination (Join-Path $packageRoot 'Uninstall.cmd')

$archive = Join-Path $output "$packageName.zip"
if (Test-Path -LiteralPath $archive) { throw "Release archive already exists: $archive" }
Compress-Archive -LiteralPath $packageRoot -DestinationPath $archive -CompressionLevel Optimal
Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Format-List
Write-Host "Created release package: $archive"
