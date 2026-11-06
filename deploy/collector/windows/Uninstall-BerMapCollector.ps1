#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes the BerMap collector Windows service and its program files.

.DESCRIPTION
    The data directory (configuration with the integrations and enrollment token,
    collector id, offline queue) is kept unless -RemoveData is given, so a
    reinstall continues under the same collector id. The collector entry on the
    server is removed from the console (Collectorlar page).
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'BerMap\Collector'),
    [string] $DataDir = (Join-Path $env:ProgramData 'BerMap\Collector'),
    [string] $ServiceName = 'BerMapCollector',
    [switch] $RemoveData
)

$ErrorActionPreference = 'Stop'
$serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$marker = '.bermap-collector'
$configVariable = 'BERMAP_CONFIG_FILE'

# The service records the directories the installer used (-InstallDir, -DataDir).
$serviceInstallDir = $null
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    $key = Get-ItemProperty -Path $serviceKey -ErrorAction SilentlyContinue
    if ($key.ImagePath -match '^"?(?<exe>[^"]+?\.exe)') { $serviceInstallDir = Split-Path -Parent $Matches.exe }
    $configured = @($key.Environment) | Where-Object { $_ -like "$configVariable=*" } | Select-Object -First 1
    if ($serviceInstallDir -and -not $PSBoundParameters.ContainsKey('InstallDir')) { $InstallDir = $serviceInstallDir }
    if ($configured -and -not $PSBoundParameters.ContainsKey('DataDir')) {
        $DataDir = Split-Path -Parent $configured.Substring($configVariable.Length + 1)
    }

    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
    & sc.exe delete $ServiceName | Out-Null
}

# Only a directory the installer created (marker) or the service ran from is removed.
if (Test-Path $InstallDir) {
    if ((Test-Path (Join-Path $InstallDir $marker)) -or ($serviceInstallDir -and $serviceInstallDir -eq $InstallDir)) {
        Remove-Item -Path $InstallDir -Recurse -Force
    } else {
        Write-Warning "Left $InstallDir in place: it is not a BerMap collector directory ($marker is missing)."
    }
}

if ($RemoveData) {
    # Only a collector data directory (configuration or collector id) is removed.
    $isDataDir = (Test-Path (Join-Path $DataDir 'appsettings.Production.json')) -or (Test-Path (Join-Path $DataDir 'collector-id'))
    if ($isDataDir) {
        Remove-Item -Path $DataDir -Recurse -Force
        Write-Host "Removed $DataDir (configuration, collector id, offline queue and the secret files kept there)."
    } elseif (Test-Path $DataDir) {
        Write-Warning "Left $DataDir in place: it is not a BerMap collector data directory."
    }
} else {
    Write-Host "Kept $DataDir (configuration, collector id, offline queue)."
}

Write-Host 'BerMap collector removed. Remove its entry on the Collectorlar page of the console if it is retired.'
