param(
    [Parameter(Mandatory = $true)]
    [string]$Schema,
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-SolidWorksSession {
    $fallback = $null
    try {
        $fallback = [Runtime.InteropServices.Marshal]::GetActiveObject('SldWorks.Application')
        $document = $fallback.ActiveDoc
        if ($null -ne $document) { return @($fallback, $document) }
    } catch { }

    if (-not ('SolidWorksRotReader' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class SolidWorksRotReader
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

    foreach ($candidate in [SolidWorksRotReader]::GetSessions()) {
        try {
            $document = $candidate.ActiveDoc
            if ($null -ne $document) { return @($candidate, $document) }
        } catch { }
    }
    if ($null -ne $fallback) { return @($fallback, $null) }
    return @($null, $null)
}

function Get-SchemaForKind([object[]]$rows, [string]$kind) {
    $result = [ordered]@{}
    foreach ($row in $rows) {
        $scope = ([string]$row.DocumentType).Trim()
        $name = ([string]$row.Name).Trim()
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        if ($scope -notin @('All', $kind)) { continue }
        if ($result.Contains($name)) { throw "Schema contains duplicate property for ${kind}: $name" }
        $result[$name] = if ($null -eq $row.DefaultValue) { '' } else { [string]$row.DefaultValue }
    }
    return $result
}

function Get-PropertyNames($manager) {
    if ($null -eq $manager) { return @() }
    $value = $manager.GetNames()
    if ($null -eq $value) { return @() }
    return @($value) | ForEach-Object { [string]$_ }
}

function Get-Documents($application, $activeDocument, [bool]$resolveComponents) {
    $documents = [ordered]@{}
    $rootPath = [string]$activeDocument.GetPathName()
    if (-not [string]::IsNullOrWhiteSpace($rootPath)) { $documents[$rootPath] = $activeDocument }

    # swDocASSEMBLY = 2. Preview mode remains read-only and does not resolve lightweight components.
    if ([int]$activeDocument.GetType() -eq 2) {
        if ($resolveComponents) { $null = $activeDocument.ResolveAllLightWeightComponents($false) }
        $components = @($activeDocument.GetComponents($false))
        foreach ($component in $components) {
            if ($null -eq $component) { continue }
            $document = $component.GetModelDoc2()
            if ($null -eq $document) {
                Write-Warning ("Skipped unresolved component: " + [string]$component.Name2)
                continue
            }
            $path = [string]$document.GetPathName()
            if (-not [string]::IsNullOrWhiteSpace($path) -and -not $documents.Contains($path)) {
                $documents[$path] = $document
            }
        }
    }
    return @($documents.Values)
}

function Sync-Document($document, [object[]]$schemaRows, [bool]$applyChanges) {
    $documentType = [int]$document.GetType()
    $kind = if ($documentType -eq 1) { 'Part' } elseif ($documentType -eq 2) { 'Assembly' } else { return }
    $path = [string]$document.GetPathName()
    if ([string]::IsNullOrWhiteSpace($path)) { throw 'Unsaved documents cannot be synchronized.' }

    $required = Get-SchemaForKind $schemaRows $kind
    if ($required.Count -eq 0) { throw "Schema has no fields for document type: $kind" }
    $manager = $document.Extension.CustomPropertyManager('')
    $existingNames = @(Get-PropertyNames $manager)
    $existing = @{}
    foreach ($name in $existingNames) { $existing[$name] = $true }

    $remove = @($existingNames | Where-Object { -not $required.Contains($_) })
    $add = @($required.Keys | Where-Object { -not $existing.ContainsKey($_) })
    if ($remove.Count -eq 0 -and $add.Count -eq 0) {
        Write-Host "UNCHANGED [$kind] $path"
        return
    }

    Write-Host "CHANGE    [$kind] $path"
    foreach ($name in $remove) { Write-Host "  - $name" }
    foreach ($name in $add) { Write-Host ("  + {0}={1}" -f $name, $required[$name]) }
    if (-not $applyChanges) { return }

    foreach ($name in $remove) {
        $status = [int]$manager.Delete2($name)
        if ($status -ne 0) { throw "Failed to delete property [$name] from $path; status=$status" }
    }
    foreach ($name in $add) {
        # swCustomInfoText = 30; swCustomPropertyOnlyIfNew = 0.
        $status = [int]$manager.Add3($name, 30, [string]$required[$name], 0)
        if ($status -ne 0) { throw "Failed to add property [$name] to $path; status=$status" }
    }

    $errors = 0
    $warnings = 0
    # swSaveAsOptions_Silent = 1.
    $saved = [bool]$document.Save3(1, [ref]$errors, [ref]$warnings)
    if (-not $saved -or $errors -ne 0) {
        throw "Failed to save $path; errors=$errors; warnings=$warnings"
    }
}

$schemaPath = (Resolve-Path -LiteralPath $Schema).Path
$schemaRows = @(Import-Csv -LiteralPath $schemaPath -Encoding UTF8)
if ($schemaRows.Count -eq 0) { throw "Schema is empty: $schemaPath" }
foreach ($column in @('DocumentType', 'Name', 'DefaultValue')) {
    if ($schemaRows[0].PSObject.Properties.Name -notcontains $column) { throw "Schema is missing column: $column" }
}

$session = @(Get-SolidWorksSession)
$application = $session[0]
$activeDocument = $session[1]
if ($null -eq $application) { throw 'SOLIDWORKS is not running or cannot be accessed.' }
if ($null -eq $activeDocument) {
    throw 'No open model was found in any SOLIDWORKS session. Run this preview at the same permission level as SOLIDWORKS.'
}

$documents = @(Get-Documents $application $activeDocument ([bool]$Apply))
Write-Host ("Mode: {0}; documents: {1}; schema: {2}" -f $(if ($Apply) { 'APPLY' } else { 'PREVIEW' }), $documents.Count, $schemaPath)
foreach ($document in $documents) { Sync-Document $document $schemaRows ([bool]$Apply) }
if (-not $Apply) { Write-Host 'Preview only. Run again with -Apply to write and save these changes.' }
