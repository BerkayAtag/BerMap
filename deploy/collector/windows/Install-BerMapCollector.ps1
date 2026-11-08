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

    The data directory and the configuration file must be owned by SYSTEM or
    Administrators: a directory another user created beforehand (they could change its
    permissions again later) is refused unless -Force is given, which takes ownership
    and resets the permissions. The installer only uses an install or data directory
    it creates itself, one it installed into before, or the empty default directory.

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

.EXAMPLE
    .\Install-BerMapCollector.ps1 -ServerUrl https://proxy.corp.local/bermap
    (the server is published below a path prefix by a reverse proxy)
#>
[CmdletBinding()]
param(
    [string] $ServerUrl,
    [SecureString] $EnrollmentToken,
    [string] $Name,
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'BerMap\Collector'),
    [string] $DataDir = (Join-Path $env:ProgramData 'BerMap\Collector'),
    [string] $ServiceName = 'BerMapCollector',
    [int] $RegistrationTimeoutSeconds = 120,
    # Take ownership of a data directory or configuration file another user created.
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$packageDir = Split-Path -Parent $PSCommandPath
$sourceDir = Join-Path $packageDir 'collector'
$serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
# Written into the install directory; the uninstaller only removes a directory carrying it.
# It reads "created" when this script created the directory (the uninstaller removes it) and
# "existing" for the empty default directory found in place (the uninstaller empties it).
$marker = '.bermap-collector'
# The same for the data directory. The server's data directory has its own marker
# (.bermap-server-data) and is never taken for the collector's.
$dataMarker = '.bermap-collector-data'
$configVariable = 'BERMAP_CONFIG_FILE'
$serverServiceKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\BerMapServer'
$defaultInstallDir = Join-Path $env:ProgramFiles 'BerMap\Collector'
$defaultDataDir = Join-Path $env:ProgramData 'BerMap\Collector'

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
    # SYSTEM and Administrators only; inheritance from ProgramData (Users: read) is cut. The
    # owner becomes Administrators: an owner may always change the permissions again.
    $acl = if ($Directory) { New-Object System.Security.AccessControl.DirectorySecurity } else { New-Object System.Security.AccessControl.FileSecurity }
    $acl.SetOwner((New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = if ($Directory) { 'ContainerInherit, ObjectInherit' } else { 'None' }
    foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {
        $identity = New-Object System.Security.Principal.SecurityIdentifier($sid)
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', $inherit, 'None', 'Allow')))
    }
    return $acl
}

function Get-OwnerSid([string] $Path) {
    try { return (Get-Acl -LiteralPath $Path).GetOwner([System.Security.Principal.SecurityIdentifier]).Value } catch { return $null }
}

function Get-TrustedOwners {
    return @('S-1-5-18', 'S-1-5-32-544', [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
}

function Test-ProtectedByInstaller([string] $Path) {
    # Protected (no inheritance) and open to SYSTEM and Administrators only: nobody else could
    # have added content to it.
    try { $acl = Get-Acl -LiteralPath $Path } catch { return $false }
    if (-not $acl.AreAccessRulesProtected) { return $false }
    foreach ($rule in $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin 'S-1-5-18', 'S-1-5-32-544') { return $false }
    }
    return $true
}

function Find-UntrustedItems([string] $Path, [string[]] $Trusted) {
    # $Path and what is below it, unless it is a directory only SYSTEM and Administrators can
    # write to, whose owner is not SYSTEM, Administrators or the administrator running this
    # script. Such an owner keeps the right to change the permissions after this script set them.
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $owner = Get-OwnerSid $Path
    if ($owner -notin $Trusted) { $Path }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }
    if ($owner -in $Trusted -and (Test-ProtectedByInstaller $Path)) { return }
    foreach ($child in @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue)) {
        Find-UntrustedItems $child.FullName $Trusted
    }
}

function Assert-TrustedOwnership([string[]] $Paths, [string] $Root) {
    # Refuses (or with -Force takes over) a data directory or configuration file another user
    # created before the installation, for example to plant an integration that sends a secret
    # file to their host.
    $trusted = Get-TrustedOwners
    $untrusted = @($Paths | ForEach-Object { Find-UntrustedItems $_ $trusted } | Select-Object -Unique)
    if ($untrusted.Count -eq 0) { return }
    $first = $untrusted[0]
    $owner = Get-OwnerSid $first
    try { $owner = (New-Object System.Security.Principal.SecurityIdentifier($owner)).Translate([System.Security.Principal.NTAccount]).Value } catch { }
    if (-not $Force) {
        throw ("$first is owned by $(if ($owner) { $owner } else { 'an unknown account' }), not by SYSTEM or Administrators: " +
            'another user may have created it to read or change the BerMap configuration. Inspect it, then remove it, ' +
            'or run the installer again with -Force to take ownership and reset its permissions.')
    }
    Write-Warning "Taking ownership of $Root and resetting its permissions (-Force): $first was owned by $owner."
    & icacls.exe $Root /setowner '*S-1-5-32-544' /T /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls /setowner failed for $Root (exit code $LASTEXITCODE)." }
    & icacls.exe $Root /reset /T /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls /reset failed for $Root (exit code $LASTEXITCODE)." }
}

function Get-ServerDataDir {
    # The BerMap server's data directory on this machine (vault, server configuration), if any.
    $value = Get-ItemProperty -Path $serverServiceKey -Name Environment -ErrorAction SilentlyContinue
    $configured = @($(if ($value) { $value.Environment })) | Where-Object { $_ -like "$configVariable=*" } | Select-Object -First 1
    if ($configured) { return Split-Path -Parent $configured.Substring($configVariable.Length + 1) }
    return $null
}

function Test-ServerDataDir([string] $Path) {
    # The server's data directory, or one that contains it, is never used as the collector's.
    if ((Test-Path (Join-Path $Path 'vault')) -or (Test-Path (Join-Path $Path '.bermap-server-data'))) { return $true }
    $settings = Join-Path $Path 'appsettings.Production.json'
    if (Test-Path $settings) {
        try { if ((Get-Content $settings -Raw | ConvertFrom-Json).PSObject.Properties.Name -contains 'ConnectionStrings') { return $true } } catch { }
    }
    $serverDir = Get-ServerDataDir
    return [bool] ($serverDir -and (Get-FullPath $serverDir).StartsWith((Get-FullPath $Path), 'OrdinalIgnoreCase'))
}

function Get-FullPath([string] $Path) { return [System.IO.Path]::GetFullPath($Path).TrimEnd('\') + '\' }

if (-not (Test-Path (Join-Path $sourceDir 'BerMap.Collector.exe'))) {
    throw "collector\BerMap.Collector.exe was not found next to this script. Run it from the extracted release package."
}

# An upgrade without -InstallDir/-DataDir keeps the directories the service uses.
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$serviceExe = $null
$serviceDataDir = $null
if ($service) {
    $imagePath = (Get-ItemProperty -Path $serviceKey -Name ImagePath -ErrorAction SilentlyContinue).ImagePath
    if ($imagePath -match '^"?(?<exe>[^"]+?\.exe)') { $serviceExe = $Matches.exe }
    if (-not $PSBoundParameters.ContainsKey('InstallDir') -and $serviceExe) { $InstallDir = Split-Path -Parent $serviceExe }
    $configured = Get-ServiceEnvironment | Where-Object { $_ -like "$configVariable=*" } | Select-Object -First 1
    if ($configured) { $serviceDataDir = Split-Path -Parent $configured.Substring($configVariable.Length + 1) }
    if (-not $PSBoundParameters.ContainsKey('DataDir') -and $serviceDataDir) { $DataDir = $serviceDataDir }
}

$installFull, $dataFull = (Get-FullPath $InstallDir), (Get-FullPath $DataDir)
if ($installFull.StartsWith($dataFull, 'OrdinalIgnoreCase') -or $dataFull.StartsWith($installFull, 'OrdinalIgnoreCase')) {
    throw "-InstallDir and -DataDir must not contain each other: $InstallDir, $DataDir"
}

# The install directory is removed by the uninstaller: only a directory this script creates,
# one it installed into before (marker), the one the registered service runs from, or the
# empty default directory is used, never an arbitrary one such as C:\Tools (even when empty).
$serviceRunsHere = $serviceExe -and (Get-FullPath (Split-Path -Parent $serviceExe)) -eq $installFull
$installDirState = if (-not (Test-Path $InstallDir)) { 'created' }
    elseif (Test-Path (Join-Path $InstallDir $marker)) { 'installed' }
    # Installed into before without completing: a directory this script creates is marked at once.
    elseif ($serviceRunsHere) { 'existing' }
    elseif ($installFull -eq (Get-FullPath $defaultInstallDir) -and -not (Get-ChildItem -Path $InstallDir -Force | Select-Object -First 1)) { 'existing' }
    else { throw "$InstallDir already exists and is not a BerMap collector directory ($marker is missing). Choose a directory that does not exist yet with -InstallDir (it is created)." }

$configPath = Join-Path $DataDir 'appsettings.Production.json'
$idFile = Join-Path $DataDir 'collector-id'
if (Test-ServerDataDir $DataDir) {
    throw "$DataDir is (or contains) the BerMap server's data directory. Choose a separate -DataDir, for example $defaultDataDir."
}
# The data directory gets a protected ACL and is removed by Uninstall-BerMapCollector.ps1
# -RemoveData: the same rule as for the install directory. A directory holding a collector id
# or an offline queue is a collector data directory.
$serviceUsesDataDir = $serviceDataDir -and (Get-FullPath $serviceDataDir) -eq $dataFull
$dataDirState = if (-not (Test-Path $DataDir)) { 'created' }
    elseif ((Test-Path (Join-Path $DataDir $dataMarker)) -or (Test-Path $idFile) -or (Test-Path (Join-Path $DataDir 'offline_queue'))) { 'installed' }
    elseif ($serviceUsesDataDir) { 'existing' }
    elseif ($dataFull -eq (Get-FullPath $defaultDataDir) -and -not (Get-ChildItem -Path $DataDir -Force | Select-Object -First 1)) { 'existing' }
    else { throw "$DataDir already exists and is not a BerMap collector data directory ($dataMarker is missing). Choose a directory that does not exist yet with -DataDir (it is created)." }

# The existing configuration is merged (and its integrations kept) only once the data
# directory and the file are known to be owned by SYSTEM or Administrators.
Assert-TrustedOwnership @($DataDir, $configPath) $DataDir

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
        throw "Invalid -ServerUrl '$ServerUrl' (expected https://host[:port][/path])."
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
# A directory this script created is marked at once: it is BerMap's whatever happens next.
# The empty default directory found in place is marked only after a successful install.
if ($installDirState -eq 'created') { Set-Content -Path (Join-Path $InstallDir $marker) -Value 'created' -Encoding ASCII }
Copy-Item -Path (Join-Path $sourceDir '*') -Destination $InstallDir -Recurse -Force

Write-Step "Writing $configPath"
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
# The directory holds the enrollment token, the offline queue (discovered inventory)
# and usually the integration secret files: set before the content is written.
Set-Acl -Path $DataDir -AclObject (New-ProtectedAcl $true)
if ($dataDirState -eq 'created') { Set-Content -Path (Join-Path $DataDir $dataMarker) -Value 'created' -Encoding ASCII }
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

# The installation succeeded: the empty default directories found in place are BerMap's now.
if ($installDirState -eq 'existing') { Set-Content -Path (Join-Path $InstallDir $marker) -Value 'existing' -Encoding ASCII }
if ($dataDirState -eq 'existing') { Set-Content -Path (Join-Path $DataDir $dataMarker) -Value 'existing' -Encoding ASCII }

Write-Host ''
Write-Host "BerMap collector is running and registered with $($after[0])" -ForegroundColor Green
Write-Host "Configuration: $configPath"
Write-Host "Collector id:  $((Get-Content $idFile -Raw).Trim())"
Write-Host "Enrollment token: $(Get-MaskedToken $after[1])"
Write-Host "Add integrations to $configPath (see KURULUM-COLLECTOR.md), then: Restart-Service $ServiceName"
if ($after[0] -like 'http://*') {
    Write-Warning 'The server is reached over plain HTTP; the enrollment token and inventory travel unencrypted. Use https://.'
}
