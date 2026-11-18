# Windows DLP Hardening — Lab Matrix

Run before releasing a production DLP installer. Mark each cell Pass / Fail / N/A.

## Builds under test

| Item | Value |
|------|-------|
| Client VERSION | (fill) |
| Installer | `AlphaAITracker-Setup-<ver>.exe` |
| API | migration **076** applied |
| Lab images | Win10 Pro LTSC/ESU + Win11 Pro |

## 1. Install / upgrade

| Case | Win10 | Win11 | Notes |
|------|-------|-------|-------|
| Clean install → service LocalSystem RUNNING under Program Files | | | Abort if Service payload missing |
| Legacy x86 (`Program Files (x86)` 2.4.0) upgrade → x64 path, no open window | | | FW/hosts deny preserved during stop |
| `ALPHA_SERVER_URL` keeps `https://` (nginx) in service.env — never rewritten to HTTP | | | |
| Reboot after install → blockers still enforcing | | | |
| Failed SCM start aborts setup visibly | | | |

## 2. Policy contract

| Case | Win10 | Win11 | Notes |
|------|-------|-------|-------|
| No-policy bootstrap (v0) keeps total outbound deny | | | |
| Malformed / hash-mismatch pull keeps last-known-good | | | `policy_reject` audit |
| Offline / 401 pull keeps last-known-good | | | |
| Allowlist host/app create auto-publishes complete hashed bundle | | | No second Publish click required |
| Device ack downloaded → installed → verified | | | `dlp_policy_device_ack` |
| Rollback to prior LKG snapshot | | | |

## 3. Network total outbound

| Case | Win10 | Win11 | Notes |
|------|-------|-------|-------|
| Default outbound Block verified on all profiles | | | |
| Direct public HTTP/HTTPS fails | | | |
| Public DNS / DoH / DoT / QUIC bypass fails | | | |
| VPN / alternate-port egress fails | | | |
| Protective DNS / SWG path works when configured | | | `ALPHA_DLP_PROTECTIVE_DNS` |
| API bootstrap + published allow.hosts work | | | |
| DHCP / NDP / NTP still work | | | |
| Service stop / API down / DNS down → still denied | | | production profile |

## 4. App control

| Case | Win10 | Win11 | Notes |
|------|-------|-------|-------|
| Denied EXE never reaches entry (native Windows UI) | | | |
| Denied MSI / Appx / script blocked | | | |
| User-writable path allow rejected | | | |
| Managed launcher opens only catalog entries | | | `client.exe --managed-launch` |
| Direct / CLI / scheduled denied launch → native denial | | | |
| WDAC CIP present + Secure Boot → enforcing | | | else `wdac_unsigned_pending` |
| AppLocker Task Manager not Allowed | | | behavioral verify |

## 5. Recovery

| Case | Win10 | Win11 | Notes |
|------|-------|-------|-------|
| Production Clear blocked without recovery.auth.json | | | |
| Signed expiring recovery opens + audits | | | |
| Service crash → deny remains | | | |
| OS update / reboot → enforce returns | | | |

## Sign-off

| Role | Name | Date | Result |
|------|------|------|--------|
| Lab | | | |
| Security | | | |
| Release | | | |

Only rebuild/ship the Windows installer after this matrix passes.
