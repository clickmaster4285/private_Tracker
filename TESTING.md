# Testing — live playbooks

Procedures that need a running server (and for DLP, an installed Windows agent). Unit/CI tests live next to the code; this file is for **lab proof**.

Companion: [plan.md](./plan.md) (always-on + Windows lockdown), [doc/plan.md](./doc/plan.md) (releases bridge), [WORKFLOW.md](./WORKFLOW.md), [client/build.md](./client/build.md). Historical Fixed/Roaming notes remain in [newplan.md](./newplan.md) only as archive.

*Last audited: 2026-11-05*

---

## H. Windows network lockdown (P1) + Allowlists

**Prereqs:** migrations **059** + **060**; Windows PC with next installer (service half); DeviceAuth credentials; web admin with `allowlists/web` (and `web-blocker` for deny rules).

1. Fresh install + login, **no Publish**: browser cannot open a public site; tracker sync / policy / presence still work (bootstrap). Service log: `Lockdown ENFORCED`.
2. Default-deny **Off** + Publish → lab PC opens general web (`AlphaAI-DLP-LOCK-*` cleared).
3. Default-deny **On** again + Publish (or reinstall) → blocked again.
4. Add one allow host → Save → Publish → only that host loads (plus platformAllow / API).
5. Add an explicit Web Blocker **block** rule for that same host → Publish → host stays blocked (deny-list still applies).
6. Employees → **Pause lockdown…** (30–240 min) → general web works until deadline; then relocks without waiting for a new publish.
7. Protected uninstall / teardown → no leftover `AlphaAI-DLP-LOCK-*` (`netsh advfirewall firewall show rule name=all` filtered).
8. Flap: publish add/remove 20 allow hosts → no orphan LOCK rules (same bar as §F.2).
9. Role without `allowlists/web` does not see the sidebar entry; `/allowlists/web` → `/unauthorized`.
10. App Allow List: add a publisher/SHA draft → Publish → pulled bundle has `allow.apps` (enforce still P3).

**P2 Chrome/Edge address bar (same publish):** under lockdown, Chrome and Edge get
`HKLM\...\URLBlocklist` = `*` plus deny patterns, and `URLAllowlist` for
platformAllow + allow.hosts + API host (explicit Web Blocker denies are subtracted
from the allow set so block still wins). Lockdown off / teardown clears `URLAllowlist`
the same way `URLBlocklist` clears (§F.1). Firefox and other browsers have **no**
enterprise allow API here — they stay blocked by P1 network deny only; do not claim
an address-bar interstitial for them.

Honest limit: P3 AppLocker/WDAC is not built. AdvFirewall uses a complement deny
(block wins over allow on Windows), not a literal allow-after-deny stack.

---

## G. Tracking mode removed (always-on)

Fixed/Roaming Start/Stop is deleted (migration **058**). There is no mode API, no Start/Stop modal, and no `control.json` control plane. Collectors run for the whole login.

1. `GET /api/v1/devices/control`, `POST /api/v1/devices/tracking-events`, `PATCH /employees/:id/machines/:machineId/tracking-mode`, and `/device-configuration/*` → **404**.
2. Sync JSON has **no** `controlEpoch`.
3. Web: no Device Configuration page, no "Set tracking mode", no mode badge on Employees / Device Specs.
4. Windows installed build (next installer): no Start/Stop window; End Task on `client.exe` → watchdog relaunches `--background` and collection resumes.
5. `--remove-account` still drains → self-remove → wipe. Product uninstall still needs **Issue Removal Code** + `--protected-uninstall --code`.
6. Pre-P0 installer against a post-058 server: control poll 404 is expected; the tracker must keep collecting.

---

## Public `/releases` + private GitHub download bridge

**Prereqs:** API with `GITHUB_OWNER` / `GITHUB_REPO` / `GITHUB_TOKEN` (Contents: Read) set; web pointed at that API (`NEXT_PUBLIC_API_URL`). Repo may be private.

1. Open `/releases` in a **logged-out** / private window → versions list (newest first); drafts absent; prereleases labeled; latest stable marked.
2. Expand release notes → Markdown renders without scripts; empty body shows fallback.
3. Click Windows / Linux download → browser native download of the real installer filename (not a GitHub login page).
4. API checks:
   ```bash
   curl -sS "$API/api/v1/releases" | head
   ASSET_ID=…   # from JSON assets[].id
   curl -I "$API/api/v1/downloads/$ASSET_ID"
   curl -v -H "Range: bytes=0-1023" -o range-test.bin "$API/api/v1/downloads/$ASSET_ID"
   ```
   Expect `200`/`206`, `Content-Disposition` with a safe filename, no `Authorization` / GitHub token in response headers. Confirm `range-test.bin` is 1024 bytes when upstream supports Range.
5. Negative: unknown `assetId` → 404; empty `GITHUB_TOKEN` → 503 on these routes only; protected admin routes still require JWT.

---

## Security & DLP (Windows lab — P0+P1+P2 domain)

**Prereqs:** server with migrations 051/052 applied; web admin with `dlp-rules` / `dlp-alerts` / `web-blocker` grants; Windows PC with tracker + DLP service installed (Inno or `client/DlpService/install-service.ps1` elevated).

### A. Policy publish → service pull

1. Web `/dlp-rules` → create a rule: trigger `usb_storage`, action `block` (or `alert_block`), enabled.
2. Click **Publish** → note `policyVersion` in the response toast / network tab.
3. On the PC (as SYSTEM service): wait ≤ `ALPHA_DLP_POLICY_POLL_SEC` (installer lab often uses 60s), or restart the Windows service.
4. Confirm `%ProgramData%\<APP_MUTEX>\dlp\` has a policy blob / store row with that version (`GetPolicyVersion` via pipe or service logs).

### B. USBSTOR coarse block + alert

1. With a published `usb_storage` **block** rule and `ALPHA_DLP_USB_BLOCK=true`, plug a USB mass-storage stick.
2. Expect: Removable volumes unavailable / USBSTOR disabled (coarse — whole class, not per-file).
3. Web `/dlp-alerts` within ≤ `ALPHA_DLP_ALERT_SYNC_SEC`: new row (`actionTaken` `blocked` or `alerted`), `policyVersion` matches.
4. Open the row → detail dialog → change status/notes → **Save** → History shows `dlp_alert_events` entries. Status must **not** update on dropdown change alone.

### C. Protected uninstall (removal code)

1. Employees → device menu → **Issue Removal Code** (machine scoped). Copy the raw code (shown once).
   Modal shows **Linux** + **Windows** copy-paste commands (`lib/client-commands.ts`).
2. On the PC:
   ```powershell
   # Windows
   & "C:\Program Files (x86)\Alpha AI Tracker\client.exe" --protected-uninstall --code "<paste>" --no-pause
   ```
   ```bash
   # Linux
   alpha-ai-tracker --protected-uninstall --code "<paste>"
   ```
3. **Windows expect:** service unlocks ACL → stops → drain → `self-remove` → wipe → `%ProgramData%\<APP_MUTEX>\dlp\teardown.ok`.
4. **Linux expect:** HTTP `removal-verify` (token from `~/.local/share/alpha-ai-tracker/data/*.db`) → drain → wipe → LocalAppData / `/var/lib/<pkg>/dlp` `teardown.ok`; then `sudo apt remove` / `dpkg -r` allowed.
5. Apps & features / `.deb` remove succeeds with gate. Replay the same code → verify fails. Without `teardown.ok`, Inno / `prerm` refuses.
6. Windows before unlock: Services.msc **Stop** on the DLP service fails for Administrators (query-only DACL).

### C.1 Plain `--remove-account` (local wipe — not product uninstall)

1. While logged in (Fixed or Roaming):
   ```bash
   alpha-ai-tracker --remove-account
   # Windows: & "…\client.exe" --remove-account --no-pause
   ```
2. Expect: sync unsynced rows if online (bounded); wipe local DB either way; tracker relaunches signed-out.
3. Does **not** write `teardown.ok` / does **not** unlock DLP — use §C for full uninstall.

### D. Master switch off

1. Set `ALPHA_DLP_ENABLED=false` in `service.env` (and/or bake `config.enc` for the helper), restart service.
2. Expect: workers parked; tracker sync / live stream / self-remove without DLP behave as pre-DLP; no USBSTOR enforcement from this product.

### E. RBAC

1. Role without `dlp-*` / `web-blocker` / `shadow-it` → sidebar entries hidden; direct URL → `/unauthorized`.
2. `company_admin` → Security & DLP pages (Alerts / Rules / Web Blocker / Shadow IT) reachable.

### F. Web Blocker (hosts + firewall + DNS pin — P2)

1. Web `/web-blocker` → Add rule: `youtube.com` with **Include related domains** (chips, not commas), action **Block** / **Alert + Block**, Status Active.
2. Save auto-publishes → wait for policy pull (~30–60s). Active/Inactive also auto-publishes. **Delete without Publish leaves the last published bundle on the client** until a new version is published.
3. Expect hosts `# AlphaAI-DLP-BEGIN` with `127.0.0.1` / `::1` per domain; `netsh` rules `AlphaAI-DLP-*` (apex) and soon `AlphaAI-DLP-PIN-*` (live CDN IPs). Chrome/Edge may show the org “This page is blocked” interstitial from `URLBlocklist` (not a localhost page — `:47832` branded page was removed).
4. Apply path: browser policies + firewall + TCP-kill + hosts + flushdns only — **never** winsock / `ipconfig /release` / NIC reset.
5. **Mid-session lab (no employee restart):** start YouTube playback → Publish/Activate → keep the same tab playing. `DnsBlockPinWorker` should see `rr*---sn-*.googlevideo.com` in DNS cache, pin those IPs, TCP-kill; playback should stall within a few seconds to ~1–2 minutes without closing Chrome or rebooting. Second browser should fail on navigate.
6. Confirm: `netsh advfirewall firewall show rule name=all | findstr AlphaAI-DLP-PIN` shows pinned rules after video DNS runs; service log / `enforcement_audit` may show `web_dns_pin`.
7. Active→Inactive clears hosts + owned FW + pins + URLBlocklist; Active again re-applies.
8. Teardown / clear policy removes owned hosts block + all `AlphaAI-DLP-*` rules + browser policies.
9. Edit a rule → History dialog shows prior revisions (migration 054).

#### F.1 Clear-path after rule delete (stale-block regression)

After **Delete rule + Publish** (or Active→Inactive) wait ~30–60s for policy pull, then verify **all** of:

1. `%ProgramData%\AlphaAITracker\dlp\dlp.db` — no active web domains for that rule / empty domain set in applied policy.
2. `C:\Windows\System32\drivers\etc\hosts` — no `# AlphaAI-DLP-BEGIN` / `# AlphaAI-DLP` block.
3. `netsh advfirewall firewall show rule name=all | findstr AlphaAI-DLP` — empty (no apex or `PIN-` rules).
4. Registry — no `HKLM\SOFTWARE\Policies\Google\Chrome\URLBlocklist` (and Edge twin `...\Microsoft\Edge\URLBlocklist`).
5. Chrome: open a **fresh** tab to the formerly blocked site — must **not** show “Your organization doesn't allow…”. Empty `dlp.db` alone is not enough; leftovers mean EnforceWorker must run `Apply([])` / self-heal (`web_domain_clear` in audit). Chrome may take a moment to drop enterprise policy after registry clear.
6. **PIN leftovers:** YouTube often stays denied via `AlphaAI-DLP-PIN-*` (CDN IPs) even when `youtube` is gone from hosts. `findstr AlphaAI-DLP` must show **no** `PIN-` rules after clear. Pins are **not** in `dlp.db` — clear is by firewall name prefix under a generation lock so the pin worker cannot recreate them after Inactive.

#### F.1b Schedule + timezone

1. Create/edit a web-blocker rule → enable **Timer-based restriction** → pick days/start/end.
2. Timezone field is a searchable dropdown (`TimezoneCombobox`); select **Asia/Karachi** (or the employee locale), not free-text / not UTC unless intended.
3. Publish → wait for pull. Inside the window (in that IANA zone) domains block; outside the window they clear (hosts/FW/PIN). Wrong TZ or silent UTC fallback on the service makes the window look “broken” in local wall-clock.

#### F.2 High-churn lab (100-site production path)

1. Publish ~20+ domains including `youtube.com` (+ related) and several others.
2. Within a few minutes: Inactive half → Publish; Delete some → Publish; fix a typo domain → Publish; Active again.
3. After each pull (~30–60s): hosts AlphaAI block and `netsh … findstr AlphaAI-DLP` must match **exactly** the active set (no orphan apex; no `PIN-*` when empty; no youtube/PIN when youtube removed).
4. Confirm YouTube loads again after youtube removed **without** manual `netsh delete` (`ERR_NETWORK_ACCESS_DENIED` gone in Chrome / Edge / Cursor Browser Tab).
5. Audit may show `web_domain_reconcile` / `web_dns_pin_clear` when OS drifted and self-healed.

---

## Orphan `app-items` FK 23503 (server preflight)

Historical: items batch arriving before parent `app_sessions` used to 500 the whole batch. Server now preflights and returns 200 with a reduced `accepted` count; client keeps refused rows `is_synced=0` when `rejectedIds` is present.

**Steps (abbrev):**

1. With a DeviceAuth token, POST an `app-items/sync` batch whose `appSessionId` does not exist → 200, survivors only / rejected ids.
2. POST the parent `app-sessions/sync`, then re-POST the items → accepted.
3. Confirm no silent `is_synced=1` on refused rows (client ≥ 2026-09-09).

---

## App Usage aggregate (`GET /app-sessions/usage`)

See server smoke via curl: one row per `(appDisplayName, processName)`; web Duration = `lastClosedAt - firstOpenedAt`, not Σ row durations. Chevron expand uses `GET /app-sessions/usage/sessions`.
