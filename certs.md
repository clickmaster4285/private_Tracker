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

## Fleet / production (private LAN only)

This product stays on the org network (`https://192.x.x.x:8000`). Do **not** skip TLS verify in fleet builds.

### Model

1. **One org root CA** (mkcert CAROOT for lab, or enterprise AD/Intune CA for production) as `certs/rootCA.pem`.
2. **One API server cert** signed by that CA, with the LAN IP (and optional internal DNS name) in SAN.
3. **Windows installer auto-trusts that root** on every PC (no employee / IT script):
   - bake copies `certs/rootCA.pem` → `publish/windows/tls/rootCA.pem`
   - Inno post-install → `%ProgramData%\AlphaAITracker\tls\rootCA.pem` + `certutil -addstore Root`
   - silent auto-update re-runs the same step
4. Client `ALPHA_SERVER_URL=https://API_IP:8000` in baked `config.enc` — leave `ALPHA_TLS_CA` unset (auto-discover). Trust policy: `client/Core/TlsCertificateTrust.cs`.

### Issue API cert (example with mkcert as stand-in org CA)

On the **API host** (once):

```powershell
mkcert -install
cd C:\projects\alpha-ai-tracker\certs
mkcert -cert-file local.pem -key-file local-key.pem 192.168.88.35
Copy-Item "$env:LOCALAPPDATA\mkcert\rootCA.pem" .\rootCA.pem -Force
```

`server/.env`:

```env
TLS_CERT_FILE=../certs/local.pem
TLS_KEY_FILE=../certs/local-key.pem
TLS_CA_FILE=../certs/rootCA.pem
COOKIE_SECURE=true
INSTANCE_PUBLIC_URL=https://192.168.88.35:8000
```

Client bake (`.env` → `config.enc`):

```env
ALPHA_SERVER_URL=https://192.168.88.35:8000
# Do not set ALPHA_TLS_CA to a repo path — installer + ProgramData handle trust.
# ALPHA_TLS_INSECURE=false
```

Then `bash client/publish/build-installer.sh -b win` (requires `certs/rootCA.pem`) and drop the Setup exe into `server/setup/`. Fleet PCs install/update — HTTPS works with no manual CA step.

### Lab repair (only if bake missed the PEM)

```powershell
powershell -ExecutionPolicy Bypass -File client\publish\install-org-ca.ps1 -CaPem C:\path\to\rootCA.pem
```

### Lab-only escape hatch

`ALPHA_TLS_INSECURE=true` disables verify (login works without CA install). **Do not** bake this into production installers.

---

## Notes

- **Do not commit** `*.pem` — they stay local per machine / secure share.
- Fleet PCs must share the **same root CA** that signed the API leaf (distribute `rootCA.pem`, not the server private key).
- Without CA trust, browsers warn and the .NET client fails with “SSL connection could not be established”.
- face_worker: `TLS_CA_FILE` / `REQUESTS_CA_BUNDLE` → same `rootCA.pem`.
- Web: `NODE_EXTRA_CA_CERTS` → same `rootCA.pem`.
- Re-mint when you change LAN IP or need extra hostnames:

```bash
# Linux / macOS / Git Bash
mkcert -cert-file local.pem -key-file local-key.pem localhost 127.0.0.1 ::1 NEW_IP
```

```powershell
# Windows
mkcert -cert-file local.pem -key-file local-key.pem localhost 127.0.0.1 ::1 NEW_IP
```
