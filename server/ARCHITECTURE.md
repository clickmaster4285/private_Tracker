# Server Architecture — Alpha AI Tracker API

> **Last audited:** 2026-11-12 (face enrollment UX + server FaceWorker)
> **Changelog:**
>
> - 2026-11-12: **face_worker is Python** under `server/face_worker` (`worker.py`, OpenCV).
>   Same internal claim/result/complete HTTP contract; not the Avalonia agent.
>
> - 2026-11-12: **Face enrollment retry + gallery + face_worker under `server/face_worker`.**
>   JWT `POST /employees/:id/face-enrollment/:jobId/retry`, `GET …/face-profile/samples`;
>   per-sample `process_status` (069). Worker is server-side only (not Avalonia).
>   RBAC route `users/face-enrollment` → `/employees/face`. Embeddings still deferred
>   (`embeddings_deferred`) until AdaFace ONNX license (`doc/face-preprocess-v1.md`).
>
> - 2026-11-12: **Face enrollment APIs + migration 068.** JWT multipart enrollment uploads original
>   images to `FACE_MEDIA_DIR`; `face_enrollment_jobs` queue; FaceWorker claim/fail under
>   `/api/v1/internal/face-worker/*` (`FACE_WORKER_API_KEY`). DeviceAuth `GET /face-profiles/me`,
>   `POST /face-profiles/sync-ack`, `GET /face-models/manifest`. RBAC `users/face-enrollment`.
>   ONNX embedding generation still blocked on license gate (`doc/face-preprocess-v1.md`).
>
> - Prior: 2026-11-11 (Edge/Chrome allow path widen)
>
> **Last audited (prior):** 2026-11-11 (Edge/Chrome allow path widen)
> **Changelog (continued):**
> - 2026-11-11: **Browser allow paths widen to `Application\*`.** `WidenBrowserAllowPath`
>   (repo) turns Edge/Chrome/EdgeWebView single-EXE inventory paths into
>   `…\Application\*` so AppLocker covers versioned helpers after updates. Applied in
>   `resolveInstallIdentity`, `ExpandEnabledAllowApps`, and `CreateAllowApp`.
>   `wellKnownWindowsAllowPath` adds Microsoft Edge fallback
>   `C:\Program Files (x86)\Microsoft\Edge\Application\*` when inventory has no path.
>   Windows inventory join appends `.exe` when `binary_name` lacks an extension.
>   Verified: `go build`/`go vet` repository+services. Restart API so Publish re-expands.
> - 2026-11-10: **Screenshots store + API.** Migration **065** `screenshots` index. Files under
>   `SCREENSHOTS_DIR` (`media/screenshots/{emp}/{yyyy}/{MM}/{dd}/`). DeviceAuth
>   `POST /screenshots/upload` (multipart WebP, size cap, `RATE_LIMIT_SCREENSHOT_*`); JWT
>   `GET /screenshots` (cursor infinite scroll) + `GET /screenshots/:id/image` (`ServeContent`).
>   **No retention purge** this ship. Verified: `go build`/`go vet`.
> - 2026-11-10: **Scale foundation (one Postgres).** `admit` rate limits (login/sync/admin GET) +
>   sync concurrency gate (`503`/`429` + `Retry-After`); Redis `cache` for dashboard summary;
>   Asynq embedded worker/scheduler (or `cmd/worker`) for session/retention/staleness with
>   `ASYNQ_CONCURRENCY` cap; `LIVE_STREAM_MAX_STREAMS` default 100; `ScaleConfig` env knobs.
>   Verified: `go build`/`go vet`, `go test` admit+cache.
> - 2026-11-10: **Allow-app create resolves usable Windows paths.** `CreateAllowApp` accepts an
>   optional path override; maps well-known Windows Settings to
>   `C:\Windows\ImmersiveControlPanel\SystemSettings.exe` + Appx product
>   `windows.immersivecontrolpanel_cw5n1h2txyewy` when inventory only has Linux
>   cinnamon/gnome entries. `ResolveCatalogApp` prefers active inventory paths, falls back to
>   inactive, and drops bare shell tokens AppLocker cannot use (`isUsableAllowPath`). Duplicate
>   catalog allow is idempotent (`GetAllowAppByCatalogID` → return existing). Handler maps
>   catalog/path client errors to HTTP 400 via `isAllowAppClientErr`.
> **Last audited:** 2026-11-07 (hardware & port control)
> **Changelog:**
> - 2026-11-07: **Hardware & Port Control.** Migration **066** (`machine_ports` inventory;
>   `device_control_rules` with `scope`/`scope_value`; `dlp_policy_device_control` snapshot) + **067**
>   (idempotent re-create; renumbered after screenshots **065**). New `PortRepo`/`PortService`/`PortHandler`:
>   DeviceAuth `POST /hardware-ports/sync` (machine-scoped), JWT `GET /hardware-ports` +
>   `/device-control/rules` CRUD (auto-publish via `DlpService.Publish`). `DlpRepo.AssembleBundleJSON`
>   emits `deviceControl`; `Publish` calls `SnapshotDeviceControl` (global rules); `PullPolicy` overlays
>   employee/machine/department rules per device (`ResolveScopedDeviceControl` + `mergeDeviceControl`).
>   Verified: `go build`, `go vet`. Restart API for 066/067.
> - 2026-11-07: **Releases from local `setup/` folder; GitHub bridge removed.** `GitHubReleaseService`
>   + `GITHUB_*` env deleted. `LocalReleaseService` scans `RELEASES_DIR` (default `setup`) and keeps the
>   same `/releases` + `/downloads/:assetId` contract (web + clients unchanged on the wire). Downloads via
>   `http.ServeContent` (Range/HEAD). Verified: `go build`, `go vet`, `go test` services+handlers.
> - 2026-11-05: **P3 catalog app allow.** Migration **064** `installed_application_id` /
>   `app_name` / `install_path` on draft + snapshot apps. Create accepts catalog id;
>   Publish `ExpandEnabledAllowApps` emits path-first identities into `allow.apps`.
> - 2026-11-05: **Allowlists Active/Inactive.** Migration **063** `enabled` on draft
>   hosts/apps; list `?enabled=true|false`; Publish snapshots only `enabled=TRUE`.
> - 2026-11-05: **P1 lockdown contract.** Migration **059** draft + snapshot tables;
>   `GET|PUT /dlp/lockdown`; Publish embeds `lockdown` / `platformAllow` / `allow`;
>   `GET /dlp/policy` returns `lockdownPausedUntil`; `POST .../lockdown-pause` (max 4h).
> - 2026-11-05: **Tracking mode deleted.** Migration **058** drops the 056 control plane.
>   DeviceAuth `/devices/control` and `/devices/tracking-events`, JWT
>   `PATCH .../tracking-mode` and `/device-configuration/*` are unregistered. Sync responses
>   no longer carry `controlEpoch`. Employee detail `devices[]` no longer projects mode /
>   desiredState / phase. RBAC seeder deletes `configuration/device-configuration`.
> - 2026-11-05: **Terms & Conditions removed.** Migration **057** drops `terms_consent` and
>   `terms_content`. DeviceAuth `/terms-content/active` and `/terms-consent/sync`, and the
>   JWT `/terms-content` and `/terms-consent` routes, are unregistered. Live-stream push,
>   watch, and the employee list no longer check consent. Dashboard `live.consentMissing`
>   is gone. RBAC seeder deletes `settings/terms-and-conditions`.
> - 2026-11-03: **Attendance / timesheets Last Seen while the tracker is still online.**
>   `attendanceForDay` `lastActiveAt` was max(session_events, single `app_status.last_heartbeat_at`
>   value) — sparse OS events + a stalled heartbeat left Last Seen / Active frozen (e.g. power_on
>   09:17) while Live Stream (presence WS) and App Usage still showed the employee working.
>   **Fix:** `GetLastHeartbeat` takes the freshest across machines of GREATEST(value, updated_at);
>   `GetLastDeviceSeen` + `extendLastActive` also fold non-revoked `employee_devices.last_seen_at`
>   (DeviceAuth `TouchLastSeen` on every sync) into the work-day window. Client 2.3.25 dual-stamps
>   heartbeat on sync passes. Server-only deploy heals live Last Seen without a new installer.
> - 2026-10-31: **Device Configuration JWT admin APIs** under `/api/v1/device-configuration`:
>   `GET /roaming-machines` (employee-grouped: `employees[].current` + `history[]`, paginate
>   by employee), `GET /mode-types` (Fixed/Roaming copy + org defaults), `GET /tracking-events`
>   (Start/Stop audit log). RBAC submodule `configuration/device-configuration`. Ingest
>   `POST /devices/tracking-events` normalizes flush (`ok`→`complete`, `timeout`→`partial`)
>   and still requires UUID `clientEventId`.
> - 2026-10-28: **Device tracking control P0 (Fixed/Roaming spine).** Migration **056** —
>   `device_tracking_policies` (org seed `fixed`, `keep_dlp_when_stopped` default true),
>   `device_control_meta.global_epoch`, `device_tracking_events`, `device_policy_audit`,
>   control columns on `employee_machines`. DeviceAuth `GET /devices/control` +
>   `POST /devices/tracking-events`; sync `controlEpoch` piggyback. See `newplan.md`.
> - 2026-10-26: **Public releases bridge for a private GitHub repo.** Env `GITHUB_OWNER` /
>   `GITHUB_REPO` / `GITHUB_TOKEN` / `GITHUB_API_VERSION` / `GITHUB_RELEASES_CACHE_TTL` on
>   `config.GitHub`. `GitHubReleaseService` paginates Releases API, caches metadata, classifies
>   `.exe`/`.deb`/`.dmg`, streams assets via `GET|HEAD /api/v1/downloads/:assetId` (no open proxy;
>   token never leaves the server; redirect auth strip + host allowlist). Public
>   `GET /api/v1/releases` returns `latestStable` + published releases (drafts excluded). Soft
>   download rate limit in `ReleaseHandler`. Empty token → 503 on these routes only. See
>   `doc/plan.md`. Verified: unit tests + `go build` / `go vet`.
> - 2026-10-24: **Classic DLP rule revisions** — migration **055** `dlp_rule_revisions` +
>   `dlp_revision_extensions` / `dlp_revision_patterns`; snapshot before each `PATCH /dlp/rules/:id`;
>   `GET /dlp/rules/:id` + `GET /dlp/rules/:id/history`.
> - 2026-10-24: **Web Blocker** — migration **054** revision child tables (relational, no TEXT[]);
>   `ExpandRelatedDomains` + `POST /web-blocker/domains/expand`; `GET /web-blocker/rules/:id/history`;
>   create/update `expandRelated`; EvidenceMap alert sync 400 fix (nested JSON → string).
> - 2026-10-22: **Security & DLP spine + alert triage history (migrations 051/052).**
>   Migration **051** adds `dlp_rules` (editable), `dlp_policy_versions` (immutable published
>   bundles), `dlp_alerts` (device-synced inbox), `shadow_it_apps`, `removal_challenges`
>   (SHA-256 of one-time codes only). Migration **052** adds `dlp_alert_events` (per-field triage
>   audit on Save). New `DlpRepo` / `DlpService` / `DlpHandler` + DTOs/models. DeviceAuth:
>   `GET /dlp/policy`, `POST /dlp/alerts/sync`, `POST /dlp/health`, `POST /devices/removal-verify`.
>   JWT: rules CRUD + `POST /dlp/rules/publish`, `GET|PATCH /dlp/alerts`, `GET /dlp/alerts/:id/history`,
>   `GET|PATCH /shadow-it/apps`, `POST /devices/removal-code`. Env `DLP_REMOVAL_CODE_TTL_MIN`
>   (default 60). RBAC `security-dlp` submodules already seeded. Verified: `go build` / `go vet`;
>   publish smoke test in `dlp_publish_smoke_test.go`. See *DLP Policy Version Rule* /
>   *Protected Removal Rule* in AGENTS.md §6.
> - 2026-10-19 (late): **Client E2E on the installed build — post-wipe re-login verified end-to-end.**
>   Earlier in the day the new code shipped in `AlphaAITracker-Setup-1.2.36.exe` was smoke-tested on
>   the very machine producing the report: `client.exe --remove-account` (admin UI tell, not web admin)
>   drained pending rows, called `/api/v1/devices/self-remove`, and the server flipped employee MU-90
>   to `tracking_status='untracked'` / `is_online=false` and machine `6e27923275124a1b801fac207aa40bf0`
>   to `old` with an advanced `last_track_at`. The same-process GUI re-login (new secret, keyboard
>   fill) minted a fresh machine id (`1b39f2a22ad4497eab549907d2d0b627`) that returned `active`, and
>   within ~12 seconds WITHOUT any client restart the `RecollectAfterLoginAsync` path had already pushed
>   a `device_hardware_info` snapshot, a `network_info` row, `storage_devices` and 74
>   `employee_installed_applications` links for the new machine — the precise fix for the "No hardware
>   snapshot synced yet" symptom. employee tracking restored to `tracked`. No server change; confirming
>   the client-path contract so the devs don't re-derive the same fix.
> - 2026-10-19: **Machine lifecycle registry (`employee_machines`), full `machine_id` coverage, and the remove-device / remove-account handshake.**
>   Migration **049** adds `employee_machines` — one row per `(employee_id, machine_id)` with `status`
>   (`active`|`old`), `first_track_at`, `last_track_at`, `wipe_requested_at`, unique partial index on
>   `(employee_id, machine_id) WHERE deleted_at IS NULL`, plus a backfill from `employee_devices`
>   (`active` when any registration is unrevoked, `MIN(created_at)` / `MAX(last_seen_at)` as the
>   window). It exists because `employee_devices` is the AUTH-TOKEN registry and `UpsertDevice`
>   revokes + re-inserts on every login, so a machine list built from it duplicated machines. New repo
>   helpers in `repository/device_repo.go` (`UpsertEmployeeMachineActive`,
>   `TouchEmployeeMachineLastTrack`, `MarkEmployeeMachineOld`, `MarkAllEmployeeMachinesOld[WithWipe]`,
>   `ListEmployeeMachines`). `EmployeeLogin` upserts the machine `active` (preserving
>   `first_track_at`); `DeviceAuth` advances `last_track_at` alongside `TouchLastSeen`.
>   Migration **050** adds `machine_id` to `session_events`, `app_items` and `location_samples`
>   (+ `(employee_id, machine_id, …)` indexes, 047-style attribution from the device activity window;
>   `app_items` attributes exactly from its parent `app_sessions.machine_id`). `session_events` /
>   `app_items` / `location_samples` sync requests gained `MachineID` (`json:"-"`) filled by the
>   handler from `DeviceAuth` — never trusted from the body. `terms_consent` stays employee-scoped.
>   **Remove-device (R6):** `DeviceAuth` now resolves revoked tokens too
>   (`GetByTokenHashIncludingRevoked`), sets `device_removed` and skips `TouchLastSeen`; every sync
>   handler passes its response through `applyDeviceAction`, which appends `action="wipe_local"`
>   **after** the batch was persisted. `RemoveEmployeeDevices` additionally marks the employee's
>   machines `old` + `wipe_requested_at`. **Remove-account (R4/R5):** new DeviceAuth endpoint
>   `POST /api/v1/devices/self-remove` → `NewSchemaService.RemoveAccount` (mark the calling machine
>   `old`, revoke devices, close open activity, `tracking_status='untracked'`), returns
>   `SelfRemoveAccountResponse`. `GET /employees/:id/detail` now maps `devices[]` from
>   `employee_machines` (deduped) and each entry carries `status`, `firstTrackAt`, `lastTrackAt`,
>   `wipeRequestedAt`. Verified live on the dev DB: 049/050 applied; login → `active`; hardware /
>   session-events / location syncs carried the device's `machine_id`; admin remove → `old` +
>   `wipe_requested_at` and the revoked token's next batch was persisted **and** answered
>   `{"action":"wipe_local"}`; second login + `self-remove` → `{removed:true,…,"trackingStatus":"untracked"}`;
>   detail listed one row per machine with status. `go build` / `go vet` / `go test ./internal/...` clean.
> - 2026-10-17: **Dashboard summary now carries today's attendance (`attendance` block).**
>   `DashboardSummaryResponse` gains `attendance` (`date`, `total`, `present`, `late`, `absent`,
>   `halfDay`, `offShift`, `unknown`). New `TimeAttendanceService.AttendanceDayCounts(ctx, employeeIDs)`
>   resolves "today" per employee from their shift timezone and reuses the private `attendanceForDay`, so
>   the counts match `GET /attendance/today`; an employee without a schedule falls back to UTC and lands
>   in `unknown`. `DashboardService` runs it in the existing errgroup over the department-filtered
>   `EmployeeRepo.ListAll` set and gained a `*TimeAttendanceService` dependency (wired in `main.go`).
>   Replaces the web "Fleet health" card; the `devices` block is unchanged (still consumed by the
>   attention strip). Verified: `go build ./...` / `go vet ./...` clean.
> - 2026-10-17: **Device Specs — legacy machine rows are now attributed to their device (migration 047); a removed device shows its last-known software.**
>   Migration **046** added `machine_id` to the device-scoped sync tables with `DEFAULT ''` and then
>   tried to attribute the pre-existing rows from `app_sessions` with `HAVING COUNT(DISTINCT machine_id) = 1`.
>   That heuristic fails in the exact device-swap scenario it targets: the retired machine's
>   `app_sessions` are purged by the retention worker (30 days) before the migration runs, so the
>   employee looks single-machine (or machine-less) and nothing is backfilled — the old machine's
>   `device_hardware_info` / `network_info` / `storage_devices` / `hardware_devices` /
>   `permission_status` / `app_status` / link rows stay `machine_id = ''`. The Device Specs dropdown
>   still listed that machine (by its `default-EMP-XXXXX` machine id — the fallback bound when a login
>   arrives without a machine id), so selecting it filtered `machine_id = 'default-EMP-XXXXX'`, matched
>   nothing, and the Hardware Overview rendered "No hardware snapshot synced yet".
>   Migration **047** re-attributes each leftover `''` row from the **device registry's own activity
>   window** `[created_at, last_seen_at]` — only when exactly ONE device window contains the row's
>   (server-stamped) sync time; ambiguous/no-window rows stay `''` (the "unknown machine" bucket shown
>   under All devices). Idempotent, `machine_id`-set rows untouched, natural-key collisions skipped.
>   `ListEmployeeApplications` / `ListEmployeePackages` now also include `is_active = false` links when
>   the requested machine has **no live registration** (all its `employee_devices` rows revoked), so a
>   removed device shows the last-known inventory the staleness sweep had deactivated; the
>   employee-wide view still filters `is_active`. No route/contract change.
>   Verified: `go build ./...` / `go vet ./...` clean; migration dry-run + applied against the dev DB
>   (10, 3, 6, 21, 8, 37 link/row groups attributed for the affected employees, 0 unattributed left);
>   per-device reads for `default-EMP-10003` now return hw/storage/network/peripherals/status/packages;
>   installed-software scoping measured live — retired device 99 apps, current device 73, aggregate 73.
> - 2026-10-16: **RBAC catalog — new `settings/terms-and-conditions` submodule (fixes a nav/permission leak).**
>   `internal/services/rbac_service.go` now seeds `settings/terms-and-conditions`
>   ("Terms & Conditions", route `/settings/terms-and-conditions`, sort 8) under `settings-module`.
>   Before this the catalog had no terms key, so the web sidebar rendered `/settings/terms-and-conditions`
>   for every role and the client-side route guard fell back to the `/settings` prefix — meaning T&C
>   management was gated by the unrelated **General Settings** permission. The boot seeder is idempotent
>   (no migration); `GrantAllPermissions` gives `company_admin` the new key automatically (39 → 40
>   grants) and existing roles keep their grants (default deny). Verified: `go build`/`go vet ./...` clean;
>   boot against the dev DB seeded the row (Settings 7 → 8) and `GET /modules` returns the new key.
> - 2026-10-15: **Live stream signaling — ICE trickle buffered until the answer is written (fixes LIVE with zero frames).**
>   Root cause: the Pion SFU starts ICE gathering inside `SetLocalDescription` during
>   `AcceptPublisherOffer` / `AcceptSubscriberOffer` and Pion invokes the candidate callback on a
>   separate goroutine, so an `ice` message could reach the peer BEFORE the `answer`. The peer's
>   `addIceCandidate` then throws `InvalidStateError` (null `remoteDescription`) and the candidate is
>   dropped — ICE/DTLS never completes while `ontrack` has already fired, so watchers stay LIVE /
>   `--- FPS` and the 5s renegotiation loop never converges. Fix: new `iceTrickle` type
>   (`sync.Mutex`, `answered`, `pending`) in `internal/handlers/stream_handler.go`, wired into BOTH the
>   Push (`AcceptPublisherOffer`) and Watch (`AcceptSubscriberOffer`) paths — empty candidates are
>   dropped, candidates generated before the answer are buffered, and `answerMarked()` flushes them in
>   order immediately after the answer message is written. No route, schema, or env change.
>   Verified: `go build ./...` clean; `go vet ./internal/handlers/...` clean.
> - 2026-10-06: **Log Insights + Graphical Logs aggregates.** JWT `GET /api/v1/logs/insights`
>   and `GET /api/v1/logs/graphical` (`from`/`to` + optional `departmentId`/`employeeId`).
>   Insights = digest (metrics, productivity mix, top apps/domains, outliers, highlights).
>   Graphical = hour/day time series (activity + classified productivity) + top bars.
>   Verified: `go build`/`go vet`; web `npx tsc --noEmit`.
> - 2026-10-05: **Dashboard summary aggregate + live-stream list caps.**
>   JWT `GET /api/v1/dashboard/summary?from&to&departmentId` (`DashboardHandler` /
>   `DashboardService` / `DashboardRepo`) returns employees, activity, monitoring,
>   devices, live counts, topApps, topDomains, recentSessions (bounded Top-N). Migration
>   **042** adds `idx_app_items_type_opened` and `idx_employee_devices_last_seen`.
>   `GET /live-stream/employees` accepts optional `onlineOnly`, `limit` (max 50),
>   `departmentId` (omitted = legacy full list). Verified: `go build`/`go vet`;
>   `go test ./internal/services/ -run Dashboard`.
> - 2026-10-03: **DeviceAuth refreshes `employee_devices.client_version` without re-login.**
>   `TouchLastSeen(deviceID, clientVersion, platform)` updates `client_version`/`platform` when
>   DeviceAuth requests carry `X-Client-Version` / `X-Client-Platform` (optional; empty leaves
>   prior values). Wired in `device_auth.go` Background+timeout path. Fixes stale web Client
>   Version after silent reinstall (version was only set on `UpsertDevice` at employee-login).
>   Verified: FA-27/MU-115 → 1.2.33/1.2.34 without re-login; `go build`/`go vet` clean.
> - 2026-10-03: **Live stream V3 Phase 3 — F14c + presence Online contract.**
>   Push/watch/presence sockets: 4h max life. DeviceAuth push + `/ws`: every 5m
>   `DeviceRepo.IsActive` closes on revoke/expire. `employeeLiveOnline` + unit tests
>   (presence-on → WS; presence-off → heartbeat ≤3m).
> - 2026-10-03: **Live stream V3 — safety, slow-network resilience, Redis cluster state (one API).**
>   Phase 0: fail-closed `CheckOrigin`, Subscribe capacity before Upgrade (429), `sendPLI` done
>   channel, `TouchLastSeen` Background+timeout. Phase 1: `POST /live-stream/uplink-probe`
>   (DeviceAuth); token-bucket `WEBRTC_MAX_BITRATE_KBPS` on `forwardRTP`; ticket cap; 4h socket
>   lifetime; watch UserID audit. Phase 2: Redis keys `presence:emp:*`, `stream:ticket:*`,
>   `stream:pub:{emp}` (+ pub/sub `alpha:presence` / `alpha:stream_route`); optional
>   `INSTANCE_ID` / `INSTANCE_PUBLIC_URL`; watch-ticket may return `watchBaseUrl`; Watch may 307.
>   Sticky LB example removed (single-server deploy). Hubs fall back to in-memory when Redis is nil
>   (secrets still need Redis). Verified: `go build`/`go vet`; `go test` redis/scale/stream.
> - 2026-09-29: **Live stream V2 — JPEG relay → Pion WebRTC SFU + dedicated presence WS.**
>   `internal/stream/sfu.go` RTP-forwards publisher tracks to watchers (no re-encode, no frame
>   storage). Hub keeps DeviceAuth push + JWT watch-ticket signaling, consent (`live_view`),
>   `select_monitor`, idle reap, caps. JPEG mailbox / `LIVE_STREAM_MAX_FPS` / frame-byte caps
>   removed. Env: `LIVE_STREAM_ENABLED`, `LIVE_STREAM_MAX_STREAMS`,
>   `LIVE_STREAM_MAX_WATCHERS_PER_EMPLOYEE`, `LIVE_STREAM_IDLE_SEC`, `WEBRTC_MAX_BITRATE_KBPS`
>   (ops advertise; client encode uses `ALPHA_STREAM_MAX_BITRATE_KBPS`), `WEBRTC_STUN_URLS`,
>   optional `WEBRTC_TURN_*`, `PRESENCE_WS_ENABLED`, `PRESENCE_WS_MAX_CONNECTIONS`.
>   Routes: DeviceAuth `GET /live-stream/push`, `GET /ws`; JWT `GET /live-stream/employees`,
>   `GET /live-stream/watch-ticket`; ticketed `GET /live-stream/watch`. Client-vs-Web Auth
>   Separation Rule unchanged. Verified: `go build`/`go vet` clean.
> - 2026-11-04: **`GET /api/v1/app-items/usage` — per-domain aggregate for Web Activity.**
>   `/employee-journey/web` was summing Visits/Duration from infinite-scroll pages in the
>   browser. New endpoint returns one row per domain (`visitCount`, `totalDurationSeconds`,
>   `lastVisitedAt`, `firstVisitedAt`) for `item_type=browser_tab` (default). Open tabs
>   stretch to `LEAST(now, dateTo)`; closed tabs use `closed_at − opened_at`. `GET /app-items`
>   gains `domain` (exact aggregate key; `Other` = empty/NULL domain) for the chevron expand.
>   Registered BEFORE `/app-items`. Verified: `go build`/`go vet`; live ZE-102 aggregates.
> - 2026-09-14: **Per-feature T&C consent audit trail.** Migration 036 creates append-only `terms_consent` table (`employee_id`, `feature_id`, `terms_version`, `action`, `created_at`). Migration 037 adds `terms_content` table (editable T&C content with `is_system` flag for featured vs user-created terms). New `terms_consent_repo` + `terms_content_repo` with full CRUD. `terms_consent_handler` (POST sync, GET list, GET check) + `terms_content_handler` (GET/PUT/POST/DELETE `/terms-content`). Verified: `go build`/`go vet` clean.
> - 2026-09-11: **App-session usage accuracy, per-row stagnation sweep + migrations 034/035.**
>   - `AggregateAppSessionsUsage` projects `has_open_session` via `BOOL_OR(status='ACTIVE' AND ended_at IS NULL)` — OFFLINE/STALE rows with `ended_at=NULL` no longer count as open — and `last_active_at = MAX(COALESCE(last_activity_at, last_sync_at, ended_at, started_at))` (new `AppSessionUsageRow.LastActiveAt` + DTO field `lastActiveAt`, consumed by `/employee-journey/apps`).
>   - Step 4 in `session_lifecycle_sweep.go` is a **per-row stagnation sweep** that closes any OFFLINE/STALE row whose own `last_sync_at` is ≥ CLOSE_AFTER old, independent of the machine-level window.
>   - Migration 034 aligns historical rows that already carried `ended_at` to `CLOSED`. Migration 035 freezes stranded OFFLINE/STALE sessions older than 24h (verified applied live — `OFFLINE & ended_at IS NULL = 0`).
> - 2026-09-04: **`GET /api/v1/app-sessions/usage/sessions` — paginated per-app session list for the chevron expand on `/employee-journey/apps`.** The web page's per-app aggregate row now has a chevron; clicking it fires a server call that returns the raw `app_sessions` rows for the chosen `(appDisplayName, processName)` pair (the same GROUP BY key the aggregate uses, so the inner count always matches the parent `sessionCount`). Filters: `employeeId`, `dateFrom`, `dateTo`, `page`, `perPage` — `appDisplayName` and `processName` are the group key (both URL-encoded). Both are REQUIRED: an empty pair matches `IS NULL OR = ''` on each side (Postgres NULL doesn't match `=`), a half-empty pair is a 400. The new repo `ListAppSessionsForApp` reuses the `ListAppSessions` shape (same SELECT, same `AppSessionListResult` return type) so the service is a one-liner that maps models to DTOs. Registered BEFORE the existing `GET /app-sessions/usage` in `router.go` (Echo's static matching means `/app-sessions/usage/sessions` would otherwise be ambiguous with `/app-sessions/usage` if a `:id` route was added later — explicit ordering removes the risk). The web inner list uses `useInfiniteQuery` (per the *Web Infinite-Scroll Rule*); a "Load more sessions" button is the manual sentinel (the inline area is short — IntersectionObserver doesn't add value here). The page does NOT fetch on initial load — the 100-row aggregate is still one HTTP call; the inner fetch is purely on chevron click. Companion to the `GET /app-sessions/usage` aggregate (same date, same changelog).
> - 2026-09-04: **`GET /api/v1/app-sessions/usage` — per-app aggregate for the web "App Usage" page.** A chrome window with 3 tabs × 10 min used to render as "30 min" because the old page summed per-row `endedAt - startedAt` across every `app_sessions` row with the same `appDisplayName`. New endpoint returns ONE row per `(app_display_name, process_name)` with `MIN(started_at)` (`firstOpenedAt`), `MAX(COALESCE(ended_at, last_sync_at, started_at))` (`lastClosedAt`), `COUNT(*)` (`sessionCount`), and `SUM(EXTRACT(EPOCH FROM (COALESCE(ended_at, last_sync_at, started_at) - started_at)))` (`totalDurationSeconds` — kept for the cross-app "Active Time" tile; the per-app Duration cell uses `lastClosedAt - firstOpenedAt`, NOT the sum, so multi-tab windows never inflate the total). Filters: `employeeId`, `search`, `platform`, `dateFrom`, `dateTo`, `page`, `perPage` — same shape as `ListAppSessions`. The 3-state lifecycle is honored via the `COALESCE(ended_at, last_sync_at, started_at)` expression (CLOSED → `ended_at`, STALE/ACTIVE → `last_sync_at` — never `NOW()`). New repo `AggregateAppSessionsUsage` + service `ListAppSessionsUsage` + handler `ListAppSessionsUsage` + DTO `AppUsageRow` / `AppUsageListResponse`. **Migration 032** adds a composite index `idx_app_sessions_employee_started_name` on `(employee_id, started_at DESC, app_display_name)` so the WHERE + GROUP BY plans without a sort. The route is registered BEFORE the existing `GET /app-sessions` in `router.go` so a future `GET /app-sessions/:id` cannot swallow it. Cross-service contract: web `lib/api.ts` exposes `AppUsageRow` + `appSessionsApi.usage(params)`; web `/employee-journey/apps` switched from a 5×100 raw-row fan-out (silent 500-row truncation bug) to a single `usage()` call and now shows "First Opened" + "Last Closed" columns so the open range is visible directly. The page also re-derives `totalDurationSeconds = lastClosedAt - firstOpenedAt` in JS (defense in depth — if a future server change reintroduces the sum, the page stays correct). Result: chrome 3 tabs × 10 min renders as **Duration: 10m, Sessions: 3**; chrome where tab1 stays open 9:00→9:12 with the others at 9:00→9:10 renders as **Duration: 12m, Sessions: 3**. Verified: `go build`/`go vet` clean, `npx tsc --noEmit` clean, `next build` passes (`/employee-journey/apps` 2.89 kB). No env knobs. Companion client-side fix lives in `client/ARCHITECTURE.md` (`ResolveWindowKey` URL-match + recency rules, ships in next installer build).
> - 2026-09-02: **3-state app_sessions lifecycle (ACTIVE / STALE / CLOSED) — orphan "Running forever" bug fixed at the schema level.**
>   Migration **031** adds 3 columns to `app_sessions` — `status TEXT NOT NULL DEFAULT 'ACTIVE'`, `last_activity_at TIMESTAMPTZ`, `last_sync_at TIMESTAMPTZ` — plus 2 indexes (`status, last_sync_at DESC` and a partial `WHERE status='ACTIVE'`) and backfills 1131 pre-existing rows. New background job `internal/jobs/session_lifecycle_sweep.go` runs every minute, transitioning `ACTIVE → STALE` (no sync for > `SESSION_STALE_AFTER_MINUTES`, default 10) and `STALE → CLOSED` (no sync for > `SESSION_CLOSE_AFTER_HOURS`, default 24). When transitioning to CLOSED, the sweep freezes `ended_at = COALESCE(last_activity_at, last_sync_at, started_at)` so duration reflects real activity. **Upsert recovery in `BulkInsertAppSessions`:** a `ON CONFLICT (id) DO UPDATE` CASE flips STALE/CLOSED back to ACTIVE and clears the premature server-side `ended_at` when a live client re-uploads the row with `ended_at=NULL`; a non-NULL `ended_at` from the client keeps the row CLOSED with the new value. `AppSession` model + DTO + list query updated. Live-tested: 1131 ACTIVE→STALE then 1131 STALE→CLOSED in 62 ms; a live `POST /api/v1/app-sessions/sync` with `ended_at=NULL` flipped a CLOSED row back to ACTIVE in one round-trip.
> - 2026-09-01: **Attendance late/present uses shift IANA timezone; legacy UTC rows auto-migrated.**
>   Shifts created before admins set a timezone stayed on migration 028's `UTC` default while
>   `session_events` carried local offsets — status math disagreed with the web table (e.g. 09:08
>   PKT displayed as on-time when compared to 09:00 UTC). New `DEFAULT_SHIFT_TIMEZONE` in
>   `config.Load()`; `ShiftRepo.ApplyDefaultTimezone` runs at boot via `ShiftService` when set;
>   `AttendanceResponse.timezone` echoes the shift zone used for `present`/`late`/`half_day`;
>   `ShiftService.Create` falls back to `DEFAULT_SHIFT_TIMEZONE` when the payload omits timezone.
>   Operators must set this to the company's wall-clock zone (e.g. `Asia/Karachi`) or edit each
>   shift's timezone on `/shifts`.
> - 2026-08-31: **Time & Attendance Phase 2 server contract.** Migration 028 adds an IANA
>   timezone to shifts, the company-holiday calendar, and aggregate-compatible
>   `session_events` fields (`event_count`, `first_at`, `last_at`). Device-authenticated
>   clients can read `GET /api/v1/schedules/me`; `GET /api/v1/server-time` is public;
>   JWT-protected admins can manage `/holidays` and read `/attendance/today` or
>   `/attendance/range`. Attendance is computed on read from session events, the latest
>   heartbeat (**freshest machine**, GREATEST value/updated_at), device `last_seen_at`
>   (2026-11-03), the assigned shift (**in `shifts.timezone` — not the admin's browser zone**), and
>   holidays; idle and lock intervals are unioned. `late` = earliest active-marker `first_at` is
>   strictly after `shift_start + grace_minutes` in that IANA zone. Response includes `timezone`
>   for web display parity.
>   The range response is paginated for web infinite scrolling. Unit tests cover the
>   lowercase weekly-pattern contract and overlapping inactive intervals.
> - 2026-08-18: **Server-side date-range filtering on app-sessions + app-items list endpoints.**
>   `GET /app-sessions` and `GET /app-items` now accept `dateFrom` and `dateTo` query parameters (RFC3339
>   or date-only like `2026-08-18`). Sessions filter on `started_at`, items on `opened_at`. Combined with
>   the existing `search`/`platform` params — all filters compose in one SQL WHERE. Zero-value/absent params
>   mean "no bound" (backward compatible). The app-items search query was also extended to match `url` and
>   `domain` columns (so searching "youtube" finds the exact page, not just the domain group).
>   New repo params (`DateFrom`, `DateTo` as `*time.Time`) in `ListAppSessionsParams` and
>   `ListAppItemsParams`; handler parses query params with `time.Parse`. Verified: `go build`/`go vet` clean,
>   filtered SQL verified live against Postgres (EMP-10002 today = 70 sessions / 100 browser pages).
> - 2026-08-11: **Employee detail aggregate endpoint** — `GET /api/v1/employees/:id/detail` (protected)
>   returns the full machine picture for one employee in a single response: `employee` (UUID-resolved,
>   then all sync-table reads keyed on the `EMP-XXXXX` id), latest `deviceHardware` + `storageDevices`
>   + `networkInfo`, currently-installed `applications`/`packages` (active junction links joined with
>   catalog rows — version/path/date from the junction, identity from the catalog), `hardwareDevices`
>   peripherals, `permissions` checks, `appStatus` key/value map and activity `stats`
>   (sessions/items/last-activity). New repo read methods in `new_schema_repo.go`, service method
>   `GetEmployeeDetail`, handler `GetEmployeeDetail`, route 31 → 32. The web dashboard consumes it
>   from the new `/users/[id]` page.
> - 2026-08-10: **Employee disconnect endpoint removed** — `POST /api/v1/auth/employee-disconnect`
>   (handler `EmployeeDisconnect`, DTO `EmployeeDisconnectRequest`, route) was deleted along with the
>   client-side Disconnect button. The web admin `POST /api/v1/auth/logout` is unrelated and
>   unchanged. Route count 31 → 30. Client consequence: no `logout` session event is emitted anymore
>   (only `login`, from `StartTracking`) and the Windows anti-sleep state now persists for the whole
>   process lifetime (the OS clears it at process exit — no leak).
> - 2026-08-01: **Docs audit** — removed stale `activity_logs` / `shell_commands` references (both dropped server-side), corrected migration inventory to 001–016 (15 files), added `jobs/staleness_sweep.go`, added `employee_app_link.go` junction models, corrected the API surface (7 sync endpoints + 2 list endpoints, no `/activity-logs`, no `/shell-commands`), and documented all 12 Postgres tables including the app/package catalogs.
> - 2026-07-31: Migrations 013–016 + catalog/link sync rewrite. 013 adds `installed_app_id`/`installed_package_id`/`grouped_by`/`cgroup_scope`/`context_label` to `app_sessions`. 014 adds `process_id` + 9 journey fields to `app_items`. 015/016 build company-global app/package catalogs (`app_fingerprint = desktop_id|binary_name`, `package_fingerprint = package_name|source_manager`) with per-employee junction tables `employee_installed_applications`/`employee_installed_packages` (version/path/install_date + first/last_seen_at + is_active) — two employees with the same app now share ONE catalog row. `SyncInstalledApps`/`SyncInstalledPackages` rewritten to upsert-catalog-then-link inside one tx. New `internal/jobs/staleness_sweep.go` hourly deactivates links idle > `LINK_STALE_DAYS` (default 7, configurable). `NewSchemaRepo` gains `Begin()` + 4 upsert methods. App-session/item bulk inserts + list queries extended with the new fields.
> - 2026-07-29: Browser/terminal/headless filtering arrived client-side only; server DTOs extended with `url`/`domain` (migration 011) and installed-application identity columns (migration 012).
> **Service completion (honest):** ~50%

---

## 1. Responsibility & Scope

**Owns:**
- All persistent data storage (PostgreSQL) and temporary auth data (Redis)
- Authentication for both web admins (email/password → httpOnly cookie) and employees (secret key → JWT)
- REST API for CRUD operations on users, employees, departments
- Ingestion from desktop clients: device hardware, installed apps, installed packages, network info, session events, app sessions, app items (bulk insert with dedup)
- App/package catalog dedup + per-employee link tracking (`employee_installed_applications` / `employee_installed_packages`)
- Company admin auto-initialization on first startup
- Migration management (runs `.sql` migration files on startup)
- Background staleness sweep (hourly, deactivates stale catalog links)

**Does NOT own:**
- Any frontend rendering (served by the Next.js web app)
- Shell command storage or API (**removed from the product** — client no longer collects or sends shell commands, migration 007 drops the legacy table)
- Activity log storage (**removed** — replaced by relational `app_sessions`/`app_items`, migration 006 drops the legacy table)
- Recording / archival of live-stream media (WebRTC SFU is preview-only; no Postgres/disk frames)
- Rate limiting (none implemented)

**Does own (real-time, 2026-09-29):**
- WebRTC SFU live-stream signaling + RTP forward (`internal/stream`)
- Presence WebSocket `GET /api/v1/ws` for Online/Offline

---

## 2. Tech Stack Detail

| Component | Library | Version |
|---|---|---|
| **Language** | Go | 1.25 (go.mod requires `go 1.25.0`) |
| **HTTP Framework** | github.com/labstack/echo/v4 | v4.15.4 |
| **PostgreSQL Driver** | github.com/jackc/pgx/v5 | v5.10.0 |
| **Redis Client** | github.com/redis/go-redis/v9 | v9.21.0 |
| **JWT** | github.com/golang-jwt/jwt/v5 | v5.3.1 |
| **Password Hashing** | golang.org/x/crypto (bcrypt) | v0.54.0 |
| **Env Loading** | github.com/joho/godotenv | v1.5.1 |

### Middleware Stack (in order)

1. `middleware.LoggerWithConfig` — JSON-format request logging
2. `middleware.Recover()` — panic recovery
3. `middleware.CORSWithConfig` — configurable origins with credentials
4. `appMiddleware.OptionalAuth` (semi-protected routes) — validates JWT if present, doesn't fail if absent
5. `appMiddleware.JWTAuth` (protected routes) — requires valid JWT from cookie or Authorization header

---

## 3. Project Structure

```
server/
├── cmd/server/main.go           # Entry point. Config loading, DI setup, auto-init admin, graceful shutdown, starts staleness sweep
├── Makefile                     # build, run, dev (air hot-reload), test, clean, deps, setup
├── go.mod / go.sum              # Go module: github.com/alpha-ai-tracker/server
├── .env.example                 # Environment variable template
│
├── migrations/                  # SQL migration files, run in sorted order on startup (latest: 028)
│   ├── 001_init.sql             # users, departments, employee_id_seq, triggers, seed departments
│   ├── 002_employees.sql        # Separate employees table, migrate non-admin users out of users
│   ├── 004_employee_department_id.sql  # FK employees.department_id → departments.id
│   ├── 005_soft_delete.sql      # deleted_at on users/employees/departments
│   ├── 006_new_schema.sql       # device_hardware_info, installed_applications, network_info, session_events, app_sessions + DROP legacy tables
│   ├── 007_shell_commands.sql   # DROP TABLE shell_commands CASCADE (shell tracking removed)
│   ├── 008_app_items.sql        # generic self-referencing app_items table
│   ├── 009_installed_packages.sql   # CLI tools/runtimes/libraries table
│   ├── 010_session_process_ids.sql  # process_id / parent_process_id on app_sessions
│   ├── 011_app_items_url_domain.sql # url / domain on app_items
│   ├── 012_installed_applications_identity.sql # binary_name / is_browser / desktop_id / categories
│   ├── 013_session_identity.sql # installed_app_id / installed_package_id / grouped_by / cgroup_scope / context_label
│   ├── 014_app_items_journey.sql# process_id + 9 journey fields on app_items
│   ├── 015_employee_app_links.sql   # app_fingerprint + employee_installed_applications junction
│   └── 016_employee_package_links.sql # package_fingerprint + employee_installed_packages junction
│   ├── 017_status_hardware_permission_storage.sql # app_status, hardware_devices, permission_status, storage_devices
│   ├── 018_drop_employee_role.sql / 019_drop_employee_department.sql # denormalized columns removed
│   ├── 020_app_session_focus.sql    # foreground_seconds / background_seconds on app_sessions
│   ├── 021_employee_devices.sql     # opaque device tokens for sync endpoints (DeviceAuth middleware)
│   ├── 022_sequence_retention_indexes.sql # retention-purge support indexes
│   ├── 023_monitoring_config.sql    # monitoring_types, monitoring_categories + classification columns + monitoring_sites
│   ├── 024_catalog_merge.sql        # cross-OS catalog dedup by normalized name
│   ├── 025_rbac_roles_modules.sql   # roles/modules/submodules/role_submodule_permissions; users.role_id sole source of truth
│   ├── 026_refresh_tokens.sql        # rotating web refresh-token persistence
│   ├── 027_shifts.sql               # relational shift catalog + employee assignment
│   ├── 028_time_attendance_phase2.sql # timezone, holidays, aggregate event fields
│   ├── 029_location_samples.sql    # location_samples table (Phase 1 of GPS / geofence)
│   ├── 030_geofence_zones.sql      # geofence_zones table
│   └── 031_app_sessions_status.sql # 3-state lifecycle: status + last_activity_at + last_sync_at + 2 indexes (backfill of existing rows)
│   └── 032_app_sessions_usage_index.sql # composite index (employee_id, started_at DESC, app_display_name) for /app-sessions/usage aggregation
│
└── internal/
    ├── config/config.go         # Loads env vars, builds Config struct (incl. LINK_STALE_DAYS, DEFAULT_SHIFT_TIMEZONE, ReleasesConfig)
    ├── database/postgres.go     # pgxpool creation, migration runner
    ├── redis/
    │   ├── redis.go             # Secrets: StoreSecret / ValidateSecret / DeleteSecret
    │   └── cluster.go           # Phase 2: presence / watch tickets / publisher registry (+ pub/sub)
    ├── scale/instance.go        # ResolveInstanceID (INSTANCE_ID or hostname-pid-random)
    ├── jobs/staleness_sweep.go  # Hourly background job deactivating stale employee↔catalog links
    ├── jobs/retention_sweep.go  # Hourly purge of stale app_items and ended app_sessions (RETENTION_DAYS)
    ├── jobs/session_lifecycle_sweep.go # 1-min sweep: ACTIVE→STALE→CLOSED by last_sync_at; honors SESSION_STALE_AFTER_MINUTES / SESSION_CLOSE_AFTER_HOURS
    └── stream/                 # Live preview WebRTC SFU (V2/V3)
        ├── hub.go                 # Signaling, tickets, publisher registry hook, idle reap, caps
        ├── sfu.go                 # Pion RTP forward publisher → watchers (+ bitrate bucket / PLI)
        └── (handlers wired from router / stream_handler)
    │
    ├── models/                  # Database models (structs with db/json tags)
    │   ├── user.go              # User + UserPublic (safe for API)
    │   ├── employee.go          # Employee + EmployeePublic
    │   ├── device_hardware_info.go # DeviceHardwareInfo, InstalledApplication, NetworkInfo, InstalledPackage, SessionEvent (+ unused ShellCommand)
    │   ├── app_session.go       # AppSession + AppItem (replaces BrowserContext/FileExplorerContext/UrlRecord/UrlVisit)
    │   ├── employee_app_link.go # EmployeeInstalledApplication / EmployeeInstalledPackage junction rows
    │   ├── status_tables.go     # AppStatus, PermissionStatus rows
    │   ├── device.go            # EmployeeDevice (opaque device-token auth)
    │   └── rbac.go              # Role, Module, Submodule, RoleSubmodulePermission
    │
    ├── dto/                     # Request/Response DTOs
    │   ├── user_dto.go          # LoginRequest, CreateUserRequest (roleId), UserResponse (+ permissions), etc.
    │   ├── employee_dto.go      # EmployeeLoginRequest, GenerateSecretResponse, etc.
    │   ├── new_schema_dto.go    # Sync DTOs for all 7 tables + AppSessionListResponse + AppItemListResponse
    │                             # (still contains unused legacy DTOs: BrowserContext/FileExplorerContext/Url/UrlVisit/ShellCommand)
    │   └── rbac_dto.go          # ModuleTreeResponse, RoleResponse (submoduleIds + permissions keys), role CRUD payloads
    │
    ├── repository/              # Data access layer (raw SQL via pgx)
    │   ├── user_repo.go         # User CRUD (roles JOIN; qualified u.* columns), CountUsersWithRole, IsUniqueEmail
    │   ├── employee_repo.go     # Employee CRUD, GenerateEmployeeID, GetDepartments
    │   ├── department_repo.go   # Department CRUD with employee count (LEFT JOIN)
    │   ├── new_schema_repo.go   # Bulk inserts for 7 tables, ListAppSessions/ListAppItems, Begin() tx + catalog-upsert methods
    │   ├── device_repo.go       # UpsertDevice / ListByEmployeeID / RevokeDevice (employee_devices)
    │   ├── monitoring_repo.go   # Types/categories CRUD, classified apps/sites listing + SyncWebsiteDomains
    │   └── rbac_repo.go         # Module/submodule catalog, roles CRUD, grants replace, PermissionKeysForUser, SeedCatalog queries
    │
    ├── services/                # Business logic layer
    │   ├── auth_service.go      # Login, token generation/validation, EnsureCompanyAdmin, attachPermissions
    │   ├── user_service.go      # User CRUD with email uniqueness checks
    │   ├── employee_service.go  # Employee CRUD, GenerateSecret (Redis)
    │   ├── department_service.go # Department CRUD
    │   ├── new_schema_service.go # Sync handlers for 7 tables (catalog-upsert for apps/packages) + 2 list queries
    │   ├── monitoring_service.go # Monitoring configuration business rules (409 type-in-use guard etc.)
    │   ├── rbac_service.go      # SeedCatalog (idempotent module/submodule/grant seeding) + roles CRUD orchestration
    │   └── redis_interface.go   # Interface for Redis operations (decouples auth handler)
    │
    ├── handlers/                # HTTP handlers (Echo context)
    │   ├── auth_handler.go      # Login, Logout, Me, CheckAuth, EmployeeLogin, device listing/revocation
    │   ├── user_handler.go      # List, Get, Create, Update, Delete users
    │   ├── employee_handler.go  # List, Get, Create, Update, Delete, GenerateSecret, Import/Export
    │   ├── department_handler.go # List, Create, Update, Delete departments
    │   ├── new_schema_handler.go # 11 sync endpoints + ListAppSessions + ListAppItems
    │   ├── monitoring_handler.go # Types/categories/apps/websites configuration endpoints
    │   └── rbac_handler.go      # ListModules, roles List/Create/Update/Delete
    │
    ├── middleware/auth.go       # JWTAuth (required), OptionalAuth (optional)
    ├── middleware/device_auth.go # Device <token> auth for the 11 sync endpoints
    │
    └── router/router.go         # Route definitions grouped by auth level
```

### Architecture Layering

```
┌──────────────┐
│   Router     │  Route definitions, middleware application
├──────────────┤
│  Handlers    │  HTTP concerns: parse request, call service, return response
├──────────────┤
│  Services    │  Business logic, validation, orchestration
├──────────────┤
│ Repositories │  Data access: SQL queries, transactions
├──────────────┤
│  PostgreSQL  │  Persistent storage
└──────────────┘
```

---

## 4. API Surface

All endpoints are under `/api/v1`. Full route inventory (~46 routes):

### Health

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/health` | None | Returns `{status: "ok"}` — **bug: timestamp uses client IP instead of actual time** |

### Auth (Public)

| Method | Path | Auth | Purpose |
|---|---|---|---|
| POST | `/auth/login` | None | Web admin login. Sets `auth_token` (15-min access) + `refresh_token` (30-day) httpOnly cookies. |
| POST | `/auth/refresh` | Refresh cookie | Validates + ROTATES the refresh token, re-mints both cookies. 401 ⇒ session unrecoverable. |
| POST | `/auth/employee-login` | None | Employee desktop login. Returns long-lived JWT + device token. |

### Public releases / installer downloads (no login)

Served from the local setup folder (`RELEASES_DIR`, default `setup`; e.g. `setup/v1.0.0.exe`) by
`services/local_release_service.go` + `handlers/release_handler.go`. No GitHub. Version = first `X.Y.Z` in
the filename; `.exe`/`.deb`/`.dmg` only; optional notes `v<version>.md`. Folder re-listed at most every 10s.
Asset ids are a stable FNV hash of the filename (JS-safe integer).

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/releases` | None | One release per version (newest first) + `latestStable` + installer assets. Polled every minute by desktop updaters. |
| GET | `/downloads/:assetId` | None | Stream the installer (`http.ServeContent` — `Range` resume supported). Per-IP soft rate limit. |
| HEAD | `/downloads/:assetId` | None | Metadata only (Content-Length / Disposition / Accept-Ranges). |

CORS for these routes exposes `Content-Disposition`, `Content-Length`, `Content-Range`, `Accept-Ranges` and allows the `Range` request header.

### Auth (Semi-Protected — validates token if present)

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/auth/check` | Optional | Returns `{authenticated, user?}` |

### Auth (Protected)

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/auth/me` | Required | Returns current user profile |
| GET | `/auth/profile` | Required | Aggregate profile payload (user + role + RBAC module breakdown + linked employee) for `/settings/profile` — single-shot, no client-side fan-out |
| POST | `/auth/logout` | Required | Clears httpOnly cookie |

### Users (Protected)

| Method | Path | Purpose |
|---|---|---|
| GET | `/users` | List users (paginated, filterable) |
| GET | `/users/:id` | Get user by ID |
| POST | `/users` | Create user |
| PUT | `/users/:id` | Update user |
| DELETE | `/users/:id` | Soft-delete user |

### Employees (Protected)

| Method | Path | Purpose |
|---|---|---|
| GET | `/employees` | List employees (paginated, filterable, JOIN department; every row projects `hasUserLogin` — see below) |
| GET | `/employees/:id` | Get employee by ID (with `hasUserLogin`) |
| GET | `/employees/:id/detail` | Aggregate machine picture (hardware, storage, network, apps, packages, peripherals, permissions, stats) |
| POST | `/employees` | Create employee (RETURNING includes `hasUserLogin`) |
| PUT | `/employees/:id` | Update employee (RETURNING includes `hasUserLogin`) |
| DELETE | `/employees/:id` | Soft-delete employee |
| POST | `/employees/:id/generate-secret` | Generate one-time secret → Redis (5-min TTL) |

> **`hasUserLogin` projection (2026-08-28).** Every employee SELECT path
> (`List`, `GetByID`, `GetByEmployeeID`, `GetByEmail`, `ListAll`, `Create` and `Update`
> `RETURNING`, and the `scanEmployeeRow` helper) adds
> `EXISTS(SELECT 1 FROM users u WHERE u.employee_id = e.employee_id AND u.deleted_at IS NULL) AS has_user_login`
> to the projection. The probe runs against the UNIQUE `users.employee_id` index
> — it does **not** scan the `users` table. The cost is one indexed lookup per
> page row (10/page by default), so it stays O(1) regardless of the total
> employee or user count. The web employees page uses this flag to hide the
> "Login Credential" dropdown item for employees who already have a login
> account, and the employees `updateMutation` uses `updated.hasUserLogin` from
> `PUT /employees/:id` to decide whether to propagate the name/email change to
> the linked user — no extra round-trips, no client-side maps.

### Departments (Protected)

| Method | Path | Purpose |
|---|---|---|
| GET | `/departments` | List with employee count |
| POST | `/departments` | Create |
| PUT | `/departments/:id` | Rename |
| DELETE | `/departments/:id` | Soft-delete |

### Monitoring Configuration (Protected — web admin reads)

Classification of the detected app catalog and observed website domains. Types are the Productive/Unproductive/Neutral-style references; categories are scoped by kind (`application` | `website` | `both`). `PATCH` classification bodies use nullable presence — `{"typeId": n}` sets, `{"typeId": null}` clears, an absent key leaves the value untouched.

| Method | Path | Purpose |
|---|---|---|
| GET | `/monitoring/types` | List types (seeded + custom) |
| POST | `/monitoring/types` | Create type |
| PUT | `/monitoring/types/:id` | Update type |
| DELETE | `/monitoring/types/:id` | Soft-delete (409 while any app/site references it) |
| GET | `/monitoring/categories?kind=` | List categories, optionally by kind |
| POST | `/monitoring/categories` | Create category |
| PUT | `/monitoring/categories/:id` | Update category |
| DELETE | `/monitoring/categories/:id` | Soft-delete (classifications detach via ON DELETE SET NULL) |
| GET | `/monitoring/apps` | List classified catalog apps (`search`, `typeId`, `categoryId`, `unclassified`, `page`, `perPage`) |
| PATCH | `/monitoring/apps/:id` | Set/clear an app's type + category |
| GET | `/monitoring/websites` | List classified observed domains (auto-syncs new domains from `app_items` first; same filters) |
| PATCH | `/monitoring/websites/:id` | Set/clear a site's type + category |

### RBAC (Protected — web admin manages)

Dynamic role-based access control backed by migration 025 (`roles`, `modules`, `submodules`,
`role_submodule_permissions`; `users.role_id`). The module/submodule catalog + the SYSTEM
`company_admin` role's grants are re-seeded idempotently on every boot (`RBACService.SeedCatalog`).
A granted `(role, submodule)` pair means "allowed"; submodule keys are what the web client receives
as `user.permissions`.

| Method | Path | Purpose |
|---|---|---|
| GET | `/modules` | Full module→submodule tree (the permission catalog for the roles UI + nav guards) |
| GET | `/roles` | List roles with `userCount`, granted `submoduleIds` and derived `permissions` keys |
| POST | `/roles` | Create role `{name, description?, submoduleIds?}` |
| PUT | `/roles/:id` | Partial update — nil fields untouched; `submoduleIds` present ⇒ grants replaced wholesale |
| DELETE | `/roles/:id` | Soft-delete; SYSTEM roles and roles with assigned users are rejected |

Users carry `roleId` on create/update; every user-bearing response (`login`, `/auth/me`,
`/auth/check`, users CRUD) embeds the resolved permission keys via
`RBACRepo.PermissionKeysForUser`. ⚠️ These permissions are NOT enforced by any middleware yet —
they only drive the web client's sidebar/route gating.

### Client Ingestion (Public — JWT in body)

Employee token is carried in the request body (`{employeeId, token, entries: [...]}`), not a cookie. Server validates the token and returns `SyncBatchResponse{accepted, rejected}`.

| Method | Path | Table | Notes |
|---|---|---|---|
| POST | `/device-hardware/sync` | `device_hardware_info` | Bulk upsert |
| POST | `/installed-apps/sync` | `installed_applications` + `employee_installed_applications` | Upsert catalog-then-link in ONE tx |
| POST | `/installed-packages/sync` | `installed_packages` + `employee_installed_packages` | Upsert catalog-then-link in ONE tx |
| POST | `/network-info/sync` | `network_info` | Bulk upsert |
| POST | `/session-events/sync` | `session_events` | Bulk upsert; accepts optional count/firstAt/lastAt aggregates |
| GET | `/schedules/me` | `shifts`, `company_holidays` | Device-auth schedule mirror |
| POST | `/app-sessions/sync` | `app_sessions` | Bulk upsert |
| POST | `/app-items/sync` | `app_items` | Bulk upsert |
| POST | `/app-status/sync` | `app_status` | Upsert by (employee_id, key) |
| POST | `/hardware-devices/sync` | `hardware_devices` | Bulk upsert |
| POST | `/permission-status/sync` | `permission_status` | Bulk upsert by check_id |
| POST | `/storage-devices/sync` | `storage_devices` | Bulk upsert |
| POST | `/devices/self-remove` | `employee_machines`, `employee_devices` | **Remove the account** (DeviceAuth). Marks the CALLING machine `old` + advances `last_track_at`, revokes its device rows, closes open sessions/items and sets `tracking_status='untracked'`. Returns `{removed, machineId, status, lastTrackAt, trackingStatus}`. Called by the client AFTER its final drain. |
| GET | `/dlp/policy` | `dlp_policy_versions` | Pull published bundle (`?sinceVersion=`). Empty/304 when current. *DLP Policy Version Rule*. |
| POST | `/dlp/alerts/sync` | `dlp_alerts` | Batch upsert by client UUID; returns accepted ids |
| POST | `/dlp/health` | — | Heartbeat: policyVersion, enforceOK, lastError |
| POST | `/devices/removal-verify` | `removal_challenges` | Validate one-time code (marks `used_at`); unlocks protected Windows teardown |

> **Server→client wipe instruction (`action`, 2026-10-19).** `SyncBatchResponse` gained
> `action,omitempty`. When `DeviceAuth` authenticated a **revoked** device token (that machine was
> removed via the dashboard) it flags `device_removed`, and every sync handler appends
> `{"action":"wipe_local"}` through `applyDeviceAction` — but **only after** the incoming batch has
> been persisted, so no data is lost to the wipe. The key is absent from every other response, so an
> older client ignores it and a new client never sees it from an older server.

### Security & DLP (Protected — web admin)

RBAC keys: `dlp-rules`, `dlp-alerts`, `shadow-it` under module `security-dlp`. Publish is the only
path that moves draft rules onto endpoints (immutable `dlp_policy_versions`).

| Method | Path | Purpose |
|---|---|---|
| GET/POST | `/dlp/rules` | List / create editable rules (non-web triggers) |
| GET | `/dlp/rules/:id` | Single rule detail |
| GET | `/dlp/rules/:id/history` | Pre-update snapshots (migration 055) |
| PATCH/DELETE | `/dlp/rules/:id` | Update (snapshots revision) / soft-delete |
| POST | `/dlp/rules/publish` | Compile enabled rules → new `dlp_policy_versions` row (`policyVersion`, `hash`, `ruleCount`) |
| GET | `/dlp/alerts` | Infinite-scroll inbox (`q`, `status`, `severity`, `type`, `employeeId`, `dateFrom`, `dateTo`, `page`, `perPage`) + status counters |
| PATCH | `/dlp/alerts/:id` | Triage: `status` / `assignedTo` / `notes` (writes `dlp_alert_events` per changed field) |
| GET | `/dlp/alerts/:id/history` | Triage audit trail (newest first) |
| GET | `/shadow-it/apps` | Catalog list (`q`, `authStatus`, pagination + counters) |
| PATCH | `/shadow-it/apps/:id` | `authStatus` / `risk` / `action` (`approve`\|`block`\|`flag`) |
| POST | `/devices/removal-code` | Issue one-time protected-uninstall code (raw returned once; hash stored) |

### App Sessions & App Items (Protected — web admin reads)

| Method | Path | Purpose |
|---|---|---|
| GET | `/app-sessions/usage/sessions` | Per-app session list (paginated, filterable: `employeeId`, `dateFrom`, `dateTo`, `page`, `perPage`). Returns one `app_sessions` row per match for the given `(appDisplayName, processName)` pair — powers the chevron-expanded table under each app row on `/employee-journey/apps`. Both keys REQUIRED (400 if both empty; half-empty is ambiguous). Registered BEFORE `/app-sessions/usage` (since 2026-09-04). |
| GET | `/app-sessions/usage` | Per-app aggregate (paginated, filterable: `search`, `platform`, `dateFrom`, `dateTo`, `employeeId`). One row per `(appDisplayName, processName)` with `firstOpenedAt`, `lastClosedAt`, `sessionCount`, `totalDurationSeconds`. Powers the web `/employee-journey/apps` page; page renders `lastClosed - firstOpened` so multi-tab windows never inflate the per-app total. Registered BEFORE `/app-sessions` (since 2026-09-04). |
| GET | `/app-sessions` | List sessions (paginated, filterable: `search`, `platform`, `dateFrom`, `dateTo`, `employeeId`). Each row carries `status` (`ACTIVE`/`STALE`/`CLOSED`), `lastActivityAt`, `lastSyncAt` (3-state lifecycle — see below) |
| GET | `/app-items/usage` | Per-domain aggregate for `/employee-journey/web` (paginated, filterable: `employeeId`, `search`, `dateFrom`, `dateTo`, `itemType` default `browser_tab`). One row per domain with `visitCount`, `totalDurationSeconds`, `lastVisitedAt`. Registered BEFORE `/app-items` (since 2026-11-04). |
| GET | `/app-items` | List items (paginated, filterable: `search` matches title/identifier/url/domain, `dateFrom`, `dateTo`, `itemType`, `domain` (exact usage GROUP BY key; `Other` = empty), `session`) |

> **3-state app_sessions lifecycle (migration 031 + `jobs/session_lifecycle_sweep.go`).** Every `app_sessions` row carries a server-projected `status` field (`ACTIVE` / `STALE` / `CLOSED`) plus `lastActivityAt` and `lastSyncAt` timestamps. A 1-minute background sweeper promotes `ACTIVE → STALE` when no sync has been received for `SESSION_STALE_AFTER_MINUTES` (default 10), then `STALE → CLOSED` when no sync has been received for `SESSION_CLOSE_AFTER_HOURS` (default 24). Only CLOSED is terminal. The sweep freezes `ended_at = COALESCE(last_activity_at, last_sync_at, started_at)` at the moment of CLOSE so the duration reflects real activity. **A live client re-uploading a STALE/CLOSED row with `ended_at=NULL` flips it back to ACTIVE** in the upsert (the `ON CONFLICT` CASE in `BulkInsertAppSessions`) and clears the premature server-side `ended_at` — so a network outage never destroys information that may still exist on the client. When the client supplies a non-NULL `ended_at`, the row stays CLOSED with the new value. Pre-031 rows default `status='ACTIVE'`; the web's `sessionStatus()` helper falls back to the legacy `endedAt ? CLOSED : ACTIVE` interpretation for forward compatibility.

### Employee Detail (Protected)

| Method | Path | Purpose |
|---|---|---|
| GET | `/employees/:id/detail` | **Aggregate machine picture** for one employee: employee record, latest `device_hardware_info`, `storage_devices`, latest `network_info`, currently-installed apps/packages (active junction links), `hardware_devices` peripherals, `permission_status`, `app_status` map, the machine list and activity stats. `?deviceId=<machineId>` scopes every machine-data block to that machine (`""` = employee-wide aggregate). Consumed by the web Device Specs module (`EmployeePage` shell). |

> **`devices[]` comes from `employee_machines` (2026-10-19).** The device list is sourced from the
> canonical lifecycle registry — **one entry per physical machine** — not from `employee_devices`,
> which rotates a row on every login and therefore listed the same PC several times. Each entry
> carries `status` (`active`|`old`), `firstTrackAt`, `lastTrackAt`, `wipeRequestedAt`,
> `isActiveDevice` (derived from `status === 'active'`) and the newest registration's
> `platform`/`clientVersion`/`lastSeenAt`/`revokedAt`/`removeAt`. `deviceName`/`hostname` fall back
> device name → hostname → machine id so a device is never rendered "Unknown device".

### Time & Attendance (Protected — web admin reads)

| Method | Path | Purpose |
|---|---|---|
| GET | `/attendance/today?employeeId=` | Today's row: `timezone`, `firstActiveAt`, `lastActiveAt`, `status`, `lateMinutes`, active/idle/off-shift seconds |
| GET | `/attendance/range?employeeId=&from=&to=&page=&perPage=` | Paginated daily rows (timesheets; infinite scroll on web) |
| GET/POST/PUT/DELETE | `/holidays` | Company holiday calendar CRUD |
| GET/POST/PUT/DELETE | `/shifts` | Shift catalog CRUD — each shift carries an IANA `timezone` |

> **Shift timezone rule (2026-09-01).** Late/present math runs in `shifts.timezone`, not the admin
> browser's local zone. Legacy rows still on `UTC` are rewritten at boot when
> `DEFAULT_SHIFT_TIMEZONE` is set in `.env`. The web shift form defaults new shifts to the admin
> browser's IANA zone.
>
> **Last Seen / Active alive signals (2026-11-03).** `lastActiveAt` is the max of (1) that day's
> session-event `last_at` values after first active, (2) freshest `app_status.last_heartbeat_at`
> across machines (`GREATEST(value, updated_at)`), and (3) non-revoked
> `employee_devices.last_seen_at` — all clipped to the work-day window. Live Stream Online and
> App Usage “Running” use different signals (presence WS / app_sessions); do not assume they
> share Last Seen. Idle/lock math still comes only from session events.

### Live stream + presence (WebRTC SFU, V3)

Preview-only — no frame storage in Postgres or on disk. Auth split follows the Client-vs-Web API Auth Separation Rule.

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/live-stream/push` | DeviceAuth (WS) | Desktop publisher signaling (`hello`/`offer`/`answer`/`ice` + `force_keyframe` ctrl) |
| POST | `/live-stream/uplink-probe` | DeviceAuth | Client uplink benchmark body (discarded); times RTT for bitrate ladder |
| GET | `/ws` | DeviceAuth (WS) | Presence / keep-alive (Online/`wsConnected`); mirrored to Redis when available |
| GET | `/live-stream/employees` | JWT | Admin list + online/wsConnected flags |
| GET | `/live-stream/watch-ticket` | JWT | Short-lived ticket (Redis when available); may include `watchBaseUrl` |
| GET | `/live-stream/watch` | Watch ticket (WS) | Admin subscriber signaling + WebRTC receive (may 307 if publisher elsewhere) |

SFU: `internal/stream/sfu.go` (Pion) + `hub.go`. Cluster: `internal/redis/cluster.go` (presence TTL, tickets, `stream:pub:{emp}` → instance). Deploy shape: **one API process**; sticky LB not used.

### Missing Endpoints (sync-only tables with no standalone listing API)

| Expected Endpoint | Purpose | Status |
|---|---|---|
| `GET /installed-apps` | List installed apps (all employees) | ❌ Sync only — per-employee view available via `/employees/:id/detail` |
| `GET /installed-packages` | List installed packages (all employees) | ❌ Sync only — per-employee view via `/employees/:id/detail` |
| `GET /device-hardware` | List device hardware (all employees) | ❌ Sync only — latest-per-employee via `/employees/:id/detail` |
| `GET /network-info` | List network info (all employees) | ❌ Sync only — latest-per-employee via `/employees/:id/detail` |
| `GET /session-events` | List session events | ❌ Sync only |

---

## 5. Database Schema

### Tables (tracked tables + RBAC/monitoring/status/DLP tables + `schema_migrations`)

> 2026-08-11: +4 sync tables — `app_status` (key/value per employee), `hardware_devices`
> (USB/peripheral hotplug), `permission_status` (one row per permission method), and
> `storage_devices` (children of `device_hardware_info`). Migration 017.
>
> 2026-08-25: migration 025 adds the RBAC cluster (`roles`, `modules`, `submodules`,
> `role_submodule_permissions`) and drops the legacy `users.role` / `users.department` /
> `users.is_company_admin` columns — **`users.role_id` is now the sole role source of truth**.
>
> 2026-10-22: migrations **051** / **052** add the Security & DLP cluster (rules, policy versions,
> alerts, alert events, shadow IT, removal challenges).

**`dlp_rules`** — Admin-editable current policy set (migration 051)
```
id UUID PK · name · enabled · trigger (usb_storage|usb_file|web_navigate|software_install|…)
match_json · action (alert|block|alert_block) · severity · scope_json · soft-delete
```

**`dlp_policy_versions`** — Immutable published bundles (migration 051)
```
version BIGSERIAL PK · bundle_json · bundle_hash · published_at · published_by → users(id)
```

**`dlp_alerts`** — Device-synced incident inbox (migration 051)
```
id UUID PK (client-generated) · rule_id · policy_version · employee_id · machine_id
type · severity · action_taken (alerted|blocked|blocked_failed) · occurred_at · evidence_json
status (open|investigating|resolved|false_positive) · assigned_to · notes · synced_at
```

**`dlp_alert_events`** — Triage audit (migration 052)
```
id BIGSERIAL · alert_id FK · changed_by · changed_by_name · field (status|assigned_to|notes)
old_value · new_value · created_at
```

**`shadow_it_apps`** — App/SaaS authorization catalog (migration 051)
```
id BIGSERIAL · name · name_normalized UNIQUE · kind · auth_status · risk · user_count · soft-delete
```

**`removal_challenges`** — One-time protected stop/uninstall codes (migration 051)
```
id UUID · machine_id · employee_id · token_hash UNIQUE · purpose · expires_at · used_at · issued_by
```

**`employee_machines`** — Canonical per-machine lifecycle registry (migration 049)
```
id                UUID PK (gen_random_uuid())
employee_id       VARCHAR(20) → FK → employees(employee_id) ON DELETE CASCADE
machine_id        VARCHAR(255) NOT NULL
status            VARCHAR(10) NOT NULL DEFAULT 'active' CHECK (status IN ('active','old'))
first_track_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
last_track_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
wipe_requested_at TIMESTAMPTZ NULL   ← stamped by "Remove current device" (R6)
created_at / updated_at TIMESTAMPTZ
UNIQUE (employee_id, machine_id) WHERE deleted_at IS NULL
INDEX (employee_id)
```

> **Why a second table next to `employee_devices`.** `employee_devices` is the AUTH-TOKEN registry:
> `UpsertDevice` revokes the previous `(employee, machine)` row and inserts a NEW one on every login,
> so one physical PC appears as many rows over time and no row can carry an honest
> `active|old` lifecycle state. `employee_machines` holds exactly one row per physical machine and is
> what the web Device Specs dropdown and the active→old transition read; the two are joined on
> `(employee_id, machine_id)`.

**`users`** — Web admin users (role via role_id)
```
id              UUID PK (gen_random_uuid())
employee_id     VARCHAR(20) UNIQUE (EMP-XXXXX)
name            VARCHAR(255) NOT NULL
email           VARCHAR(255) UNIQUE NOT NULL
password_hash   VARCHAR(255) NOT NULL (bcrypt)
role_id         INTEGER → FK → roles(id)          ← migration 025; legacy role/is_company_admin dropped
shift           VARCHAR(20) DEFAULT 'Day'
tracking_enabled BOOLEAN DEFAULT true
tracking_status VARCHAR(20) DEFAULT 'untracked'
is_online       BOOLEAN DEFAULT false
avatar          VARCHAR(10) NULL (auto-generated initials)
avatar_color    VARCHAR(10) NULL (random from palette)
created_at / updated_at / deleted_at TIMESTAMPTZ (updated_at auto-trigger)
```

**`roles`** — RBAC role reference (migration 025)
```
id              SERIAL PK
name            VARCHAR(100) UNIQUE ('company_admin' seeded as SYSTEM, is_system=true)
description     TEXT
is_system       BOOLEAN (system roles cannot be deleted)
created_at / updated_at / deleted_at TIMESTAMPTZ
```

**`modules`** — Navigation module groups (General, HR, Monitoring, Settings, …)
```
id              SERIAL PK
key             VARCHAR(100) UNIQUE (permission module key)
name            VARCHAR(100) NOT NULL
sort_order      INTEGER DEFAULT 0
created_at / updated_at TIMESTAMPTZ (updated_at auto-trigger; no soft delete — catalog is seeded code-side)
```

**`submodules`** — Concrete permission keys under a module (carry route_path for nav guards)
```
id              SERIAL PK
module_id       INTEGER → FK → modules(id) ON DELETE CASCADE
key             VARCHAR(100) UNIQUE  ← what ships to the client as user.permissions entries
name            VARCHAR(100) NOT NULL
route_path      VARCHAR(200) (web route the key guards)
sort_order      INTEGER DEFAULT 0
created_at / updated_at TIMESTAMPTZ
```

**`role_submodule_permissions`** — Junction: a granted (role, submodule) pair means "allowed"
```
role_id         INTEGER → FK → roles(id) ON DELETE CASCADE
submodule_id    INTEGER → FK → submodules(id) ON DELETE CASCADE
PRIMARY KEY (role_id, submodule_id)
```

**`refresh_tokens`** — Web-admin rotating refresh tokens, hashed at rest (migration 026)
```
id              BIGSERIAL PK
user_id         UUID → FK → users(id) ON DELETE CASCADE
token_hash      VARCHAR(64) UNIQUE NOT NULL   ← hex(sha256(raw_token)); raw value lives ONLY in the cookie
expires_at      TIMESTAMPTZ NOT NULL          ← JWT_REFRESH_EXPIRY (default 30d) from creation
revoked_at      TIMESTAMPTZ                   ← set on rotation or logout; NULL = live
created_at / deleted_at TIMESTAMPTZ
```

**`departments`** — Reference table for employee departments
```
id              SERIAL PK
name            VARCHAR(100) UNIQUE NOT NULL
deleted_at      TIMESTAMPTZ (soft delete)
```

**`employees`** — Tracked employees (desktop client users)
```
id              UUID PK (gen_random_uuid())
employee_id     VARCHAR(20) UNIQUE (EMP-XXXXX)
name            VARCHAR(255) NOT NULL
email           VARCHAR(255) NOT NULL DEFAULT ''
department      VARCHAR(100) DEFAULT 'Engineering'
department_id   INTEGER NOT NULL DEFAULT 1 → FK → departments(id)
role            VARCHAR(50) DEFAULT 'employee'
shift           VARCHAR(20) DEFAULT 'Day'
tracking_enabled BOOLEAN DEFAULT true
tracking_status VARCHAR(20) DEFAULT 'untracked'
is_online       BOOLEAN DEFAULT false
avatar          VARCHAR(10)
avatar_color    VARCHAR(10)
created_at / updated_at / deleted_at TIMESTAMPTZ
```

**`device_hardware_info`** — Hardware snapshot per employee (migration 006)
```
id              TEXT PK (client-generated GUID)
employee_id     VARCHAR(20) NOT NULL → FK → employees(employee_id)
device_id       TEXT NOT NULL DEFAULT ''
mac_address     TEXT NOT NULL DEFAULT ''
hostname        TEXT NOT NULL DEFAULT ''
os_name         TEXT NOT NULL DEFAULT ''
os_version      TEXT NOT NULL DEFAULT ''
cpu_model       TEXT NOT NULL DEFAULT ''
cpu_cores       INTEGER NOT NULL DEFAULT 0
ram_total_mb    BIGINT NOT NULL DEFAULT 0
storage_devices JSONB NOT NULL DEFAULT '[]'
gpu_model       TEXT NOT NULL DEFAULT ''
gpu_vram_mb     BIGINT NOT NULL DEFAULT 0
collected_at / synced_at / created_at / deleted_at TIMESTAMPTZ
```

**`installed_applications`** — Company-global GUI app catalog (migrations 006, 012, 015)
```
id              TEXT PK (client-generated GUID)
employee_id     VARCHAR(20) NOT NULL → FK → employees(employee_id)   -- "owner" who first synced it (legacy)
app_name        TEXT NOT NULL
app_version     TEXT NOT NULL DEFAULT ''
publisher       TEXT NOT NULL DEFAULT ''
install_path    TEXT NOT NULL DEFAULT ''
install_date    TIMESTAMPTZ
uninstall_string TEXT NOT NULL DEFAULT ''
change_type     TEXT NOT NULL DEFAULT 'seen'
detected_at / synced_at / created_at / deleted_at TIMESTAMPTZ
binary_name     TEXT NOT NULL DEFAULT ''   (012)
is_browser      BOOLEAN NOT NULL DEFAULT false (012)
desktop_id      TEXT NOT NULL DEFAULT ''   (012)
categories      TEXT NOT NULL DEFAULT ''   (012)
app_fingerprint TEXT UNIQUE                (015)  = desktop_id|binary_name
```
Per-install metadata (version/path/date/publisher) now lives on `employee_installed_applications`, so the catalog row is shared across employees. The legacy `employee_id` column is retained for backward compat but is no longer the dedup key.

**`employee_installed_applications`** — employee↔app junction (migrations 015, 046)
```
id                      BIGSERIAL PK
employee_id             VARCHAR(20) NOT NULL → FK → employees(employee_id)
machine_id              TEXT NOT NULL DEFAULT ''   (046 — device scope)
installed_application_id TEXT NOT NULL → FK → installed_applications(id)
app_version / publisher / install_path / install_date
first_seen_at / last_seen_at TIMESTAMPTZ NOT NULL DEFAULT now()
is_active               BOOLEAN NOT NULL DEFAULT true
UNIQUE (employee_id, machine_id, installed_application_id)
```
`is_active` means "the machine is still reporting this app" (only the hourly staleness sweep clears it).
`ListEmployeeApplications` still filters `is_active` for the employee-wide view, but for a **device that
has no live registration** (every `employee_devices` row revoked) it includes inactive links so a removed
device shows its last-known inventory instead of an empty page.

**`network_info`** — Network snapshots per employee (migration 006)
```
id TEXT PK · employee_id FK · public_ip · private_ip · mac_address
network_interface_name · collected_at / synced_at / created_at / deleted_at
```

**`session_events`** — Power/login/lock/idle telemetry (migrations 006, 028).
```
id TEXT PK · employee_id FK · event_type · os_username
event_at · event_count · first_at · last_at / synced_at / created_at / deleted_at
```

**`app_sessions`** — Relational app sessions (migrations 006, 010, 013)
```
id               TEXT PK (client-generated GUID)
employee_id      VARCHAR(20) NOT NULL → FK → employees(employee_id)
process_name     TEXT NOT NULL
app_display_name TEXT NOT NULL DEFAULT ''
started_at / ended_at TIMESTAMPTZ
machine_id       TEXT NOT NULL DEFAULT ''
session_id       TEXT NOT NULL DEFAULT ''
platform         TEXT NOT NULL DEFAULT ''
synced_at / created_at / deleted_at TIMESTAMPTZ
process_id       INTEGER          (010)
parent_process_id INTEGER         (010)
installed_app_id  TEXT            (013)  -- no FK: may reference an unsynced client row
installed_package_id TEXT         (013)
grouped_by       TEXT             (013)  -- 'cgroup' | 'pid' | NULL
cgroup_scope     TEXT             (013)  -- systemd app-*.scope
context_label    TEXT             (013)  -- VS Code workspace / Chrome profile
```

**`app_items`** — Generic self-referencing child of app_sessions (migrations 008, 010, 011, 014)
```
id                TEXT PK
employee_id       VARCHAR(20) NOT NULL → FK → employees(employee_id)
app_session_id    TEXT NOT NULL → FK → app_sessions(id)
parent_item_id    TEXT → FK → app_items(id)
item_type         TEXT NOT NULL DEFAULT ''   ('tab', 'browser_tab', 'browser_navigation', 'terminal', 'folder', 'file')
title / identifier TEXT NOT NULL DEFAULT ''
url / domain      TEXT NOT NULL DEFAULT ''   (011)
opened_at / closed_at TIMESTAMPTZ
process_id        INTEGER          (014)
object_type / action / journey_id / previous_path / current_path / metadata_json (014)
sequence          INTEGER NOT NULL DEFAULT 0 (014)
window_id / tab_id INTEGER        (014)
synced_at / created_at / deleted_at TIMESTAMPTZ
```

**`installed_packages`** — Company-global CLI tool/runtime/library catalog (migrations 009, 016)
```
id               TEXT PK
employee_id      VARCHAR(20) NOT NULL → FK → employees(employee_id)  -- legacy owner column
package_name     TEXT NOT NULL
version          TEXT NOT NULL DEFAULT ''
category         TEXT NOT NULL DEFAULT 'tool'
source_manager   TEXT NOT NULL DEFAULT ''
install_path / publisher / description TEXT NOT NULL DEFAULT ''
detected_at / synced_at / created_at / deleted_at TIMESTAMPTZ
package_fingerprint TEXT UNIQUE (016)  = package_name|source_manager
```

**`employee_installed_packages`** — employee↔package junction (migrations 016, 046)
```
id BIGSERIAL PK · employee_id FK · machine_id TEXT DEFAULT '' (046)
installed_package_id FK · version / publisher / install_path
first_seen_at / last_seen_at · is_active BOOLEAN
UNIQUE (employee_id, machine_id, installed_package_id)
```
Same removed-device read rule as the app junction (`ListEmployeePackages`).

**`schema_migrations`** — Migration tracking
```
filename        VARCHAR(255) PK
applied_at      TIMESTAMPTZ DEFAULT NOW()
```

### Removed Tables

| Table | Migration | Status |
|---|---|---|
| `activity_logs` | 006 (DROP CASCADE) | Replaced by `app_sessions`/`app_items` |
| `browser_contexts` | 006 (DROP CASCADE) | Replaced by `app_items` |
| `file_explorer_contexts` | 006 (DROP CASCADE) | Replaced by `app_items` |
| `urls` / `url_visits` | 006 (DROP CASCADE) | Replaced by `app_items` |
| `shell_commands` | 007 (DROP CASCADE) | Shell tracking removed from product |

### Indexes

- All tables have `deleted_at` partial indexes for soft-delete filtering
- app_sessions: `(employee_id, started_at DESC)`, `(started_at DESC)`, `(process_id)`, `(parent_process_id)`, `(employee_id, started_at)`
- app_items: `(employee_id, opened_at DESC)`, `(app_session_id)`, `(parent_item_id)`, `(employee_id, item_type, identifier)`, `(journey_id, sequence)`, `(url)`, `(domain)`
- installed_applications: `app_fingerprint` UNIQUE, `binary_name`, `(employee_id, detected_at DESC)`, `app_name`
- installed_packages: `package_fingerprint` UNIQUE, `source_manager`, `category`, `(employee_id, detected_at DESC)`
- refresh_tokens: `(user_id)`, `(expires_at)`

### Migration Tool

**Custom runner** in `database/postgres.go`. Reads all `.sql` files from `migrations/` in filename order (latest: 039), tracks applied migrations in `schema_migrations`, and runs each file in its own transaction.

---

## 6. Auth & Security Model

### Web Admin Auth Flow

1. Admin submits email + password to `POST /api/v1/auth/login`
2. Server looks up user by email, compares bcrypt hash
3. Valid: generates access JWT with claims `{userId}`, signs with HMAC-SHA256, then **encrypts the signed JWT with AES-256-GCM** using a key derived from JWT secret
4. Two httpOnly cookies are set:
   - `auth_token` — the encrypted access JWT, **short-lived** (`JWT_ACCESS_EXPIRY`, default **15m**)
   - `refresh_token` — an opaque 32-byte random value (base64url); only its SHA-256 hash is stored in `refresh_tokens` with `expires_at = now + JWT_REFRESH_EXPIRY` (default **30 days**)
5. Subsequent requests: `JWTAuth` middleware reads `auth_token`, decrypts the AES layer, then validates the JWT signature
6. When the access token expires, any API call gets 401 → the web client calls `POST /auth/refresh`: server validates the presented hash (not revoked/expired/deleted), loads + re-authenticates the user, mints a NEW access JWT AND a replacement refresh token (**rotation** — old row revoked after the replacement insert succeeds, so a mid-refresh crash can never lock the user out), and re-mints both cookies
7. A 401 from `/auth/refresh` itself means the session is gone (refresh expired/revoked/user deleted) — the handler also clears both cookies and the web client force-redirects to `/login`
8. `POST /auth/logout` revokes the presented refresh row (it can never mint another token) and clears both cookies
9. **Double encryption rationale**: protects against JWT secret leakage from log files (JWT is AES-encrypted until the middleware decrypts it)

### Employee Auth Flow

1. Admin generates a one-time secret via `POST /api/v1/employees/:id/generate-secret`
2. Server stores secret in Redis with key `employee_secret:<emp_id>` and 5-min TTL
3. Employee enters emp_id + secret in desktop client
4. Client sends `POST /api/v1/auth/employee-login` with `{employeeId, secretKey}`
5. Server validates secret from Redis, deletes it (one-time use)
6. Returns an encrypted JWT (same mechanism as admin, but issuer `alpha-ai-tracker-employee`) with its OWN TTL (`JWT_EMPLOYEE_ACCESS_EXPIRY`, default 24h)
7. ⚠️ Desktop clients have NO refresh mechanism — they embed this one token in every sync request body until the process stops. Never lower this TTL to the web-admin value.
8. Additionally returns a non-expiring opaque device token (`dev_tok_…`, hash stored in `employee_devices`) used by the DeviceAuth middleware on all 11 sync endpoints

### Token Security

| Aspect | Implementation |
|---|---|
| **Signing** | HMAC-SHA256 via `golang-jwt/jwt/v5` |
| **Encryption** | AES-256-GCM, key = SHA256(JWT_SECRET) |
| **Web-admin access expiry** | `JWT_ACCESS_EXPIRY`, default **15m** (`auth_token` cookie) |
| **Web-admin refresh expiry** | `JWT_REFRESH_EXPIRY`, default **720h / 30 days** (`refresh_token` cookie, rotated on every refresh) |
| **Employee token expiry** | `JWT_EMPLOYEE_ACCESS_EXPIRY`, default 24h (body-carried, no refresh) |
| **Cookies** | httpOnly, SameSite=Lax, Secure=false in dev |
| **Refresh storage** | `refresh_tokens` table — SHA-256 hex hash UNIQUE, `revoked_at` on rotate/logout, FK → users ON DELETE CASCADE |
| **Employee token** | Returned in response body, stored locally by client |

### Missing Security Controls

- **No rate limiting** on login/refresh or any endpoint
- **No CSRF protection** (SameSite=Lax provides partial coverage)
- **No brute-force protection** for login attempts
- **Refresh-token reuse detection not implemented** — a revoked-but-still-presented token should ideally revoke the whole family; today rotation simply fails the stale token
- **Expired `refresh_tokens` rows are not auto-purged yet** (one row per login/rotation; retention hook exists as `RefreshTokenRepo.DeleteExpired`)
- **No audit log** of who performed what action
- **No permission model** on the server — any authenticated user can access all endpoints

---

## 7. Client Ingestion Path

All 7 sync endpoints share the same flow — the app-sessions path shown here is representative:

```
┌──────────┐     POST /api/v1/app-sessions/sync    ┌──────────────┐
│  Client   │───────────────────────────────────────▶│  Handler      │
│  (C#)     │  {employeeId, token, entries: [...]}   │  SyncAppSessions() │
└──────────┘                                         └──────┬───────┘
                                                             │
                                                             ▼
                                                    ┌──────────────┐
                                                    │  Auth Check   │
                                                    │  ValidateToken│
                                                    │  (encrypted   │
                                                    │   JWT →       │
                                                    │   signed JWT) │
                                                    └──────┬───────┘
                                                             │ valid
                                                             ▼
                                                    ┌──────────────┐
                                                    │  Service      │
                                                    │  SyncAppSessions() │
                                                    │  - Verify emp │
                                                    │  - Parse ts   │
                                                    │  - BulkInsert │
                                                    └──────┬───────┘
                                                             │
                                                             ▼
                                                    ┌──────────────┐
                                                    │  Repository   │
                                                    │  BulkInsert() │
                                                    │  - Batch of   │
                                                    │    500        │
                                                    │  - ON CONFLICT│
                                                    │    DO NOTHING │
                                                    └──────┬───────┘
                                                             │
                                                             ▼
                                                    ┌──────────────┐
                                                    │  PostgreSQL   │
                                                    │  app_sessions │
                                                    └──────────────┘
```

> **Special case — installed apps/packages:** `SyncInstalledApps`/`SyncInstalledPackages` use a two-step tx: (1) upsert the catalog row by fingerprint (`UpsertApplicationCatalog`/`UpsertPackageCatalog`), (2) upsert the employee junction link with per-install metadata (`UpsertEmployeeAppLink`/`UpsertEmployeePackageLink`) — sharing ONE catalog row across employees with the same app.

### Validation

- **Token**: decrypted and JWT-validated in the handler
- **Employee**: looked up by `employeeId` from the decoded JWT — **but the handler also accepts `employeeId` from the request body and verifies the employee exists. The JWT userId and the body employeeId are NOT cross-checked** — meaning an employee with a valid token could send data under a different `employeeId`
- **Timestamps**: parsed as RFC3339, falls back to `time.Now()` on failure
- **Data**: no sanitization or length limits on process names, window titles, URLs

---

## 8. Web-Facing Read Path

```
┌──────────┐     GET /api/v1/app-sessions             ┌──────────────┐
│  Web App  │─────────────────────────────────────────▶│  Handler      │
│  (Next.js)│  ?employeeId=&search=&page=&perPage=...   │  ListAppSessions() │
└──────────┘                                           └──────┬───────┘
                                                               │
                                                               ▼
                                                     ┌──────────────┐
                                                     │  Repository   │
                                                     │  List()       │
                                                     │  - Dynamic    │
                                                     │    SQL build  │
                                                     │  - Manual     │
                                                     │    row scan   │
                                                     │  - Pagination │
                                                     └──────┬───────┘
                                                               │
                                                               ▼
                                                     ┌──────────────┐
                                                     │  PostgreSQL   │
                                                     └──────────────┘
```

The same pattern applies to `GET /app-items`. The web dashboard's Logs/Comprehensive page queries `app-sessions` (there is no `activity-logs` API anymore).

### Caching Layer

**None.** Every request hits PostgreSQL directly. No Redis caching, no in-memory cache, no CDN.

### Pagination Approach

- Page/perPage query parameters
- Default: page=1, perPage=10 (employees), perPage=20 (app sessions / app items)
- Returns `{data, total, page, perPage, totalPages}`
- SQL: `SELECT ... LIMIT $n OFFSET $m` with a separate `SELECT COUNT(*)` for total

---

## 9. Scalability Considerations

### Statelessness

The server is **mostly stateless**:
- JWT validation requires only the shared `JWT_SECRET` — no session store
- Employee secrets are in Redis (external), so any instance can validate
- **Bottleneck**: PostgreSQL connection pool (25 max open connections per instance by default)

### Can It Run Multiple Instances?

**Yes for REST/sync**, with caveats for live-stream:
- Employee secrets: Redis is external, so any instance can validate
- Ingestion: no ordering requirement, so round-robin works
- **Sequences**: `employee_id_seq` uses `NEXTVAL` — safe across instances
- **Cookie JWT auth is stateless** — no sticky sessions for admin REST
- **Live-stream media (Pion SFU) is process-local.** Redis shares presence/tickets/publisher
  location; a second API would need `INSTANCE_PUBLIC_URL` (watch redirect / `watchBaseUrl`).
  Current product deploy is **one API** — sticky LB is not configured.

### Connection Pooling

- pgxpool configured in `database/postgres.go`
- Default: MaxConns=25, MinConns=10, MaxConnLifetime=5min
- These are hardcoded in config with env overrides (`DB_MAX_OPEN_CONNS`, etc.)

### Background Jobs

**Jobs started in `main.go`:**
- `jobs/staleness_sweep.go` — hourly goroutine that sets `is_active = false` on `employee_installed_applications` / `employee_installed_packages` rows whose `last_seen_at` is older than `LINK_STALE_DAYS` (default 7). Link-lifecycle only, NOT data pruning.
- `ShiftService.ApplyDefaultTimezone` — runs once at boot when `DEFAULT_SHIFT_TIMEZONE` is set; updates all non-deleted shifts whose `timezone` is still `UTC` to that IANA value (idempotent).

**Environment (attendance-related):**

| Variable | Default | Purpose |
|---|---|---|
| `LINK_STALE_DAYS` | `7` | Catalog junction staleness window |
| `DEFAULT_SHIFT_TIMEZONE` | *(empty)* | Company IANA zone applied to legacy `UTC` shifts at boot; create-shift fallback when timezone omitted |

**Environment (live stream + presence, V3):**

| Variable | Default | Purpose |
|---|---|---|
| `LIVE_STREAM_ENABLED` | `true` | Master switch for SFU routes |
| `LIVE_STREAM_MAX_STREAMS` | `25` | Concurrent publisher rooms |
| `LIVE_STREAM_MAX_WATCHERS_PER_EMPLOYEE` | `10` | Watchers per employee room |
| `LIVE_STREAM_IDLE_SEC` | `90` | Idle reap (no watchers → stop publisher) |
| `WEBRTC_MAX_BITRATE_KBPS` | `12000` | Enforced on subscriber RTP forward (token bucket); client encode uses `ALPHA_STREAM_MAX_BITRATE_KBPS` |
| `WEBRTC_STUN_URLS` | Google STUN | Comma-separated STUN |
| `WEBRTC_TURN_*` | empty | Optional TURN for restrictive NATs (startup WARNING if empty) |
| `PRESENCE_WS_ENABLED` | `true` | Accept `GET /api/v1/ws` |
| `PRESENCE_WS_MAX_CONNECTIONS` | `10000` | Cap on simultaneous presence sockets |
| `INSTANCE_ID` | auto | Process id for Redis publisher/presence ownership |
| `INSTANCE_PUBLIC_URL` | empty | Base URL for watch redirects (only if a second API exists) |
| `REDIS_*` | localhost:6379 | Secrets + live-stream cluster keys |

**Still missing (data jobs):**
- No data-pruning job — app_sessions/app_items/device_hardware/etc. grow unbounded
- No data aggregation/pre-computation

---

## 10. Production-Readiness Gaps

| Gap | Severity | Details |
|---|---|---|
| **Sparse tests** | 🟠 Medium | Live-stream coverage exists (`internal/redis`, `internal/scale`, `internal/stream`); most handlers/services still untested. |
| **No structured logging** | 🟠 Medium | Uses `log.Printf` instead of a structured logger (zap, slog, logrus). No log levels beyond text prefix `[server]`, `[database]`, `[auth]`. |
| **No request validation** | 🟠 Medium | DTOs have `validate:` tags but **no validation library is imported**. Echo's `c.Bind(&req)` only deserializes JSON — it doesn't validate. All validation is manual `if req.Field == ""` checks. |
| **No rate limiting** | 🟠 Medium | Login endpoint and sync endpoints are completely unprotected. Brute-force / DoS is trivial. |
| **Health check bug** | 🟢 Low | Returns `c.RealIP()` as the timestamp value. Should use `time.Now()`. |
| **No graceful Redis degradation** | 🟠 Medium | If Redis is down, `employeeService` is constructed with a nil Redis client and employee secret generation/validation fails. No fallback (e.g., DB-stored secrets). |
| **No data-pruning job** | 🟢 Low | App sessions/items accumulate indefinitely. The staleness sweep only deactivates catalog links, it does not delete old data. |
| **No permission model** | 🟠 Medium | Server has no role-based access control. Any authenticated user can access all endpoints. |
| **Cross-account data injection** | 🟠 Medium | Sync handlers validate the JWT but don't verify the `employeeId` in the body matches the JWT subject. |
| **No graceful shutdown** | 🟢 Low | `Shutdown()` is called with a 10s context, but the stale-sweep goroutine isn't joined and in-flight requests may be dropped. |
| **Dead DTOs** | 🟢 Low | `new_schema_dto.go` still ships unused `BrowserContextEntry`, `UrlEntry`, `UrlVisitEntry`, `ShellCommandEntry` types. Harmless, but misleading. |
| **Legacy catalog owner column** | 🟢 Low | `installed_applications.employee_id`/`installed_packages.employee_id` are now just "first uploader" markers (catalog dedup is fingerprint-based). Keeping them allows legacy queries but is semantically stale. |

---

## 11. Immediate Next Steps

1. **Add rate limiting** — at minimum on `/auth/login` and sync endpoints
2. **Fix the health check bug** — `c.RealIP()` is not a timestamp
3. **Add structured logging** — replace `log.Printf` with `slog` (Go 1.21+ standard library)
4. **Add request validation** — use `go-playground/validator` or similar
5. **Cross-check employeeId in sync** — ensure the JWT userId matches the request employeeId
6. **Add tests** — start with service-layer tests using mock repositories, then handler integration tests
7. **Add data cleanup job** — periodic deletion of old app_sessions/app_items data
8. **Add server-side permissions** — even a simple role check before allowing writes
9. **Add listing endpoints for the remaining sync tables** — installed-apps, installed-packages, device-hardware, network-info, session-events
10. **Prune dead DTOs** — remove `BrowserContext*`, `Url*`, `UrlVisit*`, `ShellCommand*` types from `new_schema_dto.go`
