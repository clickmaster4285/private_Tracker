# File Hierarchy

Annotated node tree for the whole monorepo. Every directory that holds source is listed; leaf files are named where knowing the filename saves a search, and summarised where the count matters more than the names.

**How to read it:** ⭐ marks an entry point or a single-source-of-truth file — start there. 🔒 marks a file with a rule attached; changing it without reading the rule breaks something in the field. Generated / vendored trees are marked and should never be edited by hand.

*Last audited: 2026-10-30 (roaming Start/Stop UX through client 2.3.12 — `newplan.md`). Companion docs: [AGENTS.md](./AGENTS.md), [WORKFLOW.md](./WORKFLOW.md), [TESTING.md](./TESTING.md), [plan.md](./plan.md), [newplan.md](./newplan.md) (Fixed/Roaming), [doc/plan.md](./doc/plan.md), [client/ARCHITECTURE.md](./client/ARCHITECTURE.md), [client/UI_ARCHITECTURE.md](./client/UI_ARCHITECTURE.md), [server/ARCHITECTURE.md](./server/ARCHITECTURE.md), [web/ARCHITECTURE.md](./web/ARCHITECTURE.md).*

---

## Root

```
Alpha-AI-TrackerV2.0/
├── AGENTS.md              ⭐ repo constitution — rules, contracts, completion state. Read first.
├── FILE_HIERARCHY.md         this file
├── TESTING.md                live lab playbooks (DLP USB + removal code, orphan app-items, …)
├── WORKFLOW.md               dev loop, rebrand, add-a-page, release pipeline, DLP installer knobs
├── plan.md                   Security & DLP phased roadmap (P0+P1 shipped; P2 Web Blocker largely done)
├── newplan.md                Fixed/Roaming + Start/Stop + hourly control (P0 spine shipped source-build)
├── doc/plan.md               feature plans (public releases / private GitHub download bridge)
├── client/                   .NET 10 / Avalonia desktop agent + Windows DlpService
├── server/                   Go API + Postgres + Redis (the collector of record)
└── web/                      Next.js dashboard (the human-facing console)
```

There is **no shared monorepo tooling** (no Turborepo/Nx). Each service builds independently; cross-service docs live at the root (plus `doc/` for feature plans), service-scoped docs inside the service.

---

## `client/` — desktop agent

The only tier that touches the employee's machine. Everything here either **collects** (OS probes), **stores** (local SQLite), **syncs** (HTTP to server), or **renders** (Avalonia GUI).

```
client/
├── Program.cs                    ⭐ composition root: Host builder, ALL DI registration, startup order
├── App.axaml                        ~55 style selectors — every visual is a Classes.* rule
├── App.axaml.cs                     tray icon, single-instance SHOW handler, ShutdownMode
├── ViewLocator.cs                   Avalonia VM→View convention resolver
├── client.csproj                 🔒 reads VERSION at *evaluation* time; embeds APP_IDENTIFIERS;
│                                    <AvaloniaResource Include="Assets\**"/> globs in all UI assets
├── APP_IDENTIFIERS               ⭐🔒 single source of truth for the product name/publisher/ids
├── VERSION                       ⭐🔒 single source of truth for the version (read by AppInfo + packagers)
├── app.manifest                     Windows manifest (DPI, no elevation)
├── appsettings.json                 non-secret defaults; secrets live in config.enc
├── config.enc                       AES-256-GCM config, baked at build time by encrypt-config.sh
│
├── Core/                            OS-facing logic — no UI, no HTTP. The bulk of the intelligence.
│   ├── AppInfo.cs                ⭐ runtime branding: parses embedded APP_IDENTIFIERS + version
│   ├── EncryptedConfigService.cs 🔒 transport key → machine-derived key. TransportKeySeed /
│   │                                MachineKeyPrefix are KDF SEEDS — never templatize (see AGENTS §6)
│   ├── ExecutableMetadata.cs     ⭐ PE-subsystem probe — the anchor of the No-Hardcoded-Names rule
│   ├── InstalledAppDetector.cs      registry / Start Menu / .desktop / .app walks
│   ├── PackageDetector.cs           winget · npm · pip · choco · scoop · apt · snap · flatpak · brew
│   ├── SoftwareClassifier.cs        app vs package vs OS component — metadata-driven, no name lists
│   ├── SoftwareCategoryResolver.cs  category assignment from OS metadata
│   ├── SoftwareIdentityResolver.cs  identity/fingerprint for dedup across managers
│   ├── AppProcessClassifier.cs      running process → user-facing app or not
│   ├── ProcessFilter.cs             OS-shell process exclusions (the documented allowed exception)
│   ├── ParentProcessResolver.cs     process ancestry
│   ├── SessionHierarchyResolver.cs  nests child sessions under their parent app
│   ├── SessionLabelResolver.cs      human-readable session titles
│   ├── ActivityContextParser.cs     window-title → activity context
│   ├── CgroupResolver.cs            Linux cgroup → snap/flatpak attribution
│   ├── SingleInstanceService.cs     named mutex + pipe; second launch raises the running window
│   ├── CollectionExtensions.cs      small shared helpers
│   ├── Abstractions/                IActivityCollector · IInstalledAppDetector · ILogStore · IPackageDetector
│   ├── Models/                      ActivityLog · AppSession · DeviceHardwareInfo · EmployeeInfo ·
│   │                                HardwareDevice · SessionInfo
│   ├── BrowserAccessibility/        11 files — URL capture with NO extension and NO debugger port:
│   │                                factory + tracker + snapshot + helpers + history reader, and one
│   │                                reader per OS (LinuxAtSpi / WindowsUia / MacOsAccessibility).
│   │                                Browser detection is now dynamic via IBrowserRegistry (sourced from
│   │                                installed_applications.is_browser) — no hardcoded name lists.
│   └── DesktopEventBus/             7 files — RawDesktopEvent → validator → EventCoordinator →
│                                    JourneyEngine → JourneyRecord (the file/browser journey pipeline)
│
├── Services/                        long-running hosted services and OS integration
│   ├── LogCollectorService.cs    ⭐ the heartbeat: collect → persist → sync loop
│   ├── LinuxUserSystemdUnit.cs      User unit template RestartSec=8 + linger helper (L2)
│   ├── SyncService.cs               dedicated sync drain (byte-bounded, gzip, backoff)
│   ├── LiveStreamClient.cs          WebRTC VP8 publisher → /live-stream/push (ABR + connected-gated send)
│   ├── NetProbeService.cs           Uplink probe → POST /live-stream/uplink-probe; bitrate ladder
│   ├── ScreenCaptureService.cs      Windows multi-path GDI BitBlt → BGRA (DropOldest; Win11 hardened)
│   ├── ClientIdentityHeaders.cs     X-Client-Version / X-Client-Platform on DeviceAuth HTTP+WS
│   ├── WsClient.cs                  presence WS → /api/v1/ws (Online/Offline; not media)
│   ├── Streaming/
│   │   └── ScreenVp8Encoder.cs      libvpx wrapper that honors TargetKbps
│   ├── DesktopEventService.cs       hosts the event bus
│   ├── HardwareDeviceWatcherService.cs  plug/unplug tracking
│   ├── AutoStartService.cs          per-OS auto-start registration
│   ├── BackgroundGuardService.cs    keeps tracking alive when the window is closed
│   ├── AccountRemovalService.cs     drain → self-remove → wipe (CLI + wipe_local + protected uninstall)
│   ├── TrackerDeviceTokenReader.cs  SQLite device_token for removal-verify (Linux: alpha-ai-tracker data dir)
│   ├── ProtectedUninstallService.cs --protected-uninstall --code (Win: DLP pipe; Linux: HTTP + teardown.ok)
│   ├── DlpPipeClient.cs · DlpDeviceCredentialBridge.cs · WindowsTeardownCleanup.cs · CliConsole.cs
│   ├── FileLoggerProvider.cs        file sink → ~/.config/<pkg>/ (never the install dir)
│   └── Watchers/                    ATSPIEventWatcher · FileSystemEventWatcher · RecentFilesWatcher ·
│                                    WindowsExplorerWatcher · IExplorerWindowProvider
│                                    · SystemEventWatcher · InstalledSoftwareWatcher · IdleDetector …
│
├── DlpService/                   ⭐ Windows LocalSystem DLP + silent update + watchdog process
│   ├── NamedPipeHost.cs          (policy / teardown / update / health IPC)
│   ├── Program.cs · AlphaAITracker.Service.csproj · install-service.ps1 · uninstall-service.ps1
│   ├── PolicyPullWorker · EnforceWorker · DnsBlockPinWorker · UsbStorEnforcer · RemovableDriveMonitor
│   ├── LockdownNetworkEnforcer.cs   P1 default-deny 80/443 (AlphaAI-DLP-LOCK-*)
│   ├── DomainBlockEnforcer · TcpConnectionKiller · DnsCacheReader
│   ├── AlertSyncWorker · PolicyStore · NamedPipeHost · ServiceAclHardener · TrackerWatchdogWorker
│   └── DeviceCredentialStore · TeardownGate · UserSessionProcessLauncher · DlpConfig · WebBlockState
│
├── Platform/                        one ProcessCollector.cs per OS — the only place OS branching lives
│   ├── Linux/  Windows/  MacOS/
│
├── Storage/
│   ├── DatabaseSchema.cs            local SQLite DDL + migrations
│   └── SqliteLogStore.cs            ILogStore implementation; every UI read goes through this
│
├── Configuration/
│   ├── AppConfig.cs                 typed config surface
│   └── EnvLoader.cs              🔒 dev reads .env; installed reads config.enc, and replaces a stale
│                                    user copy when the shipped one differs
│
├── ViewModels/                      MVVM (CommunityToolkit.Mvvm)
│   ├── ViewModelBase.cs
│   ├── MainViewModel.cs          ⭐ shell: splash → login → wizard → page routing (1155 lines)
│   ├── DashboardViewModel.cs        page 4 — read-only aggregation
│   ├── SystemSpecsViewModel.cs      page 5 — read-only hardware snapshot
│   └── InstalledAppsViewModel.cs    page 6 — live inventory scan + InventoryRow projection
│
├── Views/
│   ├── MainWindow.axaml(.cs)     ⭐ ROUTER: 4 exclusive states + 246px nav rail
│   └── Pages/                       one UserControl per screen, each with x:DataType
│       ├── SplashPage           (1)  ├── DashboardPage      (4)
│       ├── LoginPage            (2)  ├── SystemSpecsPage    (5)
│       └── PermissionSetupPage  (3)  └── InstalledAppsPage  (6)
│
├── Converters/                      BoolInvert · StringNotEmpty · LoadingToText · PercentToGridLength
├── Styles/AppTheme.xaml          ⭐ design tokens (light): colors, brushes, shadows, radii, icons, fonts
├── Assets/                          avalonia-logo.ico · icon.png
│   └── backgrounds/                 login-hero · dashboard-hero · specs-hero · apps-hero (.png)
│
├── publish/                      ⭐ the entire release surface — see WORKFLOW.md §5
│   ├── build-installer.sh        🔒 orchestrator; bundle_into_publish() is where new assets go
│   ├── build-deb.sh  build-dmg.sh  installer-windows.iss   per-platform packagers
│   ├── generate-windows-vars.sh     APP_IDENTIFIERS + VERSION → windows_vars.iss
│   ├── encrypt-config.sh            .env → config.enc (must run AFTER any new env var is added)
│   ├── release.sh                   full pipeline + GitHub release
│   ├── firefox-a11y-apparmor.sh     runtime-referenced; lives here so it is always bundled
│   ├── avalonia-logo.ico
│   └── linux/ macos/ windows/       ⚠️ GENERATED publish output — never edit; windows/ also holds
│                                    the generated windows_vars.iss
│
├── installers/                   ⚠️ build artifacts (.deb / Setup.exe) — filenames carry VERSION
├── bin/ obj/                     ⚠️ MSBuild output — never edit
│
└── ARCHITECTURE.md · UI_ARCHITECTURE.md · build.md · APP_IDENTIFIERS_README.md · VERSION_README.md
```

**Entry points vs leaves.** `Program.cs` (DI + startup) and `MainWindow.axaml` (UI routing) are the two doors into the client. `Core/*` and `Platform/*` are leaves — pure logic reachable from a service or a VM, with no knowledge of who called them. If a change needs a new class wired in, it lands in `Program.cs`; if it needs a new screen, it lands in `MainWindow.axaml` plus `Views/Pages/`.

---

## `server/` — Go API

30 Go files, layered strictly. Requests flow **router → middleware → handler → service → repository → Postgres**, and never skip a layer.

```
server/
├── cmd/server/main.go        ⭐ entry point: config, DB, Redis, router, listen
├── go.mod / go.sum              module github.com/alpha-ai-tracker/server
├── Makefile                     build / run / migrate targets
│
├── internal/
│   ├── config/config.go         env → typed config
│   ├── database/postgres.go     pool + connection lifecycle
│   ├── redis/redis.go           client (employee one-time secrets)
│   ├── router/router.go      ⭐ ALL routes under /api/v1 — the definitive endpoint list
│   ├── middleware/auth.go       cookie (web) and JWT-in-body (client) auth paths
│   ├── middleware/device_auth.go  Device <token> auth for the 11 sync endpoints
│   │
│   ├── handlers/                HTTP edge — decode, validate, delegate. Never touch the DB.
│   │   ├── auth_handler.go          login, logout, me, check, employee-login, device revoke
│   │   ├── user_handler.go          console users (roleId-aware)
│   │   ├── employee_handler.go      tracked employees (+ import/export)
│   │   ├── department_handler.go
│   │   ├── monitoring_handler.go    types/categories/apps/websites classification
│   │   ├── rbac_handler.go          GET /modules + roles CRUD
│   │   ├── release_handler.go       public GET /releases + GET|HEAD /downloads/:assetId (private GitHub bridge)
│   │   └── new_schema_handler.go    the client-ingest surface (sessions, apps, packages, hardware) +
│   │                                 ListAppSessions + ListAppSessionsUsage (per-app aggregate for /employee-journey/apps) +
│   │                                 ListAppSessionsForApp (per-app paginated list, fired on chevron expand)
│   │
│   ├── services/                business rules — the only layer allowed to orchestrate repos
│   │   ├── auth_service.go · user_service.go · employee_service.go
│   │   ├── department_service.go · new_schema_service.go · monitoring_service.go · rbac_service.go
│   │   ├── github_release_service.go  private-repo Releases API + streaming download bridge (+ tests)
│   │   └── redis_interface.go       seam that keeps services testable without a live Redis
│   │
│   ├── repository/              SQL only — one file per aggregate
│   │   └── user_repo.go · employee_repo.go · department_repo.go · new_schema_repo.go
│   │      · device_repo.go · monitoring_repo.go · rbac_repo.go · refresh_token_repo.go
│   │      · dlp_repo.go (rules / policy versions / alerts / shadow IT / removal challenges)
│   │
│   ├── models/                  DB row shapes: user · employee · app_session ·
│   │                            device_hardware_info · employee_app_link · device ·
│   │                            status_tables · rbac · refresh_token · dlp.go
│   ├── dto/                     wire shapes: user_dto · employee_dto · new_schema_dto · rbac_dto · dlp_dto · releases_dto
│   ├── handlers/ · services/    … + dlp_handler.go / dlp_service.go (+ dlp_publish_smoke_test.go)
│   ├── jobs/                    staleness_sweep.go (stale catalog links)
│   │                            · retention_sweep.go (hourly data purge, RETENTION_DAYS)
│   │                            · session_lifecycle_sweep.go (1-min ACTIVE→STALE→CLOSED sweep)
│   ├── redis/cluster.go         live-stream presence / tickets / publisher registry (Phase 2)
│   ├── scale/instance.go        INSTANCE_ID resolution
│   └── stream/                  live preview WebRTC SFU — hub.go + sfu.go (preview only; no DB frames)
│
├── migrations/               ⭐ sequential SQL files (001…052+). Append-only: never edit applied migrations.
│                                051_dlp_security.sql · 052_dlp_alert_history.sql
└── bin/                      ⚠️ build output
```

**The `new_schema_*` triple** (`handler` / `service` / `repo` / `dto`) is the client-facing ingest path — that is where a client-side contract change lands on the server, not in the employee/user files. Live-stream signaling lands in `internal/stream/` + the stream handler from `router.go`.

---

## `web/` — Next.js dashboard

App Router. Route groups are directories; `(app)` is a **layout group** (parentheses = no URL segment) that wraps every authenticated page in the sidebar shell.

```
web/
├── package.json · next.config.ts · tsconfig.json · postcss.config.js · components.json (shadcn)
├── public/
└── src/
    ├── config.ts                  API base URL and runtime config
    ├── globals.css                Tailwind layers + CSS variables
    │
    ├── app/
    │   ├── login/ mfa/ forgot-password/ reset-password/ unauthorized/    unauthenticated routes
    │   └── (app)/              ⭐ ~30 authenticated sections, sidebar-wrapped:
    │       ├── dashboard · executive-dashboard
    │       ├── employees · departments · roles · onboarding
    │       │                       (/employees/[id] detail page removed 2026-08-18;
    │       │                        /roles is a real-API CRUD page since 2026-08-25)
    │       ├── employee-journey    per-employee journey behind the shared EmployeePage shell +
    │       │                       EmployeeSelector picker (?employeeId= deep-link): timeline ·
    │       │                       apps · web are real-API; screenshots · location placeholders
    │       ├── device-specs        per-employee machine picture over GET /employees/:id/detail:
    │       │                       hardware · software · peripherals · permissions
    │       ├── configuration       apps · websites · categories (monitoring classification, 2026-08-22)
    │       ├── shadow-it (live API) · logs (comprehensive · graphical · insights)
    │       ├── charts (activity · productivity) · reports · ai-summary
    │       ├── attendance · shifts · timesheets · hours-insights
    │       ├── dlp-alerts · dlp-rules · web-blocker (domains/paths/schedule) · audit-log · emails
    │       ├── goals · kpis · projects · productivity-scoring
    │       ├── screenshots · live-stream (WebRTC SFU; FPS; Open theater → /live-stream/theater) · gps-location
    │       └── settings (billing · compliance · notifications · security ·
    │                     tracking · user-management · profile)
    │
    │   (live-popout)/                 full-bleed theater shell (no AppSidebar)
    │       └── live-stream/theater/   /live-stream/theater?ids= watch wall
    │
    │   (public)/                      ⭐ no ProtectedRoute — public marketing/download surfaces
    │       └── releases/              /releases — published GitHub releases + installer download buttons
    │
    ├── components/
    │   ├── layout/                AppLayout · AppSidebar (Device Specs + Employee Journey are
    │   │                          collapsible sections) · TopBar · ProtectedRoute
    │   ├── live-stream/           WatchTile (FPS) · EmployeeSidebar · PreviewGrid · selection helpers
    │   ├── releases/              SafeMarkdown (sanitized GitHub release notes)
    │   ├── DownloadAppSection.tsx login + dashboard download dialog → API bridge (not GitHub)
    │   ├── dlp/                   DlpAlertDetailDialog (triage Save + history)
    │   ├── EmployeeSelector.tsx   searchable employee picker (shared query with EmployeePage)
    │   ├── TimezoneCombobox.tsx   searchable IANA TZ picker (@vvo/tzdb) — Web Blocker + Shifts
    │   ├── employees/             EmployeePage shell · InventoryTable · EmptyState · DeviceClassIcon
    │   ├── journey/               FocusTime (foreground/background stacked bar) · ActivityFilters (search + date presets)
    │   ├── sessions/              SessionStatusBadge (3-state app_sessions lifecycle: ACTIVE / STALE / CLOSED)
    │   ├── NavLink.tsx · providers.tsx
    │   └── ui/                    ~50 shadcn primitives — generated; regenerate rather than hand-edit
    │
    ├── hooks/
    │   ├── use-url-query-state.ts    ⭐ URL-synced filters (never router.* inside setState updater)
    │   ├── use-video-fps.ts          decoded FPS for live-stream <video> tiles
    │   └── use-url-activity-filter.ts · use-employee-detail.ts …
    │
    ├── lib/
    │   ├── api.ts              ⭐ every server call goes through here — the client-side contract
    │   │                         (incl. dlpApi · shadowItApi · issueRemovalCode)
    │   ├── releases.ts            public fetchReleases / downloadAssetUrl (no cookies required)
    │   ├── dlp-labels.ts          trigger / severity / status display labels
    │   ├── useLiveStreamSocket.ts  WebRTC watch PC + watch WS for /live-stream
    │   ├── format.ts              shared formatters (duration · seconds · MB · dates)
    │   ├── auth.tsx               session context
    │   ├── permissions.tsx        SERVER-driven RBAC gating (submodule keys from user.permissions)
    │   ├── store/                 Redux store + typed hooks (legacy mock store.ts deleted 2026-08-25)
    │   └── utils.ts
    │
    ├── hooks/                     use-mobile · use-toast · use-employee-detail
    └── node_modules/           ⚠️ vendored
```

**Route count vs completion.** The route tree is far ahead of the backend — many sections render UI against endpoints that do not exist yet. Treat `web/`'s directory count as *scaffolding*, not delivered features; [AGENTS.md](./AGENTS.md) §5 carries the honest per-tier completion figures.

---

## Where a change lands

| Change | Files it touches |
| ------ | ---------------- |
| New OS probe / detector | `client/Core/*.cs` (+ `Core/Abstractions/` if it needs an interface) → registered in `client/Program.cs` |
| New GUI screen | `client/ViewModels/` + `client/Views/Pages/` + `Program.cs` DI + `MainWindow.axaml` rail & host — see [UI_ARCHITECTURE.md](./client/UI_ARCHITECTURE.md) §7 |
| Re-brand or version bump | `client/APP_IDENTIFIERS` **or** `client/VERSION` — nothing else |
| New runtime asset (file-based) | the asset + `client/publish/build-installer.sh` **and** `build-deb.sh` + `build-dmg.sh` + `installer-windows.iss` |
| New UI asset under `Assets/` | the asset only — the `AvaloniaResource` glob compiles it in |
| New env var / secret | `.env` → re-run `encrypt-config.sh` before building the installer |
| New API endpoint | `server/internal/router/router.go` + `handlers/` + `services/` + `repository/` (+ `dto/`, + a new numbered migration if the schema moves) |
| New dashboard page | `web/src/app/(app)/<route>/` + `lib/api.ts` + a sidebar entry in `components/layout/AppSidebar.tsx` + a module in `lib/permissions.tsx`; per-employee pages reuse the `EmployeePage` shell + `EmployeeSelector` |
| Public (no-login) page | `web/src/app/(public)/<route>/` — **not** under `(app)`; no sidebar RBAC module |
| Public installer download | `server` `GITHUB_*` env + `github_release_service` / `release_handler` + web `lib/releases.ts` — never put the token in `NEXT_PUBLIC_*` |
