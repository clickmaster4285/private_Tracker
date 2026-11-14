# Alpha AI Tracker — Project Map

> **Last audited:** 2026-11-13
> **Changelog:**
>
> - 2026-11-13: **DLP Rules — File Download / File Upload triggers (server + web + DlpService).**
>   Browser download/upload blocking lives in **DLP Rules** (not a separate page): new triggers
>   `file_download` and `file_upload`; an enabled rule with action **Block** or **Alert + Block**,
>   once published, makes every Windows PC apply it (same model as `usb_storage`). **Server:**
>   migration **072** widens `dlp_rules_trigger_check`; `allowedTriggers` gains both. No new tables /
>   routes / RBAC — rules ride the existing bundle `rules[]`. **Web `/dlp-rules`:** trigger options
>   "File Download (browser)" / "File Upload (browser)" with an inline hint; alert labels
>   `file_download` / `file_upload` / `file_transfer_tamper`. **DlpService:** new
>   `FileTransferEnforcer` writes HKLM Chromium policies for Chrome / Edge / Brave / Chromium —
>   `DownloadRestrictions=3` (no downloads) and `AllowFileSelectionDialogs=0` (file picker never
>   opens → no uploads) — drift-only every `EnforceWorker` tick (`PolicyBundleParser.BlockingRule`);
>   `Clear` removes only values holding our exact value. Independent of network lockdown; honours
>   machine pause and local switch `ALPHA_DLP_FILE_TRANSFER_BLOCK` (default `true`). A rewrite while
>   the desired state is unchanged = tamper → `file_transfer_tamper` alert (rule id + severity, ≤1 /
>   10 min). Offline-first comes free: `PolicyStore` already persists rules. Park, pipe teardown,
>   and tracker `WindowsTeardownCleanup` clear the values; pipe `Health.fileTransferBlock`. Env →
>   `.env(.example)` → `generate-windows-vars.sh` → Inno `service.env` / `install-service.ps1` /
>   `EnvBootstrap`. Client **VERSION 1.4.8**. Honest limits: action **Alert Only** does nothing yet
>   (no download/upload detection); Firefox / Opera / Tor ignore these policies → allow only
>   Chrome/Edge in `/allowlists/apps`; drag-and-drop of a file into a page, pasted text and
>   screenshots are not blocked. Verified: `go build ./...` + `go vet`; `dotnet build` DlpService
>   0/0 + client 0/0 (scratch output — bin locked by a running `client.exe`); `npx tsc --noEmit`
>   (only the pre-existing stale `.next/types` port-control error). NOT verified: migration 072 on a
>   live DB, `dotnet test` (bin locked), installer build/install, browser lab (`TESTING.md` § File
>   Transfer). Installer-Parity: re-bake `config.enc` + rebuild the Windows installer; restart API
>   for **072**.
> - 2026-11-11: **24/7 antivirus guard (Windows DlpService + server + web).** Real-time scanning is
>   Microsoft Defender's on-access engine (every file scanned on open/run/download/copy; malicious →
>   blocked + quarantined). The tracker now keeps it ON at full strength and reverts tampering.
>   **DlpService:** new `DefenderGuard` + `AntivirusGuardWorker` (independent of web policy and of
>   `ALPHA_DLP_ENABLED`; local switch `ALPHA_AV_ENFORCE`, default `true`). Every `ALPHA_AV_GUARD_SEC`
>   (30 s) it reverts: real-time / behaviour / download / script / archive / removable scanning off,
>   Block-at-First-Sight off, cloud (MAPS Advanced, CloudBlockLevel High, 50 s timeout), PUA + network
>   protection off, threat default action Allow/NoAction, per-threat allow overrides, exclusions not in
>   `ALPHA_AV_ALLOWED_EXCLUSIONS`, and `HKLM\SOFTWARE\Policies\Microsoft\Windows Defender` disable /
>   exclusion GPO values; restarts `WinDefend`. Only drifted settings are written; what Windows refuses
>   (Tamper Protection) is reported, not looped. Defender Operational 1116/1006 → `malware` alert
>   (*blocked*), 1118/1119/1008 → *blocked_failed*, 5001/5010/5012 → `av_tamper` critical, 5013 →
>   `av_tamper`; deterministic alert ids per record + `policy_kv` bookmark (first run baselines to now).
>   Signatures ≥ 1 day → `Update-MpSignature` (≥ 4 h apart); no quick scan in
>   `ALPHA_AV_QUICK_SCAN_HOURS` (24) → background `Start-MpScan`. A registered third-party AV that owns
>   protection is reported, never fought. `AppProcessGuard` also kills the Windows Security app
>   (`SecHealthUI`) for interactive users under lockdown. Pipe `Health.avEnforce`.
>   **Server:** migration **068** `antivirus_status` (one row per employee+machine) + index on
>   `dlp_alerts(type, machine_id, occurred_at)`; DeviceAuth `POST /antivirus/status/sync` (admission
>   group); JWT `GET /antivirus/status?search=&health=&page=&perPage=` with server-projected `isStale`
>   (30 min) / `threats24h` + health counts. Detections + tamper ride the existing `dlp_alerts` inbox.
>   RBAC submodule `antivirus` under `security-dlp`. **Web:** `/antivirus` (infinite scroll, URL-synced
>   `search`/`health`; tiles link to `/dlp-alerts?type=malware|av_tamper`); sidebar
>   `module:"antivirus"`; alert labels `malware` / `av_tamper`. Env `ALPHA_AV_ENFORCE / GUARD_SEC /
>   STATUS_SEC / QUICK_SCAN_HOURS / ALLOWED_EXCLUSIONS` → `.env(.example)` → `generate-windows-vars.sh`
>   → Inno `service.env` + `EnvBootstrap` seeds. Verified: `dotnet build` DlpService 0/0; `go build ./...`
>   + `go vet` (handlers/repository/services/router/cmd); `npx tsc --noEmit` (only a pre-existing stale
>   `.next/types` port-control error); status probe run live against the dev PC's Defender; enforce
>   script parse-checked only (not executed — it would rewrite the dev PC's Defender). NOT verified:
>   migration 068 + endpoints on a live DB (no Postgres/Redis running), installer build/install, EICAR
>   lab (`TESTING.md` § 24/7 antivirus guard). Honest limit: a local Administrator with Tamper
>   Protection ON can still turn real-time off in the Windows Security UI (guard raises a critical
>   alert); offline / Safe Mode stays possible. Installer-Parity: re-bake `config.enc` + rebuild the
>   Windows installer; restart API for **068**.
> - 2026-11-11: **Protected-uninstall pipe unlock + AppLocker leftover fix (client + DlpService).**
>   `ApplyTeardownGate` / `AuthorizeTeardown` no longer gated on `ALPHA_DLP_ENABLED` (was returning
>   `dlp_disabled` / racing write_timeout while AppLocker stayed Enforce). Pipe teardown timeouts
>   raised; `AppAllowEnforcer.Clear` deletes `SrpV2` then NotConfigured. Elevated
>   `WindowsTeardownCleanup` clears AppLocker + browser policies + firewall via **HNetCfg COM**
>   (netsh often AppLocker-denied); SYSTEM task runs AppLocker clear. UAC elevate also triggers
>   when AppLocker still Enabled. Client **VERSION 1.4.7**. Lab: [`removeTracker.md`](removeTracker.md).
>   Verified: `dotnet build` client + DlpService; teardown unit tests. Installer-Parity: next Windows
>   installer.
>
> - 2026-11-11: **Lab playbook [`removeTracker.md`](removeTracker.md).** Windows leftover wipe +
>   diagnostics after protected uninstall (AppLocker/SRP/WDAC, hosts, `AlphaAI-DLP-*` FW/PIN via
>   COM, USBSTOR, delete-block ACL). Linked from `README.md` + `TESTING.md` §C. Use when
>   `pipeUnlock=False` / Error 1260 / apps still blocked after product remove.
>
> - 2026-11-11: **Silent apply — AppLocker updates path + pre-stop service (DlpService).** Staged
>   installer under `%ProgramData%\<APP_MUTEX>\updates\` was default-denied under Exe Enforce
>   (EXE left on disk). Fix: SYSTEM-only AppLocker allow for that folder; fingerprint `v7`.
>   Helper pre-stops DLP service before Inno (unlock `Service\*.dll`); after success clears
>   flags + `relaunch_tracker`, and `sc start` only if not RUNNING (Inno `InstallDlpService`
>   already restarts the service; watchdog relaunches client — main dual-restart parity).
>   Client **VERSION 1.4.6**. Verified: `dotnet build` DlpService. Installer-Parity: next
>   Windows installer.
>
> - 2026-11-11: **Post-auto-update restart service + tracker (DlpService).** Silent apply helper
>   clears `update_in_progress` / priority **before** `sc stop/start` of
>   `AlphaAITracker.Service` (wait until RUNNING), writes `relaunch_tracker`, and the
>   watchdog launches `client.exe --background --restart` in the user session. Client
>   **VERSION 1.4.5**. Verified: `dotnet build` DlpService. Installer-Parity: next Windows
>   installer.
>
> - 2026-11-11: **Allowlists apps — path-only allow (web).** `/allowlists/apps` “Allow
>   application” no longer requires a catalog pick or Windows Settings: enter a full EXE
>   path alone and **Allow & publish** enables when a **note** is also filled (note stays
>   optional for catalog / Settings preset). Posts `POST /dlp/lockdown/apps` with `path` +
>   `note` (server already supported path-only). Verified: `npx tsc --noEmit` as available.
>
> - 2026-11-11: **Update-priority yield (client + DlpService).** When a newer release will
>   auto/force install, `update_priority` is set and the tracker yields sync / live-stream /
>   screenshots / collectors / browser a11y so download+Inno run first (auto jitter capped at
>   120s; server `RELEASE_DOWNLOAD_MAX_CONCURRENT` still admits the fleet). Cleared on apply
>   success/fail/TTL. Client **VERSION 1.4.2**. Verified: `dotnet build` client + DlpService.
>   Installer-Parity: next Windows installer.
>
> - 2026-11-11: **Silent update stuck flag + fleet download admission (client + server).** Lab dump
>   showed `update_in_progress` left after a second apply arm (helper never logged) → watchdog
>   never relaunched and auto-update stayed parked; Edge AppLocker XML stayed on bad
>   `%PROGRAMFILES% (x86)` until the service process reloads v6. Fix: TTL/orphan clear of
>   `update_in_progress` (watchdog + SilentUpdateWorker); clear flag if helper `Process.Start`
>   fails; GUI force bypasses 1h `apply_failed`; post-Inno `sc stop/start` of the DLP service;
>   `ALPHA_UPDATE_DOWNLOAD_JITTER_SEC` (default 3600) + honor download `Retry-After`; server
>   `RELEASE_DOWNLOAD_MAX_CONCURRENT` (default 25) + `Retry-After` on `/downloads`. Client
>   **VERSION 1.3.7**. Verified: `dotnet build`/`dotnet test`, `go build`/`go vet`/`go test`
>   handlers. Installer-Parity: re-bake `config.enc` + Windows installer; restart API for
>   download gate env.
>
> - 2026-11-11: **AppLocker PF(x86) prefix order (DlpService).** DB policy for Edge was correct
>   (`…\Program Files (x86)\Microsoft\Edge\Application\*`), but `NormalizeAppLockerPath`
>   matched `C:\Program Files` first → invalid `%PROGRAMFILES% (x86)\…` (AppLocker looks for a
>   literal ` (x86)` folder). Fix: directory-boundary prefix + PF(x86) before PF; rewrite
>   broken `%PROGRAMFILES% (x86)` macros; fingerprint `v6`. Client **VERSION 1.3.6**.
>   Verified: `dotnet build` DlpService + normalize sanity. Installer-Parity: next Windows
>   installer; no republish required (path in DB already OK).
>
> - 2026-11-11: **AppLocker Edge allow broken under Program Files (x86) (DlpService + server).**
>   Root cause: `NormalizeAppLockerPath` emitted `%PROGRAMFILES(X86)%`, which AppLocker does
>   not understand (only `%PROGRAMFILES%`, which covers both PF roots). Task Manager
>   (`%SYSTEM32%`) unblocked; Edge stayed default-deny. Fix: map both Program Files roots to
>   `%PROGRAMFILES%`; fingerprint `v5` forces re-apply; rewrite legacy `(X86)` macros;
>   `AppProcessGuard` expands `%PROGRAMFILES%` to both roots. Server: `WidenBrowserAllowPath`
>   publishes Edge/Chrome `Application\*` (versioned helpers) + Edge well-known fallback.
>   Client **VERSION 1.3.4**. Verified: `dotnet build` DlpService; `go build`/`go vet`
>   repository+services. Installer-Parity: next Windows installer for DlpService half;
>   restart API for widen-on-publish.
>
> - 2026-11-10: **Merge amircode + sohaib (`marged-11-10-2026`).** Keeps screenshots / scale /
>   timedatewarning / delete-block from amircode and Port Control + same-site API proxy from
>   sohaib. Port migrations renumbered to **066** / **067** (after screenshots **065**).
>   Client **VERSION 1.0.16**.
>
> - 2026-11-10: **Clock/timezone warning subprocess (client).** `TimeDateWarningWorker` checks on
>   tracker start then every hour: skew vs `GET /api/v1/server-time` + OS TZ vs IP IANA timezone
>   (`LocationProbe` `timezone` field; shift schedule fallback). Unhealthy → writes
>   `timedatewarning/state.json` and spawns `client --timedatewarning` (sibling mutex, Avalonia
>   window: Open Date & Time settings / Dismiss with cooldown). Never auto-sets the OS clock.
>   Env: `ALPHA_TA_TIMEDATE_*`. Client **VERSION 1.0.13**. Verified: `dotnet build` client.
>   Installer-Parity: re-bake `config.enc` + Windows installer.
>
> - 2026-11-10: **Screenshot duplicate-upload flood (client).** Root cause: SyncWorker deleted
>   pending while `File.OpenRead` still held the Windows lock → webp orphaned without JSON;
>   `GuessMetaFromName` folded screenshotId into `machineId` and `ListPending` minted a new
>   Guid each poll → ~90 byte-identical server rows per real capture. Fix: read bytes then
>   close before delete; parse trailing UUID from basename; never mint ids on list. Media path
>   kept `media/screenshots/{emp}/{yyyy}/{MM}/{dd}/`. Client **VERSION 1.0.12**. Verified:
>   `dotnet build` client. Installer-Parity: next Windows installer.
>
> - 2026-11-10: **Screenshot upload idempotency.** Client mints `screenshotId` UUID at capture
>   (sidecar + form field); server uses it as `screenshots.id` PK. Replay of a lost-200 returns
>   `{accepted:true, idempotentReplay:true}` without a second file. Fast-path GetByID before
>   reading the multipart body. Verified: `go build`/`go vet`, `dotnet build`.
>
> - 2026-11-10: **Offline-first screenshots (client + server + web).** Two hosted workers:
>   `ScreenshotCaptureWorker` (30s still → WebP → LocalAppData `screenshots/pending/`, no
>   network) and `ScreenshotSyncWorker` (DeviceAuth multipart upload; delete local only on
>   2xx `accepted`; Retry-After on 429/503). Server migration **065** indexes rows; files at
>   `SCREENSHOTS_DIR` default `media/screenshots/{emp}/{yyyy}/{MM}/{dd}/` (**no retention
>   purge** this ship); `POST /screenshots/upload` + JWT `GET /screenshots` + `/image`
>   (`ServeContent`). Web `/screenshots` + journey screenshots: infinite lazy grid. Client
>   **VERSION 1.0.10** + `ALPHA_SCREENSHOT_*`. Verified: `dotnet build`, `go build`/`go vet`,
>   `npx tsc --noEmit`. Installer-Parity: re-bake `config.enc` + Windows installer.
>
> - 2026-11-10: **Live theater false LIVE on offline tiles (web).** SFU `ontrack` stuck
>   `status=live` (black + `— FPS`) while sidebar correctly said Offline; status ticks kept
>   LIVE when `clientConnected=false`. Fix: watch socket clears to `offline` + drops the
>   MediaStream when the publisher is gone; tile Offline overlay/badge; LIVE only when
>   decoded FPS exists. Verified: `npx tsc --noEmit`.
>
> - 2026-11-10: **Live-stream VP8 — ship VCRUNTIME140 with Windows client (vpxmd load).** Root cause
>   of `LIVE` + SFU `withTrack=false` / `stream_encode_error` `DllNotFoundException: Unable to load
>   DLL 'vpxmd'` on machines without VC++ 2015-2022 x64: `vpxmd.dll` was already in the install dir
>   but LoadLibrary failed on missing `VCRUNTIME140.dll`. Fix: `publish/ensure-vpx-native.ps1` (+
>   `.sh` / MSBuild AfterBuild+Publish) copies app-local `vcruntime140.dll` (+ `_1`) into build/
>   publish output; Inno installs it via `windows\*`; optional `ALPHA_BUNDLE_VC_REDIST=1` downloads
>   `vc_redist.x64.exe` for quiet system install. Runtime: `VpxNativeBootstrap` preloads CRT +
>   resolves `vpxmd` from `AppContext.BaseDirectory`; probe clears/writes `stream_encode_error`
>   before encode. Client **VERSION 1.0.8**. Verified: `dotnet build` client. Installer-Parity:
>   rebuild Windows installer / drop into `server/setup/`. Lab: `TESTING.md` live-stream VP8 note.
>
> - 2026-11-10: **Scale foundation for ~1000 agents / 100 live watches / 100 admins (one Postgres).**
>   Server: rate limits on login + DeviceAuth sync + admin list GETs (`Retry-After`); sync
>   concurrency gate (`SYNC_MAX_CONCURRENT`, 503 when full); Redis dashboard summary cache +
>   singleflight; Asynq embedded worker/scheduler for session/retention/staleness (or
>   `cmd/worker` when `ASYNQ_EMBED=false`); `LIVE_STREAM_MAX_STREAMS` default **100**. Client:
>   `ALPHA_SYNC_LOGIN_JITTER_SEC` (default 120) + honor `Retry-After` on 429/503 (never mark
>   synced). Web live-stream polls `onlineOnly` + `limit=200`. Rules: *Single-Postgres* +
>   *Scale/Admission* in §6 + `prompt.md`. Plan: `newplan.md`. Verified: `go build`/`go vet`/
>   admit+cache tests; `dotnet build` client; `npx tsc --noEmit` as available.
>
> - 2026-11-10: **Delete-block ACL + stricter AppLocker bootstrap + Windows Settings allow path
>   (client / server / web).** DlpService: new `DeleteBlockAclEnforcer` — under published
>   `lockdown: true` (same pause/park/teardown clear as network/apps), Deny Delete +
>   DeleteSubdirectoriesAndFiles for Authenticated Users on every Fixed/Removable volume root
>   (create/edit stay allowed; SYSTEM/Administrators not denied). Local switch
>   `ALPHA_DLP_DELETE_BLOCK` (default `true`; `false` → `disabled_local` clear); marker
>   `%ProgramData%\<APP_MUTEX>\dlp\delete_block_roots.json`; pipe `Health.deleteBlock`; audits
>   `delete_block_apply` / `delete_block_clear`. AppLocker: PowerShell is **SYSTEM-only**
>   (S-1-5-18) so `Set-AppLockerPolicy` still works; interactive users stay default-deny until
>   `/allowlists/apps`; Settings / ImmersiveControlPanel / broad WindowsApps wildcards removed
>   from bootstrap A; fingerprint `v3`. `AppProcessGuard` force-kills interactive
>   PowerShell/Settings/cmd/terminal (session > 0) even when path lookup fails. Server: catalog
>   allow create accepts optional path override + well-known Windows Settings EXE/Appx product;
>   rejects bare Linux shell tokens (`cinnamon-settings` / …); inactive inventory paths used as
>   fallback; duplicate catalog allow is idempotent (return existing + auto-publish). Web
>   `/allowlists/apps`: **Windows Settings (Win10/11)** preset + install-path override + Linux
>   catalog warning. Client **VERSION 1.0.6**. Lab: `TESTING.md` §H steps 15–17. Installer-Parity:
>   re-bake + rebuild Windows installer so `service.env` carries `ALPHA_DLP_DELETE_BLOCK`.
> - 2026-11-10: **Auth 401 on `/auth/refresh` fixed — same-site API proxy restored.** `web/.env` pointed
>   `NEXT_PUBLIC_API_URL` at `http://192.168.88.44:8000/api/v1`, so a page on `localhost:3000` called the
>   API cross-site; the server's `SameSite=Lax` httpOnly cookies were never sent (no `Cookie` header) and
>   `/auth/check` / `/auth/refresh` always failed → bounce to `/login`. `next.config.ts` had no rewrites
>   despite the docs. Added `rewrites()` `/api/v1/:path*` → `API_PROXY_TARGET` (server-side env, default
>   `http://localhost:8080`); `.env` now leaves `NEXT_PUBLIC_API_URL` unset (client uses relative
>   `/api/v1`). `<body suppressHydrationWarning>` for extension-injected attributes (ColorZilla).
>   WebSockets still use `NEXT_PUBLIC_WS_URL` (ticket auth, not cookies). Verified via `localhost:3000`:
>   login 200 → check authenticated → refresh 200 → logout 200; `npx tsc --noEmit`.
>
> - 2026-11-10: **Port blocking reliability + instant policy delivery (DlpService + server + tracker).**
>   Root causes from testingpc (EMP-10003): (1) disabled devices were never recognised as
>   disabled — `Get-PnpDevice` reports them as `Status=Error`, not `Disabled`, so every block
>   was "Failed", retried forever and never recorded for Unblock; (2) a re-enabled Ethernet
>   adapter stayed online until the next slow pass (11 rules × up to 3 PowerShell spawns);
>   (3) rules looked "restart-only" because the policy pull interval was read once at service
>   start and nothing woke the PC on publish. **Fix:** new `CfgMgrDevices` (native cfgmgr32:
>   locate/status/disable-persist/enable/list-by-class/class-guid; disabled = problem code 22;
>   veto → `Vetoed`/pending, PowerShell only as fallback in the full pass); new
>   `PortGuardWorker` re-disables any re-enabled blocked device every 2 s (native only, 60 s
>   back-off on veto) and raises a `device_control` / `reblocked` alert (10 min per-device
>   rate limit). Policy delivery: server `ws.Hub.SubscribePolicy/BroadcastPolicyChanged`,
>   presence socket writes `{"type":"policy_changed"}` after every `DlpService.Publish`;
>   tracker `WsClient` forwards it as pipe op `RequestPolicyPull`; `DlpSignals` wakes
>   `PolicyPullWorker` (interval now re-read each pass) and then `EnforceWorker`. Verified:
>   `dotnet build` client + DlpService 0/0, `dotnet test` 37/37, `go build`/`go vet`,
>   `go test ./internal/ws`; native wrapper read-only against live devices (enumeration,
>   class GUID, problem codes match `Get-PnpDevice`). Disable/enable path needs an elevated
>   installed-service lab run. Installer-Parity: rebuild the Windows installer; restart API.
>
> - 2026-11-07: **Hardware & Port Control (scan + block, server + client + DlpService + web).** New
>   feature: scan every physical port / interface / device on each Windows PC and block/allow/alert it
>   per employee, per machine, or fleet-wide. **Server:** migration **066** (`machine_ports` inventory,
>   `device_control_rules` with `scope`/`scope_value`, `dlp_policy_device_control` snapshot) + **067**
>   (idempotent re-create — renumbered after screenshots **065**). DeviceAuth
>   `POST /hardware-ports/sync`; JWT `GET /hardware-ports`, `/device-control/rules` CRUD (auto-publish).
>   `Publish` folds **global** rules into the bundle (`SnapshotDeviceControl`); employee/machine/
>   department rules resolve per-device at pull (`ResolveScopedDeviceControl` → `mergeDeviceControl`).
>   **Client tracker:** `PortDeviceCollector` (Get-PnpDevice → `machine_ports` SQLite → sync),
>   `ALPHA_PORT_SCAN_ENABLED` (default true). **DlpService:** `PortDeviceEnforcer` (instance →
>   `Disable-PnpDevice`; class → Device Installation Restrictions retroactive; `Clear` re-enables),
>   wired into `EnforceWorker` + park-clear, local switch `ALPHA_DLP_PORT_BLOCK` (`DlpConfig` +
>   `EnvBootstrap`). **Offline-first fix (root cause of "blocking never applied"):** `PolicyStore`
>   round-trips the applied bundle through `dlp.db` relational tables and rebuilds it, but previously
>   **dropped `deviceControl`**, so `EnforceWorker` always saw an empty block set. New
>   `policy_device_control` table + persist on apply + emit on rebuild — port rules now survive offline
>   and actually enforce. **Web:** `/device-specs/ports` (Device Specs → Ports Control) — inventory by
>   category with per-device descriptions, Block/Alert/Unblock, scope selector; `portControlApi`.
>   Video outputs + internal system disks are detect/alert-only (never hard-disabled). Verified:
>   `go build`/`go vet`; `npx tsc --noEmit`; `dotnet build` client + DlpService (0/0); live scan on a
>   Windows PC (52 devices classified); SQLite upsert/`is_synced` round-trip. Installer-Parity: rebuild
>   client installer + re-bake `config.enc` so `ALPHA_PORT_SCAN_ENABLED` / `ALPHA_DLP_PORT_BLOCK`
>   reach installed PCs; restart API for 066/067.
>
> - 2026-11-07: **Local blocker switches in client `.env` (DlpService).** Four `true|false` keys decide
>   whether this PC applies each blocker at all: `ALPHA_DLP_USB_BLOCK` (USB storage),
>   `ALPHA_DLP_WEB_BLOCK` (Web Blocker domains), new `ALPHA_DLP_NETWORK_LOCKDOWN` (default-deny
>   outbound 80/443 firewall, `/allowlists/web`) and new `ALPHA_DLP_APP_BLOCK` (AppLocker + running-app
>   kill, `/allowlists/apps`). `false` = never applied even when the published server policy blocks,
>   and any leftover from an earlier run is cleared (audit state `disabled_local`; USB `false` now lifts
>   a stale USBSTOR block on the first tick). Default `true` (unchanged behaviour). Flow: `.env` →
>   `generate-windows-vars.sh` → Inno `service.env` / `install-service.ps1` / `.deb`; `EnvBootstrap`
>   seeds missing keys as `true`; pipe `Health` reports all four. Read at service start (restart the
>   service after editing `service.env`). Verified: `dotnet build` DlpService + client (0 warnings),
>   `dotnet test` 37/37. Installer-Parity: re-bake + rebuild installer to ship non-default values.
>
> - 2026-11-07: **Client auto-update from the server `setup/` folder (GitHub removed).** Server:
>   `GitHubReleaseService` + `GITHUB_*` env deleted; new `LocalReleaseService` serves installers
>   dropped in `RELEASES_DIR` (default `server/setup`, e.g. `v1.0.0.exe`; version = first X.Y.Z in the
>   filename; optional `v1.0.0.md` notes) on the unchanged `GET /api/v1/releases` +
>   `GET|HEAD /api/v1/downloads/:assetId` contract (Range via `http.ServeContent`). Client: Windows
>   service `SilentUpdateWorker` and GUI `AppUpdateService` check every
>   `ALPHA_UPDATE_CHECK_INTERVAL_SEC` (default 60s; replaces `ALPHA_UPDATE_AUTO_CHECK_HOURS`) and
>   self-install a newer version (failed apply → 1h retry guard). `publish/release.sh` now builds and
>   copies installers into `server/setup/` (no `gh`, no git push). Web: wording only. Verified:
>   `go build`/`go vet`/`go test` (services+handlers), `dotnet build` client + DlpService, `dotnet test`
>   37/37, `npx tsc --noEmit`. Installer-Parity: re-bake `config.enc` + rebuild installer; existing
>   installs gain the 60s key via `EnvBootstrap` ship defaults on the next service start.
>
> - 2026-11-06: **P3 AppLocker launch block (Exe + Appx).** Blocked apps get the Windows
>   “blocked by your organization” dialog on double-click — not open-then-kill.
>   `AppAllowEnforcer` enforces **Exe + Appx** (Store Calculator / Photos / etc.), MSI
>   empty-deny, UTF-16 policy XML, AppIDSvc start+restart after apply, and
>   `Test-AppLockerPolicy` verify that Taskmgr is Denied. Bootstrap A keeps Explorer /
>   Start / shell + product; kill sweeper remains backup for already-running EXEs.
>   Verified: `dotnet build` DlpService. Installer **2.10.2+**.
>
> - 2026-11-05: **P3 app kill sweeper.** AppLocker only blocks *new* launches (and can
>   sit inert without AppIDSvc). `AppProcessGuard` kills running EXEs outside bootstrap A
>   + published `allow.apps` paths every Enforce tick while lockdown is ON. AppIDSvc
>   started before `Set-AppLockerPolicy`. Verified: `dotnet build` DlpService. Installer
>   **2.10.1+**.
>
> - 2026-11-05: **P3 Application allowlist (fail-closed + catalog).** Migration **064**
>   catalog-backed draft apps (`installed_application_id` / `app_name` / `install_path`);
>   Publish expands to path/publisher/sha. `/allowlists/apps` picks from the same
>   inventory catalog as Configuration → Applications. DlpService `AppAllowEnforcer`
>   AppLocker EXE default-deny (bootstrap A: product + Explorer/Start/shell; Calculator /
>   Task Manager blocked until allowed). Same `lockdown` switch as network. Verified:
>   `go build`/`go vet`, `dotnet build` DlpService, `npx tsc --noEmit`. Restart API for
>   **064**. Installer-Parity: next Windows installer for AppLocker.
>
> - 2026-11-05: **Allowlists auto-publish.** Create/update/status/delete (and web
>   default-deny switch) call `POST /dlp/rules/publish` immediately; Publish buttons
>   removed from `/allowlists/web` and `/apps`. Verified: `npx tsc --noEmit`.
>
> - 2026-11-05: **Allowlists Active/Inactive.** Migration **063** adds `enabled` on draft
>   hosts/apps. Row Switch + status filter on `/allowlists/web` and `/apps`. Publish only
>   snapshots `enabled=true` rows. Verified: `go build`/`go vet`, `npx tsc --noEmit`.
>   Restart API for **063**.
>
> - 2026-11-05: **Allowlists list UI + audit.** Migration **062** adds id/note/created_by/
>   updated_by on draft hosts + apps. Paginated CRUD `GET|POST|PATCH|DELETE
>   /dlp/lockdown/hosts|apps` with search + createdBy filter. Pages `/allowlists/web` and
>   `/allowlists/apps` use infinite scroll tables (creator, timestamps). Publish unchanged.
>   Verified: `go build`/`go vet`, `npx tsc --noEmit`. Restart API for **062**.
>
> - 2026-11-05: **Network + hosts only (no Chromium HKLM enforce).** Web allow/deny is
>   hosts + `AlphaAI-DLP-*` / `AlphaAI-DLP-LOCK-*` for every app. Service clears legacy
>   Chrome/Edge URL/DoH/Quic policy leftovers; does not write them. Publish→allow is
>   mid-session. Verified: `dotnet build` DlpService. Installer-Parity: rebuild Windows
>   installer. Lab: `TESTING.md` §H.
>
> - 2026-11-05: **Fail-closed web by default (no Publish to block).** After DeviceAuth,
>   DlpService applies network lockdown unless published `lockdown: false` or pause.
>   Publish is only required to expand `/allowlists/web` hosts. Migration **061** flips
>   historical snapshots to lockdown ON; local `fail_closed_default_deny_v1` migrate sets
>   `policy_lockdown=1`. Bundle JSON always emits `lockdown` true|false. Verified: `go
>   build`/`go vet`, `dotnet build` DlpService, `npx tsc --noEmit`. Installer-Parity:
>   rebuild Windows installer. Lab: `TESTING.md` §H.
>
> - 2026-11-05: **Allowlists module (server + web + DlpService).** New RBAC module
>   `allowlists` with `allowlists/web` + `allowlists/apps`. Migration **060** adds
>   draft/snapshot `dlp_lockdown_allow_apps` / `dlp_policy_allow_apps` and sets the
>   lockdown draft default **ON**. Pages `/allowlists/web` (default-deny + host chips)
>   and `/allowlists/apps` (publisher/SHA draft); lockdown UI removed from `/web-blocker`.
>   `GET|PUT /dlp/lockdown` returns `allowApps` / `maxAllowApps`; Publish emits
>   `allow.apps`. Service: post-login `ClearPinnedIps` + `FlushDns`; persists
>   `policy_allow_apps` (WDAC/AppLocker enforce remains P3). Verified: `go build`/
>   `go vet`, `dotnet build` DlpService, `npx tsc --noEmit`. Installer-Parity: next
>   Windows installer. Lab: `TESTING.md` §H.
>
> - 2026-11-05: **P2 Chrome/Edge URLAllowlist (DlpService).** Under published
>   `lockdown: true`, `DomainBlockEnforcer` sets Chrome/Edge `URLBlocklist=*` and
>   `URLAllowlist` for platformAllow + allow.hosts + API bootstrap, minus explicit
>   web-block hosts. Lockdown off clears `URLAllowlist`. Other browsers: network deny
>   only. Verified: `dotnet build` DlpService (0 warnings). Installer-Parity: next
>   Windows installer. Lab: `TESTING.md` §H.
>
> - 2026-11-05: **P1 Windows network lockdown (server + DlpService + web).** Migration
>   **059** adds lockdown draft tables, immutable publish snapshot hosts, and
>   `employee_machines.lockdown_paused_until`. Publish snapshots `lockdown` /
>   `platformAllow` / `allow.hosts` into the existing policy version (absent =
>   today's deny-list). JWT `GET|PUT /dlp/lockdown`, `POST .../lockdown-pause`;
>   DeviceAuth `GET /dlp/policy` returns `lockdownPausedUntil`. Web admin UI later
>   moved to `/allowlists/web` (see Allowlists entry). Employees "Pause lockdown…".
>   Service: `LockdownNetworkEnforcer` (AdvFirewall complement deny on outbound
>   80/443; bootstrap + allow IPs carved out). Missing `lockdown` keeps deny-list
>   path. Teardown / `ALPHA_DLP_ENABLED=false` clears `AlphaAI-DLP-LOCK-*`. Verified:
>   `go build`, `dotnet build` DlpService (0 warnings), `npx tsc --noEmit`.
>   Installer-Parity: next Windows installer. Lab: `TESTING.md` §H.
>
> - 2026-11-05: **Tracking mode deleted (server + client + web).** Fixed/Roaming Start/Stop
>   is gone. Migration **058** drops `device_tracking_*` / `device_control_meta` /
>   `device_policy_audit` and the control columns on `employee_machines`. Routes
>   `/devices/control`, `/devices/tracking-events`, `PATCH .../tracking-mode`, and
>   `/device-configuration/*` are unregistered. Sync no longer piggybacks `controlEpoch`.
>   Client loses the Start/Stop modal, mode gate, control follower/poller, and service
>   `TrackingControlWorker`; watchdog always relaunches `--background`. Web loses Device
>   Configuration, Set tracking mode, and mode badges. RBAC seeder deletes
>   `configuration/device-configuration`. Collectors always run while logged in.
>   Verified: `go build` / `go vet`, `dotnet build` client + DlpService (0 warnings),
>   `npx tsc --noEmit`. Ships in the next installer (Installer-Parity).
>
> - 2026-11-05: **Terms & Conditions removed (server + client + web).** Settings page,
>   client `--terms` window, `terms_consent` / `terms_content`, and the live-stream
>   consent gate are gone. Migration **057** drops both tables. Boot seeder deletes the
>   `settings/terms-and-conditions` RBAC submodule. Live watch and push no longer return
>   `consent_required`. Tracking-mode deletion uses migration **058**.
>   Verified: `go build` / `go vet`, `dotnet build` client (0 warnings), `npx tsc --noEmit`.
>
> - 2026-11-04: **Web Activity expanded Page/URL wrap instead of horizontal scroll (web).**
>   Long search-query titles and URLs in `/employee-journey/web` expand rows widened the
>   nested pages table (CSS `truncate`/`max-w-*` do not constrain auto-layout `<td>`s), so
>   the parent `overflow-x-auto` scrolled sideways. Expanded list now uses `table-fixed`,
>   wraps Page with `break-words` and URL/identifier with `break-all`, and the colspan
>   cell uses `max-w-0` so wrap stays inside the parent width. Verified: `npx tsc --noEmit`.
>
> - 2026-11-04: **Web Activity Visits/Duration inflate on infinite scroll (server + web).**
>   `/employee-journey/web` summed Visits + Duration client-side from whichever
>   `GET /app-items` pages had loaded, so each "load more" grew the parent row
>   totals. Same class of bug as App Usage before `/app-sessions/usage`.
>   **Server:** `GET /app-items/usage` aggregates one row per domain
>   (`visitCount`, `totalDurationSeconds`, `lastVisitedAt`); `GET /app-items`
>   accepts `domain` for the chevron expand list. **Web:** page uses
>   `appItemsApi.usage` for the table; expand loads pages via infinite scroll
>   without touching parent totals. Search-engine query regrouping dropped from
>   the parent table (domains stay the GROUP BY key). Verified: `go build`/
>   `go vet`; `npx tsc --noEmit`; live SQL for ZE-102 domain aggregates.
>
> - 2026-11-03: **Attendance / timesheets Last Seen stuck while live-stream+apps online.**
>   Root cause: `/attendance`+`/timesheets` `lastActiveAt` = max(session_events, app_status
>   `last_heartbeat_at` value only). Sparse OS events freeze after last power_on; heartbeat
>   could stall post-reboot while DeviceAuth sync still refreshed `employee_devices.last_seen_at`
>   (live-stream Online = presence WS; apps = app_sessions). **Server:** `GetLastHeartbeat`
>   now MAX across machines of GREATEST(value, updated_at); `attendanceForDay` also extends
>   Last Seen from non-revoked `employee_devices.last_seen_at` in the work-day window.
>   **Client:** `SyncService` stamps `last_heartbeat_at` every authenticated pass and drains
>   `/app-status/sync` before heavy app_sessions/items so budget cannot starve heartbeat.
>   Verified: `go build`/`go vet`; `dotnet build` client. Server deploy heals live Last Seen
>   immediately; client half ships in next installer (Installer-Parity).
>
> - 2026-11-02: **Linux Tracker Controller tray hide restored.** Close/minimize again
>   hide to tray (same as Windows). `TrackingControlsSupervisor` only relaunches when
>   the process is dead (mutex gone) — does not force-show a tray-hidden modal.
>
> - 2026-10-31: **Linux Tracker Controller auto-open + relaunch.** Windows
>   `TrackerWatchdogWorker` launches `--tracking-controls` for Roaming; Linux had no
>   equivalent (user unit only starts `--background`). New `TrackingControlsSupervisor`
>   in the headless tracker opens/relaunches the modal (~5s) and refreshes Wayland env.
>   Fixed mode still exits the agent. Verified: `dotnet build` client.
>
> - 2026-10-31: **Linux DLP L5a–c (privileged daemon).** Same `DlpService` /
>   `AlphaAITracker.Service` runs as root systemd (`alpha-ai-tracker-dlp.service` in `.deb`):
>   `/var/lib/<pkg>/dlp` store + `device.auth` bridge; Unix `dlp.sock` IPC (teardown/health);
>   USB via udev mass-storage authorize=0; web via `/etc/hosts` AlphaAI-DLP markers + nftables
>   drop-set + apex re-resolve pin; schedule from existing policy bundle. Tracker
>   `DlpDeviceCredentialBridge` / `DlpPipeClient` work on Linux. Honest limit: needs root;
>   CDN pin weaker than Windows DNS-cache. Verified: `dotnet build` DlpService+client; Health
>   IPC lab. Installer-Parity: rebuild `.deb` + sudo lab for USB/hosts. Plan: `linuxplan.md` L5.
>
> - 2026-10-31: **Linux L2 Kill → auto-restart (systemd handoff).** Resources Kill previously
>   left the tracker dead because autostart / menu launched `client --background` outside
>   `alpha-ai-tracker.service` (`Restart=always` only covers systemd MainPID). Fix: early
>   `LinuxUserSystemdUnit.TryHandoffIfUnsupervised` (write/enable/start unit, exit before
>   mutex); autostart `.desktop` Exec=`systemctl --user start …`; StartLimit* moved under
>   `[Unit]` (were ignored under `[Service]`). Skip handoff for `--remove-account` /
>   `--protected-uninstall`. Lab: kill −9 MainPID → reopen ~8s. Plan: `linuxplan.md` L2.
>
> - 2026-10-31: **`--remove-account` always wipes + Linux protected-uninstall path fix (lab-proven).**
>   Plain `alpha-ai-tracker` / `client.exe --remove-account` no longer refuses Fixed mode: stop →
>   bounded drain (online) → `self-remove` → wipe always (offline timeout still wipes) → detached
>   relaunch `--background --restart`. **Product uninstall** still needs **Issue Removal Code** +
>   `--protected-uninstall --code`. Linux verify: `TrackerDeviceTokenReader` probes
>   `~/.local/share/alpha-ai-tracker` (not Windows `AlphaAITracker`); skip Windows-only
>   `device.auth` round-trip after SQLite token recovery. Web: Remove Current Device shows plain
>   `--remove-account` only; Issue Removal Code shows Linux + Windows protected-uninstall commands
>   (`lib/client-commands.ts`). Verified on this Linux VM: remove-account + coded uninstall both
>   OK. `dotnet build` client. Shared client code — next Windows installer picks this up too.
>   Plan: `linuxplan.md`.
>
> - 2026-10-31: **Linux parity L1–L3 + linuxplan.md.** Plan `linuxplan.md` (L1–L6).
>   **L1:** `DeviceControlPollWorker` → LocalAppData `control/control.json` (Fixed UI / Stop gate;
>   remove-account code gate later removed — see entry above). **L2:** `LinuxUserSystemdUnit`
>   `RestartSec=8` + linger + guard heals stale units. **L3:** Linux `teardown.ok` (LocalAppData +
>   `/var/lib/<pkg>/dlp`); disable user systemd on uninstall; `.deb` `prerm` refuses remove without
>   gate. Cooperative (not Windows SCM/Inno). Verified: `dotnet build` client. Open: next L4 silent
>   update / L5 DLP daemon.
>
> - 2026-10-31: **Device Configuration admin module + Start/Stop audit actually persists.**
>   Web **Configuration → Device Configuration** (`/configuration/device-configuration`, RBAC
>   `configuration/device-configuration`) with URL-synced tabs: **Roaming PC list**, **Tracker mode
>   types**, **Logs**. JWT `GET /device-configuration/roaming-machines|mode-types|tracking-events`
>   (infinite scroll on lists). **Roaming list is employee-grouped:** one row per employee
>   (`employees[].current` + `history[]`); chevron expands prior/old machines (not duplicate
>   employee rows). UI guards missing `employees` so a stale flat `machines` cache cannot throw.
>   Client Start/Stop was already POSTed via `TrackingEventReporter`, but every row was
>   **rejected** (`clientEventId` was `start-{guid}` not a UUID; stop `flushResult` was
>   `ok`/`timeout` outside CHECK `complete|partial|pending`). Fix: UUID ids, mapped flush values,
>   local pending queue under LocalAppData `control/pending-tracking-events.json` + follower retry;
>   Stop always enqueues even when drain fails; reboot resume does not invent a Start log. Server
>   also normalizes flush on ingest. Verified: `dotnet build` client; `go build`/`go vet`;
>   `npx tsc --noEmit`. Ships in the next Windows installer (client half).
>
> - 2026-10-30: **HR Set tracking mode — Fixed/Roaming only + Device Specs picker UX.**
>   `SetTrackingModeDialog` drops **Use organization default** (no UI `null` inherit); default
>   selection is **Fixed**; Save always PATCHes `fixed`|`roaming`. Device Specs header machine
>   trigger shows only `{employee} ({hostname})` — status + mode badge stay in the open menu
>   (and the Tracking mode card), so the `h-9` control no longer wraps. API still accepts
>   `null` inherit for org/dept policy paths (P3). Plan §17. Verified: dialog + `EmployeePage`
>   trigger. `npx tsc --noEmit` as needed.
>
> - 2026-10-30: **Roaming Start/Stop UX complete through client 2.3.12 (post-P2).**
>   Offline `LocalRoamingControl` + `TRACKING_START`/`STOP` IPC; Stop force-disconnects
>   WS/WebRTC (`ForceDisconnect` / `Resume`); day working timer (Start→count, Stop→freeze,
>   local midnight→0); `--tracking-controls` tray hide (─/× → tray, process stays);
>   responsive modal (10%×5% screen, HH:MM + smaller :SS); **celebration toast removed**
>   (no `UpdateCelebrationWindow` / no `--celebrate-update` UI; `celebrate.json` cleared only);
>   `service.env` upserts `ALPHA_CONTROL_POLL_SEC` (+ DLP knobs) from bake `.env`;
>   `TrackingControlsActivation` named pipe so tray / second launch / GUI reopen the modal;
>   MainWindow rail **Tracker control** (roaming-only, `TimerGeometry`). Plan: `newplan.md`.
>   Verified: `dotnet build` client + `AlphaAITracker.Service`. Installer-Parity: ship **2.3.12+**.
>
> - 2026-10-28: **P2 Roaming Start/Stop + Control Shell (client + DlpService).**
>   `control.json` desired×phase SM (`TryRequestStart/Stop`, `ReportPhase`); gate roles
>   Full / DrainOnly / ControlShell; `PauseTracking` + `PurgeSyncedAsync` + follower drain;
>   watchdog launches `--drain-only` / `--control-shell`; Avalonia `--control-shell` /
>   `--tracking-controls` popup; `TrackingEventReporter`. DLP unchanged while stopped
>   (`keep_dlp_when_stopped`). Plan: `newplan.md`. Verified: `dotnet build` client + service.
>   Installed lab §16 + Installer-Parity open (superseded UX notes → 2026-10-30 entry).
>
> - 2026-10-28: **P1 Fixed lockdown (client + DlpService).** *(2026-10-31: Fixed no longer blocks
>   plain `--remove-account` — wipe is always allowed; see top changelog.)* Originally: Fixed
>   refused plain `--remove-account` unless `--code`. Still in force: `TrackingModeResolver`
>   (pipe → ProgramData `control.json` → fail-safe Fixed on Windows; roaming on Linux/macOS);
>   pipe `RequestStop`/`RequestStart` require interactive-user SID (`PipeCallerGuard`); Fixed
>   `RequestStop` → `mode_fixed`. Plan: `newplan.md`. Verified: `dotnet build` client +
>   `AlphaAITracker.Service`.
>
> - 2026-10-28: **HR web — Set Fixed/Roaming tracking mode from Employees / Device Specs.**
>   JWT `PATCH /employees/:id/machines/:machineId/tracking-mode` (`fixed`|`roaming`|null inherit);
>   bumps `control_version` + global epoch; `device_policy_audit`. Detail `devices[]` projects
>   `effectiveMode` / `effectiveModeSource` / `desiredState` / `phase`. Web: Employees row
>   **Set tracking mode…** dialog + Device Specs mode chips / Change mode (`SetTrackingModeDialog`,
>   `TrackingModeBadge`). *(2026-10-30: dialog Fixed/Roaming only, default Fixed — see entry above.)*
>   Plan §17. Verified: `go build`/`go vet`; `npx tsc --noEmit`. Needs migration **056** on the
>   live DB. Roaming Start/Stop client UI still P2.
>
> - 2026-10-28: **Fixed/Roaming tracking control — P0 spine (server + Windows service + client gate).**
>   Migration **056** adds `device_tracking_policies` (org seed `fixed`), `device_control_meta`,
>   `device_tracking_events`, `device_policy_audit`, and control columns on `employee_machines`.
>   DeviceAuth: `GET /devices/control` (hourly profile/mode poll) + `POST /devices/tracking-events`
>   (idempotent; stop rejected for fixed); sync responses piggyback `controlEpoch`.
>   **DlpService:** `TrackingControlStore` (`%ProgramData%\<APP_MUTEX>\control\control.json`) +
>   `TrackingControlWorker` (`ALPHA_CONTROL_POLL_SEC` default 3600, independent of DLP enable) +
>   pipe `GetTrackingState` / Health tracking fields. **Client:** `TrackingRunMode` +
>   `TrackingModeGate` (P0 always `Full` — no user-visible Stop yet). Plan: `newplan.md`.
>   Decisions locked: keep DLP when tracking stopped; offline removal = bounded drain then wipe.
>   Verified: `go build`/`go vet`/`go test` services; `dotnet build` client +
>   `AlphaAITracker.Service`. Installer/`config.enc` re-bake + live migration + installed lab = next.
>
> - 2026-10-27: **Unsynced DLP alerts are not deleted.** The 10-day age prune and the
>   50k unsynced cap are gone. A local alert and its evidence stay until this response's
>   `acceptedIds` lists that id (an empty array deletes nothing; a missing field deletes
>   nothing). Startup still deletes `is_synced = 1` leftovers, orphan evidence, audit past
>   200 rows, and policy snapshots past the newest 3. A large `dlp.db` or low disk space
>   writes an `enforcement_audit` `disk_pressure` row and does not drop unsynced alerts.
>   `permission_status` cleanup deletes only `is_synced = 1` rows older than a day.
>   Session-event `old_data_dropped` is unchanged. Protected uninstall republishes
>   `device.auth` from the current user's tracker SQLite when the file is missing, then
>   still calls `POST /devices/removal-verify`. The GUI Update button polls the service
>   when the pipe returns `ok`, and uses the UAC installer only for `unknown_op`.
>   Ships in the next Windows installer.
>
> - 2026-10-27: **Local retention — client log, update log, policy snapshots, dlp.db.**
>   `dotnetrunlog.txt` was appending across restarts (LocalAppData file from 2026-10-21, ~1.4 MB).
>   `FileLoggerProvider` now opens with `FileMode.Create` so each client start replaces the previous
>   run. Silent apply deletes `%ProgramData%\<APP_MUTEX>\updates\update.log` before writing the new
>   apply. `PolicyStore` keeps the latest **3** `policy\policy.vN.json` files (numeric version, so
>   v110 beats v99), deletes alerts the server already accepted (and their evidence) instead of
>   leaving `is_synced=1`, and caps `enforcement_audit` at the newest 200 rows. Startup also sweeps
>   leftovers and `VACUUM`s when rows were removed. Unsynced alerts are not deleted for
>   age or count. No new env knob. Verified: `dotnet build` client +
>   DlpService. Ships in the next Windows installer.
>
> - 2026-10-27: **Chrome-like silent Windows updater (DlpService + client).** Auto-update no longer
>   prompts UAC after the first admin install. **Service:** `SilentUpdateWorker` (independent of
>   `ALPHA_DLP_ENABLED`) checks `GET /api/v1/releases`, Range-downloads to
>   `%ProgramData%\<APP_MUTEX>\updates\` while the tracker keeps running, then spawns a detached
>   SYSTEM helper that runs Inno `/VERYSILENT` (no `RunAs`) and clears `update_in_progress`.
>   Watchdog skips mid-apply. Pipe ops `GetUpdateStatus` / `RequestUpdateCheck`; named pipe stays
>   up for update IPC even when DLP is parked. **Client:** Windows `AppUpdateService` only queues
>   the service (never elevates). Linux/macOS paths unchanged. `service.env` seeds `ALPHA_UPDATE_*`.
>   *(2026-10-30: post-update celebration toast / `celebrate.json` launch path removed — apply still
>   silent.)* Verified: `dotnet build` DlpService + client. Ships in the next Windows installer.
>
> - 2026-10-26: **Public `/releases` + private GitHub download bridge (server + web).** Repo can stay
>   private while anyone (logged out) browses published releases and downloads Windows `.exe` /
>   Linux `.deb` installers. **Server:** `GITHUB_OWNER` / `GITHUB_REPO` / `GITHUB_TOKEN` (Contents:
>   Read) + `GITHUB_API_VERSION` / `GITHUB_RELEASES_CACHE_TTL`; public `GET /api/v1/releases`
>   (paginated GitHub metadata, drafts excluded, in-memory TTL cache) and `GET|HEAD
>   /api/v1/downloads/:assetId` (asset-id validation against published installers only; stream
>   with bounded memory; Range forwarded; Authorization stripped on redirect; SSRF host allowlist;
>   soft per-IP rate limit). Empty token → those routes **503** without failing boot. **Web:**
>   public route group `(public)/releases` (no ProtectedRoute); sanitized Markdown notes
>   (`react-markdown` + `rehype-sanitize`); `DownloadAppSection` no longer calls GitHub from the
>   browser — uses the API bridge + native navigation (not Blob fetch). Plan: `doc/plan.md`.
>   **Client follow-up (same day):** `AppUpdateService` now uses the same bridge
>   (`GET /api/v1/releases` + `/downloads/:assetId`) instead of unauthenticated GitHub —
>   gated on `ALPHA_SERVER_URL` / `ALPHA_UPDATE_ENABLED`. Removed client `ALPHA_UPDATE_REPO` /
>   `UpdateRepo` — GitHub owner/repo lives on the API host (`GITHUB_OWNER`/`GITHUB_REPO`);
>   bare `REPO=` in `client/.env` remains only for `publish/release.sh` (`gh release create`).
>   Ships in the next installer. Verified: `go test` services+handlers; `go build`/`go vet`;
>   `npx tsc --noEmit`; `dotnet build` client. Live Range/`curl` against a real asset needs
>   `GITHUB_TOKEN` on the API host.
>
> - 2026-10-24: **DLP web UX — alerts domains + full DLP rules CRUD/history + status Switch.**
>   `/dlp-alerts` list shows blocked domain chip from evidence; detail Domains section is a
>   per-domain list (not CSV). `/dlp-rules` gains edit + detail dialog + revision history
>   (migration **055** `dlp_rule_revisions` + child extension/pattern tables; `GET/PATCH`
>   history endpoints mirror Web Blocker) and Active/Inactive Switch with auto-publish.
>   `/web-blocker` list + form status use the same Switch control. Verified: `go build`/`go
>   vet`; `npx tsc --noEmit`.
>
> - 2026-10-24: **Reusable searchable IANA timezone picker (web).** New
>   `web/src/components/TimezoneCombobox.tsx` (`@vvo/tzdb` + cmdk). Used by `/web-blocker` schedule
>   and `/shifts` forms so admins pick real zones (e.g. Asia/Karachi) instead of free-text that
>   can break Windows DLP schedule eval (silent UTC fallback). Verified: `npx tsc --noEmit`.
>
> - 2026-10-24: **Production web-block reconcile + DNS-pin generation lock (DlpService).** Root cause
>   of YouTube staying blocked after Inactive while Facebook cleared: orphan
>   `AlphaAI-DLP-PIN-*` firewall rules (CDN IPs, not in `dlp.db` / not named per-site) plus pin
>   worker racing clears. **Fix:** Apply bumps `PolicyGeneration` under `PinGate`, honest
>   `ClearPinnedIps` (verify + fail), `PinRemoteIps(ips, gen)` no-ops on stale gen; every Enforce
>   tick reconciles via `OsStateMatchesDesired` (`web_domain_reconcile`); empty pin worker also
>   clears PIN leftovers. Lab: `TESTING.md` §F.1–F.2. Verified: `dotnet build` DlpService.
>
> - 2026-10-24: **Remove localhost block page + fix stale blocks after rule delete (DlpService +
>   tracker).** Root cause of “`dlp.db` empty but Chrome still blocks”: OS leftovers
>   (especially `HKLM\...\Chrome\URLBlocklist`) with fingerprint short-circuit after a silent
>   clear failure. **Fix:** `ClearBrowserPolicies` no longer swallows delete failures;
>   EnforceWorker does not fingerprint-advance on `access_denied` / `browser_error` / incomplete
>   clear; `HasLeftoverOsArtifacts` self-heals hosts / `AlphaAI-DLP-*` / URLBlocklist each tick
>   (`web_domain_clear` audit). **Removed:** `BlockPageServer` / `BlockHttpsServer` /
>   `BlockPageCertStore`, tracker `WebBlockUxService` + `IWebBlockHitSink`, `ALPHA_DLP_BLOCK_PAGE*`.
>   Lab: `TESTING.md` §F.1. Verified: `dotnet build` DlpService + client.
>
> - 2026-10-24: **P2 Web Blocker hardening — mid-session YouTube without PC/browser restart (service +
>   web + server).** Root gap: hosts cannot wildcard `rr*---sn-*.googlevideo.com`, and apex firewall
>   IPs miss rotating CDN nodes; TCP-kill alone misses QUIC. **Service:** incremental
>   `AlphaAI-DLP-*` firewall + IP resolve cache (TTL); `TcpConnectionKiller` (`SetTcpEntry`); Chrome/Edge
>   DoH-off + optional URLBlocklist/QuicAllowed; new **`DnsBlockPinWorker`** polls DNS client cache →
>   pins live CDN IPs into `AlphaAI-DLP-PIN-*` + TCP-kill (no winsock/NIC reset, no employee restart).
>   EnforceWorker forces hosts re-apply on every new policy version. **Web `/web-blocker`:** edit +
>   revision history (migration **054** relational child tables — no TEXT[]); Active/Inactive +
>   auto-publish; domain chips; related-domain expand (www/m + curated packs); suggestions deduped;
>   URL paths field removed from UI. **Server:** `ExpandRelatedDomains`,
>   `POST /web-blocker/domains/expand`, `GET /web-blocker/rules/:id/history`. Earlier same week:
>   `EvidenceMap` sync 400 fix; protected-uninstall hosts clear; HTTPS branded block page + local CA;
>   installer service-lock DeleteFile 5 fix. Verified: `dotnet build` DlpService; `go build`/`go vet`;
>   `npx tsc --noEmit`. Lab: `TESTING.md` §F.
>
> - 2026-10-23: **P2 Web Blocker (web + Windows service) — domain DNS sink + schedule.** New web
>   route `/web-blocker` (RBAC `web-blocker` under `security-dlp`) authors `web_navigate` rules with
>   `match.domains` / `match.paths` / `match.schedule`; `/dlp-rules` keeps non-web triggers.
>   Service: `DomainBlockEnforcer` rewrites system hosts with `# AlphaAI-DLP` markers (plan “DNS
>   sink”); `EnforceWorker` applies schedule-aware domain sets; teardown clears the sink; apply
>   alerts sync as `web_navigate`. Path patterns are stored for later alert-match (hosts cannot
>   hard-block URL paths). Verified: `go build`/`go vet`; `dotnet build` DlpService; `npx tsc
>   --noEmit`. Lab: `TESTING.md` §F.
>
> - 2026-10-22: **Security & DLP P0+P1 shipped (server + Windows service + web) — USBSTOR block, policy publish, alert inbox + triage history, protected uninstall.**
>   Windows-only enforcement lives in a **LocalSystem** process (`client/DlpService` →
>   `AlphaAITracker.Service.exe`), not inside the Avalonia user helper. **Server:** migrations
>   **051** (`dlp_rules`, `dlp_policy_versions`, `dlp_alerts`, `shadow_it_apps`,
>   `removal_challenges`) + **052** (`dlp_alert_events` triage audit). DeviceAuth:
>   `GET /dlp/policy`, `POST /dlp/alerts/sync`, `POST /dlp/health`, `POST /devices/removal-verify`.
>   JWT: rules CRUD + `POST /dlp/rules/publish`, alerts list/PATCH + `GET /dlp/alerts/:id/history`,
>   Shadow IT list/PATCH, `POST /devices/removal-code`. Env `DLP_REMOVAL_CODE_TTL_MIN` (default 60).
>   RBAC module `security-dlp` with submodules `dlp-rules` / `dlp-alerts` / `shadow-it` (already in
>   `rbacCatalog`). **Client service:** PolicyPull / Enforce (`UsbStorEnforcer`) / RemovableDriveMonitor
>   / AlertSync / NamedPipe / ServiceAclHardener / TrackerWatchdog; store under
>   `%ProgramData%\<APP_MUTEX>\dlp\` (separate from user SQLite). Tracker bridges DeviceAuth via
>   `device.auth`, CLI `--protected-uninstall --code` → `ProtectedUninstallService` → pipe
>   `AuthorizeTeardown` → drain/self-remove → `teardown.ok`; Inno uninstall refuses without that
>   gate. Service/pipe/mutex names from `APP_IDENTIFIERS` (`WINDOWS_SERVICE_NAME`,
>   `WINDOWS_DLP_PIPE_NAME`). Knobs `ALPHA_DLP_*` (re-bake `config.enc`). **Web:** live
>   `/dlp-rules` (CRUD + Publish), `/dlp-alerts` (infinite scroll + URL filters + detail dialog with
>   Save-only triage + history), `/shadow-it` (catalog + Approve/Block/Flag), employees menu
>   **Issue Removal Code**. P2+ (WFP domains, Shadow IT rollup→`shadowDeny`, AppLocker, minifilter)
>   still open — see `plan.md`. Rules codified: **DLP Policy Version Rule**, **Protected Removal
>   Rule** (§6). Verified: `go build`/`go vet`; `dotnet build` (client + DlpService); `npx tsc
>   --noEmit`. Lab playbook: `TESTING.md` §DLP.
>
> - 2026-10-20: **App Usage / Hours Insights / Graphical Logs duration inflation (server + web).**
>   On `/employee-journey/apps?preset=yesterday` for IS-16, chrome/OneDrive/Grammarly/client all
>   showed **18h 34m** while the expanded session list's first open was ~09:28 and last seen ~18:30
>   (~9h). Same class of bug hit `/hours-insights` (total ~108h, per-app 18.57h) and
>   `/logs/graphical` productivity (~46h summed concurrent tabs). Root cause: overlap date filters
>   (`session open anytime during the day`) + summing concurrent sessions without wall-clock merge.
>   Multi-day zombie rows that all started `2026-10-17 18:44:29` and closed `2026-10-19 18:34:27`
>   were clipped into `[dateFrom, dateTo]` — exactly 18h 34m — while expand lists hid them behind
>   `started_at >= dateFrom`. Fix: `AggregateAppSessionsUsage`, `ListAppSessions`,
>   `GetHoursInsights` (summary + both charts), `LogGraphicalRepo.ProductivitySeries`, and
>   `LogInsightsRepo.ProductivityTotals` all use **started_at in range**; hours-insights /
>   graphical / insights scale classified shares to wall-clock (island-merge / per-bucket cap);
>   `has_open_session` requires fresh `last_sync_at`; web App Usage + Web Activity cap open-row
>   duration on historical filters. Verified: apps chrome ~9.02h; hours-insights total ~9h;
>   graphical totalSeconds ≤ 24h. `go build`/`go vet` clean.
>
> - 2026-10-20: **Live stream Linux — capture + VP8 encode (Wayland ScreenCast + GStreamer).**
>   `ScreenCaptureService` was Windows-only (`StreamAvailable=false` parked the loop on Linux), so
>   WebRTC publish never produced frames on this fleet's GNOME Wayland machines. New
>   `Services/Streaming/LinuxScreenCapturer` drives an embedded python3 probe: xdg-desktop-portal
>   `ScreenCast` (MONITOR, `persist_mode=2`) → `OpenPipeWireRemote` → GStreamer
>   `pipewiresrc ! videoconvert ! BGRx ! appsink` (no `videorate` — it starved appsink on Mutter;
>   FPS is bounded by appsink DropOldest + the C# capture interval), framed BGRA on stdout; restore
>   token at `~/.local/share/alpha-ai-tracker/stream-restore-token` so the Share dialog is once per
>   grant. Max-width scales in the probe (nearest-neighbor; GStreamer `videoscale` hit Mutter
>   negotiate overflow). ABR overrides also downscale in `CaptureFrameLinux`. Monitors via Mutter
>   `DisplayConfig.GetCurrentState`. CLI `--smoke-linux-capture`. **VP8:** SIPSorceryMedia.Encoders
>   only ships Windows `vpxmd.dll`; linking system libvpx as `libvpxmd.so` segfaults on ABI mismatch.
>   `ScreenVp8Encoder` now routes Linux through `LinuxGstVp8Encoder` (python3 + `vp8enc` appsrc/appsink
>   framed protocol). `.deb` Depends: `gstreamer1.0-plugins-good` (+ pipewire/gi). Windows GDI +
>   `vpxmd` path unchanged. Verified on this Ubuntu GNOME Wayland VM: `dotnet build` 0/0; smoke
>   capture OK; live `LIN-1` → SFU `publisher track ready … codec=video/VP8` +
>   `subscriber answer … withTrack=true`; client encode stats ~2 Mbps at 1680×1050. Ships in the
>   next Linux installer; no `config.enc` re-bake. Server/web unchanged.

> - 2026-10-19 (late): **Three fixes — post-wipe re-login re-collects hardware/network/software (client), the setup wizard can pass step 2 on Linux (client), "All devices" removed from the Device Specs dropdown (web).**
>   **1. "No hardware snapshot synced yet / No installed applications synced yet" on a device that re-logged in after a removal** — root-caused on the live dev DB (employee `MU-90`: the relogged-in machine had `app_items` / `app_sessions` / `app_status` rows but **zero** `device_hardware_info` / `network_info` / `employee_installed_applications`). The removal wipe runs inside the already-`running process: it deletes every SQLite row, but the collector's IN-MEMORY dedup fingerprints (`_lastHardwareFingerprint`, `_lastNetworkPublicIp`/`_lastNetworkPrivateIp`) survive it, and the installed-software scan only runs at STARTUP (inventory is event-driven — there is deliberately no periodic scan). The machine therefore still "looks unchanged": every periodic collect takes the same-process fast path and the startup scan never runs again, so the machine id minted for the re-login never receives a snapshot, a network row or an inventory. Fix: new `LogCollectorService.RecollectAfterLoginAsync()` drops those fingerprints (dedup then falls back to the wiped, i.e. empty, DB) and re-runs hardware + network + `RescanInventoryAsync`; `MainViewModel.LoginAsync` fires it after `StartTracking()` and wakes `SyncService` again once it finishes. Login also now wipes a leftover local DB that belongs to a DIFFERENT employee **before** the login request (with `ResetMachineId()` + `EncryptedConfigService.ResetCache()`), so the rotated machine id and the server-side device registration always agree. Restarting the client alone also heals an already-stuck machine — the startup collects run regardless of login.
>   **2. Setup wizard stuck on "SETUP STEP 2 OF 4" (Linux):** step 2's gate `IsBackgroundGuardConfigured()` requires the systemd USER unit `~/.config/systemd/user/alpha-ai-tracker.service`, but `GrantBackgroundRunningAsync` only called `EnableAutoStartForced()` — which writes the `.desktop` autostart entry and runs `systemctl --user enable` against a unit file that does not exist yet. The step printed "Background guard enabled successfully." and then re-evaluated to ITSELF forever (no forward navigation). New `AutoStartService.EnsureBackgroundGuardAsync()` writes the unit, daemon-reloads and enables it — deliberately without starting it (the wizard runs inside the live process; starting the unit would spawn a mutex-fighting `Restart=always` twin) — and the step now throws honestly if that fails. The macOS gate checks the launchd plist instead of unconditionally returning `false` (same stuck-step class).
>   **3. "All devices" removed from the Device Specs dropdown (web):** `EmployeePage` no longer renders the `__all__` sentinel — with 2+ machines it always scopes to exactly one (the URL's machine while it is still registered, else the first entry; the server orders machines active-first, then most recently tracked) and mirrors that back into `?deviceId=` (URL-Synced Filters Rule). Single-machine employees keep the employee-wide view they already got (no dropdown is rendered for them). Verified: `dotnet build` 0/0, `npx tsc --noEmit` clean, `next build` passes.
> - 2026-10-19: **Device lifecycle — `employee_machines` registry, full `machine_id` coverage, and the remove-account / remove-device wipe handshake (server + client + web).**
>   **Server:** migration **049** adds `employee_machines` — the canonical one-row-per-physical-machine
>   lifecycle record (`status` `active|old`, `first_track_at`, `last_track_at`, `wipe_requested_at`),
>   backfilled from `employee_devices`. It exists because `employee_devices` is the AUTH-TOKEN registry
>   and `UpsertDevice` revokes + re-inserts a row on every login, so one physical PC appeared as many
>   rows and the Device Specs dropdown could not tell machines apart. Every employee login upserts the
>   machine as `active` (preserving `first_track_at`); `DeviceAuth` advances `last_track_at`. Migration
>   **050** completes `machine_id` coverage on `session_events`, `app_items` and `location_samples`
>   (per-table indexes + 047-style attribution from the device activity window; `app_items` attributes
>   exactly from its parent session). `terms_consent` deliberately stays employee-scoped — a consent is
>   the person accepting terms, not the machine. **R6 handshake:** `DeviceAuth` now also resolves
>   REVOKED device tokens (`GetByTokenHashIncludingRevoked`), sets `device_removed` and skips
>   `TouchLastSeen`; every sync handler persists the incoming batch FIRST and only then adds
>   `{"action":"wipe_local"}` to the response (`applyDeviceAction`). `POST /employees/:id/devices/remove`
>   additionally marks the employee's machines `old` and stamps `wipe_requested_at`. **R4/R5:** new
>   DeviceAuth endpoint `POST /api/v1/devices/self-remove` marks the CALLING machine `old`, advances
>   `last_track_at`, revokes its device rows, closes open activity and flips the employee to
>   `untracked` (non-destructive — nothing is deleted). `GET /employees/:id/detail` now sources
>   `devices[]` from `employee_machines` (deduped to one row per machine) with `status`, `firstTrackAt`,
>   `lastTrackAt`, `wipeRequestedAt` and a corrected `isActiveDevice`.
>   **Client:** every telemetry INSERT stamps `machine_id` from a new single-row `machine_identity`
>   table, written from a lazy `AppConfig.ClientId` provider so a wipe + re-login in the same process
>   stores the NEW id (the `employee_info.machine_id` column is written on login too); `JourneyEngine`
>   no longer uses the hostname as the machine id. New `Services/AccountRemovalService.cs` owns the one
>   removal routine — stop collecting → bounded `SyncService.WaitForDrainAsync` →
>   `POST /devices/self-remove` → wipe local DB + `.machine-id` + `ResetMachineId()` — and serves the
>   `--remove-account` CLI (cold-start and background), the running-instance signal AND the
>   server-initiated wipe; `SyncService` parses the response `action` and raises
>   `OnServerRequestedWipe` (once per process). The cold-start path now starts the host first (the old
>   code fired `RequestImmediateSync()` at a host that had not started, so the drain never ran and the
>   server was never told).
>   **Web:** `EmployeeDevice` drops `isActive` for the lifecycle `status` / `firstTrackAt` /
>   `lastTrackAt` / `wipeRequestedAt`; the Device Specs dropdown shows each machine with its status
>   (`active` / `old · removed` + last tracked); the Hardware Overview empty state keys off
>   `status === 'old'`.
>   Verified live against the dev DB: 049/050 applied (24 backfilled machine rows); employee login →
>   machine `active`; hardware / session-event / location syncs stamped the device's `machine_id`;
>   admin remove → machine `old` + `wipe_requested_at` and the revoked token's next batch was
>   **persisted and** answered `{"action":"wipe_local"}`; second machine login + `self-remove` →
>   `{removed:true, machineId, status:"old", trackingStatus:"untracked"}`; detail `devices[]` returned
>   one row per machine with status. `go build`/`go vet`/`go test ./internal/...` clean, `dotnet build`
>   0/0, `npx tsc --noEmit` clean. Rule codified: **Device Lifecycle Rule** (§6).
>
> - 2026-10-17: **Dashboard — the "Fleet health" card is replaced by an "Attendance" card (server + web).**
>   The home page's fleet panel is gone; its slot now shows **today's attendance breakdown**
>   (Present / Late / Absent / Off shift, plus **Half day** and **Not recorded** rows only when non-zero)
>   for the department-filtered employee set, with a "View attendance →" link to `/attendance`.
>   **Server:** `DashboardSummaryResponse` gains
>   an `attendance` block (`date` / `total` / `present` / `late` / `absent` / `halfDay` / `offShift` /
>   `unknown`); new `TimeAttendanceService.AttendanceDayCounts(employeeIDs)` buckets each employee's
>   current-day status by resolving "today" per shift timezone and reusing `attendanceForDay`, so the
>   card agrees with `GET /attendance/today`; `DashboardService` computes it inside the existing
>   errgroup over the department-filtered `employeeRepo.ListAll` set (its constructor gained a
>   `*TimeAttendanceService`; `main.go` wiring updated). The dashboard remains a **single** summary
>   request — no list-API fan-out. **Web:** `FleetHealthCard` was removed from
>   `DashboardStatGrid.tsx` and replaced by `AttendanceCard`; `DashboardSummaryResponse` gained the
>   `attendance` type. The `devices` stats stay in the payload (the attention strip still uses
>   `devices.stale7d`). Verified: `go build`/`go vet ./...` clean, `npx tsc --noEmit` clean,
>   `next build` passes.
>
> - 2026-10-17: **Device Specs — legacy machine rows attributed to their device (migration 047) + a removed device shows its last-known software (server + web).**
>   Reported: after **Remove current device** + a new login, `/device-specs?deviceId=<old>` showed the
>   current device fine but the removed device rendered **"No hardware snapshot synced yet — the desktop
>   client will upload one after login"**, with every other device surface empty. Root cause was a gap in
>   migration **046**: it added `machine_id` with `DEFAULT ''` and then attributed the pre-existing rows
>   from `app_sessions` with `HAVING COUNT(DISTINCT machine_id) = 1` — but `app_sessions` is purged by the
>   retention worker after 30 days, so a swapped PC's retired-machine sessions are usually already gone
>   when the migration runs and **nothing** is backfilled. The old machine's `device_hardware_info` /
>   `network_info` / `storage_devices` / `hardware_devices` / `permission_status` / `app_status` / link
>   rows stayed `machine_id = ''` while the Device Specs dropdown still listed that machine by its
>   `default-EMP-XXXXX` machine id (the fallback the login handler binds when a request omits a machine
>   id) — so the machine-scoped read `machine_id = 'default-EMP-XXXXX'` matched nothing. **Server:**
>   migration **047** re-attributes each leftover `''` row from the **device registry's own activity
>   window** `[created_at, last_seen_at]`, only when exactly ONE device window contains the row's
>   server-stamped sync time (ambiguous/no-window rows stay `''` = the "unknown machine" bucket shown
>   under All devices; rows that already carry a machine id are untouched, natural-key collisions are
>   skipped — idempotent). `ListEmployeeApplications` / `ListEmployeePackages` also include
>   `is_active = false` links when the requested machine has **no live registration** (all its
>   `employee_devices` rows revoked), so a removed device shows the last-known inventory the hourly
>   staleness sweep had deactivated; the aggregate view still filters `is_active`. **Web:** the Hardware
>   Overview empty state now says *"No hardware snapshot was recorded for this device before it was
>   removed"* for a removed device instead of the false "will upload one after login". Verified:
>   `go build`/`go vet ./...` clean, `npx tsc --noEmit` clean, `next build` passes; migration 047 dry-run
>   (rolled back) then applied to the dev DB — rows attributed per table **10/3/6/21/8/37/242/4354**, 0
>   left unattributed; per-device reads for `default-EMP-10003` return hw/storage/network/peripherals/
>   status/packages and installed-software scoping measures **retired 99 / current 73 / aggregate 73**.
>   Rule codified: **Retired-Device History Rule** (§6).
>
> - 2026-10-17: **Device swap handling — "Remove current device" + per-device Device Specs (server + web + client).**
>   An employee who moved to a new PC stayed tracked on the old machine and every synced surface was
>   collapsed into one employee-wide bucket, so Device Specs could not tell the two machines apart.
>   **Server:** migration **046** adds `machine_id` to `device_hardware_info`, `network_info`,
>   `storage_devices`, `hardware_devices`, `permission_status`, `app_status` and both employee↔catalog
>   junctions; the natural keys become device-wide (`permission_status` PK `(employee_id, machine_id,
>   check_id)`, `app_status` PK `(employee_id, machine_id, key)`, links UNIQUE
>   `(employee_id, machine_id, <catalog id>)`) and a single-machine backfill attributes legacy rows.
>   **No client sync change was needed:** `DeviceAuth` now also sets `device_machine_id`, every sync
>   DTO carries a `json:"-"` `MachineID` filled by the handler, and the repos stamp it on insert.
>   `SyncDeviceHardware` also copies the reported hostname into `employee_devices.device_name`.
>   **New endpoint** `POST /api/v1/employees/:id/devices/remove` (JWTAuth): revokes every active device,
>   closes all open `app_sessions` (`status='CLOSED'`) and `app_items` (`closed_at`), sets
>   `tracking_status='untracked'` + `is_online=false`, and returns the counts — nothing is deleted, so
>   Device Specs and history stay intact. **Web:** the employees row menu shows **Remove Current Device**
>   while `trackingStatus === 'tracked'` (confirmation dialog) and falls back to **Generate Login Secret**
>   once untracked; `EmployeePage` gained a URL-synced `?deviceId=` machine selector (shown when the
>   employee has 2+ devices) wired into `GET /employees/:id/detail?deviceId=`, which scopes hardware,
>   storage, network, apps, packages, peripherals, permissions and app status to that machine. Device
>   labels are always **"{employee name} ({hostname})"** — hostname falls back to the device name, then
>   the machine id, never "Unknown device". **Client:** login now sends `deviceName = Environment.MachineName`
>   (installer ships it in the next build; the server-side hostname copy already covers older clients).
>   Verified: `go build`/`go vet` clean, `npx tsc --noEmit` clean, `next build` passes, `dotnet build` 0/0;
>   migration 046 applied to the dev DB; live API tests — detail with/without `?deviceId=` (aggregate
>   deduped, per-machine scoped), a real device-token sync stamped `machine_id` + back-filled
>   `employee_devices.device_name` from the hostname, the same `app_status` key held two device rows, the
>   remove endpoint returned `{revokedDevices:2, closedSessions:1, closedItems:1, trackingStatus:"untracked"}`
>   with device rows kept (`revoked_at`), sessions/items closed, history still readable, and a revoked
>   device token then synced → 401.
>   Rule codified: **Machine-Scoped Data Rule** (§6).
>
> - 2026-10-16: **RBAC leak — Terms & Conditions was visible to every role and guarded by an unrelated permission (server + web).**
>   Reported: a user whose role grants only the HR module saw **Settings → Terms & Conditions**,
>   and clicking it landed on `/unauthorized`. Root cause was one missing key in the nav config:
>   `web/src/components/layout/AppSidebar.tsx` added the entry with **no `module` key** (a copy of the
>   `/settings/profile` pattern, which is intentionally ungated), so `isItemVisible`/`visibleChildrenOf`
>   short-circuit to `true` and the item rendered for every authenticated user — while
>   `findModuleForPath` skipped the module-less entry in its exact-match pass and fell back to the
>   `/settings` prefix, returning `settings`. Net effect: always visible, the page was really gated by
>   the unrelated **General Settings** permission (so any role with plain `settings` also got T&C
>   create/edit/delete), and a role without `settings` got a dead nav link. The server catalog had no
>   terms key at all. Fix: `internal/services/rbac_service.go` seeds
>   `settings/terms-and-conditions` ("Terms & Conditions", route `/settings/terms-and-conditions`)
>   under `settings-module`, and the sidebar entry now carries
>   `module: "settings/terms-and-conditions"` — nav visibility and the route guard resolve to the
>   same key. `company_admin` keeps access via `GrantAllPermissions` (39 → 40 grants); no existing
>   role is auto-granted, so the item disappears until an admin ticks it on `/roles`. No migration —
>   the boot seeder upserts the catalog. Verified: `go build`/`go vet` clean, `npx tsc --noEmit`
>   clean, boot seeder against the live dev DB (Settings 7 → 8 submodules, `hr` grants unchanged at
>   6), and `GET /modules` + `GET /auth/profile` (Settings 8/8 for `company_admin`).
>   Rule codified: **RBAC Nav → Catalog Parity Rule** (§6) — a permission-gated page must
>   add the submodule to `rbacCatalog` AND the matching `module` key to the sidebar entry, in the
>   same change.
>
> - 2026-10-15: **Live stream — WebRTC signaling race dropped ICE → tiles read LIVE with zero frames (server + web + client).**
>   Root cause was signaling ORDER, not per-PC hardware: the Pion SFU starts ICE gathering inside
>   `SetLocalDescription` during `AcceptPublisherOffer` / `AcceptSubscriberOffer`, and Pion fires the
>   candidate callback on its own goroutine — so `{"type":"ice"}` could be written to the socket BEFORE
>   `{"type":"answer"}`. A peer that calls `addIceCandidate` while its `remoteDescription` is still null
>   throws `InvalidStateError`; that candidate was silently dropped, the ICE/DTLS handshake never
>   completed, yet `ontrack` had already fired (the answer carried the media section) — so the UI showed
>   LIVE / `--- FPS` and the 5s renegotiation loop spun forever. Which employees broke was pure timing
>   luck (the connections where the answer landed before the first trickled candidate worked).
>   Fix — buffer trickled candidates on all three peer roles until the remote description exists:
>   (1) **server** `internal/handlers/stream_handler.go`: new `iceTrickle` (`sync.Mutex` + `answered`
>   flag + `pending`) shared by the Push (publisher) and Watch (subscriber) paths; empty candidates are
>   skipped, pre-answer candidates are held, and `answerMarked()` flushes them in order only AFTER the
>   answer message is written. (2) **web** `lib/useLiveStreamSocket.ts`: per-PC
>   `PcEntry { pc, remoteReady, queue }` held in `pcStateRef`; the `answer` branch awaits
>   `setRemoteDescription`, then sets `remoteReady` and flushes the queue; the `ice` branch queues while
>   `!remoteReady`; `cleanupPc` nulls the entry so a renegotiated PC gets fresh state (stale candidates
>   can never hit a dead connection); `ws.onmessage` is now `async`. (3) **client**
>   `Services/LiveStreamClient.cs`: `pendingIce` + `remoteIceReady`, flushed after `setRemoteDescription`
>   returns `OK`, reset when the PC is recreated on `start`/`stop`; `ApplyRemoteIce` → `BuildRemoteIce`
>   (returns the candidate). No route, contract, env, or migration change; the client half ships only in
>   the next installer build.
>   Verified: `go build ./...` clean; `go vet ./internal/handlers/...` clean; `npx tsc --noEmit` clean;
>   `dotnet build` 0 warnings / 0 errors.
>
> - 2026-10-05: **Web dashboard redesign — summary-first home (server + web).**
>   New JWT `GET /api/v1/dashboard/summary` (date range + optional `departmentId`) returns
>   employees/activity/monitoring/devices/live counts + bounded topApps/topDomains/recentSessions
>   in one request (errgroup SQL; migration **042** `idx_app_items_type_opened` +
>   `idx_employee_devices_last_seen`). Presence list: `GET /live-stream/employees?onlineOnly&limit`
>   (backward compatible). Web `/dashboard` uses `dashboardApi.summary` + capped presence poll +
>   URL filters; no list-API fan-out; download strip dismissible. Client/installer N/A. Verified:
>   `go build`/`go vet`; `go test ./internal/services/ -run Dashboard`; `npx tsc --noEmit`;
>   `next build`.
>
> - 2026-10-03: **Live stream post-Phase-3 — Win11 blank preview (FA-27) + stale Client Version.**
>   Root cause of FA-27 `LIVE` + `--- FPS` / SFU `withTrack=false`: Win11 GDI capture
>   (`CopyFromScreen` / CAPTUREBLT-only BitBlt) failed on that machine so no RTP reached the
>   SFU; signaling still looked healthy. Fix (client **1.2.34**): multi-path BitBlt
>   (`SRCCOPY` → CAPTUREBLT → desktop HWND → `CreateDC("DISPLAY")` → CopyFromScreen fallback);
>   `SendVideo` only after WebRTC `connected` + force keyframe; honest
>   `stream_capture_stall`/`stream_pc_state`/`stream_ice_state`/`stream_capture_error` telemetry.
>   Stale web Client Version: `employee_devices.client_version` only refreshed on login —
>   reinstall kept old version until re-login. Fix: DeviceAuth `X-Client-Version` /
>   `X-Client-Platform` → `TouchLastSeen` updates version; client `ClientIdentityHeaders` on
>   all DeviceAuth HTTP/WS; boot `app_status.client_version`. Verified: FA-27 + MU-115 live
>   preview; FA-27 `stream_ice_state=connected` / `stream_send_kbps` flowing; `dotnet build` 0/0;
>   installer `AlphaAITracker-Setup-1.2.34.exe`.
>
> - 2026-10-03: **Live stream Phase 3 — F14c socket hardening + presence Online contract.**
>   DeviceAuth push + presence WS: 4h max lifetime + every-5m `DeviceRepo.IsActive` re-check
>   (revoked/expired/missing device closes the socket; transient DB errors keep the socket).
>   Watch sockets keep the existing 4h lifetime (ticket-auth). `employeeLiveOnline` documents
>   presence-on → WS authoritative, presence-off → heartbeat window; unit-tested. Verified:
>   `go build`/`go vet`; `go test` handlers+stream+redis+scale ok.
>
> - 2026-10-03: **Live stream Phase 2 — Redis cluster state (single-server deploy).**
>   Shared Redis presence (`presence:emp:*` + `alpha:presence` pub/sub), watch tickets
>   (`stream:ticket:*`), publisher registry (`stream:pub:{emp}` → instanceId). Env
>   `INSTANCE_ID` / `INSTANCE_PUBLIC_URL` (optional; auto instance id). Watch-ticket may
>   return `watchBaseUrl` / Watch 307 if a second API appears later. Sticky LB example
>   removed — not needed for the one-server deploy shape. Keep Pion SFU. Verified:
>   `go build`/`go vet`/`go test` scale+stream ok; `npx tsc --noEmit` clean.
>
> - 2026-10-01: **Live stream V3.0 — safety + slow-network resilience (VPS/public internet).**
>   Implements [plan.md](./plan.md) Phase 0 + Phase 1 against the V2 WebRTC SFU. Deploy assumes
>   API on a public VPS domain (not LAN).
>   - **Phase 0:** `ScreenVp8Encoder` wrap-only (no `VpxImgFree` on managed ptr); ICE `iceReady`
>     always completed + session CT; web `track.stop()`/`video.pause()` + clear open timer;
>     `TouchLastSeen` uses Background+timeout; `CheckOrigin` fail-closed; Subscribe capacity
>     before Upgrade (HTTP 429); `sendPLI` done channel; idle capture poll skipped when stream off.
>   - **Phase 1:** `NetProbeService` + `POST /live-stream/uplink-probe`; `ALPHA_STREAM_MIN_UPLINK_KBPS`
>     / bitrate ladder / send-backpressure degrade (bitrate→resolution→fps); PLI→`force_keyframe`
>     ctrl; stream telemetry via `app_status`; token-bucket `WEBRTC_MAX_BITRATE_KBPS` on
>     `forwardRTP`; TURN empty startup WARNING; ticket cap + offer semaphore; 4h socket lifetime;
>     watch UserID audit log; employee poll `staleTime`/`refetchInterval` tuned. **Ops:** provision
>     TURN (`WEBRTC_TURN_*`) on the VPS; re-bake `config.enc` after `.env` (new stream keys).
>   - Phase 2 Redis presence/tickets/publisher registry landed; sticky LB example removed
>     (single-server deploy — no second API planned).
>   - Verified: `dotnet build` 0/0, `go build`/`go vet` clean, `go test ./internal/stream/...` ok,
>     `npx tsc --noEmit` clean. F1 heap fix needs installed-build start/stop cycle (not provable by build).
>
> - 2026-09-29: **Web live-stream — per-tile FPS + full-bleed theater popout.**
>   - Shared components under `web/src/components/live-stream/` (`LiveStreamWatchTile`,
>     `LiveStreamEmployeeSidebar`, `LiveStreamPreviewGrid`, selection helpers).
>   - Each live tile shows decoded **FPS** (`hooks/use-video-fps.ts` via `requestVideoFrameCallback`).
>   - **Open theater** opens `/live-stream/theater?ids=` in a new browser tab (route group
>     `(live-popout)` — ProtectedRoute + RouteGuard, **no** AppSidebar/TopBar). Same employee
>     sidebar + multi-tile preview (max 4). Note: browsers always show some address chrome on
>     new tabs/windows; a fully URL-less window is not allowed for web apps.
>
> - 2026-09-29: **Live stream V2 — JPEG relay → WebRTC SFU (VP8) + dedicated presence WS + encode quality fix.**
>   Branch `feature/live_streamV2.0` (client **1.2.2**). Preview remains **ephemeral** (never stored in DB/disk).
>   - **Server:** Pion **SFU** (`server/internal/stream/sfu.go`) RTP-forwards publisher → watchers; hub keeps signaling/consent/`select_monitor`/idle reap (JPEG frame mailbox removed). Env: `LIVE_STREAM_*`, `WEBRTC_MAX_BITRATE_KBPS` / `WEBRTC_STUN_URLS` / optional TURN, `PRESENCE_WS_*`. Routes unchanged shape: DeviceAuth `GET /live-stream/push` + **`GET /ws`** (presence); JWT `GET /live-stream/employees`, `watch-ticket`, `watch`.
>   - **Client:** `LiveStreamClient` publishes SIPSorcery WebRTC **VP8** after server `start`; capture still GDI/`ScreenCaptureService` (Windows). **`ScreenVp8Encoder`** replaces SIPSorcery `VpxVideoEncoder` — upstream `Vp8Codec.InitialiseEncoder` set `RcTargetBitrate` then called `VpxCodecEncConfigDefault` (wiping bitrate → ~256 kbps). Fixed order + VBR + ~1s keyframes (`ALPHA_STREAM_KEYFRAME_INTERVAL_SEC`). New **`WsClient`** → `GET /api/v1/ws` for Online/Offline (`ALPHA_WS_*`, independent of stream). Env: `ALPHA_STREAM_ENABLED/FPS/MAX_WIDTH/MAX_BITRATE_KBPS/KEYFRAME_INTERVAL_SEC`, `ALPHA_WS_*` — re-bake `config.enc` for installers.
>   - **Web:** `/live-stream` uses `<video>` + `useLiveStreamSocket` (RTCPeerConnection); multi-tile `?ids=` (max 4); monitor dropdown unchanged. **`useUrlQueryState`:** never call `router.replace` inside a `setState` updater (was updating `LinkComponent` while `LiveStreamInner` rendered).
>   - Linux capture still deferred. Verified: `dotnet build` 0/0; live encode logs show ~50–270 KB I-frames at 1680×1050 with `ScreenVp8Encoder`.
>
> - 2026-09-18: **Live stream Phase 1 (Windows) + Phase 2 multi-monitor + app-items index fixes.**
>   - **Server:** WS JPEG relay hub (`LIVE_STREAM_*`); online window 3 min; migrations **040/041** drop unsafe btree indexes on long `url`/`identifier`; Phase 2 `select_monitor` + monitors on hello/status.
>   - **Client:** DXGI/GDI capture + push socket; EnumDisplayMonitors switchable target; retention children-before-parents; Windows installer `AlphaAITracker-Setup-1.1.6.exe`.
>   - **Web:** `/live-stream` preview + monitor dropdown when the client reports 2+ displays. Linux capture still deferred.
>
> - 2026-09-11: **Core + Web: app-session accuracy, per-row stagnation sweep, Windows shutdown finalizer, migrations 034/035.**
>   - **Server:** `AggregateAppSessionsUsage` now projects `has_open_session = BOOL_OR(status='ACTIVE' AND ended_at IS NULL)` (I-01 — OFFLINE/STALE rows with `ended_at=NULL` no longer count as "Running") and `last_active_at = MAX(COALESCE(last_activity_at, last_sync_at, ended_at, started_at))`. A new **per-row stagnation sweep** (step 4 of `session_lifecycle_sweep.go`) advances any OFFLINE/STALE row whose OWN `last_sync_at` is ≥ CLOSE_AFTER old — even on a machine that is still alive — so a single abandoned session can never be stranded forever (I-02). **Migration 034** aligns historical rows that already carried `ended_at` to `CLOSED`; **migration 035** freezes stranded OFFLINE/STALE sessions (`status='CLOSED'`, `ended_at=COALESCE(last_activity_at,last_sync_at,started_at)`) older than 24h. 035 verified applied on the live DB (`OFFLINE & ended_at IS NULL = 0`).
>   - **Web `/employee-journey/apps`:** aggregate columns → **Application | Sessions | Duration | Last Active | Status** (Last Active wired to the new `lastActiveAt`); Status badge driven by `hasOpenSession` (green "Running" only when truly ACTIVE); expanded-row Closed cell shows `lastSyncAt` for STALE/OFFLINE; tiles → **Total session time / With open sessions**; Edge webviews folded into the browser group (`msedgewebview2` → `msedge`) so Windows Edge renders as one aggregate.
>   - **Web `/employee-journey/timeline`:** STALE/OFFLINE rows show `lastSyncAt` in the Closed column instead of an em-dash; the `py-px` typo is fixed.
>   - **Web `/employee-journey/web`:** an open tab's duration now uses `now − openedAt` instead of `0` (matches the 3-state duration end used elsewhere).
>   - **Client (Windows):** `SystemEventWatcher` → `SessionEnding` now calls `CloseAllOpenSessionsOnShutdownAsync()` (`CloseSessionsAndAppItemsAsync`) so every open session's `ended_at` is stamped (with `is_synced=0`) in SQLite before the process is killed — the next boot's sync drain pushes the closes to the server. Linux already had the equivalent `PrepareForShutdown` path.
>   - Verified: `go build`/`go vet` clean, `npx tsc --noEmit` clean, `next build` passes, `dotnet build` 0 warnings / 0 errors.
> - 2026-09-09: **Core: Bug #9 — orphan `app_items` no longer silently lost; server `synced_at` is never NULL.** Two root causes closed. (1) The 2026-09-04 orphan preflight returned only a survivor COUNT and the client's `SendAsync` never read the response body, so the client marked ALL sent ids `is_synced=1` — including rows the server had refused — permanently losing them. `SyncBatchResponse` now carries `rejectedIds []string` (omitempty: the other 11 sync endpoints' wire format is untouched; absent field = old mark-all behavior, so new-client↔old-server stays compatible), `BulkInsertAppItems` returns the refused item ids, `SyncAppItems` surfaces them, and `SyncService.DrainTableAsync` marks ONLY accepted rows (case-insensitive parse; absent/unparseable body ⇒ mark all, idempotent upserts make that safe). Poison rows (parent session never lands) are quarantined **in-memory** after 3 consecutive refusals — skipped by the drain until process restart, and the SQLite row stays `is_synced=0` (a refused row must never masquerade as synced; retention only purges synced rows, so quarantined rows persist as diagnostic evidence; restart resets the counter and gives self-heal a fresh chance). (2) `app_items` INSERT omitted `synced_at` (migration 008 gave it no DEFAULT) so every first insert of the highest-volume table stored NULL — only the ON-CONFLICT re-sync path stamped it. All five sync writers that leaned on DDL defaults or omitted the column (app_items, hardware_devices, permission_status, storage_devices, location_samples) now stamp `synced_at = NOW()` inline on INSERT. Migration **033** backfills historical NULLs from `created_at` (the honest server-side arrival time) using batched 10k-row `ctid` UPDATEs inside the runner's single transaction (pre-run the idempotent backfill via psql for very large DBs), then sets `DEFAULT now()` + `NOT NULL` on `app_items.synced_at`. Verified: `go build`/`go vet` clean, `dotnet build` 0 warnings/0 errors. Deploy order: server first (+ migration 033), client ships in the next installer build (no `config.enc` re-bake — no new env vars).
> - 2026-09-05: **Core: Windows `power_off` event now fires on shutdown/restart — `SystemEventWatcher` subscribes to `SystemEvents.SessionEnding`.**
>   The Windows half of `SystemEventWatcher` was missing `SystemEvents.SessionEnding`, so shutdown/restart never emitted `power_off` (sleep/resume via `PowerModeChanged` worked; lock/unlock via `SessionSwitch` worked; power_on worked via `LogCollectorService` on boot). `SessionEnding` with `SessionEndReasons.SystemShutdown` is now subscribed in `SubscribeWindows()` (fire-and-forget, mirrors Linux's synchronous `PrepareForShutdown` handler), with matching unsubscription in `UnsubscribeWindows()`. `Logoff` is intentionally skipped to avoid duplicating the existing `SessionSwitch` → `os_logout` path. `ShutdownSentinel` remains as fallback. This makes Windows match Linux's two-layer power-off detection pattern. Verified: `dotnet build` 0/0, 0 warnings. Real-world test requires a Windows shutdown/restart cycle against an installed build.
> - 2026-09-05: **Web: `/employee-journey/web` groups search-engine queries and shows expandable dropdown arrows.**
>   Searches on Google, Bing, Yahoo, and DuckDuckGo are now parsed from `url` query params (`q`/`p`) and grouped by query text instead of being flattened under the domain. In the default "Visited Sites" view, search rows show a `Search` icon + engine badge (`Google`/`Bing`/`Yahoo`/`DuckDuckGo`) and are expandable with a chevron arrow, just like domain groups. In the active-search "Matching Pages" view, results are grouped by search query with the same expand/collapse behavior instead of a flat table. Non-search URLs continue to group by domain as before. The grouping is client-side only and reuses the existing infinite-scroll data; no server change. Verified: `npx tsc --noEmit` clean, `next build` passes (`/employee-journey/web` 4.8 kB).
>
> - 2026-09-04: **Core: chrome "App Usage" duration is wrong on the web (3 layers).** A chrome
>   window with 3 tabs × 10 min used to render as "30 min" because (1) the a11y reader returns a
>   fresh `WindowKey` per tab, the old `ResolveWindowKey` only collapsed tabs when their titles
>   matched (transient "Loading…" states slipped through and opened a session per tab), and
>   (2) the web page summed per-row `endedAt - startedAt` over every row with the same
>   `appDisplayName`. **Layer 1 — client (`AccessibilityBrowserTracker.ResolveWindowKey`):**
>   two new collapse rules — (a) exact URL match (stronger than title, kills the "3 tabs all
>   titled 'Loading…'" transient case), and (b) fresh-key recency window of `poll×2` seconds
>   that attaches a new same-PID key to the FRESHEST tracked window (the recency guard is what
>   prevents two real long-lived windows from collapsing — when both are old, the guard fails
>   on every candidate). Existing title-match and single-window-count rules stay as fallbacks.
>   `IsBrowser` detection is already metadata-driven (`.desktop Categories=WebBrowser`, Windows
>   `URLAssociations`, macOS `CFBundleURLSchemes`), so chrome variants are caught without code
>   changes. **Layer 2 — web (`/employee-journey/apps`):** the `useMemo` now recomputes
>   duration as `lastClosedAt - firstOpenedAt` regardless of what the server returns, so a
>   future regression on the server side can't reintroduce the sum. **Layer 3 — server (the
>   user-visible win regardless of layer 1):** new `GET /api/v1/app-sessions/usage` returns
>   one row per `(appDisplayName, processName)` with `MIN(started_at)`, `MAX(COALESCE(ended_at,
>   last_sync_at, started_at))`, `SUM(...)` for `totalDurationSeconds`. Composite index
>   `idx_app_sessions_employee_started_name` (migration 032) covers the WHERE + GROUP BY.
>   Route is registered BEFORE the existing `GET /app-sessions` (safe ordering even if a
>   `:id` route is added later). Web switched from a 5×100 raw-row fan-out (silent 500-row
>   truncation bug) to a single `appSessionsApi.usage` call. The page now shows "First Opened"
>   and "Last Closed" columns so the open range is visible directly. Result: chrome 3 tabs
>   × 10 min renders as **Duration: 10m** (lastClosed - firstOpened), Sessions: 3; chrome
>   where tab1 stays open 9:00→9:12 with the others at 9:00→9:10 renders as **Duration:
>   12m**, Sessions: 3. Verified: `go build`/`go vet` clean, `dotnet build` 0/0, `npx tsc
>   --noEmit` clean, `next build` passes (`/employee-journey/apps` 2.89 kB). ⚠️ Pre-existing
>   split chrome sessions in deployed DBs are NOT deduped (destructive to rewrite); new
>   sessions after deploy will be 1-row-per-window. Layer 1 ships only in a new installer
>   build (Installer-Parity Rule); layers 2 + 3 deploy with just the server restart.
>
> - 2026-09-02: **Core: 3-state app_sessions lifecycle (ACTIVE / STALE / CLOSED) — orphan "Running forever" bug fixed at the schema level.**
>   `app_sessions` rows with `ended_at=NULL` used to stay "Running" on the web dashboard forever once a PC went offline, was uninstalled, or never rebooted (see `project-orphan-sessions.md` for the live-DB evidence). The new model lets the SERVER own the session state, while the CLIENT remains the source of truth for activity history. **Migration 031** (`server/migrations/031_app_sessions_status.sql`) adds 3 columns to `app_sessions` — `status TEXT NOT NULL DEFAULT 'ACTIVE'`, `last_activity_at TIMESTAMPTZ`, `last_sync_at TIMESTAMPTZ` — plus 2 indexes (`status, last_sync_at DESC` and a partial `WHERE status='ACTIVE'`). 1131 pre-existing rows were backfilled in place. **State machine** (`server/internal/jobs/session_lifecycle_sweep.go`, every 1 min): `ACTIVE → STALE` when `last_sync_at < NOW() - SESSION_STALE_AFTER_MINUTES` (default 10), `STALE → CLOSED` when `last_sync_at < NOW() - SESSION_CLOSE_AFTER_HOURS` (default 24). Only CLOSED is terminal; the sweeper freezes `ended_at = COALESCE(last_activity_at, last_sync_at, started_at)` at the moment of CLOSE so duration reflects real usage, not the sweep moment. **Upsert recovery** (server `new_schema_repo.BulkInsertAppSessions`): when a live client re-uploads any row with `ended_at=NULL`, the `ON CONFLICT` CASE flips STALE/CLOSED back to ACTIVE and clears the premature server-side `ended_at` — so a 2-hour network outage never destroys information that may still exist on the client. When the client supplies a non-NULL `ended_at`, the row stays CLOSED with the new value (client's decision wins). **Cross-service contract kept in sync** — server `AppSession` model + DTO + service + repo + JSON; client `Core/Models/AppSession.cs` (`LastActivityAt`) + `Storage/DatabaseSchema.cs` (idempotent ALTER, insert + focus + ended SQL) + `SqliteLogStore.cs` (mapper) + `Services/LogCollectorService.cs` (sets `LastActivityAt = log.Timestamp` on new sessions) + `Services/SyncService.cs` (payload includes `lastActivityAt`); web `lib/api.ts` (3 new fields) + `lib/format.ts` (`formatRelative`) + new `components/sessions/SessionStatusBadge.tsx` (reusable `sessionStatus()` + `isSessionLive()` helpers — pre-031 rows fall back to the legacy `endedAt ? CLOSED : ACTIVE` rule) + all three consuming pages (`employee-journey/timeline`, `employee-journey/apps`, `logs/comprehensive`) now use a 3-state-aware duration end: CLOSED → `endedAt`, STALE → `lastSyncAt`, ACTIVE → `now` (eliminates the "Running for 2h 17m" lie for a STALE row). Live-tested end-to-end against the dev PostgreSQL: with `SESSION_STALE_AFTER_MINUTES=1` the sweep promoted 1131 ACTIVE→STALE then 1131 STALE→CLOSED in 62 ms; a fresh `POST /api/v1/app-sessions/sync` re-uploading a CLOSED row with `ended_at=NULL` flipped it back to ACTIVE in one round-trip; supplying a non-NULL `ended_at` left it CLOSED with the new value. **Env knobs** added to `server/.env` and `server/.env.example`: `SESSION_STALE_AFTER_MINUTES=10`, `SESSION_CLOSE_AFTER_HOURS=24`. Verified: `go build`/`go vet` clean, `dotnet build` 0/0, `npx tsc --noEmit` clean, `next build` passes (3 pages using the new badge).
>
> - 2026-09-01: **Web: URL-synced filters + no-Clear date modal — *URL-Synced Filters Rule* codified.**
>   New generic `useUrlQueryState<T>` hook (`web/src/hooks/use-url-query-state.ts`) syncs any typed filter record to the browser URL (debounced, preserves unrelated keys, strips empty values). New `useUrlActivityFilter` (`web/src/hooks/use-url-activity-filter.ts`) bridges `ActivityFilter` to compact URL keys (`?q=&preset=today|7d|all|custom&from=YYYY-MM-DD&to=YYYY-MM-DD`). Wired into 13 pages: employees (search + department), shifts (search), timesheets (employee + from + to), attendance (date + status), employee-journey/timeline (employeeId via shell), employee-journey/apps + web (search + date preset), device-specs/software (tab + per-tab search), configuration/apps + websites (search + type + category + status via `ClassifiedItemsTable`), logs/comprehensive (employee + search + page), settings/user-management (search). `ActivityFilters` lost its search Clear (X) button — clearing now means select "All time" or empty-the-input. Rule added to §6 (conventions table + new rule subsection) and `prompt.md`. Every page using `useSearchParams` is now wrapped in `<Suspense>`. Verified: `npx tsc --noEmit` clean, `next build` clean.
>
> - 2026-09-01: **Server + Web: attendance late/present timezone fix — shift IANA zone must match wall-clock, not browser display.**
>   Root cause: migration 028 defaulted `shifts.timezone` to `UTC` while session events and admins sit in local time (e.g. PKT +05) — a 9:08 arrival looked **Present** in the UI (browser-local `toLocaleString`) but was compared against 09:00 **UTC** on the server. Fix: new `DEFAULT_SHIFT_TIMEZONE` env (`server/.env.example`) — on boot, `ShiftService.ApplyDefaultTimezone` rewrites every active shift still on `UTC`; create-shift fallback when the client omits timezone; `AttendanceResponse` adds `timezone`; late rule unchanged (`firstLocal.After(shiftStart + grace)` in `time_attendance_service.go`). Web: `/shifts` new-shift form defaults to `Intl.DateTimeFormat().resolvedOptions().timeZone`; `/attendance` + `/timesheets` format first/last active via `formatDateTimeInZone(iso, record.timezone)`. Verified: `go build`/`go vet` clean, `npx tsc --noEmit` clean; live IS-131 09:08 + 5 min grace → `late` / 3 min with `Asia/Karachi`.
> - 2026-09-01: **Web: GPS / Location admin UI gated behind Coming Soon (client + server unchanged).** `/gps-location` and `/employee-journey/location` render `LocationComingSoon` while `web/src/lib/locationUi.ts` sets `LOCATION_UI_ENABLED=false`. Full live implementations preserved in `gps-location/GpsLocationLive.tsx` and `employee-journey/location/LocationTrailLive.tsx` — flip the flag to re-enable. Client `ALPHA_LOCATION_ENABLED` default stays `false`; sync APIs and geofence backend are untouched. Verified: `npx tsc --noEmit`.
> - 2026-09-01: **Client: A.9/A.10 session-event sync aggregation + S6 row ceiling.** `SessionEventSyncAggregator` groups unsynced `session_events` into closed time buckets (`ALPHA_EVENT_AGGREGATION_WINDOW_SEC`, default 300 s) at sync drain; `DrainSessionEventsAsync` POSTs `{count, firstAt, lastAt}` to `/api/v1/session-events/sync` and marks all source rows sent. `ALPHA_TA_MAX_LOCAL_ROWS` (default 50k) rolls oldest unsynced rows into `old_data_dropped` sentinel. Cross-service vocabulary mirrors: `server/internal/models/session_event_types.go`, `web/src/lib/eventTypes.ts`; `test/contract-event-types.sh` (T5). Verified: `dotnet build` 0/0, `bash test/contract-event-types.sh`, `npx tsc --noEmit`.
> - 2026-08-31: **Client: real reboot lifecycle now persists both `power_off` and `power_on`.**
>   Live reboot proof exposed two independent gaps. Startup: `SystemEventWatcher` (first hosted service) attempted `power_on` before SQLite existed; the store silently returned, then the recorder's dedup state suppressed `LogCollectorService`'s valid retry, leaving only `tracker_login`. `Program.cs` now initializes `ILogStore` before `host.StartAsync`, and `SqliteLogStore.InitializeAsync` is serialized/idempotent so the collector's existing initialization call is harmless. Shutdown: because the headless service had lazily opened Avalonia, GNOME reboot destroyed Xwayland first (`X connection to :0 broken`); the process exited status 1 before systemd's stop job and `ApplicationStopping` never fired. Linux `SystemEventWatcher` now synchronously persists `power_off` from login1 Manager `PrepareForShutdown(true)`, which is emitted before display teardown; the existing sentinel remains the normal SIGTERM/Ctrl+C fallback. The recorder's fixed 5-second buckets were also replaced with a true sliding interval after the same DB showed two `screen_unlock` sources 342 ms apart straddling a bucket boundary. Verified installed: every service start now writes `power_on` followed by `tracker_login`, normal stop writes `power_off`, service healthy with zero restarts, and `dotnet build` is 0 warnings / 0 errors. Final reboot-path confirmation requires the next real reboot (not triggered automatically during development).
> - 2026-08-31: **Client: installed Linux GUI opens from the headless service on Wayland again.**
>   Root cause was the generated user systemd unit forcing `XAUTHORITY=~/.Xauthority`; GNOME Wayland uses a rotating `/run/user/<uid>/.mutter-Xwaylandauth.*` cookie. Tracking worked headlessly, but a manual second launch's SHOW signal made the service process call Avalonia with the stale cookie, producing `Authorization required` / `XOpenDisplay failed`. New units no longer hardcode `DISPLAY` or `XAUTHORITY`; `BackgroundGuardService` migrates existing units by removing only those overrides, reloads systemd, and performs one non-blocking self-restart only when actually running under systemd (`INVOCATION_ID`, so `dotnet run` never starts a competing service). Lazy GUI startup also refreshes the graphical environment from the user systemd manager as defense in depth. Installed-build proof included deliberately restoring the broken legacy unit, reinstalling, observing automatic migration + restart to the live Mutter auth path, then launching a second instance and confirming the real `Alpha AI Tracker 1.1.0` X11 window with no GUI startup error. `dotnet build`: 0 warnings / 0 errors.
> - 2026-08-31: **Client: Time & Attendance correctness pass — clean SIGTERM, live config gates, login/resume wake-ups, and schedule-aware attendance math.**
>   Background mode now waits with `host.WaitForShutdownAsync()` instead of an uncancellable infinite delay, so systemd SIGTERM reaches hosted-service shutdown and `ShutdownSentinel` persists `power_off`; installed-build proof: `systemctl --user stop` returned in 812 ms, service became inactive, and SQLite contained the matching `power_off` row. `ALPHA_TA_ENABLED=false` now parks `SystemEventWatcher` and `IdleDetector`, `ALPHA_TA_LOCK_HYSTERESIS_SEC` drives the actual lock suppression window, and `--print-config` exposes all T&A/idle values. `ScheduleCacheService` has a coalesced wake signal: login/session restore pulls immediately, resume waits 10 s for network stabilization, then pulls; device auth is now one valid `Authorization: Device` header (Bearer fallback only for legacy rows). `LocalTimeSkewService` likewise wakes 10 s after resume instead of carrying an unused stabilization constant. `AttendanceAggregator` wakes on login and now understands the contract's `mon`…`sun` keys, company holidays, nullable no-event arrival, `absent`/`half_day`/`off_shift`, schedule overlap and off-shift seconds, and the union of idle + screen-lock intervals without double-counting; while the tracker is alive, last-seen advances to now instead of freezing at the last sparse event. Installed testing exposed one more bug: legacy events with `+05:00` offsets and new `Z` events were parsed into incompatible `DateTime` wall-clock ticks, undercounting today by five hours; the store mapper now normalizes every event through `DateTimeOffset.UtcDateTime`. Removed unused recorder dependencies and corrected §19 documentation that previously claimed missing server/web event mirrors and GPS hiding already existed. Verified: `dotnet build` 0 warnings / 0 errors; Linux `.deb` rebuilt and installed, config printed from the installed binary, WAL confirmed, service healthy with zero restarts, and the immediate real-DB rollup corrected active time from 3,994 s to 21,976 s (158 s inactive).
> - 2026-08-31: **Client: duplicate Linux lock/unlock attendance events fixed by enforcing one tracker across login sessions.**
>   Root cause confirmed from the installed build: a manual desktop launch and the systemd user service ran concurrently against the same SQLite database. The old unqualified .NET mutex was session-scoped on Unix (`/tmp/.dotnet/shm/session<SID>/AlphaAITracker`), so each process acquired a different lock; every D-Bus lock/unlock signal was consequently written twice, 0–3 ms apart, and the recorder's in-memory dedup could not cross process boundaries. `SingleInstanceService.MutexName` now uses .NET's machine-wide `Global\` namespace with `AppInfo.AppMutex` from `APP_IDENTIFIERS`; `Program.cs` consumes it for all launch modes. Verified: `dotnet build` clean (0 warnings / 0 errors), Linux `.deb` built and installed, the installed systemd process owns `/tmp/.dotnet/shm/global/AlphaAITracker`, and a second process in a different SID exits with “Another instance is already running” while the process count remains one.
> - 2026-08-28: **Client: Time & Attendance Phase 1 foundation (A.0–A.8) — OS power/lock/login telemetry, idle detection, shift mirror, local daily-attendance rollup.**
>   New `IEventRecorder` abstraction (`Core/Abstractions/IEventRecorder.cs` + `Services/SessionEventRecorder.cs`) is the single funnel for ALL session_events writes — 5 s dedup window collapses bursts (UPower + ScreenSaver firing within 1 s → one row, not two) and a hard 2 s write timeout guarantees telemetry never blocks shutdown (R7). `SessionEvent` extracted to `Core/Models/SessionEvent.cs` with the authoritative `SessionEventTypes` vocabulary (power_on/off, resume, os_login/logout, screen_lock/unlock, tracker_login, ui_hidden, idle_start/end; `login` kept as an `[Obsolete]` back-compat alias) — single source of truth per R5 (R2/R5 compliance). `LogCollectorService` now writes `tracker_login` + explicit `power_on` through the recorder instead of a hard-coded `"login"` literal. New `ShutdownSentinel` (hosted FIRST in DI) writes `power_off` on SIGTERM / Ctrl+C / Avalonia Exit / dispose via a `ManualResetEventSlim` that `Program.cs` waits on (≤3 s) before disposing the store — BUG-1/4 fixed; `App.axaml.cs` window.Closing now emits `ui_hidden` for the tray state — BUG-3 fixed. New `SystemEventWatcher` (partials per OS): Linux D-Bus `org.freedesktop.UPower` PrepareForSleep + `org.freedesktop.login1` Lock/Unlock + `org.gnome.ScreenSaver` ActiveChanged (Tmds.DBus.Protocol `WatchSignalAsync`, no /dev/input), Windows `Microsoft.Win32.SystemEvents` PowerModeChanged + SessionSwitch (new `Microsoft.Win32.SystemEvents` package; Hardware HWND_MESSAGE WTS pump is a documented TODO A.3.2), macOS NSWorkspace stub pending the maccatalyst TFM split; 30 s lock-hysteresis + `ta_last_known_os_event_at` health watermark — BUG-8 fixed. New `IdleDetector`: OS idle source (Mutter.IdleMonitor `GetIdletime` D-Bus first, X11 XScreenSaver fallback, Windows GetLastInputInfo) polls every 30 s and emits threshold-crossing `idle_start`/`idle_end` markers — BUG-10 (Wayland /dev/input permission) fixed. New `ScheduleCacheService` (pull `GET /api/v1/schedules/me` on login + every 6 h; graceful 404 no-op until the Phase 2 server ships) — BUG-6 fixed; new `LocalTimeSkewService` (client↔server clock skew from HTTP Date header every 15 min, stored per server URL, post-resume stabilization skip) — BUG-7/12 fixed; new `AttendanceAggregator` (5-min daily rollup into `daily_attendance_cache`: arrival/departure/active/idle + present|late|off_shift|unknown status). SQLite: WAL mode + `synchronous=NORMAL` enabled FIRST then a second **ReadOnly** connection (`WithReadConnectionAsync`) for pure readers — BUG-9 fixed (6 hosted services on SQLite would deadlock the old single-connection `SemaphoreSlim(1,1)`); 4 tables (`employee_schedule`, `company_holidays`, `daily_attendance_cache`, `local_time_skew`) + `(event_type, event_at)` / `(employee_id, work_date)` indexes. `ALPHA_TA_ENABLED` / `ALPHA_IDLE_*` / `ALPHA_TA_LOCK_HYSTERESIS_SEC` env knobs in `.env` + `.env.example` + `AppConfig`. Verified: `dotnet build` clean (0 warnings / 0 errors). Client-only Phase 1 — no server/web change; A.9/A.10 session-event sync aggregation (S1–S6) is gated on the Phase 2 `session-events/sync` aggregate payload (SVR-2, documented in `client/ARCHITECTURE.md` §19). ⚠️ Installer-Parity: the new services are compiled into `client.dll` (no new binary asset), but the `config.enc` re-bake is MANDATORY before shipping so the `ALPHA_TA_*` knobs reach installed builds.
> - 2026-08-28: **Web + Server: Dedicated `GET /api/v1/auth/profile` endpoint for the self-service Profile page — single-shot payload (user + role + RBAC view + linked employee).**
>   Server: new `AuthHandler.GetProfile` calls `UserService.GetProfile(ctx, userID)`, which composes the user record, the role (via `rbacRepo.GetRoleByIDWithPerms`), the granted submodule keys (via `rbacRepo.PermissionKeysForUser`), the navigation-module breakdown (each module's `grantedCount`/`submoduleCount`, derived by joining the granted keys against `rbacRepo.ListModules` — no hardcoded module names), an `isSystemAdmin` convenience flag (true for the `company_admin` role), and the linked employee (via `employeeRepo.GetByEmployeeID` when the user has a non-empty `employeeId`). New DTO `ProfileResponse` carries `UserResponse`, `RoleResponse?`, `ProfilePermissions`, and `EmployeeResponse?`. `UserService` and `AuthHandler` constructors were widened to receive the RBAC + employee repos (call sites in `cmd/server/main.go` updated). Route: `GET /api/v1/auth/profile` under the existing `JWTAuth` middleware group. Web: `authApi.profile()` exposed; `/settings/profile` switched from `authApi.me()` to `authApi.profile()` and now renders three blocks — (1) **Account** (user id, employee link, role + system badge, department, shift) as read-only tiles; (2) **Modules you can access** (per-module `grantedCount/submoduleCount` cards with system-admin "all" override) — derived purely from the server's RBAC catalog joined with granted keys; (3) **Update details** (name/email/password with confirm-password + eye toggles, reverse-sync to the linked employee on save, same pattern as the admin user-management edit flow). Verified: `go build`/`go vet` clean, `npx tsc --noEmit` clean, `next build` passes (`/settings/profile` 7.56 kB).
> - 2026-08-28: **Web + Server: User-Management edit flow + server-projected `hasUserLogin` flag on every employee row.** (see below)
>   Plus: **`/settings/profile` self-service page** — visible to every authenticated user (no permission gate), lets the logged-in user update their own `name`/`email`/`password` (with confirm-password + eye toggles, ≥6-char validation). If the user has a non-empty `employeeId`, name/email changes propagate to the linked employee record via the same `usersApi.update` + `employeesApi.update` pattern used in the admin edit flow. The Profile sidebar entry deliberately omits the `module` key — the sidebar's `isItemVisible` short-circuits the `canAccess` check and `RouteGuard.findModuleForPath` returns undefined, so the page is reachable for every role (including pre-RBAC and zero-permission users). On user-management: the **Edit** button is hidden for the system `company_admin` user and a defense-in-depth guard refuses the dialog open via a crafted `?edit=1&userId=…` URL — the bootstrap admin is locked from this surface end-to-end.
> - 2026-08-28: **Web + Server: User-Management edit flow + server-projected `hasUserLogin` flag on every employee row.**
>   The `/settings/user-management` page is now a full CRUD surface — not just a create-from-employee form. A per-row **Edit** button (new "Action" column in the users table) opens the same dialog in edit mode, driven by `?edit=1&userId=…` deep-links. Identity fields (`name`, `email`, `employeeId`) are **locked** in both create-from-employee and edit modes (they mirror the employee record and are immutable from this surface); the **role dropdown excludes the system `company_admin` role**; the password field gains an **eye toggle** (lucide `Eye`/`EyeOff`); a new **Confirm password** field sits beneath Password and the two must match (≥6 chars). On a successful user edit, if the user has a non-empty `employeeId`, the web client also calls `employeesApi.update` to propagate any name/email change to the attached employee record (employeeId is never sent — it is immutable). The reverse sync fires in the employees page `updateMutation` — editing an employee's name/email propagates to the linked user.
>   The scalability fix that made the **"Login Credential" dropdown item** (on the employees page) hide for employees who already have a login account is **server-projected, not client-mapped**: every employee SELECT path (`List`, `GetByID`, `GetByEmployeeID`, `GetByEmail`, `ListAll`, `Create` RETURNING, `Update` RETURNING, `scanEmployeeRow`) now projects `EXISTS(SELECT 1 FROM users u WHERE u.employee_id = e.employee_id AND u.deleted_at IS NULL) AS has_user_login`. The expression is a **single indexed probe per row** against the UNIQUE `users.employee_id` index — it does **not** scan the users table, and the cost is O(1) per page row regardless of total employee or user count. The previous draft of this change (a web-side `useQuery` that pulled up to 200 users and built a `Set<string>` of employeeIds) was rejected as a non-scalable workaround and is **fully removed** — no `usersCheckData`, no `employeesWithUsers` Set, no `['employee-users-map']` query key anywhere in `web/src`. The web dropdown now uses the server-projected `emp.hasUserLogin` directly, and the `updateMutation`'s "is this employee linked to a user?" guard reads `updated.hasUserLogin` from the `UPDATE…RETURNING` response — no extra round-trip. Web API type (`Employee.hasUserLogin`) and the `EmployeeResponse.hasUserLogin` JSON field (camelCase) are added in lockstep. Verified: `go build`/`go vet` clean, `npx tsc --noEmit` clean, `next build` passes. The `/users` list is still queried once per save on the user-management page to look up the linked user by employeeId (necessary because we need the user's UUID to PUT) — that path is unchanged and the lookup uses the existing `search` parameter (already indexed on `users.employee_id`).
> - 2026-08-25: **Core: Rotating refresh tokens for the web-admin session — 15-min access JWT, 30-day refresh cookie, transparent regeneration, forced logout to /login.**
>   Migration **026_refresh_tokens.sql** adds `refresh_tokens` (user_id FK CASCADE, UNIQUE `token_hash`
>   = hex(sha256(raw)), expires_at, revoked_at, deleted_at + user/expires indexes). The raw token is a
>   32-byte random base64url string that NEVER touches the DB or JSON bodies — only its SHA-256 hash is
>   stored, and it travels ONLY in the httpOnly `refresh_token` cookie (DTO fields are `json:"-"`).
>   `JWTConfig` split into three TTLs: `JWT_ACCESS_EXPIRY=15m` (web admin), `JWT_REFRESH_EXPIRY=720h`
>   (30 days), `JWT_EMPLOYEE_ACCESS_EXPIRY=24h` — the desktop client has NO refresh mechanism, so its
>   TTL deliberately stays 24h (never lower it; documented in server/ARCHITECTURE.md §6). New public
>   endpoint **POST /auth/refresh**: validates the cookie's hash → mints the replacement access+refresh
>   pair FIRST → revokes the old row AFTER (crash-safe rotation — a mid-refresh failure leaves the old
>   row valid); replaying a rotated cookie returns 401. Login now sets BOTH cookies; logout REVOKES the
>   refresh row server-side (real session invalidation) before clearing both cookies. Web: `api.ts`
>   request wrapper catches 401s (except login/refresh) → single-flight POST /auth/refresh → replays
>   the original request once (`_retried` cap, no loops) → on failure `window.location.replace('/login')`;
>   the `checkAuth` thunk also tries refresh + re-check once when `/auth/check` answers
>   `{authenticated:false}`. Verified: `go build`/`go vet` clean, `tsc --noEmit` clean, `next build`
>   passes; live curl lifecycle against the dev server proved login sets BOTH cookies (+15m/+30d,
>   HttpOnly), `/auth/refresh` rotates (old cookie replay → 401), and logout revokes (post-logout
>   refresh → 401).
> - 2026-08-25: **Web: ALL localStorage mock data removed — zero localStorage references remain in web/src.**
>   `web/src/lib/store.ts` (the 328-line mock factory: 12 fake employees, activity/system logs,
>   productivity rows, screenshots, settings CRUD, `initializeData` seeder) is deleted, as is the
>   legacy localStorage permission-matrix editor `settings/permissions/page.tsx` (+ its sidebar child
>   entry and breadcrumb key). Seven no-backend pages (screenshots, live-stream, hours-insights,
>   employees/activity, logs/graphical, logs/insights, ai-summary) became honest empty states — header
>   + `EmptyState` explaining there is no endpoint yet. `/dashboard` is fully live-API: Total Employees
>   (+ tracked/untracked split), Departments count, App Sessions ·24h (`GET /app-sessions?dateFrom=`),
>   Web Pages ·24h (`GET /app-items?itemType=browser_tab&dateFrom=`); the mock chart/Best-Performer
>   cards are replaced by an empty-state analytics card linking the real journey routes.
>   `/settings/tracking` keeps in-memory state only with an explicit non-persistence notice.
>   `app/page.tsx` runs a one-time sweep that purges every orphaned `alpha_ai_tracker_*`
>   (`STORAGE_PREFIX`) key from localStorage. Auth persistence was already cookie-based
>   (httpOnly cookie → `/auth/check` → Redux), so nothing else read localStorage. Verified:
>   `tsc --noEmit` clean, `next build` passes, grep confirms zero `localStorage` usage in src.
> - 2026-08-25: **Server: GET /api/v1/users 500 root-caused via live testing — ambiguous `deleted_at` under the roles JOIN.**
>   After two wrong static hypotheses, a live loop (build → run → login → reproduce → read the raw
>   PostgreSQL error from the APIError `detail`) exposed SQLSTATE 42702: `user_repo.List` filtered on a
>   bare `deleted_at IS NULL` while its `LEFT JOIN roles r` introduced `roles.deleted_at`, making the
>   reference ambiguous. All List conditions are now qualified (`u.deleted_at`, `LOWER(u.name)`, …).
>   Hardening kept alongside the true fix: every projection COALESCEs nullable `avatar`/`avatar_color`,
>   and Update's RETURNING replaced an invalid `COALESCE(r.name,'')` (no alias `r` exists in
>   UPDATE…RETURNING — would have 500'd every user edit) with a scalar subquery
>   `(SELECT name FROM roles WHERE id = u.role_id)`. Known follow-up: creating a user with a
>   nonexistent `roleId` surfaces as FK 23503 → 500 instead of a friendly 400.
> - 2026-08-25: **Core: Dynamic RBAC end-to-end — server-driven roles/modules/submodules replace the client-side permission matrix.**
>   Migration **025_rbac_roles_modules.sql** adds `roles` (seeded SYSTEM `company_admin`),
>   `modules`, `submodules` (concrete permission keys carrying `route_path`), and the
>   `role_submodule_permissions` junction; `users.role_id` becomes the sole role source of truth
>   (legacy `users.role` / `is_company_admin` dropped). A Go seeder
>   (`RBACService.SeedCatalog`, idempotent, runs at boot) re-ensures the module/submodule catalog +
>   grants. New protected endpoints: `GET /modules` (full module→submodule tree) and `/roles` CRUD —
>   role payloads carry both `submoduleIds` (grants) and derived `permissions` keys; user
>   create/update accept `roleId`. Web: `/roles` rebuilt as a real-API CRUD page with per-submodule
>   permission toggles; `/settings/user-management` rebuilt on the live `/users` API (infinite scroll
>   per the §6 rule, create/edit/delete dialogs, role assignment). `permissions.tsx` rewritten to be
>   SERVER-driven only: the granted submodule keys ride every auth response (`user.permissions`) into
>   `PermissionsProvider`; `RouteGuard` resolves the current pathname to a module and redirects
>   unauthorized access to a new `/unauthorized` page. Semantics: absent/null permissions array =
>   fail-open (pre-RBAC sessions keep working), present array = strict allow-list,
>   `company_admin` always full. Server-side enforcement of these permissions is still NOT
>   implemented (middleware has no role checks yet). Verified: `go build`/`go vet` clean,
>   `tsc --noEmit` clean, `next build` passes.
> - 2026-08-22: **Web: Sidebar parent menus stay open when navigating to child pages.**
>   `AppSidebar` now auto-expands any parent section whose `children` array contains the current `pathname`, so the menu stays open when the user opens a child page. Verified: `tsc --noEmit` clean, `next build` passes.
> - 2026-08-22: **Web: Configuration pages UI/UX redesign + manual website creation.**
>   `ClassifiedItemsTable` component redesigned with improved filter bar (status toggle chips, clearable filters, active count badge), better table spacing/hover states, per-row classification status badges (Classified/Unclassified/Partial), and richer empty/loading states. `websites/page.tsx` gains an **Add Website** button + modal dialog that posts to new `POST /api/v1/monitoring/websites` (server creates the row with optional type/category and normalizes the domain). Server: new `CreateWebsite` endpoint in `monitoring_handler.go` + `MonitoringService` + `MonitoringRepo`. Fixed 400 error on manual website creation: server now normalizes domain (strips protocol/path/lowercases) and the dialog fetches types/categories dynamically instead of hardcoding IDs. Added live duplicate detection. Verified: `go build`/`go vet` clean, `tsc --noEmit` clean, `next build` passes.
> - 2026-08-22: **Web: Dynamic browser badge names on Web Activity — removed hardcoded `BROWSER_NAMES` map.**
>   `employee-journey/web/page.tsx` no longer translates `metadataJson.processName` through a hardcoded 10-entry map (`chrome`→`Chrome`, `msedge`→`Edge`, …). The server now resolves the friendly browser name dynamically: `ListAppItems` extracts `processName` from each row's `metadata_json` and looks it up in `installed_applications` (`binary_name` or `app_name` match, `is_browser = true`), returning `browserName` in the API response. The web page uses `item.browserName` directly; for embedded webviews (`source: "webview"`) the host app's process name is preserved as before. Verified: `go build`/`go vet` clean, `tsc --noEmit` clean.
> - 2026-08-22: **Server: Catalog merge migration 024 + UpsertApplicationCatalog hardening (cross-OS dedup).**
>   New migration **024_catalog_merge.sql** collapses duplicate `installed_applications` catalog rows by normalized display name (`regexp_replace(lower(app_name), '[^a-z0-9]', '', 'g')`). For each merge group it picks the winner by earliest `created_at`, re-points `employee_installed_applications` links and `app_sessions.installed_app_id` to the winner, carries `type_id`/`category_id` from losers when the winner lacks them, and soft-deletes losers so the UNIQUE `app_fingerprint` constraint stays intact. `UpsertApplicationCatalog` in `new_schema_repo.go` now performs a two-phase upsert: first it checks for an existing non-deleted row by normalized name and updates its metadata in place, then it falls back to the `app_fingerprint` INSERT/ON CONFLICT. Verified: `go build`/`go vet` clean, migration applied + smoke-tested on the dev DB (3 merge groups collapsed).
> - 2026-08-22: **Client: Flatpak `bwrap` PPID chain walk in embedded Python probe.**
>   `resolve_app_name(pid)` in `LinuxAtSpiBrowserReader.cs` now detects when the AT-SPI PID belongs to Flatpak's `bwrap` or `xdg-dbus-proxy` and walks up the PPID chain (via `/proc/<pid>/stat`) until it reaches the real app process. The FLATPAK_ID / snap / comm resolution then runs against the real PID, so Floorp/LibreWolf/Waterfox Flatpak installs resolve to their short app ID (`floorp`, `librewolf`, `waterfox`) instead of the proxy name. Verified: `dotnet build` 0/0, 0 warnings.
> - 2026-08-21: **Client: All hardcoded browser names removed — dynamic `IBrowserRegistry` replaces `BrowserProcessHints` / `IsBrowserProcess` / `BROWSER_HINTS` / `ResolveFamily`.**
>   New `IBrowserRegistry` interface + `BrowserRegistry` implementation source browser classification from `installed_applications.is_browser` (set via `.desktop` `Categories=WebBrowser`, Windows registry `URLAssociations`, macOS `CFBundleURLSchemes`) and cache it for 5 minutes. Every reader, tracker, history fallback, session label resolver, and the main collector loop now resolves browsers through the registry instead of hardcoded name lists. The Linux embedded Python probe's `BROWSER_HINTS` tuple is deleted; the probe uses `_browser_exes` (already built from `.desktop` files) for both AT-SPI browser detection and Firefox sessionstore discovery. `StripBrowserSuffix` is now dynamic — it strips ` - {appDisplayName}` using the registry's display name instead of a 10-entry hardcoded suffix list. `BrowserHistoryReader.ResolveFamily()` is removed; URL resolution searches all visits without a family filter (exact-title match is the primary signal). `SessionLabelResolver` accepts the registry as a parameter and runs profile extraction (`--profile-directory`, `-P`, `--profile=`) for ANY browser the registry knows, not just a hardcoded Chromium-family switch. `InstalledAppDetector.ScanStartMenuShortcuts` no longer pre-classifies shortcuts as browsers — the subsequent registry scan sets `IsBrowser` from URL associations. `IBrowserRegistry` is registered unconditionally in DI (outside the `BrowserTrackingEnabled` guard) because `LogCollectorService` and `SessionLabelResolver` consume it regardless of the tracking master switch. Verified: `dotnet build` 0/0, 0 warnings; zero remaining references to `BrowserProcessHints`, `IsBrowserProcess`, `BROWSER_HINTS`, `ResolveFamily`, or `ResolveChromeProfile` in the client.
> - 2026-08-20: **Web: "Apps & Websites" became a real Configuration domain — dynamic Applications + Websites classification pages and a Categories & Types CRUD, backed by new relational PostgreSQL tables.**
>   The old static `web/src/app/(app)/apps/page.tsx` is deleted. New server migration **023** adds `monitoring_types` (scalable type reference, seeded with Productive `#10B981` / Unproductive `#EF4444` / Neutral `#6B7280`), `monitoring_categories` (name + `kind` CHECK `application|website|both`), classification columns on the DETECTED catalog (`installed_applications.type_id` ON DELETE RESTRICT + `category_id` ON DELETE SET NULL, partial indexes) and `monitoring_sites` — a website registry of one row per observed domain (partial UNIQUE on `domain WHERE deleted_at IS NULL`), auto-synced from distinct `app_items.domain` on every list read (`SyncWebsiteDomains`, idempotent upsert refreshing `last_seen_at`). Server: new `MonitoringRepo`/`MonitoringService`/`MonitoringHandler` + protected `/api/v1/monitoring/{types,categories,apps,websites}` routes (departments layering); type delete returns 409 while any app/site still references it (`CountTypeUsage`); category delete soft-detaches; classification PATCH bodies support nullable presence (`{"typeId": n|null}` — null clears, absent leaves untouched). Web: `monitoringApi` client, three new pages behind the sidebar's existing Configuration section — **Applications** and **Websites** use the shared `ClassifiedItemsTable` component (infinite scroll per the Web Infinite-Scroll Rule, debounced search, Type/Category/unclassified filters, inline per-row Type+Category selects, in-place cache updates so scroll position survives classification) and **Categories & Types** is a two-tab CRUD (cards + dialogs, color picker for types, kind select for categories); the orphaned `apps` permission module was replaced by `configuration/apps|websites|categories` in `ALL_MODULES` + `DEFAULT_PERMISSIONS` (Configuration group, sidebar already pointed at the new routes). Verified: `go build`/`go vet` clean, `go test ./...` clean, migration 023 applied + smoke-tested live on the dev DB (178 catalog apps, 83 auto-discovered sites, classification PATCH/clear, 409 guard, invalid-FK 400, seeded types restored), `tsc --noEmit` clean, `next build` registers the three routes and drops `/apps`. Server + web only — no client/installer change (Installer-Parity Rule N/A).
> - 2026-08-19: **Core: Secure Linux update handoff + Opaque Device Authentication + DB Retention Purge & Performance Indexes.**
>   **Linux Update Handoff**: `AppUpdateService.InstallAsync` replaces synchronous `pkexec dpkg -i` with a detached bash script (`aat_update_*.sh`) in `/tmp`. The script waits for the parent tracker process to terminate before invoking `pkexec dpkg -i`, and automatically relaunches `--background --restart` upon success, preventing headless process hangs.
>   **Device Authentication**: `AuthHandler.EmployeeLogin` generates a secure 256-bit opaque token (`dev_tok_...`) alongside JWT, upserts hardware metadata into `employee_devices`, and enforces device validation on all 11 sync endpoints via `DeviceAuth` middleware (`Authorization: Device <token>`). Added admin device listing and revocation endpoints. Client `EmployeeInfo`, `SqliteLogStore`, and `SyncService` updated to persist `device_token` and send `Authorization: Device ...`.
>   **DB Retention & Performance**: Added `RetentionWorker` background job running hourly DB purges (configurable via `RETENTION_DAYS`, default 30 days) for stale `app_items` and ended `app_sessions`. Created composite index `idx_app_items_session_opened` (`app_session_id, opened_at DESC`) in migration 021 for optimized timeline reads. Verified: `go build`/`go vet` clean, `dotnet build` clean (0 warnings, 0 errors), `tsc --noEmit` clean.
> - 2026-08-18: **Web: Employees list now uses server-side infinite scroll — Next/Previous buttons banned; rule codified in the docs.**
>   The Employees page's `page` state + Previous/Next buttons were replaced with the same
>   `useInfiniteQuery` + IntersectionObserver-sentinel pattern as the Session Timeline / Web Activity
>   journey pages (`employeesApi.list` unchanged — server pagination via `page`/`perPage`, 10/page;
>   filters change the query key and restart at page 1; "Loading more…" inline + "Showing all N" footer).
>   **New mandatory rule — *Web Infinite-Scroll Rule*:** every web list/table page MUST use server-side
>   infinite scroll; Next/Previous buttons are forbidden. Codified in `AGENTS.md` §6 (conventions table +
>   rule subsection), `web/ARCHITECTURE.md` §4 (Data Fetching Strategy table + rule subsection), and
>   `WORKFLOW.md` §1 (Web note). Verified: `tsc --noEmit` clean, `next build` passes.
> - 2026-08-18: **Web: Employees Excel import/export.** The Employees page gains **Import** / **Export**
>   buttons. **Export** downloads `employees-<date>.xlsx` (Employee ID / Name / Email / Department / Shift)
>   from the new `GET /api/v1/employees/export` (all non-deleted employees, `EmployeeRepo.ListAll`).
>   **Import** reads an `.xlsx/.xls/.csv` in the browser (`xlsx` client-side), extracts ONLY the columns
>   whose headers match `userid|user_id|user id|employeeid|employee_id|employee id|name|employee name|
>   username|email|department` (case/whitespace-insensitive — the header map in `employees/page.tsx`),
>   and posts normalized rows to `POST /api/v1/employees/import`. The server imports in ONE transaction
>   (`EmployeeRepo.Import`): departments are **get-or-created by name** (created first, then attached via
>   `department_id`; soft-deleted depts are revived), and each employee is **upserted by the exact
>   employee_id from the spreadsheet** (the `RETURNING (xmax = 0)` trick distinguishes insert vs update;
>   soft-deleted employees revive; email may only be reused by the same employee_id; empty department
>   → Engineering, empty shift → Day). Per-row outcomes (`imported`/`updated`/`skipped` + reason) return
>   to the UI as a result dialog with a skipped-rows table. `employee_id` must be ≤20 chars and unique in
>   the file. Verified: `go build`/`go vet` clean, `tsc --noEmit` clean, `next build` passes. Server + web
>   only — no client/installer change.
> - 2026-08-18: **Embedded-webview journeys — sites opened INSIDE apps (VS Code Simple Browser, Slack, Teams, any Electron/embedded browser) now tracked as web activity, with ZERO hardcoded app names.**
>   Previously only real browser processes were tracked (a hardcoded `BrowserProcessHints` gate in the
>   readers), so a website opened inside VS Code's Simple Browser never reached `/employee-journey/web`.
>   The gate is replaced with **structural detection — the No-Hardcoded-Names Rule applied**: a window
>   carries web content iff its accessibility tree contains a DOCUMENT_WEB node (AT-SPI role 95) whose
>   DocURL is an **http(s)** URL. App chrome excludes itself by scheme (`vscode-webview://`, `file://`,
>   `about:` never match), so no product-name list is needed anywhere — any app with an embedded http
>   document is captured, and the HOST APP (process name, e.g. `code`) rides in metadata `"source":"webview"`
>   so the web dashboard shows it as the source badge. Linux (`LinuxAtSpiBrowserReader`): the probe now
>   scans ALL a11y apps (structurally skipping `--type=` Chromium/Electron child processes and `--headless`)
>   — browsers keep the full path (omnibox + DocURL + incognito), non-browsers must PROVE an http DocURL
>   or they are never emitted. The expensive non-browser walk runs every 5th poll (~15s) with a 400-node
>   budget, and the reader **caches webview windows and re-emits them every poll** so sessions stay stable,
>   focus accounting works, and the tracker's time-based missing-window close (5×interval) never fires
>   between scans (cache entries expire ~60s after a rescan stops producing them). Windows
>   (`WindowsUiaBrowserReader`): non-browser windows are scanned for a descendant Document/Edit whose
>   Value/Name is an http(s) URL (the UIA analog of DocURL) — Electron webviews expose this on demand;
>   browsers unchanged. Tracker: `HydrateTrackedWindowsAsync` now hydrates browser_tab-rooted sessions
>   (webview sessions survive fast relaunches), metadata `source` is `webview` for webview windows, and
>   **the main loop (`LogCollectorService`) now skips sessions whose root item is `browser_tab`** — without
>   that, a webview session (process `code`) hydrated under the SAME key as the host app's own session and
>   was closed as a duplicate one cycle later. Web: the source badge on Web Activity shows the host app
>   process name for webview-source items (data-driven). Verified: `dotnet build` 0/0, `tsc` clean; the
>   ACTUAL embedded probe run live on the dev PC captures Chrome (chatgpt.com, active) with zero junk
>   webview rows, and the poll-4 throttle returns browsers only. ⚠️ Known Linux limitation (Chromium,
>   NOT our code): Electron/Chromium apps only expose their content a11y tree when launched with
>   `--force-renderer-accessibility` (the same documented Chrome limitation) — the webview DocURL is
>   invisible otherwise; Windows/macOS UIA/AX expose webview trees on demand, so the feature works there
>   out of the box. Ships only in a new installer build.
> - 2026-08-18: **Structural process name resolution — Flatpak/snap browsers (Floorp, LibreWolf, etc.) no longer show "xdg-dbus-proxy" as their browser name.**
>   The AT-SPI PID for Flatpak apps belongs to the `xdg-dbus-proxy` IPC broker, not the app itself,
>   so `/proc/<pid>/comm` returned the proxy name. New `resolve_app_name(pid)` in the Python probe
>   checks **FLATPAK_ID** in `/proc/<pid>/environ` (extracts the short app name from the Flatpak app ID,
>   e.g. `org.mozilla.Floorp` → `floorp`) and **snap path** in `/proc/<pid>/exe` (e.g.
>   `/snap/firefox/...` → `firefox`). Falls back to `/proc/comm` for native apps. No name lists —
>   Flatpak and snap are the only sandboxing systems that inject proxy PIDs, and the metadata they
>   expose is structural (OS-defined environment variable / filesystem path). The resolved name is
>   also used for browser detection: new **structural browser detection** scans `.desktop` files for
>   `Categories=WebBrowser` (cached 5 min in `~/.cache/alpha-ai-tracker/browser_exes.json`) and
>   matches against the resolved process name — any browser installed on the system is detected
>   without a hardcoded hints list. Flatpak `.desktop` files with `Exec=flatpak run <app-id>` are
>   parsed to extract the app ID's short name. C# reader: new `_pidNameCache` (populated from AT-SPI
>   probe results each poll) lets `ReadComm()` resolve WM-only Flatpak/snap windows too.
>   `StripBrowserSuffix` gains Floorp/LibreWolf/Waterfox. Verified: `dotnet build` 0/0.
> - 2026-08-18: **Downloaded installers no longer linger in `updates/` — the folder is force-deleted after every successful update (Windows + Linux) + a startup sweep.**
>   Root cause: `AppUpdateService` downloaded the platform installer into the user data dir
>   (`~/.local/share/alpha-ai-tracker/updates` / `%LocalAppData%\AlphaAITracker\updates`) and handed it to
>   the OS installer, but nothing ever deleted it — every updated machine kept every installer it had
>   ever downloaded. Fix: new `CleanupUpdatesDirectoryAsync` force-deletes every file (retry loop, `.part`
>   leftovers included) then the folder itself. Wired into three places: (1) **Linux** — after a successful
>   `pkexec dpkg -i` in `InstallAsync` (dpkg is synchronous, so the app is still alive and can wipe the
>   consumed `.deb` immediately); (2) **Windows** — inside the detached PowerShell update script, after the
>   installer finishes and the app is relaunched (`Remove-Item` on the installer, every file in `updates/`,
>   then the folder — the app can't do it itself because on Windows `InstallAsync` returns BEFORE setup
>   runs); (3) **startup sweep** — `StartAsync` prunes the folder on every boot, so installers already
>   sitting on disk from past updates (including the two PCs affected by this report) are wiped without
>   waiting for the next update. Failures are tolerated (transient AV lock → logged, re-swept on next
>   pass; the next download prunes its own dest anyway). macOS untouched (dmg is opened, not consumed).
>   Verified: `dotnet build` 0/0. ⚠️ ships only in a new installer build.
> - 2026-08-18: **Focus totals were still frozen (~0s on the web) — the periodic flush OVERWROTE the DB row with the in-memory delta instead of accumulating. Fixed + guaranteed 1-minute push.**
>   Live evidence after the previous fix shipped in v1.2.4: Chrome stuck at exactly `fg=30.0`, VS
>   Code/Calendar/Help at exactly `300.0`, byte-identical across 65s of running — so the collector WAS
>   detecting foreground, but the flush SQL (`UpdateAppSessionFocusSql`) did `SET foreground_seconds =
>   $foreground_seconds` and then cleared the counter — each flush rewrote only the last ~10-cycle window
>   (300s main loop / 30s browser tracker), never the session total, and the web App Usage page showed
>   those tiny deltas as 0s. Fix: the flush is now **ADDITIVE** (`foreground_seconds = COALESCE(foreground_seconds,
>   0) + $foreground_seconds`) — the in-memory dictionary stays a per-flush DELTA (cleared after each write,
>   verified both loops), so the SQLite row is the true cumulative session total, survives restarts, and is
>   re-sent verbatim to the server (whose upsert overwrites with it). Close paths were audited for
>   double-counting: `CloseSessionsAndAppItemsAsync`/browser closes flush FIRST then close with NULL focus
>   (`UpdateAppSessionEndedSql` is `COALESCE($fg, fg)` — NULL keeps the flushed total). **User rule
>   2026-08-18 — every minute, force-push all `is_synced=0` rows:** (1) the main loop now calls
>   `FlushSessionFocusAsync` every 2 cycles (~60s at the default 30s interval, was every 10/~5min), so
>   growing totals re-queue every minute; (2) `SyncService` failure backoff is now **capped at 60s** — the
>   backoff only stretches a single retry gap, never the guaranteed cadence, so a drain pass (and thus every
>   `is_synced=0` row) reaches the server within a minute even during repeated failures (previously a 5-min
>   max backoff could starve unsent rows). Verified: `dotnet build` 0/0; the exact additive SQL executed
>   twice against a copy of the live DB accumulates 30→60 fg / 310→320 bg and re-queues `is_synced=0`.
>   ⚠️ ships only in a new installer build.
> - 2026-08-18: **Foreground/background focus times were stuck at 0 — two root causes fixed (live-DB + OS-probe root-caused on the Linux/Wayland dev PC, cross-checked against Windows EMP-10005/EMP-10006).**
>   **Cause 1 — Linux/Wayland foreground detection was completely dead.** Every method in `ProcessCollector.GetActiveWindowInfo` failed on this machine: `xprop _NET_ACTIVE_WINDOW` returns `0x0` (Wayland), the GNOME portal has no `org.freedesktop.portal.Window` interface (GNOME backend doesn't implement it), GNOME Shell Introspect `GetWindows` is `AccessDenied`, xdotool is X11-only, and — the core bug — **the AT-SPI probe could not read `GetState`**: at-spi2-core ≥ 2.50 returns a **PACKED 64-bit bitmask** (`[uint32 1124073730, 0]` — low word first, bit N = state N) instead of a list of state ids, and the check `if 8 not in state` (python) / `Contains("8")` (gdbus fallback) looked for state **8 = ENABLED** (every window has it) in the wrong format. Verified live: the focused Chrome window is the ONLY one whose bitmask has bit **1 = STATE_ACTIVE** set (`0x43000102` vs `0x43000100`); VS Code/Nautilus/etc. don't. Fix: `IsAtSpiActiveState`/`is_active_window` decode BOTH formats and test ACTIVE(1)/FOCUSED(12) — the old fallback heuristic (newest-started process) never matched a tracked session, so `fgKey` was always empty → every session earned only `background_seconds`. **Cause 2 — browser sessions never earned focus time at all.** Browsers are owned by `AccessibilityBrowserTracker` (excluded from the main loop's `resolvedLogs`), and the tracker never wrote foreground/background — so Chrome/Firefox/Edge rows (the majority of usage!) always showed 0/0, and when a browser held focus even non-browser apps got only background (fgKey empty). Fix: new `AccessibilitySnapshot.IsActive` set per-platform (Linux AT-SPI ACTIVE/FOCUSED bit, Windows `GetForegroundWindow` HWND match, macOS frontmost process) and the tracker now accumulates the poll interval into `foreground_seconds`/`background_seconds` per open window every poll (active window → foreground, rest → background), flushing every 10 polls + on close through the existing `UpdateAppSessionFocusAsync` (re-syncs via `is_synced=0`). Verified: `dotnet build` 0/0; the exact new decode run live on the dev PC returns exactly one active window. ⚠️ ships only in a new installer build.
> - 2026-08-18: **Client: app-session tracking now starts headlessly at boot — the GUI can no longer start/stop tracking.**
>   Root cause: `_trackingEnabled` was set ONLY by `StartTracking()`, which was called exclusively from the
>   Avalonia GUI (`MainViewModel.InitializeAsync` / `LoginAsync`). Since 2026-08-15 boot runs `--background`
>   (services only, no window), a powered-on PC restored the login (`LogCollectorService.RefreshEmployeeInfo`)
>   but the main loop spun on "waiting for login" forever: browser journeys kept flowing (`AccessibilityBrowserTracker`
>   restores the login from SQLite itself) while ZERO app sessions were collected — the web dashboard showed Web
>   Activity but empty Session Timeline/App Usage after every reboot (Linux AND Windows). Fix: `ExecuteAsync` now
>   calls `StartTracking()` right after `RefreshEmployeeInfo` when persisted employee credentials exist — the same
>   restore the GUI performed, moved into the service so it runs in `--background` mode at power-on. The GUI is now
>   strictly login-only (first-time identity + permission wizard); opening/closing it never starts or stops tracking
>   (window-close hides to tray; only an explicit tray Quit / process stop exits the tracker). `StartTracking()` is
>   idempotent (guarded login-event dedup), so the GUI restoring a second time is harmless. Verified: `dotnet build`
>   0/0.
> - 2026-08-18: **Web: Server-side date filters + expandable session groups + structural browser badges + UX fixes on App Usage and Web Activity.**
>   Server: `GET /app-sessions` and `GET /app-items` now accept `dateFrom`/`dateTo` (RFC3339 or date-only),
>   filtering `started_at` / `opened_at`; app-items search also matches `url`/`domain`. New shared
>   `ActivityFilters` component (debounced search, Today-default date presets with real local-day bounds,
>   custom range via Calendar popover) wired into both pages. **App Usage**: Duration column replaces
>   Foreground/Background (computed from `endedAt - startedAt` / `now - startedAt`); Active Time tile =
>   sum of durations; apps with >1 session get chevron → expandable nested per-session table (Opened/
>   Closed/Duration/Details). **Web Activity**: pages grouped by domain ("Visisted Sites") with expandable
>   groups; browser badge (Chrome/Floorp/etc.) from `metadata_json.processName` on every row + distinct
>   browsers per site group; search switches to flat "Matching Pages" view (exact URL visible). Both pages
>   use `keepPreviousData` so filter changes never swap the whole content to a spinner; `ActivityFilters`
>   is always mounted (search field never loses focus, filters never disappear on empty results).
>   `next.config.ts` gains `allowedDevOrigins` (suppresses cross-origin dev warning). Verified: `tsc`
>   clean, `next build` passes.
> - 2026-08-18: **Web: Employee Journey + Device Specs modules — the `/users/[id]` detail page was replaced by nine pages behind a shared shell.**
>   The old 820-line `web/src/app/(app)/employees/[id]/page.tsx` is deleted. New shared `EmployeePage` shell
>   (`web/src/components/employees/EmployeePage.tsx`) provides the page header, a searchable `EmployeeSelector`
>   picker (deep-linkable via `?employeeId=`), and loading/error/no-selection states; the page body is a
>   render-prop handed `{employee, detail?, detailLoading}`, and pages needing the aggregate machine picture pass
>   `fetchDetail` to load `GET /employees/:id/detail` (`hooks/use-employee-detail.ts`). **Employee Journey** —
>   Session Timeline (`useInfiniteQuery` on `GET /app-sessions`, 30/page, IntersectionObserver sentinel,
>   `FocusTime` fg/bg stacked bar), App Usage (aggregates the most recent 5×100 app sessions into per-app
>   fg/bg totals), Web Activity (`GET /app-items?itemType=browser_tab`, infinite scroll), plus Screenshots /
>   Location Trail placeholders (the client collects neither yet). **Device Specs** — Hardware Overview (specs +
>   storage + network + app_status), Installed Software (Applications/Packages tabs with search,
>   `InventoryTable`), Peripherals (plugged/unplugged cards + `DeviceClassIcon`), Permissions. The employees
>   table action menu now offers **View Journey** (`/employee-journey/timeline?employeeId=`) and **Device
>   Specs** (`/device-specs?employeeId=`) instead of the old detail link. `AppItem` gained the journey fields
>   in `api.ts` (`url`, `domain`, `processId`, `objectType`, `action`, `journeyId`, `sequence`, `previousPath`,
>   `currentPath`, `windowId`, `tabId`, `metadataJson`); new permission modules `employee-journey` +
>   `device-specs`; new shared helpers `lib/format.ts` + `EmptyState`. **Hook-order crash fixed while landing:**
>   hooks (`useQueries`/`useInfiniteQuery`/`useMemo`…) were being called INSIDE the `EmployeePage` render-prop,
>   so selecting an employee changed the shell's hook count and React threw "Rendered more hooks than during the
>   previous render" — each page body was extracted into a real inner component (`AppUsageBody`/`TimelineBody`/
>   `WebBody`/`SoftwareBody`). Verified: `tsc --noEmit` clean.
> - 2026-08-17: **Web sidebar restructure — Device Specs became a collapsible section.** `AppSidebar.tsx`: the
>   single top-level "Device Specs" item is now a parent with four children — Hardware Overview
>   (`/device-specs`), Installed Software (`/device-specs/software`), Peripherals (`/device-specs/peripherals`),
>   Permissions (`/device-specs/permissions`) — all under the existing `device-specs` permission module;
>   "Employee Journey" stays a collapsible with Session Timeline / App Usage / Web Activity / Screenshots /
>   Location Trail. The rest of the commit is whitespace alignment of the nav config.
> - 2026-08-15: **Duplicate sessions fixed — one session per logical window on Windows/macOS (root-PID grouping).** Root cause: on Linux, systemd cgroups (`app-*.scope`) collapse VS Code's ~8 same-named `Code.exe` processes into ONE session; on Windows/macOS there are no cgroups, so the session key fell back to per-PID and every `Code.exe` process (main window + renderer + GPU + utility + extension hosts) created its OWN session row — the web detail page showed 8 identical "Visual Studio Code" rows for one window. Fix: `LogCollectorService.ResolveRootPid` walks the PPID chain up to the TOP-MOST same-binary process (the app's main window process — a different binary is a hard boundary, so two VS Code windows still get two sessions) and every `BuildSessionKey`/`ProcessId`/root-item call site keys on it; boot hydration now dedups legacy per-PID open sessions that collapse to one key (keeps the earliest, closes the rest so they stop showing "running"). Also: a title-less child creating the session root first no longer leaves the root stuck on the process name — `UpdateActivityContextAsync` upgrades the open root in place when a window-bearing process arrives.
> - 2026-08-15: **Foreground/background focus time per session (client + server + web).** The collector already knew the OS foreground PID each cycle (`ActivityLog.IsForeground`) but never persisted it. Client: new `foreground_seconds`/`background_seconds` on SQLite `app_sessions` (idempotent MigrateSql ALTERs), accumulated per cycle for every open session (mapped through the same root-PID grouping so the whole window counts), flushed every 10 cycles + on close with `is_synced=0` (rule 2026-08-12) so SyncService re-sends; payload sends the values. Server: migration **020** adds the two columns, model/DTO/`SyncAppSessions`/`BulkInsertAppSessions` upsert (EXCLUDED overwrite — the client re-sends the totals) + `ListAppSessions` SELECT. Web: employee detail **Activity tab rebuilt with server-side pagination + infinite scroll** (`useInfiniteQuery` on `GET /app-sessions`, 30/page, IntersectionObserver sentinel) and columns **Application, Status (Running/Closed), Opened, Closed, Duration, Foreground/Background** (green/gray stacked bar + `fg / bg` readout).
> - 2026-08-15: **Boot is now fully headless — GUI only opens on manual launch.** Auto-start (Windows Run key + ONSTART scheduled task, Linux `.desktop` autostart, macOS plist) switched from `--minimized` (hidden-to-tray, UI still created) to `--background` (services only — no window, no tray at boot). `Program.cs` background mode now lazily creates the Avalonia UI ONCE on a dedicated thread when a manual launch sends the SHOW signal (new `App.LaunchedHidden` flag replaces the args check in `App.axaml.cs`), so "open the GUI" works any number of times without stopping the tracking process. The background guard migrates stale `--minimized` autostart entries to `--background` automatically (`AutoStartService.EnsureBackgroundAutoStartFlag`).
> - 2026-08-13: **Server: `employees.department` name column removed — department_id is the sole source of truth.**
>   The `employees` table stored the department NAME as a denormalized VARCHAR next to the `department_id` FK.
>   Migration **019** drops `employees.department` (column + `idx_employees_department` index, mirroring the 018
>   role-drop pattern). The department name is now resolved ONLY at read time via the existing
>   `LEFT JOIN departments d ON e.department_id = d.id` (`COALESCE(d.name, '')` in all SELECT/RETURNING clauses),
>   so API responses still carry a `department` name; nothing writes it anymore. Go: `Department` removed from
>   `CreateEmployeeRequest`/`UpdateEmployeeRequest` and from the UPDATE allowed-fields map + service branches
>   (the model/`EmployeeResponse` keep the derived name for the web table/display); repo List filter now matches
>   `d.name` (and the count query gained the departments JOIN), INSERT writes `department_id` only. Web:
>   `department?` dropped from `CreateEmployeePayload`/`UpdateEmployeePayload` (the value was never sent — the UI
>   always used `departmentId`). `users` admin table is unrelated and unchanged. Verified: `go build`/`go vet`
>   clean. **2026-08-13: Client `ALPHA_FILE_JOURNEY_ENABLED` — file-journey master switch.** New `.env` knob
>   (`.env` + `.env.example`, default `true`) controls the **Desktop Event Bus** (file-manager navigations +
>   file create/rename/delete/recent-file journeys — AT-SPI on Linux, Shell COM on Windows, FileSystemWatcher +
>   recent-files on every platform). `false` → the entire event bus (`EventCoordinator`, `JourneyEngine`,
>   `ATSPIEventWatcher`, `WindowsExplorerWatcher`, `FileSystemEventWatcher`, `RecentFilesWatcher`,
>   `DesktopEventService`) is NOT registered in DI, so zero file-journey rows are produced or synced. Browser
>   journey tracking is governed separately by `ALPHA_BROWSER_TRACKING_ENABLED` (unchanged). `AppConfig`
>   property + `--print-config` line added. Verified: `dotnet build` 0/0.
>
> - 2026-08-13: **Employees rename + employee role removed.** Web: the HR sidebar item "List of Users" is now
>   **"Employees"** and the routes `/users`, `/users/[id]`, `/users/activity` moved to `/employees`,
>   `/employees/[id]`, `/employees/activity` (permission module keys stay `users`/`users/activity` so stored
>   permission configs in localStorage survive). Employee `role` was removed end-to-end on the employee surface:
>   server migration **018** drops `employees.role` (column + its index), and `role` is gone from the Go employee
>   model/`EmployeePublic`/DTOs (`CreateEmployeeRequest`, `UpdateEmployeeRequest`, `EmployeeResponse`), the repo
>   (list filter, all SELECTs, INSERT, UPDATE allowed-fields, scans), the service, the handler, and the
>   `POST /auth/employee-login` response. Web: `role` removed from `Employee`/`CreateEmployeePayload`/
>   `UpdateEmployeePayload`, the employees table (Role column), the Add-Employee dialog, and the employee detail
>   page role badge. Desktop client: the Role line is gone from the dashboard hero and the nav-rail identity
>   block (`EmployeeRole` VM props + XAML bindings); the local `employee_info.role` cache column is left intact
>   (no client migration risk — the server simply no longer sends a value). **Admin-user RBAC is unrelated and
>   unchanged** (`users.role`, `UserRole`, permissions, settings/user-management, onboarding invites). Verified:
>   `go build`/`go vet` clean, `tsc --noEmit` clean, `next build` registers `/employees`,
>   `/employees/[id]`, `/employees/activity`, `dotnet build` 0/0.
> - 2026-08-12: **Windows auto-update "downloaded but never installed" root-caused — the installer was TREE-KILLING itself.**
>   Linux updated fine, Windows did nothing after the download finished. Root cause: `installer-windows.iss`
>   `KillRunningInstance` ran `taskkill /F /IM client.exe /T` — the **`/T` is a TREE-kill**. The self-updater
>   launches the installer as a DESCENDANT of the running app (`client.exe → cmd.exe update script → setup.exe`),
>   so that tree-kill terminated the updater's OWN cmd script AND the installer itself in `InitializeSetup` —
>   before a single file was written, with the relaunch script dead too → old version stays. Linux works because
>   `pkexec dpkg` is a separate elevated process, never in the app's kill-tree. Fix, both sides:
>   **(1)** `installer-windows.iss` — `/T` removed from `KillRunningInstance` (kills by image name only as a
>   manual-install safety net); exe name is no longer hardcoded either (`{#MyAppExeName}`) and `AppMutex` is now
>   `{#APP_MUTEX}` — both from APP_IDENTIFIERS via `windows_vars.iss`. **(2)** `AppUpdateService.InstallAsync`
>   (Windows) rewritten so the updater does NOT depend on the installer killing the app: it writes a detached
>   `.cmd` to `%TEMP%` that (a) polls `tasklist /FI "IMAGENAME eq {exeName}"` (exit code 0 = running, 1 = gone —
>   no `find` pipe, so the 25-char tasklist display truncation that broke the old `find`-based check is moot)
>   until the app exits (max ~60s, then falls through — the installer's image-name taskkill is the net), (b)
>   launches the silent installer (`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`), (c) waits for setup to APPEAR
>   (UAC approval — setup.exe is only created AFTER the consent prompt is approved; ~5 min), (d) waits for it to
>   finish (~10 min), (e) relaunches `{exe} --restart` and (f) deletes itself. The app shuts itself down
>   gracefully via `Dispatcher` (Program.cs finally releases the mutex) right after spawning the script. **Slow-UAC
>   self-heal:** if setup never appears (UAC denied / launch failed) the script relaunches the old app but KEEPS
>   watching — a slow consent click can still spawn setup after that relaunch (setup force-closes the app it
>   finds), so it waits that setup out and relaunches the NEW build once more. Bounded counters everywhere
>   (60/300/600/60/600) — no loop can run away. Verified: `dotnet build` 0/0; two code reviews walked every
>   batch line against real-Windows `tasklist`/UAC/Inno semantics; the exact rendered `.cmd` was extracted from
>   the source and audited (every `goto` resolves, all five loops bounded, no executable `/T` anywhere). Wine
>   cannot smoke-test the flow (its `tasklist` returns 0 for no-match, unlike Windows' 1), so the real test is a
>   Windows machine: click Update → app closes → UAC → silent install → app reopens on the new version. Note:
>   the previous 2026-08-12 release/installer-cleanup fix (no stale assets) must ship TOGETHER with this —
>   rebuild the Windows installer so the new `.iss` (no `/T`) actually reaches machines.
>
> - 2026-08-12: **Auto-update "still shows old version" root cause fixed — stale installers can never be published or picked.**
>   Live evidence: the GitHub `v1.1.1` release carried BOTH `alpha-ai-tracker_1.1.0_amd64.deb`/`AlphaAITracker-Setup-1.1.0.exe`
>   AND the 1.1.1 ones, and the client's `updates/` dir held the downloaded **1.1.0** deb — because `release.sh` uploaded
>   EVERY file left in `client/installers/` from the previous release, and `ResolvePlatformAsset` used `FirstOrDefault`,
>   so the OLD installer won. "Update to v1.1.1" re-installed 1.1.0 over 1.1.0. Two-part fix: **(1)** `release.sh` and
>   `build-installer.sh` now `rm -rf "$INSTALLER_DIR"` at the start (after arg parsing in build-installer so `-h` doesn't
>   wipe; release.sh cleans before anything else so even a failed build can never upload stale artifacts) — stale assets are
>   unrepresentable; **(2)** `AppUpdateService.ResolvePlatformAsset` now prefers the asset whose name embeds the RELEASE
>   version (boundary-aware regex `(^|[\-_.])1\.1\.1(?=$|[\-_.])` — "1.1.10"/"1.11.1" can never match a request for "1.1.1"),
>   falling back to the old arch/extension matching for well-formed releases. Verified: `bash -n` both scripts, `dotnet build`
>   0/0, regex unit-checked against the real asset names (stale skipped / new matched / prefix-collisions rejected).
>   Note: the ALREADY-published v1.1.1 release still contains the stale 1.1.0 assets — future releases won't, and the client
>   now picks 1.1.1 even if they linger.
>
> - 2026-08-12: **Self-update from GitHub Releases — auto-update + GUI "Check updates".** New
>   `client/Services/AppUpdateService.cs` (ObservableObject + IHostedService singleton, same
>   register-singleton-and-hosted pattern as SyncService): checks
>   `https://api.github.com/repos/{repo}/releases/latest`, normalizes the tag (`v1.1.0` → `1.1.0`),
>   picks the platform installer asset (Linux `_amd64.deb` by runtime arch, Windows `.exe`, macOS
>   `.dmg`) and compares against `AppInfo.Version` (numeric three-part compare, `-beta`/`+sha`
>   ignored). Quiet **auto-check loop** every 30 min that only fires when the persisted
>   `update_last_check_at` (app_status) is older than `ALPHA_UPDATE_AUTO_CHECK_HOURS` (24h); with
>   `ALPHA_UPDATE_AUTO_INSTALL=true` (default) it **auto-downloads and installs** with no click:
>   Linux `pkexec dpkg -i` (the only human step is the polkit password dialog — the install dir is
>   root-owned), Windows runs the Inno installer with `/VERYSILENT` (Inno `CloseApplications=force`
>   terminates the app itself, so a detached `.cmd` waits then relaunches with `--restart`), macOS
>   `open`s the dmg (manual drag). Downloads stream into the user data dir
>   (`~/.local/share/alpha-ai-tracker/updates` / `%LocalAppData%\AlphaAITracker\updates`) — never the
>   install dir. GUI: top bar gains **Check updates** (ghost), a **"Update to vX.Y.Z"** install
>   button, a **Restart to apply** button (Linux dpkg replaces the binary while running) and a status
>   line; the dashboard shows an **update banner** (version + release notes + progress bar + Later
>   which persists `update_dismissed_version`). `Program.cs` handles the new `--restart` arg by
>   retrying the single-instance mutex for 8s (post-update relaunch vs signal-and-exit). Config: new
>   `ALPHA_UPDATE_REPO/ENABLED/AUTO_CHECK_HOURS/AUTO_INSTALL` keys in `.env` + `.env.example` +
>   `AppConfig`. The repo ALWAYS comes from `.env` — `ALPHA_UPDATE_REPO`, falling back to the
>   pre-existing `REPO=` key (now actually read); there is NO hardcoded repo anywhere, and when
>   neither key is set the updater is disabled with a clear "No update repository configured"
>   message on manual checks. Verified: `dotnet build` 0/0;
>   live smoke test hit the real GitHub API (`You're up to date (1.0.0).` against the v1.0.0
>   release with `alpha-ai-tracker_1.0.0_amd64.deb` + `AlphaAITracker-Setup-1.0.0.exe` assets).
>   **Note:** `AppUpdateService` deliberately uses EXPLICIT properties + `RelayCommand` fields
>   (no `[ObservableProperty]`/`[RelayCommand]` source generators) — generated members made IDEs
>   show phantom "name does not exist" errors until the analyzer re-ran, even though the CLI build
>   was always clean. Manual install/auto-install share one download path behind an atomic
>   `_installGate` so the shared `.part` file can never be written by two threads at once.
>
> - 2026-08-12: **Client rule — any UPDATE on an already-synced row resets `is_synced=0` (server always learns changes).**
>   Audited every write path in the client SQLite layer and found 8 that mutated rows without re-queueing them:
>   (1) `UpdateAppSessionEndedSql` (session close) — CRITICAL: a session synced as OPEN never told the server it
>   ended; the web dashboard showed it open forever. (2) `CloseHardwareDeviceAsync` — CRITICAL: a plugged-in device
>   synced as connected never told the server it was unplugged. (3) `InsertAppSessionSql` + (4) `InsertAppItemSql`
>   `ON CONFLICT` DO-UPDATE clauses — a re-stored session/item now re-syncs. (5–8) all four `network_info`
>   mutators (`MarkNetworkInfoNotCurrentAsync`, `TouchNetworkInfoAsync`, `MarkAllNetworkInfoNotCurrentAsync`,
>   `TouchCurrentNetworkInfoAsync`) + the `MigrateSql` current-row backfill — demoted/touched rows re-sync so the
>   server can never show a superseded row as current. Server-side consumption verified: `app_sessions`/`app_items`/
>   `hardware_devices` upsert on conflict (DO UPDATE), so re-sent rows propagate. ⚠️ `network_info` sync is
>   `ON CONFLICT (id) DO NOTHING` server-side and has no `is_current`/`last_seen_at` columns — re-sent network rows
>   are inert no-ops today (kept for rule consistency + future-proofing; the `TouchCurrentNetworkInfoAsync` reset
>   re-queues one tiny row ~every 5 min on stable networks). All other update paths already reset `is_synced`.
>   Verified: `dotnet build` 0/0, `go build`/`go vet` clean, all 8 edited statements executed cleanly against a
>   copy of the real client DB.
>
> - 2026-08-11: **Web user-detail page + employee detail endpoint.** The HR → List of Users page
>   dropdown was clipped by the table's `overflow-x-auto` scroll container whenever the list was
>   short (the custom absolute-positioned menu) — replaced with the Radix `DropdownMenu` (portal-
>   rendered, can never be clipped), and each row now links to a new **`/users/[id]`** page.
>   Server: new aggregate endpoint **`GET /employees/:id/detail`** (protected) returns one
>   employee's full machine picture in a single response — employee record, latest
>   `device_hardware_info`, `storage_devices`, latest `network_info`, currently-installed
>   applications/packages (active `employee_installed_*` junction links joined with the catalog
>   rows), `hardware_devices` peripherals, `permission_status` checks, `app_status` key/value map
>   and activity stats (session/item counts + last activity). UUID route param is resolved to the
>   `EMP-XXXXX` id first (sync tables are keyed by it). New repo read methods in `new_schema_repo.go`
>   (`GetLatestDeviceHardware`, `ListStorageDevices`, `GetLatestNetworkInfo`,
>   `ListEmployeeApplications`, `ListEmployeePackages`, `ListHardwareDevices`, `ListAppStatus`,
>   `ListPermissionStatus`, `GetEmployeeActivityStats`), `NewSchemaService.GetEmployeeDetail`,
>   handler + route. The page shows identity/status, six stat tiles, and tabbed sections for
>   Hardware (specs + storage + network + device status), Applications, Packages, Peripherals and
>   Permissions — all real API data, with loading/empty/error states and a Generate-Secret dialog.
>   Verified: `go build`/`go vet` clean, `tsc --noEmit` clean, `next build` succeeds with
>   `/users/[id]` registered as a dynamic route.
> - 2026-08-11: **Instant sync on login — full machine picture lands on the server the moment an employee logs in.**
>   `SyncService` gained a wake-up signal (`RequestImmediateSync()` — `SemaphoreSlim(0,1)` released by the caller;
>   the inter-pass wait is now `WaitAsync(wait, ct)` so a release ends the wait at once and runs a full drain pass
>   instead of waiting out the 60s idle tick). `MainViewModel` injects the singleton `SyncService` (now registered
>   `AddSingleton` + `AddHostedService(GetRequiredService)` — same pattern as `LogCollectorService`) and fires it in
>   BOTH login paths: `LoginAsync` (after `SaveEmployeeInfoAsync`, so the token is persisted before the sync reads it)
>   and `InitializeAsync` (session restore on launch). The instant pass drains every unsent table —
>   `device_hardware_info`, `installed_applications`, `installed_packages`, `network_info`, `storage_devices`,
>   `hardware_devices`, `session_events`, `permission_status`, `app_status`, `app_sessions`, `app_items` — in the same
>   byte-bounded/gzip/backoff pipeline as the idle passes. `employee_info` itself needs no sync: the login endpoint
>   response already carries the employee record server-side (the client table is its offline cache). Semantics: a
>   request while a pass is running or while one is already pending is a single no-op release (one drain covers it),
>   so login never stacks passes. Verified: `dotnet build` 0/0, code review clean (no races; credentials persisted
>   before signal; DI resolves the same singleton).
> - 2026-08-11: **Client retention + 4 new server sync surfaces.** The client now DELETES synced
>   data the server already has: `app_items`/`app_sessions` older than `ALPHA_SYNC_RETENTION_HOURS`
>   (24h default; OPEN sessions are NEVER deleted, and a session is only deleted once all its
>   app_items are gone), `installed_applications`/`installed_packages` rows with `is_installed=0`
>   (closed install cycles), and superseded `network_info` rows (`is_current=0`). Everything else
>   is retained forever. Runs in `SyncService` after each clean drain pass, one SQLite transaction.
>   **Four previously local-only tables now sync to the server** (migration 017): `app_status`
>   (key/value; a value change resets is_synced so the server learns it next roundtrip),
>   `hardware_devices`, `permission_status`, `storage_devices` — new DTO/service/repo/handler/routes
>   (11 sync endpoints total). **permission_status duplication root-caused & fixed:**
>   `SetPermissionStatusAsync` minted a FRESH GUID per check (~every 5 min) and inserted new rows
>   instead of updating → ~2,800 rows/day; now keyed on the stable `"{platform}_{method}"` with
>   `ON CONFLICT(check_id) DO UPDATE` (one row per permission method). Client: `is_synced`/`synced_at`
>   columns added to `app_status`/`permission_status` (idempotent MigrateSql ALTERs);
>   `MarkSentCoreAsync` generalized to a configurable id column. Server DB wiped as requested
>   (all data tables emptied; users/employees/departments/schema_migrations kept — verified).
>   Verified: `dotnet build` 0/0, `go build`/`go vet` clean, migrations 001–017 applied.
> - 2026-08-11: **Sync engine decoupled — collection NEVER blocks on the network; 50k+ backlogs drain in minutes.**
>   The old inline sync ran inside the collection loop every 10 cycles (~5 min) and fetched a FIXED 500 rows/table/cycle
>   (50k queued rows → ~8 h to drain, while collection paused and CPU spiked). New dedicated `client/Services/SyncService.cs`
>   (BackgroundService, registered in `Program.cs`) drains unsent SQLite rows on its own loop: chunks bounded by BOTH row
>   count (`ALPHA_SYNC_MAX_ROWS`, 1000) and serialized payload bytes (`ALPHA_SYNC_MAX_BYTES`, ~1 MB, oversized slices
>   auto-split by binary halving), ~150 ms politeness pause between chunks, a 5-min per-pass budget
>   (`ALPHA_SYNC_MAX_DURATION_SEC`) so a huge backlog never monopolizes CPU, and **exponential backoff** on failure
>   (5s→10s→…→`ALPHA_SYNC_BACKOFF_MAX_SEC` 5 min). Request bodies are **gzip-compressed** (`ALPHA_SYNC_COMPRESSION`,
>   server side: `middleware.BodyLimit("20M")` + `middleware.Decompress()` added in `server/internal/router/router.go` —
>   Echo's default 2 MB body cap could reject big raw batches). SQLite mark-sent is now batched
>   (`UPDATE … WHERE id IN (…)`, 400 ids/statement — was one UPDATE per row), and existing DBs gained
>   `(is_synced, started_at)` / `(is_synced, opened_at)` indexes on the two big tables (fresh DBs already had them).
>   Server: `SyncInstalledApps`/`SyncInstalledPackages` now use **ONE transaction per request** (was 1 tx per entry =
>   500 tx per batch). New env knobs: `ALPHA_SYNC_INTERVAL_SEC / MAX_ROWS / MAX_BYTES / CHUNK_DELAY_MS /
>   MAX_DURATION_SEC / BACKOFF_MAX_SEC / COMPRESSION` (`.env` + `.env.example` + `AppConfig`). The collection loop now
>   only does `StorePermissionStatus` + heartbeat. Docs: `client/ARCHITECTURE.md` §13 + env table. Verified: `dotnet build`
>   0/0, `go build`/`go vet` clean. ⚠️ Installer-Parity: not "done" until verified from an installed build (re-bake
>   `config.enc` — the new vars must be in `.env` before `encrypt-config.sh`).
> - 2026-08-11: **Inventory GUI shows ONLY currently-installed software — uninstall history hidden.** The Installed Applications page (Applications/Packages tabs) no longer lists closed install cycles: `InstalledAppsViewModel.ApplyFilter` skips `IsInstalled == false` rows, and the table's **UNINSTALLED column was removed** (header + cell) along with the dimmed `RowOpacity` history treatment — the `RowOpacity` property is gone from `InventoryRow` (closed-cycle rows still live in SQLite and sync upstream unchanged; page + dashboard badges already counted only installed cycles). The lifecycle data model is untouched — this is display-only, client-only (no server/web change).
> - 2026-08-11: **Package install/uninstall is now real-time too — `InstalledSoftwareWatcher` gained package-manager watch locations.** The round-3 watcher only watched APP evidence (`.desktop` dirs + `/var/lib/dpkg` on Linux), so `npm install -g`/`npm uninstall -g` (e.g. cline) changed nothing the watcher saw — the DB only updated when the GUI Rescan button manually called `RescanInventoryAsync`. New `GetPackageWatchDirectories()` watches the trees where PACKAGE installs leave filesystem evidence: **Linux** — npm global root (resolved at runtime via a time-boxed `npm root -g` probe, since the root varies per setup: nvm/fnm/user-prefix/deb node, plus `/usr/local/lib/node_modules` + `/usr/lib/node_modules` fallbacks; a package dir is a DIRECT child, so one non-recursive watcher sees install/uninstall instantly), `~/.local/lib` (PEP 370 pip `--user` site, recursive — the versioned site-packages dir is a grandchild), `/var/lib/snapd/db` (snap state, rewritten in place → LastWrite), flatpak runtime roots; **Windows** — `%APPDATA%\npm\node_modules` (npm global); **macOS** — Homebrew Cellars. node_modules/Program Files trees are watched top-level only (recursion inside package trees only adds noise); `RescanInventoryAsync` already re-scans apps AND packages, so one event covers both. Verified live on this DB, one process lifetime, no GUI: `npm install -g cline` → new open `installed_packages` row in **~2 s**; `npm uninstall -g cline` → that cycle closed in **~3 s**; rescan count stays flat while idle (2 total, no loop); cline restored to its pre-test (uninstalled) state; build 0/0, client healthy.
> - 2026-08-10 (round 3): **Install/uninstall detection is now 100% EVENT-DRIVEN — no minute-based polling (user rule 2026-08-10: no periodic inventory scan).** New `InstalledSoftwareWatcher` (BackgroundService) watches the OS install locations — Linux `.desktop` dirs (user + system + flatpak + snap exports) and `/var/lib/dpkg`; Windows Start Menu (user + common) and Program Files (top-level only); macOS `/Applications` — and triggers an instant `LogCollectorService.RescanInventoryAsync` whenever software is installed/uninstalled through terminal, software center, control panel, cmd/powershell or manual file delete. Events are debounced (1.5s anchored to the FIRST event of the burst, so a constant event stream can never starve the rescan), min-gapped (5s) and coalesced (an in-flight rescan is never stacked; a change arriving mid-scan re-arms the burst so nothing is lost). The collector's `_cycleCount % 30/60` periodic app/package scans were REMOVED entirely — the one-time startup scan is the only other scan — and `ALPHA_INVENTORY_SCAN_MINUTES` was deleted (only `ALPHA_INVENTORY_WATCH_ENABLED` remains). The GUI Installed Apps page runs a 5s `DispatcherTimer → PollAsync` (pure SQLite re-read, no OS scan) while visible, so changes appear live without clicking Rescan or restarting. **Two root-cause bugs were found and fixed live:** (1) **probe deadlock froze the watcher** — the old `StandardOutput.ReadToEnd() → WaitForExit(ms)` pattern blocked forever when a grandchild inherited the stdout pipe (observed `wait_for_partner`/`anon_pipe_read`); because the rescan never returned, its in-flight flag stayed set and EVERY later install/uninstall event was coalesced away — the DB looked frozen until an app restart. All 11 CLI probes (10 in `PackageDetector`, 1 in `InstalledAppDetector`) now go through the new `ProcessFilter.RunProbe`: concurrent stdout+stderr drain (a chatty stderr can never fill its pipe), a hard time-box on BOTH exit and stream drain, and `Kill(entireProcessTree)` on timeout; (2) **uninstall was invisible after the first rescan** — `InstalledAppDetector.ForceRecheck()` cleared `_knownApps` but NEVER `_installedApps`, and the platform scanners only APPEND, so an uninstalled app stayed in every scan result forever and the lifecycle pass never closed its cycle (it only looked correct in earlier tests because each test cycle restarted the client). `ForceRecheck` now clears `_installedApps` + `_binaryToDisplayName` (PackageDetector already cleared its `_packages`). Verified live on this DB, one process lifetime, no restart: copyq install → new open row in **~2–5 s**; copyq uninstall → that row closed in **~2–5 s**; rescan count stays flat while idle (no loop); client healthy, build 0/0. Installer note: the watcher is compiled into `client.dll` (no packaging change); the `ALPHA_INVENTORY_WATCH_ENABLED` knob ships via the normal `config.enc` pipeline (`.env` before `encrypt-config.sh`).
> - 2026-08-10 (round 2): **Install/uninstall lifecycle REDESIGNED — ONE ROW PER INSTALL CYCLE (v1 install_count design replaced).** The user's rule: install date is the *honest* OS-reported date — pre-existing software the tracker never saw installed shows **NULL / “Unknown”** on the GUI (no `detected_at` backfill faking a date), and only **newly detected installs** (copyq while the tracker runs) get the current time stamped. And no install counter: install→uninstall→reinstall yields **one record per cycle** — `record 1 (install → uninstall)`, `record 2 (install → …)`. Client SQLite tables were rebuilt into the rows-per-cycle shape: `app_name` and the package `(package_name, source_manager)` fingerprint are **no longer unique** (a UNIQUE constraint would block reinstall history), `install_count` is dropped, and `install_date` is reset to NULL for all pre-existing rows (legacy detection via `install_count` column presence → `RebuildInventoryTablesIfLegacyAsync` drops+recreates both tables in one tx with `PRAGMA foreign_keys=OFF` — the parent tables are FK-referenced by `app_sessions`, and the idempotent migration must also NOT re-create the old UNIQUE fingerprint index, which it previously did on every launch and which would have broken reinstall inserts). Store upserts now open/close cycles: an open cycle (`uninstall_date IS NULL`) is updated in place (install_date kept), otherwise a NEW cycle row is inserted (install_date = OS date, or NULL on the baseline scan, or now for a runtime-detected install). `ApplyInventoryLifecycleAsync` is close-only (missing open rows → `is_installed=0` + `uninstall_date`); reinstalls open new rows in the store pass. Mappers/lookups prefer the open cycle; the GUI lists **only currently-installed cycles** — closed history rows are filtered out of the table and there is no Uninstalled column (display-only change, 2026-08-11); page + dashboard badges count only currently-installed cycles. Two migration bugs found & fixed on the live DB: the rebuild deadlocked startup (`SemaphoreSlim` gate re-acquired inside the gated `InitializeAsync` — now gate-free, documented) and then failed with `SQLite Error 19 FOREIGN KEY constraint failed` on `DROP TABLE` — fixed with `PRAGMA foreign_keys=OFF/ON` around the rebuild. Verified live on this DB (build 0/0): copyq install → `install_date=12:19:00` while VS Code/Firefox/Chrome stay NULL/Unknown; copyq uninstall → `is_installed=0` + `uninstall_date=12:20:05`; copyq reinstall → **NEW row** `install_date=12:21:03` (2 records for 2 cycles, exactly as specified); cline reinstall likewise opens a new row; migration zero false uninstalls; client healthy.
> - 2026-08-10: **Employee disconnect removed.** The **Disconnect** button was removed from the
>   client nav rail, along with the entire employee-disconnect flow: `LogoutCommand`/
>   `LogoutAsync` and `LogCollectorService.StopTracking()` are gone from the client, and the server
>   endpoint `POST /api/v1/auth/employee-disconnect` (handler + `EmployeeDisconnectRequest` DTO +
>   route) was deleted. The web admin `POST /api/v1/auth/logout` (httpOnly-cookie flow) is
>   unrelated and unchanged. Employees can no longer disconnect themselves; the client tracks until
>   the process stops. Contract table updated below.
> - 2026-08-10: **Client GUI rebuilt as six pages + runtime branding pipeline.** The Avalonia UI was a
>   single `MainWindow.axaml` monolith (login → wizard → profile, dark theme). It is now a **router**
>   plus six `UserControl` pages under `client/Views/Pages/` — Splash, Login, PermissionSetup, Dashboard,
>   SystemSpecs, InstalledApps — behind a 246px nav rail. Three page ViewModels
>   (`DashboardViewModel`, `SystemSpecsViewModel`, `InstalledAppsViewModel`) joined `MainViewModel` in DI
>   as Transient; `Styles/AppTheme.xaml` became a **light design-token dictionary** (palette, rail tokens,
>   badge surfaces, 4 shadows, 5 radii, 9 vector icon geometries) and `App.axaml` carries ~30 style
>   classes + 5 animations. Two previously-unsurfaced data sets got real screens: **System Specs** (CPU,
>   RAM, GPU, storage, network, hot-plugged peripherals) and **Installed Applications** (the
>   `installed_applications` + `installed_packages` inventory, searchable, virtualized for
>   multi-thousand-row lists). New `client/Core/AppInfo.cs` makes **every visible brand string and version
>   derive from `client/APP_IDENTIFIERS` + `client/VERSION` at runtime** — `APP_IDENTIFIERS` is embedded
>   into `client.dll` as `client.APP_IDENTIFIERS` and parsed with a strict regex (read as data, never
>   executed); `VERSION` flows through `InformationalVersion` and is read back with any `+sha` stripped.
>   Change either file, rebuild, and the rail, window title, splash, footer, tray tooltip and installer
>   filenames all follow — see §6 → *Branding-Single-Source Rule*. No installer-script change was needed:
>   the embedded resource and the `Assets/**` glob both ship inside `client.dll`. New docs:
>   [FILE_HIERARCHY.md](./FILE_HIERARCHY.md), [WORKFLOW.md](./WORKFLOW.md),
>   [client/UI_ARCHITECTURE.md](./client/UI_ARCHITECTURE.md).
> - 2026-08-08 (round 2): **Journey noise flood fixed — AppData churn + feedback loop.** Fresh-DB
>   test showed 374 junk rows appearing with NO user file ops: Chrome/Brave/Edge rewrite
>   `Local State`, `Cookies-journal`, `History-journal`, `Breadcrumbs`, `Network Persistent
>   State`, `Cache_Data\f_*` via tmp+rename every few seconds, and a recursive journey watcher
>   had been attached to `C:\Users\pc005\AppData\Local` (browsed while checking the DB). Fixes:
>   (1) `FileSystemEventWatcher` excludes the Windows app-data/package trees structurally —
>   `appdata/`, `programdata/`, `program files/` (slash-normalized, no product names) — plus
>   the user-profile root and drive roots are never watched recursively. (2) `EnsureWatching`
>   now requires a real existing DIRECTORY, returns a success bool, and only logs on success —
>   extensionless cache FILES ("Local State") can no longer be mistaken for folders and spawn
>   watchers. (3) `EventCoordinator.InferObjectType` on Windows: extensionless paths that no
>   longer exist classify as **File** (browser config stores), not Folder — killed the
>   misclassification that fed the loop. (4) `DesktopEventService` calls `EnsureWatching` ONLY
>   for `navigate` actions, never for file create/rename/delete/modify — file-op events can no
>   longer spawn new watchers (the feedback loop). Verified: 90s idle with zero file ops → 13
>   rows, all legitimate (4 Explorer navigations for actually-open windows + 5 browser tabs),
>   0 cache-churn rows; journey watchers only on real browsed folders (`C:\tetsting`).
> - 2026-08-08: **Windows file-explorer journey — Explorer navigation + journey-driven watching.**
>   The journey pipeline was Linux-shaped: `ATSPIEventWatcher` (D-Bus AT-SPI) is Linux-only, so on
>   Windows nothing reported which folder the user was browsing, and `FileSystemEventWatcher` only
>   covered the 6 fixed user folders — create/rename/delete in any other folder (e.g. `C:\project`)
>   was invisible, and `RecentFilesWatcher` watched the Linux `recently-used.xbel` path. Fixes:
>   (1) NEW `WindowsExplorerWatcher` — polls the shell's OWN registry via Shell COM
>   (`Shell.Application → Windows()`), reads each Explorer window's `file://` `LocationURL` as its
>   exact browsed folder, and emits `navigate`/`close` events per window (verified live: a window on
>   `C:\tetsting` exposed `file:///C:/tetsting`). No UIA tree walking, no product-name lists.
>   (2) `FileSystemEventWatcher.EnsureWatching(folder)` — journey-driven watching: the folder the
>   user navigates to gets a recursive watcher even when it is outside the fixed 6 (bounded set of
>   24, pruned after 15min idle; drive roots and `C:\Windows` tree excluded). (3) `EventCoordinator`
>   now attributes raw `filesystem` events (which carry no window identity) to the Explorer window
>   browsing the containing folder via `IExplorerWindowProvider.TryGetWindowForPath` — the file op
>   joins the SAME journey (same WindowId key) as the navigation. (4) `RecentFilesWatcher` Windows
>   source: watches `%APPDATA%\Microsoft\Windows\Recent\*.lnk` and resolves each shortcut's target
>   via WScript.Shell COM (the OS's own LNK reader) → `open` events. Windows shell `explorer` is a
>   platform constant (like `C:\Windows`), mapped to "File Explorer" for display. Verified:
>   `dotnet build` 0/0, client healthy, create/rename/delete now stored with `WindowId` matching the
>   browsing Explorer window.
> - 2026-08-08 (round 5): **DE-HARDCODED Windows software detection — pure OS metadata, zero product-name lists.**
>   The user's rule (now §6 *No-Hardcoded-Names Rule*): never hardcode software names for detection — every employee PC
>   has a different OS (Win 7/10/11, Ubuntu versions), department (dev/SEO/marketing/IT) and toolset, so name lists
>   silently break detection everywhere except the machine that generated them. All Windows app-vs-package decisions are
>   now OS metadata: (1) **PE Subsystem** — the OS's own statement of GUI (IMAGE_SUBSYSTEM_WINDOWS_GUI=2) vs console/CLI
>   (CUI=3); new `client/Core/ExecutableMetadata.cs` reads it for the Start Menu scan (Node.js/Git Bash/Command
>   Prompt/Python shortcuts are dropped as CUI targets — the exact .desktop analog), the registry scans (DisplayIcon/
>   InstallLocation exes decide app vs package), the classifier, and the runtime session gate; (2) **C:\Windows tree** —
>   anything under the Windows dir (System32/SystemApps/WinSxS) is an OS component (RuntimeBroker, GameBar, Video.UI,
>   conhost…); `LogCollectorService` resolves the running exe once per process name (5-min memoized) and rejects it —
>   the four Windows name blocklists (WindowsNonAppProcesses/Suffixes/Prefixes/WindowsCliOrRuntimeBinaries, ~120
>   names) are DELETED; (3) **Driver Store** — drivers never appear in the Uninstall registry or package managers; new
>   `PackageDetector.ScanDriverStore` reads
>   `HKLM\SYSTEM\CurrentControlSet\Control\Class\{class-GUID}\<instance>` — the data Device Manager shows
>   (DriverDesc/ProviderName/DriverVersion, e.g. "Realtek Audio"/"Realtek Semiconductor Corp."/6.0.9175.1), works
>   Win 7→11, replaces the Realtek/NVIDIA/Intel/AMD name blocks; (4) **winget is the source of truth** —
>   `IsWingetFrameworkRow` deleted, so `.NET SDK`, VC++ redists and runtimes land in installed_packages like dpkg
>   lists every apt package (the classifier dedups GUI-app collisions); (5) **registry flags only** —
>   `JunkRegistryNamePatterns`/`IsDevToolRegistryName`/`ClassifyRegistryPackage` (~60 names) deleted;
>   `IsSystemComponentEntry`/`IsSystemOrUpdateRow` use SystemComponent/ParentKeyName/ReleaseType + Windows Update
>   conventions (KB, Security Update, LocalServiceComponents, * Uninstaller); entries without a GUI exe are apps only
>   when the OS says so (URL associations or a matching Start Menu shortcut), otherwise packages. Installer
>   bootstrappers (C:\ProgramData\Package Cache), uninstallers (unins*/uninstall*) and *setup* executables are
>   structurally excluded from BOTH tables (they are installers, not software); (6) `CliKnownPackages` (~60 names)
>   deleted — `IsKnownPackage` was dead code (no consumers). **Critical bug found while validating:** the first PE
>   reader read the optional-header magic at peOffset+4 — that is the COFF `Machine` field (0x8664 = AMD64), so EVERY
>   x64 exe failed the magic check and read as subsystem 0 ("unknown") — the magic lives at peOffset+24 after the
>   20-byte COFF header; the Subsystem field is at optional-header offset 68 in BOTH PE32 and PE32+. Answer to "why
>   is dotnet missing": `.NET SDK 10.0.302` IS in installed_packages (winget); the `.NET Runtime` rows are
>   SystemComponent=1 internal churn, correctly skipped. Honest metadata outcomes documented: Git Bash/git-gui stay
>   apps (git-bash.exe is a GUI-subsystem mintty terminal, like gnome-terminal on Linux), GNS3 lands in packages on
>   Windows (its gns3.exe is a genuine console launcher stub — subsystem 3 — verified byte-level). Allowed exceptions
>   (documented in §6): OS-shell constructs only — Linux GNOME daemon prefixes, Windows shell display names
>   (explorer→File Explorer), the KB-prefix update convention, and unins*/uninstall/*setup installer naming.
>   Verified live: build 0/0, full re-scan: 76 apps (VS Code one clean row, browsers, Office, WPS, WinRAR, Wireshark…),
>   55 packages (Node.js, .NET SDK, Python, Realtek, freebuff@0.0.142, drivers…), zero bootstrapper rows, zero
>   RuntimeBroker/GameBar/Video.UI leaks.
> - 2026-08-08 (round 4): **Windows packages now include MSI-installed dev tools + drivers.** Root-caused from the live
>   DB: Node.js (v24.19.0) was missing entirely, and Realtek audio components vanished too. The registry dev-tool
>   filter correctly dropped them from installed_applications (not GUI), but NO source ever added them to
>   installed_packages — winget/npm/pip only see package-manager installs, and MSI installers exist ONLY in the
>   registry Uninstall keys (the Windows analog of dpkg listing libs/firmware). New `PackageDetector.ScanRegistrySoftware`
>   reads all 3 Uninstall nodes (HKLM + WOW6432Node + HKCU) and captures: dev runtimes/tools (Node.js, Eclipse Temurin
>   JDK, .NET SDK, OpenSSL, Npcap, plus Git/Go/Python/PostgreSQL/Redis/Nmap with `IsPackageAlreadyKnown` fuzzy dedup
>   so nothing duplicates winget) → category runtime/tool/library, and driver/system components (Realtek Audio COM
>   Components, Realtek High Definition Audio Driver, NVIDIA/Intel/AMD patterns) → category `driver`
>   (`SoftwareCategoryResolver.ResolveForPackage` now preserves the driver category — it previously fell through to
>   `tool`). SystemComponent=1 rows (the .NET/VC++/Python sub-component churn) and GUI apps are excluded; names are
>   cleaned ("Eclipse Temurin JDK with Hotspot 17.0.19+10 (x64)" → "Eclipse Temurin JDK"). Verified live: 7 new
>   installer packages, zero duplicates, Video.UI leak fixed (`WindowsNonAppProcesses` had `videoui` but the real
>   process is `Video.UI` — dotted name never matched). Build 0/0.
> - 2026-08-08 (round 3): **Windows inventory accuracy — Start Menu .lnk = the .desktop analog.** Linux decides
>   GUI-vs-package by `.desktop` presence; Windows had no equivalent, so registry DisplayNames came through raw
>   ("Microsoft Visual Studio Code (User)" while the binary is Code) and global CLIs were invisible. Four fixes landed
>   together, verified live on a Windows 10 machine: (1) `InstalledAppDetector.ScanStartMenuShortcuts` enumerates Start
>   Menu .lnk files (user + common) via WScript.Shell (base64 `-EncodedCommand` PowerShell — no quoting bugs), resolves
>   each target exe → clean display name + binary map (`Visual Studio Code.lnk` → Code.exe) — the exact Windows analog
>   of `Exec=` in a `.desktop`; AppX/URL/folder targets and junk names (Uninstall/Getting Started/…) are skipped.
>   (2) Registry scan now prefers the shortcut name over the registry DisplayName and merges registry metadata
>   (version/publisher) into the SAME row (the table is keyed on `app_name`), so "Microsoft Visual Studio Code (User)"
>   dedups to "Visual Studio Code"; `CleanRegistryDisplayName` strips (User)/(64-bit) noise; junk filter extended
>   (LocalServiceComponents, * Uninstaller). (3) New `SoftwareClassifier.CollapseWindowsDuplicateApps` pre-pass:
>   same-binary rows collapse to the shortest clean name, registry-only stragglers match their shortcut by
>   version-stripped name ("Advanced IP Scanner 2.5.1" → "Advanced IP Scanner"), and CLI/runtime launchers (node,
>   git-bash, git-cmd, git-gui, python, pythonw, cmd, pg_ctl, wsl…) are dropped from apps — they belong in
>   installed_packages, exactly like Linux tools without a .desktop. (4) `PackageDetector.BuildCliStartInfo` — npm/scoop
>   are `.cmd` batch shims that `Process.Start`/CreateProcess can't launch, so `ScanNpmGlobal`/`ScanScoop` died silently
>   in the catch and global npm tools never appeared (freebuff@0.0.142, opencode-ai@1.18.15 missing). Now invoked via
>   `cmd.exe /c` on Windows. Also: the empty-`Categories` GUI gate in `LogCollectorService.ResolveAppInfoInner` is now
>   Linux-only (Windows registry/lnk apps only get Categories for browsers, so the gate wrongly rejected every
>   non-browser). Verified live: 81 clean apps (VS Code one row, binary Code, v1.132.0 + publisher), 13 packages
>   (freebuff npm, Git, Go, Nmap, PostgreSQL, Python, Redis, Ubuntu, WSL, pip — zero GUI apps), zero system-component
>   leaks. Note: old polluted installed_* rows linger until wiped — the scan upserts by app_name but never deletes
>   stale rows (wipe keeping employee login rebuilds clean, as on Linux).
> - 2026-08-08 (round 2): **Fresh-Windows-DB "missing tables" + hardware_devices fix.** A brand-new Windows DB showed empty
>   installed_* tables for the first minutes because the app/package scan only ran every 30 collection cycles (~15 min)
>   — the periodic cadence is invisible on long-lived Linux DBs. `LogCollectorService.ExecuteAsync` now also runs
>   `CollectInstalledApplicationsAsync` immediately at startup. `hardware_devices` stayed at 0 on Windows because the
>   PnP probe used `Win32_PnPEntity`, which has NO `Class` property (only ClassGuid) — the class filter silently matched
>   nothing. Switched to `Get-PnpDevice -PresentOnly` (real Class values), dropped `PrintQueue` (virtual printer queues
>   like "Microsoft Print to PDF"/". AnyDesk Printer"), required USBSTOR/USB InstanceIds for DiskDrive and USB for
>   NetworkAdapter (internal SATA/NVMe/NIC excluded), and verified live: 7 clean peripherals (keyboard, mouse, headset,
>   monitor, USB composite) recorded on the test machine. Blocklist extended for the remaining WindowsApps components
>   (GameBarFTServer, olk, GameBar, gamingservices, xboxstatsserver…) and Git-Bash/Windows CLI tools (tail, grep, sed,
>   ipconfig, tasklist…) so they never auto-register as applications.
> - 2026-08-08: **Windows installed-software + hardware detection overhaul (root-caused from the live Windows DB).**
>   `installed_packages` was flooded with GUI apps (77/78 rows from `winget list`) and `installed_applications`
>   with runtimes/system components (node, dotnet, RuntimeBroker, tray helpers) — both fixed:
>   (1) `PackageDetector.ScanWinget` rewritten — the space-padded console table is now sliced by column start
>   offsets read from the header (multi-word names like "Google Chrome" are no longer shredded), ARP\/MSIX rows
>   (the actual GUI apps) and MS Store apps are dropped, .NET/VC++ framework rows are filtered, and the version
>   column is sliced against `Available`. `SoftwareClassifier` gained fuzzy name suppression ("Microsoft Visual
>   Studio Code (User)" vs registry "Visual Studio Code", ≥4 chars both sides so Git/Go are never over-suppressed);
>   (2) `InstalledAppDetector.DetectInstalledWindows` now scans ALL three registry Uninstall nodes
>   (HKLM, WOW6432Node, HKCU — the old code read only HKLM, which is why only 3 apps were ever found) with a
>   junk filter (SystemComponent, updates, runtimes/redists/drivers, dev tools Git/Go/Python/Node/PostgreSQL→
>   packages) and an explorer→"File Explorer" display-name override; (3) `LogCollectorService` gained a Windows
>   non-app blocklist (RuntimeBroker, tray/helper/updater suffixes, WindowsApps components), a CLI-runtime skip
>   set (node/dotnet/git/go/python… never auto-register as apps), auto-detected browsers now get IsBrowser+
>   WebBrowser categories, real RAM via `GlobalMemoryStatusEx` P/Invoke (was a hardcoded fake) and GPU via
>   PowerShell `Win32_VideoController` (was always empty on Windows); (4) `HardwareDeviceWatcherService` is no
>   longer Linux-only — new Windows PnP implementation polls `Get-CimInstance Win32_PnPEntity` every 30s,
>   keys open rows by `PNPDeviceID`, closes rows when a device disappears, and maps PnP classes
>   (DiskDrive→storage, KeyBoard/Mouse/HID→input, Camera/Monitor→display, Media/AudioEndpoint→audio,
>   Bluetooth/USB→usb). Verified: `dotnet build` 0/0 and the new winget parser was validated against the real
>   machine output (125 of 153 rows correctly skipped).
> - 2026-08-08: **Windows journey fixes — real incognito flag + junk-data sweep.** (1) **Incognito was mis-detected on Windows:** Chrome/Edge never put "incognito" in the window TITLE (the title heuristic only ever matched Firefox's "Private Browsing"), so incognito journeys were stored with `"incognito":false`. The `WindowsUiaBrowserReader` now also scans the window's accessibility tree for the dedicated **"Incognito"/"InPrivate" toggle button** Chrome and Edge expose (verified live via UIA) — the flag is now truthful and the URL is still captured (incognito capture is on). (2) **Junk-session root cause fixed:** `SearchApp` was treated as a BROWSER because the `"arc"` hint **substring-matched** `SearchApp` — `IsBrowserProcess` now matches hints as STANDALONE WORDS only (`arc` no longer matches `SearchApp`; `edge` no longer matches `msedge`-unrelated names). (3) **Runtime auto-registration disabled on Windows** — it was the factory for every junk `installed_applications` row (svchost → "Windows Services", dllhost → "Windows DCOM Host", conhost, Video.UI, SearchApp, M365Copilot…): `ResolveAppInfoInner` now refuses rows with an **empty `desktop_id`** (real inventory rows always carry a Start Menu shortcut / registry key / .desktop id) and the Windows structural gate (C:\Windows tree + PE CUI subsystem) now runs BEFORE the fuzzy matcher (powershell.exe was fuzzy-matching the "Windows PowerShell ISE" row → 7 junk console sessions). New boot sweep `CleanupWindowsJunkSessionsAsync` deletes empty-desktop_id app rows and closes their sessions. Tracking is now strictly driven by the `installed_applications` inventory.
> - 2026-08-07: **Analog audio device tracking** — `HardwareDeviceWatcherService` now monitors
>   headset/headphone analog audio jacks via `amixer` polling in addition to USB devices. Detects
>   plug/unplug events for 3.5mm audio jacks and stores them as `audio`/`headphone` class devices
>   in `hardware_devices` table. Fixes issue where headsets/earphones connected via analog jack
>   were ignored. Linux-only (requires `amixer`/ALSA).
> - 2026-08-07: **Private/incognito window URLs captured — Firefox via AT-SPI `DocURL`, Chrome needs one launch flag.**
>   Live-DB root-causing showed private journeys rotated only when the profile-History fallback happened to match a URL;
>   when it didn't (private visits, direct-opened sites) rotation froze and URLs stayed empty. Three fixes landed together:
>   (1) `AccessibilityBrowserTracker` now rotates on title change after a bounded ~25s historyLag timeout even with no URL
>   (was: held forever waiting for history) — same-tab navigation in private windows always creates the next record;
>   (2) `LogCollectorService` main loop no longer closes browser sessions owned by the accessibility tracker — it hydrated
>   ALL open sessions but only browser processes were in its current-key set, so browser sessions were silently closed
>   4-40s after opening while the windows stayed up; browser-owned sessions are now excluded from the close phase
>   (verified: sessions stay open 60s+); (3) URL sources for private windows: **Firefox builds its full AT-SPI tree on
>   demand**, so `LinuxAtSpiBrowserReader` now reads the `DOCUMENT_WEB` (role 95) node's `DocURL` attribute — the EXACT
>   private-window URL (verified live: pakprivatehire + wikipedia rows with `metadata_json
>   {"source":"accessibility","incognito":true,"processName":"firefox",...}`); normal Firefox gets exact URLs from
>   this source too (no more history false-positives). **Chrome 136+ is the hard case:** verified on Chrome 151 that a
>   running instance's a11y tree has ZERO children below the window frame (basic mode only), and BOTH runtime wake-up
>   signals (org.a11y.Bus `ScreenReaderEnabled` PropertiesChanged + legacy signal) are ignored — the tree is only built
>   when Chrome is launched with `--force-renderer-accessibility` (process-wide). With the flag, the omnibox ENTRY node
>   (`"Address and search bar"`, role 79) exposes the exact URL for EVERY window — including incognito windows opened
>   normally via Ctrl+Shift+N (verified: `https://en.wikipedia.org/wiki/Incognito` with `"incognito":true`).
>   Normal-mode Chrome tabs also fall back to the profile History DB; incognito writes nothing to disk so the flag is the
>   only source. Applied a user-level `~/.local/share/applications/google-chrome.desktop` with
>   `--force-renderer-accessibility` on ALL Exec lines (main + NewWindow + NewIncognitoWindow actions); Chrome must be
>   relaunched once for it to take effect. Also fixed the installed `--background` systemd service crash on Wayland:
>   `Program.cs` no longer initializes the Avalonia/X11 UI in background mode (the unit hardcodes a stale
>   `XAUTHORITY=~/.Xauthority`; the real Xwayland auth is `/run/user/<uid>/.mutter-Xwaylandauth.*`), so the installed
>   service stays active instead of dying at startup. Verified end-to-end in the INSTALLED build: service active, Chrome
>   incognito journey (title + exact URL + incognito flag) in `app_items`, Firefox private journeys intact.
> - 2026-08-07: **Killed the `dpkg-query: no packages found matching ${Version}` startup noise** — the apt package scan's
>   `-f` format string contained literal TAB characters, and .NET's Unix argument parser splits argv on tabs too, so
>   `${Version}`/`${Maintainer}`/`${Section}` reached dpkg-query as package-name PATTERNS (3 stderr errors per scan, 6
>   per startup). The format is now double-quoted so it arrives as ONE argument with `\t`/`\n` escapes for dpkg-query to
>   interpret, and stderr is drained. Verified: `dpkg-query -W -f='${Package}\t...'` returns proper rows and the log is
>   clean.
> - 2026-08-07: **Quiet terminal: per-event browser-journey logs demoted to Debug** — `AccessibilityBrowserTracker`
>   emitted "Browser journey opened / window closed / idle-closed / download recorded" at Information level, flooding the
>   terminal during `dotnet run` (the DB is the source of truth for journeys). All four are now LogDebug (visible only
>   with `ALPHA_LOG_LEVEL=debug`); the startup banner stays at Information.
> - 2026-08-06: **snap Firefox private-window capture — surgical AppArmor AT-SPI fix.** Why private
>   Firefox was invisible even after the journey overhaul: Ubuntu's snap Firefox AppArmor profile
>   denies EVERY inbound D-Bus call from outside the sandbox — including the AT-SPI accessibility bus
>   (`AppArmor policy prevents this sender... label="snap.firefox.firefox (enforce)"` verified live).
>   And Firefox itself never writes private windows to sessionstore or places.sqlite, so no disk source
>   exists. New `publish/firefox-a11y-apparmor.sh` (idempotent, `--undo` supported) loads a *surgical*
>   copy of the snap profile adding ONE rule — `dbus (receive)` — that lets the AT-SPI bridge call INTO
>   Firefox while the sandbox stays fully enforcing (verified `/sys/kernel/security/apparmor/profiles`
>   shows `(enforce)`, NOT complain; peer-agnostic because the tracker's sender label can be
>   `unconfined` OR `vscode (unconfined)` depending on how it's spawned). Installs a systemd oneshot
>   (`alpha-ai-firefox-a11y.service`, enabled) that re-applies the override at boot and after
>   `snap refresh firefox` (snapd regenerates the base profile). Firefox must be restarted once after
>   applying (AppArmor mode is fixed at process exec). Verified end-to-end on this machine: the tracker
>   now opens `PRIVATE — Mozilla Firefox Private Browsing` with `metadata_json {"source":"accessibility",
>   "incognito":true,"processName":"firefox",...}` stored in `app_items` — private-window presence,
>   title, and flag captured; the URL is still not exposed by Firefox itself on Linux (no location-bar
>   node in the a11y tree, no sessionstore entry) — an extension would be the only source, which the
>   project has rejected. Note: deb/non-snap Firefox needs no override (already AT-SPI reachable).
> - 2026-08-06: **Browser-journey overhaul — all browsers incl. Firefox + incognito, duplication eliminated.**
>   Root causes found in the live DB (52x duplicate `browser_tab` rows, 316 items written into already-
>   closed sessions, sessions opening/closing every 5-13s): (1) window identity was the AT-SPI registry
>   path, which churns on navigation, and the PID-based re-key **stole** one window's session for another
>   when 2+ windows shared one PID (Chrome/Edge/Firefox all do); (2) snap Firefox is AppArmor-blocked from
>   AT-SPI entirely, so it was never captured. Fixes: `LinuxAtSpiBrowserReader` rewritten to merge THREE
>   sources — AT-SPI (Chrome & co.), **Firefox sessionstore** (`recovery.jsonlz4`/`sessionstore.jsonlz4`,
>   decompressed by an embedded pure-python LZ4 block decoder — no external module, no installer asset;
>   gives exact per-tab URLs that survive the snap sandbox), and the **WM window list** (xprop + GNOME Shell
>   introspect) for stable window ids. `AccessibilityBrowserTracker.ResolveWindowKey` now re-keys by
>   page-title match (multi-window) or single-window count guard (macOS/wayland navigation) — stealing is
>   impossible; missing-window grace raised 3→5 polls. `ALPHA_BROWSER_CAPTURE_INCOGNITO=true` (was false).
>   Main `LogCollectorService` skips browsers when browser tracking is enabled (no more double sessions per
>   window; hints-based skip covers the post-wipe window before the catalog scan). DB wiped keeping login.
>   Verified live: 3 Chrome windows (incognito flagged) + 2 snap-Firefox windows with exact URLs.
> - 2026-08-06: **Hybrid URL fallback — browser profile History reader (no restart, all browsers).**
>   Chrome 136+ (verified on Chrome 151) ignores every AT-SPI enablement switch on Linux, so a11y
>   gave titles but empty URLs unless the browser was relaunched with `--force-renderer-accessibility`
>   (restart = rejected). NEW `Core/BrowserAccessibility/BrowserHistoryReader.cs` reads each browser's
>   own profile history database — Chromium-family `History` (Chrome/Edge/Brave/Opera/Vivaldi/
>   Chromium) and Firefox `places.sqlite` — WHILE the browser runs: DB + `-wal`/`-shm`/`-journal`
>   sidecars copied to a temp dir and opened read-only (torn snapshots can't corrupt anything;
>   failures are swallowed and retried next poll), change signatures throttle re-reads, and URL
>   resolution is STRICTLY title-match (no unconditional newest-visit fallback — multi-window setups
>   never get a misattributed URL). Generic scans of `~/.config` and `~/.mozilla` (plus per-platform
>   roots on Windows/macOS) catch brand-new browsers — install→use→uninstall-in-5-min journeys are
>   captured before the uninstaller deletes the diary. Verified live on this machine: running Chrome
>   151 (`Web analytics - Wikipedia` → exact URL), snap Firefox (AppArmor blocks D-Bus but NOT file
>   reads → URL recovered from `places.sqlite`), a fresh brand-new profile, and a full tracker run
>   (DB rows show title + full URL + domain with `metadata_json.source="history"`).
>   `AccessibilityBrowserTracker.EnrichUrlsFromHistoryAsync` fills empty URLs before the tab logic;
>   URL-less title changes no longer rotate tabs until history catches up (kills the spurious
>   empty-URL rotation). New `AccessibilitySnapshot.UrlSource` (`a11y`|`history`), shared
>   `StripBrowserSuffix` helper, config knobs `ALPHA_BROWSER_HISTORY_ENABLED` (default true) +
>   `ALPHA_BROWSER_HISTORY_POLL_SECONDS` (default 10); `.env.example` + `--print-config` updated.
>   Pure code — no installer asset changes (config flows through the existing `config.enc` pipeline).
> - 2026-08-06: **Per-page browser_tab records (no more title overwrite)** — `AccessibilityBrowserTracker`
>   used to UPDATE the single `browser_tab` root item in place on every navigation (ON CONFLICT DO
>   UPDATE replaces `title`), so "YouTube - Google Chrome" silently became the video page. Now each
>   page visit CLOSES the previous tab root — via a dedicated `CloseAppItemAsync` that also resets
>   `is_synced=0` so the server learns the close even for already-synced rows — and OPENS a fresh
>   `browser_tab` record; `browser_navigation` children still record transitions. Title-only changes
>   rotate only after a ~10s stability window (badge/timer flicker is filtered); real URL changes
>   rotate immediately. The Linux/Chrome empty-URL gap is RESOLVED below (2026-08-06 history-reader
>   fallback fills URLs from the browser's own profile History DB while it runs — no restart).
> - 2026-08-06: **Crash-loop fix: duplicate-PID open sessions in `SessionHierarchyResolver`** — the
>   accessibility browser tracker writes one `app_sessions` per browser window (ProcessId = browser main
>   pid) while the process collector writes its own session for the same browser process, so two open
>   sessions can share one `process_id`. The resolver's `existingOpen.ToDictionary(r => r.ProcessId)`
>   threw `ArgumentException` on every collection cycle and killed the whole tracking loop whenever a
>   browser was open. Now dedupes per PID (keeps the earliest record, logs a warning).
> - 2026-08-06: **Installer config refresh fix (stale `config.enc` shadowing)** — installed builds load
>   `~/.config/alpha-ai-tracker/config.enc` (the machine-key copy) BEFORE the freshly baked `config.enc`
>   next to the binary, and previously never refreshed it — so rebuilding the installer with a changed
>   `.env` (e.g. a new server IP) had no effect on machines that already ran once. `dotnet run` always
>   looked fine because it reads the plaintext `.env`. `EnvLoader.Load()` now compares the DECRYPTED
>   content of the shipped copy vs the user copy and replaces the user copy when they differ (then
>   re-migrates to the machine key); corrupt user copies self-heal from the shipped copy; non-writable
>   user dirs load the shipped copy directly. Added `--print-config` headless CLI to print which config
>   an installed build resolves (e.g. `client/publish/linux/client --print-config`).
> - 2026-08-05: **Option B — accessibility-tree browser journey (debugger pipeline DELETED).**
>   Chrome 136+ real-profile debugging is closed by every mechanism (verified on this machine:
>   same-path `--user-data-dir` fails headless + GUI; `RemoteDebuggingAllowed`/`DevToolsAvailability`
>   policy fails too). With extensions ruled out, the entire debugger pipeline (`client/Core/Browser/*`
>   — 16 files) was REMOVED and replaced by `client/Core/BrowserAccessibility/*`: the tracker reads the
>   OS accessibility tree — the same tree screen readers use — so the employee's REAL browser journey
>   is captured on every platform and every Chrome version, with no debugger, no extension, and no
>   catalog dependency (install→use→uninstall-in-5-min browsers are captured; headless instances are
>   skipped). Linux: python3+AT-SPI D-Bus probe (validated live on Chrome 151 — the omnibox ENTRY node
>   `"Address and search bar"` exposes the exact URL/query; Name/Role are D-Bus properties, text via
>   `org.a11y.atspi.Text`). Windows: UIA via `Interop.UIAutomationClient` (netstandard2.0, API verified
>   by reflection). macOS: osascript/System Events (best-effort; Accessibility grant).
>   `AccessibilityBrowserTracker` polls every `ALPHA_BROWSER_ACCESSIBILITY_POLL_SECONDS` (3), writes one
>   `app_sessions` per window + `browser_tab`/`browser_navigation` `app_items` (exact URL + domain),
>   closes vanished windows after 3 polls, idle-closes after `ALPHA_BROWSER_JOURNEY_IDLE_MINUTES` (15),
>   closes gracefully on shutdown (no orphans), records Downloads as `browser_download` items, and gates
>   incognito URLs behind `ALPHA_BROWSER_CAPTURE_INCOGNITO=false` (legal-safe default; incognito windows
>   are still detected + flagged in metadata). Dead code removed: shell-command collectors (interface +
>   3 impls + `ShellCommand` model), server dead DTOs (`BrowserContext*`, `FileExplorerContext*`, `Url*`,
>   `UrlVisit*`, `ShellCommand*`), `publish/install-extensions.sh` + the `extensions/` bundling step.
>   Fixed the `SqliteLogStore` fuzzy-match AND/OR precedence bug (a junk `chrome` row beat `Google Chrome`;
>   now prefers `is_browser` rows + shortest binary). Env knobs are now a11y-only (`.env` + `.env.example`
>   updated per installer parity). Verified: `dotnet build` 0 errors / 27 warnings, `go build`/`go vet`
>   clean, and a live runtime test captured a real Chrome journey into SQLite (exact URL, graceful close).
> - 2026-08-03: **Installer-Parity rule + single-instance/tray UX fix** — codified a MANDATORY rule (see §6 below): every feature or modification must ALSO be wired into the installer functionality, and verified in an installed build — `dotnet run` alone is NOT a valid release test, because installed builds ship the publish output plus only what the `publish/*` scripts bundle (extensions/, publish/*.sh, config.enc) and run from a root-owned dir. Client fix behind the rule: a second launch now restores the running window (`client/SingleInstance.cs` named-event signaling), the tray menu gains **Quit**, and tray-less desktops (no StatusNotifierWatcher — `client/Services/TrayAvailability.cs` D-Bus check) quit on window-close instead of stranding an invisible process.
> - 2026-08-01: **Docs audit (AGENTS.md + client/server ARCHITECTURE.md)** — removed all stale `activity-logs/sync` and `shell-commands/sync` references (both endpoints/tables/code are deleted from the product). Fixed the mermaid diagram + cross-service contracts to show the real 7 sync endpoints. Server: migration inventory corrected to 001–016 (15 files), added `jobs/staleness_sweep.go`, `employee_installed_applications`/`employee_installed_packages` junctions, and all 12 Postgres tables; API surface now lists `GET /app-sessions` + `GET /app-items` and drops the removed `activity-logs` section. Client: corrected SQLite schema (11 tables incl. `storage_devices`, `permission_status`, `app_items`), documented the `MigrateSql` migration strategy, the real sync batch size (500), the file logger (`dotnetrunlog.txt`), `install-extensions.sh`, and flagged `IShellCommandCollector`/`ShellCommand` as dead code. Branch updated to `restructureClient`.
> - 2026-07-31: **Cross-service payload + employee↔catalog dedup (migrations 013–016)** — tracking tables were washed out (backup at `server/bin/backup/alpha_ai_tracker_20260731_134750.dump`) and server + client landed together. 013 adds `installed_app_id`/`installed_package_id`/`grouped_by`/`cgroup_scope`/`context_label` to `app_sessions`; 014 adds `process_id` + 9 journey fields to `app_items`; 015/016 add company-global app/package catalogs (`app_fingerprint=desktop_id|binary_name`, `package_fingerprint=package_name|source_manager`) with junction tables `employee_installed_applications`/`employee_installed_packages` (per-install version/path/date + `first_seen_at`/`last_seen_at`/`is_active`; FKs → `employees(employee_id)` VARCHAR per convention). `SyncInstalledApps`/`SyncInstalledPackages` rewritten to upsert-catalog-then-link in one tx. Hourly `jobs/staleness_sweep.go` deactivates links idle > `LINK_STALE_DAYS=7`. Client: installed-apps mapper sends `binaryName`/`isBrowser`/`desktopId`/`categories`; app-sessions sends `groupedBy`/`cgroupScope`/`contextLabel`; app-items sends `processId` + 9 journey fields; apps conflict-update resets `is_synced=0`; packages dedup to `ON CONFLICT(package_name, source_manager)` + unique index (fixes 6,530 duplicate package rows from the old `ON CONFLICT(id)`). Smoke-tested: happy path, two-employee-same-app → 1 catalog + 2 links, malformed/partial payloads → 4xx/200 not 500.
> - 2026-07-31: **Atomic cascade-close of app_items with sessions** — the main collection loop closed sessions but never closed their `app_items` (11 orphaned open items on already-closed sessions in the live DB). New composite `CloseSessionsAndAppItemsAsync()` store method acquires the connection gate ONCE and closes sessions + items in ONE transaction (a crash mid-close can no longer leave orphans). Wired into all 4 close paths: main-loop normal close (was missing entirely — root cause of the orphans), `ReconcileStaleSessionsOnBootAsync`, garbage cleanup, and non-GUI cleanup. Per-tab closes in `NativeMessageService`/`JourneyEngine` still use `CloseAppItemsBySessionIdsAsync`.
> - 2026-07-31: **Terminal classification + PPID walk logging** — `TerminalEmulators` set expanded (`gnome-terminal-server`, `foot`) so flatpak/snap-packaged terminals resolve correctly without `Categories=` metadata. `SessionHierarchyResolver` now takes an optional `ILogger` and logs `ResolveParent` PPID walks at Debug level (wired from `LogCollectorService`).
> - 2026-07-30: **Software classification pipeline** — Added 3 new files: `SoftwareCategoryResolver.cs` (metadata-driven category resolution from .desktop Categories, macOS bundle IDs), `SoftwareClassifier.cs` (joint dedup pipeline — GUI apps from `InstalledAppDetector` win over matching package entries from `PackageDetector`), `SoftwareIdentityResolver.cs` (stable SHA-256 identity for cross-source dedup). Refactored `AppProcessClassifier.cs` — `FileManagerProcesses`/`IdeProcesses` renamed to `FileManagerFallbacks`/`IdeFallbacks`, added `ResolveCategory()` with metadata-first approach, added `ResolveRootItemType()` overload accepting `categories`/`desktopId` params. Upgraded `InstalledAppDetector.cs`: Linux .desktop scanning now follows `$XDG_DATA_DIRS` (snap/flatpak dirs), macOS bundle inspection returns `CFBundleIdentifier` + browser detection via `CFBundleURLSchemes`, browser detection for Linux via `Categories=WebBrowser`, Windows via `URLAssociations` http/https. Added server migration 012 (`desktop_id`, `categories`, `is_browser` columns on `installed_applications`).
> - 2026-07-30: **File logger for dotnet run** — Added `FileLoggerProvider.cs` (writes `ILogger` output to `dotnetrunlog.txt`) and registered it in `Program.cs`.
> - 2026-07-30: **GUI-apps-only tracking gate** — New tracking rule: only processes that resolve to `installed_applications` rows or are detected as GUI apps (have .desktop files on Linux, .app bundles on macOS, Start Menu/Program Files entries on Windows) are tracked. Removed shell-process-always-tracked, build-tool auto-registration, runtime auto-registration, and package fallback tracking from `ResolveAppInfoInner`. Added `IsGuiApplication()` to `IInstalledAppDetector`/`InstalledAppDetector` with `CheckGuiPath()` (cross-platform). Simplified main loop filter. Removed `_knownPackageNames`, `CloseStalePackageSessionsAsync`, `runningPackageIds`. Renamed `AutoDetectInstalledApp` → `AutoDetectInstalledGuiApp`. Historical data preserved as-is. 4 files changed.
> - 2026-07-30: **Fixed app_display_name resolving to raw GUID in NativeMessageService** — `ResolveBrowserAppIdAsync` cached and returned `app.Id` (GUID) which the caller stored as `AppDisplayName`. Fixed: `_browserAppCache` now stores `(id, displayName)` tuple; method renamed to `ResolveBrowserAppAsync` with `(string? id, string? displayName)` return; caller uses `browserDisplayName` for `AppDisplayName`.
> - 2026-07-30: **Chromium/Electron headless subprocess filter (cross-platform)** — Added `GetProcessCommandLine()` via PowerShell (Windows) and `ps -o command=` (macOS). Centralized `ChromiumSubprocessFlags` array + `IsHeadlessSubprocess()` in `AppProcessClassifier` for all 3 platforms. Linux renamed `IsChromeSubprocess()` → `ReadProcessCmdline()` using shared check. Startup cleanup: `CleanupGarbageSessionRowsAsync()` closes old rows with `--type=` flags or long process names.
> - 2026-07-30: **Dynamic browser suffix stripping** — Removed hardcoded `BrowserSuffixes` array (12 entries) from `ActivityContextParser`. Added `appDisplayName` parameter to `Parse()` → threaded to `ParseBrowserContext()`. Suffix stripped dynamically using the real app name from `installed_applications`. No generic regex fallback — only strips when `rootItemType == "browser_tab"`.
> - 2026-07-30: **SemaphoreSlim concurrency gate on SqliteLogStore** — Added `SemaphoreSlim(1,1)` guarding all public methods using `_connection`. Private ungated helpers (`SetStatusCoreAsync`, `GetEmployeeInfoCoreAsync`) avoid reentrancy deadlock from composite methods. Added `PRAGMA busy_timeout = 5000;`. Added `GatedTransaction` wrapper releasing gate on `DisposeAsync`. Fixed `BeginTransactionAsync` gate-leak edge case.
> - 2026-07-30: **FileSystemEventWatcher exclusion list + per-process resilience** — Added `ExcludedDirectoryPrefixes` (Waydroid, Flatpak, Snap, cache, trash, containers, Steam, per-platform temp/cache). Removed `UserProfile` from `WatchDirectories`. Added inner try/catch around each `ResolveAppInfo` call so one bad process can't abort the entire collection cycle.
> - 2026-07-29: **Fixed false positive "Connected" status in browser extension detection** — `IsExtensionActiveAsync()` was purely process-based (`pgrep -f native-host.py` + `pgrep ^chrome$`), producing false `ExtensionActive` when orphaned `native-host.py` processes or unrelated Chrome instances were running. Replaced with socket-level heartbeat: extension's background.js already sends `{action:"ping"}` every ~27s via `chrome.alarms`; `native-host.py` now forwards pings to the tracker socket (was swallowing them); `NativeMessageService` records `_lastHeartbeatAt` timestamp; `BrowserExtensionService.IsExtensionActiveAsync()` queries `NativeMessageService.IsExtensionConnected()` (60s threshold). Added `KillOrphanedNativeHostProcesses()` startup cleanup. `NativeMessageService` registered as singleton+hosted for DI injection into `BrowserExtensionService`. Three files changed: `native-host.py`, `NativeMessageService.cs`, `BrowserExtensionService.cs`, `Program.cs`.
> - 2026-07-29: **Fixed GNOME daemon contamination via Xwayland empty binary_name** — Xwayland `.desktop` file has no `Exec=` line, so `InstalledAppDetector` stored it with `binary_name=""`. `GetInstalledAppByBinaryNameFuzzyAsync()` SQL `$name LIKE '%%'` (empty binary) matched every process — causing all GNOME services + Chrome to resolve to the Xwayland entry. Fixed by: (1) `WHERE binary_name != ''` in fuzzy match SQL, (2) `NonAppProcesses` expanded with 16 GNOME daemons + prefix-matching array, (3) `KernelNamePrefixes` in `ProcessFilter.cs` for first-stage filter, (4) `NoDisplay=true` + `Type!=Application` gate in `AddAppFromDesktopFile`. DB cleaned: Xwayland entry patched with proper `binary_name`, orphaned sessions closed, Chrome display names restored.
> - 2026-07-29: **Added File Explorer journey tracking** — Full event-driven desktop event bus for file manager operations (Nautilus, Dolphin, Thunar, Nemo, etc.). Three watchers: `ATSPIEventWatcher` (Tmds.DBus.Protocol → AT-SPI `focus:`/`window:` events, detects foreground file manager via `xdotool` + `/proc/cwd`), `FileSystemEventWatcher` (FileSystemWatcher on 7 user directories), `RecentFilesWatcher` (XBEL monitor at `~/.local/share/recently-used.xbel`). `EventCoordinator` deduplicates (3s), correlates (500ms), normalizes raw→business events. `JourneyEngine` resolves `AppSession`, creates `AppItem` rows with 9 journey fields (`object_type`, `action`, `journey_id`, `sequence`, `previous_path`, `current_path`, `window_id`, `tab_id`, `metadata_json`). `IObservableEventSource` interface for all watchers — future IDE/terminal/Office integrations plug in the same way. Coexistence: `item_type` preserved, auto-derived from `object_type`+`action`; browser pipeline untouched. NuGet: `Tmds.DBus.Protocol` v0.94.2.
> - 2026-07-28: **Added browser extension journey tracking** — Chrome MV3 extension (`extensions/chrome/background.js`) captures real-time tab navigations (URL, title, tabId, windowId) via `chrome.tabs.onUpdated/onActivated/onCreated/onRemoved`. Sent through native messaging (`chrome.runtime.connectNative`) → `native-host.py` (Native Messaging stdio bridge) → `NativeMessageService` (Unix socket listener) → SQLite `app_items` as `browser_tab`/`browser_navigation` entries with `url`/`domain` fields.
> - 2026-07-28: **Added `NativeMessageService`** — `BackgroundService` listening on Unix domain socket (`~/.local/share/alpha-ai-tracker/native-messaging.sock`) for browser navigation events. Maintains `_tabSessionCache` mapping `browser:tabId` → `AppSession.Id`. Stores `browser_tab` root items + `browser_navigation` child items per navigation.
> - 2026-07-28: **Added `BrowserExtensionService`** — Detects installed browsers (Chrome, Chromium, Edge, Brave, Opera, Vivaldi, Firefox). Two-strategy extension installation: (1) `--load-extension` with `--no-first-run` for Chromium-based browsers, (2) profile injection via Python SHA256→extension-ID → `Preferences.json` edit as fallback for branded Chrome 150+. Async-safe with `Task.Delay` polling. Extension detection via NativeMessageService socket-level heartbeat (replaced process-based `pgrep` on 2026-07-29).
> - 2026-07-28: **Added `url`/`domain` columns to `app_items`** — client SQLite schema extended with `url TEXT` and `domain TEXT`. Server-side migrations and DTOs updated. NativeMessageService stores parsed URLs with proper domain extraction.
> - 2026-07-28: **Added `ComputeExtensionId` helper** — runs Python SHA256→a-p alphabet to compute Chrome extension ID from directory path. Used for native messaging manifest `allowed_origins`.
> - 2026-07-28: **Fixed `InstallNativeHostManuallyAsync`** — now computes and includes the extension ID in `allowed_origins` (was empty array, breaking native messaging).
> - 2026-07-28: **Fixed extension active detection** — replaced `IsExtensionConnectedAsync` (socket-based `fuser` which failed because native-host.py only holds ephemeral connections) with `IsExtensionActiveAsync` (process-based `pgrep native-host.py` + `pgrep chrome`). Reliable on all platforms. *(Note: replaced again 2026-07-29 with socket-level heartbeat for precision.)*
> - 2026-07-28: **Removed `--enable-automation` from Chrome launch args** — was triggering GCM errors (`QUOTA_EXCEEDED`, `DEPRECATED_ENDPOINT`) and "controlled by automation" banner. Unnecessary: `--load-extension` works without it on Chromium/Brave/Edge, and branded Chrome 150+ blocks it regardless.
> - 2026-07-28: **Added crash-safe session ended_at tracking** — heartbeat persisted every cycle (`last_heartbeat_at` in `app_status`), stale heartbeat detection on boot, and automatic reconciliation of orphaned sessions with the last heartbeat time as approximate crash time. Includes cross-platform `GetSystemUptime()` for diagnostic logging. Handles poweroff, process crash, and fast restart scenarios.
> - 2026-07-27: Full activity hierarchy engine — PID-persisted sessions, `SessionHierarchyResolver` (node→terminal→IDE), browser `browser_navigation` URL items, file manager `folder`/`file` path items, 30s context dedup cooldown.
> - 2026-07-27: Added `process_id` / `parent_process_id` to `app_sessions` (client SQLite + server migration 010).
> - 2026-07-27: Added `binary_name` to `installed_applications` model/SQLite table for process→display-name mapping.
> - 2026-07-27: Added `installed_app_id` / `installed_package_id` FK columns to `app_sessions` (client SQLite only).
> - 2026-07-27: Replaced in-memory `IsInstalledApp()` filter with SQLite-backed `ResolveAppInfo()` — processes not in DB get auto-detected via filesystem heuristics and saved before tracking.
> - 2026-07-27: Fixed Linux ProcessCollector `resolvedTitle ??= name` bug (was assigning fake titles to all processes, bypassing window-title filter).
> - 2026-07-27: Added process-tree-based parent-child tracking for terminal shells inside IDE/terminal-emulator sessions (via `ParentItemId` fixup pass).
> - 2026-07-27: Added `waydroid`/`gnome-software` to `NonAppProcesses` blocklist.
> - 2026-07-27: `AppDisplayName` now uses the real app name from `installed_applications.app_name` (e.g., "Visual Studio Code"), not the process name ("code") or window title.
> - 2026-07-27: Removed hardcoded `CommonKnownApps` list from `InstalledAppDetector` — app detection is now 100% dynamic from OS (.desktop files, registry, .app bundles).
> - 2026-07-27: **Added `BuildToolProcesses` set** — `make`, `go`, `npm`, `npx`, `cargo`, `pip`, `tsc`, `rustc`, etc. Auto-registered as `installed_packages` (category=`tool`) on first sight, tracked without window.
> - 2026-07-27: **Fixed process filter bug** — processes with `appId != null` or `IsBuildTool()` are now tracked even without a window title. Wayland-native apps (VSCode, Chrome) don't appear in X11 window list and were silently dropped.
> - 2026-07-27: **Broadened auto-detect paths** — `/home/*` and `/media/*` now accepted as valid install locations (project-local compiled binaries like `alpha-ai-server` in `./bin/`).
> - 2026-07-27: **Fixed file manager path resolution** — folder display names now resolved to absolute paths by checking `~/`, `~/Documents`, `~/Desktop`, `/media/<user>/`.
> - 2026-07-27: **Fixed `SessionHierarchyResolver`** — PPID walk now traverses build tools and runtimes as intermediate steps; `ShouldLinkTo` accepts build tools as children of IDEs and terminals.
>   **Overall completion (honest):** ~50% across all 3 services

> **⚠️ MANDATORY RULE — Installer parity (`dotnet run` ≠ installed build).** `dotnet run` compiles from the source tree, so it always runs the newest code. Installed/released builds (`bash publish/build-installer.sh`, `bash publish/release.sh`) ship the **publish output** plus whatever the build scripts explicitly bundle. **A change is NOT "done" until it works in an installed build** — any new functionality or modification must also be added to the installer functionality. See §6 → *Installer-Parity Rule* for the full checklist.

---

## 1. Project Overview

Alpha AI Tracker is an employee monitoring and productivity analytics system consisting of three services:

1. **Desktop Client** (`client/`) — Installed on employee machines. Collects process activity, window titles, CPU/memory usage, and installed app/package information. Sends data to the central server via REST.
2. **Server** (`server/`) — Go + Echo + PostgreSQL + Redis. Central API hub. Receives and stores client data, exposes admin-facing REST API for the web dashboard. Handles authentication for both web admins and employee desktop clients.
3. **Web Dashboard** (`web/`) — Next.js 15 App Router. Admin-facing UI for viewing employee data, managing departments, generating login secrets, analytics, live stream, attendance, and Security & DLP. Real-API surfaces include employees, departments, logs, Employee Journey + Device Specs, live-stream, DLP Rules/Alerts/Shadow IT; remaining pages are scaffolding or Coming Soon (no localStorage mocks).

---

## 2. System Architecture Diagram

```mermaid
flowchart LR
 EMP[Employee Machine\nUser helper Avalonia\n+ Windows DLP Service SYSTEM] -->|REST / JSON\nPOST /api/v1/{device-hardware,\ninstalled-apps,\ninstalled-packages,network-info,\nsession-events,app-sessions,app-items}/sync| SRV[Go Server\nEcho v4 / PostgreSQL\nPort 8080]
 EMP -->|POST /api/v1/auth/employee-login| SRV
 EMP -->|WS DeviceAuth\n/live-stream/push + WebRTC VP8| SRV
 EMP -->|WS DeviceAuth\n/ws presence| SRV
 EMP -->|DeviceAuth\nGET /dlp/policy · POST /dlp/alerts/sync| SRV

 SRV -->|Query / Store| PG[(PostgreSQL)]
 SRV -->|Store/Validate\nOne-Time Secrets| RD[(Redis\n5-min TTL)]

 WEB[Web Dashboard\nNext.js 15 / React 18\nPort 3000] -->|REST / JSON\nhttpOnly Cookie Auth\nvia Next.js Rewrites proxy| SRV
 WEB -->|WS JWT watch-ticket\n/live-stream/watch + WebRTC| SRV
 WEB -->|JWT /dlp/* · /shadow-it/* · removal-code| SRV

 note_ws[WebRTC SFU + presence WS.\nDLP: policy publish → Windows service USBSTOR enforce → alert inbox.]
 
 style EMP fill:#2d2a4e,color:#fff
 style SRV fill:#1a3a4a,color:#fff
 style WEB fill:#3a2a1a,color:#fff
 style PG fill:#2d4a2d,color:#fff
 style RD fill:#4a2d2d,color:#fff
 style note_ws fill:#3a4a5a,color:#fff
```

> All 11 sync endpoints exist on the server (7 original + app-status/hardware-devices/permission-status/storage-devices, 2026-08-11). `activity-logs/sync` and `shell-commands/sync` were removed from the product entirely (client + server). Windows DLP adds DeviceAuth policy/alert/health + JWT admin surfaces (2026-10-22).

---

## 3. Service Breakdown Table

| Service           | Stack                                                                | Responsibility                       | Entry Point                                | Internal Doc                                      |
| ----------------- | -------------------------------------------------------------------- | ------------------------------------ | ------------------------------------------ | ------------------------------------------------- |
| **client/** | .NET 10, Avalonia 12.1, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite | Employee-side data collection & sync | `Program.cs`, `App.axaml.cs`           | [client/ARCHITECTURE.md](./client/ARCHITECTURE.md) |
| **client/DlpService/** | .NET 10 Windows Service (LocalSystem) | DLP policy pull, USBSTOR enforce, alert sync, protected teardown | `DlpService/Program.cs` | [client/ARCHITECTURE.md](./client/ARCHITECTURE.md) §DLP |
| **server/** | Go 1.25, Echo v4.15, pgx v5.10, go-redis v9.21                       | Central API hub, data storage, auth  | `cmd/server/main.go`                     | [server/ARCHITECTURE.md](./server/ARCHITECTURE.md) |
| **web/**    | Next.js 15.3.4, React 18, Redux Toolkit, TanStack Query              | Admin dashboard & analytics          | `next.config.ts`, `src/app/layout.tsx` | [web/ARCHITECTURE.md](./web/ARCHITECTURE.md)       |

### Cross-cutting docs

| Doc | Answers |
|---|---|
| [FILE_HIERARCHY.md](./FILE_HIERARCHY.md) | *Where does this file live and who owns it?* — annotated node tree of all three services |
| [WORKFLOW.md](./WORKFLOW.md) | *How do I do X?* — dev loop, adding a GUI page, re-branding, version bump, adding a runtime asset, release |
| [TESTING.md](./TESTING.md) | *How do I prove it live?* — curl / Windows lab playbooks (DLP USB + removal code, orphan app-items, …) |
| [plan.md](./plan.md) | *What is Security & DLP building?* — phased roadmap (P0–P7); P0+P1 shipped |
| [client/UI_ARCHITECTURE.md](./client/UI_ARCHITECTURE.md) | *How is the desktop UI built?* — design tokens, style classes, the 6 pages, router, binding patterns |
| [client/APP_IDENTIFIERS_README.md](./client/APP_IDENTIFIERS_README.md) | *How do I re-brand?* — every consumer of `APP_IDENTIFIERS` (incl. Windows service / DLP pipe names) |
| [client/VERSION_README.md](./client/VERSION_README.md) | *How do I bump the version?* |
| [client/build.md](./client/build.md) | *How do I build installers?* (+ Windows DLP service lab install) |

---

## 4. Cross-Service Contracts

### Client ↔ Server

| Direction                                  | Protocol                                      | Auth Method                           | Format                                                   |
| ------------------------------------------ | --------------------------------------------- | ------------------------------------- | -------------------------------------------------------- |
| Employee login (client → server)          | REST POST`/api/v1/auth/employee-login`      | emp_id + secret_key (Redis-validated) | JSON`{employeeId, secretKey}` → `{employee, token}` |
| Device hardware sync (client → server)    | REST POST`/api/v1/device-hardware/sync`     | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| Installed apps sync (client → server)     | REST POST`/api/v1/installed-apps/sync`      | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| Installed packages sync (client → server) | REST POST`/api/v1/installed-packages/sync`  | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| Network info sync (client → server)       | REST POST`/api/v1/network-info/sync`        | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| Session events sync (client → server)     | REST POST`/api/v1/session-events/sync`      | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| App sessions sync (client → server)       | REST POST`/api/v1/app-sessions/sync`        | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| App items sync (client → server)          | REST POST`/api/v1/app-items/sync`           | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| App status sync (client → server)          | REST POST`/api/v1/app-status/sync`          | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| Hardware devices sync (client → server)    | REST POST`/api/v1/hardware-devices/sync`    | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| Permission status sync (client → server)   | REST POST`/api/v1/permission-status/sync`   | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| Storage devices sync (client → server)     | REST POST`/api/v1/storage-devices/sync`     | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| Live stream push (client → SFU)            | WS `GET /api/v1/live-stream/push` + WebRTC VP8 | `Authorization: Device <token>`    | Signaling JSON (`hello`/`offer`/`ice`/`start`/`stop`/`select_monitor`/`force_keyframe`); media via WebRTC |
| Live stream uplink probe (client → server) | REST POST `/api/v1/live-stream/uplink-probe` | `Authorization: Device <token>`     | Timed body (discarded); client measures RTT → bitrate ladder |
| Presence keep-alive (client → server)      | WS `GET /api/v1/ws`                         | `Authorization: Device <token>`       | Ping/pong; Online/`wsConnected` (Redis-mirrored when available) |
| Remove the account (client → server)       | REST POST `/api/v1/devices/self-remove`     | `Authorization: Device <token>`       | Empty JSON body → `{removed, machineId, status, lastTrackAt, trackingStatus}`; called AFTER the final drain |
| Server-requested wipe (server → client)    | inline in every `*/sync` response            | —                                     | `{"action":"wipe_local"}` appended only when the reporting device was removed; the batch in that response is already persisted |
| DLP policy pull (service → server)         | REST GET `/api/v1/dlp/policy?sinceVersion=` | `Authorization: Device <token>`       | Immutable published bundle (`policyVersion`, `hash`, `rules`, `shadowDeny`); 304/empty when current |
| DLP alerts sync (service → server)         | REST POST `/api/v1/dlp/alerts/sync`         | `Authorization: Device <token>`       | Batch upsert by client UUID; accepted ids returned |
| DLP health (service → server)              | REST POST `/api/v1/dlp/health`              | `Authorization: Device <token>`       | Heartbeat: policyVersion, enforceOK, lastError |
| Removal verify (service → server)          | REST POST `/api/v1/devices/removal-verify`  | `Authorization: Device <token>`       | One-time code → marks challenge used; unlocks protected teardown |

### Server ↔ Web

| Direction         | Protocol                                     | Auth Method                         | Format                                   |
| ----------------- | -------------------------------------------- | ----------------------------------- | ---------------------------------------- |
| Web admin login   | REST POST`/api/v1/auth/login`              | email + password → httpOnly cookie | JSON`{email, password}` → sets cookie |
| All web API calls | REST via Next.js rewrites (`/api/*` proxy) | httpOnly cookie (auto-sent)         | JSON request/response                    |
| Live stream watch (web → SFU) | WS `GET /api/v1/live-stream/watch` + WebRTC | Short-lived watch ticket (`watch-ticket`; may include `watchBaseUrl`) | Signaling + remote MediaStream on `<video>` |
| DLP Rules / publish | REST `/api/v1/dlp/rules` (+ `POST …/publish`) | JWT cookie + RBAC `dlp-rules` | CRUD current rules; publish compiles immutable `dlp_policy_versions` row |
| DLP Alerts triage | REST `/api/v1/dlp/alerts` (+ `/:id` PATCH, `/:id/history`) | JWT cookie + RBAC `dlp-alerts` | Infinite-scroll inbox; Save-only triage; `dlp_alert_events` history |
| Shadow IT catalog | REST `/api/v1/shadow-it/apps` | JWT cookie + RBAC `shadow-it` | Approve / Block / Flag (Block feeds next publish `shadowDeny` when P3 lands) |
| Issue removal code | REST POST `/api/v1/devices/removal-code` | JWT cookie | Raw code returned once; only SHA-256 hash stored (`removal_challenges`) |

### Contract Documentation

**No formal contract documentation exists** beyond what is implicit in the Go DTO files (`server/internal/dto/`) and the TypeScript API client (`web/src/lib/api.ts`). These are manually kept in sync by the developer — there is no schema generation, OpenAPI spec, or shared type system.

### Inconsistencies Found

1. **Shell commands REMOVED (resolved 2026-08-05)** — Shell command collection/sync removed from client and server. No endpoint, no table (`shell_commands` dropped by migration 007). All dead-code leftovers (`IShellCommandCollector`, 3 platform impls, `ShellCommand` model, server DTOs) were deleted 2026-08-05.
2. **Legacy child tables removed (resolved)** — `browser_contexts`, `file_explorer_contexts`, `urls`, `url_visits` tables and all Go/C# code replaced by single generic `app_items` table. Remaining dead DTOs for those types deleted 2026-08-05.
3. **`activity_logs` removed (resolved)** — The old `activity_logs` table (server Postgres + client SQLite) and all Go/C# code referencing it have been removed. Migration 006 drops the table with no rollback. Replaced by relational `app_sessions` + `app_items`; the web Logs/Comprehensive page now queries `GET /app-sessions`.
4. **Field naming** — Client and server agree on `employeeId` (camelCase) for auth/sync payloads and camelCase field names throughout. **Consistent by convention, no validation schema enforces it.**
5. **Catalog owner column** — `installed_applications.employee_id` / `installed_packages.employee_id` on the server are legacy "first uploader" markers; dedup is now fingerprint-based (`app_fingerprint`, `package_fingerprint`) with per-employee junction tables holding the real per-install metadata.

---

## 5. Current Completion State

### Server — ~50% complete

**What works:**

- Migrations 001–052 run on startup (append-only; latest DLP: **051** security tables, **052** alert triage history)
- **Dynamic RBAC** (2026-08-25): roles/modules/submodules catalog seeded idempotently at boot (`RBACService.SeedCatalog`); protected `GET /modules` + `/roles` CRUD endpoints; `users.role_id` is the sole role source of truth; every auth/user response carries the granted submodule permission keys — includes `security-dlp` → `dlp-rules` / `dlp-alerts` / `shadow-it`
- **Security & DLP (P0+P1, 2026-10-22):** editable `dlp_rules` + immutable `dlp_policy_versions` publish; DeviceAuth policy/alerts/health; JWT admin CRUD + alert triage history; Shadow IT catalog API; one-time `removal_challenges` (hash-only)
- Full CRUD for users, employees, departments
- **`Employee.hasUserLogin` projection** (2026-08-28): every employee SELECT path projects an indexed `EXISTS()` against `users.employee_id` so the web can answer "does this employee have a login?" in O(1) per page row, with no client-side map (see *Server-Projected Flags Rule* in §6)
- Web admin auth (email/password → httpOnly cookie with encrypted JWT)
- Employee auth (Redis one-time secret → JWT token)
- 11 sync endpoints: device_hardware, installed_apps, installed_packages, network_info, session_events, app_sessions, app_items, app_status, hardware_devices, permission_status, storage_devices (+ synced_at for all; 2026-08-11 added the last four)
- Catalog dedup: apps keyed by `app_fingerprint` (desktop_id|binary_name), packages by `package_fingerprint` (package_name|source_manager), per-employee junction tables with `first_seen_at`/`last_seen_at`/`is_active`
- Hourly `jobs/staleness_sweep.go` deactivates junction links idle > `LINK_STALE_DAYS=7`
- App sessions + app items listing (`GET /app-sessions`, `GET /app-items`) with filtering/pagination
- Per-app usage aggregate (`GET /app-sessions/usage`, since 2026-09-04) — one row per `(appDisplayName, processName)` with `firstOpenedAt` / `lastClosedAt` / `totalDurationSeconds`. The web "App Usage" page renders `lastClosed - firstOpened` (NOT `Σ durations`) so multi-tab windows never inflate the per-app total.
- Per-app session list (`GET /app-sessions/usage/sessions`, since 2026-09-04) — paginated server-side; powers the chevron-expanded table on `/employee-journey/apps` (one row per `app_sessions` for the chosen `(appDisplayName, processName)` pair; page 1 returns the 20 most recent).
- Company admin auto-initialization on first run
- Graceful shutdown

**What's missing:**

- **Sparse tests** — live-stream packages have unit tests (`redis`/`scale`/`stream`); most handlers still untested
- **No rate limiting** on any endpoint (including login)
- **No structured logging** — uses `log.Printf` only
- **No Redis fallback** — if Redis is down, employee secret generation/validation fails (no DB fallback)
- **No observability** — no metrics, tracing, health check depth
- **No request validation library** — manual field checks in handlers
- **No data-pruning job** — app_sessions/app_items accumulate indefinitely (staleness sweep only deactivates catalog links)
- **No listing endpoints** for installed-apps, installed-packages, device-hardware, network-info, session-events (sync-only tables)
- **Dead DTOs** — `BrowserContext*`, `Url*`, `UrlVisit*`, `ShellCommand*` types still in `new_schema_dto.go`

### Client — ~85% complete

**What works:**

- Cross-platform process collection (Win/Linux/macOS)
- **Crash-safe session ended_at tracking** — heartbeat persisted every cycle, stale heartbeat detection on boot closes orphaned sessions with approximate crash time. Handles poweroff, process crash, and fast restart.
- SQLite local storage with relational schema (device_hardware_info, storage_devices, installed_applications, installed_packages, network_info, session_events, app_sessions, app_items, app_status, permission_status, employee_info)
- **PID-based session tracking** with `process_id` / `parent_process_id` on `app_sessions`
- **cgroup-based session dedup**: multi-process GUI apps (VS Code, Chrome) collapse to ONE `app_sessions` row per logical window via systemd scope grouping; `grouped_by` / `cgroup_scope` / `context_label` columns record how each session was grouped + which window/profile it is (VS Code workspace folder, Chrome profile)
- **Atomic cascade-close**: sessions and their `app_items` close in ONE transaction via `CloseSessionsAndAppItemsAsync()` — no orphaned open items when a session ends
- **Hierarchy resolver**: node/bash → terminal → IDE via `parent_item_id` + OS process tree
- **GUI-apps-only tracking**: only GUI apps with .desktop files (Linux), .app bundles (macOS), or Start Menu/Program Files entries (Windows) are tracked. CLI tools, shells, build tools, runtimes, and daemons are skipped entirely
- **Wayland-native app tracking**: known GUI apps (VSCode, Chrome) tracked even without a window title — they don't appear in X11's `_NET_CLIENT_LIST` on Wayland
- **Browser journey (Option B — accessibility)**: `browser_tab` + `browser_navigation` + `browser_download` items with the EXACT active-tab URL + domain, read from the OS accessibility tree (AT-SPI / UIA / AX) — works on every browser and every Chrome version (136+ included), no debugger, no extension, no catalog dependency. Session per window, idle-close, graceful close, incognito gated. Private/incognito URLs: Firefox private via AT-SPI `DocURL` (role 95); Chrome incognito requires Chrome to be launched with `--force-renderer-accessibility` (Chrome 136+ ignores runtime AT-SPI activation — the tracker's installed user-level launcher carries the flag). Same-tab navigation always rotates via a bounded historyLag timeout. Linux reader validated live on Chrome 151.
- **Dynamic browser registry**: `IBrowserRegistry` + `BrowserRegistry` replace all hardcoded browser-name lists (`BrowserProcessHints`, `BROWSER_HINTS`, `ResolveFamily`, `StripBrowserSuffix` suffix list). The registry sources from `installed_applications.is_browser` (set via `.desktop` `Categories=WebBrowser`, Windows registry `URLAssociations`, macOS `CFBundleURLSchemes`) and refreshes every 5 minutes. Any browser with a `.desktop` file or URL associations is detected automatically — Floorp, LibreWolf, Waterfox, Arc, Zen, etc. require no code change.
- **File explorer journey tracking**: 3 watchers (ATSPIEventWatcher, FileSystemEventWatcher, RecentFilesWatcher) → EventCoordinator (dedup/correlate/normalize) → JourneyEngine (resolve session, create AppItem rows with 9 journey fields)
- **IObservableEventSource interface**: common contract for all watchers — IDE, Terminal, Office integrations plug in the same pattern
- **Headless subprocess filtering (cross-platform)**: Chromium/Electron `--type=` flags detected via cmdline on Windows (PowerShell), macOS (`ps`), and Linux (`/proc/pid/cmdline`). Centralized `ChromiumSubprocessFlags` in `AppProcessClassifier`
- Models: DeviceHardwareInfo (with mac/gpu/storage), InstalledApplication (with metadata + binary_name), NetworkInfo (with public IP), SessionEvent, AppSession (with FK to installed_apps/packages), AppItem (self-referencing via parent_item_id)
- Encrypted config system (AES-256-GCM, transport→machine key migration)
- Login flow with server
- **Dedicated sync engine** (2026-08-11) — `SyncService` background loop decoupled from collection: drains unsent rows in byte-bounded chunks (1000 rows / ~1MB), gzip-compressed, ~150ms polite pauses, 5-min per-pass budget, exponential backoff; FK-ordered (sessions before items). Collection never blocks on the network.
- **Device hardware**: now collects mac_address, storage_devices, gpu_model from OS
- **Installed apps**: scans actual OS databases (registry, .desktop files, .app bundles) — GUI apps only, not running processes; binary_name mapping extracted from Exec= line
- **Installed packages**: detects CLI tools/runtimes/libraries from npm/pip/apt/brew/choco/winget/scoop/cargo/snap/flatpak — separate table from installed_applications
- **Network info**: has public IP lookup, dedup by IP change, mac_address removed (in device_hw)
- **Shell commands REMOVED** — no longer collected or synced
- **Generic app_items** replaces browser_contexts + file_explorer_contexts + urls + url_visits
- **Process filtering**: SQLite-backed ResolveAppInfo() replaces in-memory IsInstalledApp() — processes not in DB auto-detected and saved before tracking
- **AppDisplayName**: uses real app name from installed_applications (e.g., "Visual Studio Code"), not process name ("code") or window title
- **Parent-child tracking**: `SessionHierarchyResolver` walks PPID chain + open-session PID registry; links via `parent_item_id` (nullable for standalone terminals)
- **Linux filtering fixed**: removed resolvedTitle ??= name fallback that was bypassing window-title filters
- **GNOME daemon contamination fixed**: Xwayland empty `binary_name` caused fuzzy-match SQL `$name LIKE '%%'` to match every process. Fixed via: `WHERE binary_name != ''` in fuzzy SQL, `NonAppProcesses` + `NonAppProcessPrefixes` blocklist expansion (16 GNOME daemons), `KernelNamePrefixes` in first-stage filter, `NoDisplay=true` + `Type!=Application` gate in `.desktop` file parsing
- Auto-start persistence (all platforms)
- Background guard watchdog
- Tray icon (minimize to tray on close)
- Windows power management (prevents sleep)
- **Headless `--background` service mode** — runs the tracking services with no Avalonia/X11 UI (systemd); skips GUI init so the installed service can't crash on Wayland `XAUTHORITY`. **Tracking starts headlessly at boot** (2026-08-18): `ExecuteAsync` restores the persisted session and calls `StartTracking()` itself, so power-on → full tracking (app sessions + browser journeys + inventory) with no GUI. The GUI is login-only — it cannot start or stop tracking
- **Single-instance activation** — a second user launch signals the running instance (named pipe `alpha-ai-tracker-activation`) to raise its window; `--background`/`--minimized` relaunches exit quietly
- **Six-page GUI** (2026-08-10) — `MainWindow` is a router over four exclusive states; pages live in `Views/Pages/`: Splash (boot checklist), Login, PermissionSetup (stepper), and behind the nav rail Dashboard (identity + status tiles + pipeline health + attached devices), System Specs (machine/compute/network/storage/peripherals) and Installed Applications (searchable apps + packages inventory, virtualized). One VM per page, all Transient in DI. Details: [client/UI_ARCHITECTURE.md](./client/UI_ARCHITECTURE.md)
- **Runtime branding from a single source** — `Core/AppInfo.cs` resolves the product name, tagline, initials, publisher, copyright and version from the embedded `APP_IDENTIFIERS` + `VERSION`; no XAML or C# literal names anywhere in the UI. Editing either file re-brands both the app and the installers (§6 → *Branding-Single-Source Rule*)
- **Live stream WebRTC publisher (V3, Windows)** — `LiveStreamClient` + `NetProbeService` + multi-path GDI `ScreenCaptureService` + `ScreenVp8Encoder`; ABR / min uplink / `force_keyframe`; send gated on PC `connected`; `ClientIdentityHeaders` for Client Version; `ALPHA_STREAM_*` / `ALPHA_WS_*`. Installer **1.2.34**
- **Windows DLP service (P0+P1, 2026-10-22)** — separate `client/DlpService` LocalSystem process: policy pull, USBSTOR coarse block, removable-drive alerts, alert sync, ACL-hardened stop, tracker watchdog, named-pipe IPC; user helper writes `device.auth` and runs `--protected-uninstall --code`. Linux/macOS: no DLP enforcement (detection/telemetry only)

**What's missing:**

- **Sparse client tests** — a few unit tests under `client/client.Tests` (DLP pipe timeout / teardown); most collectors still untested
- ~~**No auto-update mechanism**~~ (resolved 2026-08-12 — GitHub Releases self-updater with GUI Check updates; see changelog)
- **No crash reporting** — unhandled exceptions crash silently
- **No offline queue analysis** — if server is unreachable, logs buffer locally with no back-pressure handling
- **No encryption at rest** — SQLite encryption (sqlcipher) is commented out
- **macOS CPU measurement** — macOS process collector skips CPU measurement (always 0%)
- **macOS window titles** — only captures foreground window
- **Six-page GUI not yet ship-tested** — it needs no packaging change (hero images and `APP_IDENTIFIERS` both compile into `client.dll`), but per the Installer-Parity Rule it is not "done" until verified from an installed build

### Web — ~30% complete

**What works:**

- ~50 page routes exist with polished UI
- Login page with animated hero section
- Auth check on mount (Redux + server cookie); **zero localStorage dependencies in web/src** (2026-08-25 mock purge)
- **Server-driven RBAC** (2026-08-25) — `user.permissions` (submodule keys) from every auth response gate the sidebar (`AppSidebar` via `PermissionsProvider`) and routes (`RouteGuard` → `/unauthorized`); `/roles` is a real-API CRUD page with per-submodule permission toggles over `GET /modules` + `/roles`; fail-open for pre-RBAC sessions, strict allow-list otherwise, `company_admin` always full
- Employees page — real API calls via TanStack Query (CRUD + generate secret, portal-rendered Radix action menu); each row's action menu deep-links to **View Journey** and **Device Specs** (2026-08-18); the **"Login Credential"** dropdown item is hidden when `emp.hasUserLogin` is true (server-projected flag, 2026-08-28); the `updateMutation` propagates name/email to the linked user via `usersApi.update` when the server's `UPDATE…RETURNING` reports `hasUserLogin`
- User Management (`/settings/user-management`) — real-API CRUD on `/users` with role assignment, server-side infinite scroll (2026-08-25), and a full edit flow (2026-08-28): per-row **Edit** button → same dialog in edit mode, identity fields (`name`/`email`/`employeeId`) locked, `company_admin` excluded from the role dropdown, password + confirm-password with eye toggles; on save, if the edited user has an `employeeId`, the client also calls `employeesApi.update` to sync name/email to the attached employee. The system `company_admin` row hides the Edit button and a deep-link guard refuses the dialog open — the bootstrap admin is locked from this surface end-to-end.
- **Self-service Profile** (`/settings/profile`) — real-API self-update via `authApi.me()` + `usersApi.update(me.id, …)`. Visible to every authenticated user (no permission module — the sidebar entry omits the `module` key and `RouteGuard` treats it as ungated). Fields: name, email, new password + confirm-password (eye toggles, optional, ≥6 chars). If the current user has a non-empty `employeeId`, name/email propagate to the linked employee.
- **Employee Journey module** (2026-08-18) — Session Timeline, App Usage, and Web Activity are live-API; **Screenshots** and **Location Trail** show Coming Soon (`LOCATION_UI_ENABLED=false` in `web/src/lib/locationUi.ts`; live code in `LocationTrailLive.tsx`)
- **Device Specs module** (2026-08-18) — four real-API pages over the aggregate `GET /employees/:id/detail`: Hardware Overview, Installed Software (Applications/Packages tabs + search), Peripherals, Permissions. This replaced the old `/users/[id]` single-page detail view (deleted)
- Shared building blocks: `EmployeePage` shell (header + picker + loading/error/no-selection states, optional `fetchDetail`), `hooks/use-employee-detail.ts`, `lib/format.ts`, `EmployeeSelector`, `EmptyState`/`InventoryTable`/`FocusTime`/`DeviceClassIcon`
- **Web Infinite-Scroll Rule** + **URL-Synced Filters Rule** — see §6. `useUrlQueryState` must never call `router.push`/`replace` inside a `setState` updater (updates Next `LinkComponent` while another component is rendering).
- **Live stream (WebRTC SFU, V3)** — `/live-stream` multi-tile preview (`?ids=`, max 4); per-tile **FPS**; **Open theater** → `/live-stream/theater`; shared `components/live-stream/*`; `useLiveStreamSocket` (+ optional ticket `watchBaseUrl`); consent `live_view`; Windows publisher only; server Redis presence/tickets (single API)
- **Security & DLP (2026-10-22)** — `/dlp-rules` (CRUD + Publish), `/dlp-alerts` (infinite scroll + URL filters + detail/history dialog; Save applies triage), `/shadow-it` (catalog + Approve/Block/Flag); employees **Issue Removal Code**; `dlpApi` / `shadowItApi` in `lib/api.ts`
- Departments page — real API calls (CRUD)
- Logs/Comprehensive page — real API calls (now using new app_sessions API)
- Dashboard — fully live-API stat tiles (employees total + tracked/untracked split, departments count, app sessions ·24h, web pages ·24h) since the 2026-08-25 mock purge; analytics cards link the journey modules until chart endpoints exist

**What's missing (most pages):**

- **Many pages remain scaffolding** — no backend endpoint yet; the former localStorage mocks were removed 2026-08-25 and those pages now render honest empty states instead (DLP / Shadow IT are live)
- **Server-side permission enforcement** — RBAC grants are enforced only in the web client (sidebar + RouteGuard); API middleware has no role checks
- **No error boundaries** — uncaught React errors crash the page
- **No real-time updates on most pages** — live-stream + presence WS are real-time; other dashboards still poll or are static
- **No accessibility testing** — many interactive elements lack aria attributes
- **No unit tests** — 0 test files
- **GitHub release download** fetches from `clickmaster4285/Alpha-AI-Tracker`, not the org repo
- **Live stream Linux — first Share dialog** — Wayland capture uses xdg-desktop-portal ScreenCast; the employee must click Share once (restore token then silences later starts until Privacy revoke). X11 sessions are not a separate silent path yet (portal still used when available).

---

## 6. Global Conventions

*Extracted from observed patterns, not documented anywhere:*

| Convention                    | Observed Pattern                                                                          |
| ----------------------------- | ----------------------------------------------------------------------------------------- |
| **API versioning**      | All routes under`/api/v1`                                                               |
| **Error responses**     | `{code, message, detail?}` via `dto.APIError` (server)                                |
| **Auth**                | httpOnly cookies for web, JWT in request body for employee clients                        |
| **Naming (Go)**         | PascalCase exports, camelCase JSON fields                                                 |
| **Naming (TypeScript)** | camelCase variables, PascalCase components                                                |
| **Naming (C#)**         | PascalCase for classes/methods,`_camelCase` for private fields                          |
| **Soft delete**         | `deleted_at TIMESTAMPTZ` on all tables, filtered in queries                             |
| **Migrations**          | Sequential numbered SQL files in`server/migrations/`                                    |
| **Go module**           | `github.com/alpha-ai-tracker/server`                                                    |
| **Commit style**        | Descriptive lowercase messages: "now remove the exit btn on the tray on windows", "fixit" |
| **Monorepo tooling**    | No shared tooling (no Turborepo, Nx, etc.). Each service has its own build system.        |
| **Build parity**        | `dotnet run` is NOT a release test — every change must be verified from an installed build; new assets/config/scripts must be bundled by the `publish/*` scripts (see below) |
| **Web list pagination** | List/table pages ALWAYS use server-side **infinite scroll** (`useInfiniteQuery` + IntersectionObserver sentinel) — Next/Previous buttons are forbidden (see *Web Infinite-Scroll Rule* below) |
| **Web filter state** | Every filterable page keeps its filter state (search, picker, date, type, status, …) in the browser URL query string. The URL is the source of truth; React Query keys are derived from the URL; the hooks `useUrlQueryState` / `useUrlActivityFilter` enforce this. Date modals and search inputs have no "Clear" button (see *URL-Synced Filters Rule* below) |
| **Cross-table boolean flags** | Booleans that depend on a cross-table relationship (e.g. "does this employee have a login account?") MUST be projected by the server in the same query that returns the row — never built client-side from a separate fetch (see *Server-Projected Flags Rule* below) |
| **Branding & version**  | Product name and version are written in exactly two files — `client/APP_IDENTIFIERS` and `client/VERSION`. No literal product name or version string anywhere else in C#, XAML, or the build scripts (see below) |
| **Cross-platform OS guards** | Guard platform-specific method bodies with `OperatingSystem.IsWindows/Linux/MacOS()`; do not propagate `[SupportedOSPlatform]` through cross-platform partial/background-service call graphs (see below) |
| **RBAC nav → catalog parity** | Every permission-gated nav entry carries a `module` key that matches a submodule seeded in the server `rbacCatalog`; both halves change together and `module` is NEVER omitted (see *RBAC Nav → Catalog Parity Rule* below) |
| **Machine-scoped data** | Every synced row that describes a specific machine carries `machine_id`, stamped server-side from `DeviceAuth` (never from the request body); device-level reads take `machineID` where `""` = all devices (see *Machine-Scoped Data Rule* below) |
| **Retired-device history** | A removed device keeps showing its last-known data: legacy rows are attributed to it from the device registry's activity window (migration 047), and device-scoped software reads include the staleness-swept `is_active = false` links while the aggregate keeps the filter (see *Retired-Device History Rule* below) |
| **Device lifecycle** | `employee_machines` is the canonical one-row-per-machine registry (`status` `active|old`, `first_track_at`, `last_track_at`, `wipe_requested_at`); `employee_devices` stays the rotating auth-token registry. Removal is a handshake: persist first, then instruct the client to wipe (see *Device Lifecycle Rule* below) |
| **DLP policy versions** | Clients enforce only **published** immutable bundles (`dlp_policy_versions`); editing `dlp_rules` alone never changes the fleet until Publish (see *DLP Policy Version Rule* below) |
| **Protected removal** | Windows stop/uninstall of the agent requires a server-issued one-time removal code (hash at rest); standard users cannot stop the SYSTEM service (see *Protected Removal Rule* below) |
| **Single PostgreSQL** | Exactly one Postgres server for the product; all Go processes share it; never add a second DB / shard / per-service database (see *Single-Postgres Rule* below) |
| **Scale admission** | Login/sync storms return `429`/`503` + `Retry-After`; clients keep `is_synced=0` and honor Retry-After + login jitter; Asynq workers are concurrency-capped (see *Scale/Admission Rule* below) |

### Web Infinite-Scroll Rule (mandatory)

**Every list/table page on the web dashboard MUST paginate with server-side infinite scrolling — Next/Previous buttons are forbidden.**

- `useInfiniteQuery` with `initialPageParam: 1` and
  `getNextPageParam: (last) => (last.page < last.totalPages ? last.page + 1 : undefined)`; flatten pages
  with `data?.pages.flatMap(p => p.data)`.
- Trigger the next fetch with an **IntersectionObserver sentinel** (`rootMargin: '300px'`, gated on
  `hasNextPage && !isFetchingNextPage`) rendered below the list.
- Inline "Loading more…" while `isFetchingNextPage`; a "Showing all N" footer once `hasNextPage` is false.
- Filter/search inputs change the query key (the infinite query restarts at page 1) — there is NO `page`
  state anywhere.
- Reference implementations: `web/src/app/(app)/employees/page.tsx`, the Session Timeline journey page.

### Server-Projected Flags Rule (mandatory since 2026-08-28)

**A boolean flag that depends on a cross-table relationship (e.g. "does this employee have a login
account?", "is this department in use?", "is this role assigned?") MUST be projected by the
server in the same query that returns the row — never built client-side from a separate fetch.**

- **Wrong:** a web `useQuery` that pulls up to N rows of a related table and builds a `Set<string>`
  to answer a per-row question. It is O(N) on the client, O(perPage × flag-query-cost) on the
  server, and **silently wrong** when `N < total related rows` — rows past the boundary are
  misclassified. It also doubles round-trips and burns a second query per page load.
- **Right:** add `EXISTS(SELECT 1 FROM other_table ot WHERE ot.fk = e.id AND ot.deleted_at IS NULL)`
  (or a `COUNT(*) > 0` / `BOOL_OR` aggregate) to the list/return SELECTs. One indexed probe per
  row against the existing FK index — it does **not** scan the related table. The cost is O(1)
  per page row regardless of how big either table grows. The web consumes the field directly from
  the existing DTO (`Employee.hasUserLogin` etc.) — no extra `useQuery`, no client-side map, no
  cache key to invalidate when either table mutates.

- The web API type carries the same field with a camelCase JSON key (e.g. `hasUserLogin: boolean`).
- After a `POST`/`PUT`, prefer the **same response** carrying the projected flag
  (e.g. `updated.hasUserLogin` from `UPDATE…RETURNING`) so the client guard needs no extra trip.
- **First column-level use of this rule:** `Employee.hasUserLogin` (server/ARCHITECTURE.md §4).
  Drop a one-line `> `Server-Projected Flags Rule` note in the relevant API table when a new
  projection is added, mirroring the `hasUserLogin` entry.

### URL-Synced Filters Rule (mandatory since 2026-09-01)

**Every web list/table/filter page MUST keep its filter state in the browser URL query string.** Applying a filter writes the value to `?key=value` first; the React Query call is then re-keyed from the URL. Conversely, when the user manually edits the URL (paste a deep link, browser back/forward, share a link), the page's filter inputs reflect the new URL state and the server query re-fires. The URL is the single source of truth for the page's filter view.

**How to apply:**

- Use `useUrlQueryState<T>` in `web/src/hooks/use-url-query-state.ts` for ad-hoc filter shapes. It returns `[value, setValue]`; `value` is a typed `Record<keyof T, string>` and `setValue(patch)` writes only the supplied keys (preserves unrelated query keys). Empty / null / undefined values are stripped from the URL.
- **Never call `router.push` / `router.replace` inside a React `setState` updater** — compute the next URL outside the updater (use a `valueRef` for merges). Calling the router from an updater updates sibling Next components (`LinkComponent`) while the page is still rendering and throws in React 19.
- Use `useUrlActivityFilter` in `web/src/hooks/use-url-activity-filter.ts` for the rich `{ search, preset, dateFrom, dateTo }` shape consumed by `ActivityFilters`. Presets are encoded as short keys (`today`, `7d`, `all`, `custom`); custom ranges are stored as `from=YYYY-MM-DD&to=YYYY-MM-DD`.
- For search inputs, keep a LOCAL debounced mirror (~400 ms) of the URL value — the URL holds the canonical value, the mirror drives the `<input>` so rapid keystrokes don't spam `router.replace` + re-fetch.
- React Query / `useQueries` keys MUST be derived from the URL state, not from local `useState`. The key changes when the URL changes; that's the trigger for the re-fetch.
- The date modal / filter popovers MUST NOT expose a "Clear" button that wipes the URL state silently. The only ways to clear a filter are (a) select a default preset (e.g. "All time"), (b) clear the search input (no separate X button — typing + delete is the explicit gesture), or (c) navigate to a different URL. The search input has no inline clear (X) button for the same reason.
- `useSearchParams` requires a `<Suspense>` boundary — wrap any client component using the hook (or the URL hooks above, which are built on it). Convention: a top-level `<Suspense fallback={<Loader2/>}>` around the page body.
- Pagination, sort, and per-row deep-link query params (`?edit=1&userId=…`, `?create=1&employeeId=…`) flow through the same URL state. The filter state and the deep-link state share the URL — never use `useState` for either.
- The hooks never touch `localStorage` / `sessionStorage`. They are URL-only.

**Reference implementations:** `web/src/app/(app)/employees/page.tsx` (search + department), `web/src/app/(app)/shifts/page.tsx` (search), `web/src/app/(app)/timesheets/page.tsx` (employee + from + to), `web/src/app/(app)/attendance/page.tsx` (date + status), the three `employee-journey/*` pages (search + date preset), `web/src/components/configuration/ClassifiedItemsTable.tsx` (search + type + category + status — used by both `configuration/apps` and `configuration/websites`), `web/src/app/(app)/device-specs/software/page.tsx` (tab + per-tab search), `web/src/app/(app)/logs/comprehensive/page.tsx` (employee + search + page), `web/src/app/(app)/settings/user-management/page.tsx` (search + the existing `?create=1&edit=1` deep-link).

### Single-Postgres Rule (mandatory since 2026-11-10)

**The product uses exactly one PostgreSQL server.** Tracking ingest, journey reads, attendance,
auth, DLP, Asynq workers, and streaming metadata all connect to that database.

- Do **not** introduce a second Postgres, read-replica-as-SoR, per-service databases, or table sharding.
- Every process’s `DB_MAX_OPEN_CONNS` must be budgeted so **Σ pools + reserve ≤ `max_connections`**.
- Scale by admission control, Redis cache/queue/presence, bounded workers, and optional extra **Go**
  processes — not by adding database servers.
- Plan of record: [`newplan.md`](./newplan.md).

### Scale / Admission Rule (mandatory since 2026-11-10)

**Overload must be explicit; telemetry must not silently skip.**

- DeviceAuth sync POSTs are rate-limited and concurrency-gated. When saturated the API returns
  **`503`/`429` with `Retry-After`**. The client leaves rows `is_synced=0` and retries (never treat
  queue-accept alone as success for mark-synced).
- Login storms use **`ALPHA_SYNC_LOGIN_JITTER_SEC`** (default 120) so agents do not align.
- Background sweeps (session lifecycle, retention, staleness) run via **Asynq** with a fixed
  `ASYNQ_CONCURRENCY` — never unbounded goroutines per job.
- Live publishers are capped by `LIVE_STREAM_MAX_STREAMS` (product target **100** watched at once).
- Installer downloads: global **`RELEASE_DOWNLOAD_MAX_CONCURRENT`** + `Retry-After`; clients use
  **`ALPHA_UPDATE_DOWNLOAD_JITTER_SEC`** and resume Range — per-IP download limits alone are not
  enough for a same-minute fleet stampede.
- Redis may cache/queue/rate-limit; Postgres remains the telemetry system of record.

### Installer-Parity Rule (mandatory)

**Why:** `dotnet run` always reflects the source tree, so it can never catch packaging gaps. The installed app runs from a root-owned dir (`/usr/share/alpha-ai-tracker/` on Linux, Program Files on Windows) with only what the `publish/*` scripts bundled. Recent real incidents: installed app crashed at startup (log written to a root-owned dir), browser-journey tracking dead (extensions/ not bundled), stale binary shipped (publish not rebuilt).

**Every feature/modification must be verified end-to-end in an installed build:**

1. **Always ship-test** — `dotnet run` success is NOT sufficient. Build the installer, install the artifact, and run the new functionality from there:
   ```bash
   cd client
   bash publish/build-installer.sh -b linux   # or win / mac
   sudo dpkg -i installers/alpha-ai-tracker_0.2.0_amd64.deb   # filename tracks client/VERSION
   ```
2. **New runtime assets** (icons, JSON, images, fonts) — bundled ONLY if copied by `build-installer.sh` (`bundle_into_publish`) or the platform builders (`build-deb.sh`, `build-dmg.sh`, `installer-windows.iss`). Add the copy step when you add the asset.
3. **New runtime assets** (embedded scripts, JSON, icons) — the accessibility probe is embedded as a C# string in `LinuxAtSpiBrowserReader` (no external file to bundle). Anything file-based must be added to `bundle_into_publish()` or the platform builders.
4. **New scripts under `publish/`** — copied into every publish output automatically. Runtime-referenced scripts must live in `publish/` (or be added to the copy list).
5. **New env vars / config** — must be added to `.env` BEFORE `encrypt-config.sh` runs; installers ship `config.enc` baked at build time. Dev reads `.env` directly — config that works in `dotnet run` is silently missing in the installer. Config changes now auto-propagate: `EnvLoader` replaces a stale user-config copy (`~/.config/alpha-ai-tracker/config.enc`) with the freshly shipped one on next launch when the decrypted contents differ.
6. **Path assumptions** — the installed app's working dir is root-owned / not user-writable. NEVER write files relative to cwd or the exe dir. Use `~/.config/alpha-ai-tracker/` (logs, machine-id) and `~/.local/share/alpha-ai-tracker/` (DB, sockets). `dotnet run` cannot catch this because the dev working dir is writable.
7. **Packaging edits apply to ALL platforms** — when changing build scripts, update `build-installer.sh`, `build-deb.sh`, `build-dmg.sh`, and `installer-windows.iss` consistently.
8. **Stale-binary guard** — `build-installer.sh` aborts if any source file is newer than the published `client.dll`; fix with `dotnet clean && bash publish/build-installer.sh`. This guard covers compiled code only — items 2–6 are the developer's responsibility.

### No-Hardcoded-Names Rule (mandatory)

**Software detection must NEVER use hardcoded product/software names.** Employees run different OSes (Windows 7/10/11, Ubuntu LTS/releases, macOS), different departments (development / SEO / marketing / IT support / IT technician) install different tools, and packages differ per machine — a name list only works on the machine that generated it and silently breaks (missing rows, misclassified software) everywhere else. Classification must come from **genuine OS metadata**:

1. **PE Subsystem** (`client/Core/ExecutableMetadata.cs`) — the OS's own statement of GUI vs console for any Windows `.exe`: `IMAGE_SUBSYSTEM_WINDOWS_GUI` (2) = application, `IMAGE_SUBSYSTEM_WINDOWS_CUI` (3) = CLI tool/runtime. Used by the Start Menu scan, both registry scans, the classifier, and the runtime session gate. Works on every Windows version, every language, every department.
2. **Filesystem structure** — C:\Windows\* (System32/SystemApps/WinSxS…) = OS component; Start Menu `.lnk` presence = user-facing; `.desktop` presence (Linux) / `.app` bundle (macOS) = GUI application. `ExecutableMetadata.IsWindowsSystemTree` + `IsUninstallerFileName` are the structural helpers.
3. **Registry flags** — `SystemComponent`, `ParentKeyName`, `ReleaseType` mark installer churn/updates; `URLAssociations` http/https marks browsers; `DisplayIcon`/`InstallLocation` resolve the exe that decides app-vs-package.
4. **Driver Store** — drivers are inventoried from `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\DriverDatabase\DriverPackages`, never from name patterns.
5. **Package managers are the source of truth** — winget/npm/pip/choco/scoop/apt/snap/flatpak/brew report what they installed; dedup is by identity/fingerprint, not by filtering names.

**Allowed exceptions (OS-shell constructs, NOT user software):** Linux GNOME/session daemon prefixes in `NonAppProcesses`/`NonAppProcessPrefixes` (gnome-*, gsd-*, gvfsd-*, ibus-*, evolution-*), Windows shell display names (`DisplayNameOverrides`: explorer→File Explorer, svchost→Windows Services…), and the Windows Update `KB`-prefix naming convention. These are OS-provided labels for OS processes; user-installed software detection must stay 100% metadata-driven. When a fix is tempting as a name list, it must be implemented as metadata first (probe the OS), and the resulting rule documented here.

### Client-vs-Web API Auth Separation Rule (mandatory, 2026-09-16)

**An endpoint consumed by the desktop client MUST live under `DeviceAuth` (the `syncGroup` in
`router.go`); an endpoint consumed by the web dashboard MUST live under `JWTAuth` (the
`protected` group). Never put a client-facing route in the web-admin group, and never make a
client handler depend on context values only the other middleware sets.**

- The two middlewares are NOT interchangeable. `DeviceAuth` accepts `Authorization: Device <token>`
  (or the legacy employee-Bearer fallback) and sets **`employee_id`** (+ `device_id`) in the Echo
  context. `JWTAuth` accepts the httpOnly web cookie (or Bearer) with the **web-admin issuer**
  (`alpha-ai-tracker`) and sets only **`user_id`** — employee JWTs carry a different issuer
  (`alpha-ai-tracker-employee`) and are rejected with 401.
- A handler reading `c.Get("employee_id")` (e.g. `getAuthenticatedEmployeeID`) **always 401s**
  when mounted under `JWTAuth`, even for valid admin tokens — that context key is never set
  there. This exact wiring bug shipped in the T&C feature (`POST /terms-consent/sync`) and had
  to be remounted under `DeviceAuth`.
- Client-facing content endpoints that must not leak admin-drafted data get a dedicated
  client-shaped route under `DeviceAuth` (e.g. `GET /terms-content/active` returns only
  `is_active = 1` rows) instead of exposing the admin list.
- Web-admin endpoints that need employee scoping take `?employeeId=` explicitly and stay under
  `JWTAuth` (e.g. `GET /terms-consent`).
- When adding any new endpoint, name its consumer FIRST (client machine vs admin browser) and
  mount it in the matching group — do not copy the group of whatever route happens to be nearby.

### RBAC Nav → Catalog Parity Rule (mandatory)

**A permission-gated page has TWO matching halves, and they ship together: a submodule key in the
server catalog and that same key on the sidebar entry.** A nav item without them is not *"just
ungated"* — it leaks the page to every role and hands its actions to whoever holds an unrelated
permission.

1. **Server — add the submodule to `rbacCatalog`** (`server/internal/services/rbac_service.go`):
   `{Key: "<module>/<page>", Name: "<Label>", Route: "<path>"}` inside the owning module's
   `Submodules` slice. The key is the permission identity (`settings/user-management`,
   `settings/terms-and-conditions`, …); `Route` is the web path. **No migration** — the boot
   seeder (`RBACService.SeedCatalog`) upserts the catalog idempotently on every start, so a new key
   exists after the next server boot.
2. **Web — put the same key on the nav entry** (`web/src/components/layout/AppSidebar.tsx`):
   `{ label, path, module: "<same key>" }`. `module` is the ONLY thing that gates the item.
3. **Never omit `module`.** `visibleChildrenOf` / `isItemVisible` evaluate
   `c.module ? canAccess(...) : true`, so a missing key renders the item for **every** authenticated
   user (and keeps its parent group visible). `findModuleForPath` then skips the module-less entry in
   its exact-match pass and falls back to the nearest guarded parent
   (`/settings/terms-and-conditions` → `settings`), so the page is gated by an unrelated
   permission: a role without it sees a dead link that redirects to `/unauthorized`, and a role
   holding that parent key silently gains the page's full CRUD. The **only** permitted module-less
   entry is `/settings/profile` — intentionally open to all roles and listed in
   `RouteGuard.ALWAYS_ALLOWED`. Adding another path to that list is a permission regression, not a fix.
4. **Grants after adding a key:** `company_admin` is auto-granted everything via
   `RBACRepo.GrantAllPermissions`; **no other role is auto-granted** (default deny), so tick the new
   submodule per role on `/roles`. A role's grant set is re-read from the DB on each auth response, so
   an existing session picks it up on its next page load / re-login.
5. **Verify before claiming done:** confirm the key appears in `GET /api/v1/modules` and that the
   target role's grants actually changed (`role_submodule_permissions`) — hiding the sidebar item
   alone is not proof.

**Reference (why this rule exists):** the 2026-10-16 Terms & Conditions bug — the nav entry carried
no `module` key and the catalog had no `settings/terms-and-conditions` submodule, so a role granting
only the HR module saw **Settings → Terms & Conditions** and clicking it landed on `/unauthorized`.

### Machine-Scoped Data Rule (mandatory since 2026-10-17)

**Any synced row that describes a specific machine carries `machine_id`, and the server stamps it
from the authenticated device — never from the request body.** An employee can swap PCs; without this
dimension, two machines silently overwrite each other's hardware, network, software and permission
rows and the Device Specs module cannot show one machine at a time.

1. **Schema:** the machine-data tables (`device_hardware_info`, `network_info`, `storage_devices`,
   `hardware_devices`, `permission_status`, `app_status`) and the employee↔catalog junctions store
   `machine_id TEXT NOT NULL DEFAULT ''` plus an `(employee_id, machine_id, …)` index. Natural keys
   that used to be employee-wide include `machine_id` (see migration 046): `permission_status` PK
   `(employee_id, machine_id, check_id)`, `app_status` PK `(employee_id, machine_id, key)`, link
   UNIQUE `(employee_id, machine_id, <catalog id>)`. `app_sessions` has carried it since 006.
2. **Write path:** `DeviceAuth` sets `device_machine_id`; each sync handler copies it into the DTO's
   `MachineID` (`json:"-"`), the service maps it onto the model and the repo inserts it. The token is
   the identity — a client cannot spoof another machine by sending a body field.
3. **Read path:** repo reads take `(employeeID, machineID)`; `machineID == ""` means the employee-wide
   aggregate (`GET /employees/:id/detail` with no `?deviceId=`). Rows still carrying `machine_id = ''`
   are the genuinely unattributable bucket and appear only in the aggregate — migration **047**
   re-attributes every legacy row that can be resolved to a device (see *Retired-Device History Rule*).
4. **Labeling:** a device is always rendered **"{employee name} ({hostname})"** — hostname from the
   latest hardware snapshot (or `employee_devices.device_name`, filled from that hostname on sync) with
   the machine id as the last resort. Never render "Unknown device".
5. **Removal is non-destructive:** "Remove current device" revokes `employee_devices` rows, closes
   open sessions/items and flips the employee to untracked. Rows are never deleted; history stays
   queryable and the next login on a new machine registers a fresh device and resumes tracking.

### Retired-Device History Rule (mandatory since 2026-10-17)

**A removed device must keep showing its last-known data on every Device Specs surface — its rows are
attributed to it, and "currently installed" software is not emptied out just because the machine stopped
syncing.** Two failure modes have to be handled together or the `/device-specs?deviceId=<old>` view goes
blank after a device swap.

1. **Attribute legacy rows from the device registry, never from `app_sessions`.** Migration 046's
   `HAVING COUNT(DISTINCT machine_id) = 1` over `app_sessions` is wrong: `app_sessions` is purged by the
   retention worker (30 days) and the replacement machine may have no sessions yet, so a swapped PC's
   leftover rows silently stay `machine_id = ''`. Attribute from `employee_devices`'
   `[created_at, last_seen_at]` activity window and only when exactly ONE window contains the row's
   server-stamped sync time (`created_at` for `app_status` — `updated_at` is client time; `synced_at`
   elsewhere; `last_seen_at` for junctions). Ambiguous rows stay `''`; never guess. See migration **047**.
2. **A removed device's inventory is frozen, not uninstalled.** The hourly `jobs/staleness_sweep.go`
   flips `employee_installed_*` links to `is_active = false` when the machine stops re-syncing — for a
   retired device that empties the whole Installed Software page. Device-scoped reads
   (`ListEmployeeApplications` / `ListEmployeePackages`) therefore include inactive links when the
   requested machine has **no live `employee_devices` row** (all revoked), while the employee-wide view
   keeps the `is_active` filter so a *live* machine's uninstalled apps stay hidden.
3. **Empty states must be truthful.** A removed device will never log in again, so never tell the user
   "the desktop client will upload one after login" for it — say the snapshot was never recorded for
   that device.
4. **Verify with the device registry, not just the API.** Check that the device id the dropdown sends
   (`employee_devices.machine_id`, e.g. the `default-EMP-XXXXX` login fallback) is the id the rows now
   carry, and measure per-device vs aggregate counts (a correct fix leaves the aggregate unchanged).

### Device Lifecycle Rule (mandatory since 2026-10-19)

**A machine's lifecycle has ONE source of truth (`employee_machines`), and every removal is a
"persist first, then instruct" handshake — the server never tells a client to wipe before it has
stored the data that client just sent.**

1. **Registry split.** `employee_machines` (one row per `(employee_id, machine_id)`, `status`
   `active|old`, `first_track_at`, `last_track_at`, `wipe_requested_at`) is the canonical lifecycle
   record. `employee_devices` keeps owning the auth-token state and **rotates a row on every login**
   — never drive a machine list or a status badge off it. A machine that logs in again becomes
   `active` with `first_track_at` preserved; `DeviceAuth` keeps `last_track_at` current on every
   request.
2. **`machine_id` is everywhere.** Server (migrations 046/050): every device-scoped sync table plus
   `session_events`, `app_items`, `location_samples` carry it, stamped from the authenticated device
   (`json:"-"` `MachineID` on the DTO), never from the request body. Client: every local table has
   the column and stamps it from the single-row `machine_identity` table, whose value comes from a
   **lazy** `AppConfig.ClientId` provider (a wipe resets the id, so a re-login in the same process
   must persist the new one — a value captured at construction would stamp the retired machine).
3. **Devices with `machine_id = ''`** are the honest "unknown machine" bucket: they appear only in
   the employee-wide aggregate, never under a specific device.
4. **Remove current device (admin) = persist-then-wipe.** `POST /employees/:id/devices/remove`
   revokes the devices, closes open activity, sets `tracking_status='untracked'` **and** marks the
   machines `old` + `wipe_requested_at=NOW()`. `DeviceAuth` deliberately still accepts the now
   **revoked** token (`GetByTokenHashIncludingRevoked`), flags `device_removed`, and the sync handler
   persists the batch FIRST and only then answers `{"action":"wipe_local"}`. The client wipes its
   local DB + machine id and lands on the sign-in page; its next login mints a **new** machine id and
   a new `employee_machines` row, while the old row stays `old` with its history.
5. **Remove the account (employee) = drain then notify.** `client.exe --remove-account` starts the
   host, drains every unsynced row (bounded by `ALPHA_REMOVE_ACCOUNT_SYNC_TIMEOUT_SEC`), then calls
   `POST /api/v1/devices/self-remove` (DeviceAuth) — which marks the calling machine `old`, advances
   `last_track_at`, revokes its devices and flips the employee to `untracked` — and only then wipes
   locally. One routine (`AccountRemovalService`) serves the CLI burst, the running-instance signal
   and the server-initiated wipe; a failed drain/server call never blocks the local wipe.
6. **Never delete to remove.** Every removal path only changes statuses; history and last-known data
   stay readable (see *Retired-Device History Rule*).
7. **Protected Windows uninstall extends this rule** — see *Protected Removal Rule*. A removal code
   unlocks the SYSTEM service ACL and writes `teardown.ok`; the Inno uninstaller refuses without it.
   `--remove-account` / `wipe_local` still drain + self-remove, but they do **not** bypass the
   uninstall gate on a locked Windows install.

### DLP Policy Version Rule (mandatory since 2026-10-22)

**Endpoints never “chat” per-rule. The fleet pulls a versioned, immutable policy bundle; draft
rules on the web are inert until Publish.**

1. **Two tables.** `dlp_rules` is the admin-editable current set (soft-delete). `dlp_policy_versions`
   is append-only: each Publish writes a new `version` (BIGSERIAL), `bundle_json`, `bundle_hash`,
   `published_at`, `published_by`. Never UPDATE a published row.
2. **Device pull.** `GET /api/v1/dlp/policy?sinceVersion=` (DeviceAuth) returns the newest bundle
   newer than `sinceVersion`, or empty/304 when the service is already current. The Windows service
   stores the applied blob under `%ProgramData%\<APP_MUTEX>\dlp\` and keeps enforcing the **last
   good** bundle offline.
3. **Compile on publish.** `POST /api/v1/dlp/rules/publish` (JWT, `dlp-rules`) snapshots enabled
   rules (+ Shadow IT deny list when P3 lands) into `bundle_json`. Web CRUD alone must not be treated
   as “live on the PC.”
4. **Alerts carry the version.** Every synced alert includes `policyVersion` + optional `ruleId` so
   triage can explain which published bundle was active.
5. **Windows-only enforce.** Detection/telemetry may exist on Linux; USBSTOR / WFP / AppLocker /
   service ACL are Windows-only. `ALPHA_DLP_ENABLED=false` parks all service workers — tracker
   behaviour matches pre-DLP.
6. **No second firewall product.** Domain blocks (P2+) use named WFP/`AlphaAI-DLP-*` filters or
   optional DNS helpers configured by the service — not a custom userspace firewall.

### Unsynced telemetry rule (mandatory)

Local telemetry that has not been accepted by the server is the source of truth until that
acceptance arrives. Do not delete it because of age, row count, or an empty accept list.

1. **DLP alerts.** Delete a local alert and its evidence only when `acceptedIds` in that
   response contains the alert id. `[]` and a missing `acceptedIds` property delete nothing.
   Rejected ids stay and retry with the same id. The server upsert is idempotent.
2. **Synced rows may go.** `is_synced = 1` alerts, orphan evidence with no parent alert,
   diagnostic `enforcement_audit` past 200 rows, and policy JSON snapshots past the newest 3
   may be deleted. `dotnetrunlog.txt` and `updates\update.log` are diagnostic logs, not telemetry.
3. **Disk full is not a silent purge.** Warn when `dlp.db` is large or the volume is low.
   Do not delete unsynced alerts to free space. A full disk can still fail the next insert;
   that failure must be logged.
4. **Session events stay on the existing ceiling.** `ALPHA_TA_MAX_LOCAL_ROWS` may still roll
   the oldest unsynced `session_events` into one `old_data_dropped` sentinel. Do not extend
   that pattern to DLP alerts.
5. **A recovered device token is not uninstall approval.** `device.auth` may be rewritten
   from the current user's tracker SQLite, then `POST /devices/removal-verify` still has to
   succeed. Do not publish an empty token (that deletes the file).

### Protected Removal Rule (mandatory since 2026-10-22)

**Marketing-accurate language: protected uninstall for standard users — not “impossible for
Administrators offline.” Safe Mode / offline disk remains the IT break-glass path.**

1. **Issue once, store hash.** `POST /api/v1/devices/removal-code` (JWT) mints a raw code returned
   **once**, stores only `sha256(raw)` in `removal_challenges` with `expires_at` / `used_at` /
   `purpose` (`stop`|`uninstall`|`both`). TTL from `DLP_REMOVAL_CODE_TTL_MIN` (default 60).
2. **Verify then teardown (product uninstall — not plain `--remove-account`).**
   - **Windows:** `client.exe --protected-uninstall --code <raw>` → named pipe `AuthorizeTeardown`
     → service `POST /devices/removal-verify` → unlock DACL → stop service → drain/self-remove/wipe
     → `%ProgramData%\<APP_MUTEX>\dlp\teardown.ok`.
   - **Linux:** `alpha-ai-tracker --protected-uninstall --code <raw>` → HTTP `removal-verify`
     (DeviceAuth from SQLite / repaired token; no DLP pipe) → drain/self-remove/wipe →
     `teardown.ok` under LocalAppData + best-effort `/var/lib/<pkg>/dlp` → `.deb` `prerm` gate.
3. **Uninstaller gate.** Windows Inno `InitializeUninstall` requires `teardown.ok` (no break-glass
   checkbox). Service install uses a locked DACL (LocalSystem full / Administrators query-only) so
   Services.msc Stop fails until a removal code unlocks it. Linux `.deb` `prerm` refuses
   `remove|purge` without the gate.
4. **Identity names from `APP_IDENTIFIERS`.** `WINDOWS_SERVICE_NAME`, `WINDOWS_DLP_PIPE_NAME`,
   `APP_MUTEX` — never hardcode service/pipe paths in scripts or C#.
5. **Never store a forever plaintext uninstall password** in SQLite or on disk. Replay of a used or
   expired code fails.
6. **Honesty.** Never claim “Admin cannot uninstall offline.” Document Safe Mode / offline disk in
   the support runbook (`TESTING.md` / ops notes).

### Cross-Platform Analyzer Safety Rule (mandatory)

**A client build that hangs or consumes multiple GB is a critical development/CI blocker.** In this
single-TFM cross-platform project, do not place `[SupportedOSPlatform]` on fields or methods whose
platform state must propagate through a cross-platform partial class or async `BackgroundService`
call graph. .NET 10's `PlatformCompatibilityAnalyzer` can enter exponential global-flow analysis.

- Put an explicit `OperatingSystem.IsWindows/Linux/MacOS()` early-return guard **inside** each
  platform-specific method.
- If platform-only APIs inside event-handler lambdas still produce CA1416, a narrowly scoped
  `#pragma warning disable CA1416` is allowed only in that platform file and only when every entry
  point has the explicit runtime guard.
- Never fix this by globally disabling .NET analyzers.
- After changing platform code, run a non-incremental `dotnet build`; investigate builds taking
  more than twice the normal baseline or approaching 1 GB compiler memory.

### Branding-Single-Source Rule (mandatory)

**Every visible product name and version comes from `client/APP_IDENTIFIERS` + `client/VERSION`.** Neither value is duplicated anywhere else. Edit either file, rebuild, and the rail wordmark, window title, splash, footer, tray tooltip, log banners, installer filenames and package metadata all follow — with no other source edit. This is a structural guarantee, not a convention to remember:

| File                    | Build-time consumers                                                                                    | Runtime consumer                                                          |
| ----------------------- | ------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------- |
| `APP_IDENTIFIERS` | `build-deb.sh`, `build-dmg.sh`, `generate-windows-vars.sh` → `installer-windows.iss`, `release.sh` (sourced as shell) | `client.csproj` embeds it as `client.APP_IDENTIFIERS`; `Core/AppInfo.cs` parses it |
| `VERSION`         | same scripts — package/artifact filenames and installer version fields                                   | `client.csproj` → `Version` / `InformationalVersion`; `AppInfo.Version` reads it back |

**Runtime path.** `Core/AppInfo.cs` reads the embedded resource with `Assembly.GetManifestResourceStream()` and parses it with a **strict regex — the file is read as data, never executed** — exposing `DisplayName`, `Publisher`, `AppUrl`, `PackageName`, `BundleId`, `ExecutableName`, `AppMutex`, `WmClass`, `Tagline`, `Initials`, `Copyright`, `Version`, `VersionDisplay`, `TitleWithVersion`. Every accessor has a fallback default and the loader swallows exceptions — **branding must never take the app down**. `MainViewModel` re-exposes these as `AppDisplayName` / `AppTagline` / `AppInitials` / `AppVersionDisplay` / `AppCopyright` / `AppTitleWithVersion` so XAML binds to them; `App.axaml.cs` uses `AppInfo.DisplayName` directly for the tray tooltip and menu.

**Three consequences worth knowing before you "fix" something:**

1. **Re-branding needs NO installer-script change.** The embedded resource lives inside `client.dll`, and hero images are swept in by the existing `<AvaloniaResource Include="Assets\**" />` glob — both ride the publish output automatically. The Installer-Parity Rule still applies to *new file-based* runtime assets; it does not apply to branding strings or to anything under `Assets/`.
2. **`client.csproj` reads `VERSION` at project-evaluation time, not in a `BeforeBuild` target.** This is deliberate — a target-based read leaves IDE design-time builds silently falling back to `1.0.0`. Trade-off: after editing `VERSION`, run `dotnet clean` (or restart the IDE) so the cached project graph is flushed.
3. ⚠️ **`Core/EncryptedConfigService.cs` `TransportKeySeed` / `MachineKeyPrefix` are NOT branding.** They read like product names (`"AlphaAITracker:TransportKey:v1"`) but are cryptographic key-derivation seeds. Templatizing them from `APP_IDENTIFIERS` would make **every `config.enc` already deployed in the field undecryptable**. Leave them byte-for-byte alone during any re-brand.

**Acceptance proof (re-brand smoke test):** change `DISPLAY_NAME` in `APP_IDENTIFIERS`, bump `VERSION`, `dotnet clean && bash publish/build-installer.sh -b linux`, install the artifact, and confirm the rail, window title, splash, footer, tray tooltip and installer filename all changed with no other edit. Details: [client/APP_IDENTIFIERS_README.md](./client/APP_IDENTIFIERS_README.md), [client/VERSION_README.md](./client/VERSION_README.md).

---

## 7. Known Gaps / Risks

### Cross-Cutting

| Gap                                 | Severity  | Details                                                                                                                                                                          |
| ----------------------------------- | --------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Sparse tests**              | 🔴 High   | Live-stream Go packages have tests; client/web and most server handlers still untested.                                                                                           |
| **No observability**          | 🟠 Medium | No structured logging, metrics, tracing. Debugging production issues requires SSH + log scraping.                                                                                |
| **Client-side RBAC grants**    | 🟠 Medium | RBAC grants (roles/modules/submodules, migration 025) are enforced only in the web frontend (sidebar + RouteGuard). A malicious user can bypass them — API middleware still has no role checks.                                                                   |
| **No rate limiting**          | 🟠 Medium | Login endpoint and all sync endpoints have no rate limiting. Brute-force / DoS is trivial.                                                                                       |
| **No server permission model**| 🟠 Medium | Any authenticated user can access all protected endpoints; no role checks server-side.                                                                                           |
| **Cross-account injection**   | 🟠 Medium | Sync handlers validate the JWT but don't verify the body `employeeId` matches the JWT subject.                                                                                   |
| **Sync FK race — app-items vs app-sessions** | 🟢 Low (FK 500 fixed 2026-09-04; silent-loss component fixed 2026-09-09 via `rejectedIds`) | The client sends `app-sessions` and `app-items` in separate HTTP calls. A retry, second client process, or network blip could land the items batch BEFORE the parent session row arrived, causing Postgres `SQLSTATE 23503 (app_items_app_session_id_fkey)` to 500 the whole batch and silently drop every row. **Fix:** `server/internal/repository/new_schema_repo.BulkInsertAppItems` now preflights each 500-row batch with `SELECT id FROM app_sessions WHERE id = ANY($1)` over the **distinct** `appSessionID`s (one indexed round-trip, O(1) per id), drops rows whose `appSessionID` is empty or missing, logs a single WARN per batch with count + first 20 ids, and returns 200 with `accepted: <survivor count>`. Dropped rows stay `is_synced=0` in the client's local SQLite and re-send on the next sync — by then the parent session has landed. (⚠️ implemented 2026-09-09 — until then the client marked ALL sent ids synced and the dropped rows were lost; see top changelog entry.) **Why the other 11 sync endpoints are NOT at risk:** every other sync table's parent is `employees.employee_id`, which is created server-side via the web admin BEFORE any client can sync, so no client-side race exists. The only client-synced parent→child pair is `app_sessions → app_items`, and that's the one the preflight covers. Reproduce + verify in `TESTING.md` §"orphan `app-items` FK 23503 fix" (Steps A-D). No migration, no client change — server-only fix; ships with the next server deploy. |
| **Scaffolding pages dominate** | 🟠 Medium | Many web pages still have no backend endpoint; since 2026-08-25 they render honest empty states (the localStorage mocks were deleted). Real-API now also includes DLP Rules / Alerts / Shadow IT (2026-10-22), plus login, dashboard, employees, user-management, roles, departments, logs, Employee Journey, Device Specs, live-stream, attendance/shifts/timesheets. |
| **Default passwords**         | 🟠 Medium | `AlphaAI@2024!` is the compiled-in default. Easy to forget to change.                                                                                                          |
| **No offline/retry strategy** | 🟢 Low    | Client retries sync every cycle but has no exponential backoff or dedup.                                                                                                         |
| **No data-pruning job**       | 🟢 Low    | Server's only background job (`staleness_sweep.go`) deactivates stale catalog links but never deletes old app_sessions/app_items data.                                            |
| ~~Dead shell-command code~~ (resolved) | — | All shell-command and legacy browser-context DTOs/models deleted 2026-08-05 (client + server). |
| **Horizontal scaling**        | 🟢 Low    | Server uses Redis for employee secrets (short TTL), so scaling is straightforward — but DB queries have no query analysis.                                                      |

---

## 8. How to Run Locally

### Prerequisites

- Go 1.22+ (tested 1.25), PostgreSQL 16+, Redis 7+
- Node.js 20+, npm
- .NET 10 SDK (for client development with `dotnet run`)
- Docker (optional, for PostgreSQL/Redis)

### 1. Start PostgreSQL and Redis

```bash
# Using Docker (recommended)
docker run -d --name pg -e POSTGRES_USER=alpha_ai -e POSTGRES_PASSWORD=yourpassword -e POSTGRES_DB=alpha_ai_tracker -p 5432:5432 postgres:16
docker run -d --name redis -p 6379:6379 redis:7
```

### 2. Server

```bash
cd server
cp .env.example .env
# Edit .env — set DB_PASSWORD and JWT_SECRET
make setup
make run
# Server starts on http://localhost:8080
```

### 3. Web Dashboard

```bash
cd web
npm install
npm run dev
# Dev server on http://localhost:3000
```

If server runs on a different host, set `NEXT_PUBLIC_API_URL` in `web/.env`.

### 4. Desktop Client

```bash
cd client
# Ensure .env has ALPHA_SERVER_URL=http://localhost:8080
dotnet run
```

### Notes

- No `docker-compose.yml` exists — you must start PostgreSQL and Redis manually.
- The client requires a running server with at least one employee created via the web admin.
- Login to the web dashboard with the default credentials from `.env` (admin@alphai.com / AlphaAI@2024!).
