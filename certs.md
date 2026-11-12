# Local HTTPS certs (mkcert)

PEM files under `certs/` are **gitignored**. Each machine that runs the API + web over HTTPS must install mkcert and mint its own certs.

Expected files:

| File | Purpose |
|------|---------|
| `certs/local.pem` | Server / Next TLS certificate |
| `certs/local-key.pem` | Private key |
| `certs/rootCA.pem` | Local CA (Node rewrite + face_worker trust) |

`.env` paths (relative to `server/` and `web/` cwd):

```env
TLS_CERT_FILE=../certs/local.pem
TLS_KEY_FILE=../certs/local-key.pem
TLS_CA_FILE=../certs/rootCA.pem
```

Web also needs:

```env
NODE_EXTRA_CA_CERTS=../certs/rootCA.pem
API_PROXY_TARGET=https://YOUR_HOST:8000
NEXT_PUBLIC_WS_URL=wss://YOUR_HOST:8000
```

Server also needs HTTPS CORS / public URL, for example:

```env
COOKIE_SECURE=true
INSTANCE_PUBLIC_URL=https://YOUR_HOST:8000
CORS_ALLOWED_ORIGINS=https://YOUR_HOST:3000,https://localhost:3000,https://127.0.0.1:3000
ALPHA_SERVER_URL=https://YOUR_HOST:8000
```

Replace `YOUR_HOST` with `localhost` or this PC’s LAN IP (e.g. `192.168.88.35`). Include every name you will open in the browser when minting the cert.

---

## Windows

### 1. Install mkcert

```powershell
winget install --id FiloSottile.mkcert -e --accept-source-agreements --accept-package-agreements
```

Open a **new** PowerShell so `mkcert` is on `PATH`.

### 2. Trust the local CA (one-time, UAC / Admin)

```powershell
mkcert -install
```

### 3. Mint certs into the repo

```powershell
cd C:\projects\alpha-ai-tracker
New-Item -ItemType Directory -Force -Path certs | Out-Null
cd certs

# Add every hostname / IP you will use in the browser or .env
mkcert -cert-file local.pem -key-file local-key.pem localhost 127.0.0.1 ::1 192.168.88.35

Copy-Item "$env:LOCALAPPDATA\mkcert\rootCA.pem" .\rootCA.pem -Force
Get-ChildItem
```

### 4. Run

```powershell
# API — from server/ so ../certs resolves
cd C:\projects\alpha-ai-tracker\server
go run ./cmd/server

# Web — HTTPS via scripts/dev-https.mjs
cd C:\projects\alpha-ai-tracker\web
npm run dev
```

Open `https://YOUR_HOST:3000` (not `http://`).

Plain HTTP web fallback (no TLS): `npm run dev:http`.

---

## Linux

### 1. Install mkcert

**Option A — Homebrew (any distro with brew)**

```bash
brew install mkcert nss
```

**Option B — official binary (amd64 example)**

```bash
curl -L -o mkcert "https://github.com/FiloSottile/mkcert/releases/download/v1.4.4/mkcert-v1.4.4-linux-amd64"
chmod +x mkcert
sudo mv mkcert /usr/local/bin/mkcert
```

For Firefox trust you also need NSS tools:

```bash
# Debian / Ubuntu
sudo apt-get update && sudo apt-get install -y libnss3-tools

# Fedora
sudo dnf install -y nss-tools
```

### 2. Trust the local CA (one-time)

```bash
mkcert -install
```

### 3. Mint certs into the repo

```bash
cd /path/to/alpha-ai-tracker
mkdir -p certs
cd certs

# Add every hostname / IP you will use in the browser or .env
mkcert -cert-file local.pem -key-file local-key.pem localhost 127.0.0.1 ::1 192.168.88.35

cp "$(mkcert -CAROOT)/rootCA.pem" ./rootCA.pem
ls -la
```

### 4. Run

```bash
# API — from server/ so ../certs resolves
cd /path/to/alpha-ai-tracker/server
go run ./cmd/server

# Web — HTTPS via scripts/dev-https.mjs
cd /path/to/alpha-ai-tracker/web
npm run dev
```

Open `https://YOUR_HOST:3000`.

---

## Notes

- **Do not commit** `*.pem` — they stay local per machine.
- Prefer **generating on each PC** over copying another machine’s key/CA. If you copy PEMs, that PC must trust the **same** CA (`mkcert -install` against that CAROOT), which is awkward; regenerate instead.
- Without `mkcert -install`, HTTPS still works but the browser shows a warning, and Node/Python may fail TLS verify until `TLS_CA_FILE` / `NODE_EXTRA_CA_CERTS` / `REQUESTS_CA_BUNDLE` point at `rootCA.pem`.
- face_worker loads `server/.env`; with `TLS_*` set it calls the API over `https` and trusts `TLS_CA_FILE`.
- Desktop client `ALPHA_SERVER_URL` is separate — point it at `https://…` only if that client build trusts this CA.
- Re-mint when you change LAN IP or need extra hostnames:

```bash
# Linux / macOS / Git Bash
mkcert -cert-file local.pem -key-file local-key.pem localhost 127.0.0.1 ::1 NEW_IP
```

```powershell
# Windows
mkcert -cert-file local.pem -key-file local-key.pem localhost 127.0.0.1 ::1 NEW_IP
```
