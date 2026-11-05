# Web Architecture — Alpha AI Tracker Dashboard

> **Last audited:** 2026-11-05 (Allowlists module)
> **Changelog:**
> - 2026-11-05: **Fail-closed copy.** Web Allow Lists: Publish opens allow hosts only;
>   PCs block all sites after login without a prior Publish. Switch Off + Publish =
>   admin opt-out.
> - 2026-11-05: **Allowlists module.** Sidebar **Allowlists** → `/allowlists/web`
>   (RBAC `allowlists/web`: default-deny + host chips) and `/allowlists/apps`
>   (`allowlists/apps`: publisher/SHA draft). Lockdown card removed from
>   `/web-blocker` (deny-list rules only; notice links to Web Allow Lists).
>   `dlpApi.lockdown` carries `allowApps` / `maxAllowApps`.
> - 2026-11-05: **P1 lockdown UI** (superseded surface). Originally a card on
>   `/web-blocker`; now under Allowlists. Employees **Pause lockdown…** still
>   calls `POST .../lockdown-pause`.
> - 2026-11-05: **Tracking mode removed.** `/configuration/device-configuration`,
>   `SetTrackingModeDialog`, `TrackingModeBadge`, Employees "Set tracking mode…", and
>   Device Specs mode chips are gone. `employeesApi.setMachineTrackingMode` and
>   `deviceConfigurationApi` are deleted. `EmployeeDevice` no longer carries mode fields.
> - 2026-11-05: **Terms & Conditions removed.** `/settings/terms-and-conditions` and its
>   create/edit/view routes are gone, along with the sidebar entry. Live stream no longer
>   shows "Consent required" or blocks a tile on `consentMissing`.
> - 2026-11-04: **`/employee-journey/web` expanded pages wrap long titles/URLs.** Nested
>   Page/URL columns use `table-fixed` + `break-words`/`break-all` (no ellipsis truncate that
>   still forced parent horizontal scroll). Colspan cell `max-w-0` keeps wrap inside the card.
> - 2026-10-31: **Employees removal commands split.** **Remove Current Device** shows plain
>   `--remove-account` only (`removeAccountCommands()` in `lib/client-commands.ts`) — no auto-issued
>   code. **Issue Removal Code** shows Linux + Windows `--protected-uninstall --code "…"` rows
>   (`protectedUninstallCommands`). Matches client: account wipe needs no code; product uninstall does.
> - 2026-10-31: **Device Configuration** under Configuration
>   (`/configuration/device-configuration`, RBAC `configuration/device-configuration`).
>   Tabs (URL `?tab=`): Roaming PC list / Tracker mode types / Logs. API client
>   `deviceConfigurationApi`. Roaming list is **one employee row** with chevron history
>   (`employees[].current` + `history[]`); guards undefined `employees` so a stale payload
>   cannot throw. Infinite scroll on roaming + logs.
> - 2026-10-30: **Set tracking mode dialog + Device Specs header picker UX.**
>   `SetTrackingModeDialog` offers **Fixed** / **Roaming** only (default Fixed); removed
>   “Use organization default” — Save always PATCHes an explicit mode (API `null` inherit
>   remains for P3 org policy). `EmployeePage` device `SelectTrigger` shows only
>   `deviceLabel` so the `h-9` control no longer wraps status + mode badge (those stay in
>   `SelectItem` + the Hardware Overview Tracking mode card). Plan: `newplan.md` §17.
> - 2026-10-26: **Public `/releases` page + download bridge (no GitHub calls from the browser).**
>   New `(public)/releases` route group (no `ProtectedRoute` / `RouteGuard`). Lists published
>   releases via `GET /api/v1/releases`; installers via native `<a href>` /
>   `window.location` to `GET /api/v1/downloads/:assetId`. Sanitized notes:
>   `components/releases/SafeMarkdown.tsx` (`react-markdown` + `rehype-sanitize` + `remark-gfm`).
>   Helpers in `lib/releases.ts`. `DownloadAppSection` (login + dashboard) uses the same API —
>   removed direct `api.github.com` / `browser_download_url` / `NEXT_PUBLIC_GITHUB_REPO`.
>   Verified: `npx tsc --noEmit`. Plan: `doc/plan.md`.
> - 2026-10-24: **DLP UX** — `/dlp-alerts` shows blocked domain chip + detail Domains list
>   (`evidenceDomains` in `dlp-labels.ts`). `/dlp-rules` full edit/detail/history + Active Switch
>   with auto-publish (`GET /dlp/rules/:id/history`). `/web-blocker` status uses Switch (list + form).
> - 2026-10-24: **Reusable `TimezoneCombobox`** (`components/TimezoneCombobox.tsx`, `@vvo/tzdb` +
>   cmdk search). Wired into `/web-blocker` schedule form and `/shifts` create/edit. Prefer IANA
>   picks (e.g. Asia/Karachi) so Windows DLP schedule eval does not silently fall back to UTC.
> - 2026-10-24: **Web Blocker UX** — domain chips, related-domain suggestions (deduped), Active/Inactive
>   auto-publish, edit + revision history dialog, URL paths field removed from create/edit form.
> - 2026-10-23: **Web Blocker page** under Security & DLP (`/web-blocker`, RBAC `web-blocker`).
>   Authors `web_navigate` rules with `match.domains` / `match.paths` / `match.schedule`
>   (timer window); still uses `POST /dlp/rules/publish`. `/dlp-rules` lists non-web triggers only.
> - 2026-10-22: **Security & DLP pages go live (Rules / Alerts / Shadow IT) + Issue Removal Code.**
>   Replaced mocks with `dlpApi` / `shadowItApi` in `lib/api.ts`. `/dlp-rules` — CRUD + **Publish**
>   (immutable policy version). `/dlp-alerts` — infinite scroll + URL-synced filters + status
>   counters; `DlpAlertDetailDialog` loads alert + `GET /dlp/alerts/:id/history`; triage fields
>   only PATCH on **Save** (not on every dropdown change). `/shadow-it` — catalog list +
>   Approve/Block/Flag. Employees row menu: **Issue Removal Code** → `POST /devices/removal-code`
>   (code shown once). Labels in `lib/dlp-labels.ts`. RBAC keys already on sidebar:
>   `dlp-rules` / `dlp-alerts` / `shadow-it`. Verified: `npx tsc --noEmit`.
> - 2026-10-19 (late): **"All devices" removed from the Device Specs machine dropdown (user request).**
>   `components/employees/EmployeePage.tsx` no longer renders the `__all__` sentinel (the
>   `ALL_DEVICES` constant and its `SelectItem` are gone, and the trigger placeholder is now
>   "Select device"). Whenever the employee has 2+ machines the shell ALWAYS scopes to exactly one
>   machine: the machine id in `?deviceId=` while it is still a registered machine, otherwise the
>   first entry of `devices[]` (the server orders machines active-first, then most recently tracked,
>   so the default is the live machine). The resolved scope is mirrored back into the URL
>   (`useEffect` → `setExtra`, never inside a setState updater — URL-Synced Filters Rule), the
>   render-prop now hands bodies `deviceId: effectiveDeviceId`, and a stale/unknown `?deviceId=`
>   falls back the same way. Single-machine employees are untouched: no dropdown is rendered and the
>   employee-wide aggregate (server `deviceId` omitted) is what they already saw. Verified:
>   `npx tsc --noEmit` clean, `next build` passes, and the running dev server recompiles
>   `/device-specs` (200) with the new chunk.
> - 2026-10-19: **Device Specs machine dropdown shows the machine lifecycle status (`EmployeeDevice` fix).**
>   `EmployeeDevice` in `src/lib/api.ts` dropped the stale `isActive` field (the server had been
>   emitting `isActiveDevice`, so `isActive` was always `undefined` and **every** device rendered as
>   "removed") and now mirrors the server DTO: `status` (`active` | `old`), `firstTrackAt`,
>   `lastTrackAt`, `wipeRequestedAt`, `isActiveDevice`, `removeAt`. The list itself now comes from the
>   server's canonical `employee_machines` registry (migration 049) — one entry per physical machine,
>   deduplicated even though `employee_devices` rotates a token row per login.
>   `components/employees/EmployeePage.tsx` renders each entry as
>   `"{employee} ({hostname})" · active` / `"· old · removed (last tracked …)"` via the new
>   `deviceStatusLabel`, and `app/(app)/device-specs/page.tsx` keys its removed-device empty state off
>   `status === 'old'` (Retired-Device History Rule: a removed machine keeps its last-known data and
>   must not claim the client "will upload one after login"). The URL-synced `?deviceId=` selector and
>   the `__all__` aggregate are unchanged. Verified: `npx tsc --noEmit` clean.
> - 2026-10-16: **Sidebar/route gating fixed for Terms & Conditions — the nav entry had no `module` key.**
>   `AppSidebar.tsx` rendered **Settings → Terms & Conditions** for every authenticated user (a
>   module-less child short-circuits both `isItemVisible` and `visibleChildrenOf` to `true`), and
>   `findModuleForPath` fell through its exact-match pass to the `/settings` prefix, so the page was
>   actually gated by the unrelated **General Settings** permission: a role without it (HR-only) saw
>   the item and was bounced to `/unauthorized` on click, while a role holding plain `settings` got
>   T&C create/edit/delete. The entry now carries `module: "settings/terms-and-conditions"`, matching
>   the new server submodule key, so nav visibility and the route guard agree. `/settings/profile` is
>   the only remaining module-less entry and is intentionally ungated (`RouteGuard.ALWAYS_ALLOWED`).
>   Verified: `npx tsc --noEmit` clean; `GET /modules` returns the key (Settings 8/8 for `company_admin`).
> - 2026-10-15: **Live stream — remote ICE is queued until the answer is applied (fixes LIVE tiles with zero frames).**
>   `useLiveStreamSocket` held a single `pcRef` and applied every `ice` message immediately; because the
>   SFU trickles candidates from a separate goroutine (they can precede the `answer`), a candidate could
>   arrive while `remoteDescription` was still null — `addIceCandidate` throws `InvalidStateError`, the
>   candidate was silently dropped, ICE/DTLS never completed, and the tile stayed LIVE / `--- FPS` while
>   the 5s renegotiation loop spun. New per-PC `PcEntry { pc, remoteReady, queue }` in `pcStateRef`: the
>   `answer` branch awaits `setRemoteDescription`, flips `remoteReady`, then flushes the queue in arrival
>   order; the `ice` branch queues while `!remoteReady` and only calls `addIceCandidate` once ready;
>   `cleanupPc` nulls the entry so a renegotiated PC gets fresh state. `ws.onmessage` became `async` for
>   the awaited set. No server contract or page change beyond the hook.
>   Verified: `npx tsc --noEmit` clean.
> - 2026-10-05: **Dashboard home redesign — summary-first.**
>   `/dashboard` consumes `dashboardApi.summary` + capped
>   `liveStreamApi.employees({ onlineOnly, limit })` + departments filter.
>   URL-synced `preset`/`from`/`to`/`departmentId`; per-widget skeletons; attention strip;
>   fleet/classification/top apps/domains/recent; dismissible download strip. Components under
>   `components/dashboard/`. Verified: `npx tsc --noEmit`; `next build`.
> - 2026-10-03: **Client Version on employees/live-stream refreshes without re-login (server+client).**
>   Web still reads `employee_devices.client_version` as before; server `TouchLastSeen` now
>   updates version/platform from DeviceAuth headers so a reinstall of 1.2.33+ shows immediately.
>   No web code change. Blank FA-27 preview was client capture/WebRTC (fixed in installer 1.2.34).
> - 2026-10-03: **Live stream V3 — ticket `watchBaseUrl` + tile teardown hygiene.**
>   `useLiveStreamSocket` prefers `watchBaseUrl` from `GET /live-stream/watch-ticket` when set
>   (future multi-API); otherwise `NEXT_PUBLIC_WS_URL`. Phase 0: clear open timer; pause video /
>   stop local tracks on cleanup (avoid orphan MediaStream). Employee list poll:
>   `refetchInterval` 2s + `staleTime` 1s (console + theater share queryKey). Verified:
>   `npx tsc --noEmit` clean.
> - 2026-09-29: **Live stream theater UX — collapsible sidebar, cols-per-row, scrollable wall.**
>   Theater navbar: sidebar toggle, **1–4 screens/row** (`?cols=`), online-only filter, clear,
>   fullscreen, link to console. Sidebar collapses to a thin rail (`?sidebar=0`). Preview scrolls
>   with aspect-video tiles; theater max **16** concurrent (main console stays max 4).
> - 2026-09-29: **Live stream theater popout + per-tile FPS.**
>   Shared UI in `components/live-stream/` (`LiveStreamWatchTile`, `LiveStreamEmployeeSidebar`,
>   `LiveStreamPreviewGrid`, `live-stream-selection.ts`). Each LIVE tile shows decoded FPS
>   (`hooks/use-video-fps.ts`). **Open theater** on `/live-stream` opens `/live-stream/theater?ids=`
>   in a new tab under route group `(live-popout)` — auth/RBAC only, no AppSidebar/TopBar.
>   Theater reuses the same sidebar + grid (max 4). Browsers always show some address chrome.
> - 2026-09-29: **Live stream V2 — WebRTC SFU viewer + multi-tile `?ids=` + `useUrlQueryState` fix.**
>   `/live-stream` uses `<video>` + `lib/useLiveStreamSocket.ts` (RTCPeerConnection; one offer on
>   open, one renegotiate on `track_ready`, never on periodic `status`). Multi-select up to 4
>   employees via URL `?ids=EMP-1,EMP-2` (replaces single `employeeId` for tiles). Monitor
>   dropdown when client reports 2+ displays. Consent feature id remains `live_view`.
>   **`useUrlQueryState`:** move `router.replace`/`push` **outside** the `setState` updater
>   (calling the router inside an updater updated Next `LinkComponent` while `LiveStreamInner`
>   rendered — React 19 "Cannot update a component while rendering a different component").
>   Verified: warning gone on `/live-stream` selection; `npx tsc` / page load clean.
> - 2026-09-14: **Per-feature T&C content management page.** New `/settings/terms-and-conditions` page with editable per-feature T&C cards (featured terms with `isSystem` flag cannot be deleted, custom terms can be created via a dialog and deleted). Uses `termsContentApi` (list/get/update/create/delete) from `lib/api.ts`. Verified: `npx tsc --noEmit` clean, `next build` passes (`/settings/terms-and-conditions` 8.3 kB).
> - 2026-09-14: **T&C page restructure — tabbed list + dedicated view/create/edit routes.** `/settings/terms-and-conditions` is now a tabbed **list** (Featured Terms / Custom Terms tabs, active tab URL-synced via `?tab=featured|custom`). Clicking a card opens the new **view detail** route `/settings/terms-and-conditions/view/[id]` (read-only content + Edit/Delete actions). Create and edit are dedicated routes: `/settings/terms-and-conditions/create` and `/settings/terms-and-conditions/edit/[id]`. Shared components extracted to `web/src/components/terms/terms-view.tsx` + `terms-editor.tsx` (reuse the existing `RichTextEditor`, `termsContentApi` list/get/create/update/delete — no server change). Edit navigates forward to the created/edited term's view; featured terms remain non-deletable. Verified: `npx tsc --noEmit` clean, `next build` passes (routes: `/settings/terms-and-conditions` 2.37 kB, `/create` 280 B, `/edit/[id]` 306 B, `/view/[id]` 2.96 kB).
> - 2026-11-04: **Web Activity Visits/Duration moved to `GET /app-items/usage` (server aggregate).**
>   Parent-row Visits + Duration were summed from infinite-scroll pages in the
>   browser, so totals grew on every "load more". Page now mirrors App Usage:
>   `appItemsApi.usage` for the domain table; chevron expand uses paginated
>   `appItemsApi.list({ domain })`. Search-engine query regrouping removed from
>   the parent table (GROUP BY stays domain). Verified: `npx tsc --noEmit`.
> - 2026-09-05: **Web Activity (`/employee-journey/web`) groups search-engine queries and shows expandable dropdown arrows.**
>   Searches on Google, Bing, Yahoo, and DuckDuckGo are now parsed from `url` query params (`q`/`p`) and grouped by query text instead of being flattened under the domain. In the default "Visited Sites" view, search rows show a `Search` icon + engine badge (`Google`/`Bing`/`Yahoo`/`DuckDuckGo`) and are expandable with a chevron arrow, just like domain groups. In the active-search "Matching Pages" view, results are grouped by search query with the same expand/collapse behavior instead of a flat table. Non-search URLs continue to group by domain as before. The grouping is client-side only and reuses the existing infinite-scroll data; no server change. Verified: `npx tsc --noEmit` clean, `next build` passes (`/employee-journey/web` 4.8 kB). *(Superseded 2026-11-04 — parent totals are server-side domain aggregates; search-query regrouping no longer on the parent table.)*
> - 2026-09-04: **`/employee-journey/apps` switched to `GET /app-sessions/usage` — chrome multi-tab duration is correct end-to-end.** A chrome window with 3 tabs × 10 min used to render as "30 min" because the page summed per-row `endedAt - startedAt` across every `app_sessions` row with the same `appDisplayName`, and the client occasionally opened a separate `app_sessions` per tab when the a11y reader's per-tab `WindowKey` slipped past the title-only collapse rule. Three layers: (1) **server `GET /app-sessions/usage`** (migration 032) — one row per `(appDisplayName, processName)` with `firstOpenedAt`, `lastClosedAt`, `sessionCount`, `totalDurationSeconds`; (2) **web defense in depth** — the page's `useMemo` now recomputes `totalDurationSeconds = lastClosedAt - firstOpenedAt` regardless of what the server returns, so a future regression on the server side can't reintroduce the sum; (3) **client `ResolveWindowKey`** (separate changelog entry) — exact-URL match + fresh-key recency collapse so a11y-driven per-tab `WindowKey`s fold into the FRESHEST same-PID tracked window. The web switched from a 5×100 raw-row `useQueries` fan-out (silent 500-row truncation bug — sessions past the boundary were silently dropped from the duration total) to a single `appSessionsApi.usage` call. The page now shows "First Opened" and "Last Closed" columns so the open range is visible directly. New `AppUsageRow` + `AppUsageListResponse` types in `lib/api.ts`; the legacy "Sessions / Duration / Last Active / Status" columns are replaced with "Sessions / Duration / First Opened / Last Closed" (the 3-state status badge is no longer per-app because every per-app row in the aggregate is a single (app, appSession-set) tuple — the open/close times tell the same story). Result: chrome 3 tabs × 10 min renders as **Duration: 10m, Sessions: 3**; chrome where tab1 stays open 9:00→9:12 with the others at 9:00→9:10 renders as **Duration: 12m, Sessions: 3**. Verified: `npx tsc --noEmit` clean, `next build` passes (`/employee-journey/apps` 2.89 kB). Cross-service contract kept in sync: server `new_schema_dto.AppUsageRow` + handler `ListAppSessionsUsage` + route `GET /app-sessions/usage` (registered BEFORE `/app-sessions`); client `ResolveWindowKey` collapse rules in `client/ARCHITECTURE.md`.
> - 2026-09-02: **3-state session status badge across journey + logs pages.**
>   New shared `components/sessions/SessionStatusBadge.tsx` renders ACTIVE (green pulse + "Running"), STALE (amber dot + "Stale · last sync X ago"), and CLOSED (muted) from the server-projected `AppSession.status` field. The accompanying `sessionStatus()` and `isSessionLive()` helpers fall back to the legacy `endedAt ? CLOSED : ACTIVE` rule for pre-031 rows so older backends still render correctly. The 3-state duration end is now used everywhere a session duration is computed: CLOSED → `endedAt` (frozen), STALE → `lastSyncAt` (don't pretend it's still growing), ACTIVE → `now`. New helper `formatRelative(iso)` in `lib/format.ts` produces the "3m ago / 2h ago / 1d ago" labels the STALE badge needs. `AppSession` in `lib/api.ts` gained `status`, `lastActivityAt`, `lastSyncAt`. Pages wired: `employee-journey/timeline`, `employee-journey/apps` (parent row "Status" + per-session expanded row), `logs/comprehensive` (3-state-aware `formatDuration` IIFE).
> - 2026-09-01: **Attendance/timesheets display aligned with server shift timezone.**
>   `/attendance` and `/timesheets` format `firstActiveAt`/`lastActiveAt` with
>   `formatDateTimeInZone(iso, record.timezone)` so the table matches late/present math (not
>   browser-local `toLocaleString` alone). `AttendanceRecord` carries optional `timezone` from
>   the API. `/shifts` new-shift dialog defaults timezone to
>   `Intl.DateTimeFormat().resolvedOptions().timeZone` instead of hardcoded `UTC`. Server must
>   set `DEFAULT_SHIFT_TIMEZONE` or per-shift IANA zones — see `server/ARCHITECTURE.md`.
> - 2026-08-22: **Sidebar parent menus stay open when navigating to child pages.**
>   `AppSidebar` now auto-expands any parent section whose `children` array contains the current
>   `pathname`, so the menu stays open when the user opens a child page.
> 2026-08-22: **Configuration pages UI/UX redesign + manual website creation.**
>   `ClassifiedItemsTable` component redesigned with improved filter bar (status toggle chips, clearable filters,
>   active count badge), better table spacing/hover states, per-row classification status badges
>   (Classified/Unclassified/Partial), and richer empty/loading states. `websites/page.tsx` gains an
>   **Add Website** button + modal dialog that posts to new `POST /api/v1/monitoring/websites` (server creates
>   the row with optional type/category and normalizes the domain). Server: new `CreateWebsite` endpoint in
>   `monitoring_handler.go` + `MonitoringService` + `MonitoringRepo`. Verified: `go build`/`go vet` clean,
>   `tsc --noEmit` clean, `next build` passes.
> 2026-08-22: **Dynamic browser badge names on Web Activity — removed hardcoded `BROWSER_NAMES` map.**
>   `employee-journey/web/page.tsx` no longer translates `metadataJson.processName` through a hardcoded
>   10-entry map. The server resolves the friendly browser name dynamically: `ListAppItems` extracts
>   `processName` from `metadata_json` and looks it up in `installed_applications` (`binary_name` /
>   `app_name` match, `is_browser = true`), returning `browserName` in the API response. The web page
>   uses `item.browserName` directly; for embedded webviews (`source: "webview"`) the host app's
>   process name is preserved as before. Verified: `go build`/`go vet` clean, `tsc --noEmit` clean.
> 2026-08-18: **Server-side date/search filters on App Usage + Web Activity + structural browser detection + UX fixes.**
> - **Server-side filters**: `GET /app-sessions` and `GET /app-items` now accept `dateFrom`/`dateTo` (RFC3339 or
>   date-only) — sessions filtered on `started_at`, items on `opened_at`; combined with existing `search`/`platform`.
>   App-items search also matches `url`/`domain` (so searching "youtube" finds the exact page).
> - **Shared `ActivityFilters` component** (`components/journey/ActivityFilters.tsx`): debounced search (300ms),
>   date presets (Today/Yesterday/7d/30d/All Time) defaulting to **Today** with real local-day bounds
>   (`createDefaultFilter()` sends `dateFrom`/`dateTo` on initial load — no stale data leaking), plus custom
>   range via Calendar popover (`react-day-picker` + `date-fns`).
> - **App Usage page** (`employee-journey/apps`): Duration column replaces Foreground/Background — computed from
>   `endedAt - startedAt` (closed) or `now - startedAt` (running). Active Time tile = sum of durations.
>   Apps with >1 session show a chevron → expandable nested table (Opened/Closed/Duration/Details per session).
>   Uses `keepPreviousData` so filter changes never swap the whole content to a spinner.
> - **Web Activity page** (`employee-journey/web`): Pages grouped by domain ("Visited Sites") with expandable
>   groups (Visits/Duration/Last Visited). Sites with >1 visit get chevron → nested page list (Page/URL/Visited/
>   Duration). Browser badge (Chrome/Floorp/etc.) from `metadata_json.processName` shown on every row — site
>   rows show distinct browsers present. Search switches to flat "Matching Pages" view (exact URL visible).
>   `keepPreviousData` for smooth filter transitions.
> - **Filter UX fixes**: `ActivityFilters` always mounted (no unmount on empty/error/loading) — search field
>   never loses focus, filters never disappear on no-match. `next.config.ts` gains `allowedDevOrigins`
>   (suppresses cross-origin dev warning for LAN IP).
> 2026-08-18: **Employee Journey + Device Specs modules** — the old `/users/[id]` detail page was replaced by nine
> pages behind a shared `EmployeePage` shell + `EmployeeSelector` picker (deep-linkable via `?employeeId=`):
> Session Timeline, App Usage, Web Activity (real APIs, infinite scroll) + Screenshots/Location Trail placeholders;
> Device Specs: Hardware Overview, Installed Software, Peripherals, Permissions (all over `GET /employees/:id/detail`).
> The employees table action menu now deep-links to both modules; `AppItem` gained the journey fields; new
> `employee-journey` + `device-specs` permission modules; shared helpers `lib/format.ts`, `hooks/use-employee-detail.ts`,
> components `EmptyState`/`InventoryTable`/`FocusTime`/`DeviceClassIcon`.
> 2026-08-17: Sidebar restructure — Device Specs became a collapsible section with 4 children (Hardware Overview,
> Installed Software, Peripherals, Permissions); Employee Journey stays collapsible with 5 children.
> 2026-08-13: Employees rename — `/users`, `/users/[id]`, `/users/activity` moved to `/employees`, `/employees/[id]`,
> `/employees/activity`; employee `role` removed (see [AGENTS.md](./AGENTS.md) changelog).
> 2026-08-11: `/users/[id]` detail page + `GET /employees/:id/detail` aggregate endpoint; users list action menu became a
> portal-rendered Radix `DropdownMenu`.
> 2026-07-25: Updated comprehensive/logs page to use new app_sessions API (replaced old activityLogsApi). Added appSessionsApi to api.ts with AppSession types. Tabs removed (system log and productive/unproductive tabs not yet implemented for new schema).  
> **Service completion (honest):** ~20%

---

## 1. Responsibility & Scope

**Owns:**
- Admin-facing web dashboard for employee monitoring and productivity analytics
- Authentication UI (login page) and session management via httpOnly cookies
- Employee and department management (CRUD via real API calls)
- Activity log viewing with filtering and pagination (via real API calls)
- Permission/role management UI (client-side only, stored in localStorage)
- Desktop app download dialog (fetches latest GitHub release)
- Mock data seeding for ~25+ feature pages that are not yet API-connected

**Does NOT own:**
- Any direct communication with desktop clients (all data goes through the server)
- Real-time data delivery (no WebSocket, SSE, or polling)
- Server-side rendering of protected data (all pages are `"use client"` — CSR only)
- Background job processing or data aggregation

---

## 2. Tech Stack Detail

| Component | Library | Version |
|---|---|---|
| **Framework** | Next.js (App Router) | ^15.3.4 |
| **React** | react / react-dom | ^18.3.1 |
| **State (Auth)** | @reduxjs/toolkit | ^2.12.0 |
| **Data Fetching** | @tanstack/react-query | ^5.83.0 |
| **UI Components** | shadcn/ui (Radix primitives) | — |
| **Styling** | Tailwind CSS | ^4.3.3 |
| **Animation** | framer-motion | ^12.35.2 |
| **Charts** | recharts | ^2.15.4 |
| **Forms** | react-hook-form + zod | ^7.53.2 / ^3.25.76 |
| **Toast/Sonner** | sonner | ^1.7.4 |
| **Icons** | lucide-react | ^0.462.0 |
| **Carousel** | embla-carousel-react | ^8.6.0 |
| **Drawer** | vaul | ^0.9.9 |
| **OTP Input** | input-otp | ^1.4.2 |
| **Date Picker** | react-day-picker | ^8.10.1 |
| **Date Utils** | date-fns | ^3.6.0 |
| **Theming** | next-themes | ^0.3.0 |
| **Class Merge** | tailwind-merge + clsx | ^2.6.0 / ^2.1.1 |
| **TypeScript** | typescript | ^5.8.3 |

### Build Tooling

- `@tailwindcss/postcss` v4.3.3 (PostCSS plugin for Tailwind v4)
- `@tailwindcss/typography` v0.5.16
- ESLint v9.32.0 with `eslint-config-next`
- `tw-animate-css` v1.4.0 (CSS animation utilities)

---

## 3. Project Structure

```
web/
├── next.config.ts               # Rewrites: /api/* → backend, allowedDevOrigins
├── tsconfig.json                # Path alias @/* → src/*
├── package.json                 # Dependencies, scripts
├── postcss.config.js            # @tailwindcss/postcss
├── components.json              # shadcn/ui config
├── public/
│   ├── robots.txt
│   ├── app-logo.png
│   └── favicon.ico
│
└── src/
    ├── config.ts                # APP_NAME, STORAGE_PREFIX, RELEASES_PAGE_PATH
    ├── globals.css              # Tailwind v4 CSS variables, dark/light themes
    │
    ├── lib/
    │   ├── api.ts               # API client: fetch wrapper with credentials:'include'
    │   │                        #   authApi, employeesApi, departmentsApi, appSessionsApi,
    │   │                        #   appItemsApi, healthApi (AppItem carries the journey fields)
    │   ├── releases.ts          # Public releases helpers (fetchReleases, downloadAssetUrl) — no GitHub token
    │   ├── format.ts            # Shared formatters: formatMb / formatSeconds / formatDuration
    │   │                        #   / formatDateTime / formatDate / formatDateShort
    │   ├── auth.tsx             # AuthContext, AuthProvider, useAuth, fallback users
    │   ├── permissions.tsx      # PermissionsContext, role-based permission matrix
    │   │                        #   (client-side only, stored in localStorage)
    │   ├── utils.ts             # cn() helper (clsx + tailwind-merge)
    │   └── store/
    │       ├── redux.ts         # Redux store with authSlice (checkAuth, loginUser, logoutUser)
    │       └── hooks.ts         # useAppDispatch, useAppSelector typed hooks
    │
    ├── hooks/
    │   ├── use-mobile.tsx       # Responsive detection hook
    │   ├── use-toast.ts         # Toast notification hook (shadcn/ui)
    │   ├── use-video-fps.ts     # Decoded FPS for live-stream <video> tiles
    │   └── use-employee-detail.ts  # Query for GET /employees/:id/detail (enabled once an employee is picked)
    │
    ├── components/
    │   ├── providers.tsx        # Root provider: Redux + QueryClient + Tooltip + Toasts + Auth + Permissions
    │   ├── NavLink.tsx          # Active nav link component
    │   ├── EmployeeSelector.tsx # Searchable employee picker (shared by Journey/Device-Specs pages)
    │   ├── live-stream/         # Shared live-stream UI (main page + theater)
    │   │   ├── LiveStreamWatchTile.tsx · LiveStreamEmployeeSidebar.tsx · LiveStreamPreviewGrid.tsx
    │   │   └── live-stream-selection.ts  # MAX_TILES + parse/toggle ids
    │   ├── dlp/
    │   │   └── DlpAlertDetailDialog.tsx  # Alert detail + Save-only triage + history timeline
    │   ├── employees/           # Shared employee-page building blocks
    │   │   ├── EmployeePage.tsx #   Page shell: header + picker + loading/error/no-selection
    │   │   ├── InventoryTable.tsx  EmptyState.tsx  DeviceClassIcon.tsx
    │   ├── journey/
    │   │   ├── ActivityFilters.tsx  Shared search + date presets + custom range filter bar
    │   │   ├── FocusTime.tsx    #   Foreground/background stacked bar
    │   ├── sessions/            # Session status badge (3-state app_sessions lifecycle)
    │   │   └── SessionStatusBadge.tsx  # ACTIVE / STALE / CLOSED pill — server-projected status with legacy endedAt fallback
    │   ├── ui/                  # ~45 shadcn/ui component files (button, card, dialog, table, chart, etc.)
    │   │   ├── button.tsx       #  (all are standard shadcn/ui, no customization)
    │   │   ├── card.tsx
    │   │   ├── chart.tsx        # Recharts wrapper
    │   │   ├── sidebar.tsx      # shadcn sidebar (not used — custom sidebar in layout/)
    │   │   ├── StatsCard.tsx    # Custom: animated stats card with Framer Motion
    │   │   └── ...              # 40+ other shadcn components
    │   └── layout/
    │       ├── AppSidebar.tsx   # Custom sidebar: collapsible, accordion menus, permission-filtered
    │       ├── AppLayout.tsx    # Layout wrapper: sidebar + topbar + main content
    │       ├── ProtectedRoute.tsx  # Auth guard: redirects to /login if unauthenticated
    │       └── TopBar.tsx       # Header bar: page title, search, user avatar, logout
    │
    └── app/                     # Next.js App Router
        ├── layout.tsx           # Root layout: Providers wrapper, metadata
        ├── page.tsx             # Home page: redirects to /dashboard or /login
        └── not-found.tsx        # 404 page
        │
        ├── login/               # Login page (unauthenticated)
        │   └── page.tsx         # Animated hero + email/password form
        ├── forgot-password/     # Placeholder page
        ├── reset-password/      # Placeholder page
        ├── mfa/                 # Placeholder page
        │
        ├── (live-popout)/       # Full-bleed live-stream theater (no AppSidebar/TopBar)
        │   ├── layout.tsx       # ProtectedRoute + RouteGuard only
        │   └── live-stream/theater/  # /live-stream/theater?ids= — watch wall
        │
        └── (app)/               # Authenticated route group (AppSidebar + TopBar)
            ├── layout.tsx       # Wraps children with ProtectedRoute + AppLayout
            │
            ├── dashboard/       # Dashboard: stats cards, best performer, chart (mock data)
            ├── employees/       # Employees list: CRUD via real API, generate secret dialog;
            │                    #   action menu deep-links to Journey/Device Specs
            │                    #   (/employees/[id] detail page removed 2026-08-18)
            ├── departments/     # Department CRUD via real API
            │
            ├── employee-journey/# Per-employee journey, shared EmployeePage shell + picker
            │   ├── timeline/    #   Session timeline — real API, infinite scroll
            │   ├── apps/        #   App usage — real API, duration aggregation, expandable session groups
            │   ├── web/         #   Web activity — real API, domain-grouped, browser badges, search flat view
            │   ├── screenshots/ #   Placeholder — client collects none
            │   └── location/    #   Coming Soon shell (live UI: LocationTrailLive.tsx; see locationUi.ts)
            ├── device-specs/    # Per-employee machine picture (detail API)
            │   ├── page.tsx     #   Hardware overview
            │   ├── software/    #   Installed software (apps/packages tabs + search)
            │   ├── peripherals/ #   Peripherals (plugged/unplugged cards)
            │   └── permissions/ #   Permission checks
            │
            ├── logs/
            │   ├── comprehensive/  # Activity logs with filtering (real API)
            │   ├── insights/       # Log insights digest (live GET /logs/insights)
            │   └── graphical/      # Graphical logs (live GET /logs/graphical)
            │
            ├── charts/
            │   ├── productivity/   # Productivity chart (mock data)
            │   └── activity/       # Activity chart (mock data)
            │
            ├── apps/               # Apps & Websites (mock data)
            ├── screenshots/        # Screenshots (mock data)
            ├── live-stream/        # WebRTC SFU screen preview (Windows; multi-tile ?ids=; FPS; Open theater)
            ├── emails/             # Emails & Alerts (mock data)
            ├── kpis/               # KPIs & KRAs (mock data)
            ├── roles/              # Roles (mock data)
            ├── shifts/             # Shift management (live API)
            ├── timesheets/         # Per-employee attendance range (live API)
            ├── attendance/         # Daily attendance log (live API)
            ├── gps-location/       # Coming Soon shell (live UI: GpsLocationLive.tsx; see locationUi.ts)
            ├── hours-insights/     # Hours insights (mock data)
            ├── productivity-scoring/  # Score card (mock data)
            ├── goals/              # Goals & OKRs (mock data)
            ├── reports/            # Reports & Analytics (mock data)
            ├── audit-log/          # Audit log (mock data)
            ├── executive-dashboard/  # Executive dashboard (mock data)
            ├── dlp-alerts/         # DLP Alerts (live API + detail/history dialog)
            ├── dlp-rules/          # DLP Rules (live API + Publish)
            ├── shadow-it/          # Shadow IT catalog (live API)
            ├── ai-summary/         # AI Summary (mock data)
            ├── onboarding/         # Onboarding (mock data)
            ├── projects/           # Projects (mock data)
            │
            └── settings/
                ├── page.tsx             # General settings (mock data)
                ├── permissions/         # Permission management (client-side only)
                ├── tracking/            # Tracking settings (mock data)
                ├── user-management/     # User management (mock data)
                ├── notifications/       # Notification config (mock data)
                ├── billing/             # Billing (placeholder)
                ├── compliance/          # GDPR & Compliance (placeholder)
                └── security/            # Security settings (placeholder)
```

---

## 4. Data Flow from Server

### Auth Flow

```
┌──────────┐     GET /api/v1/auth/check        ┌──────────┐
│  Web App  │──────────────────────────────────▶│  Server   │
│  (on      │  (cookie auto-sent)               │          │
│   mount)  │◀──────────────────────────────────│          │
└──────────┘     {authenticated: true, user}    └──────────┘
       │
       │ authenticated → render ProtectedRoute children
       │ not authenticated → redirect to /login?redirect=<path>
       ▼
┌──────────┐     POST /api/v1/auth/login        ┌──────────┐
│  Login    │──────────────────────────────────▶│  Server   │
│  Page     │  {email, password}                │          │
│           │◀──────────────────────────────────│          │
└──────────┘     Sets httpOnly cookie           └──────────┘
       │
       │ success → router.replace('/dashboard')
       │ fail → show error
```

### Data Fetching Strategy

| Strategy | Usage |
|---|---|
| **CSR (Client-Side Rendering)** | All pages use `"use client"` — no SSR, no ISR |
| **TanStack Query** | Employees, Departments, Logs, Dashboard tiles, User Management, Roles, Employee Journey, Device Specs pages (real API data) |
| **Infinite scroll** | `useInfiniteQuery` + IntersectionObserver sentinel — **the only allowed list pagination** on every list page (Session Timeline, Web Activity, Employees, User Management, …), server-side pagination via `page`/`perPage` (see *Web Infinite-Scroll Rule* below) |
| **Honest empty states** | Pages whose backend doesn't exist yet render a header + `EmptyState` explaining the gap (2026-08-25 removed all localStorage mock data — none remains) |
| **Next.js Rewrites** | `/api/:path*` → `http://localhost:8080/api/:path*` (or `NEXT_PUBLIC_API_URL`) |
| **Redux** | Auth state only (user, loading, auth status) |

### Real-Time Updates

| Surface | Mechanism |
|---|---|
| **`/live-stream`** | WebRTC SFU via `useLiveStreamSocket` (watch ticket WS + RTCPeerConnection → `<video>`). WS base: ticket `watchBaseUrl` if present, else `NEXT_PUBLIC_WS_URL` (Next rewrites do not proxy WS). Multi-tile `?ids=` (max 4). Per-tile FPS. **Open theater** → `/live-stream/theater`. |
| **`/live-stream/theater`** | Full-bleed watch wall (`(live-popout)`). Collapsible sidebar; navbar cols 1–4; scrollable aspect tiles (max 16); URL `?ids=&cols=&sidebar=&online=`. Same employee poll as console. |
| **Everything else** | No polling / SSE. Pages show data at fetch time (TanStack Query). |

### URL-Synced Filters (mandatory)

See AGENTS.md §6. **Never call `router.push`/`replace` inside a React `setState` updater** — it updates Next `LinkComponent` while another component is rendering (React 19). `useUrlQueryState` navigates outside the updater.

### Web Infinite-Scroll Rule (mandatory)

**Every list/table page MUST paginate with server-side infinite scrolling — Next/Previous buttons are forbidden.**

- Use `useInfiniteQuery` (TanStack Query v5) with `initialPageParam: 1` and
  `getNextPageParam: (last) => (last.page < last.totalPages ? last.page + 1 : undefined)`.
- Flatten pages with `data?.pages.flatMap(p => p.data)`.
- Trigger the next fetch with an **IntersectionObserver sentinel** (`rootMargin: '300px'`, gated on
  `hasNextPage && !isFetchingNextPage`) rendered below the list.
- Show an inline "Loading more…" row while `isFetchingNextPage`; show a "Showing all N" footer when
  `hasNextPage` is false.
- Filters/search change the query key (a fresh infinite query starts at page 1) — never a `page` state.
- Reference implementation: `src/app/(app)/employees/page.tsx` and the Session Timeline journey page.

### Server-Projected Flags Rule (mandatory since 2026-08-28)

**A boolean flag that depends on a cross-table relationship (e.g. "does this employee have a login
account?") MUST be projected by the server in the same query that returns the row — never built
client-side from a separate fetch.**

- Wrong: a web `useQuery` that pulls up to N users and builds a `Set<string>` of employeeIds to
  answer a per-row question. It is O(N) on the client, O(perPage × flag-query-cost) on the server,
  and silently wrong when `N < total users` (rows are misclassified past the page boundary).
- Right: add `EXISTS(SELECT 1 FROM other_table ot WHERE ot.fk = e.id AND ot.deleted_at IS NULL)`
  to the list/return SELECTs. One indexed probe per row, O(1) per page, no extra round-trips.
  The web consumes the field directly from the existing `Employee` (or analogous) DTO.

The first column-level use of this rule is `Employee.hasUserLogin` (server/ARCHITECTURE.md §4).
The web `Employee` type carries the same field as `hasUserLogin: boolean` (camelCase JSON key).
Pattern reference: `web/src/app/(app)/employees/page.tsx` (dropdown visibility + reverse-sync guard
in the `updateMutation`).

---

## 5. Pages/Routes Inventory & Data Dependencies

> The old `/employees/[id]` single-page detail view was removed 2026-08-18 — it is replaced by the
> Employee Journey + Device Specs modules below (both deep-linkable via `?employeeId=`).

| Route | Page | Data Source | Real API? |
|---|---|---|---|
| `/login` | Login | Server (auth) | ✅ |
| `/releases` | Public desktop releases (no login) | Server (`GET /releases` + `/downloads/:assetId`; sanitized Markdown notes) | ✅ |
| `/unauthorized` | Access denied (RouteGuard target) | — | ✅ |
| `/dashboard` | Dashboard | Server (`GET /dashboard/summary` + capped `GET /live-stream/employees?onlineOnly&limit` + `/departments`) | ✅ |
| `/employees` | Employee list (hides the "Login Credential" dropdown item when `emp.hasUserLogin` is true; `updateMutation` propagates name/email to the linked user via `usersApi.update` when the server's `UPDATE…RETURNING` reports `hasUserLogin`) | Server | ✅ |
| `/employee-journey/timeline` | Session timeline | Server (`GET /app-sessions`, infinite scroll) | ✅ |
| `/employee-journey/apps` | App usage | Server (`GET /app-sessions/usage` per-app aggregate + `GET /app-sessions/usage/sessions` per-app paginated list, fired on chevron expand since 2026-09-04) | ✅ |
| `/employee-journey/web` | Web activity | Server (`GET /app-items/usage` + expand via `GET /app-items?domain=`) | ✅ |
| `/employee-journey/screenshots` | Screenshots | Placeholder — client collects none | ❌ |
| `/employee-journey/location` | Location trail | Coming Soon (`LOCATION_UI_ENABLED=false`; live code in `LocationTrailLive.tsx`) | ⏸ |
| `/device-specs` | Hardware overview | Server (`GET /employees/:id/detail`) | ✅ |
| `/device-specs/software` | Installed software | Server (detail payload) | ✅ |
| `/device-specs/peripherals` | Peripherals | Server (detail payload) | ✅ |
| `/device-specs/permissions` | Permissions | Server (detail payload) | ✅ |
| `/departments` | Departments | Server | ✅ |
| `/roles` | Roles + per-submodule permission toggles | Server (`GET /modules`, `/roles` CRUD) | ✅ |
| `/logs/comprehensive` | Activity logs | Server | ✅ |
| `/logs/insights` | Log insights digest (metrics, productivity mix, top apps/domains, idle outliers, rule-based findings) | Server (`GET /logs/insights` — `from`/`to` + optional `departmentId`/`employeeId`) | ✅ |
| `/logs/graphical` | Graphical logs (activity + productivity time series, top apps/domains) | Server (`GET /logs/graphical` — `from`/`to` + optional `departmentId`/`employeeId`; hour/day buckets) | ✅ |
| `/charts/productivity` | Productivity chart | Hardcoded Recharts demo data | ❌ |
| `/charts/activity` | Activity chart | Hardcoded Recharts demo data | ❌ |
| `/configuration/apps` | Applications classification | Server (`GET/PATCH /monitoring/apps`) | ✅ |
| `/configuration/websites` | Websites classification | Server (`GET/POST/PATCH /monitoring/websites`) | ✅ |
| `/configuration/categories` | Categories & Types CRUD | Server (`/monitoring/types`, `/monitoring/categories`) | ✅ |
| `/screenshots` | Screenshots | Honest empty state (no endpoint) | ❌ |
| `/live-stream` | Live stream | WebRTC SFU VP8 preview (Windows; consent `live_view`; FPS; multi-tile `?ids=`; optional ticket `watchBaseUrl`; Open theater) | ✅ |
| `/live-stream/theater` | Live stream theater | Full-bleed popout (no AppSidebar); employee sidebar + same tiles; `(live-popout)` layout | ✅ |
| `/kpis` | KPIs & KRAs | Hardcoded demo data (scaffolding) | ❌ |
| `/shifts` | Shift management | Server (`/shifts` CRUD; `TimezoneCombobox` IANA picker; defaults to browser zone) | ✅ |
| `/timesheets` | Timesheets | Server (`/attendance/range`; empty from/to = last 366 days; Events modal via `GET /attendance/events`) | ✅ |
| `/attendance` | Attendance log | Server (`/attendance/range` per employee/day; status + late minutes from server) | ✅ |
| `/gps-location` | GPS & Location | Coming Soon (`LOCATION_UI_ENABLED=false`; live code in `GpsLocationLive.tsx`) | ⏸ |
| `/hours-insights` | Hours insights | Honest empty state (no endpoint) | ❌ |
| `/productivity-scoring` | Score card | Hardcoded demo data (scaffolding) | ❌ |
| `/goals` | Goals & OKRs | Hardcoded demo data (scaffolding) | ❌ |
| `/reports` | Reports | Hardcoded demo data (scaffolding) | ❌ |
| `/audit-log` | Audit log | Hardcoded demo data (scaffolding) | ❌ |
| `/executive-dashboard` | Exec dashboard | Hardcoded demo data (scaffolding) | ❌ |
| `/dlp-alerts` | DLP Alerts | Server (`GET/PATCH /dlp/alerts`, `GET /dlp/alerts/:id/history`; infinite scroll + URL filters; detail dialog Save-only triage) | ✅ |
| `/dlp-rules` | DLP Rules | `/dlp/rules` CRUD + get/history + publish; edit/detail dialogs; Active Switch auto-publish | ✅ |
| `/web-blocker` | Web Blocker | `/web-blocker/rules` CRUD + expand/history; Switch status + auto-publish; schedule + `TimezoneCombobox` (deny-list only; default-deny lives under Allowlists) | ✅ |
| `/allowlists/web` | Web Allow Lists | `GET|PUT /dlp/lockdown` (lockdown + allowHosts) + Publish; platformAllow read-only | ✅ |
| `/allowlists/apps` | App Allow List | Same lockdown draft API (`allowApps`); Publish snapshots into `allow.apps` (enforce = P3) | ✅ |
| `/shadow-it` | Shadow IT | Server (`GET/PATCH /shadow-it/apps`; Approve/Block/Flag) | ✅ |
| `/emails` | Emails & Alerts | Hardcoded demo data (scaffolding) | ❌ |
| `/projects` | Projects | Hardcoded demo data (scaffolding) | ❌ |
| `/ai-summary` | AI Summary | Honest empty state (no endpoint) | ❌ |
| `/onboarding` | Onboarding | Hardcoded demo data (scaffolding) | ❌ |
| `/settings` | General settings | Static hub page | ❌ |
| `/settings/tracking` | Tracking settings | In-memory session state only (explicit non-persistence notice) | ❌ |
| `/settings/user-management` | User management (CRUD + role assignment, infinite scroll; create-from-employee form locks `name`/`email`/`employeeId` and excludes the `company_admin` role; new per-row **Edit** button opens the same dialog in edit mode via `?edit=1&userId=…` — **hidden for the system `company_admin` user**; password + confirm-password with eye toggles; on save, if the edited user has an `employeeId` the client also calls `employeesApi.update` to sync name/email to the attached employee) | Server (`/users`, `/roles`, `/employees`) | ✅ |
| `/settings/profile` | Self-service profile — **3 read-only view blocks** (Account tiles: user id, employee link, role + system badge, department, shift; Modules you can access: per-module `grantedCount/submoduleCount` from the RBAC catalog joined with the user's permission keys, with a system-admin "all" override; Update details form: name/email/password with confirm-password + eye toggles, reverse-sync to attached employee when `me.employeeId` is set) | Server (`GET /api/v1/auth/profile` — single-shot `UserResponse` + `RoleResponse?` + `ProfilePermissions` + `EmployeeResponse?`; `PUT /users/:me`; `GET /employees`; `PUT /employees/:id`) | ✅ |
| `/settings/notifications` | Notifications | Hardcoded demo data (scaffolding) | ❌ |
| `/settings/billing` | Billing | Placeholder | ❌ |
| `/settings/compliance` | Compliance | Placeholder | ❌ |
| `/settings/security` | Security | Placeholder | ❌ |


> The former `src/lib/store.ts` localStorage mock-data factory was **deleted 2026-08-25**. Pages that
> had no real backend were converted to honest empty states rather than keeping fake data; pages
> marked "Hardcoded demo data (scaffolding)" above keep their in-file demo constants and await endpoints.
> `app/page.tsx` purges orphaned `alpha_ai_tracker_*` keys from visitors' localStorage on first hit.
> The legacy client-side permission matrix (`/settings/permissions` page + its localStorage store)
> was deleted in the same pass — permissions are now server-driven only (§6).

### Location UI gate (Phase 3 GPS — web surface paused)

Location **client sync** and **server APIs** (`GET /location-samples`, geofence CRUD, ingest evaluation) remain in the product. The **admin dashboard pages are intentionally hidden** behind a Coming Soon shell:

| File | Role |
|------|------|
| `web/src/lib/locationUi.ts` | `LOCATION_UI_ENABLED` — set `true` to show live UI |
| `web/src/components/location/LocationComingSoon.tsx` | Shared Coming Soon card |
| `web/src/app/(app)/gps-location/page.tsx` | Route shell → Coming Soon or `GpsLocationLive.tsx` |
| `web/src/app/(app)/gps-location/GpsLocationLive.tsx` | Full fleet log + geofence CRUD (preserved) |
| `web/src/app/(app)/employee-journey/location/page.tsx` | Route shell → Coming Soon or `LocationTrailLive.tsx` |
| `web/src/app/(app)/employee-journey/location/LocationTrailLive.tsx` | Per-employee trail (preserved) |

Client default: `ALPHA_LOCATION_ENABLED=false` in `.env.example`. No server or client collector code is removed when the web UI is gated.

---

## 6. Auth / Session Handling

### Auth Provider (`auth.tsx`)

- Wraps the entire app (inside `Providers.tsx`)
- On mount: dispatches `checkAuth()` thunk → calls `GET /api/v1/auth/check`; if it reports
  `authenticated:false` (which an EXPIRED access token also produces — `/auth/check` is optional-auth
  and never returns 401), the thunk first tries `POST /api/v1/auth/refresh` and re-checks once before
  declaring the session dead
- Redux `auth` slice tracks: `user`, `isLoading`, `isAuthenticated`, `error`
- `ProtectedRoute` component reads `isAuthenticated` and either renders children or redirects to `/login?redirect=<path>`
- `Login` page calls `loginUser()` thunk → `POST /api/v1/auth/login` → server sets BOTH cookies (`auth_token` access ~15 min + `refresh_token` 30 days, both httpOnly)
- `Logout` calls `logoutUser()` → `POST /api/v1/auth/logout` → server REVOKES the refresh row + clears both cookies; Redux resets

### Access-Token Refresh (`api.ts` request wrapper)

- Every API call goes through `request()`; on a **401** (except `/auth/login` and
  `/auth/refresh` themselves) it runs ONE single-flight `performRefresh()`
  (module-level promise so concurrent 401s share a rotation) and replays the original request once
- Refresh success → transparent retry, user never notices the 15-minute expiry
- Refresh failure → session is unrecoverable → `window.location.replace('/login')`
  forces the full reload that also resets Redux state (the "force to login" rule)
- A `_retried` flag guarantees at most one refresh-replay cycle per call (no loops)

### Fallback Users

`auth.tsx` still exports `FALLBACK_USERS` (8 sample identities) on the context for legacy consumers,
but nothing in the permission system uses them anymore.

### Permission System (`permissions.tsx` — SERVER-driven since 2026-08-25)

- The server embeds the user's granted **submodule keys** (RBAC `role_submodule_permissions`,
  migration 025) into every auth response as `user.permissions: string[]`
- `PermissionsProvider` turns that array into an allow-list `Set`; `canAccess(role, module)` returns
  `'full'` for `company_admin`, `'full'` when the set is absent/null (**fail-open** — sessions from
  before the RBAC rollout keep working), otherwise `'full' | 'none'` by set membership
- **Entirely removed**: the old localStorage matrix (`ALL_MODULES` / `DEFAULT_PERMISSIONS` /
  `alpha_ai_tracker_dynamic_permissions`) and the `/settings/permissions` editor page — the server's
  `/roles` CRUD page (Settings ▸ Roles) is now the only place permissions are edited
- `AppSidebar` filters nav entries through `canAccess`; `RouteGuard` resolves the current pathname to
  a sidebar module (`findModuleForPath`) and redirects denied routes to `/unauthorized`
- ⚠️ Enforcement is CLIENT-side only — API middleware performs no role checks yet

---

## 7. Production-Readiness Gaps

| Gap | Severity | Details |
|---|---|---|
| **Scaffolding pages dominate** | 🟠 Medium | ~35 of ~50 pages have no backend endpoint; most render hardcoded demo constants or honest empty states. Real-API: login, dashboard tiles, employees, user-management, roles, departments, logs/comprehensive, configuration/*, Employee Journey + Device Specs. |
| **No tests** | 🔴 High | 0 test files. No unit, integration, or e2e tests. |
| **Client-side only permission enforcement** | 🟠 Medium | RBAC grants are checked only in the browser (sidebar + RouteGuard). Any crafted request with a valid cookie reaches every protected endpoint. Server middleware has no role checks. |
| **No error boundaries** | 🟠 Medium | Uncaught React errors crash the entire page. No per-page error boundaries. |
| **No SEO** | 🟢 Low | All pages are `"use client"` — no SSR, no metadata beyond the root layout. Fine for an admin dashboard. |
| **No accessibility** | 🟠 Medium | Many interactive elements lack `aria-label`, `role`, or keyboard navigation. The sidebar menus are `<button>` elements with no aria-expanded. |
| **No API error boundary** | 🟢 Low | If the server is down, the API client throws `ApiError` but only login and users pages handle it gracefully. Other pages would show blank/error states. |
| **No build-time validation** | 🟢 Low | `strict: false` in tsconfig.json. TypeScript errors that would be caught in strict mode are silently ignored. |
| **Desktop auto-update needs new installer** | 🟢 Low | Client `AppUpdateService` now uses the same releases bridge; already-installed builds still hit GitHub until users install a build that includes the bridge updater. |
| **Large bundle** | 🟢 Low | ~45 Radix UI components, Framer Motion, Recharts, Redux, TanStack Query — likely a heavy initial JS payload. No code splitting per page. |

---

## 8. Immediate Next Steps

1. ~~Connect real API to dashboard~~ ✅ done 2026-08-25 — dashboard tiles are live-API; charts still demo data pending productivity endpoints
2. **Add tests** — start with the `api.ts` client and auth flows using MSW or similar
3. **Add error boundaries** — wrap each page section in `React.ErrorBoundary`
4. **Server-side permission enforcement** — validate `user.permissions`/role per route in Echo middleware; client checks are UX only
5. **Enable `strict: true` in tsconfig** — fix the TypeScript errors it reveals
6. **Wire the empty-state pages to real endpoints** as they ship (screenshots, insights, graphical logs, activity status)
7. **Add code splitting** — dynamic imports for chart libraries, heavy pages
