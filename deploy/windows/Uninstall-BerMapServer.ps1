#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes the BerMap server Windows service and its program files.

.DESCRIPTION
    The PostgreSQL database and the data directory (configuration with the
    database connection and collector enrollment token, credential vault) are
    kept unless -RemoveData is given, so a reinstall reuses them.
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'BerMap\Server'),
    [string] $DataDir = (Join-Path $env:ProgramData 'BerMap'),
    [string] $ServiceName = 'BerMapServer',
    [switch] $RemoveData
)

$ErrorActionPreference = 'Stop'
$serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$marker = '.bermap-server'
$configVariable = 'BERMAP_CONFIG_FILE'

# The service records the directories the installer used (-InstallDir, -DataDir).
$serviceInstallDir = $null
$configPath = $null
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    $key = Get-ItemProperty -Path $serviceKey -ErrorAction SilentlyContinue
    if ($key.ImagePath -match '^"?(?<exe>[^"]+?\.exe)') { $serviceInstallDir = Split-Path -Parent $Matches.exe }
    $configured = @($key.Environment) | Where-Object { $_ -like "$configVariable=*" } | Select-Object -First 1
    if ($configured) { $configPath = $configured.Substring($configVariable.Length + 1) }
    if ($serviceInstallDir -and -not $PSBoundParameters.ContainsKey('InstallDir')) { $InstallDir = $serviceInstallDir }
    if ($configPath -and -not $PSBoundParameters.ContainsKey('DataDir')) { $DataDir = Split-Path -Parent $configPath }

    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
    & sc.exe delete $ServiceName | Out-Null
}

# Never pipe an empty result: Remove-NetFirewallRule without input must not run.
$rules = @(Get-NetFirewallRule -DisplayName 'BerMap Server (*)' -ErrorAction SilentlyContinue)
if ($rules.Count -gt 0) { $rules | Remove-NetFirewallRule }

# Only a directory the installer created (marker) or the service ran from is removed.
if (Test-Path $InstallDir) {
    if ((Test-Path (Join-Path $InstallDir $marker)) -or ($serviceInstallDir -and $serviceInstallDir -eq $InstallDir)) {
        Remove-Item -Path $InstallDir -Recurse -Force
    } else {
        Write-Warning "Left $InstallDir in place: it is not a BerMap server directory ($marker is missing)."
    }
}

if ($RemoveData) {
    if (Test-Path $DataDir) { Remove-Item -Path $DataDir -Recurse -Force }
    if ($configPath -and (Test-Path $configPath)) { Remove-Item -Path $configPath -Force }
    Write-Host "Removed $DataDir (configuration, credential vault). The PostgreSQL database was not touched."
} else {
    Write-Host "Kept $DataDir (configuration, credential vault) and the PostgreSQL database."
}

Write-Host 'BerMap server removed.'
