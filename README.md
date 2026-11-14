# Alpha AI Tracker

Employee productivity and **Windows Security & DLP** platform: a desktop agent collects activity,
a Go API stores it, and a Next.js admin dashboard monitors journeys, attendance, live stream, and
DLP (USB / Web Blocker / alerts / protected uninstall).

| Tier | Stack | Role |
|------|--------|------|
| **Client** | .NET 10 + Avalonia (+ Windows `DlpService`) | Collect, sync, enforce DLP as LocalSystem |
| **Server** | Go (Echo) + PostgreSQL + Redis | API, policy publish, auth, live-stream SFU |
| **Web** | Next.js (React) | Admin console |

Current client version: see [`client/VERSION`](./client/VERSION). Release history: [`RELEASE_NOTES.md`](./RELEASE_NOTES.md).

**Public downloads:** drop installers in `server/setup/` (e.g. `v1.2.0.exe`; set `RELEASES_DIR` if needed). The API lists them on `GET /api/v1/releases` and streams them on `GET /api/v1/downloads/:assetId` (no GitHub). Publish with `bash client/publish/release.sh`. See `server/.env.example`.

Deep docs: [`AGENTS.md`](./AGENTS.md) (rules) · [`WORKFLOW.md`](./WORKFLOW.md) · [`TESTING.md`](./TESTING.md) · [`removeTracker.md`](./removeTracker.md) (Windows AppLocker / DLP leftover cleanup) · [`plan.md`](./plan.md) · [`FILE_HIERARCHY.md`](./FILE_HIERARCHY.md).

---

## Prerequisites

| Tool | Notes |
|------|--------|
| **Go** 1.22+ | Server |
| **Node.js** 20+ + npm | Web |
| **.NET 10 SDK** | Client + DlpService |
| **PostgreSQL** | Server DB |
| **Redis** | Secrets TTL, live-stream presence/tickets |
| **Git** | Clone / release tags |
| **Inno Setup** (Windows) / **Wine+ISCC** (Linux→Win) | Windows installer |
| **dpkg** | Linux `.deb` |
| **create-dmg** (macOS) | `.dmg` |

Installer tooling detail: [`client/build.md`](./client/build.md).

---

## Clone & configure

```bash
git clone <your-repo-url> alpha-ai-tracker
cd alpha-ai-tracker
```

### Server

```bash
cd server
cp .env.example .env    # or: make setup-env
# Edit DB_*, REDIS_*, JWT_SECRET, ADMIN_*, SERVER_PORT (default 8080)
# Drop client installers in setup/ (or set RELEASES_DIR) for /api/v1/releases
make setup              # deps + build
```

Ensure Postgres and Redis match `.env`, then create the database if needed (migrations run on boot).

### Web

```bash
cd web
cp .env.example .env
# NEXT_PUBLIC_API_URL=http://localhost:8080/api/v1
# NEXT_PUBLIC_WS_URL=ws://localhost:8080
npm install
```

### Client

```bash
cd client
cp .env.example .env
# Set ALPHA_SERVER_URL to your API base (e.g. http://localhost:8080)
# Enable DLP knobs as needed (ALPHA_DLP_ENABLED, ALPHA_DLP_WEB_BLOCK, …)
```

Dev reads `.env` directly. **Installed builds** use a bake-time `config.enc` — see [Release](#release).

---

## Development mode

Start **infra → server → web → client** (three terminals).

### 1. Server

```bash
cd server
make run          # build + run
# or hot reload:
#   go install github.com/air-verse/air@latest
make dev
```

API defaults to `http://localhost:8080`.

### 2. Web

```bash
cd web
npm run dev       # http://localhost:3000
```

Log in with the bootstrap admin from `server/.env` (`ADMIN_EMAIL` / `ADMIN_PASSWORD`).

### 3. Client

```bash
cd client
dotnet build
dotnet run        # fast UI / collector iteration only
```

> **`dotnet run` is not a release test.** Packaging, `config.enc`, and Installer-Parity gaps only show up from an **installed** build. See [`WORKFLOW.md`](./WORKFLOW.md) §0.

**Windows DLP service (lab):** elevated `client/DlpService/install-service.ps1` — see `client/build.md` and [`TESTING.md`](./TESTING.md) §DLP / §F.

---

## Build (compile check)

```bash
# Server
cd server && make build && make test

# Web
cd web && npm run build && npx tsc --noEmit

# Client (+ DlpService)
cd client && dotnet build
dotnet build DlpService/DlpService.csproj   # Windows service project
```

---

## Build installers

From **`client/`**:

```bash
cd client
dotnet clean                    # required after VERSION / APP_IDENTIFIERS edits
bash publish/build-installer.sh             # all platforms available on this machine
bash publish/build-installer.sh -b win      # Windows .exe
bash publish/build-installer.sh -b linux    # Linux .deb
bash publish/build-installer.sh -b mac      # macOS .dmg
```

Artifacts land in `client/installers/` (e.g. `AlphaAITracker-Setup-<VERSION>.exe`,
`alpha-ai-tracker_<VERSION>_amd64.deb`).

The script publishes, guards stale DLLs, bundles assets, runs **`encrypt-config.sh`** → `config.enc`, then packs. Prerequisites and Wine/Inno notes: [`client/build.md`](./client/build.md).

**Ship-test:** install the artifact and exercise the change from the installed app — not from `dotnet run`.

---

## Release

1. Bump [`client/VERSION`](./client/VERSION) (and brand in `client/APP_IDENTIFIERS` if needed).
2. Update [`RELEASE_NOTES.md`](./RELEASE_NOTES.md) for that version.
3. Confirm server/web deploy + migrations if the release needs them.
4. Re-bake config: ensure `client/.env` has production `ALPHA_SERVER_URL` / `ALPHA_DLP_*` / stream knobs **before** the installer build.
5. Ship-test at least one platform installer.
6. Publish:

```bash
cd client
# Authenticate once: gh auth login
bash publish/release.sh                 # tag v$(cat VERSION), build, upload GitHub Release
bash publish/release.sh v1.0.0         # or pin the tag explicitly
```

`release.sh` builds installers, tags, pushes, and attaches artifacts via `gh`. It does **not** replace lab testing. Manual upload path: [`client/build.md`](./client/build.md) → “Release to GitHub”.

Typical deploy order for a full stack release:

1. **Server** (migrations apply on boot)  
2. **Web**  
3. **Client installer** (so DeviceAuth + DLP service match the API)

---

## Repo map

```
alpha-ai-tracker/
├── client/          Desktop agent + DlpService + publish/*.sh
├── server/          Go API + migrations/
├── web/             Next.js admin UI
├── AGENTS.md        Architecture + mandatory rules
├── WORKFLOW.md      Daily loop, packaging, release procedure
├── TESTING.md       Lab playbooks (DLP, USB, Web Blocker, …)
├── removeTracker.md Windows protected-uninstall leftovers (AppLocker / hosts / FW / USB)
├── plan.md          Security & DLP roadmap
└── RELEASE_NOTES.md Version history
```

---

## License / support

Internal product — see your organization for distribution and support. Do not claim offline Admin uninstall is impossible; protected uninstall is for standard users (removal code). See AGENTS.md §6 *Protected Removal Rule*.
