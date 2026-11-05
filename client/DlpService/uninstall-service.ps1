#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Stop and remove the Windows DLP service named in APP_IDENTIFIERS.

.DESCRIPTION
  Refuses unless %ProgramData%\<APP_MUTEX>\dlp\teardown.ok exists unless -Force.
#>
[CmdletBinding()]
param(
    [switch]$RemoveFiles,
    [switch]$Force
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
$AppMutex = if ($ids['APP_MUTEX']) { $ids['APP_MUTEX'] } else { 'AlphaAITracker' }
$ServiceName = if ($ids['WINDOWS_SERVICE_NAME']) { $ids['WINDOWS_SERVICE_NAME'] } else { "$AppMutex.Service" }
$MaintTask = if ($ids['WINDOWS_SERVICE_MAINT_TASK']) { $ids['WINDOWS_SERVICE_MAINT_TASK'] } else { "${AppMutex}SvcMaint" }
$ExeName = "$ServiceName.exe"
$InstallDir = Join-Path $env:ProgramFiles "$AppMutex\Service"
$Gate = Join-Path $env:ProgramData "$AppMutex\dlp\teardown.ok"

if (-not $Force -and -not (Test-Path $Gate)) {
    Write-Error @"
Refusing to remove $ServiceName — no authorized teardown.ok.

Run on this PC first:
  client.exe --protected-uninstall --code <code>

Lab only: re-run with -Force
"@
    exit 1
}

$tr = "cmd.exe /c sc.exe stop $ServiceName & sc.exe delete $ServiceName & taskkill /F /IM $ExeName"
schtasks.exe /Create /TN $MaintTask /TR $tr /SC ONCE /ST 00:00 /RU SYSTEM /F | Out-Null
schtasks.exe /Run /TN $MaintTask | Out-Null
Start-Sleep -Seconds 3
schtasks.exe /Delete /TN $MaintTask /F | Out-Null
Write-Host "Deleted service $ServiceName (via SYSTEM task)"

if ($RemoveFiles -and (Test-Path $InstallDir)) {
    Remove-Item -Recurse -Force $InstallDir
    Write-Host "Removed $InstallDir"
}

Write-Host "Done."
