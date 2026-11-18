#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Semi-automated V2 default-deny security probes for Alpha AI Tracker lab PCs.
.PARAMETER ServerUrl
  Management URL that must remain reachable (default https://192.168.88.35).
#>
param(
  [string]$ServerUrl = "https://192.168.88.35"
)

$ErrorActionPreference = "Continue"
$results = [System.Collections.Generic.List[object]]::new()

function Add-Result([string]$Case, [string]$Expected, [string]$Observed, [string]$Pass) {
  $results.Add([pscustomobject]@{ Case = $Case; Expected = $Expected; Observed = $Observed; Pass = $Pass })
}

Write-Host "=== Alpha AI Tracker DLP Security Matrix V2 ===" -ForegroundColor Cyan
Write-Host "ServerUrl=$ServerUrl"

# ActiveStore is the Block proof. netsh local view is not.
$storeDetail = "query_failed"
$storePass = $false
try {
  $profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction Stop)
  $need = @("Domain", "Private", "Public")
  $bad = @()
  foreach ($name in $need) {
    $p = $profiles | Where-Object { $_.Name -eq $name } | Select-Object -First 1
    if (-not $p) { $bad += "$name missing" }
    elseif ([string]$p.Enabled -ne "True") { $bad += "$name disabled" }
    elseif ([string]$p.DefaultOutboundAction -ne "Block") { $bad += "$name $($p.DefaultOutboundAction)" }
  }
  $storeDetail = if ($bad.Count -eq 0) { "Enabled+Block" } else { ($bad -join "; ") }
  $storePass = $bad.Count -eq 0
} catch {
  $storeDetail = "throw:" + $_.Exception.Message
  $storePass = $false
}
Add-Result "ActiveStore Enabled+Block" "all three" $storeDetail $(if ($storePass) { "Pass" } else { "Fail" })

$quicPass = $false
$quicDetail = "missing"
try {
  $q = Get-NetFirewallRule -PolicyStore ActiveStore -Name "AlphaAI-DLP-LOCK-BLOCK-QUIC" -ErrorAction SilentlyContinue
  if (-not $q) {
    $q = @(Get-NetFirewallRule -PolicyStore ActiveStore -DisplayName "Block QUIC" -ErrorAction SilentlyContinue |
      Where-Object { $_.Name -eq "AlphaAI-DLP-LOCK-BLOCK-QUIC" -or $_.Description -eq "AlphaAI-DLP-LOCK-BLOCK-QUIC" } |
      Select-Object -First 1)
    if ($q -is [System.Array]) { $q = $q | Select-Object -First 1 }
  }
  if ($q) {
    $pf = $q | Get-NetFirewallPortFilter
    $port = (@($pf.RemotePort) | ForEach-Object { "$_" }) -join ","
    $quicDetail = "enabled=$($q.Enabled) action=$($q.Action) dir=$($q.Direction) proto=$($pf.Protocol) port=$port"
    $quicPass = ([string]$q.Enabled -eq "True" -and [string]$q.Action -eq "Block" -and [string]$q.Direction -eq "Outbound" -and [string]$pf.Protocol -eq "UDP" -and $port -eq "443")
  }
} catch {
  $quicDetail = "throw:" + $_.Exception.Message
}
Add-Result "QUIC UDP 443 block" "Block QUIC present" $quicDetail $(if ($quicPass) { "Pass" } else { "Fail" })

function Test-PortSpecIncludes443([string]$spec) {
  if ([string]::IsNullOrWhiteSpace($spec)) { return $true }
  foreach ($part in ($spec -split "[,;]")) {
    $t = $part.Trim()
    if ($t -eq "" -or $t -eq "Any" -or $t -eq "*" -or $t -eq "-" -or $t -eq "443") { return $true }
    if ($t -match "^(\d+)-(\d+)$") {
      $lo = [int]$Matches[1]; $hi = [int]$Matches[2]
      if ($lo -le 443 -and 443 -le $hi) { return $true }
    }
  }
  return $false
}
$udp443 = @()
try {
  # Product punch-through only — Store/shell UDP Any allows are ignored.
  Get-NetFirewallRule -PolicyStore ActiveStore -Direction Outbound -Action Allow -Enabled True -ErrorAction SilentlyContinue |
    Where-Object {
      $_.Name -like 'AlphaAI-DLP-LOCK-*' -or $_.DisplayName -like 'AlphaAI-DLP-LOCK-*' -or
      $_.Name -like '*ALLOW-DEST-UDP*' -or $_.DisplayName -like '*ALLOW-DEST-UDP*'
    } | ForEach-Object {
      $pf = $_ | Get-NetFirewallPortFilter
      $proto = [string]$pf.Protocol
      $udpOrAny = [string]::IsNullOrWhiteSpace($proto) -or $proto -eq "UDP" -or $proto -eq "Any" -or $proto -eq "17" -or $proto -eq "256"
      $port = (@($pf.RemotePort) | ForEach-Object { "$_" }) -join ","
      if ($udpOrAny -and (Test-PortSpecIncludes443 $port)) {
        $udp443 += $_.Name
      }
    }
} catch {
  $udp443 += "throw:" + $_.Exception.Message
}
Add-Result "No product UDP 443 allow" "none" $(if ($udp443.Count -eq 0) { "none" } else { ($udp443 | Select-Object -First 4) -join "; " }) $(if ($udp443.Count -eq 0) { "Pass" } else { "Fail" })

$sb = "unknown"
try {
  $sbRaw = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State" -ErrorAction Stop).UEFISecureBootEnabled
  $sb = [string]$sbRaw
} catch { $sb = "throw" }
Add-Result "Secure Boot" "1 when claiming signed CIP" $sb $(if ($sb -eq "1") { "Pass" } else { "Fail" })

# Public IP probe (not a Cloudflare allow or block)
$publicOk = $false
try {
  $c = New-Object System.Net.Sockets.TcpClient
  $iar = $c.BeginConnect("1.1.1.1", 443, $null, $null)
  $publicOk = $iar.AsyncWaitHandle.WaitOne(800) -and $c.Connected
  $c.Close()
} catch { $publicOk = $false }
Add-Result "Public IP 1.1.1.1:443" "unreachable" $(if ($publicOk) { "reachable" } else { "unreachable" }) $(if (-not $publicOk) { "Pass" } else { "Fail" })

# Management host should succeed
$mgmtHost = ([uri]$ServerUrl).Host
$mgmtPort = if (([uri]$ServerUrl).IsDefaultPort) { if (([uri]$ServerUrl).Scheme -eq "http") { 80 } else { 443 } } else { ([uri]$ServerUrl).Port }
$mgmtOk = $false
try {
  $c = New-Object System.Net.Sockets.TcpClient
  $iar = $c.BeginConnect($mgmtHost, $mgmtPort, $null, $null)
  $mgmtOk = $iar.AsyncWaitHandle.WaitOne(2000) -and $c.Connected
  $c.Close()
} catch { $mgmtOk = $false }
Add-Result "Mgmt $mgmtHost`:$mgmtPort" "reachable" $(if ($mgmtOk) { "reachable" } else { "unreachable" }) $(if ($mgmtOk) { "Pass" } else { "Fail" })

# HTTPS not rewritten in service.env
$envPath = Join-Path $env:ProgramData "AlphaAITracker\service.env"
$urlLine = ""
if (Test-Path $envPath) {
  $urlLine = (Get-Content $envPath | Where-Object { $_ -match '^\s*ALPHA_SERVER_URL=' } | Select-Object -First 1)
}
$httpsOk = $urlLine -match 'ALPHA_SERVER_URL=https://'
Add-Result "service.env keeps https://" "https://" $(if ($urlLine) { $urlLine } else { "missing" }) $(if ($httpsOk) { "Pass" } else { "Fail" })

# Org CIP present for Pro
$cip = Join-Path $env:ProgramData "AlphaAITracker\dlp\wdac\base-policy.cip"
$edition = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion").EditionID
$isPro = $edition -match "Professional|^Pro$"
Add-Result "Org WDAC CIP (Pro)" $(if ($isPro) { "present" } else { "N/A interim" }) $(if (Test-Path $cip) { "present" } else { "missing" }) $(if (-not $isPro -or (Test-Path $cip)) { "Pass" } else { "Fail" })

# AppLocker Exe Enabled
$al = ""
try {
  $p = Get-AppLockerPolicy -Local -ErrorAction Stop
  $col = @($p.RuleCollections | Where-Object { $_.RuleCollectionType.ToString() -eq "Exe" })
  $al = if ($col.Count -gt 0) { [string]$col[0].EnforcementMode } else { "none" }
} catch { $al = "error:$($_.Exception.Message)" }
Add-Result "AppLocker Exe mode" "Enabled" $al $(if ($al -eq "Enabled") { "Pass" } else { "Fail" })

# Deny canary
$canary = "C:\AlphaAI-DLP-DenyCanary.exe"
$canaryDecision = "n/a"
try {
  $p = Get-AppLockerPolicy -Local -ErrorAction Stop
  $r = @(Test-AppLockerPolicy -PolicyObject $p -Path $canary -User Everyone -ErrorAction Stop)
  $canaryDecision = (@($r | ForEach-Object { [string]$_.PolicyDecision }) -join ",")
} catch { $canaryDecision = "probe_failed:$($_.Exception.Message)" }
$canaryPass = ($canaryDecision -notmatch "Allowed") -and ($canaryDecision -notmatch "probe_failed")
Add-Result "Deny canary not Allowed" "Denied/implicit" $canaryDecision $(if ($canaryPass) { "Pass" } else { "Fail" })

# Pipe GetEnforceStatus (best-effort via service if running)
$svc = Get-Service -Name "AlphaAITracker.Service" -ErrorAction SilentlyContinue
Add-Result "DLP service RUNNING" "Running" $(if ($svc) { $svc.Status } else { "absent" }) $(if ($svc -and $svc.Status -eq "Running") { "Pass" } else { "Fail" })

$outDir = Join-Path $env:TEMP "alpha-dlp-matrix-v2"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$csv = Join-Path $outDir ("matrix-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".csv")
$results | Export-Csv -NoTypeInformation -Path $csv
Write-Host ""
$results | Format-Table -AutoSize
Write-Host "Wrote $csv"
$fail = @($results | Where-Object { $_.Pass -eq "Fail" }).Count
exit $(if ($fail -gt 0) { 1 } else { 0 })
