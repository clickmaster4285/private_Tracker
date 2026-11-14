# Release Notes — v2.3.25

**Branch:** `bugFixes_timeAttendance` · **Client:** 2.3.25

## What’s new

### Attendance & timesheets stay accurate while people work
- **Last Seen** and **Active time** on Attendance and Timesheets no longer freeze after a reboot or long stretch of work with no lock/unlock.
- If the tracker is still online (Live Stream / App Usage show activity), those pages keep updating through the day.
- Fixes the case where an employee looked “done at 09:17” while they were clearly still working at midday.

---

# Release Notes — v2.3.24

**Branch:** `feature_property_based_tracker_PBT` + `linuxaddition` · **Client:** 2.3.24

## What’s new

### Fixed vs Roaming tracking (Windows + admin)
- HR can set each machine to **Fixed** (always on) or **Roaming** (employee controls Start / Stop).
- Roaming employees get a **Tracker control** window: Start / Stop, day working timer, and hide-to-tray (close or minimize keeps it running in the tray).
- Stop pauses tracking for that session; DLP protection can stay on.
- Offline Start / Stop still works; events sync when the network returns.
- Admin **Device Configuration**: roaming PC list (grouped by employee), tracker mode types, and Start / Stop logs.
- Employee Device Specs: clearer machine picker and a simple Fixed / Roaming mode dialog.

### Linux parity
- **Auto-restart** if the tracker is killed — comes back in a few seconds.
- **Remove account** and **protected uninstall** (with removal code) work on Linux the same way as Windows.
- **DLP** on Linux: USB / removable-drive blocking, web site blocking, and DLP alerts (needs admin install).
- **Roaming Tracker control** on Linux: auto-opens, can relaunch if closed, and hide-to-tray matches Windows.

---

# Release Notes — v2.2.0

**Branch:** `feature_dlp` · **Client:** 2.2.0

## What’s new

### Silent auto-update (Windows)
- After the first admin install, updates install **quietly in the background** — no repeated UAC prompts for normal upgrades.
- New versions download while the employee keeps working; the tracker applies the update without interrupting their day.
- Updates come through your Alpha server (private GitHub stays private; employees don’t need GitHub access).

### DLP reliability & storage
- Unsynced DLP alerts are **kept until the server accepts them** — nothing important is dropped just because disk got large or days passed.
- Local logs and DLP storage are cleaned more carefully so machines don’t fill up with old noise while still preserving real evidence.

### Tracker / UI stability
- The desktop window runs as a separate helper from the background tracker, so opening or closing the UI doesn’t fight the always-on service.

---

# Release Notes — v2.1.11

## Overview

v2.1.11 is the **P2 Web Blocker** release on top of **v2.0.4** (Security & DLP spine + USB +
protected uninstall). Admins author domain / schedule rules on **`/web-blocker`**; the
Windows **LocalSystem** service enforces them mid-session via hosts DNS sink, outbound
firewall, DNS-cache IP pinning, TCP-kill, and Chrome/Edge defense-in-depth — **without**
requiring a PC or browser restart. Stale blocks after Inactive/delete are self-healed;
PIN rules cannot race a clear. Web UX adds full DLP-rules CRUD/history, status Switches,
alert domain chips, and a searchable IANA timezone picker.

Branch: `feature_dlp` (line after `v2.0.4`). Client version: **2.1.11**. Installer:
`AlphaAITracker-Setup-2.1.11.exe` (ships updated `AlphaAITracker.Service.exe`).

**Range `v2.0.4..HEAD`** (2026-10-23 → 2026-10-26): **~19 commits, ~59 files, +7.7k / −0.7k**,
migrations **053–055**. Plan phase **P2 largely complete**; remaining optional: true WFP
callout / per-connection block events, tracker path-pattern alerts (path hard-block = P7).
See `plan.md`.

Headline themes:

1. **Web Blocker enforce** — hosts + `AlphaAI-DLP-*` firewall + `DnsBlockPinWorker` + TCP-kill.
2. **Mid-session cut** — YouTube/CDN style sites die without employee restart.
3. **Honest clear / reconcile** — no leftover hosts/FW/PIN/URLBlocklist after Inactive/delete.
4. **Web authoring UX** — `/web-blocker` + `/dlp-rules` edit/history/Switch; `/dlp-alerts` domains.
5. **Timezone safety** — `TimezoneCombobox` so schedule zones are real IANA (e.g. Asia/Karachi).
6. **Public `/releases` while GitHub stays private** — server download bridge + public releases page (2026-10-26).

---

## 1. Server

### Migrations

| Migration | What it does |
| --------- | ------------ |
| **053** `dlp_relational` | Relational DLP / web-blocker child tables (domains, paths, schedule days, extensions, patterns) — no TEXT[] blobs on live rules |
| **054** `web_blocker_revisions` | Snapshot before each Web Blocker UPDATE + revision child tables |
| **055** `dlp_rule_revisions` | Snapshot before each classic DLP rule UPDATE + revision extensions/patterns |

### New / extended endpoints

| Method | Route | Auth | Purpose |
| ------ | ----- | ---- | ------- |
| GET/POST/PATCH/DELETE | `/api/v1/web-blocker/rules` | JWT | Web Blocker CRUD (`web_navigate`) |
| GET | `/api/v1/web-blocker/rules/:id` | JWT | Single rule detail |
| GET | `/api/v1/web-blocker/rules/:id/history` | JWT | Pre-update revision snapshots |
| POST | `/api/v1/web-blocker/domains/expand` | JWT | www/m + curated related-domain packs |
| GET | `/api/v1/dlp/rules/:id` | JWT | Classic DLP rule detail |
| GET | `/api/v1/dlp/rules/:id/history` | JWT | Classic DLP revision snapshots |
| PATCH | `/api/v1/dlp/rules/:id` | JWT | Update now snapshots revision + `changedBy` |
| GET | `/api/v1/releases` | **None** | Published GitHub release metadata + `latestStable` (drafts excluded; cached) |
| GET / HEAD | `/api/v1/downloads/:assetId` | **None** | Stream / probe validated Windows `.exe` / Linux `.deb` (private-repo bridge) |

`POST /dlp/rules/publish` still compiles **enabled** classic rules **and** Web Blocker rules
(+ `shadowDeny`) into one immutable policy version. DeviceAuth `GET /dlp/policy` unchanged
in shape; bundle carries web domains / schedule for the service.

### Public releases bridge (private GitHub)

Env: `GITHUB_OWNER`, `GITHUB_REPO`, `GITHUB_TOKEN` (Contents: Read), `GITHUB_API_VERSION`,
`GITHUB_RELEASES_CACHE_TTL`. Token never reaches the browser. Assets validated against published
installer metadata only (no open proxy). Streaming + optional HTTP Range; redirect Authorization
stripped. See `doc/plan.md`. Empty token → `/releases` and `/downloads/*` return **503**.

### Fixes

- **EvidenceMap sync 400** — nested JSON in alert evidence coerced to string so
  `POST /dlp/alerts/sync` no longer rejects device batches.

---

## 2. Client (Windows DLP service)

### Web domain enforce (`DomainBlockEnforcer` + workers)

| Piece | Job |
| ----- | --- |
| Hosts DNS sink | `# AlphaAI-DLP-BEGIN/END` markers; schedule-aware domain set |
| Outbound firewall | Incremental `AlphaAI-DLP-*` on resolved apex/CDN IPs (`protocol=any`) |
| `TcpConnectionKiller` | `SetTcpEntry` DELETE_TCB so open HTTPS tabs die on apply |
| `DnsBlockPinWorker` | Poll DNS client cache → pin live CDN IPs → `AlphaAI-DLP-PIN-*` + TCP-kill (~1.5s) |
| Browser policies | Chrome/Edge DoH-off; optional URLBlocklist / QuicAllowed |
| EnforceWorker | Force hosts re-apply on new policy version; reconcile every tick |

**Removed:** localhost branded block page / HTTPS block cert stack
(`BlockPageServer`, `BlockHttpsServer`, `BlockPageCertStore`) and tracker
`WebBlockUxService` / `IWebBlockHitSink` / `ALPHA_DLP_BLOCK_PAGE*`. Chrome org interstitial
from URLBlocklist is enough; HTTPS never redirected to a local port.

### Clear / reconcile hardening (production)

Root cause of “`dlp.db` empty but Chrome still blocks” / “YouTube stays blocked after
Inactive while Facebook clears”:

1. Silent clear failures + fingerprint short-circuit left `URLBlocklist` / hosts / FW.
2. Orphan `AlphaAI-DLP-PIN-*` (CDN IPs, not in `dlp.db`) + pin worker racing clears.

**Fix:**

- `ClearBrowserPolicies` / clear paths no longer swallow delete failures.
- Enforce does **not** fingerprint-advance on `access_denied` / `browser_error` / incomplete clear.
- `HasLeftoverOsArtifacts` self-heals hosts / `AlphaAI-DLP-*` / URLBlocklist each tick.
- Apply bumps **`PolicyGeneration` under `PinGate`**; `ClearPinnedIps` verifies leftovers;
  `PinRemoteIps(ips, gen)` **no-ops on stale gen**; empty pin worker also clears PIN leftovers.
- Every Enforce tick: `OsStateMatchesDesired` → force Apply on drift (`web_domain_reconcile` audit).

Protected uninstall / teardown also clears hosts + FW/PIN + URLBlocklist when elevated.

### Config

Same `ALPHA_DLP_*` family as 2.0.4; **`ALPHA_DLP_WEB_BLOCK=true`** required for domain path.
Re-bake **`config.enc`** before shipping the installer (Installer-Parity). Drop any leftover
`ALPHA_DLP_BLOCK_PAGE*` from env templates.

---

## 3. Web

| Route | What shipped in 2.1.11 |
| ----- | ---------------------- |
| `/web-blocker` | Domain chips (all domains, no “+N more”), related-domain expand, edit + revision history, Active/Inactive **Switch** + auto-publish, schedule + **`TimezoneCombobox`** (IANA search) |
| `/dlp-rules` | Full create/edit/delete, detail dialog, revision history (055), Active/Inactive **Switch** + auto-publish; extensions/patterns fields removed from UI |
| `/dlp-alerts` | List shows blocked-domain chip from evidence; detail **Domains** as chips (not CSV); triage Save + history unchanged |
| `/releases` | **Public** (no login) — all published versions, sanitized release notes, Windows/Linux download buttons via API bridge |
| Shared | `TimezoneCombobox` also on `/shifts`; `evidenceDomains` / `evidenceDomainLabel` in `dlp-labels.ts`; `DownloadAppSection` uses `/api/v1/releases` + `/downloads/:id` (no direct GitHub) |

RBAC: `web-blocker` under `security-dlp` (already seeded). Publish still one button /
auto-publish after toggle so agents refresh within the policy poll window (~`ALPHA_DLP_POLICY_POLL_SEC`).

---

## 4. Not yet (after 2.1.11)

- Optional true **WFP callout** / per-connection block events (beyond netsh FW + DNS pin)
- Tracker **path-pattern** alerts (path hard-block still P7)
- Shadow IT Block → auto `shadowDeny` on next publish (P3)
- AppLocker / WDAC install hard-block (P4)
- USB device allowlist / minifilter file DLP (P5/P6)
- Linux/macOS enforcement (Windows-only)
- ~~Desktop **`AppUpdateService`** GitHub-direct~~ — **fixed 2026-10-26:** uses `/api/v1/releases` + `/downloads/:assetId` (ships in next installer)

### Known ops note

Outbound `AlphaAI-DLP-*` rules use **`protocol=any`** to blocked remotes. If a blocked
site’s resolved IP is shared with unrelated services (e.g. a VPS), **SSH/other ports to
that IP can also be denied** (`ssh: … Permission denied` at connect). Deactivate the rule
or clear PIN/FW leftovers; prefer not blocking IPs that host critical admin endpoints.

Lab: `TESTING.md` §F (Web Blocker + churn / Inactive reconcile). Docs: `AGENTS.md`,
`client`/`server`/`web` ARCHITECTURE.md, `plan.md`, `doc/plan.md` (releases bridge),
`WORKFLOW.md` §6 (private GitHub → public downloads).

### Verification (source)

- `go build` / `go vet` / `go test ./internal/services/ ./internal/handlers/`
- `dotnet build` (client + DlpService)
- `npx tsc --noEmit`
- Installed Windows lab: Inactive clears YouTube/PIN leftovers; mid-session CDN cut without restart

### Deploy sequence

1. **Server** — deploy API; apply migrations **053–055** (boot runner).
2. **Web** — deploy dashboard (Web Blocker / DLP Rules / Alerts UX).
3. **Client** — rebuild installer **2.1.11** with re-baked `config.enc`
   (`ALPHA_DLP_ENABLED` / `ALPHA_DLP_WEB_BLOCK` as intended), install so
   `AlphaAITracker.Service.exe` updates; Publish a policy so agents pull the new bundle.

---

# Release Notes — v2.0.4

## Overview

v2.0.4 is the **Security & DLP (Windows)** release: admin-authored policy on the web, a
**LocalSystem** Windows service that enforces coarse USB mass-storage blocks, a live DLP
Alerts inbox with triage history, Shadow IT catalog actions, and **protected uninstall**
gated by a one-time server removal code.

Branch: `feature_dlp`. Client version: **2.0.4**. Installer: `AlphaAITracker-Setup-2.0.4.exe`
(ships `AlphaAITracker.Service.exe` beside the user helper).

**Range after `enhance-livestreamV4` merge (`9355901..HEAD`)** (2026-10-19 → 2026-10-22):
**~19 commits, ~96 files, +11k / −1k**, migrations **051–052**. Plan phases **P0 + P1**
complete; **P2+** (WFP domains, Shadow IT → `shadowDeny`, AppLocker, minifilter) still open
— see `plan.md`.

Also on this line (pre-DLP commits on the same branch): Linux live-stream capture/VP8
(Wayland ScreenCast + GStreamer) and app-usage / hours-insights / graphical-logs
**duration inflation** fixes (started_at-in-range + wall-clock merge).

Headline themes:

1. **DLP policy spine** — editable rules → immutable published bundles → DeviceAuth pull.
2. **Windows DLP service** — USBSTOR enforce, alert queue/sync, ACL-locked service, watchdog.
3. **Protected removal** — Issue Removal Code → `--protected-uninstall --code` → `teardown.ok`.
4. **Web Security & DLP** — live `/dlp-rules`, `/dlp-alerts` (+ detail/history), `/shadow-it`.
5. **P2 Web Blocker (same line)** — `/web-blocker` (domains / paths / schedule) + Windows hosts DNS sink.

---

## 1. Server

### Migrations

| Migration | What it does |
| --------- | ------------ |
| **051** `dlp_security` | `dlp_rules`, `dlp_policy_versions` (immutable publish), `dlp_alerts`, `shadow_it_apps`, `removal_challenges` (SHA-256 of one-time codes) |
| **052** `dlp_alert_history` | `dlp_alert_events` triage audit (status / assignee / notes Save trail) |

### New endpoints

| Method | Route | Auth | Purpose |
| ------ | ----- | ---- | ------- |
| GET | `/api/v1/dlp/policy?sinceVersion=` | Device | Pull published bundle (unchanged when current) |
| POST | `/api/v1/dlp/alerts/sync` | Device | Batch upsert client alerts |
| POST | `/api/v1/dlp/health` | Device | Heartbeat (policy version / enforce OK) |
| POST | `/api/v1/devices/removal-verify` | Device | Validate removal code before teardown |
| GET/POST/PATCH/DELETE | `/api/v1/dlp/rules` | JWT | Admin rule CRUD |
| POST | `/api/v1/dlp/rules/publish` | JWT | Compile enabled rules (+ shadow deny list) → new policy version |
| GET | `/api/v1/dlp/alerts` | JWT | Infinite-scroll inbox + filters |
| GET | `/api/v1/dlp/alerts/:id/history` | JWT | Triage audit events |
| PATCH | `/api/v1/dlp/alerts/:id` | JWT | Status / assignee / notes (Save-only) |
| GET/PATCH | `/api/v1/shadow-it/apps` | JWT | Catalog + Approve / Block / Flag |
| POST | `/api/v1/devices/removal-code` | JWT | Issue one-time machine-scoped code (hash at rest) |

Env: `DLP_REMOVAL_CODE_TTL_MIN` (default **60**). RBAC: module `security-dlp` with
`dlp-rules` / `dlp-alerts` / `shadow-it`.

### Rules codified

- **DLP Policy Version Rule** — Publish is append-only; agents enforce last-good offline.
- **Protected Removal Rule** — raw code returned once; hash stored; replay/expiry fail.

---

## 2. Client (Windows DLP service + helper)

### New process: `AlphaAITracker.Service.exe` (`client/DlpService`)

Runs as **LocalSystem** Automatic service (not inside Avalonia). Workers:

| Worker | Job |
| ------ | --- |
| PolicyPullWorker | `GET /dlp/policy` on poll (`ALPHA_DLP_POLICY_POLL_SEC`) |
| EnforceWorker + UsbStorEnforcer | USBSTOR Start=4 when published `usb_*` block rules |
| RemovableDriveMonitor | Alerts when a removable volume appears while blocked |
| AlertSyncWorker | Drain local alerts → `POST /dlp/alerts/sync` |
| NamedPipeHost | Health, `AuthorizeTeardown`, policy version IPC |
| ServiceAclHardener | SY full / BA query-only stop lock |
| TrackerWatchdogWorker | Relaunch user helper if End Task'd (`ALPHA_DLP_WATCHDOG`) |

Store: `%ProgramData%\<APP_MUTEX>\dlp\` (separate from user SQLite). Credentials bridge via
`device.auth` written by the tracker after login.

### Protected uninstall

1. Web **Issue Removal Code** (employees menu).
2. PC: `client.exe --protected-uninstall --code <raw>` → pipe `AuthorizeTeardown` →
   `POST /devices/removal-verify` → unlock ACL → drain + `POST /devices/self-remove` →
   write `teardown.ok`.
3. Inno `InitializeUninstall` requires `teardown.ok` (no break-glass checkbox).
4. Installer **stops/deletes the service before file replace** (fixes Access Denied when
   locked DLLs held the previous service).

Service / pipe / mutex names come only from `APP_IDENTIFIERS`
(`WINDOWS_SERVICE_NAME`, `WINDOWS_DLP_PIPE_NAME`, `APP_MUTEX`).

### Config knobs (re-bake `config.enc`)

`ALPHA_DLP_ENABLED` (default false until ship), `ALPHA_DLP_POLICY_POLL_SEC`,
`ALPHA_DLP_ALERT_SYNC_SEC`, `ALPHA_DLP_USB_BLOCK`, `ALPHA_DLP_WEB_BLOCK`,
`ALPHA_DLP_MAX_LOCAL_ALERTS`, `ALPHA_DLP_DEDUP_SEC`, `ALPHA_DLP_WATCHDOG`,
`ALPHA_DLP_WATCHDOG_POLL_SEC`, optional `ALPHA_DLP_DATA_DIR` / `ALPHA_DLP_PIPE_NAME`.

---

## 3. Web

| Route | What shipped |
| ----- | ------------ |
| `/dlp-rules` | Live CRUD + **Publish** (USB / non-web triggers) |
| `/web-blocker` | Domains, URL paths, timer-based schedule (`web_navigate`) + Publish |
| `/dlp-alerts` | Infinite scroll + URL filters; detail dialog with **Save-only** triage + history |
| `/shadow-it` | Catalog + Approve / Block / Flag |
| Employees menu | **Issue Removal Code** (beside device lifecycle actions) |

Follows Web Infinite-Scroll + URL-Synced Filters rules. Alert status no longer PATCHes on
every dropdown change — only on Save. RBAC adds `web-blocker` under `security-dlp`.

### P2 domain enforce (client)

- `DomainBlockEnforcer` — system hosts DNS sink between `# AlphaAI-DLP-BEGIN/END` markers
- Schedule-aware apply from `match.schedule`; teardown clears the sink
- Apply-time `web_navigate` alerts; URL **paths** stored for later alert-match (not hard-blocked)

---

## 4. Not yet (later phases)

- True WFP callout / per-connection block events (beyond hosts sink)
- Tracker path-navigate alerts; TLS path hard-block (P7)
- Shadow IT Block → auto `shadowDeny` on next publish (P3)
- AppLocker / WDAC install hard-block (P4)
- USB device allowlist / minifilter file DLP (P5/P6)
- Linux/macOS enforcement (Windows-only)

Lab playbook: `TESTING.md` §DLP. Docs refreshed: `AGENTS.md`, `*/ARCHITECTURE.md`,
`WORKFLOW.md`, `FILE_HIERARCHY.md`, `plan.md`.

### Verification (source)

- `go build` / `go vet`
- `dotnet build` (client + DlpService)
- `npx tsc --noEmit`
- Installed Windows lab: policy pull + USBSTOR + removal-code uninstall (see TESTING.md)

---

# Release Notes — v1.2.36

## Overview

v1.2.36 is a **dashboard + device-lifecycle + live-stream reliability** release on top of **1.2.35**.
Branch: `enhance-livestreamV4` (merged to `main` as PR #28). Client version: **1.2.36**.
Installer: `AlphaAITracker-Setup-1.2.36.exe`.

**Range `v1.2.35..8f55701`** (2026-10-05 → 2026-10-19): **38 commits, 108 files changed,
+13,926 / −2,013**, migrations **042–050** (044 intentionally skipped — no such file).

Four headline themes:

1. **WebRTC ICE trickle race fixed** — the "**LIVE / `--- FPS` / zero frames**" black-tile bug is
   root-caused and buffered on all three tiers (server + web + client).
2. **Device lifecycle** — new `employee_machines` registry (one row per physical PC), machine-scoped
   data everywhere (`machine_id`), and a complete **Remove Current Device / `--remove-account`**
   wipe handshake that persists server-side first, then instructs the client to wipe
   (migrations 046–050).
3. **Dashboard + logs redesign** — single-request `GET /dashboard/summary` home, **Attendance card**
   replacing Fleet Health, and `/logs/graphical` + `/logs/insights` rebuilt on real APIs.
4. **Account hardening** — suspended web users can no longer sign in **or** keep an existing
   session alive; the Terms & Conditions RBAC nav leak is closed.

---

## 1. Server

### New endpoints

| Method | Route | Auth | Purpose |
| ------ | ----- | ---- | ------- |
| GET | `/api/v1/dashboard/summary` | JWT | One-shot home payload: employees/activity/monitoring/devices/live counts + bounded `topApps` / `topDomains` / `recentSessions` (errgroup SQL, no list-API fan-out; unit-tested) |
| GET | `/api/v1/logs/insights` | JWT | Rule-based (no LLM) digest: metrics, productivity split, top apps/domains, outliers, highlights — server-bounded arrays |
| GET | `/api/v1/logs/graphical` | JWT | Chart payload bucketed `hour`/`day`: activity + productivity time series, summary, top apps/domains |
| GET | `/api/v1/attendance/events` | JWT | Day events for the new Timesheets **Day events** dialog |
| POST | `/api/v1/employees/:id/devices/remove` | JWT | **Remove Current Device** — revokes device tokens, closes open sessions/items, sets `tracking_status='untracked'`, marks the machine `old` + `wipe_requested_at` (returns counts; nothing is deleted) |
| POST | `/api/v1/devices/self-remove` | DeviceAuth | **Remove the account** (R4/R5) — called by the client AFTER its final drain; marks the CALLING machine `old`, advances `last_track_at`, revokes its device rows, flips the employee to `untracked` |

### Auth / RBAC hardening

- **Migration 045 `users.status`** (`active` | `suspended`, all rows backfilled `active`):
  `AuthService.Login` refuses to mint tokens (**403**), `RefreshSession` refuses to rotate the
  refresh cookie (an issued 30-day session cannot outlive a suspension), `CheckAuth` reports
  `authenticated=false` and clears cookies.
- **`JWTAuth` middleware** now loads the user on every request and — if `status != active` —
  clears the cookie and denies access. Web user-management gained per-row **Suspend / Reactivate**
  actions (badge shows `Suspended` / `Active`).
- **T&C RBAC leak closed:** `rbac_service.go` seeds the `settings/terms-and-conditions` submodule
  and the sidebar entry now carries the matching `module` key, so nav visibility and the route
  guard resolve to the same permission (was: visible to every role, gated by the unrelated
  General Settings permission). `company_admin` keeps access via `GrantAllPermissions` (40 grants);
  no other role is auto-granted — tick it per role on `/roles`.
- `golang.org/x/sync` promoted to a direct dependency (dashboard errgroup).

### Device lifecycle — migrations 046–050

| Migration | What it does |
| --------- | ------------ |
| **046** `device_machine_scope` | `machine_id` on every device-scoped table; device-wide natural keys (`permission_status` PK, `app_status` PK, junction uniques include `machine_id`); single-machine backfill |
| **047** `attribute_legacy_machine_rows` | Attributes leftover `machine_id=''` rows from the device registry's own `[created_at, last_seen_at]` activity window — only when exactly ONE window contains the row's sync time (idempotent, never guesses; `app_sessions` retention made the 046 approach unreliable) |
| **048** `add_device_activation_columns` | `employee_devices.is_active_device` + `remove_at` for removal tracking / audit |
| **049** `employee_machines` | Canonical one-row-per-physical-machine registry (`status active\|old`, `first_track_at`, `last_track_at`, `wipe_requested_at`), backfilled from `employee_devices` — `employee_devices` rotates a row per login and can no longer serve as the machine list |
| **050** `machine_id_coverage` | `machine_id` + indexes on `session_events`, `app_items`, `location_samples` (047-style attribution; `app_items` attributes exactly from its parent session) |

- **Persist-first wipe handshake:** `DeviceAuth` now also resolves **revoked** device tokens
  (`GetByTokenHashIncludingRevoked`), sets `device_removed` and skips `TouchLastSeen`; every sync
  handler persists the incoming batch **first** and only then answers `{"action":"wipe_local"}` —
  data a removed device just sent is never lost.
- `GET /employees/:id/detail` sources `devices[]` from `employee_machines` (one row per machine)
  with `status` / `firstTrackAt` / `lastTrackAt` / `wipeRequestedAt` and a corrected
  `isActiveDevice`; `DeviceAuth` keeps `last_track_at` current on every request.
- **Fix (`8f55701`):** Remove Current Device's `UPDATE employee_machines … WHERE employee_id=$1`
  had **no status filter** — it clobbered `last_track_at=NOW()` on already-`old` rows, making
  previously-removed machines look freshly tracked. Now filtered to `status='active'`.

### Attendance / holidays / dashboard support

- **Migration 042** — dashboard-support indexes proven needed by `EXPLAIN` on the live DB
  (`idx_app_items_type_opened`, partial `idx_employee_devices_last_seen`).
- **Migration 043** — `company_holidays.status` approval workflow (`pending | approved | rejected`;
  existing rows stay `approved`; only `approved` holidays mirror to clients via `GET /schedules/me`).
- **Attendance "All time"** now maps to the last **366 days** on both page and API (the API
  rejects empty / `>366`-day ranges; an empty preset used to 400).
- ⚠️ `server/.env.example` bootstrap-admin defaults were changed to dev values
  (`admin@gmail.com` / `123456`) — real deployments must override both before first boot.

---

## 2. Web

### Dashboard redesign — summary-first home

- `/dashboard` consumes the new `GET /dashboard/summary` in **one** request (URL-synced filters via
  `use-dashboard-filters`, capped presence poll — no list-API fan-out).
- New building blocks: `DashboardStatGrid`, `ActivityWidgets`, `OnlineNowPanel`
  (`components/dashboard/*`), restyled header, dismissible download strip.
- **Attendance card replaces Fleet Health** — today's attendance breakdown (Present / Late /
  Absent / Off shift, plus Half day / Not recorded when non-zero) for the department-filtered set,
  with a "View attendance →" link; the `devices` stats stay in the payload (attention strip still
  uses `devices.stale7d`).

### Logs pages on real APIs

- **`/logs/graphical`** rebuilt — hour/day activity + productivity charts, summary tiles, top
  apps/domains over `GET /logs/graphical`.
- **`/logs/insights`** (User Insights) rebuilt — metrics, productivity %, outliers and rule-based
  highlights over `GET /logs/insights`.
- **`/logs/comprehensive`** employee filter switched to the same searchable picker used by
  Timesheets / Employee Journey (shared `EmployeeSelector` search props).

### Holidays & timesheets

- **`/holidays`** made dynamic with the approval workflow — status filter chips
  (Pending / Approved / Rejected), `pending` as the create default, status badges.
- **Timesheets** gains a **Day events dialog** (`DayEventsDialog` → `GET /attendance/events`).

### Device Specs / employees

- **`?deviceId=` machine selector** on every Device Specs page (shown for 2+ machines), scoped
  through `GET /employees/:id/detail?deviceId=`; **"All devices" removed** from the dropdown —
  single-machine employees keep the employee-wide view; the URL is the source of truth and the
  selected machine mirrors back into `?deviceId=`.
- **Fix:** that removal briefly caused an infinite `?deviceId=` ⇄ React Query re-key ping-pong
  ("Maximum update depth exceeded") — resolved by scoping the URL-synced state to registered
  machines only.
- Removed-device empty states are truthful ("No hardware snapshot was recorded for this device
  before it was removed" instead of "will upload one after login").
- **Peripherals history:** older rows in a replugged group now show **Disconnected** via
  `effectiveDisconnected()` (a row with `unplugged_at=NULL` under a newer row is a replug, not
  still-connected).
- **Employee page header** made responsive — the fixed 280px device trigger next to the 384px
  employee picker overflowed the content area and clipped the dropdown on all but wide screens.
- Removed dead pages: **`/employee-portal`** and **`/employees/activity`**.

### Live stream (web)

- **ICE candidate queueing** (`useLiveStreamSocket`): per-PC `PcEntry { pc, remoteReady, queue }` —
  candidates arriving before `setRemoteDescription` completes are queued and flushed in order after
  the answer is applied; renegotiated PCs get fresh state so stale candidates can never hit a dead
  connection (web half of the ICE race fix below).
- Watch tile gains **full-screen open** on the preview (`LiveStreamWatchTile`).

### Tooling

- **`react-scan`** added as a dev dependency — `<ReactScan/>` mounts in the root layout and
  `npm run scan` launches it (render-performance overlay; dev only, no prod data path).

---

## 3. Desktop Client — 1.2.36

### Remove the account — `client --remove-account`

New `Services/AccountRemovalService.cs` owns the single removal routine; three entry paths:

1. **Cold-start** (`--remove-account` without `--background`) — wipes local SQLite before the host
   starts, kills sibling `client`/`AlphaAI` processes, app boots to the sign-in page.
2. **Background** (`--remove-account --background`) — **sync first**: wakes `SyncService` to drain
   every unsent table (`device_hardware_info`, `installed_apps`, `installed_packages`,
   `network_info`, `session_events`, `app_sessions`, `app_items`, `app_status`,
   `permission_status`, `storage_devices`), bounded by the new
   **`ALPHA_REMOVE_ACCOUNT_SYNC_TIMEOUT_SEC`** (default **120 s**); the local wipe always proceeds
   even if the server is unreachable; then kills the processes.
3. **Running instance** — a second invocation signals the live process over the single-instance
   channel (`SingleInstanceService` removal command); the RUNNING instance performs the removal (it
   owns the SQLite connection and GUI), so no restart races auto-start / systemd `Restart=always`
   for the mutex.

The wipe deletes **every local row, `.machine-id` and `device_id`** and calls
`AppConfig.ResetMachineId()` so the next login mints a fresh machine id.
Web shows copy-paste commands per OS (`lib/client-commands.ts`):
Linux `alpha-ai-tracker --remove-account`, Windows
`& "C:\Program Files (x86)\Alpha AI Tracker\client.exe" --remove-account`.

### Machine identity + wipe-aware sync

- New single-row **`machine_identity`** SQLite table (schema + store migration); **every telemetry
  INSERT stamps `machine_id`** from the lazy `AppConfig.ClientId` provider — a wipe + re-login in
  the same process stores the NEW id; `JourneyEngine` no longer uses the hostname as the machine id.
- **`SyncService`** parses the sync response `action` and raises **`OnServerRequestedWipe`**
  (once per process) → local DB + machine id wiped, UI returns to sign-in. The cold-start
  `--remove-account` path now starts the host first (the old code signalled a host that had not
  started, so the drain never ran and the server was never told).
- **Login hygiene:** a leftover local DB belonging to a DIFFERENT employee is wiped (with
  `ResetMachineId()` + `EncryptedConfigService.ResetCache()`) **before** the login request, so the
  rotated machine id and the server-side device registration always agree.

### Re-login re-collection fix

After a removal-wipe inside an already-running process, the collector's in-memory dedup
fingerprints (`_lastHardwareFingerprint`, `_lastNetwork*Ip`) survived and the inventory scan only
runs at startup — so a re-logged-in machine never re-uploaded hardware/network/software and the
web showed **"No hardware snapshot synced yet / No installed applications synced yet"** (reproduced
live on employee `MU-90`). New `LogCollectorService.RecollectAfterLoginAsync()` drops those
fingerprints (dedup then falls back to the wiped DB) and re-runs hardware + network +
`RescanInventoryAsync`; `MainViewModel.LoginAsync` fires it after `StartTracking()` and wakes
`SyncService` once it finishes. Restarting the client also heals an already-stuck machine.

### Setup wizard fix (Linux/macOS)

Step 2's gate `IsBackgroundGuardConfigured()` required the systemd user unit, but
`GrantBackgroundRunningAsync` only wrote the `.desktop` autostart entry — the step re-evaluated to
itself forever ("SETUP STEP 2 OF 4", no forward navigation). New
`AutoStartService.EnsureBackgroundGuardAsync()` writes
`~/.config/systemd/user/alpha-ai-tracker.service`, daemon-reloads and enables it — deliberately
**without** starting it (the wizard runs inside the live process; starting the unit would spawn a
mutex-fighting `Restart=always` twin) — and the step now throws honestly on failure. The macOS
gate checks the launchd plist instead of unconditionally returning `false` (same stuck-step class).

### Live stream client

- **`pendingIce` buffering:** trickled candidates are held until `setRemoteDescription` returns
  `OK`, then flushed in order; reset when the peer connection is recreated on `start`/`stop`
  (publisher half of the ICE race fix).

### Config (re-bake `config.enc`)

| Knob | Change |
| ---- | ------ |
| `ALPHA_REMOVE_ACCOUNT_SYNC_TIMEOUT_SEC` | **new**, default 120 (floor 5 s) |
| `ALPHA_IDLE_THRESHOLD_SEC` | default **120 → 300** |

---

## 4. WebRTC ICE trickle race — "LIVE / `--- FPS` / black tiles" (server + web + client)

**Root cause (signaling ORDER, not per-PC hardware):** the Pion SFU starts ICE gathering inside
`SetLocalDescription` during `AcceptPublisherOffer` / `AcceptSubscriberOffer`, and Pion fires the
candidate callback on its own goroutine — so `{"type":"ice"}` could be written **before**
`{"type":"answer"}`. A peer calling `addIceCandidate` while `remoteDescription` is still `null`
throws `InvalidStateError`; that candidate was silently dropped, the ICE/DTLS handshake never
completed, yet `ontrack` had already fired (the answer carried the media section) — the UI showed
LIVE with zero frames and the 5s renegotiation loop spun forever. Which employees broke was pure
timing luck (connections where the answer landed before the first candidate worked).

**Fix — buffer trickled candidates on all three peer roles until the remote description exists:**

| Tier | File | Change |
| ---- | ---- | ------ |
| Server | `internal/handlers/stream_handler.go` | New `iceTrickle` buffer (`sync.Mutex` + `answered` flag + `pending`) shared by the Push and Watch paths; pre-answer candidates are held and flushed **only after** the answer message is written |
| Web | `lib/useLiveStreamSocket.ts` | Per-PC `PcEntry { pc, remoteReady, queue }`; the `answer` branch awaits `setRemoteDescription` then flushes; the `ice` branch queues while `!remoteReady`; `cleanupPc` nulls the entry on renegotiation |
| Client | `Services/LiveStreamClient.cs` | `pendingIce` + `remoteIceReady` flushed after `setRemoteDescription` returns `OK`; reset on PC recreate |

No route, contract, env or migration change; the client half ships in this installer build.

## Bug Fixes Summary

| # | Issue | Root cause | Fix |
| - | ----- | ---------- | --- |
| 1 | Watched tiles read **LIVE / `--- FPS`** with zero frames (intermittent, any employee) | ICE candidate written before the answer → `addIceCandidate` dropped → DTLS never completes | Candidate buffering on server (answer-marked flush), web (`remoteReady` queue) and client (`pendingIce`) |
| 2 | Re-login after a removal showed **"No hardware snapshot synced yet / No installed applications synced yet"** | Removal wipe runs in-process; collector dedup fingerprints survive and the inventory scan only runs at startup | `RecollectAfterLoginAsync()` drops fingerprints + re-runs hardware/network/inventory; SyncService wake-up |
| 3 | Setup wizard stuck at **step 2** on Linux (macOS gate always false) | Gate required a systemd user unit that `GrantBackgroundRunningAsync` never wrote; macOS returned `false` unconditionally | `EnsureBackgroundGuardAsync()` writes + enables the unit (no start); macOS checks the plist |
| 4 | **"Maximum update depth exceeded"** crash after removing "All devices" from the dropdown | Infinite `?deviceId=` ⇄ React Query re-key ping-pong | URL-synced `deviceId` only set for registered machines; single-source selection |
| 5 | **Remove Current Device** made previously-removed machines look freshly tracked | `UPDATE employee_machines … WHERE employee_id=$1` with **no `status='active'` filter** clobbered old rows' `last_track_at` | Status filter added; only active machines are stamped |
| 6 | **Terms & Conditions** visible to every role, gated by the wrong permission | Sidebar entry had no `module` key; catalog had no T&C submodule | Seeded `settings/terms-and-conditions` + matching `module` key (RBAC Nav → Catalog Parity Rule) |
| 7 | A **suspended** user could still sign in / keep an existing 30-day session | No `users.status` concept; JWTAuth never re-checked the user | Migration 045 + login/refresh/CheckAuth/JWTAuth status checks + Suspend/Reactivate UI |
| 8 | Device dropdown **clipped** in the Device Specs header | Non-wrapping header row (384px picker + 280px trigger) overflowed the content area | Responsive header layout |
| 9 | Peripherals **history** showed replugged devices as still Connected | Row status read from its own `unplugged_at` only | `effectiveDisconnected()` — older rows under a newer one are Disconnected |
| 10 | Attendance **All time** filter failed (400) | Preset sends empty `from`/`to`; API requires `YYYY-MM-DD`, ≤366 days | Page and API both map All time → last 366 days |
| 11 | Retired devices showed **wrong empty states** and unattributed (`machine_id=''`) legacy rows | Migration 046 attributed only from `app_sessions` (purged by 30-day retention) | Migration 047 attributes from the device registry activity window; truthful empty states |
| 12 | Live preview tile had **no full-screen affordance** | Missing control on `LiveStreamWatchTile` | Full-screen open added on the watch tile |

## Verification

| Check | Result |
| ----- | ------ |
| `go build ./...` / `go vet ./...` | clean |
| `go test ./internal/services -run Dashboard` (+ stream/redis/scale packages) | pass |
| `npx tsc --noEmit` | clean |
| `next build` | passes (dashboard, logs, holidays, device-specs routes registered) |
| `dotnet build` | 0 warnings / 0 errors |
| Migration 047 on the dev DB | dry-run (rolled back) then applied — rows attributed per table **10/3/6/21/8/37/242/4354**, 0 left unattributed; per-device reads verified for `default-EMP-10003` (retired 99 / current 73 / aggregate 73) |
| Device remove handshake (live) | admin remove → machine `old` + `wipe_requested_at`; the revoked token's next batch was **persisted and** answered `{"action":"wipe_local"}`; `self-remove` → `{removed:true, machineId, status:"old", trackingStatus:"untracked"}` |
| The three 2026-10-19 fixes | verified **end-to-end on the installed build** (re-login re-collection on MU-90, wizard step 2 on Linux, Device Specs dropdown) |
| Machine-id stamping (live) | hardware / session-event / location syncs stamped the device's `machine_id`; detail `devices[]` returned one row per machine with status |

---

## Deploy sequence

1. **Server first** — applies migrations **042–050** on startup; the boot seeder re-ensures the RBAC
   catalog (grant `settings/terms-and-conditions` per role). Set bootstrap admin credentials —
   `.env.example` now carries dev defaults.
2. **Web** — `npm ci` (new dev dependency `react-scan`) and rebuild/deploy.
3. **Client** — rebuild installer **1.2.36** with a **re-baked `config.enc`**
   (`ALPHA_REMOVE_ACCOUNT_SYNC_TIMEOUT_SEC`, `ALPHA_IDLE_THRESHOLD_SEC=300`), install, restart the
   tracker. Per the **Installer-Parity Rule** the client half (remove-account, machine identity,
   re-login re-collection, wizard fix, ICE buffering) is only done when verified from the installed
   build — the 2026-10-19 fixes were verified that way.

---

## Known gaps / follow-ups

- **Migration 044 does not exist** (number intentionally skipped) — ops should not hunt for it.
- Server-side **RBAC permission enforcement** in API middleware is still not implemented (grants
  are enforced in the web client only; the JWTAuth *status* check is new, but permission checks
  remain frontend-only).
- **No rate limiting** on login or sync endpoints.
- Linux **screen capture** for the live stream is still deferred (publisher is Windows-only).
- ⚠️ `server/.env.example` bootstrap admin defaults (`admin@gmail.com` / `123456`) are dev values —
  production installs must override them.
- `employee_devices.is_active_device` / `remove_at` (048) and `employee_machines` (049) both
  express device state now; `employee_devices` keeps rotating rows on login — never drive a machine
  list or status badge off it (Device Lifecycle Rule, AGENTS.md §6).

---

---
# Release Notes — v1.2.35

## Overview

v1.2.35 is a **live-stream field fix** on top of **1.2.34**: the publish uplink floor is lowered so
employees on thin office uploads (~1 Mbps) still publish preview video instead of showing admin
**LIVE / --- FPS** / black tiles while a peer on a fatter link works fine.

Client version: **1.2.35**. Installer: `AlphaAITracker-Setup-1.2.35.exe`.

**Scope of this note:** uplink-floor change only. All V3 WebRTC / TURN / Win11 capture behaviour from
**1.2.34** is unchanged — see that note below for the full V3 baseline.

---

## Root cause (field)

On the public VPS deploy (TURN configured), **HA-139** previewed correctly while **AH-138** stayed
black:

| Signal | HA-139 | AH-138 |
| ------ | ------ | ------ |
| Push / hello / version | OK (`1.2.34`) | OK (`1.2.34`) |
| SFU | `publisher track ready` / `withTrack=true` | **no** publisher offer / `withTrack=false` |
| `app_status.stream_skip_reason` | empty | `uplink_below_floor measured=1162 min=2500` |
| `app_status.stream_uplink_kbps` | ~4619 | (skipped before publish) |

Sidebar **“Stream ready”** only means the push WebSocket is up — it does **not** mean RTP is
flowing. AH-138’s NetProbe measured **~1162 kbps**; **1.2.34** default
`ALPHA_STREAM_MIN_UPLINK_KBPS=2500` refused to publish, so the SFU never got a publisher track.

TURN was **not** the cause for this pair (HA-139 proved the media path).

---

## Fix

| Change | Detail |
| ------ | ------ |
| Bake / default floor | `ALPHA_STREAM_MIN_UPLINK_KBPS` **2500 → 300** (`.env`, `.env.example`, `AppConfig` default) |
| Clamp range | Allow **100–15000** (was 500–15000) so a 300 bake is not forced up to 500 |
| Encode path | Unchanged — ABR ladder still picks ~**500 kbps** encode on ~1 Mbps uplinks (`NetProbeService.SelectBitrateKbps`) |
| Skip behaviour | Still skips when measured uplink &lt; floor; reason still written to `app_status.stream_skip_reason` |

No server or web code change required for this release. Ops must still keep **TURN** configured on
public VPS (`WEBRTC_TURN_*`) for CGNAT / hard NAT employees.

---

## Bug Fixes Summary

| # | Issue | Root cause | Fix |
| - | ----- | ---------- | --- |
| 1 | AH-138 LIVE / `--- FPS` / black; HA-139 OK on same VPS | `ALPHA_STREAM_MIN_UPLINK_KBPS=2500` skipped publish at measured ~1162 kbps | Floor **300**; re-bake `config.enc` + install **1.2.35** |

---

## Verification

| Check | Result |
| ----- | ------ |
| Field evidence | `app_status` + PM2 SFU logs (AH-138 skip vs HA-139 track ready) |
| Config | `.env` / `.env.example` / `AppConfig` default = **300** |
| Installer | Rebuild `AlphaAITracker-Setup-1.2.35.exe` with re-baked `config.enc` |
| Expected after deploy | AH-138: empty `stream_skip_reason`, `publisher track ready`, tile FPS &gt; 0 |

---

## Deploy sequence

1. **Server / Web** — no change required for this floor tweak (keep TURN if already provisioned).
2. **Client** — set `ALPHA_STREAM_MIN_UPLINK_KBPS=300` in `.env` **before** `encrypt-config.sh`, build
   installer **1.2.35**, install on affected machines (at least AH-138), restart tracker.
3. Confirm:  
   `SELECT key, value FROM app_status WHERE employee_id='AH-138' AND key LIKE 'stream_%';`  
   expect empty `stream_skip_reason` and non-zero `stream_send_kbps` while watched.

**Installer-Parity:** editing `.env` on the VPS alone does **not** update already-installed PCs —
they keep the old baked **2500** until a new installer / `config.enc` lands.

---

## Known gaps / follow-ups

- Very thin links (&lt; 300 kbps measured) still skip publish by design.
- Soft stream at ~500 kbps encode is usable preview, not Meet-class sharpness.
- Linux screen capture still deferred.
- Raise `ALPHA_STREAM_MIN_UPLINK_KBPS` again per company policy if you prefer hard skip over a
  soft stream on slow uplinks.

---

---

# Release Notes — v1.2.34

## Overview

v1.2.34 is the **Live Stream V3.0** release on top of the v1.2.2 WebRTC SFU baseline. It ships
safety hardening, slow-network ABR, Redis-backed single-server cluster state, socket lifetime /
device re-check, Win11 capture reliability, Client Version refresh without re-login, late-joiner
keyframe recovery, and consistent theater tile fill.

Branch: `feature/live_streamV3.0` (plan: `plan.md`). Client version: **1.2.34**.
Installer: `AlphaAITracker-Setup-1.2.34.exe`.

**Scope of this note:** everything after **v1.2.2** (V3 Phase 0–3 + field fixes). Theater popout /
per-tile FPS that landed with v1.2.2 remain the baseline and are not re-listed unless changed.

---

## 1. Server — V3 safety, ABR egress, Redis, socket hardening

### Phase 0 — Safety

- `CheckOrigin` fails **closed** when `CORS_ALLOWED_ORIGINS` is empty (CSWSH guard).
- Watch `Subscribe` capacity checked **before** WebSocket Upgrade → HTTP **429**.
- `sendPLI` takes a done channel tied to publisher close.
- `TouchLastSeen` runs on `context.Background()` + timeout (no race after WS upgrade).
- Watch-ticket `UserID` retained for audit logging.

### Phase 1 — Slow-network / egress

- DeviceAuth `POST /api/v1/live-stream/uplink-probe` (body discarded; client times RTT).
- Token-bucket enforcement of `WEBRTC_MAX_BITRATE_KBPS` on `forwardRTP`.
- RTP truncation guard on oversized packets.
- Watch-ticket map capped; per-offer goroutines bounded (semaphore).
- Startup **WARNING** when `WEBRTC_TURN_URLS` is empty (ops: provision TURN for VPS/CGNAT).
- 4h max socket lifetime on push / watch / presence paths.

### Phase 2 — Redis cluster state (single-server deploy)

- Presence mirror: `presence:emp:*` + pub/sub `alpha:presence`.
- Watch tickets: `stream:ticket:*` (+ route channel `alpha:stream_route`).
- Publisher registry: `stream:pub:{emp}` → `instanceId`.
- Env: optional `INSTANCE_ID` / `INSTANCE_PUBLIC_URL` (auto instance id when unset).
- Watch-ticket may return `watchBaseUrl`; Watch may **307** if a second API appears later.
- Sticky LB example **removed** — not needed for one-API deploy. Pion SFU kept.

### Phase 3 — DeviceAuth socket re-check + Online contract

- Push + presence WS: every **5 minutes** `DeviceRepo.IsActive` — revoked/expired/missing device
  closes the socket; transient DB errors keep it open.
- `employeeLiveOnline` contract unit-tested: presence-on → WS authoritative; presence-off →
  heartbeat window (≤3 min).

### Field fixes (post Phase 3)

- **Client Version without re-login:** `TouchLastSeen(deviceID, version, platform)` updates
  `employee_devices.client_version` / `platform` from DeviceAuth headers
  `X-Client-Version` / `X-Client-Platform`.
- **Late-joiner blank LIVE tile:** `NudgePublisherKeyframe` when a watcher answer includes media;
  `WatchTrackReady` fires immediately if the track already exists (fixes missed `track_ready` race).

---

## 2. Client — ABR, Win11 capture, identity headers (1.2.34)

### Phase 0 / 1

- `ScreenVp8Encoder` — wrap-only (`VpxImgWrap`); never `VpxImgFree` on a managed buffer (F1).
- ICE `iceReady` completed in `finally`; session cancellation token (no hung ICE tasks).
- Idle capture poll skipped when streaming is off.
- **`NetProbeService`** — uplink probe → bitrate ladder under `ALPHA_STREAM_MAX_BITRATE_KBPS`.
- Skip publish when measured uplink &lt; `ALPHA_STREAM_MIN_UPLINK_KBPS` (reason in `app_status`).
- ABR on send backpressure: bitrate → resolution → fps; slow recovery.
- Server `force_keyframe` ctrl → `ForceKeyFrame()` (PLI path).
- Dedicated LongRunning capture thread; Bitmap / scratch reuse (LOH churn cut).
- `ALPHA_STREAM_VP8_LAG_FRAMES` (default **0**; opt-in altref).
- Stream telemetry via `app_status`: uplink, selected bitrate, send kbps, encode ms, drops.
- CLI: `--self-test-stream-ladder`.

### Win11 blank preview + ICE gating (1.2.33 → 1.2.34)

- **Root cause (FA-27):** GDI `CopyFromScreen` / CAPTUREBLT-only BitBlt failed → no RTP → SFU
  `withTrack=false` while UI still showed Stream ready / LIVE.
- **`ScreenCaptureService`:** multi-path BitBlt — `SRCCOPY` → CAPTUREBLT → desktop HWND →
  `CreateDC("DISPLAY")` → CopyFromScreen fallback.
- **`LiveStreamClient`:** `SendVideo` only after WebRTC PC `connected` + force keyframe on connect.
- Honest diagnostics: `stream_capture_stall`, `stream_capture_error`, `stream_pc_state`,
  `stream_ice_state`, `stream_encode_error`, `stream_send_error`.

### Client Version refresh (1.2.33+)

- **`ClientIdentityHeaders`** — `X-Client-Version` / `X-Client-Platform` on all DeviceAuth HTTP + WS.
- Boot persists `app_status.client_version` so reinstall updates the web Client Version column
  without employee re-login.

### New / changed env (re-bake `config.enc`)

| Key | Role |
| --- | ---- |
| `ALPHA_STREAM_MIN_UPLINK_KBPS` | Floor before publish (default 2500) |
| `ALPHA_STREAM_VP8_LAG_FRAMES` | libvpx altref lag (default 0) |
| `ALPHA_STREAM_DEBUG_SEND_DELAY_MS` | Dev ABR inject (optional) |
| Existing `ALPHA_STREAM_*` / `ALPHA_WS_*` | Unchanged shape; must be in bake |

---

## 3. Web — watch resilience + tile fill

### Phase 0 / 1

- `useLiveStreamSocket`: stop prior tracks + `video.pause()` on cleanup / before `ontrack` overwrite.
- Clear open-offer timer on cleanup.
- Employee list poll: shared queryKey console ↔ theater; `staleTime` / `refetchInterval` tuned
  (avoid multi-tab hammering).
- Prefer ticket `watchBaseUrl` when present (future multi-API).

### Field fixes

- **Blank LIVE recovery:** if `ontrack` fires but `videoWidth` stays 0 for 5s → one renegotiate.
- **Consistent tile fill:** `<video>` uses `absolute inset-0 w-full h-full object-contain` so every
  employee fills the tile (letterbox as needed). Previously intrinsic resolution made some tiles
  look fullscreen and others tiny/partial.

---

## Bug Fixes Summary

| # | Issue | Root cause | Fix |
| - | ----- | ---------- | --- |
| 1 | FA-27 LIVE / `--- FPS` / SFU `withTrack=false` | Win11 GDI capture failed; RTP never published | Multi-path BitBlt + send after PC `connected` (1.2.34) |
| 2 | Web Client Version stuck after reinstall | `client_version` only set on employee-login | DeviceAuth `X-Client-Version` → `TouchLastSeen` |
| 3 | LIVE tile black after late join | Missed IDR / `track_ready` race | `NudgePublisherKeyframe` + immediate `WatchTrackReady` + web renegotiate |
| 4 | Some tiles fullscreen, some not | Video sized to intrinsic resolution | Tile-filling `object-contain` layout |
| 5 | Silent “slideshow” on thin uplink | Fixed bitrate, no floor | Uplink probe + ABR ladder + skip below floor |
| 6 | Encoder heap risk on every stop | `VpxImgFree` on wrapped managed ptr | Wrap-only encoder (F1) |
| 7 | CSWSH if CORS list empty | `CheckOrigin` fail-open | Fail-closed |
| 8 | Watcher over-capacity | Subscribe after Upgrade | Capacity check → HTTP 429 before Upgrade |

---

## Verification

| Check | Result |
| ----- | ------ |
| `dotnet build` | 0 warnings / 0 errors |
| `go build` / `go vet` | clean |
| `go test` stream + redis (+ related) | PASS |
| `npx tsc --noEmit` | clean |
| Installer | `AlphaAITracker-Setup-1.2.34.exe` built |
| Installed proof | FA-27 + MU-115 live preview; Client Version updates without re-login |
| F1 heap | Argument from libvpx contract; dedicated start/stop cycle still recommended |

---

## Deploy sequence

1. **Server** — deploy/restart with Redis up; set `WEBRTC_*`, optional `INSTANCE_*`; provision
   **TURN** on public VPS (`WEBRTC_TURN_*`). No new migration required for V3 stream work.
2. **Web** — deploy Next app (tile fill + blank-LIVE recovery + `watchBaseUrl`).
3. **Client** — install **1.2.34**; ensure `.env` has V3 stream keys **before** `encrypt-config.sh`
   so `config.enc` is re-baked (Installer-Parity).

---

## Known gaps / follow-ups

- Linux screen capture still deferred (Windows multi-path GDI only; DXGI Desktop Duplication not yet).
- TURN remains an ops prerequisite for many remote / CGNAT employees.
- Software VP8 (no hardware H.264); text sharpness is good but not Meet-class.
- F1 wrap-only fix should still get a dedicated installed start/stop/restart stress pass.
- Older clients (&lt; 1.2.33) still need one login (or upgrade) for Client Version to refresh.

---

---

# Release Notes — v1.2.2

## Overview

v1.2.2 is the **Live Stream WebRTC SFU** release. The Phase 1 JPEG WebSocket relay is replaced by
a Pion SFU that RTP-forwards VP8 from the Windows desktop publisher to admin watchers. Preview
remains ephemeral (never stored). A dedicated presence WebSocket drives Online/Offline without
opening media. Encode quality is fixed by `ScreenVp8Encoder` (SIPSorcery had been wiping the
configured bitrate). The admin web console gains a full-bleed **theater popout** with a
professional watch-wall UX (collapsible sidebar, cols-per-row, scrollable multi-screen wall).

Branch: `feature/live_streamV2.0`. Client version: **1.2.2**.

---

## 1. Server — WebRTC SFU + presence

- `internal/stream/sfu.go` — Pion SFU (publisher → watchers); JPEG mailbox removed.
- Hub keeps signaling, consent (`live_view`), `select_monitor`, idle reap, caps.
- DeviceAuth: `GET /live-stream/push`, `GET /ws` (presence).
- JWT: `GET /live-stream/employees`, `watch-ticket`; ticketed `GET /live-stream/watch`.
- Env: `LIVE_STREAM_*`, `WEBRTC_MAX_BITRATE_KBPS`, `WEBRTC_STUN_URLS`, optional TURN, `PRESENCE_WS_*`.

## 2. Client — VP8 publisher + ScreenVp8Encoder + WsClient

- `LiveStreamClient` — WebRTC offer/answer/ICE; encode only after server `start`.
- `ScreenCaptureService` — Windows GDI; max width hard-capped at 1920.
- `ScreenVp8Encoder` — applies `RcTargetBitrate` **after** `VpxCodecEncConfigDefault` (fixes ~256 kbps default); VBR + ~1s keyframes.
- `WsClient` — presence keep-alive (`ALPHA_WS_*`), independent of stream.
- Env (re-bake `config.enc`): `ALPHA_STREAM_*`, `ALPHA_WS_*`.

## 3. Web — `<video>` viewer + theater popout UX + FPS

### Main console (`/live-stream`)

- `useLiveStreamSocket` + multi-tile `?ids=` (max **4**).
- Per-tile decoded **FPS** badge (`hooks/use-video-fps.ts`).
- Shared `components/live-stream/*` (WatchTile, EmployeeSidebar, PreviewGrid, selection helpers).
- **Open theater** opens `/live-stream/theater?ids=` in a new browser tab.
- `useUrlQueryState`: never call `router.replace` inside a `setState` updater (React 19 LinkComponent warning).

### Theater popout (`/live-stream/theater` — `(live-popout)` layout)

Full-bleed watch wall (no AppSidebar / TopBar). Auth + RBAC still apply.

- **Collapsible employee sidebar** — full list or thin rail (`?sidebar=0|1`); search; selection cap banner.
- **Toolbar navbar** — sidebar toggle; **screens per row** 1 / 2 / 3 / 4 (`?cols=`); online-only filter (`?online=1`); clear all; fullscreen; link back to main console.
- **Scrollable wall** — aspect-video tiles in a column grid; vertical scroll when many screens are open (theater max **16**, vs console max 4).
- URL-synced layout: `?ids=&q=&cols=&sidebar=&online=` (shareable / refresh-safe).

## Deploy sequence

1. **Server** — restart with new SFU env (no new migration required for the SFU swap).
2. **Web** — deploy Next app.
3. **Client** — installer **1.2.2** with stream/presence knobs in `config.enc`.

## Known gaps

- Linux screen capture still deferred.
- Software VP8 (no hardware H.264 yet); fine UI text is better than JPEG-era soft bitrate but not Meet-class.
- Watching your own stream (hall of mirrors) stacks compression — judge quality on sharp desktop UI.

---

---

# Release Notes — v1.2.1

## Overview

v1.2.1 is a **bugfix release** addressing two issues: an infinite orphan `app-items` sync loop on
the client (32 poison rows re-sent every sync pass, never quarantined) and a 500 error on the
employee update endpoint caused by a missing column projection in the `UPDATE…RETURNING` clause.

Branch: `main`. Client version bump: **1.2.0 → 1.2.1**.

---

## 1. Client — Orphan App-Items Quarantine Fix

**Root cause**: `OnAppItemsRejected` only incremented `_missingSessionRetryCount` when
`_lastMissingSessionIds.Count > 0` — a stale guard left over from the pre-quarantine design. When
the server refused 32 rows with missing parent sessions, the retry count never advanced, the
quarantine threshold of 3 was never reached, and the same 32 rows were re-sent on every sync pass
indefinitely.

**Fix** (`client/Services/SyncService.cs`): removed the `_lastMissingSessionIds.Count > 0` guard
so that every call to `OnAppItemsRejected` — regardless of whether the missing session list is
populated — increments `_missingSessionRetryCount`. After 3 consecutive refusals the rows are
quarantined in-memory and skipped by the drain until process restart. The SQLite rows stay
`is_synced=0` (diagnostic evidence; retention only purges synced rows).

---

## 2. Server — Employee Update 500 Fix

**Root cause**: `employee_repo.go` `Update` RETURNING clause projected 17 columns but `scanEmployeeRow`
expects 18 — the `client_version` subquery was missing. Every `PUT /api/v1/employees/:id` returned
SQLSTATE 42703 (undefined column) → 500.

**Fix** (`server/internal/repository/employee_repo.go`): added the missing `client_version`
subquery to the `UPDATE…RETURNING` clause:

```sql
(SELECT cv.client_version FROM employee_devices cv
 WHERE cv.employee_id = e.employee_id
 ORDER BY cv.updated_at DESC LIMIT 1) AS client_version
```

---

## Bug Fixes Summary

| # | Issue                                                              | Root cause                                                                                        | Fix                                                                          |
| - | ------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------- |
| 1 | Infinite orphan`app-items` sync loop (32 poison rows every pass) | `_lastMissingSessionIds.Count > 0` guard prevented quarantine counter from advancing            | Removed the guard; all refusals count toward the 3-pass quarantine threshold |
| 2 | `PUT /api/v1/employees/:id` returns 500                          | `UPDATE…RETURNING` missing `client_version` subquery; `scanEmployeeRow` expects 18 columns | Added the subquery to the RETURNING clause                                   |

---

## Verification

- ✅ `dotnet build` 0 errors (orphan quarantine fix)
- ✅ `go build` / `go vet` clean (employee update fix)

---

## Deploy Sequence

1. **Server first** — the employee update fix is server-only; no migration needed.
2. **Client** — ships in the next installer build (the quarantine fix is in the compiled `client.dll`).

---

## Known Gaps / Follow-ups

- The 32 quarantined orphan rows will persist in SQLite until process restart (they are
  `is_synced=0` and never purged by retention). A future release could add a user-visible
  "Quarantined rows" diagnostic badge.
- Server-side permission enforcement (RBAC grants) is still not implemented in API middleware.
- No rate limiting on login or sync endpoints.

---

---

# Release Notes — v1.2.0

## Overview

v1.2.0 is the **Live Screen Preview** release. Admins open `/live-stream`, pick an online
employee who has accepted the `live_view` terms, and watch that machine’s screen in real time.
Frames are **preview-only** — never recorded, never written to disk or Postgres. Transport is a
WebSocket JPEG relay (client push + admin watch) with activity-gated capture (encode only while
someone is watching). Phase 1 ships Windows capture; Phase 2 multi-monitor selection (switch which
display is previewed) is included and verified on a dual-monitor PC.

Branch: `feature/live_stream`. Client version bump: **1.1.6 → 1.2.0**.

---

## 1. Server — Live Stream Hub + APIs

- **In-memory hub** (`server/internal/stream/`) — latest-frame-wins mailbox, watcher fan-out,
  start/stop control to the desktop push socket, idle reap, hard caps
  (`LIVE_STREAM_MAX_STREAMS`, max watchers/employee, max frame bytes, max FPS).
- **Auth split (Client-vs-Web API Auth Separation Rule):**| Surface                     | Routes                                                                                             | Auth                           |
  | --------------------------- | -------------------------------------------------------------------------------------------------- | ------------------------------ |
  | Desktop push                | `GET /api/v1/live-stream/push` (WS)                                                              | `DeviceAuth`                 |
  | Admin list / ticket / watch | `GET /live-stream/employees`, `GET /live-stream/watch-ticket`, `GET /live-stream/watch` (WS) | JWT / short-lived watch ticket |
- **Consent gate** — `terms_consent` feature `live_view` re-checked on push upgrade and watch
  ticket mint (and again on watch connect).
- **Online signal** — employee listed Online when `app_status.last_heartbeat_at` /
  `updated_at` is within **3 minutes** (sync cadence ~60 s; a 60 s window was flapping Offline).
- **Watch ticket** — REST mints a one-shot ticket so the browser can open the watch WebSocket
  without relying on cross-port httpOnly cookies (`NEXT_PUBLIC_WS_URL` → Go origin).
- **Env knobs** (`server/.env.example`): `LIVE_STREAM_ENABLED`, `LIVE_STREAM_MAX_FPS`,
  `LIVE_STREAM_FRAME_MAX_BYTES`, `LIVE_STREAM_MAX_STREAMS`,
  `LIVE_STREAM_MAX_WATCHERS_PER_EMPLOYEE`, `LIVE_STREAM_IDLE_SEC`, `LIVE_STREAM_TEST_FRAME`.

### Migrations shipped with this release

| Migration                                             | What                                                                                                                                                       |
| ----------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **040** `040_fix_app_items_url_index.sql`     | Drops`idx_app_items_url` — unbounded URL btree keys exceeded Postgres’ ~2704-byte limit (SQLSTATE 54000) and 500’d entire `app-items/sync` batches  |
| **041** `041_fix_app_items_context_index.sql` | Replaces`idx_app_items_context(employee_id, item_type, identifier)` with `idx_app_items_emp_type` — same overflow when `identifier` held a long URL |

---

## 2. Client — Windows Capture + Push Socket

- **`ScreenCaptureService`** — Windows GDI `CopyFromScreen` → managed JPEG; parked on
  Linux/macOS (`streamAvailable: false`). Adaptive FPS throttle under encode pressure.
- **`LiveStreamClient`** — persistent outbound WS to `/live-stream/push` while
  `ALPHA_STREAM_ENABLED=true` and logged in; frames only after server `{"type":"start"}`;
  stops on `stop` / last watcher gone.
- **Multi-monitor (Phase 2)** — `EnumDisplayMonitors` lists displays in `hello`; admin
  `select_monitor` switches the capture target. Monitors are **re-scanned on every hello /
  capture start** (fixed a bug where a one-shot startup scan left the web stuck on a single
  display after a second monitor was attached).
- **Config** (installer-parity): `ALPHA_STREAM_ENABLED` (default **off** in `.env.example`),
  `ALPHA_STREAM_FPS`, `ALPHA_STREAM_MAX_WIDTH`, `ALPHA_STREAM_JPEG_QUALITY` — must be in
  `.env` before `encrypt-config.sh` / installer bake.
- **Retention fix** — local SQLite purge deletes child `app_items` before parents
  (`parent_item_id` self-FK), stopping `FOREIGN KEY constraint failed` from aborting sync
  passes after a clean drain.

---

## 3. Web — `/live-stream`

- Two-pane UI: searchable employee rail (online-first) + live canvas.
- States: connecting / live / offline / consent missing / unavailable / waiting for desktop
  `hello`.
- **Monitor dropdown** when the client reports displays (enabled when 2+); one preview at a
  time — switch display, do not show all monitors side-by-side.
- URL-synced `?employeeId=` / `?q=` via `useUrlQueryState` + `<Suspense>`.
- Watch socket via `useLiveStreamSocket` + `NEXT_PUBLIC_WS_URL` (Next rewrites do not proxy WS).

---

## Bug Fixes Summary

| # | Issue                                                    | Root cause                                                        | Fix                                                                    |
| - | -------------------------------------------------------- | ----------------------------------------------------------------- | ---------------------------------------------------------------------- |
| 1 | Live-stream rail showed Offline while client was syncing | Online window was 60 s; heartbeats arrive on ~60 s sync           | 3-minute window + use`GREATEST(value, updated_at)`                   |
| 2 | `app-items/sync` 500 — `idx_app_items_url`          | Long URLs overflow btree key limit                                | Migration**040** drop index                                      |
| 3 | `app-items/sync` 500 — `idx_app_items_context`      | Long`identifier` (often URL) in composite btree                 | Migration**041** replace with `(employee_id, item_type)`       |
| 4 | Sync pass warn — SQLite FK on retention                 | Deleting parent`app_items` while children still referenced them | Delete children-with-parent first, then roots without children         |
| 5 | Dual-monitor PC, web only showed one display             | Monitors enumerated once at process start                         | Re-scan on hello / start capture; pin enum callback; use callback rect |
| 6 | Consent / feature id mismatch during early wiring        | Seeded feature is`live_view`, not `live_stream`               | Hub`FeatureID = "live_view"` end-to-end                              |

---

## Verification

- ✅ Dual-monitor preview switch confirmed on the web (admin picker → correct display).
- ✅ End-to-end Windows preview (`dotnet` / debug client) + server hub hello with `monitors=2`.
- ✅ Hub unit tests (`go test ./internal/stream/...`) including `select_monitor`.
- ✅ `go build` · `dotnet build` 0/0 · `npx tsc --noEmit`.
- ⚠️ **Installer ship-test** — re-bake `config.enc` with `ALPHA_STREAM_*`, build
  `AlphaAITracker-Setup-1.2.0.exe`, verify live preview from the **installed** client
  (Installer-Parity Rule). Old installed 1.1.x builds will not have the monitor re-scan fix
  until reinstalled.

---

## Deploy Sequence

1. **Server first** — applies migrations **040–041**; enables live-stream hub env as needed.
2. **Web** — deploy with `NEXT_PUBLIC_WS_URL` pointing at the Go host (e.g.
   `ws://192.168.88.44:8080`); ensure that origin is in `CORS_ALLOWED_ORIGINS`.
3. **Client** — installer **1.2.0** with `ALPHA_STREAM_ENABLED=true` baked only for fleets that
   should offer preview (default in `.env.example` remains `false`).

---

## Known Gaps / Follow-ups (Phase 2+)

- Linux / macOS screen capture still reports unavailable (no Linux ship-test machine yet).
- WebRTC media path, Redis multi-node hub, and **server-side who-can-watch RBAC** not in this
  release (any admin with the live-stream module can watch any consented employee).
- Side-by-side multi-monitor mosaic is out of scope — one selected display per stream.
- Ephemeral / consent-revoke / cap stress checks still worth a formal installed-build pass.

---

---

# Release Notes — v1.1.6

## Overview

v1.1.6 is the **Terms & Conditions release**: a per-feature T&C consent framework spanning all three
services, shipped as a **third standalone client instance**. Employees see a neutral, always-on-top
acceptance dialog owned by its own process — completely separate from the Alpha AI Tracker GUI — and
every acceptance lands in the server's append-only audit trail. The release includes 44 files
changed (+5,038 / −407), migrations **036–039**, a new TipTap-based admin editor on the web, and a
new `--terms` agent mode on the client.

---

## 1. Server — Terms & Conditions Framework (migrations 036–039)

- **Migration `036_terms_consent.sql`** — append-only `terms_consent` table
  (`employee_id`, `feature_id`, `terms_version`, `action`, `created_at`; UNIQUE on
  `(employee_id, feature_id, terms_version, action)` — re-sending an accepted entry is idempotent).
- **Migrations `037`/`038`** — `terms_content`: `id` (UUID-text), `heading`, `body` (**HTML**),
  `terms_version`, `is_system`, `feature_id`, `term_type`, `is_active`, `sort_order`, soft delete.
- **Migration `039_terms_content_unique_feature_fix.sql`** — the unique index on `feature_id` is
  re-scoped to non-empty values only:
  `CREATE UNIQUE INDEX idx_terms_content_feature ON terms_content (feature_id) WHERE deleted_at IS NULL AND feature_id <> ''`.
  Featured terms keep their one-per-feature guarantee; admin-created manual terms
  (`feature_id = ''`) are exempt — unlimited creates. (Fixes the `POST /terms-content` 500s caused
  by a manually-added live-DB index colliding every manual create on the empty string,
  SQLSTATE 23505.)
- **Boot seeder** (`terms_content_seeder.go`): four featured terms seeded idempotently —
  `app_usage`, `browser_journey`, `file_journey`, `live_view` (all `terms_version` `1.0`).

### Auth surfaces separated (Client-vs-Web API Auth Separation Rule, codified in AGENTS.md §6)

| Surface                   | Routes                                                                                                        | Auth                                                                                                                                                     |
| ------------------------- | ------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Client (device)** | `GET /terms-content/active`, `POST /terms-consent/sync`                                                   | `DeviceAuth` (the `syncGroup` — employee JWTs were rejected by the old JWTAuth admin-issuer check, and `employee_id` was never set under JWTAuth) |
| **Web admin**       | `POST/PUT/PATCH/DELETE /terms-content`, `GET /terms-content[/:id]`, `GET /terms-consent` (+ `/check`) | `JWTAuth`                                                                                                                                              |

`GET /terms-content/active` returns **only** `is_active = 1` terms in server order
(`sort_order ASC, created_at ASC`); `GET /terms-consent` remains the web's privacy/audit view.

---

## 2. Web — Terms Management UI (`/settings/terms-and-conditions`)

- **List page**: feature cards with hover-border highlight, deep-link to detail; featured
  (`is_system`) terms are protected from deletion.
- **Create / Edit / View** routes (`/create`, `/edit/{id}`, `/view/{id}`).
- **TipTap rich-text editor** (`terms-editor.tsx` + `rich-text-editor.tsx`) — headings, bold,
  italic, underline, bullet/ordered lists, text alignment; new deps
  `@tiptap/react`, `@tiptap/starter-kit`, `@tiptap/pm`, and the underline / list / text-align
  extensions.
- Sidebar entry added under Settings.

---

## 3. Client — Third Standalone Instance: the Terms & Conditions App

The client now runs as **three instances**:

| Instance | What                                                                              | How it runs                                                                                                                                |
| -------- | --------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------ |
| 1        | **Tracker GUI** — login, shell, dashboard pages                            | `client.exe` (normal launch) — unchanged behavior                                                                                       |
| 2        | **Collector / sync engine** — journeys + sync                              | Headless services inside the tracker process (`--background` for pure headless)                                                          |
| 3        | **Terms & Conditions app** (new) — fetch → show → accept → sync consent | `client.exe --terms` — **own process, own neutral GUI, own mutex**, no tracking services registered, exits when the queue is done |

- **`TermsService`** (new): ScheduleCacheService-style pull loop (login/session-restore wake +
  `ALPHA_TERMS_CHECK_HOURS` timer) hitting `GET /terms-content/active` with Device-token auth;
  SHA-256 content-hash diff of (`heading`, `body`) + `terms_version` — only new or changed terms
  open a pending row; stale pending rows whose feature was deactivated/deleted server-side are
  purged; consent is POSTed and the local row flips to accepted **only on a 2xx**; unacknowledged
  consents retry on every cycle; offline boots serve the cached pending queue with an honest badge.
- **Two-state acceptance** in SQLite `client_terms` (PK `(id, employee_id)`):
  `is_user_accepted` (the user clicked *I agree*) vs `is_accepted` (the server acknowledged).
  The background retry loop re-sends **only user-accepted** rows — it can never accept on the
  user's behalf. A content/version change resets both flags (re-prompt); the migration includes a
  one-time repair re-opening rows the pre-release loop had auto-accepted.
- **Spawn bridge**: the tracker's TermsService detects pending terms and launches the agent
  (`client.exe --terms`); new terms arriving mid-session spawn the agent again. A failed fetch is
  now **loud** (warning logged) and still spawns from the cached queue.
- **`TermsWindow`**: neutral styling (plain light palette, "Terms and Conditions" title — no
  tracker branding, no shell brushes), `Topmost`, sequential one-term-at-a-time flow with progress
  bar + position label, HTML stripped to plain text for display (raw HTML kept in SQLite).
- **Config knobs** (installer-parity §5): `ALPHA_TERMS_ENABLED` (default `true`) +
  `ALPHA_TERMS_CHECK_HOURS` in `.env` / `.env.example` / `AppConfig` / `--print-config`.

---

## Bug Fixes Summary

| # | Issue                                                                                | Root cause                                                                                                                                                                                                      | Fix                                                                                                                                                                 |
| - | ------------------------------------------------------------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1 | Terms visible in SQLite but the GUI showed nothing                                   | `TermsPage` never got its `DataContext` (inherited `MainViewModel`; bindings target `TermsViewModel`)                                                                                                   | Explicit`DataContext="{Binding Terms}"` + `PendingTermsChanged` marshaled through `Dispatcher.UIThread.Post`                                                  |
| 2 | Terms auto-accepted without the user ever seeing them                                | Retry loop re-POSTed every`is_accepted=0` row — pending also meant "not yet agreed"                                                                                                                          | Two-state`is_user_accepted` / `is_accepted`; retry loop re-sends user-accepted rows only; one-time data repair in `MigrateSql`                                |
| 3 | Accepting the 3rd term crashed the modal; after relaunch every accept closed it      | Index/list desync — the pending list shrank after each accept but presentation kept indexing with the stale counter →`ArgumentOutOfRangeException` inside `async void` (terms were also silently skipped) | Queue head always presented (`pending[0]`); progress/position derived from counts                                                                                 |
| 4 | Modal stayed open after the last acceptance                                          | The`Done` event subscription was lost in the move to the standalone agent                                                                                                                                     | `Done` → `Dispatcher.UIThread.Post(close)`                                                                                                                     |
| 5 | Modal not always on top                                                              | Standalone`TermsWindow` never set `Topmost`                                                                                                                                                                 | `Topmost="True"`                                                                                                                                                  |
| 6 | Agent process lingered as a windowless zombie after its window closed                | `ShutdownMode.OnMainWindowClose` never ended the Avalonia lifetime; unbounded `host.StopAsync` hung; empty-queue close-during-init wedged the lifetime                                                      | `Environment.Exit(0)` from the window's `Closed` event (safe — consents flush synchronously at accept) + bounded 5 s `host.StopAsync` on the pre-window path |
| 7 | `POST /terms-content` returned 500 on every create after the first                 | Manually-added live-DB UNIQUE index on`feature_id` collided on the hardcoded empty string                                                                                                                     | Migration`039` re-scopes the index to `feature_id <> ''`                                                                                                        |
| 8 | Client startup crashed with`SQLite Error 1: near "is_user_accepted": syntax error` | Migration comments contained`;` / quotes — `RunMigrationsAsync` splits `MigrateSql` on `;` **before** stripping comments, gluing half a comment onto the `ALTER TABLE`                         | All semicolons/quotes removed from migration comments (trap documented in`StripSqlLineComments`)                                                                  |
| 9 | A failed terms fetch was silent — no spawn, no window, no log                       | `RefreshAsync` swallowed all exceptions and returned without raising `PendingTermsChanged`                                                                                                                  | Loud warning + spawn from the cached pending queue                                                                                                                  |

---

## Verification

- ✅ `go build` / `go vet` clean · ✅ `dotnet build` 0 errors (non-incremental after window-code
  changes — no analyzer blowup) · ✅ `npx tsc --noEmit` clean
- ✅ Live dev-server scenarios: fetch → spawn → sequential acceptance → server ack → auto-close →
  process exit; empty-queue instant exit; mid-session term arrival re-spawn; duplicate-feature and
  manual-term creates return 201 after migration 039.
- ⚠️ **Tiers 2–3 not run**: the scenario matrix is exercised from dev only, and per the
  Installer-Parity Rule this release is **not "done" until verified from an installed build** —
  `config.enc` must be re-baked so the `ALPHA_TERMS_*` knobs ship.

---

## Deploy Sequence

1. **Server first** — applies migrations 036–039 on startup (the boot seeder re-ensures the four
   featured terms idempotently).
2. **Web** — rebuild and deploy (`npm ci` — new TipTap deps).
3. **Client** — ships in the next installer build (the `--terms` agent, `client_terms` schema, and
   the new knobs require a re-baked `config.enc`).

---

## Known Gaps / Follow-ups

- Installed-build ship-test pending (Installer-Parity Rule).
- Wayland has no always-on-top protocol — the agent's topmost is best-effort there
  (environment limitation, not a bug).
- Server-side permission enforcement (RBAC grants) is still not implemented in API middleware.
- No rate limiting on login or sync endpoints.

---

---

# Release Notes — v1.1.5

## Overview

v1.1.5 is a major feature release spanning Time & Attendance, Dynamic RBAC, Employee Journey UX, Hours Insights, Windows lifecycle hardening, and a critical orphan-data fix. The release includes 24 files changed on the server, 18 on the web, and 15 on the client, with migrations 024–035 applied.

---

## 1. Time & Attendance (Phase 1) — Client + Server

**Client**

- New `SystemEventWatcher` (cross-platform `BackgroundService`) emits `session_events` via the existing `IEventRecorder`:
  - **Linux**: D-Bus `UPower` (PrepareForSleep), `login1` (Lock/Unlock), `org.gnome.ScreenSaver` (ActiveChanged).
  - **Windows**: `Microsoft.Win32.SystemEvents` PowerModeChanged + SessionSwitch + SessionEnding.
  - **macOS**: NSWorkspace notifications + CGSessionCopyCurrentDictionary polling.
- New `IdleDetector`: OS idle source (Mutter.IdleMonitor on Wayland, X11 XScreenSaver fallback, Windows GetLastInputInfo) polls every 30 s and emits `idle_start` / `idle_end` markers.
- New `ScheduleCacheService`: pulls `GET /api/v1/schedules/me` on login + every 6 h.
- New `LocalTimeSkewService`: client↔server clock skew from HTTP Date header every 15 min.
- New `AttendanceAggregator`: 5-min daily rollup into `daily_attendance_cache` (arrival/departure/active/idle + present|late|off_shift|unknown).
- New `ShutdownSentinel`: writes `power_off` on SIGTERM / Ctrl+C / Avalonia Exit.
- SQLite WAL mode + `synchronous=NORMAL`; a second ReadOnly connection prevents deadlocks from 6 hosted services.
- New tables: `employee_schedule`, `company_holidays`, `daily_attendance_cache`, `local_time_skew`.
- New env knobs: `ALPHA_TA_ENABLED`, `ALPHA_IDLE_*`, `ALPHA_TA_LOCK_HYSTERESIS_SEC`.

**Server**

- Migration `028`: `shifts` gains `timezone`, `holidays` table, aggregate event fields on `session_events`.
- New endpoints: `GET /api/v1/schedules/me`, `GET /api/v1/holidays`, `POST /api/v1/attendance/rollup`.
- Shift timezone defaults to server local time (`DEFAULT_SHIFT_TIMEZONE` env); attendance late rule uses `firstLocal.After(shiftStart + grace)` in the shift’s own IANA zone.
- `AttendanceAggregator` understands `mon`…`sun` keys, company holidays, nullable no-event arrival, `absent` / `half_day` / `off_shift`, schedule overlap, and the union of idle + screen-lock intervals without double-counting.

---

## 2. Dynamic RBAC — Server-Driven Roles / Modules / Submodules

- Migration `025` adds `roles`, `modules`, `submodules`, and `role_submodule_permissions`.
- `users.role_id` is the sole role source of truth; legacy `role` / `is_company_admin` dropped.
- Go seeder `RBACService.SeedCatalog` re-ensures the module/submodule catalog + grants idempotently on every boot.
- New protected endpoints: `GET /modules` (full module→submodule tree) and `/roles` CRUD.
- `/settings/user-management` rebuilt on live `/users` API with infinite scroll, create/edit/delete dialogs, and role assignment.
- Web `RouteGuard` resolves the current pathname to a module and redirects unauthorized access to `/unauthorized`.
- Semantics: absent/null permissions array = fail-open (pre-RBAC sessions keep working); present array = strict allow-list; `company_admin` always full.

---

## 3. Self-Service Profile + User-Management Edit Flow

- New `/settings/profile` page (visible to every authenticated user, no permission gate):
  - **Account** tiles: user id, employee link, role + system badge, department, shift.
  - **Modules you can access**: per-module `grantedCount / submoduleCount` cards with system-admin "all" override.
  - **Update details**: name/email/password with confirm-password + eye toggles; name/email reverse-sync to the linked employee on save.
- `/settings/user-management` is now full CRUD:
  - Per-row **Edit** button deep-links via `?edit=1&userId=…`.
  - Identity fields (`name`, `email`, `employeeId`) are locked in both create and edit modes.
  - Role dropdown excludes the system `company_admin` role.
  - Password + confirm-password with eye toggles; on save, name/email propagate to the linked employee.
  - The system `company_admin` row hides the Edit button and a deep-link guard refuses the dialog open.

---

## 4. Rotating Refresh Tokens — Web Admin Session Hardening

- Migration `026_refresh_tokens.sql` adds `refresh_tokens` (user_id FK CASCADE, UNIQUE `token_hash` = hex(sha256(raw)), expires_at, revoked_at, deleted_at).
- Raw token is a 32-byte random base64url string stored ONLY as its SHA-256 hash; it travels ONLY in the httpOnly `refresh_token` cookie.
- JWT config split: `JWT_ACCESS_EXPIRY=15m` (web admin), `JWT_REFRESH_EXPIRY=720h` (30 days), `JWT_EMPLOYEE_ACCESS_EXPIRY=24h`.
- New public endpoint **POST /auth/refresh**: validates cookie hash → mints replacement access+refresh pair FIRST → revokes old row AFTER (crash-safe rotation).
- Login sets BOTH cookies; logout REVOKES the refresh row server-side before clearing cookies.
- Web `api.ts` request wrapper catches 401s → single-flight POST /auth/refresh → replays original request once → on failure redirects to `/login`.
- `checkAuth` thunk also tries refresh + re-check once when `/auth/check` answers `{authenticated:false}`.

---

## 5. Employee Journey + Device Specs Modules

- The old `/users/[id]` detail page is replaced by nine pages behind a shared `EmployeePage` shell.
- **Employee Journey**:
  - Session Timeline (`useInfiniteQuery` on `GET /app-sessions`, 30/page, IntersectionObserver sentinel, `FocusTime` fg/bg stacked bar).
  - App Usage (server-side per-app aggregate via `GET /app-sessions/usage`; expanded rows show per-session detail via `GET /app-sessions/usage/sessions`).
  - Web Activity (`GET /app-items?itemType=browser_tab`, infinite scroll, search-engine query grouping with expand/collapse).
  - Screenshots / Location Trail placeholders (Location UI gated behind `LOCATION_UI_ENABLED=false`).
- **Device Specs**:
  - Hardware Overview (specs + storage + network + app_status).
  - Installed Software (Applications/Packages tabs with search, `InventoryTable`).
  - Peripherals (plugged/unplugged cards + `DeviceClassIcon`).
  - Permissions.
- New aggregate endpoint `GET /employees/:id/detail` returns the full machine picture in a single response.
- New shared helpers: `lib/format.ts`, `EmptyState`, `FocusTime`, `DeviceClassIcon`, `EmployeeSelector`.

---

## 6. Hours Insights Redesign & Time Calculation Fix

**Critical bug fixed**: unclosed `app_items` with `opened_at` in the past but `closed_at = NULL` were being bounded by the filter’s `from_ts` (midnight today), inflating Today’s total to 19+ hours. The fix:

- `app_items` are now bounded by their parent `app_sessions.ended_at` / `last_sync_at` / `last_activity_at` when `closed_at` is NULL.
- `site_usage` filters on `item_type = 'browser_tab'` only (eliminates duplicate `browser_navigation` counts).
- Total active time is derived from true computer/application runtime, not by adding site durations on top of browser runtime.

**New dual-view UX** (URL-synced via `?view=productivity` or `?view=applications`):

- **"From Productivity"**: stacked-area chart (Productive / Neutral / Unproductive) + breakdown table with productivity type, category, duration, and focus score.
- **"From Application Individually"**: per-application stacked-area chart (top apps sorted by total duration, hourly buckets for ≤3 days, daily for longer) + ranked breakdown table with duration, percentage bar, session count, category, and productivity tag.

---

## 7. Chrome Multi-Tab Duration Fix (3-Layer)

**Root cause**: the a11y reader returned a fresh `WindowKey` per tab; the old `ResolveWindowKey` only collapsed tabs when titles matched; transient "Loading…" states slipped through and opened a session per tab. The web page then summed per-row `endedAt - startedAt`.

**Fix**:

- **Layer 1 — client** (`AccessibilityBrowserTracker.ResolveWindowKey`): two new collapse rules — (a) exact URL match (stronger than title), and (b) fresh-key recency window of `poll×2` seconds that attaches a new same-PID key to the FRESHEST tracked window.
- **Layer 2 — web** (`/employee-journey/apps`): the `useMemo` now recomputes duration as `lastClosedAt - firstOpenedAt` regardless of what the server returns.
- **Layer 3 — server**: new `GET /api/v1/app-sessions/usage` returns one row per `(appDisplayName, processName)` with `MIN(started_at)`, `MAX(COALESCE(ended_at, last_sync_at, started_at))`, `SUM(...)` for `totalDurationSeconds`. Composite index `idx_app_sessions_employee_started_name` covers the WHERE + GROUP BY.

---

## 8. 3-State App-Session Lifecycle (ACTIVE / STALE / CLOSED)

- Migration `031` adds `status TEXT NOT NULL DEFAULT 'ACTIVE'`, `last_activity_at TIMESTAMPTZ`, `last_sync_at TIMESTAMPTZ`, plus 2 indexes.
- **State machine** (`session_lifecycle_sweep.go`, every 1 min):
  - `ACTIVE → STALE` when `last_sync_at < NOW() - SESSION_STALE_AFTER_MINUTES` (default 10).
  - `STALE → CLOSED` when `last_sync_at < NOW() - SESSION_CLOSE_AFTER_HOURS` (default 24).
  - Only CLOSED is terminal; the sweeper freezes `ended_at = COALESCE(last_activity_at, last_sync_at, started_at)` at the moment of CLOSE.
- **Upsert recovery**: when a live client re-uploads any row with `ended_at=NULL`, the `ON CONFLICT` CASE flips STALE/CLOSED back to ACTIVE and clears the premature server-side `ended_at`.
- When the client supplies a non-NULL `ended_at`, the row stays CLOSED with the new value (client’s decision wins).
- **Per-row stagnation sweep** (step 4 of `session_lifecycle_sweep.go`): advances any OFFLINE/STALE row whose OWN `last_sync_at` is ≥ CLOSE_AFTER old — even on a machine that is still alive — so a single abandoned session can never be stranded forever.
- **Migrations**: `034` aligns historical rows that already carried `ended_at` to `CLOSED`; `035` freezes stranded OFFLINE/STALE sessions (`status='CLOSED'`, `ended_at=COALESCE(last_activity_at,last_sync_at,started_at)`) older than 24h.

---

## 9. Orphan App-Items Fix (Bug #9)

**Root cause 1**: the orphan preflight returned only a survivor COUNT and the client’s `SendAsync` never read the response body, so the client marked ALL sent ids `is_synced=1` — including rows the server had refused — permanently losing them.

**Fix**:

- `SyncBatchResponse` now carries `rejectedIds []string` (omitempty: the other 11 sync endpoints’ wire format is untouched; absent field = old mark-all behavior, so new-client↔old-server stays compatible).
- `BulkInsertAppItems` returns the refused item ids.
- `SyncAppItems` surfaces them.
- `SyncService.DrainTableAsync` marks ONLY accepted rows (case-insensitive parse; absent/unparseable body ⇒ mark all, idempotent upserts make that safe).

**Root cause 2**: `app_items` INSERT omitted `synced_at` (migration 008 gave it no DEFAULT) so every first insert stored NULL.

**Fix**: All five sync writers that omitted the column (`app_items`, `hardware_devices`, `permission_status`, `storage_devices`, `location_samples`) now stamp `synced_at = NOW()` inline on INSERT. Migration `033` backfills historical NULLs from `created_at`.

**Poison-row quarantine**: rows refused 3 consecutive passes are quarantined **in-memory** — skipped by the drain until process restart. The SQLite row stays `is_synced=0` (a refused row must never masquerade as synced; retention only purges synced rows, so quarantined rows persist as diagnostic evidence; restart resets the counter and gives self-heal a fresh chance).

---

## 10. Server-Side App-Session Accuracy + Web UX

- `GET /api/v1/app-sessions/usage` (server-side per-app aggregate).
- `GET /api/v1/app-sessions/usage/sessions` (paginated per-app session list for chevron expand).
- Web `/employee-journey/apps` columns: **Application | Sessions | Duration | Last Active | Status**.
- Status badge driven by `hasOpenSession` (green "Running" only when truly ACTIVE).
- Expanded-row Closed cell shows `lastSyncAt` for STALE/OFFLINE.
- Edge webviews folded into the browser group (`msedgewebview2` → `msedge`) so Windows Edge renders as one aggregate.
- Web `/employee-journey/timeline`: STALE/OFFLINE rows show `lastSyncAt` in the Closed column instead of an em-dash.
- Web `/employee-journey/web`: an open tab’s duration now uses `now − openedAt` instead of `0`.
- Search-engine queries (Google, Bing, Yahoo, DuckDuckGo) parsed from URL query params and grouped by query text with expand/collapse dropdown arrows.

---

## 11. Windows Power On/Off + Shutdown Finalizer

- `SystemEventWatcher` → `SessionEnding` now calls `CloseAllOpenSessionsOnShutdownAsync()` (`CloseSessionsAndAppItemsAsync`) so every open session’s `ended_at` is stamped (with `is_synced=0`) in SQLite before the process is killed.
- Linux already had the equivalent `PrepareForShutdown` path.
- Windows `SessionEnding` with `SessionEndReasons.SystemShutdown` is now subscribed (fire-and-forget, mirrors Linux’s synchronous handler); `Logoff` is intentionally skipped to avoid duplicating the existing `SessionSwitch` → `os_logout` path.

---

## 12. URL-Synced Filters + No-Clear Date Modal

- New generic `useUrlQueryState<T>` hook syncs any typed filter record to the browser URL (debounced, preserves unrelated keys, strips empty values).
- New `useUrlActivityFilter` bridges `ActivityFilter` to compact URL keys (`?q=&preset=today|7d|all|custom&from=YYYY-MM-DD&to=YYYY-MM-DD`).
- Wired into 13 pages: employees (search + department), shifts (search), timesheets (employee + from + to), attendance (date + status), employee-journey/timeline (employeeId via shell), employee-journey/apps + web (search + date preset), device-specs/software (tab + per-tab search), configuration/apps + websites (search + type + category + status), logs/comprehensive (employee + search + page), settings/user-management (search).
- `ActivityFilters` lost its search Clear (X) button — clearing now means select "All time" or empty-the-input.
- Every page using `useSearchParams` is now wrapped in `<Suspense>`.

---

## 13. Attendance Late/Present Timezone Fix

- Migration `028` defaulted `shifts.timezone` to `UTC` while session events and admins sit in local time (e.g. PKT +05) — a 9:08 arrival looked Present in the UI but was compared against 09:00 UTC on the server.
- New `DEFAULT_SHIFT_TIMEZONE` env (`server/.env.example`): on boot, `ShiftService.ApplyDefaultTimezone` rewrites every active shift still on `UTC`.
- Web `/shifts` new-shift form defaults to `Intl.DateTimeFormat().resolvedOptions().timeZone`.
- Web `/attendance` + `/timesheets` format first/last active via `formatDateTimeInZone(iso, record.timezone)`.

---

## 14. Configuration Pages — Applications + Websites Classification

- The old static `apps/page.tsx` is deleted.
- Migration `023` adds `monitoring_types` (seeded Productive / Unproductive / Neutral), `monitoring_categories`, classification columns on `installed_applications`, and `monitoring_sites` (auto-synced from distinct `app_items.domain`).
- New `MonitoringRepo` / `MonitoringService` / `MonitoringHandler` + protected `/api/v1/monitoring/{types,categories,apps,websites}` routes.
- Web: **Applications** and **Websites** use the shared `ClassifiedItemsTable` component (infinite scroll, debounced search, Type/Category/unclassified filters, inline per-row selects).
- **Categories & Types** is a two-tab CRUD (cards + dialogs, color picker for types, kind select for categories).

---

## 15. Employee List — Server-Side Infinite Scroll + Excel Import/Export

- Employees page replaced Previous/Next buttons with `useInfiniteQuery` + IntersectionObserver sentinel (10/page).
- **Export** downloads `employees-<date>.xlsx` (Employee ID / Name / Email / Department / Shift).
- **Import** reads `.xlsx/.xls/.csv` client-side, normalizes rows, and posts to `POST /api/v1/employees/import`. Server imports in ONE transaction: departments are get-or-created by name, employees are upserted by exact `employee_id`. Per-row outcomes returned to the UI.

---

## 16. Browser Registry — All Hardcoded Names Removed

- New `IBrowserRegistry` interface + `BrowserRegistry` implementation source browser classification from `installed_applications.is_browser` (set via `.desktop` `Categories=WebBrowser`, Windows registry `URLAssociations`, macOS `CFBundleURLSchemes`) and caches for 5 minutes.
- Every reader, tracker, history fallback, session label resolver, and the main collector loop now resolves browsers through the registry instead of hardcoded name lists.
- `StripBrowserSuffix` is now dynamic — it strips ` - {appDisplayName}` using the registry’s display name.
- The Linux embedded Python probe’s `BROWSER_HINTS` tuple is deleted; the probe uses `_browser_exes` (already built from `.desktop` files) for both AT-SPI browser detection and Firefox sessionstore discovery.

---

## 17. Embedded-Webview Journeys — Zero Hardcoded App Names

- A window carries web content iff its accessibility tree contains a `DOCUMENT_WEB` node (AT-SPI role 95) whose `DocURL` is an http(s) URL.
- App chrome excludes itself by scheme (`vscode-webview://`, `file://`, `about:` never match).
- The host app (process name, e.g. `code`) rides in metadata `"source":"webview"` so the web dashboard shows it as the source badge.
- The tracker **caches webview windows and re-emits them every poll** so sessions stay stable and the missing-window close never fires between scans.

---

## 18. Windows Installed-Software + Hardware Detection Overhaul

- **Start Menu .lnk** is the Windows analog of Linux `.desktop`; `InstalledAppDetector.ScanStartMenuShortcuts` enumerates .lnk files (user + common) via WScript.Shell and resolves each target exe.
- **Registry scan** reads all 3 Uninstall nodes (HKLM + WOW6432Node + HKCU); `PackageDetector.ScanRegistrySoftware` captures dev runtimes/tools and drivers.
- **winget is the source of truth** for packages; MSI-installed dev tools + drivers now appear in `installed_packages`.
- **PE Subsystem** (`IMAGE_SUBSYSTEM_WINDOWS_GUI` vs `IMAGE_SUBSYSTEM_WINDOWS_CUI`) decides app vs package structurally — no name lists.
- **C:\Windows tree** exclusion: anything under the Windows dir is an OS component.
- All Windows name blocklists (`WindowsNonAppProcesses`, `JunkRegistryNamePatterns`, `IsDevToolRegistryName`, `ClassifyRegistryPackage`, `CliKnownPackages`) are DELETED.
- `HardwareDeviceWatcherService` is no longer Linux-only: new Windows PnP implementation polls `Get-CimInstance Win32_PnPEntity` every 30 s.

---

## 19. Sync Engine Decoupled — Collection Never Blocks on the Network

- New dedicated `SyncService` (BackgroundService) drains unsent SQLite rows on its own loop.
- Chunks bounded by BOTH row count (`ALPHA_SYNC_MAX_ROWS`, 1000) and serialized payload bytes (`ALPHA_SYNC_MAX_BYTES`, ~1 MB).
- ~150 ms politeness pause between chunks; 5-min per-pass budget (`ALPHA_SYNC_MAX_DURATION_SEC`).
- Exponential backoff on failure (5 s → 10 s → … → `ALPHA_SYNC_BACKOFF_MAX_SEC` 5 min).
- Request bodies are gzip-compressed; server side adds `middleware.BodyLimit("20M")` + `middleware.Decompress()`.
- SQLite mark-sent is now batched (`UPDATE … WHERE id IN (…)`, 400 ids/statement).
- New env knobs: `ALPHA_SYNC_INTERVAL_SEC / MAX_ROWS / MAX_BYTES / CHUNK_DELAY_MS / MAX_DURATION_SEC / BACKOFF_MAX_SEC / COMPRESSION`.

---

## 20. Headless Boot + Six-Page GUI + Runtime Branding

- Boot is now fully headless (`--background`): tracking starts at boot via `ExecuteAsync` calling `StartTracking()` right after `RefreshEmployeeInfo`.
- The GUI is strictly login-only — it cannot start or stop tracking.
- GUI rebuilt as six pages under `client/Views/Pages/`: Splash, Login, PermissionSetup, Dashboard, System Specs, Installed Applications.
- `Core/AppInfo.cs` makes every visible brand string and version derive from `APP_IDENTIFIERS` + `VERSION` at runtime — no XAML or C# literal names anywhere.
- New `SingleInstanceService` named-pipe activation: a second user launch signals the running instance to raise its window; `--background`/`--minimized` relaunches exit quietly.

---

## 21. Self-Update from GitHub Releases

- New `AppUpdateService` (ObservableObject + IHostedService singleton) checks `https://api.github.com/repos/{repo}/releases/latest`, normalizes the tag, picks the platform installer asset, and compares against `AppInfo.Version`.
- Quiet auto-check loop every 30 min (configurable via `ALPHA_UPDATE_AUTO_CHECK_HOURS`, default 24 h).
- With `ALPHA_UPDATE_AUTO_INSTALL=true` (default) it auto-downloads and installs with no click.
- GUI top bar gains **Check updates**, **Update to vX.Y.Z**, **Restart to apply** buttons + update banner.
- Config: new `ALPHA_UPDATE_REPO / ENABLED / AUTO_CHECK_HOURS / AUTO_INSTALL` keys in `.env` + `.env.example` + `AppConfig`.

---

## 22. Client Retention + 4 New Server Sync Surfaces

- The client now DELETES synced data the server already has: `app_items` / `app_sessions` older than `ALPHA_SYNC_RETENTION_HOURS` (24 h default; OPEN sessions are NEVER deleted).
- Four previously local-only tables now sync to the server (migration `017`): `app_status`, `hardware_devices`, `permission_status`, `storage_devices`.
- `permission_status` duplication fixed: keyed on the stable `"{platform}_{method}"` with `ON CONFLICT(check_id) DO UPDATE` (one row per permission method).
- Instant sync on login: `SyncService.RequestImmediateSync()` wakes the drain pass so the full machine picture lands on the server the moment an employee logs in.

---

## 23. Instant Sync on Login

- `SyncService` gained a wake-up signal (`SemaphoreSlim(0,1)` released by the caller; the inter-pass wait is now `WaitAsync(wait, ct)` so a release ends the wait at once).
- `MainViewModel` injects the singleton `SyncService` and fires it in BOTH login paths: `LoginAsync` and `InitializeAsync` (session restore on launch).
- The instant pass drains every unsent table — `device_hardware_info`, `installed_applications`, `installed_packages`, `network_info`, `storage_devices`, `hardware_devices`, `session_events`, `permission_status`, `app_status`, `app_sessions`, `app_items`.

---

## 24. Foreground / Background Focus Time Per Session

- The collector already knew the OS foreground PID each cycle but never persisted it.
- Client: new `foreground_seconds` / `background_seconds` on SQLite `app_sessions` (idempotent MigrateSql ALTERs), accumulated per cycle for every open session, flushed every 10 cycles + on close with `is_synced=0`.
- Server: migration `020` adds the two columns, model/DTO/`SyncAppSessions`/`BulkInsertAppSessions` upsert + `ListAppSessions` SELECT.
- Web: Activity tab rebuilt with server-side pagination + infinite scroll and columns **Application, Status, Opened, Closed, Duration, Foreground/Background** (green/gray stacked bar + `fg / bg` readout).

---

## 25. Employee Disconnect Removed

- The **Disconnect** button was removed from the client nav rail.
- `LogoutCommand` / `LogoutAsync` and `LogCollectorService.StopTracking()` are gone from the client.
- Server endpoint `POST /api/v1/auth/employee-disconnect` (handler + DTO + route) was deleted.
- Employees can no longer disconnect themselves; the client tracks until the process stops.

---

## 26. File Explorer Journey Tracking

- Three watchers: `ATSPIEventWatcher` (D-Bus AT-SPI focus/window events), `FileSystemEventWatcher` (FileSystemWatcher on 7 user directories), `RecentFilesWatcher` (XBEL monitor).
- `EventCoordinator` deduplicates (3 s), correlates (500 ms), normalizes raw→business events.
- `JourneyEngine` resolves `AppSession`, creates `AppItem` rows with 9 journey fields.
- Windows: new `WindowsExplorerWatcher` polls the shell’s registry via Shell COM (`Shell.Application → Windows()`) and reads each Explorer window’s `file://` `LocationURL`.

---

## 27. Browser Journey Overhaul — All Browsers + Incognito

- `LinuxAtSpiBrowserReader` rewritten to merge THREE sources — AT-SPI (Chrome & co.), Firefox sessionstore (`recovery.jsonlz4` / `sessionstore.jsonlz4`, decompressed by an embedded pure-python LZ4 block decoder), and the WM window list.
- `AccessibilityBrowserTracker.ResolveWindowKey` now re-keys by page-title match or single-window count guard — stealing is impossible.
- `ALPHA_BROWSER_CAPTURE_INCOGNITO=true` (was false).
- Main `LogCollectorService` skips browsers when browser tracking is enabled (no more double sessions per window).
- New `BrowserHistoryReader` reads each browser’s own profile history database while the browser runs (Chrome `History`, Firefox `places.sqlite`) — torn snapshots can’t corrupt anything.
- URL-less title changes no longer rotate tabs until history catches up.
- Per-page `browser_tab` records: each page visit CLOSES the previous tab root and OPENS a fresh `browser_tab` record.

---

## 28. Private/Incognito Window URLs Captured

- Firefox: `LinuxAtSpiBrowserReader` now reads the `DOCUMENT_WEB` (role 95) node’s `DocURL` attribute — the EXACT private-window URL.
- Chrome 136+: the a11y tree has ZERO children below the window frame in basic mode; the tree is only built when Chrome is launched with `--force-renderer-accessibility`. Applied a user-level `~/.local/share/applications/google-chrome.desktop` with the flag on ALL Exec lines.

---

## 29. Windows Auto-Update Fix

- `installer-windows.iss` `KillRunningInstance` ran `taskkill /F /IM client.exe /T` — the `/T` is a TREE-kill. The self-updater launches the installer as a DESCENDANT of the running app, so that tree-kill terminated the updater’s OWN cmd script AND the installer itself.
- Fix: `/T` removed from `KillRunningInstance`; `AppUpdateService.InstallAsync` (Windows) rewritten so the updater does NOT depend on the installer killing the app — it writes a detached `.cmd` to `%TEMP%` that polls `tasklist` until the app exits, launches the silent installer, waits for it to finish, and relaunches `{exe} --restart`.

---

## 30. Downloaded Installers Cleanup

- `CleanupUpdatesDirectoryAsync` force-deletes every file in `updates/` then the folder itself.
- Wired into three places: Linux (after successful `pkexec dpkg -i`), Windows (inside the detached PowerShell update script), and startup sweep (prunes the folder on every boot).

---

## 31. Focus Totals Fix

- The periodic flush was OVERWRITING the DB row with the in-memory delta instead of accumulating.
- Fix: the flush is now ADDITIVE (`foreground_seconds = COALESCE(foreground_seconds, 0) + $foreground_seconds`).
- The main loop now calls `FlushSessionFocusAsync` every 2 cycles (~60 s at the default 30 s interval).
- `SyncService` failure backoff is now **capped at 60 s**.

---

## 32. Foreground/Background Focus Time Fix

- **Cause 1 — Linux/Wayland foreground detection was completely dead**: at-spi2-core ≥ 2.50 returns a PACKED 64-bit bitmask instead of a list of state ids. `IsAtSpiActiveState` / `is_active_window` decode BOTH formats and test ACTIVE(1)/FOCUSED(12).
- **Cause 2 — browser sessions never earned focus time at all**: Browsers are owned by `AccessibilityBrowserTracker` (excluded from the main loop’s `resolvedLogs`), and the tracker never wrote foreground/background. Fix: new `AccessibilitySnapshot.IsActive` set per-platform and the tracker now accumulates the poll interval into `foreground_seconds` / `background_seconds` per open window every poll.

---

## 33. Duplicate Linux Lock/Unlock Events Fixed

- Root cause: a manual desktop launch and the systemd user service ran concurrently against the same SQLite database.
- `SingleInstanceService.MutexName` now uses .NET’s machine-wide `Global\` namespace with `AppInfo.AppMutex` from `APP_IDENTIFIERS`.
- `Program.cs` consumes it for all launch modes.

---

## 34. LocalStorage Mock Data Removed

- `web/src/lib/store.ts` (the 328-line mock factory) is deleted, as is the legacy localStorage permission-matrix editor.
- Seven no-backend pages became honest empty states.
- `/dashboard` is fully live-API: Total Employees (+ tracked/untracked split), Departments count, App Sessions ·24h, Web Pages ·24h.
- `app/page.tsx` runs a one-time sweep that purges every orphaned `alpha_ai_tracker_*` key from localStorage.

---

## 35. Employee List Infinite Scroll + Excel Import/Export

- Employees page replaced Previous/Next buttons with `useInfiniteQuery` + IntersectionObserver sentinel (10/page).
- **Export** downloads `employees-<date>.xlsx`.
- **Import** reads `.xlsx/.xls/.csv` client-side, normalizes rows, and posts to `POST /api/v1/employees/import`. Server imports in ONE transaction.

---

## 36. Web UI for OFFLINE Filter + Productivity Filters

- Web UI for OFFLINE filter added.
- New filter status: `all`, `classified`, `unclassified`.
- Employee selection + search/date filters now persist together in the URL (no more deselection on filter change).
- Attendance page deep-links: clicking an employee name on `/attendance` deep-links to `/timesheets?employeeId=<uuid>&from=<date>&to=<date>`.

---

## 37. Server-Side Date Filters on App Sessions + App Items

- `GET /app-sessions` and `GET /app-items` now accept `dateFrom`/`dateTo` (RFC3339 or date-only).
- App-items search also matches `url`/`domain`.

---

## 38. Client Version Column on Employees Page

- Employees page now shows a **Version** column sourced from `employee_devices.client_version`.
- Added `clientVersion` to `EmployeeResponse` DTO + employee repo/service.

---

## 39. Histogram Shows Individual Application Usage

- When **Application** mode is selected in Hours Insights, the histogram now shows per-application usage time/hour.
- Most-usage applications are sorted to the top.

---

## 40. Total Session Time Moved to Server Side

- `GET /app-sessions/usage` now computes `totalDurationSeconds` server-side.
- Web `/employee-journey/apps` consumes the server-computed value directly.

---

## 41. Critical Productivity/Unproductivity Fix

- Bounded unclosed `app_items` to their parent session’s end time.
- Corrected `site_usage` to filter on `item_type = 'browser_tab'` only.
- Total active time is derived from true computer/application runtime, not by adding site visit durations on top of browser application durations.

---

## 42. Windows Power On/Off Perfectly Running

- `SystemEventWatcher.Windows.cs` now subscribes to `SystemEvents.SessionEnding` (fire-and-forget).
- `SessionEnding` with `SessionEndReasons.SystemShutdown` calls `CloseAllOpenSessionsOnShutdownAsync()` so every open session’s `ended_at` is stamped before the process is killed.
- Linux already had the equivalent `PrepareForShutdown` path.

---

## 43. Snap Firefox AppArmor AT-SPI Fix

- New `publish/firefox-a11y-apparmor.sh` loads a surgical copy of the snap profile adding ONE rule — `dbus (receive)` — that lets the AT-SPI bridge call INTO Firefox while the sandbox stays fully enforcing.
- Installs a systemd oneshot (`alpha-ai-firefox-a11y.service`, enabled) that re-applies the override at boot and after `snap refresh firefox`.

---

## 44. Structural Process Name Resolution (Flatpak / snap)

- `resolve_app_name(pid)` in the Python probe checks `FLATPAK_ID` in `/proc/<pid>/environ` and `snap` path in `/proc/<pid>/exe`.
- Falls back to `/proc/comm` for native apps.
- The resolved name is used for browser detection: new structural browser detection scans `.desktop` files for `Categories=WebBrowser`.

---

## 45. Download Installer Cleanup

- `CleanupUpdatesDirectoryAsync` force-deletes every file in `updates/` then the folder itself.
- Wired into Linux (after successful install), Windows (inside detached update script), and startup sweep.

---

## 46. Headless Boot — Tracking Starts Without GUI

- `ExecuteAsync` now calls `StartTracking()` right after `RefreshEmployeeInfo` when persisted employee credentials exist.
- The GUI is strictly login-only — opening/closing it never starts or stops tracking.
- Background mode now waits with `host.WaitForShutdownAsync()` instead of an uncancellable infinite delay.

---

## 47. Single-Instance Activation + Tray UX

- A second launch now restores the running window (`client/SingleInstance.cs` named-event signaling).
- The tray menu gains **Quit**.
- Tray-less desktops (no StatusNotifierWatcher) quit on window-close instead of stranding an invisible process.

---

## 48. App Display Name from Installed Applications

- `AppDisplayName` now uses the real app name from `installed_applications.app_name` (e.g., "Visual Studio Code"), not the process name ("code") or window title.

---

## 49. Windows Journey Fixes — Real Incognito Flag + Junk-Data Sweep

- `WindowsUiaBrowserReader` now also scans the window’s accessibility tree for the dedicated "Incognito"/"InPrivate" toggle button Chrome and Edge expose.
- `SearchApp` was treated as a BROWSER because the `"arc"` hint substring-matched `SearchApp` — `IsBrowserProcess` now matches hints as STANDALONE WORDS only.
- Runtime auto-registration disabled on Windows — the factory for every junk `installed_applications` row.
- New boot sweep `CleanupWindowsJunkSessionsAsync` deletes empty-desktop_id app rows and closes their sessions.

---

## 50. Cross-Platform Analyzer Safety

- All platform-specific method bodies guard with `OperatingSystem.IsWindows/Linux/MacOS()` early-return.
- Narrowly scoped `#pragma warning disable CA1416` allowed only in the platform file and only when every entry point has the explicit runtime guard.

---

## 51. Branding Single-Source Rule

- `Core/AppInfo.cs` reads `APP_IDENTIFIERS` + `VERSION` at runtime — no XAML or C# literal names anywhere.
- Editing either file re-brands both the app and the installers.

---

## 52. Installer Parity Rule (Codified)

- Every feature/modification must ALSO be wired into the installer functionality and verified from an installed build.
- `dotnet run` success is NOT a valid release test.

---

## Bug Fixes Summary

| #  | Issue                                        | Fix                                                                                         |
| -- | -------------------------------------------- | ------------------------------------------------------------------------------------------- |
| 1  | Orphan`app_items` silently lost            | Client reads`rejectedIds` + `missingSessionIds`; server preflights parent session IDs   |
| 2  | Chrome multi-tab duration wrong              | 3-layer fix: client collapse rules, server usage aggregate, web`lastClosed - firstOpened` |
| 3  | OFFLINE/STALE sessions labeled "Running"     | `has_open_session = BOOL_OR(status='ACTIVE' AND ended_at IS NULL)`                        |
| 4  | Windows shutdown never emitted`power_off`  | `SessionEnding` → `CloseAllOpenSessionsOnShutdownAsync()`                              |
| 5  | Focus totals frozen at 0                     | Additive flush SQL; foreground/background now accumulated per poll                          |
| 6  | App-session tracking not starting headlessly | `ExecuteAsync` calls `StartTracking()` after `RefreshEmployeeInfo`                    |
| 7  | Journey noise flood (AppData churn)          | `FileSystemEventWatcher` excludes appdata/programdata/program files trees                 |
| 8  | Duplicate Linux lock/unlock events           | Machine-wide`Global\` mutex                                                               |
| 9  | Unclosed sessions duration runaway           | Bounded to parent session`ended_at` / `last_sync_at` / `last_activity_at`             |
| 10 | Windows software detection hardcoded         | PE Subsystem + C:\Windows tree + registry flags + winget                                    |
| 11 | Windows installer tree-kills itself          | `/T` removed from `KillRunningInstance`; detached `.cmd` waits then installs          |
| 12 | Stale installers linger in`updates/`       | `CleanupUpdatesDirectoryAsync` after install + startup sweep                              |
| 13 | Snap Firefox invisible to AT-SPI             | Surgical AppArmor override                                                                  |
| 14 | Flatpak/snap browsers show proxy name        | `resolve_app_name(pid)` walks PPID chain + checks `FLATPAK_ID` / `snap` path          |
| 15 | Employee disconnect breaks tracking          | Disconnect button + endpoint removed; client tracks until process stops                     |
| 16 | Attendance late rule in wrong timezone       | Shift timezone defaults to server local time; web formats in shift’s IANA zone             |
| 17 | URL-synced filters lose employee selection   | Unified URL state via`useUrlQueryState`                                                   |
| 18 | Productivity total includes browser runtime  | Site usage bounded by parent session;`item_type = 'browser_tab'` only                     |
| 19 | Windows inventory shows CLI tools as apps    | PE Subsystem gate + C:\Windows tree + Start Menu`.lnk` presence                           |
| 20 | `dpkg-query` startup noise                 | Format string double-quoted so tab/newline escapes reach dpkg-query as one argument         |

---

## Verification

- `go build` / `go vet` clean
- `npx tsc --noEmit` clean
- `next build` passes
- `dotnet build` 0 warnings / 0 errors

---

## Deploy Sequence

1. **Server first** — applies migrations 024–035 on startup.
2. **Web** — rebuild and deploy.
3. **Client** — ships in the next installer build (Windows shutdown path, AppArmor snap override, and headless boot require a new installer).

---

## Known Gaps / Follow-ups

- Grafana dashboard/alert provisioning is out-of-repo; the SQL alerts above are the runbook target.
- A Windows power-off smoke test (real shutdown) is the remaining client verification.
- Server-side permission enforcement (RBAC grants) is still not implemented in API middleware.
- No rate limiting on login or sync endpoints.
- No Redis fallback for employee secret generation/validation.
