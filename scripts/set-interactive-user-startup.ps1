param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Enable', 'Disable')]
    [string]$Mode,

    [string]$UserSid
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$addInId = '{B5EC0C01-12DD-4AFA-88EE-42E1178BA63D}'
$interactiveUser = $null
if ([string]::IsNullOrWhiteSpace($UserSid)) {
    $interactiveUser = (Get-CimInstance -ClassName Win32_ComputerSystem).UserName
    if ([string]::IsNullOrWhiteSpace($interactiveUser)) {
        throw 'Cannot determine the interactive Windows user.'
    }

    $account = New-Object System.Security.Principal.NTAccount($interactiveUser)
    $sid = $account.Translate([System.Security.Principal.SecurityIdentifier]).Value
}
else {
    $sid = (New-Object System.Security.Principal.SecurityIdentifier($UserSid)).Value
    $interactiveUser = $sid
}
$subKey = "Software\SolidWorks\AddInsStartup\$addInId"
$users = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
    [Microsoft.Win32.RegistryHive]::Users,
    [Microsoft.Win32.RegistryView]::Registry64)
$userHive = $null

try {
    $userHive = $users.OpenSubKey($sid, $true)
    if ($null -eq $userHive) {
        throw "The registry hive for user $sid is not loaded."
    }
    if ($Mode -eq 'Enable') {
        $key = $userHive.CreateSubKey(
            $subKey,
            [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree)
        if ($null -eq $key) {
            throw "Cannot create HKEY_USERS\$sid\$subKey."
        }
        try {
            $key.SetValue('', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
            $saved = $key.GetValue('', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            if ($saved -ne 1) {
                throw "The SOLIDWORKS add-in startup value could not be verified."
            }
        }
        finally {
            $key.Dispose()
        }
        Write-Host "Enabled the add-in for $interactiveUser ($sid)."
        exit 0
    }

    $userHive.DeleteSubKeyTree($subKey, $false)
    Write-Host "Removed the add-in startup entry for $interactiveUser ($sid)."
}
finally {
    if ($null -ne $userHive) {
        $userHive.Dispose()
    }
    $users.Dispose()
}
