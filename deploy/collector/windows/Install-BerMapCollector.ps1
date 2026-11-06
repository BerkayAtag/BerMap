#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs or upgrades the BerMap collector as a Windows service.

.DESCRIPTION
    Copies the collector files from this package to the install directory, writes
    appsettings.Production.json (server URL, enrollment token, name, data directory)
    to the data directory, registers the "BerMapCollector" Windows service and waits
    until the collector has registered with the BerMap server.

    The collector only connects outbound to the server; no firewall port is opened.
    The enrollment token is printed by the server installer
    (BerMap:Collectors:EnrollmentToken in the server configuration).

    The configuration lives outside the program directory, in
    $DataDir\appsettings.Production.json. The data directory (configuration,
    collector id, offline queue, secret files) is readable by SYSTEM and
    Administrators only; the service finds the file through its BERMAP_CONFIG_FILE
    environment variable.

    Running the script again upgrades in place. The existing configuration is
    merged, not rebuilt: only the values given change, and integrations and
    settings added by hand are kept. When the server URL, the enrollment token or
    the name changes, the collector enrolls again.

.EXAMPLE
    .\Install-BerMapCollector.ps1 -ServerUrl https://bermap.corp.local -Name dc2-collector
    (prompts for the enrollment token)

.EXAMPLE
    .\Install-BerMapCollector.ps1 -ServerUrl https://bermap.corp.local -EnrollmentToken (Read-Host -AsSecureString)

.EXAMPLE
    .\Install-BerMapCollector.ps1
    (upgrade: server URL, token, name and integrations stay as configured)
#>
[CmdletBinding()]
param(
    [string] $ServerUrl,
    [SecureString] $EnrollmentToken,
    [string] $Name,
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'BerMap\Collector'),
    [string] $DataDir = (Join-Path $env:ProgramData 'BerMap\Collector'),
    [string] $ServiceName = 'BerMapCollector',
    [int] $RegistrationTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$packageDir = Split-Path -Parent $PSCommandPath
$sourceDir = Join-Path $packageDir 'collector'
$serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
# Written into the install directory; the uninstaller only removes a directory carrying it.
$marker = '.bermap-collector'
$configVariable = 'BERMAP_CONFIG_FILE'

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

function ConvertTo-Plain([SecureString] $Secure) {
    if (-not $Secure) { return '' }
    return [System.Net.NetworkCredential]::new('', $Secure).Password
}

function ConvertTo-OrderedTree($Value) {
    # ConvertFrom-Json objects become (case-insensitive) ordered dictionaries, so the
    # existing file can be merged key by key; Windows PowerShell 5.1 has no -AsHashtable.
    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        $result = [ordered]@{}
        foreach ($property in $Value.PSObject.Properties) { $result[$property.Name] = ConvertTo-OrderedTree $property.Value }
        return $result
    }
    if ($Value -is [System.Collections.IList]) {
        return , @($Value | ForEach-Object { ConvertTo-OrderedTree $_ })
    }
    return $Value
}

function Get-Section($Parent, [string] $Name) {
    if ($Parent[$Name] -isnot [System.Collections.IDictionary]) { $Parent[$Name] = [ordered]@{} }
    return $Parent[$Name]
}

function Get-ServiceEnvironment {
    $value = Get-ItemProperty -Path $serviceKey -Name Environment -ErrorAction SilentlyContinue
    if ($value) { return @($value.Environment) }
    return @()
}

function Get-Text($Value) { if ($null -eq $Value) { return '' } return ([string] $Value).Trim() }

function Get-MaskedToken([string] $Token) { return "$($Token.Substring(0, [Math]::Min(4, $Token.Length)))... ($($Token.Length) characters)" }

function New-ProtectedAcl([bool] $Directory) {
    # SYSTEM and Administrators only; inheritance from ProgramData (Users: read) is cut.
    $acl = if ($Directory) { New-Object System.Security.AccessControl.DirectorySecurity } else { New-Object System.Security.AccessControl.FileSecurity }
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = if ($Directory) { 'ContainerInherit, ObjectInherit' } else { 'None' }
    foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {
        $identity = New-Object System.Security.Principal.SecurityIdentifier($sid)
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', $inherit, 'None', 'Allow')))
    }
    return $acl
}

if (-not (Test-Path (Join-Path $sourceDir 'BerMap.Collector.exe'))) {
    throw "collector\BerMap.Collector.exe was not found next to this script. Run it from the extracted release package."
}

# An upgrade without -InstallDir/-DataDir keeps the directories the service uses.
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$serviceExe = $null
if ($service) {
    $imagePath = (Get-ItemProperty -Path $serviceKey -Name ImagePath -ErrorAction SilentlyContinue).ImagePath
    if ($imagePath -match '^"?(?<exe>[^"]+?\.exe)') { $serviceExe = $Matches.exe }
    if (-not $PSBoundParameters.ContainsKey('InstallDir') -and $serviceExe) { $InstallDir = Split-Path -Parent $serviceExe }
    $configured = Get-ServiceEnvironment | Where-Object { $_ -like "$configVariable=*" } | Select-Object -First 1
    if (-not $PSBoundParameters.ContainsKey('DataDir') -and $configured) {
        $DataDir = Split-Path -Parent $configured.Substring($configVariable.Length + 1)
    }
}

function Get-FullPath([string] $Path) { return [System.IO.Path]::GetFullPath($Path).TrimEnd('\') + '\' }
$installFull, $dataFull = (Get-FullPath $InstallDir), (Get-FullPath $DataDir)
if ($installFull.StartsWith($dataFull, 'OrdinalIgnoreCase') -or $dataFull.StartsWith($installFull, 'OrdinalIgnoreCase')) {
    throw "-InstallDir and -DataDir must not contain each other: $InstallDir, $DataDir"
}

# Only an empty directory or one this script installed into is used (and later
# removed by the uninstaller), never an arbitrary one such as C:\Tools. A directory
# the registered service runs from counts as installed by this script.
$serviceRunsHere = $serviceExe -and (Get-FullPath (Split-Path -Parent $serviceExe)) -eq $installFull
if ((Test-Path $InstallDir) -and -not (Test-Path (Join-Path $InstallDir $marker)) -and -not $serviceRunsHere -and
    (Get-ChildItem -Path $InstallDir -Force | Select-Object -First 1)) {
    throw "$InstallDir is not empty and is not a BerMap collector directory ($marker is missing). Choose a new or empty -InstallDir."
}

$configPath = Join-Path $DataDir 'appsettings.Production.json'
$idFile = Join-Path $DataDir 'collector-id'
# The data directory gets a protected ACL: refuse a foreign, non-empty one.
if ((Test-Path $DataDir) -and -not (Test-Path $configPath) -and -not (Test-Path $idFile) -and
    (Get-ChildItem -Path $DataDir -Force | Select-Object -First 1)) {
    throw "$DataDir is not empty and is not a BerMap collector data directory. Choose a new or empty -DataDir."
}

$config = if (Test-Path $configPath) { ConvertTo-OrderedTree (Get-Content $configPath -Raw | ConvertFrom-Json) } else { [ordered]@{} }
if ($config -isnot [System.Collections.IDictionary]) { $config = [ordered]@{} }
$created = $config.Count -eq 0

# --- Configuration (merged into the existing file) -------------------------------------------
if ($created) {
    if (-not $ServerUrl) { $ServerUrl = Read-Host 'BerMap server URL (e.g. https://bermap.corp.local)' }
    if (-not $EnrollmentToken) { $EnrollmentToken = Read-Host 'Collector enrollment token' -AsSecureString }
}

$bermap = Get-Section $config 'BerMap'
$collector = Get-Section $bermap 'Collector'
$before = @((Get-Text $bermap['ServerUrl']).TrimEnd('/'), (Get-Text $collector['EnrollmentToken']), (Get-Text $collector['Name']))

if ($ServerUrl) {
    $ServerUrl = $ServerUrl.Trim().TrimEnd('/')
    $uri = $null
    if (-not [Uri]::TryCreate($ServerUrl, [UriKind]::Absolute, [ref] $uri) -or $uri.Scheme -notin 'http', 'https' -or $uri.Query -or $uri.Fragment) {
        throw "Invalid -ServerUrl '$ServerUrl' (expected https://host[:port])."
    }
    $bermap['ServerUrl'] = $ServerUrl
}
$token = (ConvertTo-Plain $EnrollmentToken).Trim()
if ($token) {
    if ($token -match '\s') { throw 'The enrollment token must not contain whitespace.' }
    $collector['EnrollmentToken'] = $token
}
if ($Name -and $Name.Trim()) { $collector['Name'] = $Name.Trim() }
if ($PSBoundParameters.ContainsKey('DataDir') -or -not (Get-Text $collector['DataDirectory'])) { $collector['DataDirectory'] = $DataDir }
if ($created -and -not $collector.Contains('Integrations')) { $collector['Integrations'] = @() }

$after = @((Get-Text $bermap['ServerUrl']).TrimEnd('/'), (Get-Text $collector['EnrollmentToken']), (Get-Text $collector['Name']))
if (-not $after[0]) { throw "BerMap:ServerUrl is not configured in $configPath; pass -ServerUrl." }
if (-not $after[1]) { throw "BerMap:Collector:EnrollmentToken is not configured in $configPath; pass -EnrollmentToken." }
# A collector registered under other settings enrolls again (a new name included: the
# server keeps the name given at registration); the first install has nothing to compare.
$reenroll = -not $created -and (($before -join "`n") -cne ($after -join "`n"))
# The collector id and offline queue live in the configured data directory.
$collectorDataDir = [string] $collector['DataDirectory']
$idFile = Join-Path ([Environment]::ExpandEnvironmentVariables($collectorDataDir)) 'collector-id'

# --- Stop, copy, configure -------------------------------------------------------------------
if ($service -and $service.Status -ne 'Stopped') {
    Write-Step "Stopping $ServiceName"
    Stop-Service -Name $ServiceName -Force
    (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
}

Write-Step "Copying collector files to $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
New-Item -ItemType File -Force -Path (Join-Path $InstallDir $marker) | Out-Null
Copy-Item -Path (Join-Path $sourceDir '*') -Destination $InstallDir -Recurse -Force

Write-Step "Writing $configPath"
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
# The directory holds the enrollment token, the offline queue (discovered inventory)
# and usually the integration secret files: set before the content is written.
Set-Acl -Path $DataDir -AclObject (New-ProtectedAcl $true)
if (-not (Test-Path $configPath)) { New-Item -ItemType File -Path $configPath | Out-Null }
Set-Acl -Path $configPath -AclObject (New-ProtectedAcl $false)
ConvertTo-Json -InputObject $config -Depth 20 | Set-Content -Path $configPath -Encoding UTF8

if ($reenroll -and (Test-Path $idFile)) {
    Write-Step 'Server URL, enrollment token or name changed: the collector enrolls again'
    Remove-Item -Path $idFile -Force
}
$registeredBefore = (Test-Path $idFile) -and (Get-Item $idFile).Length -gt 0

# --- Service ---------------------------------------------------------------------------------
$exe = Join-Path $InstallDir 'BerMap.Collector.exe'
if (-not $service) {
    Write-Step "Registering service $ServiceName"
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'BerMap Collector' `
        -Description 'BerMap outbound discovery collector for remote networks.' `
        -StartupType Automatic | Out-Null
    & sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
}
# The service reads its configuration from $configPath; other variables are kept.
$environment = @(Get-ServiceEnvironment | Where-Object { $_ -and $_ -notlike "$configVariable=*" }) + "$configVariable=$configPath"
New-ItemProperty -Path $serviceKey -Name Environment -PropertyType MultiString -Value ([string[]] $environment) -Force | Out-Null

Write-Step "Starting $ServiceName"
Start-Service -Name $ServiceName

if ($registeredBefore) {
    # Already enrolled under the same settings: the service only has to keep running.
    Start-Sleep -Seconds 5
    if ((Get-Service -Name $ServiceName).Status -ne 'Running') {
        throw "The $ServiceName service stopped after the upgrade. Check the Windows Application event log."
    }
} else {
    Write-Step "Waiting for the collector to register with $($after[0])"
    $registered = $false
    for ($i = 0; $i -lt [Math]::Ceiling($RegistrationTimeoutSeconds / 2) -and -not $registered; $i++) {
        Start-Sleep -Seconds 2
        $registered = (Test-Path $idFile) -and (Get-Item $idFile).Length -gt 0
        if (-not $registered -and (Get-Service -Name $ServiceName).Status -eq 'Stopped') {
            throw "The $ServiceName service stopped. Check the Windows Application event log and $configPath."
        }
    }
    if (-not $registered) {
        throw ("The collector did not register with $($after[0]) within $RegistrationTimeoutSeconds seconds. " +
            'Check that the server is reachable from this host, the server URL and the enrollment token ' +
            "(BerMap:Collectors:EnrollmentToken on the server), and the Windows Application event log.")
    }
}

Write-Host ''
Write-Host "BerMap collector is running and registered with $($after[0])" -ForegroundColor Green
Write-Host "Configuration: $configPath"
Write-Host "Collector id:  $((Get-Content $idFile -Raw).Trim())"
Write-Host "Enrollment token: $(Get-MaskedToken $after[1])"
Write-Host "Add integrations to $configPath (see KURULUM-COLLECTOR.md), then: Restart-Service $ServiceName"
if ($after[0] -like 'http://*') {
    Write-Warning 'The server is reached over plain HTTP; the enrollment token and inventory travel unencrypted. Use https://.'
}
