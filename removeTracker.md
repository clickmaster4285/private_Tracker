# Alpha AI Tracker – Windows Application Blocker Cleanup & Diagnostics

Lab / break-glass playbook when **protected uninstall** left AppLocker (or related OS policy) enforcing after the product is gone — e.g. Apps & features Error **1260**, “blocked by your system administrator”, or `netsh`/`schtasks`/`reg`/`ipconfig` blocked while web/DNS already cleared.

Related docs:

- [`TESTING.md`](./TESTING.md) §C Protected uninstall · §F Web Blocker leftovers
- [`AGENTS.md`](./AGENTS.md) (Protected Removal Rule, DLP changelog)
- [`client/ARCHITECTURE.md`](./client/ARCHITECTURE.md) (SilentUpdate / DLP teardown)
- [`README.md`](./README.md)

**Run PowerShell as Administrator** unless noted. Prefer **64-bit** elevated PowerShell after AppLocker is clear; ISE x86 is fine for early steps.

---

## A. Normal product path (preferred)

1. Web → Employees → **Issue Removal Code**.
2. On the PC:

```powershell
& "${env:ProgramFiles(x86)}\Alpha AI Tracker\client.exe" --protected-uninstall --code "<paste>" --no-pause
```

3. Expect `teardown.ok` under `%ProgramData%\AlphaAITracker\dlp\` and summary with `dlpGone=True`.
4. Uninstall binaries:
   - Prefer: `& "${env:ProgramFiles(x86)}\Alpha AI Tracker\unins000.exe"`
   - Avoid Control Panel until AppLocker is clear (Inno re-runs from **TEMP** → Error 1260 under Exe Enforce).
5. If `pipeUnlock=False`, AppLocker may still be enforcing — continue with §B–§D.
   Client **1.4.7+** runs elevated/SYSTEM AppLocker clear in teardown even after pipe
   `write_timeout`; reboot if apps remain blocked (AppID in-memory policy).

---

## B. Full OS leftover wipe (hosts / FW / AppLocker / USB / ACL)

Use when the tracker is already uninstalled (or service gone) but DNS/CDN/apps/tools still look locked.

```powershell
# --- 0) leftover processes ---
Stop-Process -Name client, 'AlphaAITracker.Service', unins000 -Force -ErrorAction SilentlyContinue

# --- 1) hosts DNS sink ---
$hosts = "$env:SystemRoot\System32\drivers\etc\hosts"
if (Test-Path $hosts) {
  $c = Get-Content -LiteralPath $hosts -Raw
  if ($c -match 'AlphaAI-DLP') {
    $lines = $c -replace "`r`n","`n" -replace "`r","`n" -split "`n"
    $kept = New-Object System.Collections.Generic.List[string]
    $in = $false
    foreach ($line in $lines) {
      $t = $line.Trim()
      if ($t -eq '# AlphaAI-DLP-BEGIN') { $in = $true; continue }
      if ($t -eq '# AlphaAI-DLP-END') { $in = $false; continue }
      if ($in) { continue }
      if ($t -like '*# AlphaAI-DLP*') { continue }
      [void]$kept.Add($line)
    }
    while ($kept.Count -gt 0 -and [string]::IsNullOrWhiteSpace($kept[$kept.Count-1])) { $kept.RemoveAt($kept.Count-1) }
    $out = ($kept -join "`r`n"); if ($out.Length -gt 0) { $out += "`r`n" }
    [System.IO.File]::WriteAllText($hosts, $out, (New-Object System.Text.UTF8Encoding $false))
    'hosts: cleared'
  } else { 'hosts: already clean' }
}

# --- 2) firewall apex + LOCK + PIN (CDN) — COM (netsh often AppLocker-blocked) ---
$fw = New-Object -ComObject HNetCfg.FwPolicy2
$toRemove = @($fw.Rules | Where-Object { $_.Name -like 'AlphaAI-DLP-*' } | ForEach-Object { $_.Name })
foreach ($n in ($toRemove | Select-Object -Unique)) {
  try { $fw.Rules.Remove($n); "fw removed: $n" } catch { "fw fail $n : $($_.Exception.Message)" }
}
if (-not $toRemove) { 'fw: no AlphaAI-DLP rules via COM' }

# --- 3) AppLocker local clear (UTF-16; Windows PowerShell 5.1) ---
$xml = @'
<AppLockerPolicy Version="1">
  <RuleCollection Type="Exe" EnforcementMode="NotConfigured" />
  <RuleCollection Type="Msi" EnforcementMode="NotConfigured" />
  <RuleCollection Type="Script" EnforcementMode="NotConfigured" />
  <RuleCollection Type="Appx" EnforcementMode="NotConfigured" />
  <RuleCollection Type="Dll" EnforcementMode="NotConfigured" />
</AppLockerPolicy>
'@
$p = Join-Path $env:TEMP 'alpha-applocker-clear.xml'
[System.IO.File]::WriteAllText($p, $xml, [System.Text.Encoding]::Unicode)
Set-AppLockerPolicy -XmlPolicy $p
Restart-Service AppIDSvc -Force -ErrorAction SilentlyContinue
'AppLocker: NotConfigured applied'

# --- 4) Chrome / Edge policy leftovers ---
foreach ($bk in @(
  'HKLM:\SOFTWARE\Policies\Google\Chrome',
  'HKLM:\SOFTWARE\Policies\Microsoft\Edge'
)) {
  foreach ($sub in @('URLBlocklist','URLAllowlist')) {
    $path = Join-Path $bk $sub
    if (Test-Path $path) { Remove-Item -LiteralPath $path -Recurse -Force; "reg removed $path" }
  }
  if (Test-Path $bk) {
    foreach ($v in @('DnsOverHttpsMode','QuicAllowed')) {
      if ($null -ne (Get-ItemProperty -LiteralPath $bk -Name $v -ErrorAction SilentlyContinue)) {
        Remove-ItemProperty -LiteralPath $bk -Name $v -Force -ErrorAction SilentlyContinue
      }
    }
  }
}

# --- 5) USBSTOR demand (Start=3) — registry API (reg.exe may be blocked) ---
Set-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\USBSTOR' -Name Start -Value 3 -Type DWord
"USBSTOR Start=$((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\USBSTOR' -Name Start).Start)"

# --- 6) Device install restriction leftovers ---
$di = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions'
foreach ($name in @('AlphaAI-DLP-OwnedDenyClasses','AlphaAI-DLP-OwnedAllowIds')) {
  $path = Join-Path $di $name
  if (Test-Path $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
if (Test-Path $di) {
  (Get-Item $di).Property | Where-Object { $_ -like 'AlphaAI-DLP*' } | ForEach-Object {
    Remove-ItemProperty -LiteralPath $di -Name $_ -Force -ErrorAction SilentlyContinue
  }
}

# --- 7) delete-block Deny Delete ACEs (Authenticated Users) on fixed roots ---
$sid = New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-11'
Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Root -match '^[A-Z]:\\$' -and (Test-Path $_.Root) } | ForEach-Object {
  $root = $_.Root
  try {
    $acl = Get-Acl -LiteralPath $root
    $changed = $false
    foreach ($rule in @($acl.Access)) {
      if ($rule.AccessControlType -ne 'Deny') { continue }
      try {
        if ($rule.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value -ne $sid.Value) { continue }
      } catch { continue }
      if ($rule.FileSystemRights.ToString() -match 'Delete') {
        [void]$acl.RemoveAccessRule($rule)
        $changed = $true
      }
    }
    if ($changed) { Set-Acl -LiteralPath $root -AclObject $acl; "ACL cleared deny-delete on $root" }
  } catch { "ACL skip $root : $($_.Exception.Message)" }
}

# --- 8) leftover folders ---
@(
  'C:\ProgramData\AlphaAITracker',
  "$env:LOCALAPPDATA\AlphaAITracker",
  "$env:APPDATA\AlphaAITracker"
) | ForEach-Object {
  if (Test-Path $_) { Remove-Item -LiteralPath $_ -Recurse -Force -ErrorAction SilentlyContinue; "removed $_" }
}

# --- 9) DNS cache (cmdlet; ipconfig.exe may be blocked) ---
try { Clear-DnsClientCache; 'DNS cache cleared' } catch { $_.Exception.Message }
```

### Quick verify

```powershell
Select-String -Path "$env:SystemRoot\System32\drivers\etc\hosts" -Pattern 'AlphaAI-DLP' -ErrorAction SilentlyContinue
# After tools are unblocked:
# netsh advfirewall firewall show rule name=all | findstr AlphaAI-DLP
Get-AppLockerPolicy -Effective -Xml | Select-String EnforcementMode
(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\USBSTOR' -Name Start).Start
Test-Path 'C:\ProgramData\AlphaAITracker'
```

Expect: no hosts hits, Effective AppLocker `NotConfigured`, USBSTOR `Start=3`, ProgramData absent. Web/DNS/CDN often clear before apps/tools — that is normal if AppID still holds a stale Exe policy in memory.

---

## C. Application blocker registry purge (AppLocker / SRP)

When Effective AppLocker already shows `NotConfigured` but apps (calc, Chrome, etc.) still show “blocked by your system administrator”, purge stores then **reboot** so AppID unloads the old enforce set.

```powershell
# 1. Remove AppLocker policy registry configuration
reg delete "HKLM\SOFTWARE\Policies\Microsoft\Windows\SrpV2" /f

# 2. Remove Software Restriction Policy configuration
reg delete "HKLM\SOFTWARE\Policies\Microsoft\Windows\Safer" /f
```

PowerShell equivalents (also usable in Safe Mode):

```powershell
# 3. Safe Mode / elevated cleanup
Remove-Item -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\SrpV2' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Safer' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Srp' -Recurse -Force -ErrorAction SilentlyContinue

# Re-apply empty NotConfigured (Windows PowerShell 5.1 — Encoding::Unicode)
$xml = @'
<AppLockerPolicy Version="1">
  <RuleCollection Type="Exe" EnforcementMode="NotConfigured" />
  <RuleCollection Type="Msi" EnforcementMode="NotConfigured" />
  <RuleCollection Type="Script" EnforcementMode="NotConfigured" />
  <RuleCollection Type="Appx" EnforcementMode="NotConfigured" />
  <RuleCollection Type="Dll" EnforcementMode="NotConfigured" />
</AppLockerPolicy>
'@
$p = Join-Path $env:TEMP 'alpha-applocker-clear.xml'
[System.IO.File]::WriteAllText($p, $xml, [System.Text.Encoding]::Unicode)
Set-AppLockerPolicy -XmlPolicy $p
Restart-Service AppIDSvc -Force -ErrorAction SilentlyContinue
```

```powershell
# 4. Refresh Group Policy
gpupdate /force

# 5. Restart Windows (required when notepad/Edge work but other apps still blocked)
shutdown /r /t 0
```

---

## D. Diagnostics (after reboot or when blocks persist)

```powershell
# 6. AppLocker effective policy verification
Get-AppLockerPolicy -Effective -Xml

# 7. AppLocker registry verification
Get-ChildItem 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\SrpV2' -Recurse -ErrorAction SilentlyContinue

# 8. SRP registry verification
Get-ChildItem 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Safer' -Recurse -ErrorAction SilentlyContinue

# 9. WDAC active policy verification
Get-ChildItem 'C:\Windows\System32\CodeIntegrity\CiPolicies\Active' -ErrorAction SilentlyContinue
Get-Item 'C:\Windows\System32\CodeIntegrity\SIPolicy.p7b' -ErrorAction SilentlyContinue

# 10. Application Identity service status
Get-Service AppIDSvc

# 11. Recent AppLocker events
Get-WinEvent -FilterHashtable @{
  LogName='Microsoft-Windows-AppLocker/EXE and DLL'
  StartTime=(Get-Date).AddHours(-2)
} -MaxEvents 10 -ErrorAction SilentlyContinue |
  Format-List TimeCreated,Id,Message

# 12. Code Integrity policy files
Get-ChildItem 'C:\Windows\System32\CodeIntegrity' -Force -ErrorAction SilentlyContinue |
  Select-Object Name,Length,LastWriteTime

# 13. Code Integrity events
Get-WinEvent -FilterHashtable @{
  LogName='Microsoft-Windows-CodeIntegrity/Operational'
  StartTime=(Get-Date).AddDays(-1)
} -MaxEvents 30 -ErrorAction SilentlyContinue |
  Select-Object TimeCreated,Id,Message | Format-List

# 14. AppLocker denial events
Get-WinEvent -FilterHashtable @{
  LogName='Microsoft-Windows-AppLocker/EXE and DLL'
  Id=8003,8004
  StartTime=(Get-Date).AddDays(-1)
} -MaxEvents 20 -ErrorAction SilentlyContinue |
  Select-Object TimeCreated,Id,Message | Format-List

# 15. Computer Group Policy report
gpresult /scope computer /r

# Optional Device Guard / WDAC enforcement status
Get-CimInstance -ClassName Win32_DeviceGuard -Namespace root\Microsoft\Windows\DeviceGuard |
  Select-Object CodeIntegrityPolicyEnforcementStatus,
                UsermodeCodeIntegrityPolicyEnforcementStatus,
                SecurityServicesRunning
```

### How to read results

| Symptom | Likely cause | Action |
|--------|---------------|--------|
| Hosts / sites work; apps blocked; Effective AppLocker NotConfigured | AppID stale in-memory policy | Reboot (§C step 5) |
| Error 1260 on `unins000` / Apps & features | Inno runs from TEMP under Exe Enforce | Clear AppLocker first, then `unins000` from Program Files |
| `netsh` / `schtasks` / `reg` / `ipconfig` Error 1260 | Same Exe Enforce (SYSTEM-only tools under product lockdown) | Use COM/registry cmdlets (§B); purge + reboot |
| Effective Exe still `Enabled` after local clear | Domain GPO / other AppLocker source | `gpresult`; remove domain policy |
| WDAC `SIPolicy.p7b` / CiPolicies present + CI events | Not this product’s AppLocker | Org WDAC — out of Alpha AI Tracker scope |

---

## E. Manual binary + ARP remove (if Inno still fails)

Only after AppLocker is clear (or you accept deleting files without Inno):

```powershell
Stop-Process -Name client, 'AlphaAITracker.Service', unins000 -Force -ErrorAction SilentlyContinue
$app = "${env:ProgramFiles(x86)}\Alpha AI Tracker"
if (Test-Path -LiteralPath $app) { Remove-Item -LiteralPath $app -Recurse -Force }

Remove-Item -LiteralPath 'C:\ProgramData\AlphaAITracker' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath "$env:LOCALAPPDATA\AlphaAITracker" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath "$env:APPDATA\AlphaAITracker" -Recurse -Force -ErrorAction SilentlyContinue

foreach ($root in @(
  'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
  'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
)) {
  Get-ChildItem $root -ErrorAction SilentlyContinue | ForEach-Object {
    $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
    if ($p.DisplayName -like '*Alpha AI Tracker*') {
      Remove-Item -LiteralPath $_.PSPath -Recurse -Force
      "Removed $($p.DisplayName)"
    }
  }
}
```
