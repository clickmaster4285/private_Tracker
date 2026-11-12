# Always-on Windows tracker + default-deny lockdown

> **Status:** P0–P3 source complete. **Active:** installed Lab 3 (AppLocker + delete-block ACL) on Windows installer **1.3.6+** (PF(x86) prefix-boundary fix).
> **Written:** 2026-11-05
> **Updated:** 2026-11-11 — AppLocker match PF(x86) before PF + dir boundary (no `%PROGRAMFILES% (x86)\…`); fingerprint `v6`; Lab §H 13b. Earlier same day: `%PROGRAMFILES%` for both PF roots; Edge/Chrome `Application\*` widen. Prior 2026-11-10: delete-block ACL; Settings preset. Prior 2026-11-06: AppLocker Exe+Appx + AppIDSvc + Taskmgr verify.
> **Owns:** delete the tracking-mode feature from server, client, and web, a Windows default-deny allowlist (network, web, applications), and the Terms & Conditions feature.
> **Does not own:** Linux or macOS enforcement, or moving collectors, login, browser reading, or live capture into `AlphaAITracker.Service`.

Execute with `@prompt.md`. Read `AGENTS.md` before each phase. Ship one phase at a time. A phase is done only when its checklist, its lab, and the documentation handoff in `@prompt.md` are done.

---

## 1. Locked decisions

1. **Tracking mode is deleted, not frozen.** There is no Fixed mode, no Roaming mode, no org default, no per-machine override, and no Start/Stop. Collectors run whenever the employee is logged in. The employee has no control that pauses them.
2. **This plan is Windows-only.** Lockdown (firewall, browser policy, application control) ships in `AlphaAITracker.Service` on Windows. Do not add a Linux nftables phase, a macOS phase, or Linux/macOS labs. Shared client code that only exists for tracking mode is deleted, so a Linux build of the same source loses Start/Stop too. Linux and macOS keep today's collectors and today's DLP. They do not gain lockdown.
3. **Two Windows processes stay.** `AlphaAITracker.Service` (LocalSystem, Session 0) keeps enforcement, policy, updates, ACL, and the watchdog. `client.exe` stays in the interactive user session for login, foreground apps, browser URLs, idle, and screen capture. `UserSessionProcessLauncher` exists because a process started inside the service never sees the desktop. Folding those features into the service would blank them and would put employee activity in a LocalSystem process.
4. **New installs fail closed (web + apps).** After login, general web **and** non-bootstrap applications are blocked with **no separate Publish click required** for draft edits that auto-publish. Only bootstrap + platform destinations / shell survival stay open. Admin adds hosts on `/allowlists/web` and apps on `/allowlists/apps`.
5. **Upgrade also flips to fail-closed.** Agents treat missing/`true` lockdown as ON; only published `lockdown: false` opts out. Migration **061** marks historical snapshots lockdown ON so a re-pull does not re-open the fleet. The same `lockdown: true` flag turns on **both** network deny and AppLocker EXE enforce.
6. **One published bundle for the fleet.** Allowlists are compiled at Publish into the existing immutable `dlp_policy_versions` snapshot (`DLP Policy Version Rule`). Clients keep `GET /dlp/policy?sinceVersion=`. Do not mint a bundle per PC.
7. **Agent identity is publisher, path, or hash — never a display name.** Admin UX may pick catalog `app_name` from `installed_applications`; Publish expands that row into concrete path / publisher / SHA rules. The service never matches on display name. No new hardcoded software-name allow lists beyond the baked survival bootstrap (§4 bootstrap A).
8. **Uninstall clears enforcement.** Protected uninstall and teardown must remove firewall, DNS, browser policy, AppLocker, and delete-block ACL artifacts. A removed product must not leave the PC locked.
9. **Alerts are aggregated.** A default-deny filter must not write one unsynced alert per blocked connection. Unsynced alerts are still deleted only when `acceptedIds` lists them.
10. **Terms & Conditions is gone.** No settings page, no client acceptance window, no consent check on live stream or any other page. Migration **057** drops `terms_consent` and `terms_content`. Tracking-mode deletion uses migration **058**.
11. **App survival bootstrap A.** Always allow only this product (service + tracker install dir) + Windows sign-in / Explorer / Start / minimal shell binaries needed to stay logged in. Calculator, Task Manager, Notepad, Settings, browsers, Office, etc. stay **blocked until** allowed on `/allowlists/apps`.
12. **App enforce-now.** No audit-first week. After DeviceAuth + published `lockdown: true`, non-allowed programs do not start (AppLocker Executable default-deny).
13. **Allowlists auto-publish.** Create / update / status / delete on allowlist pages (and the web default-deny switch) call `POST /dlp/rules/publish` immediately. No separate Publish button on `/allowlists/*`.

## 2. Out of scope

- Linux and macOS lockdown, labs, and installers. No nftables default-deny and no macOS application control in this plan.
- Keeping a "Fixed" tracking mode, a mode badge, or a mode API that always returns `fixed`.
- Roaming laptops, hotel captive portals, and employee pause.
- Running UI Automation, GDI/DXGI capture, or the Avalonia login window inside Session 0.
- Per-employee policy documents.
- Hard-blocking URL paths (hosts and DNS cannot do that today; paths stay match metadata).
- Replacing Windows with a custom shell.
- Blocking OS boot components, the login screen, Windows Update infrastructure, Explorer, or Start (bootstrap A). Do **not** block those for "stricter" defaults.
- Matching applications by display name on the agent.

## 3. Current baseline

| Surface | Today |
| --- | --- |
| Tracking mode | Deleted (migration **058**). Collectors always run while logged in. |
| DLP web | Fail-closed network lockdown (059–061): `AlphaAI-DLP-LOCK-*` + hosts. Explicit web-blocker deny list still subtracts. Auto-publish on allowlist edits. |
| Apps (admin) | Draft `dlp_lockdown_allow_apps` (publisher/product/sha + `enabled`); Publish → `allow.apps`; service stores `policy_allow_apps`. **No OS enforce yet** — P3 adds AppLocker. |
| Apps (inventory) | `installed_applications` catalog + `employee_installed_applications` (publisher, install_path). Configuration `/configuration/apps` classifies the catalog. |
| Policy pull | `GET /api/v1/dlp/policy?sinceVersion=` (DeviceAuth). Bundle: `policyVersion`, `lockdown`, `platformAllow`, `allow.hosts`, `allow.apps`, `rules[]`, `shadowDeny`. |
| Split | Service = policy, USB, domain block, lockdown network, silent update, watchdog. User process = collectors and GUI. |

## 4. Target

```
Admin edits allowlists (auto-publish)
  lockdown + network allow + web allow + app allow
        │  GET /dlp/policy?sinceVersion=
        ▼
AlphaAITracker.Service (Windows, Session 0)
  bootstrap exceptions always on (network + app survival A)
  AdvFirewall complement deny 80/443
  hosts / AlphaAI-DLP-* for explicit blocks
  AppLocker EXE default-deny + path/hash/publisher allows
  watchdog relaunches client.exe in the console session
        │
        ▼
client.exe (user session) — login, collection, live stream
  never paused by the employee
```

**Bootstrap network (baked, not typed by an admin).** Always permitted for outbound, even when the host allowlist is empty:

- This product's service and user-tracker binaries (path under the install dir + signer/hash once the binary is signed).
- The API host from `ALPHA_SERVER_URL` (HTTPS). Installer downloads already use `/api/v1/downloads`, so GitHub is not a bootstrap host.
- DNS to resolve that host, and the TLS path the HTTP stack already uses.
- Live stream only when the server compiles the SFU / TURN / STUN hosts into `platformAllow`. If they are omitted, lockdown will stop video. Presence WebSocket on the API host is covered by the API host entry.
- DHCP and the OS sign-in path.

**Bootstrap apps A (baked).** Always allowed to **start** under AppLocker:

- Install directory of `AlphaAITracker.Service` and the user tracker.
- Windows sign-in / Explorer / Start / minimal shell hosts required to stay logged in (explicit path allow list in the service — not an open `%System32%\*` grant).
- Explicitly **omit** Task Manager, Calculator, Notepad, Settings UWP, browsers, Office until allowlisted.

**After a `lockdown: true` bundle:**

- Outbound web and other non-bootstrap hosts are denied.
- A browser may open only published web-allow domains (and the platform hosts).
- A program may run only if it is bootstrap A or a published `allow.apps` entry (path / hash / publisher).

**Cutover flag.** `lockdown: true` (default) enables network deny **and** AppLocker enforce. Published `lockdown: false` opens both.

## 5. Bundle contract (extend, do not fork)

Add fields on the existing snapshot. Absent field = old client and old behavior.

```json
{
  "policyVersion": 120,
  "lockdown": true,
  "platformAllow": {
    "hosts": ["api.example.com", "turn.example.com"]
  },
  "allow": {
    "hosts": ["contoso.sharepoint.com"],
    "apps": [
      { "path": "C:\\Program Files\\App\\app.exe" },
      { "publisher": "CN=Contoso", "product": "Contoso App" },
      { "sha256": "<64 hex>" }
    ]
  },
  "rules": [],
  "shadowDeny": { "domains": [], "publishers": [], "appNames": [] }
}
```

Rules:

- `allow.hosts` are registrable hostnames, normalized the same way as `PolicyBundleParser.NormalizeDomain`. No URL paths.
- `allow.apps[]` is path and/or publisher subject and/or SHA-256. Reject draft rows that cannot resolve to at least one of those after catalog expansion. Do **not** put display names in the agent match key.
- Admin selects a catalog `installed_applications` row; server stores `installed_application_id` + denormalized `app_name`, and resolves publisher / install_path / binary from active `employee_installed_applications` links at create and again at Publish.
- Explicit `web_navigate` **block** rules and `shadowDeny.domains` still subtract from the web allowlist.
- Server compiles `platformAllow.hosts` from its own public config. Admins do not edit that list.
- Caps, enforced at Publish with HTTP 400: 500 allow hosts, 2 000 app entries, each publisher/path string ≤ 256 / 512 chars. Document the caps in the web form.
- Hash the canonical JSON into `bundle_hash` as today. Never UPDATE a published version.

Clients that do not understand `lockdown` keep enforcing `rules` + `shadowDeny` only. The service must ignore AppLocker until the build that implements it is installed (Installer-Parity).

## 6. Scalability

| Risk | Rule |
| --- | --- |
| Bundle per machine | Forbidden. One snapshot. `sinceVersion` stays the pull. |
| Firewall rule per domain per PC | Forbidden as the default-deny mechanism. One Windows AdvFirewall complement deny for 80/443 except resolved addresses of `platformAllow` + `allow.hosts`, with a TTL cache. |
| CDN pin explosion | Reuse the pin worker's generation lock. Cap live pins (start at 4 096 addresses). Evict stale pins. A full cache refuses new pins and writes one `enforcement_audit` row, not a per-packet alert. |
| Alert flood | Aggregate locally: one open alert per `(rule or lockdown, host or publisher, hour)` with a count. Sync that row. Do not insert per SYN. |
| Large allowlists | Caps above. Publish is O(rules) once. Pull payload stays one JSON document; 500 hosts is small. |
| Offline | Last applied snapshot remains in `dlp.db`. No snapshot + lockdown enabled after DeviceAuth = bootstrap only (network + apps). |
| Admin unlock | Machine-scoped `lockdown_paused_until`. Returned only on `GET /dlp/policy`. Service enforces the pause locally until the timestamp, then relocks. Pause clears AppLocker enforce for the window as well as network lockdown. Default pause max 4 hours. |

Department-scoped allowlists are a later phase. They still compile into **one** bundle (`allow.scopes[]`), not N versions.

## 7. Phases

### P0 — Delete tracking mode (server, client, web)

**Done when** (source complete; installed lab open)

- [x] Repo search has no product path that sets, displays, or returns `fixed` / `roaming` as a tracking mode (migrations 056 may remain as history; 058 drops the live objects).
- [x] Control / device-configuration routes unregistered (404). Sync JSON has no `controlEpoch`.
- [ ] Windows **installed** build lab (Installer-Parity).
- [x] `go build` / `go vet`, `dotnet build` client + DlpService, `npx tsc --noEmit`. Docs handoff.

### P1 — Lockdown contract + bootstrap network deny

**Done when** (source complete; installed lab open)

- [x] Source: Publish + service AdvFirewall complement deny + pause + web Allowlists.
- [ ] Lab items in §8 Lab 1.
- [x] Docs + `TESTING.md` §H playbook.

### P2 — Browser allowlist → network + hosts only

**Done when** (source complete; no Chromium HKLM enforce)

- [x] Source: hosts + `AlphaAI-DLP-*` / `AlphaAI-DLP-LOCK-*`; legacy Chrome/Edge URL/DoH/Quic cleared.
- [ ] Lab items in §8 Lab 2 (network-deny claims only).
- [x] Docs updated.

### P3 — Application allowlist (fail-closed + catalog picker) — ACTIVE

**Goal.** By default no application starts except bootstrap A + published allow entries. Admin selects apps from the same catalog as `/configuration/apps`. Enforce immediately via AppLocker (no audit-first lag).

**Server**

- Migration **064**: `installed_application_id`, `app_name`, `install_path` on draft + snapshot app allow tables; keep publisher/product/sha256 as resolved fields.
- Create accepts `installedApplicationId` (primary UI path). Resolve catalog + aggregate active employee install links. Cap 2000.
- List shows `appName` + resolved identity. `enabled` filter unchanged.
- Publish / `AssembleBundleJSON`: emit `allow.apps[]` with `{ path?, publisher?, product?, sha256? }` from **enabled** draft rows only (re-expand from inventory at Publish).
- Auto-publish remains a **web** concern (handler still exposes `POST /dlp/rules/publish`).

**Web `/allowlists/apps`**

- Form: searchable infinite-scroll catalog picker (`monitoringApi.apps.list` — same catalog as Configuration → Applications). Select `app_name` → create by `installedApplicationId` (+ optional note / path override) → auto-publish.
- **Windows Settings (Win10/11)** preset posts the ImmersiveControlPanel EXE + Appx product (Linux cinnamon/gnome catalog rows are not Windows Settings).
- Table: Application, identity summary, creator, Active|Inactive + Edit/Delete.
- Copy: under lockdown, only listed apps (plus bootstrap) can start.

**Service**

- `AppAllowEnforcer`: when `lockdown == true` and not paused, apply local AppLocker policy:
  - **Exe + Appx** EnforcementMode=Enabled (Win32 + Store default-deny). MSI Enabled empty = no installs. Script/Dll stay NotConfigured.
  - Bootstrap A path + shell Appx product allows (baked). PowerShell is **SYSTEM-only**; Settings stays default-deny until published. Published path / publisher from `policy_allow_apps` (+ Appx family when path is under WindowsApps).
  - Path macros: both Program Files roots → AppLocker `%PROGRAMFILES%` (never invent `%PROGRAMFILES(X86)%` or `%PROGRAMFILES% (x86)\…`). Match PF(x86) before PF with a directory-boundary prefix. Fingerprint `v6`.
  - UTF-16 policy XML; AppIDSvc Automatic + restart after apply; `Test-AppLockerPolicy` verify Taskmgr Denied.
  - Blocked launches show the Windows organization dialog (not open-then-kill). `AppProcessGuard` expands `%PROGRAMFILES%` to both PF roots (backup kill for already-running EXEs; force-kills interactive shells/Settings when path is unknown).
- Server Publish: `WidenBrowserAllowPath` emits Edge/Chrome `Application\*` (versioned helpers); Edge well-known fallback when inventory path is missing.
- `DeleteBlockAclEnforcer`: same lockdown gate — NTFS Deny Delete on Fixed/Removable roots (`ALPHA_DLP_DELETE_BLOCK`); clear on pause/teardown/park/local off.
- Fingerprint + generation lock. Teardown / `ALPHA_DLP_ENABLED=false` / `lockdown: false` / pause **removes** AppLocker policy **and** delete-block ACEs.
- Apply failure → `enforcement_audit` (do not claim enforce if verify says NOT_ENFORCING).

**Done when**

- [x] Migration **064** + catalog create/list/publish expand.
- [x] `/allowlists/apps` catalog picker (no free-text publisher/SHA as the primary path).
- [x] Source: AppLocker enforcer + bootstrap A + teardown/pause/park clear.
- [x] Windows Settings allow path (web preset + server well-known resolve) + delete-block ACL source.
- [ ] Lab: Calculator / Task Manager blocked until allowed; Explorer + tracker + service still run.
- [ ] Lab: Allowed catalog app starts; rename of hashed binary denied when hash no longer matches.
- [ ] Lab: Teardown / lockdown off clears AppLocker **and** delete-block ACEs.
- [ ] Lab: Reboot + sign-in while enforce is on (ship gate).
- [ ] Lab: Delete-block create OK / delete Denied; local `ALPHA_DLP_DELETE_BLOCK=false` clears (`TESTING.md` §H 15–17).
- [x] Docs + Installer-Parity note (ship **1.0.6+**).

## 8. Feature test labs

Windows installed build only (`Installer-Parity`). Source `dotnet build` is not a substitute. Do not lab Linux or macOS. Record the Windows installer version in the notes.

### Lab 0 — Tracking mode deleted (P0)

(unchanged — see prior checklist in git history / TESTING.md §G)

### Lab 1 — Bootstrap and cutover (P1)

(unchanged — TESTING.md §H)

### Lab 2 — Browsers (P2)

Network + hosts only; no Chromium HKLM URLAllowlist claim.

### Lab 3 — Applications (P3)

1. Fresh login under `lockdown: true`, empty app allow list. Expect: Explorer + tracker OK; Calculator / Task Manager / Notepad / browsers / **PowerShell (interactive)** / **Settings** get the Windows block dialog on launch (no flash-open).
2. On `/allowlists/apps`, pick "Calculator" (or lab catalog app) from the inventory list. Expect auto-publish; within a policy poll, Calculator starts; Task Manager still blocked.
3. Allow Task Manager the same way. Expect it starts. For Settings use the **Windows Settings (Win10/11)** preset (not a Linux cinnamon/gnome catalog row).
3b. Allow Microsoft Edge (catalog or path). Expect launch OK on PF(x86) installs; `applocker.policy.xml` uses `%PROGRAMFILES%\Microsoft\Edge\Application\*`, never `%PROGRAMFILES(X86)%` or `%PROGRAMFILES% (x86)\…` (`TESTING.md` §H 13b; installer **1.3.6+**).
4. Deactivate (or delete) Calculator. Expect auto-publish; Calculator blocked again.
5. If an entry has SHA-256: copy/rename the binary → expect deny when hash no longer matches.
6. Reboot. Expect sign-in, tracker relaunch, AppLocker still on.
7. Protected uninstall / teardown. Expect AppLocker policy gone **and** delete-block ACEs cleared; Calculator able to start; file delete works again.
8. Pause lockdown. Expect apps open for the pause window; after timestamp, relock.
9. Delete-block (`TESTING.md` §H 15–17): create/edit OK, Delete → Access Denied on Fixed/Removable; `ALPHA_DLP_DELETE_BLOCK=false` clears.

### Lab 4 — Scale and abuse (P1–P3)

(unchanged)

### Lab 5 — Regression (every phase)

(unchanged; include AppLocker clear on uninstall)

## 9. Master checklist

**Contract**

- [ ] Bundle fields are optional and ignored by older services.
- [ ] Publish is append-only; hash matches canonical JSON.
- [ ] Caps return 400 with a clear message.
- [ ] Block rules beat allow entries (web).
- [ ] `platformAllow` is server-compiled.
- [ ] `allow.apps` carries path/publisher/sha only — never display name as match key.

**Client / service**

- [ ] Tracking mode is gone.
- [ ] User-session collectors still run.
- [ ] Bootstrap network + app survival A do not depend on a published allowlist.
- [ ] Stale apply cannot recreate filters (generation lock).
- [ ] Teardown, pause, and `ALPHA_DLP_ENABLED=false` clear network **and** AppLocker.
- [ ] Alerts aggregated; unsynced rows deleted only via `acceptedIds`.
- [ ] No display-name match at the agent.

**Web**

- [ ] Allowlists pages have `rbacCatalog` + sidebar `module` parity.
- [ ] Lists use infinite scroll. Filters are URL-synced.
- [ ] Create/update/status/delete auto-publish (no separate Publish required on `/allowlists/*`).
- [ ] App allow form picks from the detected catalog (`app_name`), same source as `/configuration/apps`.

**Release**

- [ ] `go build`, `go vet`, relevant `go test`.
- [ ] `dotnet build` client and `AlphaAITracker.Service`.
- [ ] `npx tsc --noEmit`.
- [ ] Windows installer built and lab run on the installed artifact when this phase changes the agent or service.
- [ ] Docs handoff from `@prompt.md`.

## 10. Risks

| Risk | What to do |
| --- | --- |
| Lockdown before login blocks the API | Do not install default-deny until `device.auth` exists. Bootstrap always includes the API host. |
| Live video dies | `platformAllow` must include SFU/TURN/STUN or the lab records video as intentionally down. |
| Chrome DoH bypasses DNS | Deny public DoH hosts at the firewall, not with a browser blocklist. |
| AppLocker bricks sign-in or Explorer | Bootstrap A is mandatory and path-specific. Reboot + sign-in is a ship gate. If AppLocker is unavailable on the SKU, audit the failure and do not claim enforce. |
| Broad Microsoft publisher allow | Never allow "CN=Microsoft*" alone for inbox apps — expand catalog picks to **path** (and hash when present). |
| Upgrade bricks the current fleet | Older services ignore AppLocker fields until Installer-Parity ships the enforcer. |
| Pin / alert volume | Caps and hourly aggregates. Never drop unsynced alerts to survive the volume. |
| Linux or macOS expected to lock down | Out of scope. |

## 11. Suggested order

P0–P3 complete in source. **Next:** rebuild + install Windows **1.3.6+** and run Lab 3 (`TESTING.md` §H steps 10–14 + **13b Edge**). Pass gate: Windows block dialog on double-click, `verify=enforcing` in service log; Edge under PF(x86) unblocks with `%PROGRAMFILES%\Microsoft\Edge\Application\*` (no ` (x86)` after the macro). There is no Linux or macOS phase.
