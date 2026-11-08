#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs or upgrades the BerMap server as a Windows service.

.DESCRIPTION
    Copies the server files from this package to the install directory, writes
    appsettings.Production.json (database connection, listen URL, vault, collector
    enrollment token) to the data directory, registers the "BerMapServer" Windows
    service, opens the firewall port and waits until /healthz/ready reports the
    server ready.

    The configuration lives outside the program directory, in
    $DataDir\appsettings.Production.json (SYSTEM and Administrators only); the
    service finds it through its BERMAP_CONFIG_FILE environment variable.

    PostgreSQL 16+ must already be reachable. The database is created and
    migrated on first start if the user has CREATEDB rights; otherwise create an
    empty database owned by the user first.

    The data directory (configuration, credential vault) is readable by SYSTEM and
    Administrators only and owned by Administrators. A data directory or configuration
    file another user created beforehand (they could change its permissions again later)
    is refused unless -Force is given, which takes ownership and resets the permissions.
    The installer only uses an install directory it creates itself, one it installed into
    before, or the empty default directory.

    Running the script again upgrades in place. The existing configuration is
    merged, not rebuilt: only the values given change (-Db* parameters the
    connection string, -Port/-CertificatePath the listen endpoint), and settings
    added by hand, such as BerMap:ReverseProxy, are kept.

.EXAMPLE
    .\Install-BerMapServer.ps1 -DbHost localhost -DbUser bermap
    (prompts for the database password)

.EXAMPLE
    .\Install-BerMapServer.ps1 -DbHost pg01.corp.local -DbUser bermap -Port 443 -CertificatePath C:\certs\bermap.pfx

.EXAMPLE
    .\Install-BerMapServer.ps1 -CertificatePath C:\certs\renewed.pfx -CertificatePassword (Read-Host -AsSecureString)
    (upgrade that only replaces the certificate; port and database stay as configured)
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'BerMap\Server'),
    [string] $DataDir = (Join-Path $env:ProgramData 'BerMap'),
    [string] $DbHost,
    [int] $DbPort = 5432,
    [string] $DbName = 'bermap',
    [string] $DbUser,
    [SecureString] $DbPassword,
    [int] $Port = 8080,
    [string] $CertificatePath,
    [SecureString] $CertificatePassword,
    [string] $ServiceName = 'BerMapServer',
    [switch] $NoFirewallRule,
    # Take ownership of a data directory or configuration file another user created.
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$packageDir = Split-Path -Parent $PSCommandPath
$sourceDir = Join-Path $packageDir 'server'
$serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
# Written into the install directory; the uninstaller only removes a directory carrying it.
# It reads "created" when this script created the directory (the uninstaller removes it) and
# "existing" for the empty default directory found in place (the uninstaller empties it).
$marker = '.bermap-server'
# Identifies the server's data directory (vault), which a collector never uses as its own.
$dataMarker = '.bermap-server-data'
$configVariable = 'BERMAP_CONFIG_FILE'
$defaultInstallDir = Join-Path $env:ProgramFiles 'BerMap\Server'

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

function Get-ListenUrl($Endpoints) {
    # The "Default" endpoint the installer writes, else the first one configured by hand.
    $names = @('Default') + @($Endpoints.Keys | Where-Object { $_ -ne 'Default' })
    foreach ($name in $names) {
        $endpoint = $Endpoints[$name]
        if ($endpoint -is [System.Collections.IDictionary] -and $endpoint['Url']) { return [string] $endpoint['Url'] }
    }
    return $null
}

function New-ProtectedAcl([bool] $Directory) {
    # SYSTEM and Administrators only; inheritance from ProgramData (Users: read, create) is cut.
    # The owner becomes Administrators: an owner may always change the permissions again.
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
    # Refuses (or with -Force takes over) a data directory, configuration file or vault another
    # user created before the installation: the planted configuration would be merged and the
    # vault keys (token signing, master key) would stay readable by that user.
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

if (-not (Test-Path (Join-Path $sourceDir 'BerMap.Api.exe'))) {
    throw "server\BerMap.Api.exe was not found next to this script. Run it from the extracted release package."
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

# The install directory is removed by the uninstaller: only a directory this script creates,
# one it installed into before (marker), the one the registered service runs from, or the
# empty default directory is used, never an arbitrary one such as C:\Tools (even when empty).
$serviceRunsHere = $serviceExe -and (Get-FullPath (Split-Path -Parent $serviceExe)) -eq $installFull
$installDirState = if (-not (Test-Path $InstallDir)) { 'created' }
    elseif (Test-Path (Join-Path $InstallDir $marker)) { 'installed' }
    # Installed into before without completing: a directory this script creates is marked at once.
    elseif ($serviceRunsHere) { 'existing' }
    elseif ($installFull -eq (Get-FullPath $defaultInstallDir) -and -not (Get-ChildItem -Path $InstallDir -Force | Select-Object -First 1)) { 'existing' }
    else { throw "$InstallDir already exists and is not a BerMap server directory ($marker is missing). Choose a directory that does not exist yet with -InstallDir (it is created)." }

$configPath = Join-Path $DataDir 'appsettings.Production.json'
# Pre-release 0.3.0 builds kept the file in the program directory; it is moved.
$legacyConfigPath = Join-Path $InstallDir 'appsettings.Production.json'

# The existing configuration is merged only once the data directory, the files and the vault
# are known to be owned by SYSTEM or Administrators. A collector's data directory below it
# (Collector) is checked by its own installer.
$ownershipPaths = @($DataDir, $configPath, $legacyConfigPath)
if (Test-Path $configPath) {
    try {
        $existingVault = (Get-Content $configPath -Raw | ConvertFrom-Json).BerMap.CredentialVault.Directory
        if ($existingVault) { $ownershipPaths += [string] $existingVault }
    } catch { }
}
Assert-TrustedOwnership $ownershipPaths $DataDir
$existingPath = @($configPath, $legacyConfigPath) | Where-Object { Test-Path $_ } | Select-Object -First 1
$config = if ($existingPath) { ConvertTo-OrderedTree (Get-Content $existingPath -Raw | ConvertFrom-Json) } else { [ordered]@{} }
if ($config -isnot [System.Collections.IDictionary]) { $config = [ordered]@{} }

# --- Configuration (merged into the existing file) -------------------------------------------
$connections = Get-Section $config 'ConnectionStrings'
$builder = [System.Data.Common.DbConnectionStringBuilder]::new()
if ($connections['DefaultConnection']) { $builder.set_ConnectionString([string] $connections['DefaultConnection']) }
$newConnection = $builder.get_Count() -eq 0
if ($newConnection) {
    if (-not $DbHost) { $DbHost = Read-Host 'PostgreSQL host' }
    if (-not $DbUser) { $DbUser = Read-Host 'PostgreSQL user' }
}
# A new user needs its password; -DbPassword on its own rotates the password.
if (-not $DbPassword -and ($newConnection -or $PSBoundParameters.ContainsKey('DbUser'))) {
    $DbPassword = Read-Host "Password for PostgreSQL user '$DbUser'" -AsSecureString
}

$connectionAliases = @{
    Host = 'Host', 'Server'
    Port = 'Port'
    Database = 'Database', 'Db'
    Username = 'Username', 'User Name', 'User Id', 'UserId', 'User'
    Password = 'Password', 'Psw', 'Pwd'
}
$connectionChanges = [ordered]@{}
if ($DbHost) { $connectionChanges.Host = $DbHost }
if ($newConnection -or $PSBoundParameters.ContainsKey('DbPort')) { $connectionChanges.Port = $DbPort }
if ($newConnection -or $PSBoundParameters.ContainsKey('DbName')) { $connectionChanges.Database = $DbName }
if ($DbUser) { $connectionChanges.Username = $DbUser }
if ($DbPassword) { $connectionChanges.Password = ConvertTo-Plain $DbPassword }
foreach ($name in $connectionChanges.Keys) {
    foreach ($alias in $connectionAliases[$name]) { [void] $builder.Remove($alias) }
    $builder.set_Item($name, $connectionChanges[$name])
}
if ($connectionChanges.Count -gt 0) { $connections['DefaultConnection'] = $builder.get_ConnectionString() }

$bermap = Get-Section $config 'BerMap'
$vault = Get-Section $bermap 'CredentialVault'
if ($PSBoundParameters.ContainsKey('DataDir') -or -not $vault['Directory']) { $vault['Directory'] = Join-Path $DataDir 'vault' }
$vaultDir = [string] $vault['Directory']

$collectors = Get-Section $bermap 'Collectors'
if (-not $collectors['EnrollmentToken']) {
    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $collectors['EnrollmentToken'] = -join ($bytes | ForEach-Object { $_.ToString('x2') })
}
$enrollmentToken = [string] $collectors['EnrollmentToken']

# Listen endpoint: -Port changes only the port, -CertificatePath only the certificate
# (and the scheme); an upgrade without either keeps URL and certificate as they are.
$urlPattern = '^(?<scheme>[A-Za-z]+)://(?<host>.+):(?<port>\d+)/?$'
$endpoints = Get-Section (Get-Section $config 'Kestrel') 'Endpoints'
$portBound = $PSBoundParameters.ContainsKey('Port')
$certificateBound = $PSBoundParameters.ContainsKey('CertificatePath')
if ($portBound -or $certificateBound -or -not (Get-ListenUrl $endpoints)) {
    $endpoint = Get-Section $endpoints 'Default'
    $scheme, $listenHost, $listenPort = 'http', '0.0.0.0', 8080
    if ([string] $endpoint['Url'] -match $urlPattern) {
        $scheme, $listenHost, $listenPort = $Matches.scheme, $Matches.host, [int] $Matches.port
    }
    if ($portBound) { $listenPort = $Port }
    if ($certificateBound) {
        $scheme = 'https'
        $endpoint['Certificate'] = [ordered]@{
            Path = (Resolve-Path $CertificatePath).Path
            Password = ConvertTo-Plain $CertificatePassword
        }
    }
    $endpoint['Url'] = "${scheme}://${listenHost}:$listenPort"
}
$listenUrl = Get-ListenUrl $endpoints
if ($listenUrl -notmatch $urlPattern) {
    throw "Kestrel listen URL '$listenUrl' in $configPath is not of the form scheme://host:port."
}
$scheme = $Matches.scheme.ToLowerInvariant()
$Port = [int] $Matches.port
$probeHost = if ($Matches.host -in '0.0.0.0', '*', '+', '[::]') { 'localhost' } else { $Matches.host }

# --- Stop, copy, configure -------------------------------------------------------------------
if ($service -and $service.Status -ne 'Stopped') {
    Write-Step "Stopping $ServiceName"
    Stop-Service -Name $ServiceName -Force
    (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
}

Write-Step "Copying server files to $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
# A directory this script created is marked at once: it is BerMap's whatever happens next.
# The empty default directory found in place is marked only after a successful install.
if ($installDirState -eq 'created') { Set-Content -Path (Join-Path $InstallDir $marker) -Value 'created' -Encoding ASCII }
Copy-Item -Path (Join-Path $sourceDir '*') -Destination $InstallDir -Recurse -Force

Write-Step "Writing $configPath"
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
# The directory holds the configuration (database password, enrollment token) and the vault
# (master key, token signing key): SYSTEM and Administrators only, owned by Administrators,
# set before the content is written. A collector data directory below it keeps its own
# protected permissions.
Set-Acl -Path $DataDir -AclObject (New-ProtectedAcl $true)
if (-not (Test-Path (Join-Path $DataDir $dataMarker))) {
    Set-Content -Path (Join-Path $DataDir $dataMarker) -Value 'BerMap server data directory (credential vault)' -Encoding ASCII
}
if (-not (Test-Path $configPath)) { New-Item -ItemType File -Path $configPath | Out-Null }
Set-Acl -Path $configPath -AclObject (New-ProtectedAcl $false)
ConvertTo-Json -InputObject $config -Depth 20 | Set-Content -Path $configPath -Encoding UTF8
if ($legacyConfigPath -ne $configPath -and (Test-Path $legacyConfigPath)) { Remove-Item -Path $legacyConfigPath -Force }

# --- Service ---------------------------------------------------------------------------------
$exe = Join-Path $InstallDir 'BerMap.Api.exe'
if (-not $service) {
    Write-Step "Registering service $ServiceName"
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'BerMap Server' `
        -Description 'BerMap infrastructure discovery, dependency mapping and simulation server.' `
        -StartupType Automatic | Out-Null
    & sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
}
# The service reads its configuration from $configPath; other variables are kept.
$environment = @(Get-ServiceEnvironment | Where-Object { $_ -and $_ -notlike "$configVariable=*" }) + "$configVariable=$configPath"
New-ItemProperty -Path $serviceKey -Name Environment -PropertyType MultiString -Value ([string[]] $environment) -Force | Out-Null

if (-not $NoFirewallRule) {
    $ruleName = "BerMap Server ($Port)"
    # Rules for an earlier port. Never pipe an empty result: Remove-NetFirewallRule
    # without input must not run.
    $staleRules = @(Get-NetFirewallRule -DisplayName 'BerMap Server (*)' -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -ne $ruleName })
    if ($staleRules.Count -gt 0) { $staleRules | Remove-NetFirewallRule }
    if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
        Write-Step "Opening TCP $Port in Windows Firewall"
        New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow | Out-Null
    }
}

Write-Step "Starting $ServiceName"
Start-Service -Name $ServiceName

$probe = "${scheme}://${probeHost}:$Port/healthz/ready"
# The certificate may be issued for the server name, not "localhost". PowerShell 7 skips
# the check per request; Windows PowerShell 5.1 has no such switch and may not offer
# TLS 1.2, so both are set process-wide for the probe only and restored afterwards.
$patchTls = $scheme -eq 'https' -and $PSVersionTable.PSVersion.Major -lt 7
if ($patchTls) {
    if (-not ('BerMapReadinessProbe' -as [type])) {
        Add-Type -TypeDefinition @'
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
public static class BerMapReadinessProbe {
    public static readonly RemoteCertificateValidationCallback AcceptAny =
        delegate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors) { return true; };
}
'@
    }
    $savedProtocol = [Net.ServicePointManager]::SecurityProtocol
    $savedCallback = [Net.ServicePointManager]::ServerCertificateValidationCallback
    [Net.ServicePointManager]::SecurityProtocol = $savedProtocol -bor [Net.SecurityProtocolType]::Tls12
    [Net.ServicePointManager]::ServerCertificateValidationCallback = [BerMapReadinessProbe]::AcceptAny
}
$ready = $false
$lastError = $null
try {
    for ($i = 0; $i -lt 90 -and -not $ready; $i++) {
        Start-Sleep -Seconds 2
        try {
            $request = @{ Uri = $probe; UseBasicParsing = $true; TimeoutSec = 5 }
            if ($scheme -eq 'https' -and -not $patchTls) { $request.SkipCertificateCheck = $true }
            $ready = (Invoke-WebRequest @request).StatusCode -eq 200
        } catch { $lastError = $_.Exception.Message }
    }
} finally {
    if ($patchTls) {
        [Net.ServicePointManager]::SecurityProtocol = $savedProtocol
        [Net.ServicePointManager]::ServerCertificateValidationCallback = $savedCallback
    }
}
if (-not $ready) {
    throw "BerMap did not become ready at $probe ($lastError). Check the Windows Application event log and the database connection in $configPath."
}

# The installation succeeded: the empty default directory found in place is BerMap's now.
if ($installDirState -eq 'existing') { Set-Content -Path (Join-Path $InstallDir $marker) -Value 'existing' -Encoding ASCII }

Write-Host ''
Write-Host "BerMap is running: ${scheme}://$($env:COMPUTERNAME):$Port" -ForegroundColor Green
Write-Host "Configuration: $configPath"
$passwordFile = Join-Path $vaultDir 'initial-admin-password.txt'
if (Test-Path $passwordFile) {
    Write-Host 'Initial administrator (delete the file after the first sign-in):'
    Get-Content $passwordFile | ForEach-Object { Write-Host "  $_" }
    Write-Host "  file: $passwordFile"
}
Write-Host "Collector enrollment token: $enrollmentToken"
if ($scheme -eq 'http') {
    Write-Warning 'The console is served over plain HTTP. Use -CertificatePath, or publish it through IIS/a reverse proxy with TLS.'
}
