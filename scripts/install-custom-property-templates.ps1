param(
    [string]$TargetDirectory,
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-SolidWorksSession {
    try {
        $active = [Runtime.InteropServices.Marshal]::GetActiveObject('SldWorks.Application')
        if ($null -ne $active) { return $active }
    } catch { }

    if (-not ('SolidWorksTemplateRotReader' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class SolidWorksTemplateRotReader
{
    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx context);

    public static object[] GetSessions()
    {
        var result = new List<object>();
        IRunningObjectTable table;
        if (GetRunningObjectTable(0, out table) != 0 || table == null) return result.ToArray();
        IEnumMoniker enumerator;
        table.EnumRunning(out enumerator);
        if (enumerator == null) return result.ToArray();
        var monikers = new IMoniker[1];
        while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
        {
            IBindCtx context = null;
            try
            {
                if (CreateBindCtx(0, out context) != 0 || context == null) continue;
                string name;
                monikers[0].GetDisplayName(context, null, out name);
                if (name == null || !name.StartsWith("SolidWorks_PID_", StringComparison.OrdinalIgnoreCase)) continue;
                object instance;
                table.GetObject(monikers[0], out instance);
                if (instance != null) result.Add(instance);
            }
            catch { }
            finally
            {
                if (context != null) Marshal.ReleaseComObject(context);
                if (monikers[0] != null) Marshal.ReleaseComObject(monikers[0]);
            }
        }
        Marshal.ReleaseComObject(enumerator);
        Marshal.ReleaseComObject(table);
        return result.ToArray();
    }
}
'@ | Out-Null
    }

    foreach ($candidate in [SolidWorksTemplateRotReader]::GetSessions()) {
        try {
            $null = $candidate.ActiveDoc
            return $candidate
        } catch { }
    }
    throw 'SOLIDWORKS is not running or cannot be accessed at the current permission level.'
}

function Get-ConfiguredTemplateDirectory($application) {
    # swFileLocationsCustomPropertyFile = 25.
    $configured = ''
    try { $configured = [string]$application.GetUserPreferenceStringListValue(25) }
    catch { $configured = '' }
    if ([string]::IsNullOrWhiteSpace($configured)) {
        try { $configured = [string]$application.GetUserPreferenceStringValue(25) }
        catch { $configured = '' }
    }
    $paths = @($configured -split ';' | ForEach-Object { $_.Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($paths.Count -eq 0) { return $null }
    $existing = @($paths | Where-Object { Test-Path -LiteralPath $_ -PathType Container })
    if ($existing.Count -eq 0) {
        throw ('Configured Custom Property Files directories do not exist: ' + ($paths -join '; '))
    }
    return [IO.Path]::GetFullPath($existing[0])
}

$sourceLayout = Join-Path (Split-Path -Parent $PSScriptRoot) 'templates\custom-properties'
$packageLayout = Join-Path $PSScriptRoot 'custom-properties'
if (Test-Path -LiteralPath $sourceLayout -PathType Container) {
    $sourceDirectory = $sourceLayout
    $backupParent = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts'
}
elseif (Test-Path -LiteralPath $packageLayout -PathType Container) {
    $sourceDirectory = $packageLayout
    $backupParent = Join-Path $PSScriptRoot 'template-backups'
}
else {
    throw 'Cannot locate the custom-properties template directory.'
}
$templateNames = @('part.prtprp', 'asem.asmprp', 'draw.drwprp', 'properties.txt')
foreach ($name in $templateNames) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory $name) -PathType Leaf)) {
        throw "Template source is missing: $name"
    }
}

$explicitTarget = -not [string]::IsNullOrWhiteSpace($TargetDirectory)
$application = if ($explicitTarget) { $null } else { Get-SolidWorksSession }
$resolvedTarget = if (-not $explicitTarget) {
    $configuredTarget = Get-ConfiguredTemplateDirectory $application
    if ([string]::IsNullOrWhiteSpace($configuredTarget)) {
        Join-Path $env:ProgramData 'SolidWorksAssetExporter\custom-properties'
    } else {
        $configuredTarget
    }
} else {
    [IO.Path]::GetFullPath($TargetDirectory)
}

if ($null -ne $application) { Write-Host 'SOLIDWORKS session: connected' }
else { Write-Host 'SOLIDWORKS directory: supplied explicitly' }
Write-Host ('Custom Property Files directory: ' + $resolvedTarget)
Write-Host ('Mode: ' + $(if ($Apply) { 'APPLY' } else { 'PREVIEW' }))
if (-not $Apply) {
    Write-Host 'Preview only. Run again with -Apply to back up and install the templates.'
    exit 0
}

if (-not (Test-Path -LiteralPath $resolvedTarget -PathType Container)) {
    New-Item -ItemType Directory -Path $resolvedTarget -Force | Out-Null
}

# Keep SOLIDWORKS pointed at the verified local directory only when the target
# was discovered through the application. An explicit target is copied without
# changing any SOLIDWORKS preference.
if ($null -ne $application) {
    $preferenceSet = [bool]$application.SetUserPreferenceStringValue(25, $resolvedTarget)
    if (-not $preferenceSet) {
        throw "SOLIDWORKS rejected the Custom Property Files directory: $resolvedTarget"
    }
}

$backupRoot = Join-Path $backupParent ('custom-property-template-backup-' +
    (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null

foreach ($name in $templateNames) {
    $source = Join-Path $sourceDirectory $name
    $destination = Join-Path $resolvedTarget $name
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        Copy-Item -LiteralPath $destination -Destination (Join-Path $backupRoot $name) -Force
    }
    Copy-Item -LiteralPath $source -Destination $destination -Force
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $destinationHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    if (-not [string]::Equals($sourceHash, $destinationHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Installed template hash mismatch: $destination"
    }
    Write-Host ('INSTALLED ' + $destination)
}

Write-Host ('Backup directory: ' + $backupRoot)
Write-Host 'Close and reopen the SOLIDWORKS Custom Properties task pane (or restart SOLIDWORKS) to refresh the templates.'
