# Alpha AI Tracker — Build & Installers

## Prerequisites

### .NET 10 SDK (required for publishing)

```bash
# Ubuntu/Debian
wget https://dotnet.microsoft.com/download/dotnet/scripts/v1/dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 10.0
# Add to ~/.bashrc:
export PATH="$HOME/.dotnet:$PATH"
```

### Windows installer (.exe) — only for Linux

```bash
sudo apt install wine wine64

# Download Inno Setup
wget https://jrsoftware.org/download.php/is.exe -O /tmp/innosetup.exe

# Install via Wine
wine /tmp/innosetup.exe /VERYSILENT /SUPPRESSMSGBOXES /DIR="C:\InnoSetup"

# Symlink for the build script
sudo mkdir -p /usr/share/wine
sudo ln -s ~/.wine/drive_c/InnoSetup/ISCC.exe /usr/share/wine/ISCC.exe
```

### Linux installer (.deb)

```bash
sudo apt install dpkg  # pre-installed on Ubuntu/Debian
```

### macOS installer (.dmg) — run ON a Mac

```bash
brew install create-dmg
```

---

## Build Commands

Run **from the** `client/` **directory**.

```bash
# Build everything available on this machine
bash publish/build-installer.sh

# Build for a specific platform only
bash publish/build-installer.sh -b win     # Windows .exe
bash publish/build-installer.sh -b linux   # Linux .deb
bash publish/build-installer.sh -b mac     # macOS .dmg

# Show help
bash publish/build-installer.sh -h
```

---

## Output Files

| Command      | Output                                          | Format                      |
| ------------ | ----------------------------------------------- | --------------------------- |
| `-b win`   | `installers/AlphaAITracker-Setup-0.2.0.exe`   | Inno Setup wizard installer |
| `-b linux` | `installers/alpha-ai-tracker_0.2.0_amd64.deb` | Debian package              |
| `-b mac`   | `installers/AlphaAITracker.dmg`               | macOS disk image            |

---

## Rebuilding After Code Changes

After making any code changes, you need to rebuild before testing or creating installers.

### Quick Rebuild (Debug)

```bash
cd client
dotnet build
```

### Rebuild and Run Locally

```bash
cd client
dotnet run
```

### Rebuild for Release

```bash
cd client
dotnet build -c Release
```

### Full Rebuild (Clean + Build)

```bash
cd client
dotnet clean
dotnet build
```

### Create Installers After Rebuild

```bash
# Rebuild first
dotnet build -c Release

# Then create installer
bash publish/build-installer.sh
```

---

## Stopping the Client

The client runs as a **systemd user service**. Use these commands to stop it:

### Stop the Running Service

```bash
systemctl --user stop alpha-ai-tracker.service
```

### Stop + Prevent Auto-Start

```bash
systemctl --user stop alpha-ai-tracker.service
systemctl --user disable alpha-ai-tracker.service
```

### Kill Immediately (should auto-reopen ~8s)

When the tracker is under the user unit (`systemctl --user status alpha-ai-tracker.service`
shows `active` and MainPID), a hard kill is expected to reopen via `Restart=always`:

```bash
kill -9 "$(systemctl --user show -p MainPID --value alpha-ai-tracker.service)"
# or:
killall -9 client
```

Wait ~8 seconds (`RestartSec=8`), then confirm `systemctl --user is-active alpha-ai-tracker.service`
is `active` again. Persistent stop (no reopen) remains `systemctl --user disable --now …`.

### Remove Auto-Start on Desktop Login

```bash
rm ~/.config/autostart/alpha-ai-tracker.desktop
```

---

## Release to GitHub

After building installers, you can publish them as a GitHub Release.

### Prerequisites

```bash
# Install GitHub CLI
sudo apt install gh   # Ubuntu/Debian
# or: brew install gh  # macOS

# Authenticate
echo "YOUR_GITHUB_TOKEN" | gh auth login --with-token
# Or: gh auth login  # interactive browser-based auth
```

### Create a Release (One Command)

Run **from the** `client/` **directory**.

```bash
# Build all installers, create git tag, and upload to GitHub Releases
bash publish/release.sh v0.2.0

# Or without a version (defaults to v0.2.0)
bash publish/release.sh
```

This will:

1. Build all installers via `build-installer.sh`
2. Commit any pending changes
3. Create and push the git tag (e.g. `v0.2.0`)
4. Create a GitHub Release with the installer files attached
5. Verify the release was created

### Manual Upload

If you prefer to upload manually:

```bash
cd client
bash publish/build-installer.sh
# Then go to https://github.com/clickmaster4285/Alpha-AI-Tracker/releases/new
# and upload the files from installers/
```

---

## Windows DLP service

Names come from `client/APP_IDENTIFIERS` (`WINDOWS_SERVICE_NAME`, `APP_MUTEX`, `WINDOWS_DLP_PIPE_NAME`) — do not hardcode them in scripts.

Lab install without rebuilding the full Inno installer (run **elevated** PowerShell):

```powershell
cd client\DlpService
# refreshes GeneratedIdentity.props from APP_IDENTIFIERS
bash ../publish/generate-windows-vars.sh
dotnet build -c Release
powershell -ExecutionPolicy Bypass -File .\install-service.ps1
```

- Installs to `C:\Program Files\<APP_MUTEX>\Service\`
- Registers **LocalSystem** auto-start service `<WINDOWS_SERVICE_NAME>` with SCM failure recovery
- Writes `%ProgramData%\<APP_MUTEX>\dlp\service.env` (`ALPHA_DLP_ENABLED=true` plus DLP/control knobs merged from bake `.env`)
- Tracker login writes `%ProgramData%\<APP_MUTEX>\dlp\device.auth` so the service can DeviceAuth
- Control store: `%ProgramData%\<APP_MUTEX>\control\control.json` (Fixed/Roaming; see `newplan.md`)

**Tracking-control knobs** (re-bake `config.enc` after changing `client/.env`; `install-service.ps1` upserts into `service.env` without overwriting existing values):

| Key | Default | Purpose |
|---|---|---|
| `ALPHA_CONTROL_POLL_SEC` | `3600` | `TrackingControlWorker` DeviceAuth `GET /devices/control` interval (clamped 60…86400) |

Mode (`fixed`/`roaming`) is **never** taken from env — only from server control poll / `control.json`. Planned stop-flush knobs (`ALPHA_STOP_FLUSH_*` in `newplan.md`) are not wired yet.

```powershell
# Uninstall service (optional -RemoveFiles)
powershell -ExecutionPolicy Bypass -File .\uninstall-service.ps1
```

The Windows Inno installer also publishes `Service\` and runs the same `sc create` / start on post-install.

### Remove account (local wipe only)

No removal code. Syncs unsynced rows when online, then wipes the local DB and relaunches signed-out:

```bash
alpha-ai-tracker --remove-account
```

```powershell
& "C:\Program Files (x86)\Alpha AI Tracker\client.exe" --remove-account --no-pause
```

Does **not** unlock DLP / write `teardown.ok`. For full product uninstall use the next section.

### Protected uninstall (removal code)

1. Web **Employees → Issue Removal Code** (pick machine) — code shown once (Linux + Windows commands in the modal).
2. On the PC:
   ```powershell
   & "C:\Program Files (x86)\Alpha AI Tracker\client.exe" --protected-uninstall --code "<paste>" --no-pause
   ```
   ```bash
   alpha-ai-tracker --protected-uninstall --code "<paste>"
   ```
3. **Windows:** verifies via DLP pipe (unlocks ACL, stops service), drains/self-removes, wipes, writes `%ProgramData%\<APP_MUTEX>\dlp\teardown.ok`. Then Apps & features.
4. **Linux:** HTTP `removal-verify` + drain/wipe + `teardown.ok` (LocalAppData / `/var/lib/<pkg>/dlp`). Then `sudo dpkg -r alpha-ai-tracker` (or Software). Without `teardown.ok`, Inno / `.deb` `prerm` refuses.

While running on Windows, the DLP service uses a locked DACL (LocalSystem full / Administrators query-only), so Services.msc **Stop** fails for admins until a removal code unlocks it.

---

## Sending to a Friend

### Windows

Share `installers/AlphaAITracker-Setup-0.2.0.exe` — double-click, Next > Next > Install.

### Linux

Share `installers/alpha-ai-tracker_0.2.0_amd64.deb` — double-click or:

```bash
sudo dpkg -i alpha-ai-tracker_0.2.0_amd64.deb
```

> **If the app is already installed** (Software Center opens but shows nothing), remove the old package first, then install the new one:
>
> ```bash
> sudo dpkg -r alpha-ai-tracker
> sudo dpkg -i alpha-ai-tracker_0.2.0_amd64.deb
> ```

### macOS

Share `installers/AlphaAITracker.dmg` — double-click, drag app to Applications folder.
