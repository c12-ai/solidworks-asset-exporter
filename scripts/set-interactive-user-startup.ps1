param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Enable', 'Disable')]
    [string]$Mode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$addInId = '{B5EC0C01-12DD-4AFA-88EE-42E1178BA63D}'
$interactiveUser = (Get-CimInstance -ClassName Win32_ComputerSystem).UserName
if ([string]::IsNullOrWhiteSpace($interactiveUser)) {
    throw 'Cannot determine the interactive Windows user.'
}

$account = New-Object System.Security.Principal.NTAccount($interactiveUser)
$sid = $account.Translate([System.Security.Principal.SecurityIdentifier]).Value
$key = "Registry::HKEY_USERS\$sid\Software\SolidWorks\AddInsStartup\$addInId"

if ($Mode -eq 'Enable') {
    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty -LiteralPath $key -Name '(default)' -Type DWord -Value 1
    Write-Host "Enabled the add-in for $interactiveUser."
    exit 0
}

if (Test-Path -LiteralPath $key) {
    Remove-Item -LiteralPath $key -Force
}
Write-Host "Removed the add-in startup entry for $interactiveUser."
