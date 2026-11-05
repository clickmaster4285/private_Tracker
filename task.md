# Alpha Monitoring — ERP Tasks

> **Project:** Alpha Monitoring
> **Goal:** SaaS Based Setup
> **Period:** Jul 24, 2026 → Sep 8, 2026
> **Total Goals:** 25 | **Total Tasks:** ~118
> **Note:** No start/end date falls on a Sunday

---

## Goal 1: Project Foundation & Core Setup

**Start:** Jul 24, 2026 | **End:** Jul 27, 2026
**Description:** Set up the core desktop client infrastructure — encrypted configuration, background service mode, cross-platform process collection, local SQLite database, and session tracking. This goal established the base layer that every subsequent feature builds on.

| #   | Task                                               | Description                                                                                                                                                                                                                                                                          | Start Date   | End Date     |
| --- | -------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------------ | ------------ |
| 1.1 | E ncrypted config system                          | Implement AES-256-GCM encryption for the .env config file with automatic transport-key to machine-key migration, so secrets are never stored in plaintext on employee machines.                                                                                                      | Jul 24, 2026 | Jul 24, 2026 |
| 1.2 | Background service mode                            | Add --background headless mode that runs tracking services without opening the GUI, with auto-start persistence on all platforms (Linux systemd, Windows Run key, macOS plist).                                                                                                      | Jul 24, 2026 | Jul 25, 2026 |
| 1.3 | Cross-platform process collection                  | Build the process collector that reads running processes, window titles, and CPU/memory usage on Windows, Linux, and macOS using platform-native APIs.                                                                                                                               | Jul 25, 2026 | Jul 25, 2026 |
| 1.4 | SQLite local storage with relational schema        | Create the local SQLite database with 11 relational tables (device_hardware_info, installed_applications, installed_packages, network_info, session_events, app_sessions, app_items, app_status, permission_status, employee_info, storage_devices) including idempotent migrations. | Jul 25, 2026 | Jul 27, 2026 |
| 1.5 | Session tracking with PID hierarchy + cgroup dedup | Implement app session tracking that groups multi-process GUI apps (VS Code, Chrome) into one session per logical window using Linux systemd cgroups and Windows/macOS PPID chain walking.                                                                                            | Jul 27, 2026 | Jul 27, 2026 |

---

## Goal 2: Browser Journey Tracking

**Start:** Jul 28, 2026 | **End:** Aug 21, 2026
**Description:** Build a complete browser activity tracking system that captures every page visit (URL, title, domain) across all browsers — Chrome, Firefox, Edge, Brave, etc. — including private/incognito windows. Evolved from a Chrome extension approach to a zero-dependency accessibility-tree reader that works on every browser version without debugger or extension.

| #   | Task                                | Description                                                                                                                                                                                                                                                                           | Start Date   | End Date     |
| --- | ----------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------ | ------------ |
| 2.1 | Browser extension for Chrome        | Develop a Chrome MV3 extension that captures real-time tab navigations (URL, title, tabId, windowId) via chrome.tabs events and sends them through native messaging.                                                                                                                  | Jul 28, 2026 | Jul 28, 2026 |
| 2.2 | Native messaging service            | Build NativeMessageService — a BackgroundService listening on a Unix domain socket that receives browser navigation events from the native host and stores them as browser_tab and browser_navigation app_items.                                                                     | Jul 28, 2026 | Jul 28, 2026 |
| 2.3 | Accessibility-based browser journey | Replace the extension approach with OS accessibility tree reading (AT-SPI on Linux via D-Bus, UIA on Windows via Interop, AX on macOS via osascript) so browser journeys work on every browser and every Chrome version (136+) with no debugger, no extension.                        | Jul 31, 2026 | Aug 5, 2026  |
| 2.4 | Browser history reader fallback     | Build a hybrid URL fallback that reads each browser's own profile history database (Chromium History file, Firefox places.sqlite) while the browser runs, as a fallback when the accessibility tree doesn't expose URLs.                                                              | Aug 6, 2026  | Aug 6, 2026  |
| 2.5 | Private/incognito window tracking   | Enable capture of private browsing URLs — Firefox via AT-SPI DocURL (role 95), Chrome incognito via the --force-renderer-accessibility flag. Add ALPHA_BROWSER_CAPTURE_INCOGNITO config gate (legal-safe default).                                                                   | Aug 6, 2026  | Aug 7, 2026  |
| 2.6 | Dynamic browser registry            | Replace all hardcoded browser name lists (BROWSER_HINTS, ResolveFamily, StripBrowserSuffix) with IBrowserRegistry sourced from installed_applications.is_browser metadata. Any browser with a .desktop Categories=WebBrowser or Windows URLAssociations is auto-detected.             | Aug 20, 2026 | Aug 21, 2026 |
| 2.7 | Embedded webview journey            | Track websites opened INSIDE apps (VS Code Simple Browser, Slack, Teams, any Electron/embedded browser) as web activity. Detected structurally — a window carries web content iff its accessibility tree contains a DOCUMENT_WEB node with an http(s) URL. Zero hardcoded app names. | Aug 18, 2026 | Aug 18, 2026 |
| 2.8 | Per-page browser_tab records        | Stop overwriting a single browser_tab root item on every navigation. Now each page visit closes the previous tab record and opens a fresh one, so the web dashboard shows per-page history instead of only the last page title.                                                       | Aug 6, 2026  | Aug 6, 2026  |

---

## Goal 3: File Explorer Journey Tracking

**Start:** Jul 29, 2026 | **End:** Aug 8, 2026
**Description:** Track which folders and files employees browse — navigation events in file managers (Nautilus, Dolphin, Thunar on Linux; Explorer on Windows), file create/rename/delete operations, and recently opened files. Provides a complete desktop activity trail beyond just browser and app usage.

| #   | Task                                        | Description                                                                                                                                                                                                                                                                                                                         | Start Date   | End Date     |
| --- | ------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------ | ------------ |
| 3.1 | AT-SPI event watcher                        | Build a Linux D-Bus watcher using Tmds.DBus.Protocol that listens for AT-SPI focus and window events to detect when a file manager window gains focus.                                                                                                                                                                              | Jul 29, 2026 | Jul 29, 2026 |
| 3.2 | FileSystemEventWatcher + RecentFilesWatcher | Create filesystem watchers on user directories (Documents, Desktop, Downloads, etc.) and an XBEL monitor at ~/.local/share/recently-used.xbel to capture file create/rename/delete and recently-opened-file events.                                                                                                                 | Jul 29, 2026 | Jul 29, 2026 |
| 3.3 | EventCoordinator + JourneyEngine            | Build the event processing pipeline: EventCoordinator deduplicates (3s window), correlates, and normalizes raw filesystem events into business events; JourneyEngine resolves the active AppSession and creates AppItem rows with 9 journey fields (object_type, action, journey_id, sequence, paths, window_id, tab_id, metadata). | Jul 29, 2026 | Jul 29, 2026 |
| 3.4 | Windows Explorer watcher                    | Implement Windows file-explorer tracking using Shell COM (Shell.Application → Windows()) to read each Explorer window's LocationURL as its exact browsed folder, plus journey-driven watching for browsed directories and .lnk shortcut resolution for recent files.                                                               | Aug 8, 2026  | Aug 8, 2026  |

---

## Goal 4: Software Inventory Detection

**Start:** Jul 30, 2026 | **End:** Aug 11, 2026
**Description:** Build an accurate, metadata-driven software inventory system that detects installed GUI applications and CLI packages on all three platforms without hardcoded product name lists. Classifies software, deduplicates across sources, and tracks install/uninstall lifecycle in real-time.

| #   | Task                                        | Description                                                                                                                                                                                                                                                                           | Start Date   | End Date     |
| --- | ------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------ | ------------ |
| 4.1 | Installed app detection                     | Scan OS-native databases for installed applications: Linux .desktop files (XDG_DATA_DIRS), Windows registry Uninstall keys + Start Menu .lnk shortcuts, macOS /Applications .app bundles. No hardcoded name lists — detection is 100% metadata-driven.                               | Aug 8, 2026  | Aug 8, 2026  |
| 4.2 | Windows structural detection                | Replace all Windows name blocklists with PE Subsystem detection (GUI=2 vs CUI=3), C:\Windows tree exclusion, Driver Store registry scanning, and registry flag analysis (SystemComponent, URLAssociations). Works on every Windows version, every department, every language.         | Aug 8, 2026  | Aug 8, 2026  |
| 4.3 | Package detection                           | Detect CLI tools, runtimes, and libraries installed via package managers — npm/pip/apt/brew/choco/winget/scoop/cargo/snap/flatpak — as installed_packages (separate from GUI apps).                                                                                                 | Aug 8, 2026  | Aug 8, 2026  |
| 4.4 | InstalledSoftwareWatcher                    | Build an event-driven BackgroundService that watches OS install locations (Linux .desktop dirs + dpkg, Windows Start Menu + Program Files, macOS /Applications) and triggers instant rescan on install/uninstall. No periodic polling — 100% event-driven with debounce and min-gap. | Aug 10, 2026 | Aug 10, 2026 |
| 4.5 | Software classification pipeline            | Build a cross-source dedup pipeline: SoftwareCategoryResolver (metadata-driven categories from .desktop, bundle IDs), SoftwareClassifier (GUI apps from InstalledAppDetector win over matching PackageDetector entries), SoftwareIdentityResolver (stable SHA-256 identity).          | Jul 30, 2026 | Jul 30, 2026 |
| 4.6 | One row per install cycle                   | Redesign the inventory lifecycle: one row per install→uninstall cycle. Reinstalling creates a new row (install_date = OS-reported date). Pre-existing software the tracker never saw installed shows NULL/Unknown. No install counter.                                               | Aug 10, 2026 | Aug 10, 2026 |
| 4.7 | GUI shows only currently-installed software | Filter the Installed Applications page to show only open (uninstall_date IS NULL) install cycles. Hide historical uninstalled rows — the table is a current-state view, not an audit log.                                                                                            | Aug 11, 2026 | Aug 11, 2026 |

---

## Goal 5: Client GUI Rebuild

**Start:** Aug 8, 2026 | **End:** Aug 10, 2026
**Description:** Rebuild the Avalonia desktop GUI from a single monolithic window into a six-page router-based UI with a navigation rail. Each page is a separate ViewModel. All brand strings (product name, version, tagline) derive from a single source (APP_IDENTIFIERS + VERSION) — rebranding requires editing exactly two files.

| #   | Task                      | Description                                                                                                                                                                                                                                                                                                       | Start Date   | End Date     |
| --- | ------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------ | ------------ |
| 5.1 | Six-page GUI              | Build 6 pages under Views/Pages/: Splash (boot checklist), Login (employee identity), PermissionSetup (stepper wizard), Dashboard (identity + status tiles + pipeline health), SystemSpecs (CPU/RAM/GPU/storage/network/peripherals), InstalledApps (searchable apps + packages inventory with virtualized list). | Aug 8, 2026  | Aug 10, 2026 |
| 5.2 | Runtime branding pipeline | Implement AppInfo.cs that reads APP_IDENTIFIERS (embedded resource) with a strict regex and exposes DisplayName, Publisher, Version, etc. Every visible brand string in the GUI, tray, window title, and installer derives from these two files.                                                                  | Aug 10, 2026 | Aug 10, 2026 |
| 5.3 | Nav rail + page router    | Convert MainWindow from a single view into a router with a 246px navigation rail. Each page is a UserControl behind the rail; MainWindow switches between them. Tray icon with Quit for tray-less desktops.                                                                                                       | Aug 10, 2026 | Aug 10, 2026 |

---

## Goal 6: Sync Engine Decoupled

**Start:** Aug 11, 2026 | **End:** Aug 11, 2026
**Description:** Separate the data synchronization loop from the collection loop so that network issues never block data collection. The new dedicated SyncService drains unsent SQLite rows in byte-bounded, gzip-compressed chunks with exponential backoff and a per-pass time budget. 50k+ backlogs drain in minutes.

| #   | Task                                 | Description                                                                                                                                                                                                                                         | Start Date   | End Date     |
| --- | ------------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------ | ------------ |
| 6.1 | Dedicated SyncService                | Build client/Services/SyncService.cs as a BackgroundService that drains unsent rows in chunks bounded by row count (ALPHA_SYNC_MAX_ROWS, 1000) and payload bytes (~1MB), with gzip compression and ~150ms polite pauses between chunks.             | Aug 11, 2026 | Aug 11, 2026 |
| 6.2 | Exponential backoff + 5-min budget   | Implement exponential backoff on sync failure (5s→10s→…→5min max) capped at 60s so a drain pass reaches the server within a minute even during repeated failures. 5-min per-pass budget prevents huge backlogs from monopolizing CPU.           | Aug 11, 2026 | Aug 11, 2026 |
| 6.3 | Instant sync on login                | Add RequestImmediateSync() signal so that login and session restore trigger an immediate full drain pass instead of waiting for the idle tick. A request while a pass is running is a single no-op release — never stacks passes.                  | Aug 11, 2026 | Aug 11, 2026 |
| 6.4 | Client retention + new sync surfaces | Delete synced data the server already has (app_items/sessions older than 24h, superseded network_info, closed install cycles). Add 4 new sync surfaces: app_status, hardware_devices, permission_status, storage_devices. New server migration 017. | Aug 11, 2026 | Aug 11, 2026 |

---

## Goal 7: Auto-Update System

**Start:** Aug 12, 2026 | **End:** Aug 19, 2026
**Description:** Enable the desktop client to self-update from GitHub Releases — auto-checking for new versions, downloading the platform-specific installer, and installing it with minimal user interaction. Includes a GUI "Check Updates" button and update banner on the dashboard.

| #   | Task                              | Description                                                                                                                                                                                                                                                             | Start Date   | End Date     |
| --- | --------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------ | ------------ |
| 7.1 | GitHub Releases self-updater      | Build AppUpdateService that checks the GitHub Releases API, normalizes the version tag, compares against AppInfo.Version, and auto-downloads the platform installer (Linux .deb, Windows .exe, macOS .dmg). Runs on a configurable auto-check loop (default 24h).       | Aug 12, 2026 | Aug 12, 2026 |
| 7.2 | GUI Check Updates button + banner | Add a "Check updates" ghost button in the top bar, a "Update to vX.Y.Z" install button with progress bar, a "Restart to apply" button (Linux dpkg replaces binary while running), and an update banner on the dashboard.                                                | Aug 12, 2026 | Aug 12, 2026 |
| 7.3 | Windows auto-install fix          | Fix the Inno Setup tree-kill bug (taskkill /F /T was killing the updater's own .cmd script). Rewrite AppUpdateService to use a detached .cmd that polls tasklist until the app exits, runs the silent installer, waits for UAC approval, and relaunches with --restart. | Aug 12, 2026 | Aug 12, 2026 |
| 7.4 | Linux update handoff              | Replace synchronous pkexec dpkg with a detached bash script in /tmp that waits for the parent tracker process to terminate before invoking pkexec dpkg -i, then relaunches with --background --restart on success.                                                      | Aug 19, 2026 | Aug 19, 2026 |

---

## Goal 8: Session Lifecycle (3-State)

**Start:** Jul 31, 2026 | **End:** Sep 2, 2026
**Description:** Fix the orphan "Running forever" bug by introducing a server-owned 3-state lifecycle (ACTIVE → STALE → CLOSED) for app_sessions. Previously, sessions with ended_at=NULL stayed "Running" on the web dashboard forever once a PC went offline or was uninstalled. Now the server promotes stale sessions and freezes their duration at close time.

| #   | Task                                 | Description                                                                                                                                                                                                                                                      | Start Date   | End Date     |
| --- | ------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------ | ------------ |
| 8.1 | 3-state model + migration 031        | Add status (ACTIVE/STALE/CLOSED), last_activity_at, last_sync_at columns to app_sessions via migration 031. Add indexes for the lifecycle sweeper and upsert recovery logic that flips STALE/CLOSED back to ACTIVE when a live client re-uploads.                | Sep 2, 2026  | Sep 2, 2026  |
| 8.2 | Lifecycle sweeper job                | Implement session_lifecycle_sweep.go running every 1 minute: ACTIVE→STALE when last_sync_at is older than 10 min, STALE→CLOSED when older than 24h. Freezes ended_at = COALESCE(last_activity_at, last_sync_at, started_at) at close time.                     | Sep 2, 2026  | Sep 2, 2026  |
| 8.3 | Upsert recovery                      | On BulkInsertAppSessions, when a live client re-uploads any row with ended_at=NULL, the ON CONFLICT CASE flips STALE/CLOSED back to ACTIVE and clears premature server-side ended_at — so a 2-hour network outage never destroys information.                   | Sep 2, 2026  | Sep 2, 2026  |
| 8.4 | Foreground/background focus tracking | Add foreground_seconds and background_seconds to app_sessions (client SQLite + server migration 020). Accumulated per collection cycle for every open session through the root-PID group, flushed every ~60s with is_synced=0. Web shows stacked green/gray bar. | Aug 18, 2026 | Aug 18, 2026 |
| 8.5 | Atomic cascade-close                 | Close sessions and their app_items in ONE SQLite transaction via CloseSessionsAndAppItemsAsync(). Prevents orphaned open items when a session ends — a crash mid-close can no longer leave orphans.                                                             | Jul 31, 2026 | Jul 31, 2026 |

---

## Goal 9: Time & Attendance Foundation

**Start:** Aug 28, 2026 | **End:** Sep 1, 2026
**Description:** Build the client-side Time & Attendance (T&A) foundation — OS power/lock/login telemetry, idle detection, shift caching, and local daily attendance rollup. This is Phase 1: all data is collected and rolled up locally on the client; server-side T&A APIs are in Goal 10.

| #   | Task                                        | Description                                                                                                                                                                                                                                                                                    | Start Date   | End Date     |
| --- | ------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------ | ------------ |
| 9.1 | IEventRecorder + SessionEventRecorder       | Create a single-write-funnel abstraction for all session_events writes. 5-second dedup window collapses bursts (UPower + ScreenSaver firing within 1s → one row). Hard 2-second write timeout guarantees telemetry never blocks shutdown.                                                     | Aug 28, 2026 | Aug 28, 2026 |
| 9.2 | SystemEventWatcher                          | Monitor OS power/lock/login events per platform: Linux D-Bus UPower PrepareForSleep + login1 Lock/Unlock + ScreenSaver ActiveChanged; Windows SystemEvents PowerModeChanged + SessionSwitch; macOS NSWorkspace stub. 30s lock-hysteresis prevents false triggers.                              | Aug 28, 2026 | Aug 28, 2026 |
| 9.3 | IdleDetector                                | Detect OS idle state using Mutter.IdleMonitor D-Bus first, X11 XScreenSaver fallback, Windows GetLastInputInfo. Polls every 30s, emits threshold-crossing idle_start/idle_end markers for attendance rollup.                                                                                   | Aug 28, 2026 | Aug 28, 2026 |
| 9.4 | ScheduleCacheService + LocalTimeSkewService | Cache employee shift schedules from the server (GET /api/v1/schedules/me on login + every 6h). Measure client↔server clock skew from HTTP Date header every 15 min with post-resume stabilization skip.                                                                                       | Aug 28, 2026 | Aug 28, 2026 |
| 9.5 | AttendanceAggregator                        | Build a 5-minute daily rollup into daily_attendance_cache: arrival/departure/active/idle time with present/late/absent/off_shift/unknown status. Handles company holidays, nullable no-event arrival, schedule overlap, off-shift seconds, and idle+screen-lock union without double-counting. | Aug 28, 2026 | Aug 28, 2026 |
| 9.6 | Session-event sync aggregation              | Implement A.9/A.10: group unsynced session_events into closed time buckets (300s windows) at sync drain, POST aggregated {count, firstAt, lastAt} to /api/v1/session-events/sync. Add 50k local-row ceiling (ALPHA_TA_MAX_LOCAL_ROWS).                                                         | Sep 1, 2026  | Sep 1, 2026  |

---

## Goal 10: Server — Shifts, Attendance & Holiday API

**Start:** Aug 31, 2026 | **End:** Sep 1, 2026
**Description:** Build the server-side Time & Attendance APIs: shift management with timezone support, company holidays, and attendance status calculation (present/late/absent/off_shift). Connects the client's local T&A data to the web dashboard.

| #    | Task                                       | Description                                                                                                                                                                                                                                   | Start Date   | End Date     |
| ---- | ------------------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------ | ------------ |
| 10.1 | Migration 028                              | Add shift timezone, company holidays table, and aggregate event fields to the server database. Schema supports per-shift timezone configuration and holiday exclusion from attendance calculation.                                            | Aug 31, 2026 | Aug 31, 2026 |
| 10.2 | Schedule CRUD + server-time + holiday CRUD | Implement full CRUD for employee schedules (shift assignment per employee with timezone), a /server-time endpoint for clock skew measurement, and company holiday management (create/list/delete).                                            | Aug 31, 2026 | Aug 31, 2026 |
| 10.3 | Attendance APIs                            | Build attendance status calculation: compare employee's first active event against shift start + grace period to determine present/late, calculate off-shift seconds, handle absent days with no events, and return timezone-aware responses. | Aug 31, 2026 | Aug 31, 2026 |
| 10.4 | DEFAULT_SHIFT_TIMEZONE env                 | Add DEFAULT_SHIFT_TIMEZONE environment variable so existing shifts on UTC are automatically rewritten to the company's real timezone on server boot. Create-shift fallback when client omits timezone.                                        | Sep 1, 2026  | Sep 1, 2026  |

---

## Goal 11: Web — Employee Journey Pages

**Start:** Aug 17, 2026 | **End:** Aug 18, 2026
**Description:** Build the Employee Journey module — a shared shell with an employee picker, plus three live-API sub-pages: Session Timeline (individual sessions with foreground/background bars), App Usage (aggregated per-app totals), and Web Activity (browser page visits grouped by domain).

| #    | Task                                           | Description                                                                                                                                                                                                                                                               | Start Date   | End Date     |
| ---- | ---------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------ | ------------ |
| 11.1 | EmployeePage shell                             | Build a shared EmployeePage component with page header, searchable EmployeeSelector picker (deep-linkable via ?employeeId=), and loading/error/no-selection states. Pages receive {employee, detail, detailLoading} as render props.                                      | Aug 17, 2026 | Aug 18, 2026 |
| 11.2 | Session Timeline page                          | Build the session timeline using useInfiniteQuery on GET /app-sessions (30/page, IntersectionObserver sentinel for infinite scroll). Shows per-session rows with FocusTime foreground/background stacked bar, opened/closed times, and app identity.                      | Aug 18, 2026 | Aug 18, 2026 |
| 11.3 | App Usage page                                 | Build the app usage aggregation page: groups the most recent 5×100 app sessions into per-app foreground/background totals with duration. Apps with >1 session get an expandable chevron showing per-session detail (opened/closed/duration).                             | Aug 18, 2026 | Aug 18, 2026 |
| 11.4 | Web Activity page                              | Build the web activity page with infinite scroll on GET /app-items?itemType=browser_tab. Pages grouped by domain ("Visited Sites") with browser badge (Chrome/Firefox/etc.) from metadata_json.processName. Search switches to flat "Matching Pages" view with exact URL. | Aug 18, 2026 | Aug 18, 2026 |
| 11.5 | Date filters + session groups + browser badges | Add shared ActivityFilters component (debounced search, Today-default date presets, custom range via Calendar popover) wired into both App Usage and Web Activity pages. KeepPreviousData so filter changes never flash a spinner.                                        | Aug 18, 2026 | Aug 18, 2026 |

---

## Goal 12: Web — Device Specs Pages

**Start:** Aug 11, 2026 | **End:** Aug 18, 2026
**Description:** Build the Device Specs module — four pages over a new aggregate server endpoint that returns one employee's full machine picture in a single response: hardware specs, installed software, peripherals, and permissions.

| #    | Task                               | Description                                                                                                                                                                                                                                                                                                   | Start Date   | End Date     |
| ---- | ---------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------ | ------------ |
| 12.1 | Hardware Overview page             | Display the employee's machine hardware: CPU, RAM, GPU, storage devices, network info, and device status — all from the new GET /employees/:id/detail aggregate endpoint.                                                                                                                                    | Aug 18, 2026 | Aug 18, 2026 |
| 12.2 | Installed Software page            | Two-tab view (Applications / Packages) with search and virtualized list. Shows the employee's currently-installed software from the server's catalog junction tables. Uses InventoryTable component for consistent rendering.                                                                                 | Aug 18, 2026 | Aug 18, 2026 |
| 12.3 | Peripherals page                   | Show currently plugged-in and recently unplugged hardware devices (keyboard, mouse, headset, USB drives) with DeviceClassIcon badges. Data comes from hardware_devices synced by the client.                                                                                                                  | Aug 18, 2026 | Aug 18, 2026 |
| 12.4 | GET /employees/:id/detail endpoint | Build a new server aggregate endpoint that returns one employee's full machine picture in a single response: employee record, latest device_hardware_info, storage_devices, network_info, installed apps/packages (via junction tables), hardware_devices, permission_status, app_status, and activity stats. | Aug 11, 2026 | Aug 11, 2026 |

---

## Goal 13: Web — Employees Enhancements

**Start:** Aug 18, 2026 | **End:** Aug 18, 2026
**Description:** Improve the Employees list page with infinite scroll pagination, Excel import/export for bulk operations, and action menu links to the new Employee Journey and Device Specs modules.

| #    | Task                              | Description                                                                                                                                                                                                                                                                                        | Start Date   | End Date     |
| ---- | --------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------ | ------------ |
| 13.1 | Server-side infinite scroll       | Replace page state + Previous/Next buttons with useInfiniteQuery + IntersectionObserver sentinel pattern. 10 employees/page, filter changes restart at page 1, inline "Loading more…" + "Showing all N" footer. Follows the mandatory Web Infinite-Scroll Rule.                                   | Aug 18, 2026 | Aug 18, 2026 |
| 13.2 | Excel import/export               | Export downloads employees-<date></date>.xlsx from GET /employees/export. Import reads .xlsx/.xls/.csv in the browser (xlsx client-side), extracts matching columns, posts to POST /employees/import. Server imports in one transaction with per-row outcomes (imported/updated/skipped + reason). | Aug 18, 2026 | Aug 18, 2026 |
| 13.3 | Portal-rendered Radix action menu | Replace the custom absolute-positioned dropdown (clipped by overflow-x-auto) with Radix DropdownMenu (portal-rendered, never clipped). Each row's action menu links to View Journey (/employee-journey/timeline) and Device Specs (/device-specs).                                                 | Aug 18, 2026 | Aug 18, 2026 |

---

## Goal 14: Web — Configuration Redesign

**Start:** Aug 20, 2026 | **End:** Aug 22, 2026
**Description:** Rebuild the Configuration section as a real domain — dynamic Applications and Websites classification pages with type/category assignment, backed by new relational PostgreSQL tables. Add manual website creation and a Categories & Types CRUD interface.

| #    | Task                                  | Description                                                                                                                                                                                                                                                                   | Start Date   | End Date     |
| ---- | ------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------ | ------------ |
| 14.1 | Migration 023                         | Add monitoring_types (Productive/Unproductive/Neutral with colors), monitoring_categories (name + kind CHECK application/website/both), classification columns on installed_applications, and monitoring_sites (website registry auto-synced from distinct app_items.domain). | Aug 20, 2026 | Aug 20, 2026 |
| 14.2 | Dynamic Applications + Websites pages | Build two pages using the shared ClassifiedItemsTable component: Applications and Websites with infinite scroll, debounced search, Type/Category/unclassified filters, and inline per-row Type+Category selects with in-place cache updates.                                  | Aug 20, 2026 | Aug 22, 2026 |
| 14.3 | Categories & Types CRUD               | Build a two-tab CRUD page: Types (cards with color picker, create/edit/delete) and Categories (cards with name + kind select for application/website/both). Type delete returns 409 while any app/site references it.                                                         | Aug 20, 2026 | Aug 20, 2026 |
| 14.4 | Manual website creation               | Add "Add Website" button + modal dialog on the Websites page. POST /monitoring/websites with optional type/category. Server normalizes domain (strips protocol/path/lowercases) and runs live duplicate detection.                                                            | Aug 22, 2026 | Aug 22, 2026 |

---

## Goal 15: Dynamic RBAC

**Start:** Aug 25, 2026 | **End:** Aug 25, 2026
**Description:** Replace the client-side permission matrix with a server-driven RBAC system: roles, modules, and submodules stored in PostgreSQL, seeded idempotently at boot. Web UI for managing roles with per-submodule permission toggles. RouteGuard protects pages based on granted submodule keys.

| #    | Task                         | Description                                                                                                                                                                                                                                                                    | Start Date   | End Date     |
| ---- | ---------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------------ | ------------ |
| 15.1 | Migration 025                | Add roles, modules, submodules, and role_submodule_permissions tables. Seed the SYSTEM company_admin role. Users.role_id becomes the sole role source of truth (legacy users.role/is_company_admin dropped).                                                                   | Aug 25, 2026 | Aug 25, 2026 |
| 15.2 | RBACService.SeedCatalog      | Build an idempotent boot seeder that re-ensures the module/submodule catalog and system role grants on every server start. Modules and submodules carry route_path for deep-linking.                                                                                           | Aug 25, 2026 | Aug 25, 2026 |
| 15.3 | /roles CRUD + GET /modules   | Implement protected server endpoints: GET /modules (full module→submodule tree), and full CRUD for /roles. Role payloads carry both submoduleIds (grants) and derived permissions keys. User create/update accept roleId.                                                     | Aug 25, 2026 | Aug 25, 2026 |
| 15.4 | Web /roles page + RouteGuard | Rebuild /roles as a real-API CRUD page with per-submodule permission toggles over GET /modules + /roles. Implement RouteGuard that resolves the current pathname to a module and redirects unauthorized access to /unauthorized. PermissionsProvider carries user.permissions. | Aug 25, 2026 | Aug 25, 2026 |

---

## Goal 16: Auth — Refresh Tokens + Profile

**Start:** Aug 25, 2026 | **End:** Aug 28, 2026
**Description:** Implement rotating refresh tokens for secure web session management (15-min access JWT, 30-day refresh cookie), a self-service Profile page, and a full user-management edit flow with server-projected cross-table flags.

| #    | Task                                     | Description                                                                                                                                                                                                                                                                                      | Start Date   | End Date     |
| ---- | ---------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------------ | ------------ |
| 16.1 | Migration 026                            | Add refresh_tokens table with user_id FK CASCADE, UNIQUE token_hash (SHA-256 of raw), expires_at, revoked_at, deleted_at. Raw token never touches DB — only its hash is stored.                                                                                                                 | Aug 25, 2026 | Aug 25, 2026 |
| 16.2 | POST /auth/refresh + cookie rotation     | Implement the refresh endpoint: validate cookie hash → mint replacement access+refresh pair → revoke old row (crash-safe rotation). Login sets both cookies; logout revokes server-side. Web api.ts catches 401s → single-flight refresh → replay original request.                          | Aug 25, 2026 | Aug 25, 2026 |
| 16.3 | Self-service Profile page                | Build /settings/profile visible to every authenticated user (no permission gate). Shows Account info (read-only tiles), Modules you can access (per-module granted/submodule counts with system-admin "all" override), and Update details form (name/email/password with confirm + eye toggles). | Aug 28, 2026 | Aug 28, 2026 |
| 16.4 | User-Management edit flow + hasUserLogin | Add per-row Edit button on /settings/user-management → same dialog in edit mode. Identity fields locked, company_admin excluded from role dropdown, password + confirm-password with eye toggles. Server projects hasUserLogin via EXISTS() on every employee SELECT — no client-side map.     | Aug 28, 2026 | Aug 28, 2026 |

---

## Goal 17: Web — Dashboard Live API

**Start:** Aug 25, 2026 | **End:** Aug 25, 2026
**Description:** Remove all mock/localStorage data from the web dashboard and replace every page with real API calls. The dashboard shows live stat tiles; pages with no backend endpoint render honest empty states instead of fake data.

| #    | Task                               | Description                                                                                                                                                                                                                                                                                                                                                   | Start Date   | End Date     |
| ---- | ---------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------ | ------------ |
| 17.1 | Remove all localStorage mock data  | Delete web/src/lib/store.ts (328-line mock factory) and all localStorage references. Run a one-time sweep that purges every orphaned alpha_ai_tracker_* key. Auth was already cookie-based — nothing else read localStorage.                                                                                                                                 | Aug 25, 2026 | Aug 25, 2026 |
| 17.2 | Dashboard stat tiles from live API | Wire the dashboard to real endpoints: Total Employees (+ tracked/untracked split) from GET /employees, Departments count from GET /departments, App Sessions ·24h from GET /app-sessions?dateFrom=, Web Pages ·24h from GET /app-items?itemType=browser_tab&dateFrom=. Mock chart cards replaced by analytics empty states linking the real journey routes. | Aug 25, 2026 | Aug 25, 2026 |

---

## Goal 18: Web — URL-Synced Filters

**Start:** Sep 1, 2026 | **End:** Sep 2, 2026
**Description:** Make every filterable page on the web dashboard keep its filter state in the browser URL query string. The URL is the single source of truth — deep-linkable, shareable, survives back/forward. React Query keys derive from the URL; no useState for filters.

| #    | Task                                          | Description                                                                                                                                                                                                                                                                                                                                       | Start Date  | End Date    |
| ---- | --------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------- | ----------- |
| 18.1 | useUrlQueryState + useUrlActivityFilter hooks | Build generic URL-synced state hooks: useUrlQueryState<T></t> for ad-hoc filter records (debounced, preserves unrelated keys, strips empty values), useUrlActivityFilter for the rich {search, preset, dateFrom, dateTo} shape with preset encoding.                                                                                              | Sep 1, 2026 | Sep 1, 2026 |
| 18.2 | Wire URL-synced filters into 13 pages         | Apply the URL-synced filter pattern to: employees (search + department), shifts (search), timesheets (employee + from + to), attendance (date + status), employee-journey/timeline, employee-journey/apps, employee-journey/web, device-specs/software, configuration/apps, configuration/websites, logs/comprehensive, settings/user-management. | Sep 1, 2026 | Sep 2, 2026 |
| 18.3 | ActivityFilters redesign                      | Redesign the shared ActivityFilters component: debounced search, Today-default date presets with real local-day bounds, custom range via Calendar popover. No "Clear" button — clearing means selecting "All time" or emptying the search input.                                                                                                 | Sep 1, 2026 | Sep 1, 2026 |

---

## Goal 19: Web — Attendance & Timesheets

**Start:** Aug 28, 2026 | **End:** Sep 3, 2026
**Description:** Build the web-side Attendance and Timesheets pages that consume the server's T&A APIs, plus dynamic shift management with timezone support. Admins can view daily attendance status and employee timesheets with active time calculations.

| #    | Task             | Description                                                                                                                                                                                                                                                  | Start Date   | End Date     |
| ---- | ---------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------------ | ------------ |
| 19.1 | Attendance page  | Build /attendance with date + status filters (URL-synced). Shows per-employee daily status: Present, Late, Absent, Off Shift. Deep-links employee name to /timesheets with employee + date pre-filled. Uses formatDateTimeInZone for timezone-aware display. | Sep 1, 2026  | Sep 2, 2026  |
| 19.2 | Timesheets page  | Build /timesheets with employee + from + to filters (URL-synced). Shows per-day active time calculation based on attendance events. Supports deep-link from attendance page to pre-fill employee and date range.                                             | Sep 1, 2026  | Sep 3, 2026  |
| 19.3 | Shift management | Add dynamic shift creation/editing with timezone defaulting from Intl.DateTimeFormat().resolvedOptions().timeZone. Attach shifts to employees. Server applies DEFAULT_SHIFT_TIMEZONE to existing UTC shifts on boot.                                         | Aug 28, 2026 | Aug 28, 2026 |

---

## Goal 20: Cross-Platform — Linux Fixes

**Start:** Jul 31, 2026 | **End:** Sep 5, 2026
**Description:** Fix critical Linux-specific issues: Wayland GUI crash from stale XAUTHORITY, AT-SPI active-state bitmask format change in at-spi2-core ≥ 2.50, snap Firefox AppArmor blocking AT-SPI, and Flatpak proxy PID resolution.

| #    | Task                               | Description                                                                                                                                                                                                                                                                    | Start Date   | End Date     |
| ---- | ---------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------------ | ------------ |
| 20.1 | Wayland GUI fix                    | Fix the installed service crashing on Wayland: remove hardcoded XAUTHORITY=~/.Xauthority from systemd units, add BackgroundGuardService migration that removes stale DISPLAY/XAUTHORITY overrides, lazy GUI startup refreshes graphical environment from user systemd manager. | Aug 31, 2026 | Aug 31, 2026 |
| 20.2 | AT-SPI active state bitmask decode | Fix foreground detection on at-spi2-core ≥ 2.50: the GetState return changed from a list of state IDs to a packed 64-bit bitmask. Decode both formats and test ACTIVE(1)/FOCUSED(12) bits instead of searching for string "8".                                                | Aug 18, 2026 | Aug 18, 2026 |
| 20.3 | Snap Firefox AppArmor AT-SPI fix   | Load a surgical AppArmor profile override adding dbus (receive) rule so the AT-SPI bridge can call into snap Firefox while the sandbox stays enforcing. Install a systemd oneshot that re-applies after snap refresh.                                                          | Aug 6, 2026  | Aug 6, 2026  |
| 20.4 | Flatpak bwrap PPID chain walk      | When the AT-SPI PID belongs to Flatpak's bwrap or xdg-dbus-proxy IPC broker, walk up the PPID chain via /proc/<pid></pid>/stat until reaching the real app process. Resolve FLATPAK_ID from /proc/<pid></pid>/environ for the short app name.                                  | Aug 22, 2026 | Aug 22, 2026 |

---

## Goal 21: Cross-Platform — Windows Fixes

**Start:** Aug 8, 2026 | **End:** Aug 18, 2026
**Description:** Fix critical Windows-specific issues: auto-update installer killing itself, duplicate sessions from missing cgroup dedup, inaccurate software detection from hardcoded name lists, and hardware detection using wrong WMI class.

| #    | Task                                | Description                                                                                                                                                                                                                                       | Start Date   | End Date     |
| ---- | ----------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------ | ------------ |
| 21.1 | Windows auto-install fix            | Fix Inno Setup KillRunningInstance using /T (tree-kill) which killed the updater's own cmd script. Rewrite to use a detached .cmd that polls tasklist by image name, runs silent installer, waits for UAC consent, and relaunches with --restart. | Aug 12, 2026 | Aug 12, 2026 |
| 21.2 | Stale installer cleanup             | After every successful update (Windows + Linux) and on startup, force-delete the updates/ directory. Downloads stream into user data dir, never the install dir. Prevents old installers from accumulating on employee machines.                  | Aug 18, 2026 | Aug 18, 2026 |
| 21.3 | Duplicate session fix               | Fix multi-process GUI apps (VS Code, Chrome) creating 8 identical session rows on Windows (no cgroups). Root-PID grouping walks the PPID chain to the topmost same-binary process. Boot hydration deduplicates legacy per-PID sessions.           | Aug 15, 2026 | Aug 15, 2026 |
| 21.4 | Windows software detection overhaul | Replace all Windows name blocklists with PE Subsystem detection (GUI vs console), C:\Windows tree exclusion, Driver Store registry, registry flag analysis, and Start Menu .lnk shortcut scanning. 100% metadata-driven, zero hardcoded names.    | Aug 8, 2026  | Aug 8, 2026  |
| 21.5 | Windows file journey                | Implement Windows file-explorer tracking using Shell COM (Shell.Application → Windows()) to read Explorer window LocationURL, plus journey-driven FileSystemWatcher for browsed directories and .lnk shortcut resolution for recent files.       | Aug 8, 2026  | Aug 8, 2026  |
| 21.6 | Windows hardware detection          | Fix hardware detection: switch from Win32_PnPEntity (no Class property) to Get-PnpDevice -PresentOnly (real Class values), add USBSTOR/USB InstanceIds for DiskDrive, require USB for NetworkAdapter. Filter PrintQueue (virtual printers).       | Aug 8, 2026  | Aug 8, 2026  |

---

## Goal 22: Security & Auth Fixes

**Start:** Aug 1, 2026 | **End:** Sep 2, 2026
**Description:** Strengthen the security posture: device-level authentication tokens, clean shutdown handling, single-instance enforcement, sync data integrity, and background service reliability.

| #    | Task                                    | Description                                                                                                                                                                                                                                                 | Start Date   | End Date     |
| ---- | --------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------ | ------------ |
| 22.1 | Device authentication                   | Generate secure 256-bit opaque tokens (dev_tok_...) alongside JWT on employee login. Upsert hardware metadata into employee_devices. Enforce device validation on all 11 sync endpoints via DeviceAuth middleware. Add admin device listing and revocation. | Aug 19, 2026 | Aug 19, 2026 |
| 22.2 | Clean SIGTERM handling                  | Replace uncancellable infinite delay with host.WaitForShutdownAsync() so systemd SIGTERM reaches hosted-service shutdown. ShutdownSentinel persists power_off on SIGTERM/Ctrl+C/Avalonia Exit.                                                              | Aug 31, 2026 | Aug 31, 2026 |
| 22.3 | Single-instance mutex                   | Use .NET's machine-wide Global\ namespace with AppInfo.AppMutex so only one tracker instance runs per machine. A second launch signals the running instance to raise its window via named pipe. Prevents duplicate sessions from concurrent processes.      | Aug 31, 2026 | Aug 31, 2026 |
| 22.4 | is_synced=0 reset on every UPDATE       | Audit and fix 8 write paths that mutated rows without re-queuing them for sync: session close, device disconnect, ON CONFLICT upserts, network_info mutators. Server always learns changes — no more silent data loss after updates.                       | Aug 12, 2026 | Aug 12, 2026 |
| 22.5 | Background guard watchdog + file logger | Add a background guard watchdog that monitors the tracking service health. Add FileLoggerProvider that writes ILogger output to dotnetrunlog.txt for debugging installed builds.                                                                            | Aug 1, 2026  | Aug 3, 2026  |

---

## Goal 23: Web — App Usage Duration Fix

**Start:** Sep 4, 2026 | **End:** Sep 4, 2026
**Description:** Fix Chrome with multiple tabs showing "30 min" instead of "10 min" on the App Usage page. Root cause: a11y reader returns a fresh WindowKey per tab, old resolver only collapsed tabs with matching titles (transient "Loading…" states slipped through), and the web page summed per-row durations instead of computing open-range. Fixed across all 3 layers: client, server, web.

| #    | Task                                    | Description                                                                                                                                                                                                                                                                                                  | Start Date  | End Date    |
| ---- | --------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ----------- | ----------- |
| 23.1 | Client: ResolveWindowKey collapse rules | Add two new collapse rules: (a) exact URL match stronger than title, kills the "3 tabs all titled Loading…" transient case, and (b) fresh-key recency window of poll×2 seconds that attaches a new same-PID key to the freshest tracked window. Prevents splitting one real window into multiple sessions. | Sep 4, 2026 | Sep 4, 2026 |
| 23.2 | Server: GET /app-sessions/usage         | Build new aggregate endpoint returning one row per (appDisplayName, processName) with MIN(started_at) as firstOpenedAt, MAX(COALESCE(ended_at, last_sync_at, started_at)) as lastClosedAt, and SUM for totalDurationSeconds. Composite index migration 032 covers the WHERE + GROUP BY.                      | Sep 4, 2026 | Sep 4, 2026 |
| 23.3 | Web: duration as open range             | Change the App Usage page useMemo to recompute duration as lastClosedAt - firstOpenedAt regardless of what the server returns. Anti-sum regression guard: even if the server regresses and returns per-tab sums, the web always shows the correct open-range duration.                                       | Sep 4, 2026 | Sep 4, 2026 |

---

## Goal 24: Web — Search Query Grouping

**Start:** Sep 5, 2026 | **End:** Sep 5, 2026
**Description:** On the Web Activity page, search-engine queries (Google, Bing, Yahoo, DuckDuckGo) are now parsed from URL query params and grouped by query text instead of being flattened under the domain. Each search shows an engine badge and is expandable with a chevron arrow.

| #    | Task                               | Description                                                                                                                                                                                                                                                                             | Start Date  | End Date    |
| ---- | ---------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------- | ----------- |
| 24.1 | Search engine query extraction     | Implement searchQueryOf(item) that parses known search engines from URL params: Google ?q=, Bing ?q=, Yahoo ?p=, DuckDuckGo ?q=. Use the search query as the group key for search-engine URLs instead of the domain.                                                                    | Sep 5, 2026 | Sep 5, 2026 |
| 24.2 | Grouped search results with badges | In the Visited Sites view, search rows render with a Search icon + engine badge (Google/Bing/Yahoo/DuckDuckGo) and are expandable with a chevron arrow. In the Matching Pages view, results are grouped by search query with the same expand/collapse behavior instead of a flat table. | Sep 5, 2026 | Sep 5, 2026 |

---

## Goal 25: GPS / Location (Coming Soon)

**Start:** Sep 1, 2026 | **End:** Sep 1, 2026
**Description:** Lay the groundwork for GPS/location tracking — client collection, server geofence backend, and web UI — but gate everything behind a feature flag (ALPHA_LOCATION_ENABLED=false). Full implementations are preserved in dedicated files, ready to enable when the feature is approved.

| #    | Task                                | Description                                                                                                                                                                                                                                  | Start Date  | End Date    |
| ---- | ----------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------- | ----------- |
| 25.1 | Client GPS collection               | Implement client-side GPS data collection with ALPHA_LOCATION_ENABLED=false as default. When enabled, collects location data and syncs to the server. Ships disabled — no battery or privacy impact.                                        | Sep 1, 2026 | Sep 1, 2026 |
| 25.2 | Server geofence backend + sync APIs | Build server-side geofence storage and sync APIs for GPS data. Geofence rules and location history stored in PostgreSQL. APIs accept location sync from client and serve location trail to web. Ships disabled — no server resource impact. | Sep 1, 2026 | Sep 1, 2026 |
| 25.3 | Web LocationComingSoon page         | Build a LocationComingSoon placeholder page for the web dashboard. Full live implementations preserved in GpsLocationLive.tsx and LocationTrailLive.tsx — flip LOCATION_UI_ENABLED=true in web/src/lib/locationUi.ts to re-enable.          | Sep 1, 2026 | Sep 1, 2026 |

---

## Summary

| Goal                        | Description                                                                                   | Tasks          | Start            | End             |
| --------------------------- | --------------------------------------------------------------------------------------------- | -------------- | ---------------- | --------------- |
| 1. Project Foundation       | Encrypted config, background mode, process collection, SQLite, session tracking               | 5              | Jul 24           | Jul 27          |
| 2. Browser Journey          | Accessibility tree reading, history fallback, incognito, dynamic registry, webviews           | 8              | Jul 28           | Aug 21          |
| 3. File Explorer Journey    | AT-SPI watcher, filesystem watchers, event coordinator, Windows Explorer                      | 4              | Jul 29           | Aug 8           |
| 4. Software Inventory       | OS-native detection, PE subsystem, package managers, event-driven watcher, lifecycle          | 7              | Jul 30           | Aug 11          |
| 5. Client GUI Rebuild       | 6-page router, runtime branding, nav rail                                                     | 3              | Aug 8            | Aug 10          |
| 6. Sync Engine              | Dedicated service, backoff, instant sync, new sync surfaces                                   | 4              | Aug 11           | Aug 11          |
| 7. Auto-Update              | GitHub Releases, GUI buttons, Windows/Linux installer fixes                                   | 4              | Aug 12           | Aug 19          |
| 8. Session Lifecycle        | ACTIVE/STALE/CLOSED, sweeper, upsert recovery, focus tracking, cascade-close                  | 5              | Jul 31           | Sep 2           |
| 9. Time & Attendance        | Event recorder, power/lock telemetry, idle detector, schedule cache, rollup                   | 6              | Aug 28           | Sep 1           |
| 10. Server T&A API          | Shift timezone, holidays, attendance status, DEFAULT_SHIFT_TIMEZONE                           | 4              | Aug 31           | Sep 1           |
| 11. Web Employee Journey    | Shell, timeline, app usage, web activity, date filters                                        | 5              | Aug 17           | Aug 18          |
| 12. Web Device Specs        | Hardware, software, peripherals, aggregate endpoint                                           | 4              | Aug 11           | Aug 18          |
| 13. Web Employees           | Infinite scroll, Excel import/export, Radix action menu                                       | 3              | Aug 18           | Aug 18          |
| 14. Web Configuration       | Monitoring tables, classification pages, categories/types CRUD, manual website                | 4              | Aug 20           | Aug 22          |
| 15. Dynamic RBAC            | Roles/modules/submodules, seed catalog, API endpoints, RouteGuard                             | 4              | Aug 25           | Aug 25          |
| 16. Auth Refresh + Profile  | Refresh tokens, cookie rotation, Profile page, user-edit flow                                 | 4              | Aug 25           | Aug 28          |
| 17. Dashboard Live API      | Mock purge, live stat tiles                                                                   | 2              | Aug 25           | Aug 25          |
| 18. URL-Synced Filters      | Generic hooks, 13-page wiring, ActivityFilters redesign                                       | 3              | Sep 1            | Sep 2           |
| 19. Attendance & Timesheets | Attendance page, timesheets page, shift management                                            | 3              | Aug 28           | Sep 3           |
| 20. Linux Fixes             | Wayland GUI, AT-SPI bitmask, snap AppArmor, Flatpak PID                                       | 4              | Jul 31           | Sep 5           |
| 21. Windows Fixes           | Auto-install, stale installer, duplicate sessions, detection overhaul, file journey, hardware | 6              | Aug 8            | Aug 18          |
| 22. Security & Auth         | Device tokens, SIGTERM, single-instance, sync integrity, background guard                     | 5              | Aug 1            | Sep 2           |
| 23. App Usage Fix           | Client collapse rules, server usage endpoint, web duration-as-range                           | 3              | Sep 4            | Sep 4           |
| 24. Search Query Grouping   | Query extraction, engine badges + expand/collapse                                             | 2              | Sep 5            | Sep 5           |
| 25. GPS/Location            | Client GPS, server geofence, web Coming Soon (flag-gated)                                     | 3              | Sep 1            | Sep 1           |
| **TOTAL**             |                                                                                               | **~118** | **Jul 24** | **Sep 8** |
