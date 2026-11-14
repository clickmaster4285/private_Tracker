# Client Architecture — Alpha AI Tracker Desktop App

> **Last audited:** 2026-11-13 (face verify local images + first/simple modes)
> **Changelog:**
> - 2026-11-13: **Local face package + first/simple verify.** Cache enrollment JPEGs
>   under `face/samples/`; first verify multi-pose wizard; simple on boot/unlock;
>   early `TryLaunchEarly` before Host.Build; warm-up + event drain in tracker.
>   **VERSION 2.0.9**.
> - 2026-11-13: **Face-verify every session start.** `TryLaunchOnSessionStart` on
>   boot restore, GUI restore/login, and LogCollector login-poll. **VERSION 2.0.8**.
> - 2026-11-13: **FaceDetectorYN detect + offline verify.** OpenCvSharp 4.13
>   `FaceDetectorYN` for YuNet (parity with face_worker); AdaFace stays ORT.
>   Verified → local `MarkVerifyAsync` then best-effort server audit. **VERSION 2.0.7**.
> - 2026-11-13: **YuNet ORT 640×640.** Detector input size taken from ONNX metadata
>   (zoo export fixed 640), not hardcoded 320. **VERSION 2.0.6**.
> - 2026-11-13: **Face models via DeviceAuth only.** YuNet + AdaFace download from
>   `/face-models/packages/yunet|adaface-ir50` into LocalAppData (no GitHub on PC).
>   **VERSION 2.0.5**.
> - 2026-11-13: **Private-LAN HTTPS auto-trust.** `TlsCertificateTrust` (OS store +
>   ProgramData / `ALPHA_TLS_CA` PEM; lab `ALPHA_TLS_INSECURE`). Windows installer
>   bakes `certs/rootCA.pem` into `{app}\tls\` and post-install installs
>   LocalMachine\Root + ProgramData PEM — no manual `install-org-ca.ps1` on fleet.
>   **VERSION 2.0.3**.
> - 2026-11-11: **Protected-uninstall AppLocker + pipe.** Teardown IPC not gated on DLP
>   enable; longer ApplyTeardownGate timeouts; `AppAllowEnforcer.Clear` deletes SrpV2;
>   `WindowsTeardownCleanup` AppLocker + FW COM + browser clear (SYSTEM task). **VERSION 1.4.7**.
> - 2026-11-11: **Lab leftover wipe doc.** See repo [`removeTracker.md`](../removeTracker.md)
>   (AppLocker/SRP purge + hosts/FW after protected uninstall). Linked from TESTING §C.
> - 2026-11-11: **Silent apply AppLocker + pre-stop.** SYSTEM-only allow
>   `%ProgramData%\<mutex>\updates\*`; fingerprint `v7`; helper stops service before Inno;
>   after success clear flags + `relaunch_tracker`, `sc start` only if not RUNNING (Inno
>   already starts service). **VERSION 1.4.6**.
> - 2026-11-11: **Post-auto-update restart.** Apply helper clears gates, restarts DLP service
>   (wait RUNNING), sets `relaunch_tracker`; watchdog starts `--background --restart`. **VERSION 1.4.5**.
> - 2026-11-11: **Update-priority yield.** Newer release → `update_priority` flag; tracker parks
>   sync/live/screenshots/collectors so download+apply run first (auto jitter ≤120s). **VERSION 1.4.2**.
> - 2026-11-11: **Silent update `update_in_progress` TTL + fleet download jitter.** Orphan/TTL
>   clear so watchdog relaunches; clear flag if helper spawn fails; post-Inno service restart
>   for AppLocker v6; `ALPHA_UPDATE_DOWNLOAD_JITTER_SEC` + Retry-After on downloads. **VERSION 1.3.7**.
> - 2026-11-11: **AppLocker PF(x86) prefix order.** `NormalizeAppLockerPath` used
>   `StartsWith(Program Files)` before PF(x86), turning
>   `C:\Program Files (x86)\Microsoft\Edge\Application\*` into invalid
>   `%PROGRAMFILES% (x86)\…`. Fix: `StartsWithDir` (boundary `\`) + match PF(x86) first;
>   rewrite broken `%PROGRAMFILES% (x86)` macros; fingerprint `v6`. **VERSION 1.3.6**.
>   Verified: `dotnet build` DlpService. Installer-Parity: next Windows installer.
> - 2026-11-11: **AppLocker Edge allow under Program Files (x86).**
>   `AppAllowEnforcer.NormalizeAppLockerPath` mapped `Program Files (x86)` to
>   `%PROGRAMFILES(X86)%`, which AppLocker does not understand (only `%PROGRAMFILES%`,
>   covering both PF roots per MS docs). Symptom: Task Manager (`%SYSTEM32%`) unblocked;
>   Edge stayed default-deny. Fix: both PF roots → `%PROGRAMFILES%`; rewrite legacy
>   `(X86)` macros on apply; fingerprint `v5`. `AppProcessGuard.AddExpandedPaths` emits
>   matchers for both Program Files roots when expanding `%PROGRAMFILES%` (and still
>   expands legacy `%PROGRAMFILES(X86)%`). **VERSION 1.3.4**. Verified: `dotnet build`
>   DlpService. Installer-Parity: next Windows installer.
> - 2026-11-10: **`--timedatewarning` subprocess.** `TimeDateWarningWorker` (start + hourly):
>   server-time skew + IP/schedule IANA TZ vs OS; spawns sibling Avalonia process when wrong.
>   State: `%LocalAppData%/<APP>/timedatewarning/state.json`. Open Date & Time settings; Dismiss
>   cooldown. `ALPHA_TA_TIMEDATE_*`. **VERSION 1.0.13**. Verified: `dotnet build`. Installer-Parity:
>   re-bake `config.enc`.
> - 2026-11-10: **Screenshot orphan re-upload fix.** `UploadOneAsync` reads WebP bytes then
>   closes before `DeleteAccepted` (Windows file-lock). `GuessMetaFromName` recovers trailing
>   UUID as `ScreenshotId` (machine id no longer absorbs it). No Guid mint on list. **VERSION
>   1.0.12**. Verified: `dotnet build`.
> - 2026-11-10: **Offline-first screenshots.** `ScreenshotCaptureWorker` + `ScreenshotSyncWorker`
>   under `Services/Screenshots/`. Capture: `ScreenCaptureService.CaptureStill` → SkiaSharp WebP →
>   `%LocalAppData%/<APP>/screenshots/pending/` (+ JSON sidecar); skip on pending pressure (never
>   delete unsynced). Sync: multipart `POST /api/v1/screenshots/upload`; delete local only on OK;
>   honor Retry-After. Env: `ALPHA_SCREENSHOT_*`. **VERSION 1.0.10**. Verified: `dotnet build`.
>   Installer-Parity: re-bake `config.enc`.
> - 2026-11-10: **Windows live-stream VP8 native load.** `VpxNativeBootstrap` (Program +
>   `ScreenVp8Encoder`) preloads app-local `VCRUNTIME140` and resolves `vpxmd.dll` from
>   `AppContext.BaseDirectory`. `publish/ensure-vpx-native.ps1` (MSBuild AfterBuild/Publish +
>   `build-installer.sh`) copies CRT next to the exe; optional `ALPHA_BUNDLE_VC_REDIST=1` for
>   full redist in Inno. `LiveStreamClient` probes before encode → honest `stream_encode_error`.
>   **VERSION 1.0.8**. Verified: `dotnet build` client. Installer-Parity: next Windows installer.
> - 2026-11-10: **Scale client half.** `ALPHA_SYNC_LOGIN_JITTER_SEC` (default 120) delays
>   post-login / session-restore sync via `RequestImmediateSyncWithLoginJitterAsync`.
>   `SyncService` honors `Retry-After` on HTTP 429/503 and still leaves rows `is_synced=0`.
>   Verified: `dotnet build` client (0 warnings). Installer-Parity: re-bake `config.enc`.
> - 2026-11-10: **Delete-block ACL + tighter AppLocker bootstrap (DlpService).** New
>   `DeleteBlockAclEnforcer` — NTFS Deny Delete + DeleteSubdirectoriesAndFiles for Authenticated
>   Users on Fixed/Removable volume roots when lockdown is ON; `ALPHA_DLP_DELETE_BLOCK` (default
>   `true`) gates apply and clears leftovers (`disabled_local` / pause / park / teardown). Marker
>   `delete_block_roots.json` under the DLP data root; pipe `Health.deleteBlock`; audits
>   `delete_block_apply` / `delete_block_clear`. Caveat: temp→delete→rename Save paths may fail
>   while ON. `AppAllowEnforcer`: PowerShell path rules are SYSTEM-only (S-1-5-18); Settings /
>   ImmersiveControlPanel / broad WindowsApps wildcards dropped from Everyone bootstrap; fingerprint
>   `v3`. `AppProcessGuard` kills interactive PowerShell/Settings/cmd/terminal (session > 0) even
>   when MainModule path is unreadable. `EnvBootstrap` / `generate-windows-vars.sh` /
>   `installer-windows.iss` / `install-service.ps1` seed the new key. Client **VERSION 1.0.6**.
>   Installer-Parity: next Windows installer.
> **Last audited:** 2026-11-07 (hardware & port control)
> **Changelog:**
> - 2026-11-07: **Hardware & Port Control (scan + enforce).** Tracker: `PortDeviceCollector`
>   (BackgroundService) scans every PnP port/device via `Get-PnpDevice`, classifies into 3 categories,
>   writes `machine_ports` (SQLite, `DatabaseSchema.UpsertMachinePortSql`; client owns present/removed),
>   drained by `SyncService` to `POST /hardware-ports/sync`; gated by `ALPHA_PORT_SCAN_ENABLED`.
>   DlpService: `PortDeviceEnforcer` disables blocked classes (Device Installation Restrictions,
>   retroactive) + instances (`Disable-PnpDevice`), wired into `EnforceWorker` + park-clear, local
>   switch `ALPHA_DLP_PORT_BLOCK` (`DlpConfig`/`EnvBootstrap`); `PolicyBundle` parses `deviceControl`.
>   **Offline-first fix:** `PolicyStore` persists `deviceControl` in a new `policy_device_control`
>   table (`dlp.db`) on apply and emits it on rebuild — previously the relational round-trip dropped it,
>   so `EnforceWorker` always received an empty block set and nothing was ever enforced. Verified:
>   `dotnet build` client + DlpService (0/0); live scan (52 devices). Ships in the next installer.
> - 2026-11-07: **Local blocker switches.** `DlpConfig` gains `NetworkLockdown`
>   (`ALPHA_DLP_NETWORK_LOCKDOWN`) and `AppBlock` (`ALPHA_DLP_APP_BLOCK`) next to `UsbBlock` / `WebBlock`.
>   `EnforceWorker` gates `ApplyLockdownAsync`, the lockdown browser allow-set in `ApplyWebAsync`, and
>   `ApplyAppLockdownAsync` (AppLocker + `AppProcessGuard`) on them; `false` clears leftovers with state
>   `disabled_local`. USB `false` now writes "allow" once so a stale block is lifted.
> - 2026-11-07: **Auto-update every 60s from the server `setup/` folder.** `ALPHA_UPDATE_AUTO_CHECK_HOURS`
>   → `ALPHA_UPDATE_CHECK_INTERVAL_SEC` (default 60, min 15) in `AppConfig`, `DlpConfig`, `EnvBootstrap`
>   ship defaults, `generate-windows-vars.sh`, `installer-windows.iss`. `SilentUpdateWorker`: cadence =
>   interval, failure backoff 1m/5m/15m, `apply_failed.txt` 1h guard. `AppUpdateService`: same cadence,
>   Linux auto-install once/hour/version. Asset version match accepts `v1.0.0.exe`. `release.sh` now
>   copies installers to `../server/setup/v<VERSION>.<ext>` (no `gh`/git). Verified: `dotnet build`
>   client + DlpService (0 warnings), `dotnet test` 37/37. Installer-Parity: next installer carries it.
> - 2026-11-06: **P3 AppLocker launch dialog.** Exe + Appx Enforce, MSI empty-deny,
>   UTF-16 XML, AppIDSvc restart, Taskmgr Denied verify. Blocked apps show the Windows
>   organization dialog on launch; `AppProcessGuard` remains backup. Installer **2.10.2+**.
>
> - 2026-11-05: **P3 AppLocker enforce.** `AppAllowEnforcer` applies local AppLocker EXE
>   default-deny when `lockdown` is ON (same pause/teardown/park clear as network).
>   Bootstrap A = service/tracker dirs + Explorer/Start/shell paths (not Taskmgr/calc).
>   Bundle `allow.apps` may include `path` / publisher / sha256. Clear on teardown.
>
> - 2026-11-05: **P2 Chrome/Edge URLAllowlist.** Lockdown on → `URLBlocklist=*` +
>   `URLAllowlist` for allow hosts (minus explicit denies) under the same HKLM policy
>   keys already used for deny-list. Lockdown off clears allowlist.
> - 2026-11-05: **P1 network lockdown.** `LockdownNetworkEnforcer` applies AdvFirewall
>   complement deny on outbound TCP/UDP 80/443 when the published bundle has
>   `lockdown: true`. Bootstrap = `ALPHA_SERVER_URL` / `device.auth`. Pause via
>   `lockdownPausedUntil` on policy pull. Clear on teardown / DLP disabled.
> - 2026-11-05: **Tracking mode / Start-Stop removed.** No Fixed/Roaming, no `--tracking-controls`,
>   `--control-shell`, `--drain-only`, `control.json` control plane, or `ALPHA_CONTROL_POLL_SEC`.
>   `ParkTrackingWork` / `ParkSyncWork` are `--gui` only. Watchdog always relaunches
>   `--background`. Ships in the next installer.
> - 2026-11-05: **Terms & Conditions acceptance gate removed.** `TermsService`, `TermsWindow`,
>   `--terms`, `client_terms`, and `ALPHA_TERMS_*` are gone. Existing local DBs drop
>   `client_terms` on startup. Live stream no longer waits on `live_view` consent.
>   Ships in the next installer.
> - 2026-11-03: **Attendance heartbeat no longer starves after reboot (2.3.25).**
>   `SyncService` stamps `last_heartbeat_at` at the start of every authenticated drain pass
>   (not only inside `LogCollectorService` collect cycles) and drains `/app-status/sync`
>   **before** heavy `app_sessions` / `app_items` so the per-pass budget cannot leave
>   attendance Last Seen frozen while Live Stream / App Usage still move. Server half
>   also extends Last Seen from `employee_devices.last_seen_at` (deploy without waiting
>   for this installer). Installer-Parity: ship **2.3.25+**.
> - 2026-11-02: **Linux Tracker Controller tray.** Close/minimize → tray again; supervisor
>   relaunches only if the `--tracking-controls` process is killed.
> - 2026-10-31: **Linux Tracker Controller supervisor.** `TrackingControlsSupervisor`
>   auto-opens / relaunches `--tracking-controls` when roaming. Windows still uses
>   DlpService watchdog.
> - 2026-10-31: **Linux DLP L5.** Privileged `AlphaAITracker.Service` on Linux (systemd
>   system unit): udev USB, hosts+nft web block, Unix socket IPC, `/var/lib/<pkg>/dlp`
>   credentials. `.deb` ships `Service/` + `*-dlp.service`. Installer-Parity: rebuild `.deb`.
> - 2026-10-31: **L2 Kill → reopen.** Unsupervised `--background` hands off to
>   `alpha-ai-tracker.service` before mutex; autostart uses `systemctl --user start`;
>   StartLimit* under `[Unit]`. Resources Kill of MainPID reopens in ~8s.
> - 2026-10-31: **`--remove-account` always wipe; Linux protected-uninstall token path.**
>   Fixed mode no longer blocks plain `--remove-account` (`AccountRemovalService` /
>   `Program.cs`): stop → bounded drain → self-remove → wipe → schedule relaunch
>   `--background --restart`. Product uninstall still requires `--protected-uninstall --code`.
>   Linux: `TrackerDeviceTokenReader` candidates use `PACKAGE_NAME` (`alpha-ai-tracker`);
>   verify uses recovered SQLite token without Windows `device.auth` republish. Plan:
>   `linuxplan.md`. Lab OK on this VM. Shared binary — Windows installer picks up next bake.
> - 2026-10-31: **Linux L1–L3 (control, reopen, protected uninstall).**
>   Control poller + LocalAppData `control.json`; `LinuxUserSystemdUnit` RestartSec=8;
>   `teardown.ok` under LocalAppData + `/var/lib/<pkg>/dlp`; `.deb` prerm gate. Plan:
>   `linuxplan.md`. Installer-Parity: rebuild `.deb` to ship prerm.
> - 2026-10-31: **Start/Stop audit actually reaches the server.** `TrackingEventReporter`
>   now uses UUID `clientEventId`, maps flush to `complete|partial|pending`, and queues
>   offline under LocalAppData `control/pending-tracking-events.json` (follower retries).
>   `TrackingControlFollower` always reports Stop (even when drain fails); reboot resume
>   does not invent a Start log. Prior `start-{guid}` + `ok`/`timeout` payloads were
>   rejected by the API. Installer-Parity: next Windows build.
> - 2026-10-30: **Roaming UX through 2.3.12.** `LocalRoamingControl` + day timer; Stop
>   disconnects WS/WebRTC; `--tracking-controls` hide-to-tray; celebration toast removed;
>   `TrackingControlsActivation` show-pipe; MainWindow rail **Tracker control** (roaming-only);
>   `service.env` merges control/DLP poll knobs from bake `.env`. Installer-Parity: **2.3.12+**.
> - 2026-10-28: **P2 Roaming Start/Stop.** Desired×phase SM; `PauseTracking` /
>   `TrackingControlFollower` / `PurgeSyncedAsync`; `--control-shell` + `--tracking-controls`;
>   watchdog drain/control-shell; `TrackingEventReporter`. Sleep-gap timer polish later.
> - 2026-10-28: **P1 Fixed lockdown.** `TrackingModeResolver` + DlpService `PipeCallerGuard` +
>   `RequestStop` → `mode_fixed`. *(2026-10-31: Fixed no longer gates `--remove-account`.)*
> - 2026-10-28: **Fixed/Roaming P0 spine.** `TrackingControlStore` /
>   `TrackingControlWorker` (hourly `GET /devices/control`, `ALPHA_CONTROL_POLL_SEC`) +
>   pipe `GetTrackingState`; client `TrackingRunMode` / `TrackingModeGate` (always Full);
>   sync observes `controlEpoch`. No user Start/Stop yet (`newplan.md` P1/P2).
> - 2026-10-27: **Unsynced DLP alerts are kept until `acceptedIds` names them.** The 10-day
>   prune and the 50k cap are removed. An empty or missing `acceptedIds` does not delete the
>   batch. Evidence is deleted in the same SQLite transaction as its accepted alert.
>   `permission_status` rows with `is_synced = 0` are no longer deleted at one day.
>   Startup still drops `is_synced = 1` alerts, orphan evidence, audit past 200, and policy
>   snapshots past 3. Low disk space is an audit warning, not a purge.
> - 2026-10-27: **Protected uninstall can repair `device.auth`.** When the file is missing,
>   the interactive user's tracker SQLite (`aat_verify.db`, then `alpha_tracker.db`, or
>   `ALPHA_DB_PATH` under that user) supplies a non-empty `device_token`. The server URL is
>   the one already loaded from `config.enc`. `POST /devices/removal-verify` still runs.
> - 2026-10-27: **GUI update vs an older service.** `RequestUpdateCheck` `ok` polls
>   `GetUpdateStatus`. `unknown_op` (installed service `b965e5d` / 2.0.0) downloads the
>   bridge installer and starts it with `RunAs` after the published size matches.
>   `update_disabled`, an update already in progress, and a pipe timeout do not start a
>   second installer. There is still no Authenticode check.
> - 2026-10-27: **Local retention.** `FileLoggerProvider` truncates `dotnetrunlog.txt` on each
>   process start (`FileMode.Create`). Silent apply replaces `updates\update.log` instead of
>   appending. `PolicyStore` keeps the latest 3 `dlp\policy\policy.vN.json` snapshots, deletes
>   server-accepted alerts (no leftover `is_synced=1`), caps `enforcement_audit` at 200 rows, and
>   `VACUUM`s on startup when it deleted rows. Unsynced alerts are not deleted for age or count.
> - 2026-10-24: **Production reconcile for high-churn web block.** `PolicyGeneration` + `PinGate`
>   so `DnsBlockPinWorker` cannot recreate `AlphaAI-DLP-PIN-*` after Inactive; honest pin/FW
>   clear verify; `OsStateMatchesDesired` forces Apply on OS drift (`web_domain_reconcile`).
>   Pins live in memory + Windows Firewall only (not `dlp.db`); cleared by `PIN-` prefix.
>   Lab: `TESTING.md` §F.2.
> - 2026-10-24: **Removed localhost block page; fixed stale OS blocks after rule delete.** Dropped
>   `BlockPageServer`/`BlockHttpsServer`/`BlockPageCertStore`, tracker `WebBlockUxService` /
>   `IWebBlockHitSink`, `ALPHA_DLP_BLOCK_PAGE*`. Empty-policy clear + `HasLeftoverOsArtifacts`
>   self-heal (hosts / `AlphaAI-DLP-*` / URLBlocklist); no fingerprint advance on failed clear.
>   Lab: `TESTING.md` §F.1.
> - 2026-10-24: **P2 mid-session web block without employee restart.** `DnsBlockPinWorker` +
>   `DnsCacheReader` + `TcpConnectionKiller`; `DomainBlockEnforcer` incremental firewall / IP cache /
>   PIN rules / browser DoH+URLBlocklist; EnforceWorker re-applies hosts on every policy version.
>   No winsock/NIC reset. Lab: `TESTING.md` §F.
> - 2026-10-22: **Windows Security & DLP — LocalSystem service + protected uninstall (P0+P1).**
>   New project `client/DlpService` (`AlphaAITracker.Service.exe`, names from `APP_IDENTIFIERS`
>   `WINDOWS_SERVICE_NAME` / `WINDOWS_DLP_PIPE_NAME` / `APP_MUTEX`): PolicyPullWorker,
>   EnforceWorker + `UsbStorEnforcer` + `DomainBlockEnforcer` (P2 hosts DNS sink), RemovableDriveMonitor, AlertSyncWorker, NamedPipeHost,
>   ServiceAclHardener, TrackerWatchdogWorker, ProgramData store under
>   `%ProgramData%\<APP_MUTEX>\dlp\` (policy blob, local alerts, `service.env`, `device.auth`,
>   `teardown.ok`) — **never** multi-writes the user helper's SQLite. User helper:
>   `DlpDeviceCredentialBridge` after login, `DlpPipeClient`, `ProtectedUninstallService`
>   (`--protected-uninstall --code`), `WindowsTeardownCleanup`, CliConsole progress.
>   Installer: Inno publishes `Service\`, `sc create` + recovery, ACL lock, uninstall gated on
>   `teardown.ok`; `install-service.ps1` for lab. Knobs `ALPHA_DLP_*` (re-bake `config.enc`).
>   `ALPHA_DLP_ENABLED=false` parks all workers. Linux/macOS: no DLP service. See AGENTS.md
>   *DLP Policy Version Rule* / *Protected Removal Rule*; lab: `TESTING.md` + `client/build.md`.
> - 2026-10-19 (late): **Post-wipe re-login re-collects hardware / network / software; the Linux setup wizard can pass step 2.**
>   *Defect 1 ("No hardware snapshot synced yet" on a device that re-logged in after a removal):* a
>   removal wipe runs inside the ALREADY-RUNNING process — it deletes every SQLite row, but the
>   collector's in-memory dedup fingerprints (`_lastHardwareFingerprint`,
>   `_lastNetworkPublicIp`/`_lastNetworkPrivateIp`) survive it and the installed-software scan only
>   runs at STARTUP (inventory is event-driven, no periodic scan — user rule 2026-08-10). The machine
>   still "looks unchanged", so every periodic collect took the same-process fast path and the startup
>   scan never ran again: the machine id minted for the re-login received zero `device_hardware_info`,
>   `network_info`, `storage_devices` and `employee_installed_applications` rows (live evidence:
>   employee MU-90's third machine had sessions/items/status but no snapshot at all). New
>   `LogCollectorService.RecollectAfterLoginAsync()` drops the fingerprints (dedup then falls back to
>   the wiped, empty DB) and re-runs hardware → network → `RescanInventoryAsync`; `MainViewModel.LoginAsync`
>   fires it after `StartTracking()` and wakes `SyncService` again when it completes. Login also wipes
>   a leftover local DB belonging to a DIFFERENT employee BEFORE the login request (with
>   `ResetMachineId()` + `EncryptedConfigService.ResetCache()`), so the rotated machine id and the
>   server-side device registration always agree (R7). *Defect 2 (wizard stuck on SETUP STEP 2 OF 4 on
>   Linux):* step 2's gate `IsBackgroundGuardConfigured()` requires the systemd USER unit, but
>   `GrantBackgroundRunningAsync` only called `EnableAutoStartForced()` — which writes the `.desktop`
>   entry and runs `systemctl --user enable` against a unit file that does not exist yet — so the step
>   reported success and re-evaluated to itself forever. New `AutoStartService.EnsureBackgroundGuardAsync()`
>   writes the unit, daemon-reloads and enables it (deliberately NOT started: the wizard runs inside
>   the live process; starting it would spawn a mutex-fighting `Restart=always` twin); the step throws
>   honestly if that fails, and the macOS gate checks the launchd plist instead of always returning
>   false. Verified: `dotnet build` 0/0 **and** the full `AlphaAITracker-Setup-1.2.36.exe` installed
>   build — remove-account (machine `old`, employee `untracked`) → same-process GUI re-login minted a
>   new machine id and, WITHIN 12 SECONDS and without any restart, synced `device_hardware_info`,
>   `network_info`, `storage_devices` and 74 installed-app links for it (the exact sequence of
>   `RecollectAfterLoginAsync`), employee back to `tracked`.
> - 2026-10-19: **`machine_id` on every local table + one account-removal routine + server-initiated wipe.**
>   **Identity:** a new single-row `machine_identity` table (id=1, `machine_id`) holds THIS machine's
>   id, and every telemetry INSERT reads it in SQL —
>   `COALESCE((SELECT machine_id FROM machine_identity WHERE id = 1), '')` — so no store method needed
>   a new parameter. `machine_id TEXT NOT NULL DEFAULT ''` was added to `device_hardware_info`,
>   `storage_devices`, `installed_applications`, `installed_packages`, `network_info`,
>   `hardware_devices`, `session_events`, `app_items`, `app_status`, `permission_status`,
>   `employee_info`, `location_samples` and `daily_attendance_cache` (both in `CreateTableSql` for new
>   installs and in `MigrateSql` for existing DBs, plus an `app_items` backfill from its parent
>   session). `SqliteLogStore` takes a **lazy** `Func<string>` provider (`() => config.ClientId`) and
>   refreshes `machine_identity` at initialization AND on every login — the id is reset by a wipe, so a
>   value captured at construction would stamp the retired machine onto the fresh account.
>   `employee_info.machine_id` is written on login (R7 "save on both DBs").
>   `Core/DesktopEventBus/JourneyEngine` no longer stamps `Environment.MachineName` on journey
>   sessions — it uses `AppConfig.ClientId`, the same id every other surface uses.
>   **Removal:** new `Services/AccountRemovalService.cs` is the ONE removal routine — stop collecting
>   (`LogCollectorService.StopTracking`) → bounded `SyncService.WaitForDrainAsync`
>   (`ALPHA_REMOVE_ACCOUNT_SYNC_TIMEOUT_SEC`, default 120s) → `SyncService.SelfRemoveAsync`
>   (`POST /api/v1/devices/self-remove`, DeviceAuth) → wipe the SQLite DB + `.machine-id` /
>   device-id files + `ResetMachineId()` + `EncryptedConfigService.ResetCache()`. `SyncService` gained
>   `Action` parsing on `SyncBatchResponse` and `OnServerRequestedWipe`, raised **once** per process when
>   a sync answer carries `{"action":"wipe_local"}` (the admin removed this device; that batch is
>   already persisted server-side) — `Program.cs` wires it to the same service so a removed device
>   wipes itself and lands on the sign-in page. `WaitForDrainAsync` waits on a real per-pass
>   completion signal (`CompletePass(drainedAll)`); a pass is "drained" only when nothing failed and
>   the per-pass budget was not exhausted, and the not-logged-in branch publishes a trivially complete
>   pass so `--remove-account` can't sit out its whole timeout.
>   **`--remove-account`:** all entry points converge on the service. The cold-start/background path
>   now starts the host FIRST (the old code called `RequestImmediateSync()` before `host.StartAsync()`, so
>   the drain never ran and the server was never told), then drains, notifies, wipes, kills siblings and
>   stops the host; the running-instance branch still signals the live process, which runs the same
>   routine in place; `MainViewModel.RemoveAccountAsync` delegates too. Verified: `dotnet build` 0
>   warnings / 0 errors.
> - 2026-10-20: **Live stream Linux — capture + GStreamer VP8.**
>   `ScreenCaptureService` treats Linux as available: monitors via Mutter DisplayConfig;
>   `SetStreamActive(true)` starts `LinuxScreenCapturer` (embedded python3 ScreenCast → PipeWire
>   → GStreamer `pipewiresrc` → framed BGRA; no `videorate`). Restore token at
>   `~/.local/share/alpha-ai-tracker/stream-restore-token` (`persist_mode=2`). ABR downscale in
>   `CaptureFrameLinux`. CLI `--smoke-linux-capture`. **Encode:** Linux uses `LinuxGstVp8Encoder`
>   (`vp8enc`) — never link system libvpx as `vpxmd` (ABI segfault). Windows keeps `vpxmd`.
>   Verified: `dotnet build` 0/0; SFU `withTrack=true` for `LIN-1`. Next Linux installer.

> - 2026-10-15: **Live stream publisher — ICE buffered until the answer is applied (`LiveStreamClient`).**
>   The publisher applied each server `ice` message immediately, but the SFU trickles candidates from a
>   separate goroutine, so a candidate could arrive before the `answer`. `addIceCandidate` with a null
>   remote description throws and that candidate is dropped — ICE/DTLS then never completes even though
>   the publisher accepted the offer (watchers see LIVE with no frames). `LiveStreamClient` now keeps
>   `pendingIce` + `remoteIceReady`: the `answer` handler sets `remoteIceReady` when
>   `setRemoteDescription` returns `OK` and then flushes the buffered candidates; the `ice` handler
>   queues while `!remoteIceReady`; both are reset whenever the PC is recreated on `start`/`stop`.
>   `ApplyRemoteIce` was replaced by `BuildRemoteIce` (returns the candidate instead of applying it).
>   Ships in the next installer build. Verified: `dotnet build` 0 warnings / 0 errors.
> - 2026-10-03: **Win11 blank live preview (FA-27) + Client Version on DeviceAuth.**
>   FA-27 showed admin `LIVE` / `--- FPS` while SFU logged `withTrack=false`: Win11 GDI
>   `CopyFromScreen` / CAPTUREBLT-only BitBlt produced no frames → no RTP. **`ScreenCaptureService`**
>   now tries `SRCCOPY` BitBlt → CAPTUREBLT → `GetDesktopWindow` → `CreateDC("DISPLAY")` →
>   CopyFromScreen. **`LiveStreamClient`** gates `SendVideo` on PC `connected`, forces a keyframe
>   on connect, and writes `stream_pc_state` / `stream_ice_state` / `stream_capture_stall` /
>   `stream_capture_error` / encode-send errors to `app_status`. **`ClientIdentityHeaders`**
>   attaches `X-Client-Version` + `X-Client-Platform` on every DeviceAuth HTTP/WS call; boot
>   persists `app_status.client_version` so reinstall updates the web Client Version without
>   re-login. Installer **1.2.34**. Verified: FA-27 + MU-115 preview; `dotnet build` 0/0.
> - 2026-10-03: **Live stream V3 Phase 0/1 — ABR + uplink probe + PLI keyframe.**
>   `NetProbeService` POSTs a timed body to DeviceAuth `POST /api/v1/live-stream/uplink-probe`,
>   then `SelectBitrateKbps` picks a rung under `ALPHA_STREAM_MAX_BITRATE_KBPS`. Publish is skipped
>   when measured uplink < `ALPHA_STREAM_MIN_UPLINK_KBPS`. `LiveStreamClient` degrades on send
>   backpressure (bitrate → resolution → fps) and handles server `force_keyframe` ctrl (PLI).
>   Phase 0: `ScreenVp8Encoder` wrap-only (no free on managed image); ICE completion under session CT;
>   idle capture poll skipped when stream off. CLI: `--self-test-stream-ladder`. Re-bake `config.enc`
>   for new knobs. Verified: `dotnet build` 0/0; ladder self-test PASS.
> - 2026-09-29: **Live stream V2 — WebRTC VP8 publisher + presence WS + ScreenVp8Encoder.**
>   Replaces the JPEG push path. `LiveStreamClient` opens DeviceAuth WS `GET /api/v1/live-stream/push`,
>   publishes SIPSorcery WebRTC VP8 after server `{"type":"start"}`, stops on `stop`. Capture remains
>   Windows GDI via `ScreenCaptureService` (max width hard-capped 1920). **`Services/Streaming/ScreenVp8Encoder.cs`**
>   is required: SIPSorceryMedia `Vp8Codec.InitialiseEncoder` set `RcTargetBitrate` then called
>   `VpxCodecEncConfigDefault` (wiping bitrate → ~256 kbps). Our encoder applies defaults first, then
>   bitrate/VBR/timebase/`KfMaxDist`, with ~1s forced keyframes (`ALPHA_STREAM_KEYFRAME_INTERVAL_SEC`).
>   **`WsClient`** is a separate hosted service → `GET /api/v1/ws` (`ALPHA_WS_*`) for Online/Offline;
>   never mixed with the media socket. Env (re-bake `config.enc`): `ALPHA_STREAM_ENABLED` (default off),
>   `ALPHA_STREAM_FPS`, `ALPHA_STREAM_MAX_WIDTH`, `ALPHA_STREAM_MAX_BITRATE_KBPS` (cap 15000),
>   `ALPHA_STREAM_KEYFRAME_INTERVAL_SEC`, `ALPHA_WS_ENABLED`, `ALPHA_WS_PING_SEC`, `ALPHA_WS_RECONNECT_BASE_SEC`.
>   NuGet: `SIPSorcery` + `SIPSorceryMedia.Encoders` (native `vpxmd`). Linux capture landed 2026-10-20.
> - 2026-09-05: **Core: Windows `power_off` event now fires on shutdown/restart — `SystemEventWatcher` subscribes to `SystemEvents.SessionEnding`.**
>   The Windows half of `SystemEventWatcher` was missing `SystemEvents.SessionEnding`, so shutdown/restart never emitted `power_off` (sleep/resume via `PowerModeChanged` worked; lock/unlock via `SessionSwitch` worked; power_on worked via `LogCollectorService` on boot). `SessionEnding` with `SessionEndReasons.SystemShutdown` is now subscribed in `SubscribeWindows()` (fire-and-forget, mirrors Linux's synchronous `PrepareForShutdown` handler), with matching unsubscription in `UnsubscribeWindows()`. `Logoff` is intentionally skipped to avoid duplicating the existing `SessionSwitch` → `os_logout` path. `ShutdownSentinel` remains as fallback. This makes Windows match Linux's two-layer power-off detection pattern. Verified: `dotnet build` 0/0, 0 warnings. Real-world test requires a Windows shutdown/restart cycle against an installed build.
>   The accessibility reader returns a fresh `WindowKey` per tab; the old title-only collapse rule let
>   transient "Loading…" states slip through and opened a separate `app_sessions` row per tab — which
>   then summed on the web to 3× the real open time. Two new collapse rules, layered BEFORE the
>   single-window count guard: (a) **exact URL match** (stronger than title — kills the
>   "3 tabs all titled 'Loading…'" transient case; both sides must have a non-empty URL to match);
>   (b) **fresh-key recency window of `poll×2` seconds** — if a same-PID tracked window was seen
>   within the recency window, the incoming key is treated as churn of the FRESHEST such window.
>   The recency guard is what keeps two real long-lived windows from collapsing: when both are old,
>   both `LastSeen` are far in the past and the guard fails on every candidate, so a new session
>   is opened instead. Existing title-match and single-window-count rules stay as primary
>   fallbacks. The reader's per-OS WindowKey generation is unchanged — this is purely a tracker-
>   side identity-resolution fix. Layer 1 of 3 for the "chrome 3 tabs × 10 min renders as 30m on
>   the web" bug; layers 2 (web defense in depth) and 3 (server `GET /app-sessions/usage`) ship
>   independently so the page is correct even before the next installer build carries this fix.
>   No env knob; no DDL change. Verified: `dotnet build` 0/0, 0 warnings.
> - 2026-09-02: **Client: app_sessions last_activity_at tracking for the 3-state server lifecycle.**
>   `Core/Models/AppSession.cs` gained a `DateTime? LastActivityAt` property. `Storage/DatabaseSchema.cs` adds the column via an idempotent `MigrateSql` ALTER, refreshes it on `InsertAppSessionSql` (new session = `log.Timestamp`), on the `CONFLICT` update, on `UpdateAppSessionFocusSql` (sets it to NOW() on every focus change), and on `UpdateAppSessionEndedSql` (freezes it to the close moment). `SqliteLogStore.cs` carries the new parameter through the batched INSERT and reads it back in `MapAppSessionReader`. `Services/LogCollectorService.cs` stamps `LastActivityAt = log.Timestamp` when opening a new session, so the server's `session_lifecycle_sweep` has a meaningful `last_activity_at` value to freeze at CLOSE time. `Services/SyncService.cs` includes `lastActivityAt` in the `POST /api/v1/app-sessions/sync` payload (falling back to `startedAt` when null so older calls still validate). The client's local SQLite is the source of truth for activity history; the server is the source of truth for the projected `status` column. No env knob required on the client.
> - 2026-08-22: **Flatpak `bwrap` PPID chain walk in embedded Python probe.**
>   `resolve_app_name(pid)` in `LinuxAtSpiBrowserReader.cs` now detects when the AT-SPI PID belongs to Flatpak's `bwrap` or `xdg-dbus-proxy` and walks up the PPID chain (via `/proc/<pid>/stat`) until it reaches the real app process. The FLATPAK_ID / snap / comm resolution then runs against the real PID, so Floorp/LibreWolf/Waterfox Flatpak installs resolve to their short app ID (`floorp`, `librewolf`, `waterfox`) instead of the proxy name. Verified: `dotnet build` 0/0, 0 warnings.
> - 2026-08-21: **All hardcoded browser names removed — dynamic `IBrowserRegistry` replaces `BrowserProcessHints` / `IsBrowserProcess` / `BROWSER_HINTS` / `ResolveFamily`.**
>   New `IBrowserRegistry` interface + `BrowserRegistry` implementation source browser classification from `installed_applications.is_browser` (set via `.desktop` `Categories=WebBrowser`, Windows registry `URLAssociations`, macOS `CFBundleURLSchemes`) and cache it for 5 minutes. Every reader, tracker, history fallback, session label resolver, and the main collector loop now resolves browsers through the registry instead of hardcoded name lists. The Linux embedded Python probe's `BROWSER_HINTS` tuple is deleted; the probe uses `_browser_exes` (already built from `.desktop` files) for both AT-SPI browser detection and Firefox sessionstore discovery. `StripBrowserSuffix` is now dynamic — it strips ` - {appDisplayName}` using the registry's display name instead of a 10-entry hardcoded suffix list. `BrowserHistoryReader.ResolveFamily()` is removed; URL resolution searches all visits without a family filter (exact-title match is the primary signal). `SessionLabelResolver` accepts the registry as a parameter and runs profile extraction (`--profile-directory`, `-P`, `--profile=`) for ANY browser the registry knows, not just a hardcoded Chromium-family switch. `InstalledAppDetector.ScanStartMenuShortcuts` no longer pre-classifies shortcuts as browsers — the subsequent registry scan sets `IsBrowser` from URL associations. `IBrowserRegistry` is registered unconditionally in DI (outside the `BrowserTrackingEnabled` guard) because `LogCollectorService` and `SessionLabelResolver` consume it regardless of the tracking master switch. Verified: `dotnet build` 0/0, 0 warnings; zero remaining references to `BrowserProcessHints`, `IsBrowserProcess`, `BROWSER_HINTS`, `ResolveFamily`, or `ResolveChromeProfile` in the client.
> - 2026-08-18: **Embedded-webview journeys (VS Code Simple Browser, Electron apps) — structural detection, no hardcoded names.**
>   Readers no longer gate on a browser-process list alone: any window whose a11y tree exposes a
>   DOCUMENT_WEB node (AT-SPI role 95) with an **http(s) DocURL** is tracked (UIA on Windows: a
>   descendant Document/Edit whose Value/Name is an http URL). App chrome excludes itself by URL scheme
>   (`vscode-webview://`, `file://`, `about:`). Metadata `source="webview"` + the host process name let
>   the dashboard show the source app. Linux scans non-browser apps every 5th poll (400-node budget) and
>   caches/re-emits webview windows each poll so sessions never falsely close; `--type=` child processes
>   are skipped structurally. The tracker hydrates browser_tab-rooted sessions, and the main loop skips
>   them too (webview session + host-app session coexist; no duplicate-close).
> - 2026-08-18: **Structural process name resolution — Flatpak/snap browsers get correct names + structural browser detection.**
>   The Python probe's `resolve_app_name(pid)` now walks up the `bwrap`/`xdg-dbus-proxy` PPID chain (via `/proc/<pid>/stat`) to reach the real app process before checking FLATPAK_ID in `/proc/<pid>/environ` (extracts short name: `org.mozilla.Floorp` -> `floorp`) and snap path in `/proc/<pid>/exe` (`/snap/firefox/...` -> `firefox`), falling back to `/proc/comm` for native apps. No name lists: Flatpak/snap are the only sandboxing systems that inject proxy PIDs (e.g. `xdg-dbus-proxy`). Structural browser detection: `.desktop` files with `Categories=WebBrowser` are scanned (cached 5 min) and the resolved process name is matched against browser exe names from the desktop files + Flatpak app IDs. C# `ReadComm()` checks a `_pidNameCache` (populated from probe results each poll) before `/proc/comm`, so WM-only Flatpak/snap windows also get the correct name. `StripBrowserSuffix` gains Floorp/LibreWolf/Waterfox.
> - 2026-08-18: **Focus totals frozen (~0s on the web) fixed — flush is now ADDITIVE + every-minute push.**
>   Root cause: `UpdateAppSessionFocusSql` OVERWROTE the row with the in-memory delta and the counter was
>   then cleared — each flush wrote only the last ~10-cycle window (300s main / 30s browser), never the
>   session total (live evidence: Chrome frozen at exactly 30.0, others at exactly 300.0, identical across
>   65s). Fix: `fg = COALESCE(fg,0) + $fg` — the in-memory dict stays a per-flush DELTA (cleared after
>   every write, both loops), so SQLite holds the true cumulative total, restart-safe, re-sent verbatim
>   (server upsert overwrites). Close paths flush first then close with NULL (`COALESCE($fg, fg)`) — no
>   double-counting. Push cadence: main loop flushes focus every 2 cycles (~60s at the default 30s
>   interval) and `SyncService` failure backoff is capped at 60s, so every `is_synced=0` row reaches the
>   server within a minute even during repeated failures.
> - 2026-08-18: **Foreground/background focus times fixed — Linux/Wayland detection + browser sessions.**
>   (1) `ProcessCollector` Linux AT-SPI foreground detection rewritten: at-spi2-core ≥ 2.50 returns
>   `GetState` as a **packed 64-bit bitmask** (bit N = state N) not a list of ids, and the old check
>   looked for state 8 (ENABLED, present on every window) — the focused window is the only one with
>   bit 1 (STATE_ACTIVE)/bit 12 (STATE_FOCUSED). New `IsAtSpiActiveState` (gdbus path) and
>   `is_active_window` (python path) decode both formats. This also makes the browser reader's
>   per-window `active` flag work (the focused browser window is now identifiable on Wayland).
>   (2) `AccessibilitySnapshot.IsActive` added and set per platform (Linux AT-SPI ACTIVE/FOCUSED,
>   Windows `GetForegroundWindow` HWND, macOS frontmost process); `AccessibilityBrowserTracker` now
>   accumulates the poll interval per open window every poll (active → `foreground_seconds`, rest →
>   `background_seconds`) and flushes every 10 polls + on close via `UpdateAppSessionFocusAsync`
>   (re-queues `is_synced=0` so SyncService re-sends) — browser journeys finally earn focus time.
> - 2026-08-12: **Self-update from GitHub Releases** — new `Services/AppUpdateService.cs`
>   (singleton + hosted, ObservableObject): checks the GitHub latest-release API, picks the platform
>   installer asset (`.deb` by arch / `.exe` / `.dmg`), compares against `AppInfo.Version`, and on a
>   newer version either auto-installs (`ALPHA_UPDATE_AUTO_INSTALL=true` default — Linux
>   `pkexec dpkg -i`, Windows silent Inno which force-closes the app then a detached `.cmd` relaunches
>   it, macOS `open` dmg) or surfaces a GUI banner + top-bar **Check updates** / **Update to vX.Y.Z** /
>   **Restart to apply** buttons. Downloads stream to `~/.local/share/alpha-ai-tracker/updates`
>   (never the install dir). Background loop checks every 30 min, gated by persisted
>   `update_last_check_at` ≥ `ALPHA_UPDATE_AUTO_CHECK_HOURS` (24h). New   `--restart` CLI arg: the
>   single-instance mutex is retried for 8s instead of signal-and-exit (post-update relaunch). Full
>   detail in §18.
> - 2026-08-10: **Employee disconnect removed.** The nav-rail **Disconnect** button, `LogoutCommand`/
>   `LogoutAsync` and `LogCollectorService.StopTracking()` are deleted; the client no longer calls
>   `POST /api/v1/auth/employee-disconnect`. Once `IsProfile` is reached the shell has no in-app
>   logout — tracking runs until the process stops. No `logout` session event is emitted anymore
>   (only `login`, from `StartTracking`), and the Windows anti-sleep state now persists for the whole
>   process lifetime (the OS clears it at process exit — no leak). `ClearEmployeeInfoAsync` remains
>   on `ILogStore` (store primitive).
> - 2026-08-10: **Six-page GUI rewrite + runtime branding pipeline.** `MainWindow.axaml` is now a
>   ~230-line **router**; every screen lives in `Views/Pages/` as its own `UserControl` (Splash, Login,
>   PermissionSetup, Dashboard, SystemSpecs, InstalledApps). Three new page ViewModels
>   (`DashboardViewModel`, `SystemSpecsViewModel`, `InstalledAppsViewModel`) joined `MainViewModel` in DI
>   as Transient. `Styles/AppTheme.xaml` flipped from a dark palette to a **light design-token
>   dictionary** (colors, radii, shadows, vector icon geometries); `App.axaml` carries ~30 style classes
>   and 5 animations. New: `Core/AppInfo.cs` reads `client/APP_IDENTIFIERS` (embedded resource) +
>   `client/VERSION` (informational version) so **every visible brand string and version is derived at
>   runtime** — no product name is baked into XAML or C#. Full UI reference: **[UI_ARCHITECTURE.md](UI_ARCHITECTURE.md)**.
> - 2026-08-07: **Docs re-audit & rewrite.** Re-verified every claim against source. Corrected stale
>   references: `TrayAvailability.cs` and `SingleInstance.cs` do not exist (`SingleInstanceService.cs`
>   does); the tray menu has **Show / Hide only (no Quit item)**; `extensions/`, `native-host.py`,
>   `NativeMessageService`, `BrowserExtensionService` and `install-extensions.sh` are gone from the
>   product; the permission wizard is now **4 steps on Linux / 3 elsewhere** (Auto-Start → Background
>   Guard → [Linux-only] Dependencies → Other Permissions) and always re-validates the real condition.
>   Documented the current 11-table schema, the joint software classifier, the sync ordering
>   (app-sessions BEFORE app-items), the headless `--background` service mode, and the exact env keys.
> - 2026-08-07: **Private/incognito window URLs captured.** (1) `AccessibilityBrowserTracker` rotates on
>   title change after a bounded ~25s `historyLag` timeout even with no URL (was: held forever waiting
>   for history — private windows froze rotation). (2) `LogCollectorService` no longer closes browser
>   sessions owned by the accessibility tracker (browser-owned sessions excluded from the close phase —
>   they were being silently closed 4–40s after opening). (3) Linux URL sources: **Firefox** builds its
>   full AT-SPI tree on demand, so `LinuxAtSpiBrowserReader` reads the `DOCUMENT_WEB` (role 95) node's
>   `DocURL` attribute — the EXACT private-window URL; **Chrome 136+** only builds the tree when launched
>   with `--force-renderer-accessibility` (the tracker ships a user-level `google-chrome.desktop` that
>   carries the flag on every Exec line). Verified end-to-end in the INSTALLED build.
> - 2026-08-07: **Killed the `dpkg-query: no packages found matching ${Version}` startup noise** — the
>   `-f` format string is now double-quoted so `\t`/`\n` escapes reach dpkg-query as ONE argument, and
>   stderr is drained.
> - 2026-08-07: **Quiet terminal** — per-event browser-journey logs demoted to Debug (visible with
>   `ALPHA_LOG_LEVEL=debug`); startup banner stays at Information.
> - 2026-08-07: **Headless `--background` service mode** — `Program.cs` no longer initializes the
>   Avalonia/X11 UI at boot. Manual launches signal that process to create the UI lazily. Linux user
>   units inherit the graphical environment from the systemd user manager instead of hardcoding
>   `DISPLAY` / `XAUTHORITY`; `BackgroundGuardService` removes legacy `~/.Xauthority` overrides and
>   performs one systemd-only restart so GNOME Wayland's rotating Mutter cookie takes effect.
> - 2026-08-06: **Hybrid URL fallback — browser profile History reader** (`BrowserHistoryReader.cs`).
>   Reads Chromium `History` / Firefox `places.sqlite` while the browser runs (safe copy + `-wal`/`-shm`/
>   `-journal` sidecars, read-only, signature-throttled); resolution is STRICTLY title-match; generic
>   profile discovery catches brand-new browsers. Tags `metadata_json.source="history"` vs `"a11y"`.
> - 2026-08-05: **Option B — accessibility-tree browser journey** (the debugger pipeline `Core/Browser/*`
>   — 16 files — was DELETED). New `Core/BrowserAccessibility/` reads the OS accessibility tree (AT-SPI
>   / UIA / AX) — no debugger, no extension, no browser-catalog dependency. `AccessibilityBrowserTracker`
>   polls every `ALPHA_BROWSER_ACCESSIBILITY_POLL_SECONDS` (3), one `app_sessions` per window +
>   `browser_tab`/`browser_navigation`/`browser_download` `app_items`, idle-close, graceful shutdown,
>   incognito gated by `ALPHA_BROWSER_CAPTURE_INCOGNITO` (default off). Dead code removed: shell-command
>   collectors, `publish/install-extensions.sh`, the `extensions/` bundling step.
> - 2026-08-03: **Single-instance + tray UX; Installer-Parity rule** — second launch restores the running
>   window via `SingleInstanceService` named-pipe signalling; `--background` runs headless for systemd.
>   Added the mandatory **Build-Parity Rule** (§15): `dotnet run` is not a valid release test.
> - 2026-08-01 … 2026-07-27: See prior entries below (kept for history — all still accurate).
>   Notable: GUI-apps-only tracking gate (07-30), cgroup-based session dedup (07-31), software
>   classification pipeline (07-30), file-explorer journey (07-29), atomic cascade-close (07-31),
>   crash-safe session ended_at tracking (07-28), SQLite concurrency gate (07-30), GNOME-daemon
>   contamination fix (07-29).
> **Service completion (honest):** ~85%

---

## 1. Responsibility & Scope

**Owns:**
- Collecting process activity (window titles, CPU%, memory) from employee machines via per-platform collectors
- Capturing the **browser journey** from the OS accessibility tree (exact active-tab URL + title) + profile-history fallback
- Capturing the **file-explorer journey** via an event-driven desktop event bus (AT-SPI + FileSystemWatcher + XBEL)
- Building a **software inventory** (GUI apps in `installed_applications`, CLI tools/runtimes/libraries in `installed_packages`)
- Collecting **system info** (device hardware incl. storage/GPU, network info, session events, permission status)
- Storing everything locally in SQLite before batched sync to the central server
- Managing the employee login lifecycle (JWT in request body) and the post-login **permission wizard**
- Keeping the app alive (auto-start + background-guard watchdog), single-instance signalling, tray, headless systemd mode
- **Windows DLP (separate process):** SYSTEM service pulls published policy, enforces USBSTOR, queues/syncs DLP alerts, ACL-hardens stop/uninstall; user helper bridges DeviceAuth credentials and runs protected-uninstall CLI

**Does NOT own:**
- Any business logic about "productive" vs "unproductive" activity
- User/department/admin management (server + web)
- Data persistence beyond local buffering (server is the source of truth) — except the DLP service's own ProgramData SQLite/files
- Any web-facing display or analytics
- Shell/terminal **command history** collection — removed; shells themselves are only tracked as *children* of terminals/IDEs
- Linux/macOS DLP block / protected service (Windows-only for P0–P5)

---

## 2. Tech Stack Detail

| Component | Technology | Version |
|---|---|---|
| **Language / TFM** | C# (.NET) | net10.0 |
| **UI Framework** | Avalonia | 12.1.0 |
| **Desktop / Theme / Fonts** | Avalonia.Desktop / Avalonia.Themes.Fluent / Avalonia.Fonts.Inter | 12.1.0 |
| **MVVM Toolkit** | CommunityToolkit.Mvvm | 8.4.2 |
| **SQLite Driver** | Microsoft.Data.Sqlite | 10.0.10 |
| **SQLite Bundle** | SQLitePCLRaw.bundle_e_sqlite3 | 2.1.12 |
| **DI/Hosting** | Microsoft.Extensions.Hosting | 10.0.10 |
| **Registry (Windows)** | Microsoft.Win32.Registry | 5.0.0 (windows only) |
| **D-Bus** | Tmds.DBus.Protocol | 0.94.2 |
| **UIA (Windows reader)** | Interop.UIAutomationClient | 10.19041.0 |
| **Diagnostics** | AvaloniaUI.DiagnosticsSupport | 2.2.3 (Debug only) |
| **HTTP Client** | System.Net.Http (built-in) | — |

### Notable Omissions

- **No ORM** — raw parameterized SQL via `SqliteCommand`
- **Sparse tests** — `client/client.Tests` covers a few DLP/teardown helpers; collectors largely untested
- **No structured logging beyond ILogger** — console (dev) + `FileLoggerProvider` → `dotnetrunlog.txt`
- **No third-party auto-updater library** — the built-in `AppUpdateService` + Windows `SilentUpdateWorker` self-update from the API `setup/` folder (`GET /api/v1/releases` + `/downloads`); publish via `release.sh`

---

## 3. Project Structure (verified against source)

```
client/
├── Program.cs                      # Entry point. CLI modes (--encrypt-config, --print-config,
│                                   #   --background, --minimized), mutex + single-instance pipe,
│                                   #   DI container, log/db path resolution
├── App.axaml / App.axaml.cs        # Avalonia app, tray icon (Show/Hide), window-close hides to tray,
│                                   #   single-instance SHOW handling, DI-resolved MainViewModel
├── SingleInstanceService.cs        # (in Core/) named-pipe signalling ("alpha-ai-tracker-activation")
├── app.manifest                    # Windows compatibility manifest
├── ViewLocator.cs                  # ViewModel → View resolution (reflection, simple replacement)
├── client.csproj                   # net10.0, Avalonia 12.1.0, DefaultServerUrl AssemblyMetadata,
│                                   #   VERSION → Version/InformationalVersion (read at evaluation time),
│                                   #   APP_IDENTIFIERS → EmbeddedResource "client.APP_IDENTIFIERS"
├── APP_IDENTIFIERS                 # ⭐ Single source of truth for branding (13 KEY="value" pairs).
│                                   #   Consumed at BUILD time by publish/* and at RUNTIME by Core/AppInfo.cs
├── VERSION                         # ⭐ Single source of truth for the version string (currently 0.2.0)
├── APP_IDENTIFIERS_README.md       # How to re-brand; lists every consumer
├── VERSION_README.md               # How to bump the version
├── appsettings.json                # Logging config (largely unused — level comes from ALPHA_LOG_LEVEL)
├── .env / .env.example             # Dev plaintext config template (ALPHA_SERVER_URL + ALPHA_UPDATE_*)
│
├── Configuration/
│   ├── AppConfig.cs                # Reads all ALPHA_* env vars into typed config (see §14)
│   └── EnvLoader.cs                # Multi-source config loading: dev .env vs installed config.enc,
│                                   #   shipped-config propagation, machine-key migration, secure wipe
│
├── Core/
│   ├── Abstractions/
│   │   ├── IActivityCollector.cs   # CollectAsync() → ActivityLog[]
│   │   ├── ILogStore.cs            # ~40 methods: store/unsent/mark-sent × 8 tables + lookups + journey + close paths
│   │   ├── IInstalledAppDetector.cs# GUI app detection + MissingPermissions / grant instructions
│   │   └── IPackageDetector.cs     # CLI tool/runtime/library detection from package managers
│   ├── BrowserAccessibility/       # ⭐ Browser journey (Option B)
│   │   ├── IBrowserRegistry.cs         # interface: IsBrowser / GetDisplayName / GetAllBrowserProcessNames / GetAllBrowserDisplayNames
│   │   ├── BrowserRegistry.cs          # implementation: caches IsBrowser apps from IInstalledAppDetector, refreshes every 5 min
│   │   ├── IAccessibilityBrowserReader.cs   # snapshot contract (Platform, IsAvailable, ReadAsync)
│   │   ├── AccessibilitySnapshot.cs         # windowKey, pid, process, title, Url, UrlSource, incognito, IsActive, IsWebview
│   │   ├── LinuxAtSpiBrowserReader.cs       # ONE embedded python3 probe: AT-SPI + Firefox sessionstore (mozLz4) + WM window list
│   │   ├── WindowsUiaBrowserReader.cs       # UIA via Interop.UIAutomationClient
│   │   ├── MacOsAccessibilityBrowserReader.cs # osascript/System Events (Accessibility grant required)
│   │   ├── AccessibilityBrowserReaderFactory.cs # platform picker
│   │   ├── BrowserHistoryReader.cs          # profile History/places.sqlite URL fallback (copy+WAL, title-match)
│   │   ├── AccessibilityBrowserTracker.cs   # BackgroundService: poll → enrich → sessions/items → idle/vanished close → downloads
│   │   └── BrowserAccessibilityHelpers.cs   # NormalizeUrl, ExtractDomain, StripBrowserSuffix(title, appDisplayName), TitleSuggestsIncognito, StableInt32
│   ├── DesktopEventBus/            # ⭐ File-explorer journey
│   │   ├── IObservableEventSource.cs   # common watcher contract (SourceName, IsActive, EventRaised, Start/Stop)
│   │   ├── RawDesktopEvent.cs          # raw OS-level event from a watcher
│   │   ├── DesktopEvent.cs             # normalized business event (after coordinator)
│   │   ├── DesktopEventValidator.cs    # file-manager detection, ignored-process prefixes, path validity
│   │   ├── EventCoordinator.cs         # dedup (3s) + correlate (500ms) + journey records + normalize
│   │   ├── JourneyEngine.cs            # resolve/create AppSession → file_manager_tab root + fm_* items (9 journey fields)
│   │   └── JourneyRecord.cs            # in-memory journey state
│   ├── Models/                     # ActivityLog, AppSession/AppItem (journey fields), DeviceHardwareInfo,
│   │                               #   InstalledApplication, InstalledPackage, NetworkInfo, StorageDevice,
│   │                               #   HardwareDevice, SessionEvent, EmployeeInfo, SessionInfo (per-launch GUID)
│   ├── AppInfo.cs                  # ⭐ Runtime branding accessor — parses the embedded APP_IDENTIFIERS
│   │                               #   (strict regex, never executed) + reads VERSION from the assembly's
│   │                               #   InformationalVersion. Every brand string in the UI comes from here.
│   ├── SingleInstanceService.cs    # Named-pipe signalling ("alpha-ai-tracker-activation")
│   ├── EncryptedConfigService.cs   # AES-256-GCM: transport key → machine-derived key; fallback .machine-id
│   │                               #   ⚠️ TransportKeySeed / MachineKeyPrefix are KDF seeds — never templatize
│   ├── CgroupResolver.cs           # /proc/<pid>/cgroup → systemd app-*.scope (session dedup key)
│   ├── SessionLabelResolver.cs     # context_label: VS Code workspace folder / Chrome --profile-directory
│   ├── InstalledAppDetector.cs     # GUI apps: .desktop (XDG), registry/Start Menu (Win), .app bundles (mac)
│   ├── PackageDetector.cs          # npm, pip, dpkg/apt, snap, flatpak, brew, macports, choco, winget, scoop
│   ├── SoftwareCategoryResolver.cs # canonical categories from .desktop Categories / bundle id
│   ├── SoftwareClassifier.cs       # joint dedup pipeline — GUI apps win over matching package entries
│   ├── SoftwareIdentityResolver.cs # SHA-256 stable identity for cross-source dedup
│   ├── ExecutableMetadata.cs       # publisher/product strings read off the binary itself (no name lists)
│   ├── CollectionExtensions.cs     # small LINQ-ish helpers shared by the resolvers
│   ├── ProcessFilter.cs            # kernel/system process filtering (names, prefixes, session, window, age)
│   ├── ParentProcessResolver.cs    # PPID tree, window-title resolution, cmdline, browser profile extraction
│   ├── AppProcessClassifier.cs     # category + root item_type resolution, headless --type= filter
│   ├── ActivityContextParser.cs    # URL/path/title parsing, dynamic browser-suffix stripping
│   └── SessionHierarchyResolver.cs # PID-tree parent linking (node → terminal → IDE)
│
├── Platform/
│   ├── Windows/ProcessCollector.cs # User32 EnumWindows (all titles), GetForegroundWindow, CPU 2-sample, PS cmdline
│   ├── MacOS/ProcessCollector.cs   # osascript foreground only. ⚠️ No CPU measurement
│   └── Linux/ProcessCollector.cs   # multi-strategy foreground + GNOME Shell Introspect / xprop window lists
│
├── Services/
│   ├── LogCollectorService.cs      # ⭐ main BackgroundService: collect → resolve → sessions/items → heartbeat (no network I/O since 2026-08-11)
│   ├── SyncService.cs              # ⭐ dedicated sync engine: drains unsent rows in byte-bounded chunks (gzip, polite pauses, exponential backoff)
│   ├── AppUpdateService.cs         # ⭐ self-updater: GET /api/v1/releases + /downloads; Windows queues SilentUpdateWorker; Linux pkexec dpkg / macOS dmg; ObservableObject for GUI Check updates
│   ├── LiveStreamClient.cs         # ⭐ WebRTC VP8 publisher → GET /live-stream/push; ABR; send after PC connected
│   ├── NetProbeService.cs          # Uplink probe → POST /live-stream/uplink-probe; SelectBitrateKbps ladder
│   ├── ScreenCaptureService.cs     # Win GDI BitBlt + Linux portal/PipeWire → BGRA (DropOldest)
│   ├── ClientIdentityHeaders.cs    # X-Client-Version / X-Client-Platform on DeviceAuth HTTP+WS
│   ├── WsClient.cs                 # Presence / keep-alive WS → GET /api/v1/ws (independent of LiveStreamClient)
│   ├── Streaming/
│   │   ├── ScreenVp8Encoder.cs     # Windows: vpxmd (ConfigDefault-then-bitrate); Linux: LinuxGstVp8Encoder
│   │   ├── LinuxGstVp8Encoder.cs   # Linux VP8 via GStreamer vp8enc (python3 helper)
│   │   └── LinuxScreenCapturer.cs  # Wayland ScreenCast portal → PipeWire/GStreamer → framed BGRA

│   ├── DesktopEventService.cs      # ⭐ orchestrates file-explorer watchers → coordinator → JourneyEngine
│   ├── BackgroundGuardService.cs   # watchdog: re-installs auto-start/systemd unit if removed (60s)
│   ├── AutoStartService.cs         # Run key / ~/.config/autostart .desktop / launchd plist
│   ├── FileLoggerProvider.cs       # dotnetrunlog.txt replaced on each start (null sink last)
│   └── Watchers/
│       ├── ATSPIEventWatcher.cs    # Tmds.DBus.Protocol → AT-SPI focus:/window: events + /proc/cwd path
│       ├── FileSystemEventWatcher.cs # FileSystemWatcher on 6 user dirs (exclusion list, debounce)
│       └── RecentFilesWatcher.cs   # XBEL monitor (~/.local/share/recently-used.xbel)
│
├── Storage/
│   ├── DatabaseSchema.cs           # CreateTableSql (11 tables) + MigrateSql (idempotent ALTERs + dedup) + insert SQL
│   └── SqliteLogStore.cs           # ILogStore impl: SemaphoreSlim(1,1) gate, PRAGMA busy_timeout, atomic cascade-close
│
├── ViewModels/                     # ⭐ One VM per concern — see UI_ARCHITECTURE.md
│   ├── ViewModelBase.cs            # ObservableObject base
│   ├── MainViewModel.cs            # Shell VM (~1150 lines): splash sequence, login, permission
│   │                               #   wizard, AppPage router state, branding passthrough to AppInfo
│   ├── DashboardViewModel.cs       # Page 4 — identity + status tiles, pipeline health, attached devices
│   ├── SystemSpecsViewModel.cs     # Page 5 — machine/compute/network, storage + peripherals
│   └── InstalledAppsViewModel.cs   # Page 6 — apps + packages inventory, search, InventoryRow projection
│
├── Views/
│   ├── MainWindow.axaml(.cs)       # ⭐ ROUTER ONLY — 4 top-level states + nav rail; splash on Opened,
│   │                               #   90%-of-working-area sizing, close-hides-to-tray
│   └── Pages/                      # One UserControl per screen (.axaml + .axaml.cs each)
│       ├── SplashPage              # 1 — boot checklist + determinate progress
│       ├── LoginPage               # 2 — artwork panel + credential card
│       ├── PermissionSetupPage     # 3 — stepper (4 steps Linux / 3 elsewhere)
│       ├── DashboardPage           # 4 — operational overview
│       ├── SystemSpecsPage         # 5 — system specifications
│       └── InstalledAppsPage       # 6 — installed applications & packages
│
├── Converters/                     # BoolInvert, StringNotEmpty, LoadingToText, PercentToGridLength
├── Styles/AppTheme.xaml            # ⭐ Light design-token dictionary: palette + rail tokens + badge
│                                   #   surfaces + 4 shadows + 5 radii + 9 StreamGeometry icons + Inter fonts.
│                                   #   No product name here — branding is resolved at runtime.
├── App.axaml                       # ~30 style classes (card/nav/badge/table/h1/label/ghost…) + 5 animations
├── Assets/
│   ├── avalonia-logo.ico, icon.png
│   └── backgrounds/                # dashboard-hero.png, login-hero.png, specs-hero.png, apps-hero.png
│                                   #   (covered by the existing <AvaloniaResource Include="Assets\**" />
│                                   #    glob → compiled into client.dll, so installer parity is automatic)
├── installers/                     # Built artifacts, version-stamped from VERSION
└── publish/                        # build-installer.sh, encrypt-config.sh, firefox-a11y-apparmor.sh,
                                    #   release.sh, build-deb.sh, build-dmg.sh, generate-windows-vars.sh,
                                    #   installer-windows.iss, linux/, macos/, windows/
```

> **Adding a screen, a style class, or a design token?** Read
> **[UI_ARCHITECTURE.md](UI_ARCHITECTURE.md)** — it documents the token set, every style class, the
> router contract, the binding patterns, and the branding pipeline.

---

## 4. MVVM Layering & DI

### Layers

```
Views (XAML)          ──►  ViewModels                ──►  Models (plain DTOs)      ──►  Services / Infrastructure
  MainWindow (router)        MainViewModel                  ActivityLog, AppSession,      LogCollectorService
  Pages/SplashPage           ├─ splash / login / wizard     AppItem, DeviceHardwareInfo,  DesktopEventService
  Pages/LoginPage            ├─ AppPage router state        InstalledApplication,         AccessibilityBrowserTracker
  Pages/PermissionSetupPage  └─ branding → Core/AppInfo     InstalledPackage, NetworkInfo, SqliteLogStore
  Pages/DashboardPage        DashboardViewModel             SessionEvent, StorageDevice,  platform collectors
  Pages/SystemSpecsPage      SystemSpecsViewModel           HardwareDevice                HardwareDeviceWatcherService
  Pages/InstalledAppsPage    InstalledAppsViewModel                                       SqliteLogStore (+ Rescan → LogCollectorService)
```

`MainViewModel` owns the three page VMs as properties (`Dashboard`, `SystemSpecs`, `InstalledApps`) and
re-points each hosted page's `DataContext` to its own VM. Because of that re-pointing, a page that needs
shell state binds through the window:
`{Binding $parent[Window].((vm:MainViewModel)DataContext).IsDashboardPage}`.

### DI Container (`Program.cs`, `Host.CreateApplicationBuilder`)

| Lifetime | Services |
|---|---|
| **Singleton** | `AppConfig`, `ILogStore` (SqliteLogStore), `HttpClient` (30s timeout), `IInstalledAppDetector`, `IPackageDetector`, `IActivityCollector` (per-platform), `AutoStartService`, `LogCollectorService`, `EventCoordinator`, `JourneyEngine`, `ATSPIEventWatcher`, `WindowsExplorerWatcher`, `IExplorerWindowProvider`, `FileSystemEventWatcher`, `RecentFilesWatcher`, `AppUpdateService`, `IBrowserRegistry` (unconditional — consumed by `LogCollectorService` + `SessionLabelResolver` regardless of `ALPHA_BROWSER_TRACKING_ENABLED`) |
| **Singleton (conditional)** | `IAccessibilityBrowserReader` (platform reader) + `BrowserHistoryReader` — only when `ALPHA_BROWSER_TRACKING_ENABLED` |
| **Hosted** | `BackgroundGuardService`, `LogCollectorService`, `DesktopEventService`, `AccessibilityBrowserTracker` (conditional), `HardwareDeviceWatcherService`, `AppUpdateService` |
| **Transient** | `DashboardViewModel`, `SystemSpecsViewModel`, `InstalledAppsViewModel`, `MainViewModel` |

`App.ServiceProvider = host.Services` is set after `StartAsync`; `App.axaml.cs` resolves `MainViewModel`
from DI (which pulls the three page VMs in through its constructor).

`Core/AppInfo` is deliberately **static, not injected** — branding is process-wide immutable data read
from an embedded resource, and it is needed from places with no container (XAML resources, tray setup,
log banners).

### CLI modes & process model

- `--encrypt-config [input] [output]` — build-time `.env` → `config.enc` (transport key)
- `--print-config` — prints the config an INSTALLED build would resolve (diagnostic)
- `--background` — headless: hosts the services with **no Avalonia/X11 UI** (systemd unit). Skips GUI init so a stale `XAUTHORITY` can never crash startup.
- `--minimized` — start hidden to tray (auto-start launches).
- `--remove-account` — stop → bounded drain (if online) → `POST /devices/self-remove` → wipe local DB/machine id → relaunch signed-out (`AccountRemovalService`). Fixed mode does **not** refuse this. Offline: wipe after drain timeout.
- `--protected-uninstall --code <raw>` — **product uninstall** (removal code required):
  - **Windows:** DLP pipe `AuthorizeTeardown` → unlock ACLs → stop service → drain/self-remove/wipe → `%ProgramData%\…\dlp\teardown.ok` (Inno gate)
  - **Linux:** HTTP `removal-verify` (token from SQLite under `~/.local/share/alpha-ai-tracker`) → drain/self-remove/wipe → LocalAppData + `/var/lib/<pkg>/dlp` `teardown.ok` (`.deb` prerm gate)
- **Single instance:** global mutex from `APP_IDENTIFIERS` `APP_MUTEX` (`Global\…`). A second launch that cannot acquire the mutex sends `SHOW` over the named pipe so the running instance raises its window — but only for *user* launches (not `--background`/`--minimized`).
- **Two Windows processes when DLP is installed:**
  | Process | Privilege | Mutex / pipe |
  |---------|-----------|--------------|
  | `AlphaAITracker.Service.exe` (`client/DlpService`) | LocalSystem | `Global\<WINDOWS_SERVICE_NAME>` + `\\.\pipe\<WINDOWS_DLP_PIPE_NAME>` |
  | User helper (`client.exe`) | Logged-on user | Existing `Global\<APP_MUTEX>` |
- Windows: `SetThreadExecutionState(ES_SYSTEM_REQUIRED)` while tracking prevents sleep.

---

## 4b. Windows DLP service (`client/DlpService`)

Production blocking and protected lifecycle live here — **not** in the Avalonia process (Session 0 cannot host interactive a11y/UI; UAC prompts mid-flight are unacceptable).

```
AlphaAITracker.Service.exe (LocalSystem)
  ├── PolicyPullWorker     GET /dlp/policy → PolicyStore
  ├── EnforceWorker        UsbStorEnforcer (usb_*) + DomainBlockEnforcer (web_navigate / schedule)
  ├── DnsBlockPinWorker    DNS-cache → AlphaAI-DLP-PIN-* firewall + TCP-kill (CDN subdomains, no restart)
  ├── RemovableDriveMonitor  plug events → local alert queue
  ├── AlertSyncWorker      POST /dlp/alerts/sync (≤60s cadence)
  ├── NamedPipeHost        health / AuthorizeTeardown / GetPolicyVersion / GetTrackingState
  ├── TrackingControlWorker hourly GET /devices/control → control\control.json (ALPHA_CONTROL_POLL_SEC)
  ├── ServiceAclWorker     SY full / BA query-only until removal unlock
  └── TrackerWatchdogWorker  relaunch user helper if End Task'd (ALPHA_DLP_WATCHDOG)
         │ named pipe + ProgramData files
         ▼
client.exe (user session) — collectors, GUI, device.auth bridge, --protected-uninstall
```

| Path | Role |
|------|------|
| `%ProgramData%\<APP_MUTEX>\dlp\service.env` | `ALPHA_DLP_*` for the service process (incl. local switches `USB_BLOCK` / `WEB_BLOCK` / `NETWORK_LOCKDOWN` / `APP_BLOCK` — `false` = never apply, even if the server policy blocks) |
| `%ProgramData%\<APP_MUTEX>\dlp\device.auth` | Device token written by tracker after login |
| `%ProgramData%\<APP_MUTEX>\dlp\dlp.db` | Current policy plus every unsynced alert, with no age or count cap. Accepted alerts and their evidence are deleted. Audit keeps the newest 200 rows |
| `%ProgramData%\<APP_MUTEX>\dlp\policy\policy.vN.json` | Debug snapshots of applied bundles; only the latest 3 versions are kept |
| `%ProgramData%\<APP_MUTEX>\updates\update.log` | Silent-apply log; replaced when the next apply starts |
| `%ProgramData%\<APP_MUTEX>\dlp\teardown.ok` | Uninstaller gate after successful protected uninstall |

`ALPHA_DLP_ENABLED=false` parks every worker (tracker behaves as pre-DLP). P2 domain block: hosts DNS sink + incremental firewall + TCP-kill + **`DnsBlockPinWorker`** (live CDN IP pin, no employee restart). Optional true WFP callout / path hard-block / AppLocker / minifilter still open — see `plan.md`.

---

## 5. Local Data Model (SQLite)

**11 tables** (defined in `DatabaseSchema.CreateTableSql`, `IF NOT EXISTS`):

`device_hardware_info` · `storage_devices` (relational child of hardware) · `installed_applications` (GUI apps) · `installed_packages` (CLI tools/runtimes/libraries) · `network_info` · `session_events` · `app_sessions` · `app_items` (generic self-referencing child) · `app_status` (key-value, e.g. `last_heartbeat_at`, `perm_*`) · `permission_status` · `employee_info` (login + JWT token)

### Key columns

**`app_sessions`** — process_name, app_display_name (real app name), started_at/ended_at, machine_id, employee_id/name, session_id (per-launch GUID), platform, `installed_app_id`/`installed_package_id` FKs, `process_id`, `parent_process_id`, and (added via MigrateSql ALTERs) `grouped_by` ('cgroup'|'pid'), `cgroup_scope` (raw systemd scope, needed for boot hydration), `context_label` (VS Code workspace / Chrome profile).

**`app_items`** — self-referencing via `parent_item_id` (session → tab → navigation / terminal → child). Core columns: `item_type` ('tab','browser_tab','browser_navigation','browser_download','file_manager_tab','fm_*','terminal','folder','file','process','window'), `title`, `identifier`, `url`, `domain`, `opened_at`/`closed_at`, `process_id`. Journey fields (coexist with item_type): `object_type`, `action`, `journey_id`, `sequence`, `previous_path`, `current_path`, `window_id`, `tab_id`, `metadata_json`.

**Upserts / dedup:**

| Table | Conflict key | Behavior |
|---|---|---|
| `installed_applications` | `ON CONFLICT(app_name)` | Updates metadata; **resets `is_synced = 0`** so re-detection re-syncs (drives server `last_seen_at`) |
| `installed_packages` | `ON CONFLICT(package_name, source_manager)` | Update version/category; dedup window-fn + `CREATE UNIQUE INDEX idx_installed_packages_fingerprint` in MigrateSql (kills duplicate rows) |
| `app_sessions` | `ON CONFLICT(id)` | Merge `ended_at` / `parent_process_id` |
| `app_items` | `ON CONFLICT(id)` | Merge title/identifier/url/domain/parent/closed_at/journey fields |

### Migration strategy

`DatabaseSchema.MigrateSql` + `SqliteLogStore.InitializeAsync`: base schema via `IF NOT EXISTS` on every start, then a batch of **idempotent `ALTER TABLE ... ADD COLUMN`** statements (caught via "duplicate column" SqliteException), the installed_packages dedup block, and a few `CREATE INDEX IF NOT EXISTS`. There is no numbered version table.

### Concurrency

`SqliteLogStore` guards the single shared connection with `SemaphoreSlim(1,1)`; `PRAGMA busy_timeout = 5000`; `GatedTransaction` releases the gate on dispose; private ungated helpers (`SetStatusCoreAsync`, `GetEmployeeInfoCoreAsync`) avoid re-entrancy deadlocks. `CloseSessionsAndAppItemsAsync()` closes sessions + their open items in ONE transaction (crash-safe cascade — no orphaned open items).

---

## 6. Lifecycle & Startup Sequence

1. `Program.cs` handles CLI modes, acquires the mutex, starts the single-instance pipe server, builds DI, `host.StartAsync`.
2. **`LogCollectorService`** (hosted):
   - `InitializeAsync` (schema), `RefreshEmployeeInfo` (restores login from SQLite),
   - **Headless session restore (2026-08-18):** if persisted employee credentials exist, `StartTracking()` runs here at boot — so `--background` mode (no GUI) tracks app sessions from power-on, not just browser journeys. Previously this restore happened ONLY in the Avalonia GUI (`MainViewModel.InitializeAsync`), so a headless boot spun on "waiting for login" forever while the browser tracker (which restores the login itself) kept working — the dashboard showed browser activity but zero app sessions.
   - **`ReconcileStaleSessionsOnBootAsync`** — stale `last_heartbeat_at` (>60s) → close orphaned sessions + items with the heartbeat time as approximate crash time (handles poweroff/crash/fast-restart).
   - **`CleanupGarbageSessionRowsAsync`** — closes old `--type=` / long process-name rows.
   - **`CleanupNonGuiAppEntriesAsync`** — removes pre-GUI-gate non-GUI entries (`sh`, `snap`) from `installed_applications` and closes their sessions.
   - Immediate hardware + network collection, then the main loop.
3. **`DesktopEventService`** starts the file-explorer watchers after DB init.
4. **`AccessibilityBrowserTracker`** starts polling browser windows (independent of login, no catalog dependency).
5. **`BackgroundGuardService`** watches auto-start / systemd unit (60s loop) and re-installs if removed — it never *creates* them on its own.
6. **Login (from UI, first-time identity only)** → `MainViewModel.LoginAsync` → `_logCollector.SetEmployeeInfo(...)` + `StartTracking()` (idempotent — already running on a restored session) → permission wizard. After the first login the GUI is **login-only**: opening or closing it never starts or stops the tracking services (the background process owns tracking; window-close hides to tray; only an explicit quit exits the process).

---

## 7. Login & Permission Wizard

### Login

- **Login:** `POST {serverUrl}/api/v1/auth/employee-login` with `{employeeId, secretKey}` → `{employee, token}`. Persists to `employee_info` (SQLite) and feeds `LogCollectorService.SetEmployeeInfo(employeeId, name, token)` + `StartTracking()` (which also records a `login` session event and arms Windows anti-sleep). **Instant sync on login (2026-08-11):** `LoginAsync` then calls `SyncService.RequestImmediateSync()` — the dedicated sync engine's inter-pass wait is a `SemaphoreSlim` that the request releases, so a full drain pass starts right away instead of waiting out the idle interval. The employee record itself is already server-side (it IS the login response); the instant pass pushes everything else — device_hardware_info, installed apps/packages, network, storage_devices, hardware_devices, session_events, permission_status, app_status, sessions/items.
- **Session restore:** at boot, `LogCollectorService` reads `employee_info` and **starts tracking headlessly** (2026-08-18) — no GUI required. When the GUI opens, `MainViewModel.InitializeAsync` re-authenticates the collector (idempotent), **fires `RequestImmediateSync()`** (so rows buffered since the last run land immediately), resets stored `perm_*` statuses, and re-evaluates the permission steps from scratch.
- **No logout:** the employee-disconnect flow (button, `LogoutCommand`, `StopTracking()`, `POST /api/v1/auth/employee-disconnect`) was **removed** 2026-08-10. Once logged in the client tracks until the process stops.

### Permission wizard (`GetNextPermissionStep`)

Always **re-validates the actual condition** — it never trusts stored statuses. Returns the first incomplete step:

| # | Step | Linux check | Grant action |
|---|---|---|---|
| 1 | **Auto-Start** | `~/.config/autostart/alpha-ai-tracker.desktop` exists | `AutoStartService.EnableAutoStartForced()` (+ systemd unit enable) |
| 2 | **Background Guard** | `~/.config/systemd/user/alpha-ai-tracker.service` exists | `EnableAutoStartForced()` (installs the unit) |
| 3 | **Dependencies** (Linux only) | `pkexec`, `loginctl`, `gsettings`, `setcap` present | `pkexec apt-get install -y policykit-1 systemd libglib2.0-bin libcap2-bin` (or manual sudo command) |
| 4 | **Other Permissions** | Linux: Wayland `toolkit-accessibility` via gsettings; Windows: process enumeration; macOS: osascript Accessibility grant | Linux: `pkexec` bash script (chmod +r shell-history files; enable toolkit accessibility as `$USER`; `setcap CAP_DAC_READ_SEARCH+ep` on the installed binary — skipped for `dotnet run`); Windows: UAC `runas`; macOS: instructions (Accessibility / Full Disk Access / Screen Recording) |

`permission_status` results (`perm_auto_start`, `perm_background`, `perm_other`) are stored in `app_status` for reference. Linux dependency installs also surface a manual `sudo apt-get install -y …` command via `ShowManualInstall`.

### UI

`MainWindow.axaml` is a **router**, not a screen. It picks exactly one of four mutually exclusive
top-level states from `MainViewModel`, in this order:

| Guard | Shows |
|---|---|
| `IsSplashVisible` | **Page 1** `SplashPage` — boot checklist + determinate progress, played once on window open |
| `!IsLoggedIn` | **Page 2** `LoginPage` — Employee ID + Secret Key (secret masked; it is a long-lived credential) |
| `RequiresPermissionAction` | **Page 3** `PermissionSetupPage` — the wizard above, one card per step |
| `IsProfile` | **Pages 4–6** behind the nav rail |

`IsProfile => IsLoggedIn && CurrentPermissionStep == PermissionStep.None`, so the shell only appears
once the wizard reports nothing left to grant.

The shell is a 246px nav rail (brand mark, three nav buttons, signed-in employee, version) plus a
content column with a top bar (`ActivePageTitle` / `ActivePageSubtitle`, environment badge, refresh)
hosting pages 4–6. Navigation is `NavigateCommand` with a string parameter (`dashboard` / `specs` /
`apps`) which sets `ActivePage` and awaits `RefreshActivePageAsync()`.

Theming is the **light** token dictionary in `Styles/AppTheme.xaml` plus the style classes in
`App.axaml`. Window close hides to tray (`ShutdownMode.OnExplicitShutdown`). Full detail:
**[UI_ARCHITECTURE.md](UI_ARCHITECTURE.md)**.

---

## 8. Process Collection Pipeline

### The 30-second cycle (`LogCollectorService.ExecuteAsync`)

```
┌─────────────────┐   ┌────────────────────────────────────┐   ┌─────────────────────────┐
│ IActivityCollect │──▶│ Per-process resolution             │──▶│ SQLite                  │
│ .CollectAsync()  │   │  ProcessFilter → headless filter   │   │  StoreAppSessionsAsync  │
│  (platform)      │   │  NonAppProcesses/prefixes          │   │  StoreAppItemsAsync     │
└─────────────────┘   │  ResolveAppInfo (known/fuzzy/auto)  │   │  CloseSessionsAnd…      │
        │             │  CgroupResolver scope               │   └─────────────────────────┘
        │ every 30s   │  BuildSessionKey (scope-aware)      │
        ▼             │  SessionHierarchyResolver parent    │
   process snapshot   └────────────────────────────────────┘
```

- Waits (5s idle poll) until `StartTracking()` after login.
- **Every cycle:** collect → build process tree → hydrate open sessions from DB (**excluding browser-owned sessions** — those belong to the accessibility tracker and must not be closed or duplicated) → resolve each process → sort by priority (IDE → browser → file manager → terminal → shell) → compute `currentKeys` → close vanished sessions atomically → store new sessions + root items + context updates → heartbeat.
- **Context updates** (`UpdateActivityContextAsync`): refreshes root title/identifier and appends `browser_navigation`/`folder`/`file` children with a 30s per-(session,type,identifier) cooldown.

### Platform collectors

| Platform | Window titles | Foreground | CPU | Cmdline |
|---|---|---|---|---|
| **Linux** | GNOME Shell `Introspect.GetWindows` (Wayland, ALL windows) + `xprop _NET_CLIENT_LIST` (X11/XWayland) | xprop → AT-SPI (python3 / gdbus, **decodes the packed 64-bit state bitmask, ACTIVE/FOCUSED bits**) → xdg portal → GNOME Shell → xdotool → heuristic | `TotalProcessorTime` two-sample 100ms gap | `/proc/<pid>/cmdline` |
| **Windows** | `EnumWindows` (all visible windows → titles) | `GetForegroundWindow` | same | PowerShell `Get-CimInstance Win32_Process` |
| **macOS** | **foreground only** via osascript | osascript/System Events | **0 (not measured)** | `ps -o command=` |

All platforms filter **headless Chromium/Electron subprocesses** (`--type=renderer|gpu-process|utility|zygote|broker|crashpad-handler`, Firefox `--contentproc`) via cmdline.

### Filtering & resolution (`ResolveAppInfo`)

1. Headless subprocesses without a window title → skipped.
2. `NonAppProcesses` (exact: GNOME daemons, gvfsd-*, gsd-*, shells `sh/bash/zsh/…`, `waydroid`, `snapd`, …) + `NonAppProcessPrefixes` → skipped.
3. **Known binary names** cache (refreshed ≤1 min from `installed_applications`): exact match, then re-verify GUI (non-browser rows need non-empty Categories).
4. **Fuzzy match** (`GetInstalledAppByBinaryNameFuzzyAsync`, process name >3 chars): `binary_name LIKE` with corrected AND/OR precedence; prefers `is_browser` rows + shortest binary name. Also rejects non-GUI rows.
5. **Auto-detect GUI app** (`IsGuiApplication` → `AutoDetectInstalledGuiApp`): executable in standard paths (Linux `/usr/bin`, `/opt`, `/snap/bin`, flatpak, `/home/*`, `/media/*`; Windows Program Files/WindowsApps; macOS `.app`/`/Applications`) or a matching `.desktop` `Exec=` → registers into `installed_applications`.
6. **Anything else is skipped** — CLI tools, shells, build tools, runtimes, and daemons never become sessions/items.
7. **Browsers are skipped when `ALPHA_BROWSER_TRACKING_ENABLED`** (owned by the accessibility tracker; `IBrowserRegistry.IsBrowser()` covers the post-DB-wipe window before the catalog scan marks `is_browser=1`).

### Session identity & hierarchy

- **`BuildSessionKey`**: scoped processes → `scope|{scope}|{installedAppId}|{machine}|{session}` (collapses all subprocesses of one logical window); unscoped → `{pid}|{machine}|{session}`.
- **`CgroupResolver`** reads `/proc/<pid>/cgroup` for `app-*.scope` (systemd transient scope per `.desktop` launch) — VS Code's ~11 PIDs become ONE session; two windows get different scopes.
- **`SessionHierarchyResolver`** walks the PPID chain (through shells, terminals, build tools, runtimes, IDE subprocesses) and links sessions under their parent via `parent_item_id`. Duplicate-PID open sessions (browser tracker + main loop overlap) are deduped keeping the earliest per PID.
- **`SessionLabelResolver`** derives `context_label` (VS Code workspace folder from argv, browser `--profile-directory`/`-P`/`--profile=` for any browser the `IBrowserRegistry` knows).
- **Close:** `CloseSessionsAndAppItemsAsync` closes sessions + their open items in ONE transaction; crash recovery reuses it at boot.

### Root item type (`AppProcessClassifier.ResolveRootItemType`)

`isBrowser → browser_tab` · `.desktop Categories`/desktopId → `folder` (file manager) / `tab` (IDE/app) / `process` (runtime) · fallbacks: file manager → `folder`, shell → `terminal`, appId → `tab`, pkgId/runtime/build-tool → `process`, window title → `tab`, else `process`.

---

## 9. System Info Collection

### Device hardware — every 30 cycles (~15 min)

Fingerprint-deduped (`mac|hostname|os|cpu|ram|gpu`): hostname, `RuntimeInformation.OSDescription`, CPU model (`/proc/cpuinfo` / `PROCESSOR_IDENTIFIER`), cores, total RAM (`/proc/meminfo` / Windows fallback constant), **MAC** (first up non-loopback interface), **storage devices** (Linux `lsblk -J` with SSD/HDD via `ROTA`; Windows `DriveInfo`), **GPU** (`/proc/driver/nvidia/version`, else `lspci`). Stored as `device_hardware_info` + relational `storage_devices` rows (backfilled if the hardware fingerprint is unchanged but storage is empty).

### Hardware device hotplug tracking — real-time (Linux only)

`HardwareDeviceWatcherService` tracks physical device plug/unplug events in real-time:
- **USB devices** via `udevadm monitor` (subsystem=usb) + periodic `/sys/bus/usb/devices/` enumeration
- **Analog audio jacks** via `amixer` polling for headphone/headset jack state changes (ALSA)
- Stores events in `hardware_devices` table with `device_class` (`usb`/`audio`/`headphone`/`storage`/`input`/`display`/`other`)
- Plug events create open rows; unplug events set `unplugged_at`
- USB storage devices detected via `/sys/class/block` entries with `/usb` in their bus path
- Audio jack detection monitors `Headphone` and `Headset Mic` mixer controls for `[on]`/`[off]` state changes
- Deduped by `bus_path` to avoid duplicate rows for the same physical slot

### Network info — every 10 cycles (~5 min)

Public IP from `api.ipify.org` → `icanhazip.com` → `checkip.amazonaws.com` (first success), private IPs from `Dns.GetHostEntry` + per up-interface unicast addresses. Deduped by IP change (only new-IP rows are stored).

### Session events

`login` rows with OS username + timestamp.

### Permission status — every 10 cycles

Per-platform `GetPermissionStatus()` (Linux: xprop/xdotool/gdbus/python3/portal/introspect/atspi/X11 availability; Windows: user32 probes; macOS: osascript + Accessibility grant) → `permission_status` table.

### Scan cadence (30s cycle counters)

| Data | Frequency |
|---|---|
| Device hardware | Every 30 cycles (~15 min) |
| Installed apps | Every 30 cycles (~15 min) |
| Installed packages | Every 60 cycles (~30 min, delegates to joint scan) |
| Network info | Every 10 cycles (~5 min) |
| Permission status | Every 10 cycles (~5 min) |
| Heartbeat `last_heartbeat_at` | Every cycle **and** every authenticated `SyncService` pass (2026-11-03) |
| **Focus flush** (open-session `foreground/background_seconds`, additive) | **Every 2 cycles (~1 min)** — was every 10 (~5 min); re-queues `is_synced=0` so the server learns growing totals within a minute (user rule 2026-08-18) |

> **Sync moved out of this loop (2026-08-11)** — the dedicated `SyncService` background loop (§13) drains unsent rows on its own schedule; the collection loop never performs network I/O.
>
> **Heartbeat dual-write (2026-11-03 / 2.3.25):** collect-loop stamps stay for crash-recovery
> reconciliation; `SyncService` also stamps `last_heartbeat_at` whenever credentials exist so
> attendance stays alive across collect gaps (e.g. post-reboot) as long as sync is running.

---

## 10. Installed Apps & Packages Inventory

**Two detectors + one classifier:**

1. **`InstalledAppDetector`** (GUI apps → `installed_applications`):
   - **Linux:** `.desktop` files from `$XDG_DATA_HOME/applications`, `~/.local/share/applications`, every `$XDG_DATA_DIRS` entry + `/applications`, plus baseline `/usr/share` & `/usr/local/share` (covers **snap** `/var/lib/snapd/desktop/applications` and **flatpak** exports). `NoDisplay=true` / non-`Type=Application` skipped. Browser detection via `Categories=WebBrowser` or `MimeType x-scheme-handler/http(s)`. `desktop_id` = `.desktop` filename, `categories` stored raw.
   - **Windows:** Start Menu `.lnk`s, Program Files (x86) dirs, registry `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` (DisplayName, version, publisher, path, uninstall string); browser via `URLAssociations` http/https.
   - **macOS:** `/Applications` + `~/Applications` `.app` bundles; `Info.plist` → `CFBundleIdentifier` (identity) + `CFBundleURLSchemes` http+https (browser).
2. **`PackageDetector`** (CLI tools/runtimes/libraries → `installed_packages`): npm global (`npm list -g --json`), pip (`pip list --format=json`), dpkg/apt (`dpkg-query -W -f='…'` — quoted format fix), snap (skips snaps with desktop entries), flatpak (skips apps with desktop entries), brew, macports, choco, winget, scoop, plus a built-in `CliKnownPackages` list. Categories: `runtime`/`tool`/`library`/`system`.
3. **`SoftwareClassifier.Classify(rawApps, rawPackages)`** — the joint dedup: generates a **SHA-256 identity** per entry (`SoftwareIdentityResolver`: Linux desktop_id, macOS bundle id, Windows uninstall-key, + normalized install path), **GUI apps win** (a matching package entry is dropped — fixes Firefox-snap appearing as a package), assigns canonical categories (`SoftwareCategoryResolver`: WebBrowser/FileManager/IDE/Development/TerminalEmulator/…), and routes to exactly one table.

Both detectors are forced-rechecked every scan (`ForceRecheck`) and upsert into SQLite (re-detect resets `is_synced=0`).

---

## 11. Browser Journey (Option B — accessibility tree + history fallback)

`AccessibilityBrowserTracker` (BackgroundService, gated on `ALPHA_BROWSER_TRACKING_ENABLED`) captures the REAL browser journey — **no debugger, no extension, no catalog dependency** (works for install→use→uninstall-in-5-min browsers). Browser detection is dynamic via `IBrowserRegistry` (sourced from `installed_applications.is_browser`, refreshed every 5 min) — any browser with a `.desktop` `Categories=WebBrowser` / Windows `URLAssociations` http+https / macOS `CFBundleURLSchemes` http+https is automatically detected without hardcoded name lists.

### Poll loop (every `ALPHA_BROWSER_ACCESSIBILITY_POLL_SECONDS`, default 3s)

1. `IAccessibilityBrowserReader.ReadAsync()` → `AccessibilitySnapshot[]` (windowKey, pid, process, title, URL, `UrlSource`, incognito, **`IsActive`** — AT-SPI ACTIVE/FOCUSED state on Linux, `GetForegroundWindow` HWND match on Windows, frontmost process on macOS).
2. `EnrichUrlsFromHistoryAsync` fills empty URLs from the **profile-history fallback** (preserves `IsActive`).
3. **Focus accounting** — each poll the ACTIVE window's session earns the poll interval as `foreground_seconds` and every other open window earns `background_seconds`; totals flush every 10 polls + on close via `UpdateAppSessionFocusAsync` (`is_synced=0` re-syncs). The flush is **additive** (delta in memory, cleared after each write — the SQLite row is the true cumulative total), so long-running browser sessions show growing foreground/background on the dashboard, not just the last flush window. Browser journeys finally earn focus time (they are owned here, not by the main loop's focus accounting).
4. Per window: first sight → **new `app_sessions`** + `browser_tab` root item (URL, domain, journeyId, `metadata_json`); changes → rotate/record; vanished (5-miss grace) → close; idle (`ALPHA_BROWSER_JOURNEY_IDLE_MINUTES`, 15) → close; shutdown → close all.

### Reading the URL — three merged sources (Linux embedded python3 probe)

- **A) AT-SPI tree** — for browsers that expose it: the omnibox `ENTRY`/`EDITBAR` node **"Address and search bar"** gives the exact URL/query; **Firefox (incl. private windows)** additionally exposes the exact page URL via the `DOCUMENT_WEB` (role 95) node's **`DocURL`** attribute (Firefox builds its tree on demand). Chrome 136+ only builds the tree with `--force-renderer-accessibility` (the installed user-level `google-chrome.desktop` carries the flag).
- **B) Firefox sessionstore** (`recovery.jsonlz4` / `sessionstore.jsonlz4`, decompressed by an embedded pure-python **LZ4 block decoder**) — survives snap Firefox's AppArmor D-Bus block; exact per-tab URL + active-tab index.
- **C) WM window list** (GNOME Shell `Introspect.GetWindows`, else `xprop _NET_CLIENT_LIST`) — stable per-window ids (`wm:<id>`) and X11-only browsers AT-SPI cannot see.

**Windows** = UIA (`Interop.UIAutomationClient`) address-bar `ValuePattern`. **macOS** = osascript/System Events (best-effort, Accessibility grant required).

### Hybrid URL fallback (`BrowserHistoryReader`)

When the a11y tree cannot expose the omnibox (Linux Chrome 136+ without the flag, snap Firefox), reads the browser's **own profile database while it runs**: Chromium-family `History` (visit_time = microseconds since 1601) and Firefox `places.sqlite` (visit_date = µs since 1970). DB + `-wal`/`-shm`/`-journal` sidecars are copied to a temp dir and opened read-only; change signatures throttle re-reads (`ALPHA_BROWSER_HISTORY_POLL_SECONDS`, 10s); resolution is **strictly title-match** (no newest-visit fallback → no cross-window misattribution); generic scans of `~/.config`, `~/.mozilla`, snap-firefox, Windows `LocalAppData`, macOS `Library/Application Support` discover brand-new profiles. Incognito visits are never written to these DBs, so they stay out automatically.

### Rotation semantics (per-page records)

- Each page visit gets its OWN `browser_tab` root — the previous root is **closed** via `CloseAppItemAsync` (resets `is_synced=0` so the server learns the close) and a fresh root is opened.
- A real URL change rotates immediately; a title-only change rotates only after ~10s stability (`MinTabRotationInterval`, filters badge/timer flicker).
- **Bounded history-lag:** if the URL is missing after a title change (private windows / `about:`/`file:` pages never write history), the tab rotates on title after ~25s (`HistoryLagTimeout`) — never frozen.
- URL changes also write a `browser_navigation` child (`previous_path` → `current_path`, `sequence` via `GetNextSequenceAsync`).
- **Downloads** in `~/Downloads` (watched via `FileSystemWatcher`, skipping `.crdownload`/`.part`/`.tmp`) are appended to the most recent browser session as `browser_download` items.
- **Window identity** (`ResolveWindowKey`): re-keys by page-title match (multi-window Chrome/Edge/Firefox share one PID) or a single-window count guard — one window can never steal another's session.
- **Incognito:** windows are detected (title/cmdline) and flagged in `metadata_json` (`"incognito":true`); their URL is stored only when `ALPHA_BROWSER_CAPTURE_INCOGNITO=true` (default off — legal-safe).
- `metadata_json` = `{source: accessibility|history|sessionstore|downloads-watcher, windowKey, incognito, processName, capturedAt}`.

---

## 12. File Explorer Journey (Desktop Event Bus)

An event-driven pipeline (introduced 2026-07-29) for file-manager operations (Nautilus, Dolphin, Thunar, Nemo, Caja, PCManFM, Konqueror, …):

```
Watchers (IObservableEventSource)          EventCoordinator                 JourneyEngine
  ATSPIEventWatcher     ──raw──►  dedup (3s) / correlate (500ms)  ──normalized──►  resolve AppSession
  FileSystemEventWatcher  (RawDesktopEvent)  journey records (15-min        create file_manager_tab root
  RecentFilesWatcher            timeout, max 500)                           + fm_* items (9 journey fields)
```

- **`ATSPIEventWatcher`** (Linux; `Tmds.DBus.Protocol`): registers for AT-SPI `focus:`/`window:` events, detects the foreground file manager via `xdotool getactivewindow getwindowpid` + `/proc/<pid>/cwd` for the current path.
- **`FileSystemEventWatcher`**: `FileSystemWatcher` on 6 user dirs (Desktop, Documents, Music, Pictures, Videos, Downloads) — created/deleted/renamed/changed; 500ms debounce; exclusion prefixes (Waydroid, flatpak, snap, cache, Trash, containers, Steam, node_modules, …).
- **`RecentFilesWatcher`**: watches `~/.local/share/recently-used.xbel` (XDG), parses the top 5 bookmarks, emits `open` events for recent files.
- **`EventCoordinator`**: validates (file manager, not ignored process, valid path), infers `object_type` (Folder/File/Window via `File.GetAttributes` + path heuristics) and `action` (navigate/open/create/delete/rename/modify/close), dedups (3s) + correlates (500ms), assigns `journey_id` + `sequence`, reaps stale journeys (15 min).
- **`JourneyEngine`**: resolves/creates an `AppSession` per journey, ensures a `file_manager_tab` root item, and stores `fm_{object}_{action}` items with the 9 journey fields; a `close` action closes the session + items.
- **`DesktopEventService`** (hosted) wires it all: starts Linux AT-SPI watcher + filesystem + recent-files watchers, subscribes them to the coordinator, forwards normalized events to the engine.

---

## 13. Sync / Transport to Server

- **Protocol:** REST over HTTP(S); JSON `{employeeId, token, entries: [...]}`; token = encrypted JWT from login, sent in the request body (not a header). Idempotent by design — the server upserts by client GUID (`ON CONFLICT (id) …`), so failed chunks are retried safely.
- **Engine (2026-08-11):** dedicated `SyncService` background loop — collection **never blocks on the network**. Unsent rows drain in chunks bounded by **both** row count (`ALPHA_SYNC_MAX_ROWS`, default 1000) **and** serialized payload bytes (`ALPHA_SYNC_MAX_BYTES`, default ~1MB), with a politeness pause between chunks (`ALPHA_SYNC_CHUNK_DELAY_MS`, 150ms), a per-pass time budget (`ALPHA_SYNC_MAX_DURATION_SEC`, 5 min) so a backlog never monopolizes CPU, and **exponential backoff** on failure (5s → 10s → … → `ALPHA_SYNC_BACKOFF_MAX_SEC`, 5 min). **2026-08-18:** the between-pass wait is now **capped at 60s even on failure** — backoff only stretches a single retry gap, never the guaranteed cadence — so every `is_synced=0` row reaches the server within a minute (user rule: force-push every minute). A 50k+ backlog drains in minutes — the old inline sync moved a fixed 500 rows/table per 5-minute cycle while blocking collection (~8h to drain 50k).
- **Compression:** request bodies are **gzip**-encoded when `ALPHA_SYNC_COMPRESSION=true` (server must run `middleware.Decompress()`); oversized slices are auto-split (binary halving) to stay under the byte cap.
- **Order matters:** small/inventory tables first (device-hardware → network-info → storage → hardware-devices → location → session-events), then **app-status + permission-status** (so `last_heartbeat_at` is not starved — 2026-11-03), then installed-apps → installed-packages, then **app-sessions (parents)**, then **app-items (children)**.
- On success rows are marked `is_synced=1` (batched `UPDATE … WHERE id IN (…)`, 400 ids/statement); on 401/403 the chunk is dropped until re-login; on network failure the chunk is retried next pass with backoff.

| Endpoint | Payload extras |
|---|---|
| `POST /api/v1/device-hardware/sync` | macAddress, storageDevices (JSON), gpuModel/vramMb |
| `POST /api/v1/installed-apps/sync` | binaryName, isBrowser, desktopId, categories |
| `POST /api/v1/installed-packages/sync` | packageName, version, category, sourceManager |
| `POST /api/v1/network-info/sync` | publicIp, privateIp, networkInterfaceName |
| `POST /api/v1/session-events/sync` | eventType, osUsername |
| `POST /api/v1/app-sessions/sync` | installedAppId, installedPackageId, processId, parentProcessId, groupedBy, cgroupScope, contextLabel |
| `POST /api/v1/app-items/sync` | parentItemId, processId, url/domain, 9 journey fields, windowId/tabId, metadataJson |
| `POST /api/v1/app-status/sync` | key, value, updatedAt (upserted by employee+key; re-sent when a value changes) |
| `POST /api/v1/hardware-devices/sync` | deviceClass, vendor, product, serial, busPath, pluggedAt/unpluggedAt |
| `POST /api/v1/permission-status/sync` | checkId, sessionId, sessionType, platform, method, works, details |
| `POST /api/v1/storage-devices/sync` | deviceHardwareId, deviceType, model, capacityMb |
| `POST /api/v1/auth/employee-login` | employeeId + secretKey → {employee, token} |

> **Instant sync on login (2026-08-11):** `MainViewModel` (which injects the singleton
> `SyncService` — registered `AddSingleton` + `AddHostedService(GetRequiredService)` like
> `LogCollectorService`) calls `RequestImmediateSync()` after a successful login and on session
> restore. That releases a `SemaphoreSlim(0,1)` the loop waits on, so a **full drain pass starts
> immediately** — the machine's whole picture (hardware, inventory, network, permissions, sessions,
> items) lands on the server right after login, not on the next 60s idle tick. A request while a
> pass is already running/pending is a single no-op release (one drain covers it — never stacks).

> **Retention (2026-08-11):** after each clean sync pass, `SyncService` deletes rows the server
> already has and that are no longer needed locally — `app_items`/`app_sessions` older than
> `ALPHA_SYNC_RETENTION_HOURS` (24h; **open sessions are never deleted**, sessions are only deleted
> once their items are gone), `installed_applications`/`installed_packages` with `is_installed=0`,
> and superseded `network_info` rows (`is_current=0`). All other tables are retained forever.

> `activity-logs/sync` and `shell-commands/sync` are **removed** — no `activity_logs`/`shell_commands` tables or calls.

---

## 14. Configuration & Encryption

### Env keys (`AppConfig.FromEnv`)

| Key | Default | Meaning |
|---|---|---|
| `ALPHA_CLIENT_ID` | auto (`.machine-id` file next to binary, else GUID) | stable machine identifier |
| `ALPHA_DB_PATH` | `data/alpha_tracker.db` | SQLite path (resolved under `~/.local/share/alpha-ai-tracker` Linux / `%LOCALAPPDATA%\AlphaAITracker` Win) |
| `ALPHA_DB_ENCRYPTION_KEY` | — | SQLite encryption key (not currently wired to sqlcipher) |
| `ALPHA_COLLECT_INTERVAL_SEC` | 30 (min 5) | process-collection interval |
| `ALPHA_LOG_LEVEL` | Info (Verbose/Debug/Info/Warn/Error) | logging level |
| `ALPHA_SERVER_URL` | `DefaultServerUrl` assembly metadata (baked at publish) | server base URL |
| `ALPHA_API_KEY` | — | unused by sync (login token is used) |
| `ALPHA_BROWSER_TRACKING_ENABLED` | true | master switch for the accessibility browser tracker |
| `ALPHA_BROWSER_ACCESSIBILITY_POLL_SECONDS` | 3 | a11y poll interval |
| `ALPHA_BROWSER_JOURNEY_IDLE_MINUTES` | 15 | idle-close timeout |
| `ALPHA_BROWSER_CAPTURE_INCOGNITO` | false | store incognito URLs (legal review required) |
| `ALPHA_BROWSER_HISTORY_ENABLED` | true | profile-history URL fallback |
| `ALPHA_FILE_JOURNEY_ENABLED` | true | master switch for the Desktop Event Bus (file-manager navigations + file create/rename/delete/recent-file journeys) |
| `ALPHA_SYNC_INTERVAL_SEC` | 60 | min wait between sync drain passes when idle |
| `ALPHA_SYNC_MAX_ROWS` | 1000 | max rows per sync chunk |
| `ALPHA_SYNC_MAX_BYTES` | 1000000 | max serialized payload bytes per chunk (~1MB) |
| `ALPHA_SYNC_CHUNK_DELAY_MS` | 150 | politeness pause between chunks while draining a backlog |
| `ALPHA_SYNC_MAX_DURATION_SEC` | 300 | per-pass time budget — a huge backlog never monopolizes CPU |
| `ALPHA_SYNC_BACKOFF_MAX_SEC` | 300 | exponential-backoff ceiling on sync failure (5 min) |
| `ALPHA_SYNC_COMPRESSION` | true | gzip request bodies (server: `middleware.Decompress()`) |
| `ALPHA_BROWSER_HISTORY_POLL_SECONDS` | 10 | history re-read cadence |
| `ALPHA_UPDATE_ENABLED` | true | master switch for self-update (background checks + auto-install + GUI); requires `ALPHA_SERVER_URL` |
| `ALPHA_UPDATE_CHECK_INTERVAL_SEC` | 60 | seconds between background update checks against the server (min 15); service + GUI |
| `ALPHA_UPDATE_AUTO_INSTALL` | true | auto-download+install when a check finds a newer version (Linux still shows the polkit password dialog) |
| `ALPHA_UPDATE_DOWNLOAD_JITTER_SEC` | 3600 | fleet per-machine silent download delay window (fleet); GUI force ≤30s |
| `ALPHA_STREAM_ENABLED` | false | master switch for WebRTC live-stream publisher |
| `ALPHA_STREAM_FPS` | 12 | capture / encode cadence (1–30); may drop under ABR degrade |
| `ALPHA_STREAM_MAX_WIDTH` | 1920 | scale cap (hard-clamped 320–1920); may drop under ABR |
| `ALPHA_STREAM_MAX_BITRATE_KBPS` | 12000 | VP8 target kbps via `ScreenVp8Encoder` (cap 15000); ladder ceiling |
| `ALPHA_STREAM_MIN_UPLINK_KBPS` | 300 | skip publish if NetProbe uplink is below this (100–15000); was 2500 in 1.2.34 |
| `ALPHA_STREAM_KEYFRAME_INTERVAL_SEC` | 1 | seconds between forced keyframes (≥1; all-I-frame starves bitrate) |
| `ALPHA_WS_ENABLED` | false | presence WebSocket (`GET /api/v1/ws`) |
| `ALPHA_WS_PING_SEC` | 30 | application ping interval |
| `ALPHA_WS_RECONNECT_BASE_SEC` | 2 | initial reconnect backoff (doubles, cap 60s) |

> Web downloads and the desktop self-updater both use the server public bridge
> (`GET /api/v1/releases` + `/downloads/:assetId`; see `doc/plan.md`). `AppUpdateService`
> needs `ALPHA_SERVER_URL` only. Installers live in the server's `setup/` folder (`RELEASES_DIR`).
>
> **Installer-Parity:** any change to `ALPHA_STREAM_*` / `ALPHA_WS_*` / update behaviour requires
> `.env` → `encrypt-config.sh` → new installer so `config.enc` carries the knobs.

### Loading order (`EnvLoader.Load`)

1. **Explicit path** (`--config-enc`).
2. **Dev (`dotnet run`):** plaintext `.env` in the source tree (walked up to 6 dirs) is authoritative — a stale user `config.enc` can never shadow fresh edits.
3. **Installed:** user-config `config.enc` (`~/.config/alpha-ai-tracker`, `%APPDATA%\AlphaAITracker`, `~/Library/Application Support/AlphaAITracker`) — but if the freshly shipped copy next to the binary decrypts to **different values** (`ShippedConfigDiffers` on decrypted content), it **replaces** the stale user copy (fixes "rebuilt installer still uses old server URL").
4. Corrupt user copies self-heal from the shipped copy; then app-dir `config.enc`; then plaintext `.env` as last resort.

### Encryption

`EncryptedConfigService` — **AES-256-GCM**, format `[nonce 12][tag 16][ciphertext]`. Build time encrypts with the fixed **transport key** (SHA256 of `AlphaAITracker:TransportKey:v1`); first launch re-encrypts with a **machine-derived key** (SHA256 of `AlphaAITracker:MachineKey:v1:` + OS machine id — `/etc/machine-id`, Windows `MachineGuid`, macOS `IOPlatformUUID` — falling back to a persisted `.machine-id` in the user config dir). Leftover plaintext `.env` files are securely wiped (3-pass random overwrite). `--print-config` prints the resolved values of an installed build.

---

## 15. Installer / Deployment

### Build scripts (`client/publish/`)

| Script | Purpose |
|---|---|
| `build-installer.sh` | Cross-platform: builds Release, publishes per-RID (win-x64/linux-x64/osx-x64), **stale-binary guard** (aborts if any source file is newer than the published `client.dll`), bundles `publish/*.sh` + `*.iss`, encrypts config, calls platform builders |
| `build-deb.sh` | Linux `.deb` (prerm kills running instances) |
| `build-dmg.sh` | macOS `.dmg` |
| `installer-windows.iss` | Inno Setup (auto-kills running processes) |
| `generate-windows-vars.sh` | Reads `APP_IDENTIFIERS` + `VERSION` → `windows_vars.iss`, so the Inno script carries no literal product name |
| `release.sh` | build + copy installers into `server/setup/v<VERSION>.<ext>` |
| `encrypt-config.sh` | `.env` → `config.enc` (transport key) |
| `firefox-a11y-apparmor.sh` | **snap Firefox fix**: loads a surgical copy of the snap AppArmor profile adding ONE `dbus (receive)` rule so the AT-SPI bridge can read snap-Firefox windows (sandbox stays enforcing); installs `alpha-ai-firefox-a11y.service` to re-apply at boot / after `snap refresh`; `--undo` supported; requires Firefox restart |

> **No `extensions/`** — browser tracking is accessibility-based (embedded in the binary); `install-extensions.sh` was deleted 2026-08-05.

### ⚠️ Build-Parity Rule (mandatory) — `dotnet run` ≠ installed build

Installed builds ship the **publish output** plus only what the scripts bundle, from a root-owned install dir. **A change is NOT done until it works from an installed build.**

1. **Always ship-test** — build the installer, install it, run the feature there.
2. **New runtime assets** (icons, JSON, images) — must be added to `bundle_into_publish()` / `build-deb.sh` / `build-dmg.sh` / `installer-windows.iss`.
3. **New scripts in `publish/`** — copied into every publish output automatically.
4. **New env vars** — add to `.env` BEFORE `encrypt-config.sh`; installers ship `config.enc` baked at build time (auto-propagation now replaces stale user copies on next launch).
5. **Path assumptions** — never write relative to cwd/exe dir (root-owned). Use `~/.config/alpha-ai-tracker/` (logs, machine-id) and `~/.local/share/alpha-ai-tracker/` (DB).
6. **Packaging edits apply to ALL platforms** — update all four builder scripts together.
7. **Stale-binary guard** — `build-installer.sh` aborts if source is newer than published `client.dll`; fix with `dotnet clean && bash publish/build-installer.sh`. Covers compiled code only — items 2–6 are the developer's responsibility.

### Branding & version — one file each, two consumers each

`client/APP_IDENTIFIERS` and `client/VERSION` are the only places a product name or version number is
written. Both feed **two** paths from that single edit:

| File | Build-time consumer | Runtime consumer |
|---|---|---|
| `APP_IDENTIFIERS` | `build-deb.sh`, `build-dmg.sh`, `generate-windows-vars.sh` → `installer-windows.iss`, `release.sh` — package names, bundle id, desktop entry, publisher | `client.csproj` embeds it as `client.APP_IDENTIFIERS`; `Core/AppInfo` parses it and the UI binds to `AppDisplayName`, `AppTagline`, `AppInitials`, `AppCopyright` |
| `VERSION` | installer filenames and package metadata | `client.csproj` sets `Version` / `InformationalVersion`; `AppInfo.Version` reads it back (stripping any `+sha`), surfaced as `AppVersionDisplay` and the window title |

Two consequences worth knowing before you touch either file:

- **The embedded resource lives inside `client.dll`,** so branding needs no `bundle_into_publish()` entry
  — installer parity for item 2 above is automatic. Same for `Assets/backgrounds/*.png`, which the
  existing `<AvaloniaResource Include="Assets\**" />` glob already compiles in.
- **`client.csproj` reads `VERSION` at project-evaluation time,** not in a `BeforeBuild` target. That is
  deliberate: a `BeforeBuild` read would leave IDE design-time builds silently on `1.0.0`.
- ⚠️ **`Core/EncryptedConfigService.cs` `TransportKeySeed` / `MachineKeyPrefix` are NOT branding.** They
  are key-derivation seeds. Re-branding must leave them byte-for-byte alone — changing them makes every
  `config.enc` already deployed in the field undecryptable.

---

## 16. Production-Readiness Gaps

| Gap | Severity | Details |
|---|---|---|
| **No crash handling** | 🔴 High | No `AppDomain.UnhandledException` subscription, no crash dumps, no telemetry. |
| ~~**No auto-update**~~ (resolved 2026-08-12; local setup 2026-11-07) | — | `AppUpdateService` + Windows `SilentUpdateWorker` poll `GET /api/v1/releases` every `ALPHA_UPDATE_CHECK_INTERVAL_SEC` (default 60s); Windows silent Inno via LocalSystem; Linux polkit / macOS dmg. |
| **SQLite unencrypted** | 🟠 Medium | `ALPHA_DB_ENCRYPTION_KEY` exists but sqlcipher is not wired in. |
| **macOS CPU = 0%** | 🟠 Medium | macOS collector does not measure CPU. |
| **macOS window capture limited** | 🟢 Low | Only foreground window via osascript; no EnumWindows equivalent. |
| **No storage quota** | 🟢 Low | No periodic pruning of old synced rows (only `permission_status` 24h prune + boot cleanup passes). |
| **No retry backoff** | 🟢 Low | Sync retries every ~5 min regardless of failure count. |
| **Single employee per device** | 🟢 Low | One login at a time; logging in as another employee replaces it. |
| **Browser journey = active tab only** | 🟢 Low | Background tabs are not captured (a11y exposes the active tab; history fallback is title-match). Per-window journey is exact; per-tab parallelism is not. |
| **Tray has no Quit** | 🟢 Low | Tray menu = Show / Hide only; window close hides to tray. Full exit via process stop (systemd); there is no in-app disconnect/logout. |

---

## 17. Immediate Next Steps

1. **Add tests** — start with `SqliteLogStore`, `ProcessFilter`, `PackageDetector`, `AccessibilityBrowserTracker` rotation logic.
2. **Add crash reporting** — subscribe to `AppDomain.CurrentDomain.UnhandledException`, write to the log file.
3. **Enable SQLite encryption** — wire `ALPHA_DB_ENCRYPTION_KEY` into the provider (sqlcipher).
4. **Fix macOS CPU + window capture** — sample `TotalProcessorTime`; enumerate windows beyond the foreground.
5. **Add periodic data cleanup** — prune old synced `app_sessions`/`app_items` locally.
6. **Offline retry with backoff** — exponential backoff on sync failures.
7. ~~**Consider auto-update**~~ (resolved 2026-08-12 — built-in `AppUpdateService`, §18).
8. ~~Remove dead shell-command code~~ ✅ (deleted 2026-08-05)
9. ~~Debugger/extension browser pipeline~~ ✅ (replaced 2026-08-05 by Option B accessibility)
10. ~~File-explorer journey watchers~~ ✅ (2026-07-29)
11. **Installed-build acceptance** — rebuild the installer and re-verify the a11y journey + private-window URLs from the installed build (the 2026-08-07 verification passed; re-run after the next code change).
12. **Ship-test the six-page GUI** — the hero images and `APP_IDENTIFIERS` both ride inside `client.dll`, so no packaging change was needed, but that has not yet been confirmed from an installed build. Per the Build-Parity Rule this is the remaining acceptance step for the 2026-08-10 redesign.
13. **Re-brand smoke test** — change `DISPLAY_NAME` in `APP_IDENTIFIERS`, bump `VERSION`, rebuild, and confirm the rail, window title, splash, footer, tray tooltip and installer filenames all follow with no other edit.

---

## 18. Self-Update via public releases bridge (`AppUpdateService` + Windows `SilentUpdateWorker`)

> Added 2026-08-12 (GitHub direct). **Updated 2026-11-07:** GitHub is gone end-to-end. The server
> serves installers from its local `setup/` folder (`RELEASES_DIR`, e.g. `setup/v1.0.0.exe`) on the same
> `/api/v1/releases` + `/downloads/:id` contract; `release.sh` copies installers there. Service and GUI
> check every `ALPHA_UPDATE_CHECK_INTERVAL_SEC` (60s) and install a newer version themselves. A failed
> silent apply writes `updates\apply_failed.txt`; that version is not retried for 1h (otherwise the helper
> would stop the tracker every minute). Linux auto-install is throttled to once/hour/version (polkit prompt).
>
> **Updated 2026-10-27 — Chrome-like Windows silent updater:** download + Inno apply moved into
> `AlphaAITracker.Service` (`SilentUpdateWorker`, LocalSystem). No mid-update UAC after the first
> admin install. Tracker keeps running during Range download; a detached SYSTEM helper applies
> `/VERYSILENT`. **Updated 2026-10-30:** post-update celebration toast removed (no
> `UpdateCelebrationWindow` / no `--celebrate-update` UI); leftover `celebrate.json` is cleared only.

### Flow

1. `GET {ALPHA_SERVER_URL}/api/v1/releases` (public; no device/JWT auth).
2. Prefer `latestStable`, else first non-prerelease; normalize `tagName` (`v1.1.0` → `1.1.0`).
3. Pick the installer for this platform from bridge `assets[].platform` (windows/linux/macos), with
   Linux arch preference (`_amd64.deb` / `_arm64.deb`). No asset → "No installer for this platform".
4. Compare vs installed version. Newer → proceed.
5. **Windows (service that understands the pipe):** Range-download to `%ProgramData%\<APP_MUTEX>\updates\`
   while the tracker runs; a detached SYSTEM PowerShell helper pre-stops the DLP service, runs Inno
   `/VERYSILENT` (no `RunAs`; AppLocker SYSTEM allow for `updates\*`), clears `update_in_progress`,
   writes `relaunch_tracker`, and `sc start`s only if Inno did not leave the service RUNNING
   (Inno `InstallDlpService` normally creates+starts; watchdog relaunches the tracker). No
   celebration toast. The GUI polls `GetUpdateStatus` and does not elevate.
   **Windows (installed service that answers `unknown_op`):** the GUI downloads the same bridge
   asset, requires the file length to match `asset.size` when that size is non-zero, then
   `Start-Process -Verb RunAs` (explicit UAC, not a silent bypass). `update_disabled`,
   `updateInProgress`, and a pipe that does not answer do not start that installer.
   There is no Authenticode check on the downloaded exe.
6. **Linux (client):** download to user `updates/` → `pkexec dpkg -i` → `--background --restart`.
7. **macOS:** `open` the dmg — manual drag to Applications.
8. `--restart` in `Program.cs`: retry the single-instance mutex for up to 8s so the new binary can take over.

### GUI

- Top bar: **Check updates** ghost button (always visible), **"Update to vX.Y.Z"** when a manual
  check finds a newer build (Windows: queues the service; Linux: download/install).
- Dashboard update banner: Linux/manual surfaces; Windows auto-path stays silent (no UAC banner).
- Both surfaces bind the SAME singleton `AppUpdateService` instance (injected into `MainViewModel`
  and `DashboardViewModel`).

### Background loop

- **Windows service:** `SilentUpdateWorker` every `ALPHA_UPDATE_CHECK_INTERVAL_SEC` (default 60s);
  independent of `ALPHA_DLP_ENABLED`; pipe ops `GetUpdateStatus` / `RequestUpdateCheck` always
  available on Windows.
- **Client:** still auto-checks; on Windows auto-install defers to the service pipe.

### Config knobs

| Key | Default | Meaning |
|---|---|---|
| `ALPHA_SERVER_URL` | required | API base used for `/api/v1/releases` + `/downloads/:id` |
| `ALPHA_UPDATE_ENABLED` | true | master switch (service worker + GUI no-op when false) |
| `ALPHA_UPDATE_CHECK_INTERVAL_SEC` | 60 | seconds between background checks |
| `ALPHA_UPDATE_AUTO_INSTALL` | true | auto-download+install without a click |
| `ALPHA_UPDATE_DOWNLOAD_JITTER_SEC` | 3600 | stable per-machine delay window before silent download (fleet admission); GUI force ≤30s |

⚠️ **Installers are not code-signed / checksum-verified** — the download is trusted over the API TLS
(only size is checked against the release list). Signing is a future hardening step. First install still needs
admin once (`PrivilegesRequired=admin`); subsequent updates do not prompt UAC.

## 19. Time & Attendance (Phase 1 — client foundation)

> Added 2026-08-28. Builds the client half of the **Time & Attendance** sidebar module. The web
> dashboard (Phase 2) renders this data via new server endpoints. This section is the client
> foundation only; it is fully transport-independent and ships behind `ALPHA_TA_ENABLED`.
> See the repo-root `finalplan.txt` (§0–§15) for the authoritative build-order + rule audit.

### Vocabulary (single source of truth, `Core/Models/SessionEvent.cs`)

The `SessionEventTypes` static class is the ONE place every session_events `event_type` string
lives on the client (finalplan R5). Go and web mirrors exist; run `test/contract-event-types.sh`
to catch drift.

| Constant | event_type | Emitter |
|---|---|---|
| `PowerOn` | `power_on` | `LogCollectorService.StartTracking` + `SystemEventWatcher` boot |
| `PowerOff` | `power_off` | `ShutdownSentinel` (SIGTERM / Ctrl+C / Avalonia exit / dispose) |
| `Resume` | `resume` | `SystemEventWatcher` (UPower, SystemEvents resume) |
| `OsLogin` | `os_login` | `SystemEventWatcher` (Windows SessionSwitch) |
| `OsLogout` | `os_logout` | `SystemEventWatcher` (Windows SessionSwitch) |
| `ScreenLock` | `screen_lock` | `SystemEventWatcher` (login1 / GNOME ScreenSaver / SessionSwitch) |
| `ScreenUnlock` | `screen_unlock` | `SystemEventWatcher` (login1 / ScreenSaver / SessionSwitch) |
| `TrackerLogin` | `tracker_login` | `LogCollectorService.StartTracking` |
| `UiHidden` | `ui_hidden` | `App.axaml.cs` window.Closing (hide-to-tray) |
| `IdleStart` | `idle_start` | `IdleDetector` (threshold crossing) |
| `IdleEnd` | `idle_end` | `IdleDetector` (threshold crossing) |
| `OldDataDropped` | `old_data_dropped` | `SyncService` S6 row-ceiling rollup |

`Login` is kept as an `[Obsolete]` alias of `TrackerLogin` for back-compat with pre-2026-08-28 rows.

Cross-service mirrors: `server/internal/models/session_event_types.go`, `web/src/lib/eventTypes.ts`.
Contract test: `test/contract-event-types.sh` (T5).

### Services

| Service | Type | Cadence | Purpose |
|---|---|---|---|
| `SessionEventRecorder` | `IEventRecorder` singleton | — | Single funnel for ALL session_events writes; 5 s dedup window (burst collapse) + hard 2 s write timeout |
| `ShutdownSentinel` | hosted (FIRST in DI) | — | Writes `power_off` before the host stops; background mode uses `WaitForShutdownAsync` so SIGTERM reaches hosted-service shutdown, then `ManualResetEventSlim` bounds the final write wait |
| `SystemEventWatcher` | hosted | event-driven | Linux D-Bus (UPower / login1 shutdown+lock / GNOME ScreenSaver), Windows `SystemEvents`, macOS stub; login1 `PrepareForShutdown(true)` persists before Xwayland teardown; touches `ta_last_known_os_event_at` watermark |
| `IdleDetector` | hosted | 30 s poll | OS idle source (Mutter.IdleMonitor / X11 / GetLastInputInfo); emits `idle_start`/`idle_end` crossings |
| `ScheduleCacheService` | hosted | login/resume + every 6 h | Mirrors the Phase 2 `GET /api/v1/schedules/me` response into local tables; login wakes it immediately and resume waits 10 s for network stabilization |
| `LocalTimeSkewService` | hosted | startup/resume + every 15 min | Measures client↔server clock skew from `GET /api/v1/server-time`'s Date header; resume waits 10 s before measuring |
| `TimeDateWarningWorker` | hosted | start + every `ALPHA_TA_TIMEDATE_CHECK_HOURS` (default 1h) | If `|skew|≥SKEW_SEC` or OS UTC offset ≠ IP/schedule IANA zone → write `timedatewarning/state.json` and spawn `client --timedatewarning` (sibling mutex). Never sets OS clock. Parked when `ALPHA_TA_ENABLED` or `ALPHA_TA_TIMEDATE_WARN_ENABLED` is false |
| `AttendanceAggregator` | hosted | login + every 5 min | Rolls up arrival/last-seen, the union of idle+screen-lock time, active time, schedule overlap, holidays, lateness, absence, half-day, and off-shift time; legacy offset timestamps are normalized to UTC at the store boundary |

DI order (finalplan §3): `ShutdownSentinel` is registered FIRST so .NET stops it LAST — guaranteeing
`power_off` is written while the SQLite singleton is still alive.

### SQLite additions (made idempotent in `DatabaseSchema.CreateTableSql`)

- `employee_schedule` (PK employee_id) — mirrored shift (timezone, weekly_pattern JSON, grace_minutes, validity).
- `company_holidays` (PK holiday_date) — mirrored holiday calendar.
- `daily_attendance_cache` (PK employee_id, work_date) — DERIVED, client-owned, NEVER sent to server.
- `local_time_skew` (PK server_url) — per-server clock-skew measurement.
- Indexes: `(event_type, event_at)` on session_events, `(employee_id, work_date)` on
  `daily_attendance_cache` (finalplan S5).

### Concurrency (finalplan R8 / BUG-9)

With 6 hosted services hitting SQLite, the original single-connection `SemaphoreSlim(1,1)` would
serialize everything and risk deadlock. `Program.cs` initializes the store before starting hosted
services (so the first boot event cannot race SQLite); `SqliteLogStore.InitializeAsync` is
serialized/idempotent and:
1. enables `PRAGMA journal_mode=WAL;` + `synchronous=NORMAL` FIRST, then
2. opens a second **ReadOnly** connection (`WithReadConnectionAsync`) for pure readers
   (`AttendanceAggregator`, schedule/holiday/skew reads).

Writers (collector, sync) still use the gated write connection; readers use the read connection, so
collection never blocks aggregation and vice versa.

### Feature flag & config

| Key | Default | Meaning |
|---|---|---|
| `ALPHA_TA_ENABLED` | true | master switch; false parks SystemEventWatcher, IdleDetector, ScheduleCacheService, LocalTimeSkewService, AttendanceAggregator, TimeDateWarningWorker |
| `ALPHA_TA_TIMEDATE_WARN_ENABLED` | true | spawn `--timedatewarning` when clock/timezone unhealthy |
| `ALPHA_TA_TIMEDATE_CHECK_HOURS` | 1 | hours between checks (first check is immediate at start) |
| `ALPHA_TA_TIMEDATE_SKEW_SEC` | 60 | absolute skew vs server UTC that counts as unhealthy |
| `ALPHA_TA_TIMEDATE_COOLDOWN_HOURS` | 4 | after Dismiss / window close, do not re-spawn for this long |
| `ALPHA_IDLE_THRESHOLD_SEC` | 120 | seconds of no input before `idle_start` |
| `ALPHA_IDLE_AWAY_THRESHOLD_SEC` | 600 | reserved for A.8 away classification |
| `ALPHA_IDLE_POLL_SEC` | 30 | idle-source poll cadence |
| `ALPHA_TA_LOCK_HYSTERESIS_SEC` | 30 | suppress re-locks within this window |
| `ALPHA_EVENT_AGGREGATION_WINDOW_SEC` | 300 | sync-time bucket size for session_events aggregates (S1) |
| `ALPHA_TA_MAX_LOCAL_ROWS` | 50000 | unsynced row ceiling; excess rolls into `old_data_dropped` (S6) |

### Session-event sync aggregation (A.9 / A.10)

Raw OS events are written one-per-row to SQLite immediately (BUG-13). `SyncService` groups
unsynced rows into rolling buckets per `(event_type, window)` when the bucket window has fully
elapsed, then POSTs `{ count, firstAt, lastAt }` to `/api/v1/session-events/sync`. Closed buckets
only — the current open window stays local until it closes. `SessionEventSyncAggregator` performs
the grouping; `DrainSessionEventsAsync` marks every source row `is_synced` after a successful send.

### Phase 1 → Phase 2 handoff

- **Server (Phase 2, implemented 2026-08-31):** `GET /api/v1/schedules/me` (SVR-1),
  `GET /api/v1/server-time` (SVR-3), `GET /api/v1/attendance/today|range` (SVR-4/5), holiday
  CRUD, and aggregate-compatible `session-events/sync` fields `{count, firstAt, lastAt}` (SVR-2).
- **Client (A.9/A.10, implemented 2026-09-01):** `SessionEventSyncAggregator` + `DrainSessionEventsAsync`
  aggregate unsynced rows at sync time (`ALPHA_EVENT_AGGREGATION_WINDOW_SEC`, default 300 s).
  S6 ceiling via `ALPHA_TA_MAX_LOCAL_ROWS` + `old_data_dropped` sentinel. Mirrors:
  `server/internal/models/session_event_types.go`, `web/src/lib/eventTypes.ts`;
  `test/contract-event-types.sh` (T5).
- **Client follow-up:** backward compatible — servers without aggregate columns still accept
  count=1 rows; Phase 2 server expects `{count, firstAt, lastAt}`.
- **Web (Phase 2, live):** `/attendance`, `/timesheets`, `/holidays` call the attendance/holiday
  APIs with infinite scroll. First/last active times use `record.timezone` from the server (shift
  IANA zone) — operators must set `DEFAULT_SHIFT_TIMEZONE` or per-shift timezone on the server so
  late/present matches wall-clock. `gps-location` UI is gated Coming Soon (`LOCATION_UI_ENABLED`).

---

## 20. Live stream (WebRTC SFU) + presence WebSocket

### Live stream (preview only — never persisted)

| Piece | Role |
|---|---|
| `ScreenCaptureService` | Win GDI BitBlt / Linux portal+PipeWire → BGRA (`DropOldest(1)`); `select_monitor`; Win11 fallbacks |
| `ScreenVp8Encoder` | Windows: `vpxmd` (**ConfigDefault then** bitrate / VBR / `1/fps` / `KfMaxDist`); Linux: `LinuxGstVp8Encoder` (`vp8enc`) |

| `NetProbeService` | Timed POST to `/live-stream/uplink-probe`; `SelectBitrateKbps` ladder under max bitrate |
| `LiveStreamClient` | DeviceAuth WS push; VP8 after `start` + PC `connected`; ABR; `force_keyframe`; stream_* app_status telemetry |
| `ClientIdentityHeaders` | `X-Client-Version` / `X-Client-Platform` on DeviceAuth HTTP + WS |

Flow: login → push WS `hello` → idle until admin watches → server `start` → NetProbe uplink → pick ladder rung (or skip if below min) → offer/answer/ICE → media pump (degrade on backpressure) → `stop` tears down PC/encoder.

CLI: `client --self-test-stream-ladder` exercises `SelectBitrateKbps` without a full publish.

### Presence (`WsClient`)

Long-lived DeviceAuth socket to `GET /api/v1/ws`. Independent of `LiveStreamClient` (same pattern as Terms vs tracking). Drives admin Online/`wsConnected` without opening a media path.

### Quality notes

- Do **not** force a keyframe every frame — starves the bitrate budget (blocky UI text). Server PLI → one `force_keyframe` is enough.
- Do **not** use stock `VpxVideoEncoder` alone — SIPSorcery wiped `TargetKbps` until `ScreenVp8Encoder`.
- Hall-of-mirrors (watching yourself) stacks compression; judge quality on sharp desktop UI outside the admin browser.
- Re-bake `config.enc` after any `ALPHA_STREAM_*` / `ALPHA_WS_*` change (Installer-Parity).
