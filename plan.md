# Always-on Windows tracker + default-deny lockdown

> **Status:** plan only. Nothing in this file is implemented.
> **Written:** 2026-11-05
> **Updated:** 2026-11-05 — tracking mode is removed entirely (no Fixed leftover). Enforcement in this plan is Windows-only.
> **Owns:** delete the tracking-mode feature from server, client, and web, and a Windows default-deny allowlist (network, web, applications).
> **Does not own:** Linux or macOS enforcement, or moving collectors, login, browser reading, or live capture into `AlphaAITracker.Service`.

Execute with `@prompt.md`. Read `AGENTS.md` before each phase. Ship one phase at a time. A phase is done only when its checklist, its lab, and the documentation handoff in `@prompt.md` are done.

---

## 1. Locked decisions

1. **Tracking mode is deleted, not frozen.** There is no Fixed mode, no Roaming mode, no org default, no per-machine override, and no Start/Stop. Collectors run whenever the employee is logged in. The employee has no control that pauses them.
2. **This plan is Windows-only.** Lockdown (firewall, browser policy, application control) ships in `AlphaAITracker.Service` on Windows. Do not add a Linux nftables phase, a macOS phase, or Linux/macOS labs. Shared client code that only exists for tracking mode is deleted, so a Linux build of the same source loses Start/Stop too. Linux and macOS keep today's collectors and today's DLP. They do not gain lockdown.
3. **Two Windows processes stay.** `AlphaAITracker.Service` (LocalSystem, Session 0) keeps enforcement, policy, updates, ACL, and the watchdog. `client.exe` stays in the interactive user session for login, foreground apps, browser URLs, idle, and screen capture. `UserSessionProcessLauncher` exists because a process started inside the service never sees the desktop. Folding those features into the service would blank them and would put employee activity in a LocalSystem process.
4. **New installs fail closed.** After login, general web and non-platform programs are blocked until a published allowlist says otherwise. The tracker, the DLP service, and the platform destinations below stay open so the client can still reach the server.
5. **Existing fleets do not flip on upgrade.** Lockdown is off until an admin publishes a bundle with `lockdown: true`. A machine that already has a last-good deny-list bundle keeps that behavior until that publish.
6. **One published bundle for the fleet.** Allowlists are compiled at Publish into the existing immutable `dlp_policy_versions` snapshot (`DLP Policy Version Rule`). Clients keep `GET /dlp/policy?sinceVersion=`. Do not mint a bundle per PC.
7. **Identity is publisher or hash, never a display name.** No new hardcoded software-name lists (`AGENTS.md`).
8. **Uninstall clears enforcement.** Protected uninstall and teardown must remove firewall, DNS, browser policy, and application-control artifacts. A removed product must not leave the PC locked.
9. **Alerts are aggregated.** A default-deny filter must not write one unsynced alert per blocked connection. Unsynced alerts are still deleted only when `acceptedIds` lists them.

## 2. Out of scope

- Linux and macOS lockdown, labs, and installers. No nftables default-deny and no macOS application control in this plan.
- Keeping a "Fixed" tracking mode, a mode badge, or a mode API that always returns `fixed`.
- Roaming laptops, hotel captive portals, and employee pause.
- Running UI Automation, GDI/DXGI capture, or the Avalonia login window inside Session 0.
- Per-employee policy documents.
- Hard-blocking URL paths (hosts and DNS cannot do that today; paths stay match metadata).
- Replacing Windows with a custom shell.
- Blocking OS boot components, the login screen, or Windows Update infrastructure by name.

## 3. Current baseline

| Surface | Today |
| --- | --- |
| Tracking mode | Migration 056: `device_tracking_policies`, `device_control_meta`, `device_tracking_events`, `device_policy_audit`, and `employee_machines` columns `tracking_mode_override`, `tracking_desired_state`, `tracking_phase`, `tracking_state_since`, `last_control_seen_at`, `control_version`. API: `GET /devices/control`, `POST /devices/tracking-events`, `PATCH .../tracking-mode`, `GET /device-configuration/roaming-machines`, `mode-types`, `tracking-events`. Sync responses piggyback `controlEpoch`. Web: `SetTrackingModeDialog`, `TrackingModeBadge`, Device Specs mode chips, `/configuration/device-configuration`. Client: `TrackingModeGate`, `TrackingModeResolver`, `TrackingControlFollower`, `LocalRoamingControl`, `TrackingEventReporter`, `--tracking-controls`, `--control-shell`, service `TrackingControlWorker`. |
| DLP | Deny list. Published `web_navigate` block rules + `shadowDeny.domains`. Enforce: hosts sink, `AlphaAI-DLP-*` firewall, `DnsBlockPinWorker`, Chrome/Edge `URLBlocklist`. Empty policy enforces nothing. |
| Apps | Inventory and sessions only. AppLocker / WDAC is not built. |
| Policy pull | `GET /api/v1/dlp/policy?sinceVersion=` (DeviceAuth). Bundle shape: `policyVersion`, `rules[]`, `shadowDeny`. Last good bundle is kept offline. |
| Split | Service = policy, USB, domain block, silent update, watchdog. User process = collectors and GUI. |

## 4. Target

```
Admin publishes one bundle
  lockdown + network allow + web allow + app allow
        │  GET /dlp/policy?sinceVersion=
        ▼
AlphaAITracker.Service (Windows, Session 0)
  bootstrap exceptions always on
  WFP default-deny for everyone else
  Chrome/Edge allow policy
  application control (later phase)
  watchdog relaunches client.exe in the console session
        │
        ▼
client.exe (user session) — login, collection, live stream
  never paused by the employee
```

**Bootstrap (baked, not typed by an admin).** Always permitted, even when the allowlist is empty:

- This product's service and user-tracker binaries (path under the install dir + signer/hash once the binary is signed).
- The API host from `ALPHA_SERVER_URL` (HTTPS). Installer downloads already use `/api/v1/downloads`, so GitHub is not a bootstrap host.
- DNS to resolve that host, and the TLS path the HTTP stack already uses.
- Live stream only when the server compiles the SFU / TURN / STUN hosts into `platformAllow`. If they are omitted, lockdown will stop video. Presence WebSocket on the API host is covered by the API host entry.
- DHCP and the OS sign-in path. Do not add a long "known Windows exe" name list.

**After a `lockdown: true` bundle:**

- Outbound web and other non-bootstrap hosts are denied.
- A browser may open only published web-allow domains (and the platform hosts).
- A program may run only if it is the OS, this product, or a published app-allow entry. Until the app-control phase ships, programs may still start, but they cannot reach the network except the bootstrap.

**Cutover flag.** Old bundles with no `lockdown` field mean today's deny-list. `lockdown: true` is the switch.

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
      { "publisher": "CN=Microsoft Corporation", "product": "Microsoft Office" },
      { "sha256": "<64 hex>" }
    ]
  },
  "rules": [],
  "shadowDeny": { "domains": [], "publishers": [], "appNames": [] }
}
```

Rules:

- `allow.hosts` are registrable hostnames, normalized the same way as `PolicyBundleParser.NormalizeDomain`. No URL paths.
- `allow.apps[]` is publisher subject and/or SHA-256. Reject rows that have neither. Do not store display names as the match key.
- Explicit `web_navigate` **block** rules and `shadowDeny.domains` still subtract from the allowlist.
- Server compiles `platformAllow.hosts` from its own public config (API public host, `WEBRTC_TURN_*`, STUN, `INSTANCE_PUBLIC_URL`). Admins do not edit that list.
- Caps, enforced at Publish with HTTP 400: 500 allow hosts, 2 000 app entries, each publisher string ≤ 256 chars. Document the caps in the web form.
- Hash the canonical JSON into `bundle_hash` as today. Never UPDATE a published version.

Clients that do not understand `lockdown` keep enforcing `rules` + `shadowDeny` only. The service must ignore `lockdown` until the build that implements it is installed (Installer-Parity). Do not publish `lockdown: true` to a fleet still on an older service.

## 6. Scalability

| Risk | Rule |
| --- | --- |
| Bundle per machine | Forbidden. One snapshot. `sinceVersion` stays the pull. |
| Firewall rule per domain per PC | Forbidden as the default-deny mechanism. One Windows WFP policy: deny outbound TCP/UDP 80/443 except resolved addresses of `platformAllow` + `allow.hosts`, with a TTL cache. |
| CDN pin explosion | Reuse the pin worker's generation lock. Cap live pins (start at 4 096 addresses). Evict stale pins. A full cache refuses new pins and writes one `enforcement_audit` row, not a per-packet alert. |
| Alert flood | Aggregate locally: one open alert per `(rule or lockdown, host or publisher, hour)` with a count. Sync that row. Do not insert per SYN. |
| Large allowlists | Caps above. Publish is O(rules) once. Pull payload stays one JSON document; 500 hosts is small. |
| Offline | Last applied snapshot remains in `dlp.db`. No snapshot + lockdown not yet enabled = today's open behavior. No snapshot + lockdown enabled on a **new install** = bootstrap only. |
| Admin unlock | Machine-scoped `lockdown_paused_until` on the lockdown migration (not on the deleted control API). Returned only on `GET /dlp/policy`. Service enforces the pause locally until the timestamp, then relocks without waiting for the next poll. Audit row required. Default pause max 4 hours. |

Department-scoped allowlists are a later phase. They still compile into **one** bundle (`allow.scopes[]`), not N versions.

## 7. Phases

### P0 — Delete tracking mode (server, client, web)

**Goal.** The Fixed/Roaming control plane is gone. Nothing in its place returns a mode. Collectors stay in the user session and run for the whole login. The Windows watchdog still relaunches `client.exe` into the console session. No Linux or macOS work in this phase beyond deleting shared mode code so those builds compile.

**Server**

- Migration **057** drops the 056 control plane:
  - tables `device_tracking_policies`, `device_control_meta`, `device_tracking_events`, `device_policy_audit`
  - `employee_machines` columns `tracking_mode_override`, `tracking_desired_state`, `tracking_phase`, `tracking_state_since`, `last_control_seen_at`, `control_version`
- Historical Start/Stop rows go with `device_tracking_events`. Do not keep a read-only log of a feature that no longer exists.
- Remove routes and their handler/service/repo/DTO types:
  - `GET /api/v1/devices/control`
  - `POST /api/v1/devices/tracking-events`
  - `PATCH /api/v1/employees/:id/machines/:machineId/tracking-mode`
  - `GET /api/v1/device-configuration/roaming-machines`
  - `GET /api/v1/device-configuration/mode-types`
  - `GET /api/v1/device-configuration/tracking-events`
- Remove `controlEpoch` from sync response DTOs and from `NewSchemaHandler`. Do not replace it with a constant.
- Lockdown pause (P1) must not revive `/devices/control`. Put `lockdown_paused_until` on the DLP policy pull.

**Client (shared source — Windows is the only platform this plan tests)**

- Delete mode and pause code: `TrackingModeGate`, `TrackingModeResolver`, `TrackingControlFollower`, `LocalRoamingControl`, `TrackingEventReporter`, `TrackingControlViewModel`, `TrackingControlsActivation`, `TrackingControlsSupervisor`, `--tracking-controls`, `--control-shell`, `--drain-only` when it exists only to pause collectors.
- Delete service pieces that exist for mode: `TrackingControlWorker`, `control.json` mode fields, pipe ops `GetTrackingState`, `RequestStart`, `RequestStop`.
- Boot path always starts collectors. No gate reads a mode file. Uninstall still uses `AccountRemovalService` to drain and wipe.
- Watchdog launches only the normal user tracker into the console session.
- Do not add Linux or macOS replacements for the deleted controller.

**Web**

- Delete `SetTrackingModeDialog`, `TrackingModeBadge`, Employees "Set tracking mode", and Device Specs mode chips / Change mode.
- Delete the page `/configuration/device-configuration` (Roaming list, mode types, Start/Stop logs) and its sidebar entry.
- Remove `configuration/device-configuration` from `rbacCatalog` and the sidebar in the same change (`RBAC Nav → Catalog Parity`). Drop the API client methods (`setMachineTrackingMode`, `deviceConfigurationApi`).
- A bookmarked `/configuration/device-configuration` or `?tab=roaming` must not throw. The route is gone; the app shell shows the normal unknown-route / unauthorized behavior already used for removed pages.

**Done when**

- [ ] Repo search has no product path that sets, displays, or returns `fixed` / `roaming` as a tracking mode (migrations 056 may remain as history; 057 drops the live objects).
- [ ] `GET /devices/control`, `POST /devices/tracking-events`, `PATCH .../tracking-mode`, and all three `/device-configuration/*` routes are unregistered (404).
- [ ] Sync JSON has no `controlEpoch`.
- [ ] Windows install: no Start/Stop window, no mode badge, collectors running, presence Online.
- [ ] End Task on `client.exe` relaunches it in the user session, still collecting.
- [ ] `--remove-account` still wipes. Product uninstall still needs a removal code.
- [ ] `go build` / `go vet`, `dotnet build` client + DlpService, `npx tsc --noEmit`.
- [ ] Docs: `AGENTS.md`, `client/ARCHITECTURE.md`, `server/ARCHITECTURE.md`, `web/ARCHITECTURE.md`, `TESTING.md` §G (replace the Fixed/Roaming playbook with "mode removed").

### P1 — Lockdown contract + bootstrap network deny

**Goal.** Admin can publish `lockdown: true`. The service then denies outbound 80/443 except bootstrap + `platformAllow` + `allow.hosts`. Applications are not killed in this phase.

**Server / web**

- Extend Publish (`DlpService.Publish`, `AssembleBundleJSON`) with `lockdown`, `platformAllow`, `allow`.
- Web: Security & DLP page (new submodule `security-dlp/lockdown` **or** a section on `/web-blocker` — one surface, registered in `rbacCatalog` and `AppSidebar` together). Controls: Lockdown on/off, host allowlist (searchable chips, infinite scroll if the list is long), link to the app inventory for P3. Save writes drafts. **Publish** is what agents enforce.
- Show the compiled `platformAllow` as read-only.
- Maintenance unlock action on the employee device menu: sets `lockdown_paused_until`, audited.

**Service**

- `PolicyBundleParser` reads the new fields. Missing `lockdown` → current deny-list path, unchanged.
- `lockdown: true`: apply a single default-deny outbound policy for 80/443. Permit the bootstrap and the allow hosts. Keep the existing hosts/`URLBlocklist` path for explicit block rules inside the allow set.
- Block DNS-over-HTTPS to non-allow hosts (known public resolvers are denied because they are not the corporate DNS path). Do not add a browser-name list to do this; deny the well-known resolver **hosts** (`dns.google`, `cloudflare-dns.com`, `mozilla.cloudflare-dns.com`, and the same class) as destinations.
- Generation lock, same pattern as `DnsBlockPinWorker` / `PinGate`: a stale apply must not recreate rules after a newer publish.
- Teardown / empty desired set / `ALPHA_DLP_ENABLED=false` removes the default-deny filters and leaves the machine open. Master switch off must not strand a PC.
- New install, lockdown published, policy not pulled yet: once `device.auth` exists, enforce **bootstrap only** until the snapshot arrives. Before `device.auth`, do not take the network away (the employee still has to log in and the tracker has to reach the API).

**Done when**

- [ ] Publish without `lockdown` does not change a lab PC that was open.
- [ ] Publish `lockdown: true` and an empty `allow.hosts`: browser cannot open a public site; tracker sync, policy pull, and presence still work.
- [ ] Adding one host and publishing makes only that host load.
- [ ] An explicit block rule for that host wins over the allow.
- [ ] Pause unlock restores general network until the timestamp, then relocks.
- [ ] Protected uninstall removes the filters (`TESTING.md` §C extended).
- [ ] 20-host flap lab (publish add/remove) leaves no orphan filter (same bar as `TESTING.md` §F.2).

### P2 — Browser allowlist

**Goal.** The address bar matches the network policy. Chrome and Edge get an allow policy derived from `platformAllow` + `allow.hosts` minus block rules. Firefox and other browsers get the network deny only; the lab must record that honestly.

Apply policy only where the installed browser exposes an enterprise policy location already used by `DomainBlockEnforcer` (Chrome/Edge `URLAllowlist` alongside the existing `URLBlocklist`). Discover the browser from `installed_applications` / the existing registry. Do not add a new process-name table.

**Done when**

- [ ] Allowed host loads in Chrome and Edge.
- [ ] Any other host shows the organization block page, and the connection also fails if policy is slow (network deny).
- [ ] Inactive / lockdown off clears `URLAllowlist` the same way `URLBlocklist` clears today (`TESTING.md` §F.1).
- [ ] A second browser with no enterprise allow API still cannot load the site (network deny). Document it. Do not claim browser-UI blocking for it.

### P3 — Application allowlist (audit, then enforce)

**Goal.** Programs outside the OS, this product, and `allow.apps` cannot start. Ship **audit first**.

- Match publisher certificate subject or SHA-256. Unsigned internal tools use hash only.
- Admin picks from the existing software inventory (server-projected rows, infinite scroll, URL filters). Publish copies publisher/hash into the bundle. The client does not re-query the catalog to decide.
- Enforcement is Windows WDAC supplemental policy or AppLocker, owned by the service. Prefer a supplemental policy that allows Windows inbox signed code + this product + the bundle, and denies the rest. Audit mode logs would-block rows through the aggregated alert path for at least one lab week before enforce.
- Windows only. Do not add a Linux or macOS application-control path, and do not mention one in the UI.

**Done when**

- [ ] Audit mode: a non-allowed exe writes one aggregated alert and still runs.
- [ ] Enforce mode: that exe does not start; tracker, Explorer, and sign-in still work.
- [ ] A renamed copy of an allowed exe does not run unless the hash or publisher still matches.
- [ ] Teardown removes the WDAC/AppLocker policy.
- [ ] Windows Update and a reboot complete while enforce is on (lab gate — if they fail, the phase does not ship).

## 8. Feature test labs

Windows installed build only (`Installer-Parity`). Source `dotnet build` is not a substitute. Do not lab Linux or macOS. Record the Windows installer version in the notes.

### Lab 0 — Tracking mode deleted (P0)

1. On the old Windows build, set a machine to Roaming and confirm the Start/Stop window. Upgrade to the P0 installer.
2. Expect: no Start/Stop window, no tray "Tracking Start / Stop", no mode chip on Employees or Device Specs, collectors running, presence Online, app usage still writing.
3. Task Manager → End Task on the user tracker. Expect relaunch within the watchdog interval, still in the user session.
4. Web: no "Set tracking mode". `/configuration/device-configuration` is gone from the sidebar. Opening that URL does not throw.
5. API, with a valid admin cookie and a device token:
   - `GET /api/v1/devices/control` → 404
   - `POST /api/v1/devices/tracking-events` → 404
   - `PATCH /api/v1/employees/:id/machines/:machineId/tracking-mode` → 404
   - `GET /api/v1/device-configuration/roaming-machines|mode-types|tracking-events` → 404
   - A normal sync body has no `controlEpoch`.
6. `--remove-account` still wipes (drain or timeout). Product uninstall still needs a removal code (`TESTING.md` §C, §C.1).

### Lab 1 — Bootstrap and cutover (P1)

1. On a PC that has **not** received `lockdown: true`, publish a normal web-blocker deny rule. Expect today's hosts/firewall behavior only (`TESTING.md` §F).
2. Publish `lockdown: true`, `allow.hosts: []`. Wait ≤ `ALPHA_DLP_POLICY_POLL_SEC` (or restart the service).
3. Browser → a public site. Expect failure.
4. Tracker: login already done; next sync succeeds; `/dlp-alerts` still receives aggregated blocks; live presence stays up if the API host is the websocket host.
5. If TURN/STUN is not in `platformAllow`, expect live **video** to fail. Add those hosts via server config, republish, expect video. Do not "fix" this by opening all UDP.
6. Publish `allow.hosts: ["example.com"]` (use a lab host you control). Expect that host to load. Expect a second host to fail.
7. Add a web-blocker **block** rule for the same host. Expect the block to win.
8. Device menu → pause lockdown for 15 minutes. Expect general browsing. After the timestamp, expect relock without a reboot.
9. Delete the allow and publish. Expect the host to fail again, and `netsh advfirewall firewall show rule name=all` to show no leftover permit for it.
10. `ALPHA_DLP_ENABLED=false` and restart the service. Expect the PC open. Set it back to true and expect lockdown to return from the last bundle.

### Lab 2 — Browsers (P2)

1. With Lab 1 step 6 in force, Chrome and Edge open the allowed host.
2. A non-allowed host shows the organization interstitial **and** does not load after a policy refresh.
3. Turn lockdown off and publish. Confirm `URLAllowlist` and `URLBlocklist` are gone (`TESTING.md` §F.1 steps 4–5).
4. Firefox (or another browser without that policy): non-allowed host still fails at the network. Record the browser. Do not file it as a miss against the Chrome policy.

### Lab 3 — Applications (P3)

1. Audit mode. Run an unsigned exe from the user profile. Expect it to run and one aggregated alert within the alert sync interval.
2. Allow it by SHA-256 from the inventory UI. Publish. Expect the would-block to stop.
3. Switch the org to enforce. Expect the unsigned exe to be blocked. Expect Explorer, the tracker, and the service to keep running.
4. Copy an allowed exe, rename it, run it. Expect deny when the hash no longer matches and the publisher is absent.
5. Reboot. Expect sign-in, tracker relaunch, and lockdown still on.
6. Protected uninstall. Expect the application-control policy gone and the unsigned exe able to start.

### Lab 4 — Scale and abuse (P1–P3)

1. Publish 100 allow hosts, then remove 50, then publish twice in a minute. After each pull, the enforced set equals the bundle. No orphan pins (`TESTING.md` §F.2 bar).
2. Generate a burst of blocked connections (script, 1 000 connects). Expect a handful of aggregated alerts, not 1 000 rows in `dlp.db` or `/dlp-alerts`.
3. Fill the pin cap on purpose. Expect an `enforcement_audit` row and no crash, no disk purge of unsynced alerts.
4. Pull `GET /dlp/policy?sinceVersion=<current>` → empty/304. Agent does not reapply or flap.

### Lab 5 — Regression (every phase)

1. USB block still works (`TESTING.md` §B).
2. Attendance Last Seen and app-usage sync still move while lockdown is on.
3. Removal code uninstall still writes `teardown.ok` and the machine is not left filtered.
4. Role without the new submodule does not see the lockdown page; direct URL → `/unauthorized`. `company_admin` sees it.

## 9. Master checklist

Copy this into the phase PR. Check only what that phase actually did.

**Contract**

- [ ] Bundle fields are optional and ignored by older services.
- [ ] Publish is append-only; hash matches canonical JSON.
- [ ] Caps return 400 with a clear message.
- [ ] Block rules beat allow entries.
- [ ] `platformAllow` is server-compiled.

**Client / service**

- [ ] Tracking mode is gone on server, client, and web. No Fixed/Roaming API, page, or badge remains.
- [ ] User-session collectors still run. Nothing in the product pauses them.
- [ ] Watchdog relaunch uses `UserSessionProcessLauncher`.
- [ ] Bootstrap does not depend on a published allowlist.
- [ ] Stale apply cannot recreate filters (generation lock).
- [ ] Teardown and `ALPHA_DLP_ENABLED=false` clear enforcement.
- [ ] Alerts aggregated; unsynced rows deleted only via `acceptedIds`.
- [ ] No new hardcoded application-name list.

**Web**

- [ ] New page has `rbacCatalog` + sidebar `module` in the same change.
- [ ] Lists use infinite scroll. Filters are URL-synced. No Clear button on search.
- [ ] Draft edits do nothing on the PC until Publish.

**Release**

- [ ] `go build`, `go vet`, relevant `go test`.
- [ ] `dotnet build` client and `AlphaAITracker.Service`.
- [ ] `npx tsc --noEmit`.
- [ ] Windows installer built and lab run on the installed artifact when this phase changes the agent or service. No Linux or macOS installer for this plan.
- [ ] `config.enc` re-bake called out if any new `ALPHA_*` knob was added. Prefer no new knob; poll intervals already exist.
- [ ] Docs handoff from `@prompt.md` (AGENTS + the architecture file for each surface + `TESTING.md` when the lab would otherwise lie).

## 10. Risks

| Risk | What to do |
| --- | --- |
| Lockdown before login blocks the API | Do not install default-deny until `device.auth` exists. Bootstrap always includes the API host. |
| Live video dies | `platformAllow` must include SFU/TURN/STUN or the lab records video as intentionally down. |
| Chrome DoH bypasses DNS | Deny public DoH hosts at the firewall, not with a browser blocklist. |
| WDAC bricks sign-in or update | Audit mode is mandatory first. Reboot + Windows Update is a ship gate. |
| Upgrade bricks the current fleet | `lockdown` absent = old behavior. Admins opt in by publishing. |
| Pin / alert volume | Caps and hourly aggregates. Never drop unsynced alerts to survive the volume. |
| Linux or macOS expected to lock down | Out of scope. Do not document a Linux/macOS lockdown promise in the UI. |
| Old clients still call `/devices/control` | After P0 those routes 404. An old Windows agent that fails the control poll keeps collecting. Confirm that once in Lab 0 with one pre-P0 installer against the new server, then retire that installer. |

## 11. Suggested order

P0 first (delete tracking mode on server, client, and web). Then P1 (Windows network default-deny). P2 tightens Chrome and Edge. P3 is last because application control can brick a PC. There is no Linux or macOS phase.
