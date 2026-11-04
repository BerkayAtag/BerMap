#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes the BerMap server Windows service and its program files.

.DESCRIPTION
    The PostgreSQL database and the data directory (credential vault) are kept
    unless -RemoveData is given, so a reinstall can reuse them.
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'BerMap\Server'),
    [string] $DataDir = (Join-Path $env:ProgramData 'BerMap'),
    [string] $ServiceName = 'BerMapServer',
    [switch] $RemoveData
)

$ErrorActionPreference = 'Stop'

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
    & sc.exe delete $ServiceName | Out-Null
}

Get-NetFirewallRule -DisplayName 'BerMap Server (*)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule

if (Test-Path $InstallDir) {
    Remove-Item -Path $InstallDir -Recurse -Force
}

if ($RemoveData -and (Test-Path $DataDir)) {
    Remove-Item -Path $DataDir -Recurse -Force
    Write-Host "Removed $DataDir (credential vault). The PostgreSQL database was not touched."
} else {
    Write-Host "Kept $DataDir (credential vault) and the PostgreSQL database."
}

Write-Host 'BerMap server removed.'
