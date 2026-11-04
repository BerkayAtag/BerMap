#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs or upgrades the BerMap server as a Windows service.

.DESCRIPTION
    Copies the server files from this package to the install directory, writes
    appsettings.Production.json (database connection, listen URL, vault, collector
    enrollment token), registers the "BerMapServer" Windows service, opens the
    firewall port and waits until /healthz/ready reports the server ready.

    PostgreSQL 16+ must already be reachable. The database is created and
    migrated on first start if the user has CREATEDB rights; otherwise create an
    empty database owned by the user first.

    Running the script again upgrades in place: the existing configuration,
    credential vault and database are kept unless new values are given.

.EXAMPLE
    .\Install-BerMapServer.ps1 -DbHost localhost -DbUser bermap
    (prompts for the database password)

.EXAMPLE
    .\Install-BerMapServer.ps1 -DbHost pg01.corp.local -DbUser bermap -Port 443 -CertificatePath C:\certs\bermap.pfx
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
    [switch] $NoFirewallRule
)

$ErrorActionPreference = 'Stop'
$packageDir = Split-Path -Parent $PSCommandPath
$sourceDir = Join-Path $packageDir 'server'
$configPath = Join-Path $InstallDir 'appsettings.Production.json'
$vaultDir = Join-Path $DataDir 'vault'

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

function ConvertTo-Plain([SecureString] $Secure) {
    if (-not $Secure) { return '' }
    return [System.Net.NetworkCredential]::new('', $Secure).Password
}

if (-not (Test-Path (Join-Path $sourceDir 'BerMap.Api.exe'))) {
    throw "server\BerMap.Api.exe was not found next to this script. Run it from the extracted release package."
}

$existing = $null
if (Test-Path $configPath) {
    $existing = Get-Content $configPath -Raw | ConvertFrom-Json
}

# --- Configuration ---------------------------------------------------------------------------
if (-not $existing -or $DbHost -or $DbUser) {
    if (-not $DbHost) { $DbHost = Read-Host 'PostgreSQL host' }
    if (-not $DbUser) { $DbUser = Read-Host 'PostgreSQL user' }
    if (-not $DbPassword) { $DbPassword = Read-Host "Password for PostgreSQL user '$DbUser'" -AsSecureString }
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    $builder['Host'] = $DbHost
    $builder['Port'] = $DbPort
    $builder['Database'] = $DbName
    $builder['Username'] = $DbUser
    $builder['Password'] = ConvertTo-Plain $DbPassword
    $connectionString = $builder.ConnectionString
} else {
    $connectionString = $existing.ConnectionStrings.DefaultConnection
}

$enrollmentToken = if ($existing -and $existing.BerMap.Collectors.EnrollmentToken) {
    $existing.BerMap.Collectors.EnrollmentToken
} else {
    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    -join ($bytes | ForEach-Object { $_.ToString('x2') })
}

$keepEndpoint = $existing -and $existing.Kestrel -and -not $CertificatePath -and -not $PSBoundParameters.ContainsKey('Port')
if ($keepEndpoint) {
    # Upgrade without new endpoint values: keep the configured URL and certificate.
    $uri = [Uri]($existing.Kestrel.Endpoints.Default.Url -replace '0\.0\.0\.0', 'localhost')
    $scheme = $uri.Scheme
    $Port = $uri.Port
} else {
    $scheme = if ($CertificatePath) { 'https' } else { 'http' }
}
$url = "${scheme}://0.0.0.0:$Port"

$config = [ordered]@{
    ConnectionStrings = [ordered]@{ DefaultConnection = $connectionString }
    BerMap = [ordered]@{
        CredentialVault = [ordered]@{ Directory = $vaultDir }
        Collectors = [ordered]@{ EnrollmentToken = $enrollmentToken }
    }
    Kestrel = [ordered]@{
        Endpoints = [ordered]@{
            Default = [ordered]@{ Url = $url }
        }
    }
}
if ($keepEndpoint) {
    $config.Kestrel = $existing.Kestrel
} elseif ($CertificatePath) {
    $config.Kestrel.Endpoints.Default.Certificate = [ordered]@{
        Path = (Resolve-Path $CertificatePath).Path
        Password = ConvertTo-Plain $CertificatePassword
    }
}

# --- Stop, copy, configure -------------------------------------------------------------------
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Write-Step "Stopping $ServiceName"
    Stop-Service -Name $ServiceName -Force
    (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
}

Write-Step "Copying server files to $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $sourceDir '*') -Destination $InstallDir -Recurse -Force

Write-Step 'Writing appsettings.Production.json'
$config | ConvertTo-Json -Depth 8 | Set-Content -Path $configPath -Encoding UTF8
# The file holds the database password: SYSTEM and Administrators only.
$acl = New-Object System.Security.AccessControl.FileSecurity
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {
    $identity = New-Object System.Security.Principal.SecurityIdentifier($sid)
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'Allow')))
}
Set-Acl -Path $configPath -AclObject $acl
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null

# --- Service ---------------------------------------------------------------------------------
$exe = Join-Path $InstallDir 'BerMap.Api.exe'
if (-not $service) {
    Write-Step "Registering service $ServiceName"
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'BerMap Server' `
        -Description 'BerMap infrastructure discovery, dependency mapping and simulation server.' `
        -StartupType Automatic | Out-Null
    & sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
}

if (-not $NoFirewallRule) {
    $ruleName = "BerMap Server ($Port)"
    if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
        Write-Step "Opening TCP $Port in Windows Firewall"
        New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow | Out-Null
    }
}

Write-Step "Starting $ServiceName"
Start-Service -Name $ServiceName

$probe = "${scheme}://localhost:$Port/healthz/ready"
$ready = $false
for ($i = 0; $i -lt 90 -and -not $ready; $i++) {
    Start-Sleep -Seconds 2
    try {
        $request = @{ Uri = $probe; UseBasicParsing = $true; TimeoutSec = 5 }
        # The certificate may be issued for the server name, not "localhost".
        if ($scheme -eq 'https' -and $PSVersionTable.PSVersion.Major -ge 7) { $request.SkipCertificateCheck = $true }
        $ready = (Invoke-WebRequest @request).StatusCode -eq 200
    } catch { }
}
if (-not $ready) {
    throw "BerMap did not become ready at $probe. Check the Windows Application event log and the database connection in $configPath."
}

Write-Host ''
Write-Host "BerMap is running: ${scheme}://$($env:COMPUTERNAME):$Port" -ForegroundColor Green
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
