#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Download Microsoft's recommended user-mode block rules, verify the pin, and merge
  them into an org App Control base policy. Does not sign. Does not deploy.

.DESCRIPTION
  Source page (documented Microsoft policy XML, version 10.2.0.0):
  https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/design/applications-that-can-bypass-appcontrol

  The script downloads that page, extracts the SiPolicy XML, and checks VersionEx plus
  SHA256 before merge. Download success alone is not enough. The two "Allow all" rules
  (ID_ALLOW_A_1 / ID_ALLOW_A_2) are removed before merge so they cannot turn an
  allowlist base policy into allow-everything. Rule options 9 (Advanced Boot Options
  Menu) and 10 (Boot Audit on Failure) are set on the merged XML. Signing stays on
  the organization certificate.

  recommended-blocks.merged is written only after merge and option setting succeed.
  Copy that stamp next to the signed base-policy.cip:
  %ProgramData%\AlphaAITracker\dlp\wdac\recommended-blocks.merged
  Without the stamp the gate still accepts the CIP fingerprint but reports
  wdac_block_rules=unverified and does not claim LOLBin coverage from AppLocker.

.PARAMETER BasePolicyXml
  Existing org base policy XML (explicit allowlist).

.PARAMETER OutXml
  Merged unsigned policy to sign offline.

.PARAMETER StampPath
  Written only after a verified merge. Default: recommended-blocks.merged beside OutXml.
#>
param(
  [Parameter(Mandatory = $true)]
  [string]$BasePolicyXml,
  [Parameter(Mandatory = $true)]
  [string]$OutXml,
  [string]$StampPath = ""
)

$ErrorActionPreference = "Stop"

# Pinned 2026-11-18 from the Microsoft Learn page above. Update all three together.
$PinnedUrl = "https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/design/applications-that-can-bypass-appcontrol"
$PinnedVersion = "10.2.0.0"
$PinnedSha256 = "dba97d04ae6c2d7adbd802a7422ea4ec1c3a403e99b4f37d622b951dd71d167d"
$RequiredDenyIds = @("ID_DENY_MSHTA", "ID_DENY_CBD", "ID_DENY_CSI", "ID_DENY_DNX", "ID_DENY_BGINFO")

function Fail([string]$Message) {
  Write-Error $Message
  exit 1
}

if (-not (Test-Path -LiteralPath $BasePolicyXml)) {
  Fail "Base policy not found: $BasePolicyXml"
}

if ([string]::IsNullOrWhiteSpace($StampPath)) {
  $StampPath = Join-Path (Split-Path -Parent $OutXml) "recommended-blocks.merged"
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("wdac-block-rules-" + [guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory -Path $work | Out-Null
$stampWritten = $false
try {
  $htmlPath = Join-Path $work "page.html"
  try {
    Invoke-WebRequest -Uri $PinnedUrl -UseBasicParsing -TimeoutSec 90 -OutFile $htmlPath
  } catch {
    Fail "Download failed ($PinnedUrl): $($_.Exception.Message)"
  }

  $html = [System.IO.File]::ReadAllText($htmlPath)
  $match = [regex]::Match($html, '(?s)<pre><code class="lang-xml">(.*?)</code></pre>')
  if (-not $match.Success) {
    Fail "Microsoft page did not contain the pinned SiPolicy code block. Refusing to invent rules."
  }

  $xmlText = [System.Net.WebUtility]::HtmlDecode($match.Groups[1].Value).Trim()
  $utf8 = New-Object System.Text.UTF8Encoding $false
  $hashBytes = [System.Security.Cryptography.SHA256]::Create().ComputeHash($utf8.GetBytes($xmlText))
  $hash = ([System.BitConverter]::ToString($hashBytes)).Replace("-", "").ToLowerInvariant()
  if ($hash -ne $PinnedSha256) {
    Fail "SHA256 mismatch. Pinned $PinnedSha256 actual $hash. Refusing to merge."
  }

  $downloaded = Join-Path $work "RecommendedUserModeBlockList.xml"
  [System.IO.File]::WriteAllText($downloaded, $xmlText, $utf8)
  [xml]$policy = Get-Content -LiteralPath $downloaded
  $versionNode = $policy.SelectSingleNode("//*[local-name()='VersionEx']")
  if ($null -eq $versionNode -or $versionNode.InnerText.Trim() -ne $PinnedVersion) {
    Fail "Version pin failed. Expected $PinnedVersion."
  }
  foreach ($id in $RequiredDenyIds) {
    $node = $policy.SelectSingleNode("//*[local-name()='Deny' and @ID='$id']")
    if ($null -eq $node) {
      Fail "Pinned policy is missing $id. Refusing to merge."
    }
  }

  # Microsoft: remove Allow-all rules before merging into an explicit allowlist.
  $allowIds = @("ID_ALLOW_A_1", "ID_ALLOW_A_2")
  foreach ($id in $allowIds) {
    $nodes = @($policy.SelectNodes("//*[(@ID='$id' or @RuleID='$id')]"))
    foreach ($node in $nodes) {
      if ($null -ne $node.ParentNode) {
        [void]$node.ParentNode.RemoveChild($node)
      }
    }
  }
  $stripped = Join-Path $work "blocklist-no-allow-all.xml"
  $policy.Save($stripped)

  if (-not (Get-Command Merge-CIPolicy -ErrorAction SilentlyContinue)) {
    Fail "Merge-CIPolicy is not available. Install the ConfigCI module on a Windows build that has it. No stamp written."
  }

  $outDir = Split-Path -Parent $OutXml
  if (-not [string]::IsNullOrWhiteSpace($outDir)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
  }
  if (Test-Path -LiteralPath $OutXml) {
    Remove-Item -LiteralPath $OutXml -Force
  }

  Merge-CIPolicy -PolicyPaths $BasePolicyXml, $stripped -OutputFilePath $OutXml
  if (-not (Test-Path -LiteralPath $OutXml)) {
    Fail "Merge-CIPolicy did not write $OutXml"
  }

  foreach ($option in @(9, 10)) {
    Set-RuleOption -FilePath $OutXml -Option $option
  }

  $stamp = @(
    "version=$PinnedVersion"
    "url=$PinnedUrl"
    "sha256=$PinnedSha256"
    "options=9,10"
    "merged=$([DateTimeOffset]::UtcNow.ToString('o'))"
  ) -join "`n"
  $stampDir = Split-Path -Parent $StampPath
  if (-not [string]::IsNullOrWhiteSpace($stampDir)) {
    New-Item -ItemType Directory -Force -Path $stampDir | Out-Null
  }
  [System.IO.File]::WriteAllText($StampPath, $stamp + "`n", $utf8)
  $stampWritten = $true
  Write-Output "Merged $OutXml"
  Write-Output "Stamp $StampPath"
  Write-Output "Sign $OutXml with the organization certificate (RSA 2048/3072/4096, SHA-256/384/512). Do not deploy this unsigned file."
}
finally {
  if (-not $stampWritten -and (Test-Path -LiteralPath $StampPath)) {
    Remove-Item -LiteralPath $StampPath -Force -ErrorAction SilentlyContinue
  }
  if (Test-Path -LiteralPath $work) {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
  }
}
