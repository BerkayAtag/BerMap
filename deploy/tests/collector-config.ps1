<#
.SYNOPSIS
    Exercises the configuration merge of deploy/collector/windows/Install-BerMapCollector.ps1
    without administrator rights, a service or a package.

.DESCRIPTION
    The installer's helper functions and its configuration section (from loading the
    existing file to "# --- Stop, copy, configure") are cut out of the script and run
    against a temporary file, the way the installer runs them. Works in Windows
    PowerShell 5.1 and PowerShell 7 (also on Linux/macOS):

        pwsh -NoProfile -NonInteractive -File deploy/tests/collector-config.ps1
#>
$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot '..\collector\windows\Install-BerMapCollector.ps1'
$text = Get-Content -Raw -Path $installer
$tokens = $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseInput($text, [ref] $tokens, [ref] $errors)
if ($errors.Count -gt 0) { throw "Install-BerMapCollector.ps1 does not parse: $($errors[0].Message)" }

$functions = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false) |
    ForEach-Object { $_.Extent.Text }
$start = $text.IndexOf('$config = if (Test-Path $configPath)')
$end = $text.IndexOf('# --- Stop, copy, configure')
if ($start -lt 0 -or $end -le $start) { throw 'The configuration section was not found in Install-BerMapCollector.ps1.' }

$merge = [scriptblock]::Create(@"
param([string] `$ServerUrl, [SecureString] `$EnrollmentToken, [string] `$Name, [string] `$DataDir = `$env:BERMAP_TEST_DATA_DIR, [string] `$configPath)
$($functions -join "`n")
$($text.Substring($start, $end - $start))
ConvertTo-Json -InputObject `$config -Depth 20 | Set-Content -Path `$configPath -Encoding UTF8
[pscustomobject]@{ Reenroll = [bool] `$reenroll; After = `$after; IdFile = `$idFile }
"@)

$work = Join-Path ([System.IO.Path]::GetTempPath()) "bermap-collector-test-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $work | Out-Null
$configPath = Join-Path $work 'appsettings.Production.json'
$dataDir = Join-Path $work 'data'
# The installer's default -DataDir, without it counting as passed.
$env:BERMAP_TEST_DATA_DIR = $dataDir
# Get-MaskedToken and friends, for checks of their own.
. ([scriptblock]::Create($functions -join "`n"))
$script:failures = 0

function Invoke-Merge([hashtable] $Parameters) {
    # $PSBoundParameters inside the section sees exactly the parameters a user would pass.
    $arguments = @{ configPath = $configPath }
    foreach ($key in $Parameters.Keys) { $arguments[$key] = $Parameters[$key] }
    return & $merge @arguments
}

function Read-Config { return Get-Content -Raw -Path $configPath | ConvertFrom-Json }

function Assert-That([string] $Description, [scriptblock] $Condition) {
    $ok = $false
    try { $ok = [bool] (& $Condition) } catch { Write-Host "  $($_.Exception.Message)" }
    if ($ok) { Write-Host "ok   $Description" } else { Write-Host "FAIL $Description" -ForegroundColor Red; $script:failures++ }
}

function Assert-Throws([string] $Description, [string] $Pattern, [scriptblock] $Action) {
    try {
        & $Action | Out-Null
        Write-Host "FAIL $Description (accepted)" -ForegroundColor Red; $script:failures++
    } catch {
        if ($_.Exception.Message -match $Pattern) { Write-Host "ok   $Description" }
        else { Write-Host "FAIL $Description`: $($_.Exception.Message)" -ForegroundColor Red; $script:failures++ }
    }
}

function Secure([string] $Value) { return ConvertTo-SecureString $Value -AsPlainText -Force }

try {
    $token = '0123456789abcdef0123456789abcdef0123456789abcdef'

    Assert-Throws 'server URL must be http(s)' 'Invalid -ServerUrl' { Invoke-Merge @{ ServerUrl = 'bermap.corp.local'; EnrollmentToken = (Secure $token) } }
    Assert-Throws 'server URL without a query' 'Invalid -ServerUrl' { Invoke-Merge @{ ServerUrl = 'https://bermap.corp.local/?x=1'; EnrollmentToken = (Secure $token) } }
    Assert-Throws 'token without whitespace' 'whitespace' { Invoke-Merge @{ ServerUrl = 'https://bermap.corp.local'; EnrollmentToken = (Secure 'abc def') } }
    $result = Invoke-Merge @{ ServerUrl = 'https://proxy.corp.local/bermap/'; EnrollmentToken = (Secure $token) }
    Assert-That 'a server URL path prefix is kept (trailing slash dropped)' { (Read-Config).BerMap.ServerUrl -eq 'https://proxy.corp.local/bermap' }
    Remove-Item -Path $configPath -Force

    $result = Invoke-Merge @{ ServerUrl = 'https://bermap.corp.local/'; EnrollmentToken = (Secure $token); Name = 'dc2-collector' }
    $config = Read-Config
    Assert-That 'first install writes the given values' {
        $config.BerMap.ServerUrl -eq 'https://bermap.corp.local' -and $config.BerMap.Collector.EnrollmentToken -eq $token -and
        $config.BerMap.Collector.Name -eq 'dc2-collector' -and $config.BerMap.Collector.DataDirectory -eq $dataDir
    }
    Assert-That 'first install writes an empty integration list' {
        $text = Get-Content -Raw -Path $configPath
        $text -match '"Integrations":\s*\[\s*\]'
    }
    Assert-That 'first install does not re-enroll' { -not $result.Reenroll }
    Assert-That 'collector id file is in the data directory' { $result.IdFile -eq (Join-Path $dataDir 'collector-id') }

    # Settings and integrations added by hand survive an upgrade without parameters.
    $config | Add-Member -NotePropertyName Logging -NotePropertyValue ([pscustomobject]@{ LogLevel = [pscustomobject]@{ Default = 'Warning' } })
    $config.BerMap.Collector | Add-Member -NotePropertyName BatchSize -NotePropertyValue 250
    $config.BerMap.Collector.Integrations = @([pscustomobject]@{
        IntegrationId = '3f0c6a8e-6f2b-4d1e-9a51-0d7c2b9e4a10'; AdapterType = 'VMware'
        Configuration = [pscustomobject]@{ server = 'vcenter.corp.local'; username = 'ro'; port = 443 }
        SecretFile = 'C:\ProgramData\BerMap\Collector\secrets\vcenter.txt'
    })
    ConvertTo-Json -InputObject $config -Depth 20 | Set-Content -Path $configPath -Encoding UTF8

    $result = Invoke-Merge @{}
    $config = Read-Config
    Assert-That 'plain upgrade keeps hand-written settings' { $config.Logging.LogLevel.Default -eq 'Warning' -and $config.BerMap.Collector.BatchSize -eq 250 }
    Assert-That 'plain upgrade keeps a single integration as a list' {
        $text = Get-Content -Raw -Path $configPath
        $integrations = @($config.BerMap.Collector.Integrations)
        $text -match '"Integrations":\s*\[' -and $integrations.Count -eq 1 -and
        $integrations[0].Configuration.server -eq 'vcenter.corp.local' -and $integrations[0].Configuration.port -eq 443 -and
        $integrations[0].SecretFile -eq 'C:\ProgramData\BerMap\Collector\secrets\vcenter.txt'
    }
    Assert-That 'plain upgrade keeps URL, token and name and does not re-enroll' {
        $config.BerMap.ServerUrl -eq 'https://bermap.corp.local' -and $config.BerMap.Collector.EnrollmentToken -eq $token -and
        $config.BerMap.Collector.Name -eq 'dc2-collector' -and -not $result.Reenroll
    }
    $result = Invoke-Merge @{ ServerUrl = 'https://bermap.corp.local'; EnrollmentToken = (Secure $token); Name = 'dc2-collector' }
    Assert-That 'the same values again do not re-enroll' { -not $result.Reenroll }

    # A changed URL, token or name re-enrolls; only that value changes.
    $result = Invoke-Merge @{ ServerUrl = 'https://bermap2.corp.local:8443' }
    $config = Read-Config
    Assert-That 'new server URL re-enrolls' { $result.Reenroll -and $config.BerMap.ServerUrl -eq 'https://bermap2.corp.local:8443' -and $config.BerMap.Collector.Name -eq 'dc2-collector' }
    $result = Invoke-Merge @{ EnrollmentToken = (Secure 'fedcba9876543210') }
    Assert-That 'new token re-enrolls' { $result.Reenroll -and (Read-Config).BerMap.Collector.EnrollmentToken -eq 'fedcba9876543210' }
    Assert-That 'the token is masked for output' { (Get-MaskedToken 'fedcba9876543210') -eq 'fedc... (16 characters)' }
    $result = Invoke-Merge @{ Name = 'dc2-collector-b' }
    Assert-That 'new name re-enrolls' { $result.Reenroll -and (Read-Config).BerMap.Collector.Name -eq 'dc2-collector-b' }

    # Data directory: kept on upgrade, replaced with -DataDir.
    $other = Join-Path $work 'other'
    $result = Invoke-Merge @{ DataDir = $other }
    Assert-That '-DataDir replaces the data directory' { (Read-Config).BerMap.Collector.DataDirectory -eq $other -and $result.IdFile -eq (Join-Path $other 'collector-id') }
    $result = Invoke-Merge @{}
    Assert-That 'the data directory is kept without -DataDir' { (Read-Config).BerMap.Collector.DataDirectory -eq $other }

    # .NET configuration keys are case-insensitive: hand-written casing is reused, not duplicated.
    Set-Content -Path $configPath -Encoding UTF8 -Value '{ "bermap": { "serverurl": "https://old.corp.local", "collector": { "enrollmenttoken": "abcd1234", "integrations": [] } } }'
    $result = Invoke-Merge @{ ServerUrl = 'https://new.corp.local' }
    Assert-That 'lower-case keys are updated in place, not duplicated' {
        $raw = Get-Content -Raw -Path $configPath
        ([regex]::Matches($raw, '"serverurl"', 'IgnoreCase')).Count -eq 1 -and $raw -match '"serverurl":\s*"https://new.corp.local"' -and
        ([regex]::Matches($raw, '"enrollmenttoken"', 'IgnoreCase')).Count -eq 1 -and $result.Reenroll
    }
} finally {
    Remove-Item Env:\BERMAP_TEST_DATA_DIR -ErrorAction SilentlyContinue
    Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) { throw "$($script:failures) check(s) failed." }
Write-Host 'All collector configuration checks passed.'
