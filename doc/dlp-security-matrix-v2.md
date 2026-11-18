# Strict Default-Deny Security Matrix V2

Automation-friendly checklist for Win10/11 Pro lab images. A signed org WDAC CIP and
Secure Boot are required before anyone calls the result production-complete.
Mark Pass / Fail / N/A. Client VERSION under test: read `client/VERSION`.

Status of a build that has not collected the lab rows below:
**2.9.1 implementation complete, evidence pending.**

## How to run (semi-automated)

```powershell
# From an elevated PowerShell on the lab PC after install + login:
pwsh -File client/publish/dlp-security-matrix-v2.ps1 -ServerUrl https://192.168.88.35
```

The script records observed outcomes. OS-level rows stay manual.

No provider CDN allow. No "block all Cloudflare" rule. Public sites and CDN IPs,
including Cloudflare, are denied because Windows Firewall DefaultOutbound=Block.
A CDN IP becomes reachable only if an approved domain's IP expansion adds it.
Shared-CDN residual risk: once one approved name expands to an address, other
names on that address can be reached too. App-aware WFP and Chromium URLAllowlist
are Phase 2 and do not close that gap.

## 1. Application control

| Case | Expected | Verify | Pass? |
|------|----------|--------|-------|
| Approved EXE (published path / hash) | Starts | Launch allowlisted binary | |
| Unauthorized EXE (Desktop portable) | Native AppLocker/WDAC block, not toast-after-kill | Double-click | |
| Renamed approved EXE | Deny unless FileHash matches | Copy+rename under Desktop | |
| Copied to Desktop | Deny (user-writable path rejected) | | |
| MSI installer (user) | Deny | msiexec Everyone blocked | |
| Portable ZIP exe | Deny | | |
| powershell / cmd (interactive) | Deny at exec | LocalSystem still allowed | |
| Store app not published | Deny (Appx) | | |
| DLL inject into explorer | Deny under signed WDAC CIP. Not claimed from AppLocker DLL rules | Residual if CIP missing | |
| Child of approved host | Follow WDAC/AppLocker | | |
| Explorer / Start / Search shell | Allowed | Bootstrap Appx list | |
| KB 5024351 present (or documented UBR floor / Win11 23H2+) | AppLocker interim possible on Pro via MDM, script, or local policy | `Get-HotFix` exact KB, or build/UBR floor in `AppControlCapability` | |
| KB 5024351 absent, build unknown, or build before 19041, and no CIP | Failed. Copy: no reliable app control. Do not claim exec-time block | | |
| GP on Pro, canary not Denied | Failed `applocker_gp_unsupported_on_pro`. Local/MDM apply still runs | | |
| Enterprise AppLocker-only | Interim. Residual kill-race until signed CIP | Document, do not call it complete | |

Group Policy deployment of AppLocker remains Enterprise/Server only. Pro is not
hard-coded as "AppLocker unsupported."

## 2. Network / DNS / CDN

| Case | Expected | Verify | Pass? |
|------|----------|--------|-------|
| google / youtube / facebook | Deny | Browser + curl | |
| Direct public IP `1.1.1.1:443` | Deny. This is a public-IP probe, not a Cloudflare allow or block | TCP connect fails | |
| Direct IP HTTPS | Deny | | |
| 8.8.8.8:53 | Deny unless that IP is `ALPHA_DLP_PROTECTIVE_DNS` | | |
| DoH / DoT | Deny except protective DNS TCP 853. Chromium `DnsOverHttpsMode=off` and `QuicAllowed=0` | No URLAllowlist | |
| QUIC rule present | Outbound UDP 443 Block, name `AlphaAI-DLP-LOCK-BLOCK-QUIC`, display name `Block QUIC` | ActiveStore | |
| QUIC rule missing | Network layer Failed `quic_rule_missing`. Default Block alone is not enough | | |
| Product UDP 443 allow present (`AlphaAI-DLP-LOCK-*` / DEST-UDP) | Failed `udp_443_allow_present` | Store/shell UDP Any allows are ignored | |
| IPv6 public | Deny | NDP allow only | |
| VPN / tunnel | Deny unless an approved app and destination are both explicit | Document the admin exception | |
| Unauthorized LAN RFC1918 | Deny (no whole-LAN allow) | | |
| `https://192.168.88.35` mgmt | Allow the URL's TCP port only (443, or 80 only when the URL is http, or an explicit port) | TCP connect succeeds | |
| Management DNS failure, last saved IP connects | Allow | TCP to that IP succeeds | |
| Management DNS failure, last saved IP fails or is missing | Failed `egress_probe_mgmt_last_ip_failed` | DNS failure alone is not success | |
| Approved browser + approved host only | Allow that host on TCP 443 | Bing pack is not broad microsoft.com | |
| Empty web allow | No public site, DNS, or CDN except management, protective DNS, and the documented boot carve-outs | | |
| ActiveStore Enabled + Block | Domain, Private, and Public all Enabled and DefaultOutboundAction=Block | `Get-NetFirewallProfile -PolicyStore ActiveStore` | |
| ActiveStore disabled or missing | Failed `active_store_profile_disabled` or `active_store_profile_missing` | | |
| ActiveStore disagrees | Local/persistent Block and ActiveStore Allow → Failed `active store disagreed` | | |

## 3. Failure / gate

| Case | Expected | Verify | Pass? |
|------|----------|--------|-------|
| Offline server | Deny stays; LKG | Unplug API | |
| Kill service | FW/AppLocker persist. No Clear on crash or pipe down | `sc stop` | |
| Restart service | Applying / Verifying, then Enforcing or Failed | | |
| Reboot | No open window; Applying then Enforcing or Failed | | |
| Corrupt policy pull | Keep LKG deny | | |
| Disable MpsSvc | Gate Failed (network) | | |
| AppLocker canary probe throw | NOT Enforcing unless org CIP fingerprint and Secure Boot both verify | | |
| `pipe_timeout` | Stays Applying/Failed — never Enforcing | Starve pipe during Apply | |
| Phase `verifying` | Screen stays. Not success | | |
| Phase `recovery_required` | Screen stays. Clear/pause/`lockdown:false` without recovery.auth, or CIP present with Secure Boot off/unknown | | |
| HTTPS URL not rewritten | `service.env` keeps `https://` | | |
| Pro without CIP | Gate not Enforcing. `wdac_cip_required_pro` | | |
| Secure Boot off or query throws, CIP present | `wdac_secure_boot_off`, phase `recovery_required`. Do not claim signed-policy tamper resistance | | |
| CIP without `recommended-blocks.merged` | Fingerprint can still match. Detail `wdac_block_rules=unverified`. Do not claim LOLBin coverage from AppLocker | | |
| recovery.auth.json | Single-use consume | Second Clear blocked | |

## 4. Residual risks (document, do not claim fixed)

| Risk | Stance |
|------|--------|
| Win10/11 Pro without a signed CIP and Secure Boot | AppLocker best-effort + killer when the capability probe says enforcement is possible. No reliable app control when the probe is unknown or the build is too old. Not "all unauthorized apps blocked at exec." |
| Secure Boot off | Signed CIP tamper resistance is not active |
| AppLocker Group Policy on Pro | Unsupported. MDM, script, or local policy can apply. GP that is not actually denying fails `applocker_gp_unsupported_on_pro` |
| Enterprise AppLocker-only | Interim. Kill-race remains until signed WDAC |
| Microsoft recommended block rules not merged and re-signed | `wdac_block_rules=unverified`. The service does not deploy an unsigned policy |
| Shared CDN IP after an approved domain expands to that address | Other names on the same IP may be reachable. No provider-wide allow and no provider-wide block. Chromium URLAllowlist is Phase 2 |
| Local administrator | Can clear firewall and AppLocker. ProductionFailClosed only raises the bar for product Clear/pause |
| App-aware WFP | Phase 2. `ALPHA_DLP_WFP_APP_AWARE` stays parked |
| Not claimed | Browser-extension blocking. Immunity to a local admin. Complete default-deny on Pro without signed CIP and Secure Boot |

## 5. Evidence still required before "complete"

Collect on Win10 Pro and Win11 Pro with the CIP present and Secure Boot enabled:

- Empty web allowlist denies public sites.
- Unauthorized EXE is blocked by WDAC or AppLocker at launch, not only a toast after kill.
- Failure rows: `pipe_timeout`, ActiveStore override, ActiveStore disabled, KB absent, GP-on-Pro, Secure Boot off, QUIC rule missing, UDP 443 allow present, management last-IP failure.
- `service.env` still has `https://`.

Until that evidence is attached, report **2.9.1 implementation complete, evidence pending.**

## Sign-off

| Role | Name | Date | Result |
|------|------|------|--------|
| Lab | | | |
| Security | | | |
| Release | | | |
