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
$dataMarker = '.bermap-collector-data'
$configVariable = 'BERMAP_CONFIG_FILE'
$serverServiceKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\BerMapServer'

function Get-FullPath([string] $Path) { return [System.IO.Path]::GetFullPath($Path).TrimEnd('\') + '\' }

function Test-ServerDataDir([string] $Path) {
    # The server's data directory (vault, server configuration), or one that contains it.
    if ((Test-Path (Join-Path $Path 'vault')) -or (Test-Path (Join-Path $Path '.bermap-server-data'))) { return $true }
    $value = Get-ItemProperty -Path $serverServiceKey -Name Environment -ErrorAction SilentlyContinue
    $configured = @($(if ($value) { $value.Environment })) | Where-Object { $_ -like "$configVariable=*" } | Select-Object -First 1
    if (-not $configured) { return $false }
    $serverDir = Split-Path -Parent $configured.Substring($configVariable.Length + 1)
    return (Get-FullPath $serverDir).StartsWith((Get-FullPath $Path), 'OrdinalIgnoreCase')
}

function Remove-InstalledDirectory([string] $Path, [string] $MarkerName) {
    # A directory the installer created (marker "created", or an older empty marker) is removed;
    # an empty directory it found in place (marker "existing") is emptied and kept.
    $markerPath = Join-Path $Path $MarkerName
    if ((Test-Path $markerPath) -and ((Get-Content -Path $markerPath -TotalCount 1) -eq 'existing')) {
        Get-ChildItem -Path $Path -Force | Remove-Item -Recurse -Force
        Write-Host "Emptied $Path (it existed before the installation)."
    } else {
        Remove-Item -Path $Path -Recurse -Force
    }
}

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
    if (Test-Path (Join-Path $InstallDir $marker)) {
        Remove-InstalledDirectory $InstallDir $marker
    } elseif ($serviceInstallDir -and $serviceInstallDir -eq $InstallDir) {
        # The service ran from here, but the installation never completed: the program files only.
        Get-ChildItem -Path $InstallDir -Force | Remove-Item -Recurse -Force
    } else {
        Write-Warning "Left $InstallDir in place: it is not a BerMap collector directory ($marker is missing)."
    }
}

if ($RemoveData) {
    # Only a collector data directory (marker, collector id or offline queue) is removed, never
    # the server's: its appsettings.Production.json has the same name as the collector's.
    $isDataDir = (Test-Path (Join-Path $DataDir $dataMarker)) -or (Test-Path (Join-Path $DataDir 'collector-id')) -or
        (Test-Path (Join-Path $DataDir 'offline_queue'))
    if (-not (Test-Path $DataDir)) {
    } elseif (Test-ServerDataDir $DataDir) {
        Write-Warning "Left $DataDir in place: it is (or contains) the BerMap server's data directory."
    } elseif ($isDataDir) {
        Remove-InstalledDirectory $DataDir $dataMarker
        Write-Host "Removed the collector data in $DataDir (configuration, collector id, offline queue and the secret files kept there)."
    } else {
        Write-Warning "Left $DataDir in place: it is not a BerMap collector data directory ($dataMarker is missing)."
    }
} else {
    Write-Host "Kept $DataDir (configuration, collector id, offline queue)."
}

Write-Host 'BerMap collector removed. Remove its entry on the Collectorlar page of the console if it is retired.'
