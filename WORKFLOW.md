# Workflow

How work actually moves through this repo — the loops you run daily, and the gates a change has to clear before it counts as done.

Rules live in [AGENTS.md](./AGENTS.md) §6; this file is the *procedure* for obeying them. File locations: [FILE_HIERARCHY.md](./FILE_HIERARCHY.md).

*Last audited: 2026-11-10 — `ALPHA_DLP_DELETE_BLOCK` local switch (NTFS Deny Delete under lockdown) joins the other `ALPHA_DLP_*` blocker keys in `.env` → `generate-windows-vars.sh` → Inno `service.env` / `install-service.ps1` / `EnvBootstrap`. Prior 2026-11-07: client auto-update from the server `setup/` folder (GitHub release bridge removed; 60s check). Prior 2026-10-31: Device Configuration + tracking-events client queue/UUID fix. Prior: Security & DLP P0+P1 (`ALPHA_DLP_*`, Windows service + Inno gate). Commands verified against `client/publish/*.sh`, `client/DlpService/install-service.ps1`, `server/Makefile`, `web/package.json`.*

---

## 0. The one rule that shapes every workflow below

> **`dotnet run` is not a release test.** It compiles from the source tree, so it can never catch a packaging gap. The installed app runs from a root-owned directory with only what the `publish/*` scripts bundled. **A client change is not done until it works from an installed build.**

Everything in §1 is the fast loop. Everything in §5 is the loop that decides whether the change is real.

### Live stream / presence config (Installer-Parity)

When changing stream or presence knobs, update **both** `client/.env` (dev) **and** bake `config.enc` before shipping:

| Side | Keys |
|---|---|
| Client | `ALPHA_STREAM_ENABLED`, `ALPHA_STREAM_FPS`, `ALPHA_STREAM_MAX_WIDTH`, `ALPHA_STREAM_MAX_BITRATE_KBPS`, `ALPHA_STREAM_KEYFRAME_INTERVAL_SEC`, `ALPHA_WS_ENABLED`, `ALPHA_WS_PING_SEC`, `ALPHA_WS_RECONNECT_BASE_SEC` |
| Server | `LIVE_STREAM_*`, `WEBRTC_MAX_BITRATE_KBPS`, `WEBRTC_STUN_URLS`, optional TURN, `PRESENCE_WS_*` |

Media encode bitrate is decided on the **client** (`ScreenVp8Encoder`). Server `WEBRTC_MAX_BITRATE_KBPS` is ops/advertise only.

**Web theater:** `/live-stream` → **Open theater** opens `/live-stream/theater?ids=` in a new tab (full-bleed; no app chrome). Same max-4 tile selection. Browsers always show some address bar chrome.

### Security & DLP config (Installer-Parity — Windows)

DLP enforcement is a **separate LocalSystem service** (`client/DlpService`). Shipping a DLP change means:

1. Add/change knobs in `client/.env` **and** `.env.example`, then re-bake `config.enc` (user helper).
2. `install-service.ps1` / Inno writes `%ProgramData%\<APP_MUTEX>\dlp\service.env` with `ALPHA_DLP_*` **and** tracking-control knobs for the service process — keep that list in sync with bake `.env` (upsert merges defaults without wiping operator overrides).
3. Service / pipe / ProgramData folder names come from `APP_IDENTIFIERS` (`WINDOWS_SERVICE_NAME`, `WINDOWS_DLP_PIPE_NAME`, `APP_MUTEX`) — never hardcode them.
4. Lab without full Inno: elevated `client/DlpService/install-service.ps1` (see `client/build.md`).
5. Server: `DLP_REMOVAL_CODE_TTL_MIN` (default 60) in `server/.env`. Fixed/Roaming needs migration **056** on the API DB.
6. Tracking-control lab: [TESTING.md](./TESTING.md) §G (Fixed lockdown + Roaming Start/Stop). Ship client **≥ 2.3.12** for Installer-Parity on the modal/tray UX.

| Side | Keys |
|---|---|
| Client / service.env | `ALPHA_DLP_*` as before — local blocker switches `USB_BLOCK` / `WEB_BLOCK` / `NETWORK_LOCKDOWN` / `APP_BLOCK` / **`DELETE_BLOCK`** (default `true`); optional `ALPHA_DLP_DATA_DIR` / `ALPHA_DLP_PIPE_NAME` |
| Server | `DLP_REMOVAL_CODE_TTL_MIN` |

**Publish → pull:** editing `/dlp-rules` alone does nothing on the PC until **Publish** (`POST /dlp/rules/publish`); the service polls `GET /dlp/policy`. Lab playbook: [TESTING.md](./TESTING.md) §DLP.

**Local account wipe:** `alpha-ai-tracker --remove-account` / `client.exe --remove-account` (drain if online → wipe → relaunch). Web **Remove Current Device** shows that command. Fixed mode does not block wipe.

**Protected uninstall (product):** web **Issue Removal Code** →  
Windows: `client.exe --protected-uninstall --code …` → `teardown.ok` → Apps & features.  
Linux: `alpha-ai-tracker --protected-uninstall --code …` → `teardown.ok` → `apt`/`dpkg` remove.  
Without `teardown.ok`, Inno / `.deb` `prerm` refuses.

---

## 1. Daily dev loop

Three services, three terminals. Start them in this order — the client needs the server, the web console needs the server.

### Infrastructure

```bash
# Postgres + Redis must be up first (see AGENTS.md §8 for connection details)
```

### Server

```bash
cd server
make setup          # first time only: .env from example, deps, build
make run            # build + run
make dev            # hot reload (needs: go install github.com/air-verse/air@latest)
make test           # go test ./... -v -count=1
```

Schema changes are **append-only**: add `017_<name>.sql` to `server/migrations/`, never edit an applied file.

Set `DEFAULT_SHIFT_TIMEZONE` in `server/.env` to your company's IANA zone (e.g. `Asia/Karachi`) so
legacy shifts on migration 028's `UTC` default are corrected at boot and attendance late/present
matches local wall-clock. Per-shift overrides live on `/shifts`.

**3-state session lifecycle** (migration 031 + `jobs/session_lifecycle_sweep.go`, since 2026-09-02).
The sweep is **always on** — a 1-minute goroutine transitions `app_sessions` rows through
`ACTIVE → STALE → CLOSED` based on `last_sync_at`. Defaults are conservative for desktop
installations: `SESSION_STALE_AFTER_MINUTES=10` (no heartbeat → "tracker is offline but the PC
might still be running"), `SESSION_CLOSE_AFTER_HOURS=24` (no heartbeat for a day → truly gone).
Tune for your deployment. The server will report the chosen values at boot
(`[session-lifecycle] starting (stale_after=…, close_after=…, interval=1m)`). A live client that
re-syncs any STALE/CLOSED row with `ended_at=NULL` flips it back to ACTIVE — the sweep will never
destroy information that may still exist on the client.

**Per-app usage aggregate** (migration 032 + `GET /app-sessions/usage`, since 2026-09-04). The
"App Usage" page reads this endpoint instead of the raw `GET /app-sessions` list — one row per
`(appDisplayName, processName)` with `firstOpenedAt` / `lastClosedAt` / `totalDurationSeconds`. The
page renders `lastClosed - firstOpened` (not `Σ durations`) so multi-tab windows never inflate the
per-app total. Route is registered BEFORE `GET /app-sessions` for safe future `:id` routing.

**Per-app session list** (`GET /app-sessions/usage/sessions`, since 2026-09-04). Powers the
chevron expand under each app row on `/employee-journey/apps` — paginated server-side (20/page)
with the same `(appDisplayName, processName)` group key as the aggregate, so the inner count
always matches the parent `sessionCount`. Both keys required (400 if both empty). Live test
playbook: `TESTING.md` §3-7.

### Web

```bash
cd web
npm install
npm run dev         # next dev
npm run build       # production build — run before claiming a page works
npm run lint
```

> **Web list pages MUST use server-side infinite scroll** (`useInfiniteQuery` + IntersectionObserver
> sentinel). Next/Previous buttons are forbidden — see AGENTS.md §6 *Web Infinite-Scroll Rule* and
> `web/ARCHITECTURE.md` §4. Reference: `web/src/app/(app)/employees/page.tsx`.

> **GPS / Location pages** (`/gps-location`, `/employee-journey/location`) show a Coming Soon shell
> while `web/src/lib/locationUi.ts` has `LOCATION_UI_ENABLED=false`. Live UI lives in
> `GpsLocationLive.tsx` / `LocationTrailLive.tsx` — set the flag to `true` to re-enable. Client
> collectors and server sync APIs are unchanged.

### Client

```bash
cd client
dotnet build        # compile check
dotnet run          # fast iteration ONLY — see §0
```

`dotnet run` is right for: XAML layout, binding wiring, VM logic, collector behaviour on your own machine. It is wrong for: anything involving a new file on disk, a config value, a path, or an installer.

---

## 2. Re-brand or bump the version

Two files. Nothing else.

```bash
cd client
$EDITOR APP_IDENTIFIERS     # DISPLAY_NAME, PUBLISHER, PACKAGE_NAME, BUNDLE_ID, APP_URL, …
$EDITOR VERSION             # e.g. 0.2.0 → 0.3.0

dotnet clean                # REQUIRED — VERSION is read at project-evaluation time and cached
bash publish/build-installer.sh -b linux
```

**Why `dotnet clean`:** `client.csproj` reads `VERSION` when the project graph is *evaluated*, not in a `BeforeBuild` target. That is deliberate — a target-based read leaves IDE design-time builds silently stamping `1.0.0`. The cost is that an incremental build reuses the cached graph, so an edited `VERSION` needs a clean.

**What updates automatically:** window title, splash, rail wordmark and initials, footers, tray tooltip and menu, log banners (all via `Core/AppInfo`), plus `.deb` package name, `.dmg` bundle id, Windows installer strings and every artifact filename (all via the build scripts sourcing the same two files).

**What must NOT change:** `Core/EncryptedConfigService.cs` — `TransportKeySeed` and `MachineKeyPrefix` look like brand strings but are cryptographic KDF seeds. Templatizing them makes every `config.enc` already deployed in the field undecryptable.

**Acceptance:** install the artifact and confirm the six surfaces above changed with **zero other source edits**. If you had to touch a third file, the single-source guarantee has regressed — fix that, not the symptom.

---

## 3. Add a GUI page

Full detail in [client/UI_ARCHITECTURE.md](./client/UI_ARCHITECTURE.md) §7. The shape:

1. `ViewModels/YourPageViewModel.cs` — `ViewModelBase`, `[ObservableProperty]` state, one `[RelayCommand] RefreshAsync(CancellationToken)` guarded by `IsBusy`.
2. `Program.cs` — `AddTransient<YourPageViewModel>()`; take it in `MainViewModel`'s constructor, expose as `{ get; }`.
3. `MainViewModel` — add to `AppPage`, add `IsYourPage`, list it in the `[NotifyPropertyChangedFor]` block on `_activePage`, and add its arm to `NavigateAsync` / `RefreshActivePageAsync` / `ActivePageTitle` / `ActivePageSubtitle`.
4. `Views/Pages/YourPage.axaml` — `x:DataType`, root `Border Classes="page-in"`, composed from existing style classes. New icon → a `StreamGeometry` in `Styles/AppTheme.xaml`.
5. `MainWindow.axaml` — one rail `Button Classes="nav" Classes.active="{Binding IsYourPage}"`, one host entry using the `$parent[Window].((vm:MainViewModel)DataContext).IsYourPage` visibility pattern.
6. Ship-test (§5).

Anything under `Assets/` — including new hero images — is compiled in by the existing `<AvaloniaResource Include="Assets\**" />` glob and needs **no** installer change.

---

## 4. Add a runtime asset, script, or config value

This is the workflow that the Installer-Parity Rule exists for. Which branch you are on decides how much work it is:

| What you added | Installer work |
| -------------- | -------------- |
| A file under `client/Assets/` (icon, image, font) | **None** — the `AvaloniaResource` glob handles it |
| A change to `APP_IDENTIFIERS` / `VERSION` | **None** — embedded resource + script sourcing handle it |
| A script under `client/publish/` | **None** — `bundle_into_publish()` copies `publish/*.sh` and `*.iss` into every publish output |
| **Any other file the app reads at runtime** | Add the copy step to `bundle_into_publish()` in `build-installer.sh` **and** mirror it in `build-deb.sh`, `build-dmg.sh`, and `installer-windows.iss` — all four, together |
| A new env var or secret | Add it to `.env` **before** `encrypt-config.sh` runs. Installers ship a `config.enc` baked at build time; dev reads `.env` directly, so a var that works under `dotnet run` is silently missing in the installer |

**Path discipline (the rule `dotnet run` can never catch):** the installed app's working directory is root-owned and not user-writable. Never write relative to cwd or the exe directory. Logs and machine-id go to `~/.config/<package-name>/`; the database and sockets go to `~/.local/share/<package-name>/`.

---

## 5. Ship-test — the gate

```bash
cd client
bash publish/build-installer.sh -b linux     # or: win | mac | all
sudo dpkg -i installers/<package>_<version>_amd64.deb
```

What `build-installer.sh` does, in order: publishes each requested RID → **verifies every published `client.dll` is newer than every source file** (aborts otherwise) → bundles `publish/*.sh` and `*.iss` into each output → runs `encrypt-config.sh` and distributes `config.enc` → invokes the per-platform packager.

**If it aborts with "a source file is newer than the published client.dll":** the publish step did not pick up your latest source. `dotnet clean && bash publish/build-installer.sh`. Note the guard covers **compiled code only** — a missing asset, an unbundled script, an absent env var, or a bad path assumption will all sail past it. Those are yours to verify by hand.

**Then actually use the installed app.** Launch it from the desktop entry (not the build directory), click through the new functionality, close the window and confirm tracking survives, relaunch and confirm the single-instance handler raises the existing window instead of starting a second process.

**Packaging edits apply to all platforms.** If you touched one build script, check whether the other three need the same change before you stop.

---

## 6. Release

```bash
cd client
bash publish/release.sh                 # all platforms this machine can build
bash publish/release.sh -b win          # Windows only
bash publish/release.sh --setup-dir /srv/alpha/setup   # custom destination
```

It builds the installers (`build-installer.sh`, which also bakes `config.enc`) and copies them into the API
server's setup folder named by `client/VERSION` — `server/setup/v1.0.0.exe`, `v1.0.0_amd64.deb`,
`v1.0.0.dmg` (destination: `--setup-dir`, else `$RELEASES_DIR`, else `../server/setup`). No GitHub, no
`gh`, no git commit/tag/push. On Windows run it from **Git Bash** (PowerShell `bash` is WSL and has no
`dotnet`). Optional release notes: drop `setup/v1.0.0.md` next to the installer.

Bump `VERSION` **before** releasing, and confirm §5 passed on at least one platform first — `release.sh` builds artifacts, it does not test them.

### How clients pick up a release

The API serves the folder (`RELEASES_DIR`, default `setup`, relative to the server's working dir or
binary) through `GET /api/v1/releases` + `GET|HEAD /api/v1/downloads/:assetId` (public, Range-capable).
The version is the first `X.Y.Z` in each filename; the highest version is `latestStable`. Every installed
client checks every `ALPHA_UPDATE_CHECK_INTERVAL_SEC` (default **60s**) and, when the server has a newer
version than the installed one, downloads and installs it itself (Windows: silent via the LocalSystem
service, no UAC; Linux: `pkexec dpkg`). For a remote API host, copy the installer into that host's
`RELEASES_DIR` — no server restart needed (the folder is re-listed every 10s).

---

## 7. Cross-service changes

A change to what the client sends the server touches four places, in this order:

1. `server/migrations/0NN_<name>.sql` — new file, never an edit to an applied one
2. `server/internal/models/` + `dto/` — row shape, then wire shape
3. `server/internal/repository/new_schema_repo.go` → `services/new_schema_service.go` → `handlers/new_schema_handler.go` → route in `router/router.go` — the client-ingest path lives in the `new_schema_*` files, not the employee/user ones
4. `client/` — the collector or `Storage/SqliteLogStore.cs` shape, then the sync payload

Then the dashboard, if the data is meant to be visible: `web/src/lib/api.ts` first (it is the client-side contract), then the page under `web/src/app/(app)/`.

**Client and server ship independently.** An older installed client will keep POSTing the old shape for as long as it is in the field, so server changes must stay backward-compatible or be gated behind a version check.

---

## 8. Before you call it done

- [ ] Compiles: `dotnet build` / `make build` / `npm run build`
- [ ] For a client change: **installed and exercised from the installer**, not `dotnet run`
- [ ] New runtime files bundled in all four packaging paths, or confirmed covered by the `Assets/**` glob
- [ ] New env vars added to `.env` before `encrypt-config.sh`
- [ ] No hardcoded product names in detection logic — classification comes from OS metadata ([AGENTS.md](./AGENTS.md) §6)
- [ ] No new literal product name or version string — both come from `APP_IDENTIFIERS` / `VERSION`
- [ ] Docs updated when the structure moved: [AGENTS.md](./AGENTS.md), [client/ARCHITECTURE.md](./client/ARCHITECTURE.md), [server/ARCHITECTURE.md](./server/ARCHITECTURE.md), [web/ARCHITECTURE.md](./web/ARCHITECTURE.md), [FILE_HIERARCHY.md](./FILE_HIERARCHY.md), [TESTING.md](./TESTING.md) when a live playbook changed
- [ ] For DLP/Windows service: installed service starts, USBSTOR toggles under a published block rule, removal-code uninstall works (`ALPHA_DLP_ENABLED=false` leaves tracker identical to pre-DLP)
