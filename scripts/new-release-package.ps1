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
$payloadSources = @{}
$interopDirectory = Join-Path $repoRoot 'third_party\solidworks'
foreach ($name in $payloadFiles) {
    $source = Join-Path $build $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf) -and $name.StartsWith('SolidWorks.Interop.')) {
        $source = Join-Path $interopDirectory $name
    }
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Build output is missing $name. Run scripts/build-addin.ps1 first."
    }
    $payloadSources[$name] = $source
}

# Refuse to package the stub-based contract build. The contract build defines
# fake SolidWorks.Interop types inside the add-in assembly, so the DLL can be
# mapped into SLDWORKS.exe but cannot be queried as the real COM ISwAddin.
$addInSource = $payloadSources['SolidWorksAssetExporter.AddIn.dll']
try {
    $addInAssembly = [Reflection.Assembly]::LoadFrom($addInSource)
    $referenceNames = @($addInAssembly.GetReferencedAssemblies() | ForEach-Object { $_.Name })
    $requiredInteropReferences = @(
        'SolidWorks.Interop.sldworks',
        'SolidWorks.Interop.swconst',
        'SolidWorks.Interop.swpublished'
    )
    $missingInteropReferences = @($requiredInteropReferences | Where-Object {
        $referenceNames -notcontains $_
    })
    $definesStubInterface = $null -ne $addInAssembly.GetType(
        'SolidWorks.Interop.swpublished.ISwAddin', $false, $false)
}
catch {
    throw "Cannot inspect add-in assembly '$addInSource': $($_.Exception.Message)"
}

if ($definesStubInterface -or $missingInteropReferences.Count -gt 0) {
    $missing = if ($missingInteropReferences.Count -eq 0) {
        '<none>'
    }
    else {
        $missingInteropReferences -join ', '
    }
    throw "Refusing to package a SOLIDWORKS contract-stub build. Missing real Interop references: $missing. Run scripts/build-addin.ps1 -Configuration Release first."
}

if (Test-Path -LiteralPath $packageRoot) { throw "Release package directory already exists: $packageRoot" }
New-Item -ItemType Directory -Path $payload -Force | Out-Null
foreach ($name in $payloadFiles) {
    Copy-Item -LiteralPath $payloadSources[$name] -Destination $payload
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.cmd') -Destination (Join-Path $packageRoot 'Install.cmd')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.cmd') -Destination (Join-Path $packageRoot 'Uninstall.cmd')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'set-interactive-user-startup.ps1') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install-custom-property-templates.ps1') `
    -Destination (Join-Path $packageRoot 'Install-CustomPropertyTemplates.ps1')
Copy-Item -LiteralPath (Join-Path $repoRoot 'templates\custom-properties') `
    -Destination (Join-Path $packageRoot 'custom-properties') -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install-interface-options.ps1') -Destination (Join-Path $packageRoot 'Install-InterfaceOptions.ps1')
Copy-Item -LiteralPath (Join-Path $repoRoot 'templates\interfaces') -Destination (Join-Path $packageRoot 'interfaces') -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot 'macros') `
    -Destination (Join-Path $packageRoot 'macros') -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $packageRoot 'README.md')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\RELEASING.md') -Destination (Join-Path $packageRoot 'RELEASING.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install-user-update.ps1') -Destination (Join-Path $packageRoot 'Install-UserUpdate.ps1')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'restore-user-update.ps1') -Destination (Join-Path $packageRoot 'Restore-UserUpdate.ps1')
New-Item -ItemType Directory -Path (Join-Path $packageRoot 'docs'),(Join-Path $packageRoot 'examples') -Force | Out-Null
foreach ($document in Get-ChildItem -LiteralPath (Join-Path $repoRoot 'docs') -File) {
    Copy-Item -LiteralPath $document.FullName -Destination (Join-Path $packageRoot 'docs')
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'examples\instance-binding') -Destination (Join-Path $packageRoot 'examples\instance-binding') -Recurse
$evidence = Join-Path $repoRoot 'artifacts\instance-binding-evidence'
if (Test-Path -LiteralPath $evidence -PathType Container) {
    New-Item -ItemType Directory -Path (Join-Path $packageRoot 'artifacts') -Force | Out-Null
    Copy-Item -LiteralPath $evidence -Destination (Join-Path $packageRoot 'artifacts\instance-binding-evidence') -Recurse
}
$interactionEvidence = Join-Path $repoRoot 'artifacts\instance-binding-ui2-evidence'
if (Test-Path -LiteralPath $interactionEvidence -PathType Container) {
    New-Item -ItemType Directory -Path (Join-Path $packageRoot 'artifacts') -Force | Out-Null
    Copy-Item -LiteralPath $interactionEvidence -Destination (Join-Path $packageRoot 'artifacts\instance-binding-ui2-evidence') -Recurse
}

$interfaceEvidence = Join-Path $repoRoot 'artifacts\instance-binding-ui3-evidence'
if (Test-Path -LiteralPath $interfaceEvidence -PathType Container) {
    New-Item -ItemType Directory -Path (Join-Path $packageRoot 'artifacts') -Force | Out-Null
    Copy-Item -LiteralPath $interfaceEvidence -Destination (Join-Path $packageRoot 'artifacts\instance-binding-ui3-evidence') -Recurse
}

$archive = Join-Path $output "$packageName.zip"
$multiParentEvidence = Join-Path $repoRoot 'artifacts\instance-binding-rc4-evidence'
if (Test-Path -LiteralPath $multiParentEvidence -PathType Container) {
    New-Item -ItemType Directory -Path (Join-Path $packageRoot 'artifacts') -Force | Out-Null
    Copy-Item -LiteralPath $multiParentEvidence -Destination (Join-Path $packageRoot 'artifacts\instance-binding-rc4-evidence') -Recurse
}
if (Test-Path -LiteralPath $archive) { throw "Release archive already exists: $archive" }
Compress-Archive -LiteralPath $packageRoot -DestinationPath $archive -CompressionLevel Optimal
Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Format-List
Write-Host "Created release package: $archive"
