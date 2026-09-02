param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$repositoryRoot = Get-RepositoryRoot
$buildDirectory = Join-Path $repositoryRoot 'src\SolidWorksAssetExporter.AddIn\bin\Release'
[Reflection.Assembly]::LoadFrom((Join-Path $buildDirectory 'SolidWorksAssetExporter.Core.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $buildDirectory 'SolidWorks.Interop.sldworks.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $buildDirectory 'SolidWorks.Interop.swconst.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $buildDirectory 'SolidWorksAssetExporter.AddIn.dll')) | Out-Null

$settings = New-Object SolidWorksAssetExporter.AddIn.ExporterSettings
$stored = New-Object SolidWorksAssetExporter.AddIn.SettingsStore
$settings = $stored.Load()
$keyStore = New-Object SolidWorksAssetExporter.AddIn.WanxiangApiKeyStore
$settings.WanxiangApiKey = $keyStore.Load()
$provider = New-Object SolidWorksAssetExporter.AddIn.WanxiangRegistryProvider
$snapshot = $provider.Fetch($settings)
Write-Output ('REMOTE_PATH=' + $snapshot.RemotePath)
Write-Output ('REMOTE_EXISTS=' + $snapshot.RemoteRegistryExists)
Write-Output ('ASSET_COUNT=' + $snapshot.Registry.Assets.Count)
$snapshot.Registry.Assets |
    Sort-Object Uuid, Version |
    Select-Object AssetId, Uuid, Version, ContentFingerprint, RegisteredUtc |
    ConvertTo-Json -Depth 4
