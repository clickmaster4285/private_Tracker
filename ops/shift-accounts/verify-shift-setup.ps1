#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Read-only acceptance check for a shared shift PC. Changes nothing.
  Prints PASS / FAIL / MANUAL per checklist item and exits 1 when anything FAILs.
#>
[CmdletBinding()]
param(
    [string[]] $Accounts = @('Shift-Morning', 'Shift-Evening', 'Shift-Night'),
    [string] $ServiceName = 'AlphaAITracker.Service',
    [string] $DataFolder = 'AlphaAITracker'
)

$results = New-Object System.Collections.ArrayList
function Add-Result($item, $status, $detail) {
    [void]$results.Add([pscustomobject]@{ Check = $item; Result = $status; Detail = $detail })
}

$adminsSid = 'S-1-5-32-544'
$admins = @(Get-LocalGroupMember -SID $adminsSid -ErrorAction SilentlyContinue | ForEach-Object { $_.Name.Split('\')[-1] })

# 1. Accounts exist, enabled, standard, listed.
$hidden = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList'
foreach ($name in $Accounts) {
    $u = Get-LocalUser -Name $name -ErrorAction SilentlyContinue
    if (-not $u) { Add-Result "Account $name" 'FAIL' 'missing'; continue }
    $problems = @()
    if (-not $u.Enabled) { $problems += 'disabled' }
    if ($admins -contains $name) { $problems += 'is an Administrator' }
    if ((Get-ItemProperty -Path $hidden -Name $name -ErrorAction SilentlyContinue).$name -eq 0) { $problems += 'hidden from sign-in' }
    if ($problems) { Add-Result "Account $name" 'FAIL' ($problems -join ', ') }
    else { Add-Result "Account $name" 'PASS' 'enabled, Standard user, listed on sign-in screen' }
}

# 2. No auto-logon / shift tasks.
$auto = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -ErrorAction SilentlyContinue).AutoAdminLogon
Add-Result 'AutoAdminLogon off' ($(if ("$auto" -eq '1') { 'FAIL' } else { 'PASS' })) "AutoAdminLogon=$auto"
$tasks = @('ShiftLogon-Change', 'ShiftLogon-Boot' | Where-Object { Get-ScheduledTask -TaskName $_ -ErrorAction SilentlyContinue })
Add-Result 'No shift-switching tasks' ($(if ($tasks.Count) { 'FAIL' } else { 'PASS' })) ($(if ($tasks.Count) { $tasks -join ', ' } else { 'none' }))

# 3. Switch user available (the DLP service signs the previous user out instead).
$fus = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -ErrorAction SilentlyContinue).HideFastUserSwitching
Add-Result 'Switch user available' ($(if ($fus -eq 1) { 'FAIL' } else { 'PASS' })) "HideFastUserSwitching=$fus"

# 4. DLP service independent of the tracker (LocalSystem, running, auto-start).
$svc = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
if (-not $svc) { Add-Result 'DLP service' 'FAIL' 'not installed' }
else {
    $ok = $svc.State -eq 'Running' -and $svc.StartName -eq 'LocalSystem' -and $svc.StartMode -eq 'Auto'
    Add-Result 'DLP service' ($(if ($ok) { 'PASS' } else { 'FAIL' })) "$($svc.State), $($svc.StartName), $($svc.StartMode)"
    $envFile = Join-Path $env:ProgramData "$DataFolder\dlp\service.env"
    if (Test-Path $envFile) {
        $single = (Select-String -Path $envFile -Pattern '^ALPHA_DLP_SINGLE_SESSION=(.*)$' | Select-Object -First 1).Matches.Groups[1].Value
        Add-Result 'Single-session guard' ($(if ("$single" -ne 'false') { 'PASS' } else { 'FAIL' })) "ALPHA_DLP_SINGLE_SESSION=$(if ($single) { $single } else { 'true (default)' })"
    }
}

# 5. Each account has its own tracker data, other users cannot read it.
foreach ($name in $Accounts) {
    $u = Get-LocalUser -Name $name -ErrorAction SilentlyContinue
    if (-not $u) { continue }
    $profile = (Get-CimInstance Win32_UserProfile -Filter "SID='$($u.SID.Value)'" -ErrorAction SilentlyContinue).LocalPath
    if (-not $profile) { Add-Result "Tracker data $name" 'FAIL' 'never signed in (no profile yet)'; continue }
    $data = Join-Path $profile "AppData\Local\$DataFolder"
    if (-not (Test-Path $data)) { Add-Result "Tracker data $name" 'FAIL' 'tracker never logged in under this account'; continue }
    # Builtin Users / Everyone / Authenticated Users must not have read access to the profile.
    $acl = Get-Acl -Path $profile
    $broad = $acl.Access | Where-Object {
        $_.AccessControlType -eq 'Allow' -and
        ($_.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::ReadData) -and
        @('S-1-5-32-545', 'S-1-1-0', 'S-1-5-11') -contains $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
    }
    if ($broad) { Add-Result "Tracker data $name" 'FAIL' 'profile readable by all users' }
    else { Add-Result "Tracker data $name" 'PASS' "$data (private to $name)" }
}

# 6. Sessions right now.
$sessions = @(quser 2>$null | Select-Object -Skip 1)
$disc = @($sessions | Where-Object { $_ -match '\sDisc\s' })
Add-Result 'One employee session' ($(if ($disc.Count -eq 0) { 'PASS' } else { 'FAIL' })) "$($sessions.Count) session(s), $($disc.Count) disconnected"

# Manual items (need a person at the PC).
foreach ($m in @(
    'Correct employee passes face verification',
    'Wrong employee cannot reach the desktop',
    'Windows lock requires face verification again',
    'Sign out stops the tracker cleanly',
    'Offline events survive sign-out and restart',
    'Attendance shows verified arrival (web: Attendance, arrival source)',
    'Night shift attendance is one record across midnight')) {
    Add-Result $m 'MANUAL' 'see README.md acceptance steps'
}

$results | Format-Table -AutoSize -Wrap
if ($results.Result -contains 'FAIL') { exit 1 }
