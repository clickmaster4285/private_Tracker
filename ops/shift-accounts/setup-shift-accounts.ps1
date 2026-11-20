#Requires -RunAsAdministrator
<#
.SYNOPSIS
  One-time setup of a shared shift PC: three Standard Windows accounts (one per shift),
  no auto-logon, no shift-switching tasks, "Switch user" available.

.DESCRIPTION
  Idempotent — safe to run again. It never deletes accounts or profiles.
  Steps:
    1. Create Shift-Morning / Shift-Evening / Shift-Night (or -Accounts) if missing.
    2. Make each one a Standard user: member of Users, NOT of Administrators.
    3. Password never expires; user cannot change it (IT owns the credential).
    4. Remove the old ShiftLogon-Change / ShiftLogon-Boot tasks and turn off AutoAdminLogon.
    5. Make sure "Switch user" is available (the DLP service signs the previous user out
       2 minutes after someone else switches in, so only one employee is tracked).
    6. Make sure the accounts are listed on the sign-in screen.

  Passwords: you are prompted for one per account (recommended). Blank passwords are only
  used with -AllowBlankPassword — Windows then blocks those accounts from network sign-in,
  and anyone at the PC can open any shift account.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\setup-shift-accounts.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\setup-shift-accounts.ps1 -AllowBlankPassword
#>
[CmdletBinding()]
param(
    [string[]] $Accounts = @('Shift-Morning', 'Shift-Evening', 'Shift-Night'),
    [switch] $AllowBlankPassword
)

$ErrorActionPreference = 'Stop'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    OK  $msg" -ForegroundColor Green }
function Write-Warn2($msg){ Write-Host "    !!  $msg" -ForegroundColor Yellow }

# Well-known SIDs — group names are localized, SIDs are not.
$usersGroup  = (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-545')).Translate([System.Security.Principal.NTAccount]).Value.Split('\')[-1]
$adminsGroup = (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')).Translate([System.Security.Principal.NTAccount]).Value.Split('\')[-1]

if ($AllowBlankPassword) {
    Write-Warn2 'Blank passwords requested: any person at this PC can open any shift account.'
}

# ── 1–3. Accounts ────────────────────────────────────────────────────────────
foreach ($name in $Accounts) {
    Write-Step "Account $name"
    $user = Get-LocalUser -Name $name -ErrorAction SilentlyContinue
    if (-not $user) {
        if ($AllowBlankPassword) {
            $user = New-LocalUser -Name $name -NoPassword -AccountNeverExpires `
                -Description 'Shared shift account (Alpha AI Tracker)'
            Set-LocalUser -Name $name -PasswordNeverExpires $true -UserMayChangePassword $false
        } else {
            $pw = Read-Host -AsSecureString "Password for $name"
            $user = New-LocalUser -Name $name -Password $pw -AccountNeverExpires `
                -PasswordNeverExpires -UserMayNotChangePassword `
                -Description 'Shared shift account (Alpha AI Tracker)'
        }
        Write-Ok 'created'
    } else {
        Set-LocalUser -Name $name -PasswordNeverExpires $true -UserMayChangePassword $false
        if (-not $user.Enabled) { Enable-LocalUser -Name $name; Write-Ok 'enabled' }
        Write-Ok 'already exists'
    }

    # Standard user: in Users, never in Administrators.
    $inUsers = Get-LocalGroupMember -Group $usersGroup -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match "\\$([regex]::Escape($name))$" }
    if (-not $inUsers) { Add-LocalGroupMember -Group $usersGroup -Member $name; Write-Ok "added to $usersGroup" }
    $inAdmins = Get-LocalGroupMember -Group $adminsGroup -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match "\\$([regex]::Escape($name))$" }
    if ($inAdmins) { Remove-LocalGroupMember -Group $adminsGroup -Member $name; Write-Ok "removed from $adminsGroup" }
    Write-Ok 'Standard user'
}

# ── 4. Old automatic shift switching ────────────────────────────────────────
Write-Step 'Remove automatic shift switching'
foreach ($task in 'ShiftLogon-Change', 'ShiftLogon-Boot') {
    if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $task -Confirm:$false
        Write-Ok "task $task removed"
    }
}
$winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
Set-ItemProperty -Path $winlogon -Name AutoAdminLogon -Value '0'
foreach ($v in 'DefaultPassword', 'AutoLogonCount') {
    Remove-ItemProperty -Path $winlogon -Name $v -ErrorAction SilentlyContinue
}
Write-Ok 'AutoAdminLogon = 0 (stored auto-logon password removed)'

# ── 5. Switch user available ─────────────────────────────────────────────────
Write-Step 'Show Switch user'
$sys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
Remove-ItemProperty -Path $sys -Name HideFastUserSwitching -ErrorAction SilentlyContinue
Write-Ok 'HideFastUserSwitching removed (Switch user shown)'

# ── 6. Listed on the sign-in screen ─────────────────────────────────────────
Write-Step 'Sign-in screen'
$hidden = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList'
foreach ($name in $Accounts) {
    if ((Get-ItemProperty -Path $hidden -Name $name -ErrorAction SilentlyContinue).$name -eq 0) {
        Remove-ItemProperty -Path $hidden -Name $name
        Write-Ok "$name un-hidden"
    }
}
Write-Ok 'accounts listed'

Write-Host ''
Write-Host 'Done. Next: restart, then sign in to EACH shift account once and log the tracker in' -ForegroundColor Green
Write-Host 'with that shift''s employee (see README.md, Step 2). Then run verify-shift-setup.ps1.' -ForegroundColor Green
