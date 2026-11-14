#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Install the Windows DLP service (APP_IDENTIFIERS WINDOWS_SERVICE_NAME) as LocalSystem.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\install-service.ps1
#>
[CmdletBinding()]
param(
    [string]$Source = "",
    [string]$ServerUrl = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$IdsFile = Join-Path (Split-Path $ScriptDir -Parent) "APP_IDENTIFIERS"

function Read-AppIdentifiers([string]$Path) {
    $map = @{}
    if (-not (Test-Path $Path)) { throw "APP_IDENTIFIERS not found at $Path" }
    Get-Content $Path | ForEach-Object {
        $line = $_.Trim()
        if ($line.Length -eq 0 -or $line.StartsWith('#')) { return }
        if ($line -match '^\s*([A-Z0-9_]+)\s*=\s*"(.*)"\s*$') {
            $map[$Matches[1]] = $Matches[2]
        } elseif ($line -match "^\s*([A-Z0-9_]+)\s*=\s*'(.*)'\s*$") {
            $map[$Matches[1]] = $Matches[2]
        } elseif ($line -match '^\s*([A-Z0-9_]+)\s*=\s*([^#\s]+)') {
            $map[$Matches[1]] = $Matches[2]
        }
    }
    return $map
}

$ids = Read-AppIdentifiers $IdsFile
$AppMutex = $ids['APP_MUTEX']
if (-not $AppMutex) { $AppMutex = 'AlphaAITracker' }
$DisplayName = $ids['DISPLAY_NAME']
if (-not $DisplayName) { $DisplayName = 'Alpha AI Tracker' }
$ServiceName = $ids['WINDOWS_SERVICE_NAME']
if (-not $ServiceName) { $ServiceName = "$AppMutex.Service" }
$ServiceDisplayName = $ids['WINDOWS_SERVICE_DISPLAY_NAME']
if (-not $ServiceDisplayName) { $ServiceDisplayName = "$DisplayName DLP" }
$PipeName = $ids['WINDOWS_DLP_PIPE_NAME']
if (-not $PipeName) { $PipeName = "$AppMutex.Dlp" }
$MaintTask = $ids['WINDOWS_SERVICE_MAINT_TASK']
if (-not $MaintTask) { $MaintTask = "${AppMutex}SvcMaint" }

$InstallDir = Join-Path $env:ProgramFiles "$AppMutex\Service"
$DataDir = Join-Path $env:ProgramData "$AppMutex\dlp"
$ExeName = "$ServiceName.exe"

function Stop-IfRunning {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc) {
        $tr = "cmd.exe /c sc.exe stop $ServiceName & sc.exe delete $ServiceName & taskkill /F /IM $ExeName"
        schtasks.exe /Create /TN $MaintTask /TR $tr /SC ONCE /ST 00:00 /RU SYSTEM /F | Out-Null
        schtasks.exe /Run /TN $MaintTask | Out-Null
        Start-Sleep -Seconds 3
        schtasks.exe /Delete /TN $MaintTask /F | Out-Null
    }
    Get-Process -Name $ServiceName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

if (-not $Source) {
    $Source = Join-Path $ScriptDir "bin\Release\net10.0"
    if (-not (Test-Path (Join-Path $Source $ExeName))) {
        $Source = Join-Path $ScriptDir "bin\Release\net10.0\win-x64"
    }
}

if (-not $SkipBuild -and -not (Test-Path (Join-Path $Source $ExeName))) {
    Write-Host "Building Release DlpService..."
    Push-Location $ScriptDir
    try {
        bash ../publish/generate-windows-vars.sh 2>$null
        dotnet build -c Release -v q
        $Source = Join-Path $ScriptDir "bin\Release\net10.0"
    } finally {
        Pop-Location
    }
}

$srcExe = Join-Path $Source $ExeName
if (-not (Test-Path $srcExe)) {
    throw "Service binary not found at $srcExe. Build DlpService Release first or pass -Source."
}

if (-not $ServerUrl) {
    $ServerUrl = $env:ALPHA_SERVER_URL
}
if (-not $ServerUrl) {
    $clientEnv = Join-Path (Split-Path $ScriptDir -Parent) ".env"
    if (Test-Path $clientEnv) {
        foreach ($line in Get-Content $clientEnv) {
            if ($line -match '^\s*ALPHA_SERVER_URL\s*=\s*(.+)$') {
                $ServerUrl = $Matches[1].Trim().Trim('"').Trim("'")
                break
            }
        }
    }
}

Write-Host "Stopping any existing $ServiceName / interactive instance..."
Stop-IfRunning

Write-Host "Installing to $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
Copy-Item -Path (Join-Path $Source "*") -Destination $InstallDir -Recurse -Force

$serviceEnvPath = Join-Path $DataDir "service.env"
# Defaults match .env.example; override from client/.env when present (lab / config.enc source).
$clientEnvPath = Join-Path (Split-Path $ScriptDir -Parent) ".env"
function Read-EnvKey([string]$Path, [string]$Key, [string]$Fallback) {
    if (-not (Test-Path $Path)) { return $Fallback }
    foreach ($line in Get-Content $Path) {
        if ($line -match ("^\s*" + [regex]::Escape($Key) + "\s*=\s*(.+)$")) {
            return $Matches[1].Trim().Trim('"').Trim("'")
        }
    }
    return $Fallback
}
# Upsert bake-controlled knobs from client/.env (same as Inno SetEnvKey on install).
# Does not remove unrelated keys (e.g. leftover comments); never invents device tokens.
$defaults = [ordered]@{
    "ALPHA_DLP_ENABLED"            = (Read-EnvKey $clientEnvPath "ALPHA_DLP_ENABLED" "true")
    "ALPHA_DLP_USB_BLOCK"          = (Read-EnvKey $clientEnvPath "ALPHA_DLP_USB_BLOCK" "true")
    "ALPHA_DLP_WEB_BLOCK"          = (Read-EnvKey $clientEnvPath "ALPHA_DLP_WEB_BLOCK" "true")
    "ALPHA_DLP_NETWORK_LOCKDOWN"   = (Read-EnvKey $clientEnvPath "ALPHA_DLP_NETWORK_LOCKDOWN" "true")
    "ALPHA_DLP_APP_BLOCK"          = (Read-EnvKey $clientEnvPath "ALPHA_DLP_APP_BLOCK" "true")
    "ALPHA_DLP_PORT_BLOCK"         = (Read-EnvKey $clientEnvPath "ALPHA_DLP_PORT_BLOCK" "true")
    "ALPHA_DLP_DELETE_BLOCK"       = (Read-EnvKey $clientEnvPath "ALPHA_DLP_DELETE_BLOCK" "true")
    "ALPHA_DLP_FILE_TRANSFER_BLOCK" = (Read-EnvKey $clientEnvPath "ALPHA_DLP_FILE_TRANSFER_BLOCK" "true")
    "ALPHA_DLP_LOGIN_REBOOT"       = (Read-EnvKey $clientEnvPath "ALPHA_DLP_LOGIN_REBOOT" "true")
    "ALPHA_DLP_LOGIN_REBOOT_DELAY_SEC" = (Read-EnvKey $clientEnvPath "ALPHA_DLP_LOGIN_REBOOT_DELAY_SEC" "5")
    "ALPHA_DLP_POLICY_POLL_SEC"    = (Read-EnvKey $clientEnvPath "ALPHA_DLP_POLICY_POLL_SEC" "300")
    "ALPHA_DLP_ENFORCE_TICK_SEC"   = (Read-EnvKey $clientEnvPath "ALPHA_DLP_ENFORCE_TICK_SEC" "60")
    "ALPHA_DLP_APP_SWEEP_SEC"      = (Read-EnvKey $clientEnvPath "ALPHA_DLP_APP_SWEEP_SEC" "120")
    "ALPHA_DLP_DNS_PIN_SEC"        = (Read-EnvKey $clientEnvPath "ALPHA_DLP_DNS_PIN_SEC" "10")
    "ALPHA_DLP_TCP_KILL_SEC"       = (Read-EnvKey $clientEnvPath "ALPHA_DLP_TCP_KILL_SEC" "30")
    "ALPHA_DLP_PORT_GUARD_SEC"     = (Read-EnvKey $clientEnvPath "ALPHA_DLP_PORT_GUARD_SEC" "15")
    "ALPHA_DLP_DELETE_RECHECK_SEC" = (Read-EnvKey $clientEnvPath "ALPHA_DLP_DELETE_RECHECK_SEC" "600")
    "ALPHA_DLP_ALERT_SYNC_SEC"     = (Read-EnvKey $clientEnvPath "ALPHA_DLP_ALERT_SYNC_SEC" "60")
    "ALPHA_DLP_WATCHDOG"           = (Read-EnvKey $clientEnvPath "ALPHA_DLP_WATCHDOG" "true")
    "ALPHA_DLP_WATCHDOG_POLL_SEC"  = (Read-EnvKey $clientEnvPath "ALPHA_DLP_WATCHDOG_POLL_SEC" "8")
    "ALPHA_DLP_PIPE_NAME"          = $PipeName
    "ALPHA_UPDATE_ENABLED"            = (Read-EnvKey $clientEnvPath "ALPHA_UPDATE_ENABLED" "true")
    "ALPHA_UPDATE_AUTO_INSTALL"       = (Read-EnvKey $clientEnvPath "ALPHA_UPDATE_AUTO_INSTALL" "true")
    "ALPHA_UPDATE_CHECK_INTERVAL_SEC" = (Read-EnvKey $clientEnvPath "ALPHA_UPDATE_CHECK_INTERVAL_SEC" "60")
    "ALPHA_UPDATE_DOWNLOAD_JITTER_SEC" = (Read-EnvKey $clientEnvPath "ALPHA_UPDATE_DOWNLOAD_JITTER_SEC" "3600")
}
if ($ServerUrl) { $defaults["ALPHA_SERVER_URL"] = $ServerUrl }
Write-Host "service.env from .env: POLICY_POLL=$($defaults['ALPHA_DLP_POLICY_POLL_SEC'])"

$lines = New-Object System.Collections.Generic.List[string]
$seen = @{}
if (Test-Path $serviceEnvPath) {
    Get-Content $serviceEnvPath -ErrorAction SilentlyContinue | ForEach-Object {
        $raw = $_
        $line = $raw.Trim()
        if (-not $line -or $line.StartsWith("#")) {
            [void]$lines.Add($raw)
            return
        }
        $eq = $line.IndexOf("=")
        if ($eq -le 0) { [void]$lines.Add($raw); return }
        $key = $line.Substring(0, $eq).Trim()
        if ($defaults.Contains($key)) {
            [void]$lines.Add("$key=$($defaults[$key])")
            $seen[$key] = $true
        } else {
            [void]$lines.Add($raw)
        }
    }
}
foreach ($key in $defaults.Keys) {
    if (-not $seen.ContainsKey($key)) { [void]$lines.Add("$key=$($defaults[$key])") }
}
Set-Content -Path $serviceEnvPath -Value ($lines -join "`r`n") -Encoding UTF8
Write-Host "Upserted service.env → $serviceEnvPath"

$binPath = Join-Path $InstallDir $ExeName
$quoted = "`"$binPath`""
sc.exe create $ServiceName binPath= $quoted start= auto DisplayName= "`"$ServiceDisplayName`"" obj= LocalSystem | Out-Null
if ($LASTEXITCODE -ne 0) {
    New-Service -Name $ServiceName -BinaryPathName $binPath -DisplayName $ServiceDisplayName -StartupType Automatic -ErrorAction Stop | Out-Null
}
sc.exe description $ServiceName "$DisplayName Security & DLP enforcement (USB / policy / alerts)." | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null

Start-Service -Name $ServiceName
Start-Sleep -Seconds 2

$lockedSddl = 'D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCLCSWLOCRRC;;;BA)'
sc.exe sdset $ServiceName $lockedSddl | Out-Null
Write-Host "Applied locked service DACL (SY full, BA query-only) for $ServiceName"

$svc = Get-Service -Name $ServiceName
Write-Host "Service status: $($svc.Status)"
Write-Host "Protected uninstall: client.exe --protected-uninstall --code <code>"
Write-Host "Lab force-remove: uninstall-service.ps1 -Force"
