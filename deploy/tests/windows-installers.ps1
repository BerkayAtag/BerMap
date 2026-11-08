<#
.SYNOPSIS
    Static checks of the Windows server and collector installers that run anywhere
    (Windows PowerShell 5.1 and PowerShell 7, also on Linux/macOS):

        pwsh -NoProfile -NonInteractive -File deploy/tests/windows-installers.ps1

.DESCRIPTION
    Every script parses. The ownership check (Assert-TrustedOwnership) runs before an
    existing configuration is read; protected ACLs set the Administrators owner; the
    collector never identifies its data directory by appsettings.Production.json (the
    server's file has the same name); markers of pre-existing directories are written only
    after a successful installation. The directory-state helpers are exercised against
    temporary directories where the platform allows it.
#>
$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..'
$script:failures = 0

function Assert-That([string] $Description, [scriptblock] $Condition) {
    $ok = $false
    try { $ok = [bool] (& $Condition) } catch { Write-Host "  $($_.Exception.Message)" }
    if ($ok) { Write-Host "ok   $Description" } else { Write-Host "FAIL $Description" -ForegroundColor Red; $script:failures++ }
}

function Read-Script([string] $RelativePath) {
    $path = Join-Path $root $RelativePath
    $text = Get-Content -Raw -Path $path
    $tokens = $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($text, [ref] $tokens, [ref] $errors)
    Assert-That "$RelativePath parses" { $errors.Count -eq 0 }
    return [pscustomobject]@{ Text = $text; Ast = $ast }
}

function Get-Function($Script, [string] $Name) {
    $Script.Ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name }, $true) |
        Select-Object -First 1
}

$collector = Read-Script 'collector/windows/Install-BerMapCollector.ps1'
$collectorUninstall = Read-Script 'collector/windows/Uninstall-BerMapCollector.ps1'
$server = Read-Script 'windows/Install-BerMapServer.ps1'
$serverUninstall = Read-Script 'windows/Uninstall-BerMapServer.ps1'

foreach ($installer in @(@{ Name = 'collector'; Script = $collector; Read = '$config = if (Test-Path $configPath)' },
                         @{ Name = 'server'; Script = $server; Read = '$config = if ($existingPath)' })) {
    $text = $installer.Script.Text
    $check = $text.IndexOf("`nAssert-TrustedOwnership ")
    $read = $text.IndexOf($installer.Read)
    Assert-That "$($installer.Name): ownership is checked before the existing configuration is read" { $check -gt 0 -and $read -gt $check }
    Assert-That "$($installer.Name): has a -Force switch" { $text -match '\[switch\] \$Force' }
    $acl = Get-Function $installer.Script 'New-ProtectedAcl'
    Assert-That "$($installer.Name): protected ACLs make Administrators the owner" { $acl -and $acl.Extent.Text -match "SetOwner\(.*S-1-5-32-544" }
    Assert-That "$($installer.Name): the data directory gets the protected ACL" { $text -match 'Set-Acl -Path \$DataDir -AclObject \(New-ProtectedAcl \$true\)' }
    Assert-That "$($installer.Name): an untrusted owner is refused without -Force" {
        (Get-Function $installer.Script 'Assert-TrustedOwnership').Extent.Text -match 'if \(-not \$Force\) \{\s*throw'
    }
    $successMarker = $text.IndexOf("if (`$installDirState -eq 'existing') { Set-Content")
    Assert-That "$($installer.Name): a pre-existing default directory is marked only at the end" {
        $successMarker -gt $text.IndexOf('Start-Service -Name $ServiceName')
    }
    Assert-That "$($installer.Name): an empty non-default install directory is refused" {
        $text -match "elseif \(\`$installFull -eq \(Get-FullPath \`$defaultInstallDir\)"
    }
}

Assert-That 'server: writes the server data marker' { $server.Text -match "\`$dataMarker = '\.bermap-server-data'" }
Assert-That 'collector: refuses the server data directory' { $collector.Text -match 'if \(Test-ServerDataDir \$DataDir\) \{\s*throw' }
Assert-That 'collector installer: appsettings.Production.json does not identify a collector data directory' {
    $state = $collector.Text.Substring($collector.Text.IndexOf('$dataDirState = '))
    $state = $state.Substring(0, $state.IndexOf("`n`n"))
    $state -notmatch 'appsettings'
}
Assert-That 'collector uninstaller: appsettings.Production.json does not identify a collector data directory' {
    $line = ($collectorUninstall.Text -split "`n" | Where-Object { $_ -match '^\s*\$isDataDir = ' }) -join ''
    $line -and $line -notmatch 'appsettings'
}
Assert-That 'collector uninstaller: leaves the server data directory alone' { $collectorUninstall.Text -match 'elseif \(Test-ServerDataDir \$DataDir\)' }
Assert-That 'server uninstaller: keeps collector data directories below its own' { $serverUninstall.Text -match 'Test-CollectorDataDir' }

# Directory helpers against real directories (they need no Windows APIs).
$work = Join-Path ([System.IO.Path]::GetTempPath()) "bermap-win-test-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $configVariable = 'BERMAP_CONFIG_FILE'
    $serverServiceKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\BerMapServer-does-not-exist'
    foreach ($name in 'Get-FullPath', 'Get-ServerDataDir', 'Test-ServerDataDir') {
        . ([scriptblock]::Create((Get-Function $collector $name).Extent.Text))
    }
    $serverDir = Join-Path $work 'server'
    New-Item -ItemType Directory -Path (Join-Path $serverDir 'vault') -Force | Out-Null
    $collectorDir = Join-Path $work 'collector'
    New-Item -ItemType Directory -Path $collectorDir | Out-Null
    Set-Content -Path (Join-Path $collectorDir 'appsettings.Production.json') -Value '{ "BerMap": { "ServerUrl": "https://x" } }'
    $plainServer = Join-Path $work 'server-config-only'
    New-Item -ItemType Directory -Path $plainServer | Out-Null
    Set-Content -Path (Join-Path $plainServer 'appsettings.Production.json') -Value '{ "ConnectionStrings": { "DefaultConnection": "Host=db" } }'

    Assert-That 'a directory with a vault is the server data directory' { Test-ServerDataDir $serverDir }
    Assert-That 'a server configuration (ConnectionStrings) marks the server data directory' { Test-ServerDataDir $plainServer }
    Assert-That 'a collector configuration does not' { -not (Test-ServerDataDir $collectorDir) }

    . ([scriptblock]::Create((Get-Function $collectorUninstall 'Remove-InstalledDirectory').Extent.Text))
    $existing = Join-Path $work 'existing'
    New-Item -ItemType Directory -Path (Join-Path $existing 'sub') -Force | Out-Null
    Set-Content -Path (Join-Path $existing '.bermap-collector') -Value 'existing'
    Remove-InstalledDirectory $existing '.bermap-collector' | Out-Null
    Assert-That 'uninstall empties a directory found in place and keeps it' { (Test-Path $existing) -and -not (Get-ChildItem -Path $existing -Force) }
    $created = Join-Path $work 'created'
    New-Item -ItemType Directory -Path $created | Out-Null
    Set-Content -Path (Join-Path $created '.bermap-collector') -Value 'created'
    Remove-InstalledDirectory $created '.bermap-collector' | Out-Null
    Assert-That 'uninstall removes a directory the installer created' { -not (Test-Path $created) }
} finally {
    Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) { throw "$($script:failures) check(s) failed." }
Write-Host 'All Windows installer checks passed.'
