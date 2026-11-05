# APP_IDENTIFIERS — App Identity Management

> **Single source of truth for all package/app identifiers across the project.**
> Includes Windows DLP SCM service name, display name, named pipe, and maint task (2026-10-22).

## Overview

`APP_IDENTIFIERS` centralizes every piece of app identity metadata that used to be scattered and hardcoded across build scripts, installer templates, platform configs, and the DLP Windows service (`DlpService` / Inno / `install-service.ps1`).

**Build-time consumers** — these `source` the file as shell:

- `publish/build-deb.sh`
- `publish/build-dmg.sh`
- `publish/generate-windows-vars.sh`
- `publish/installer-windows.iss` (via `windows_vars.iss`)
- `publish/release.sh`

**Runtime consumer** — the running app reads the same file, so the GUI cannot drift from the installer:

- `Core/AppInfo.cs` — see [At runtime](#at-runtime) below.

## At runtime

`client.csproj` embeds the file into the assembly:

```xml
<EmbeddedResource Include="APP_IDENTIFIERS" LogicalName="client.APP_IDENTIFIERS" />
```

`Core/AppInfo.cs` reads it back with `Assembly.GetManifestResourceStream("client.APP_IDENTIFIERS")` and parses it with a **strict regex — the file is read as data, never executed** — accepting `KEY="value"`, `KEY='value'` and bare `KEY=value`, skipping `#` comments. Every accessor has a fallback default and the loader swallows exceptions, because branding must never be able to take the app down.

| `AppInfo` member | Key | Where it renders |
| ---------------- | --- | ---------------- |
| `DisplayName` | `DISPLAY_NAME` | window title, splash, nav-rail wordmark, tray tooltip + menu |
| `Tagline` | `TAGLINE` *(optional — absent from the shipped file, defaults to `ENTERPRISE SECURITY SUITE`)* | under the wordmark |
| `Initials` | *computed* — first + last initial of `DisplayName` | logo tile |
| `Publisher` / `Copyright` | `PUBLISHER` | footers (`© <year> <publisher>`) |
| `AppUrl` | `APP_URL` | support links |
| `PackageName` / `BundleId` / `ExecutableName` / `AppMutex` / `WmClass` | same-named keys | per-OS runtime paths and single-instance identity |
| `Version` / `VersionDisplay` / `TitleWithVersion` | from `VERSION` via `InformationalVersion` | rail footer, splash footer, window title, log banners |

`MainViewModel` re-exposes these as `AppDisplayName` / `AppTagline` / `AppInitials` / `AppVersionDisplay` / `AppCopyright` / `AppTitleWithVersion` so XAML binds to them. **No XAML or C# file contains a literal product name.**

Two consequences:

- **Re-branding needs no installer-script change.** The embedded resource lives inside `client.dll` and rides the publish output automatically — same for hero images, which the `<AvaloniaResource Include="Assets\**" />` glob compiles in. The Installer-Parity Rule still applies to *new file-based* runtime assets.
- ⚠️ **`Core/EncryptedConfigService.cs` `TransportKeySeed` / `MachineKeyPrefix` are NOT branding.** They read like product names but are cryptographic key-derivation seeds — templatizing them from this file would make every `config.enc` already deployed in the field undecryptable. Leave them byte-for-byte alone during a re-brand.

## Editing Identifiers

To rename the app, change its bundle ID, or re-brand for a customer:

```bash
nano client/APP_IDENTIFIERS
```

That’s it. Do **not** edit the individual build scripts or `.iss` file directly.

## Supported Variables

| Variable | Example | Used In |
|----------|---------|---------|
| `PACKAGE_NAME` | `alpha-ai-tracker` | Linux `.deb` package, install path, systemd unit paths |
| `DISPLAY_NAME` | `Alpha AI Tracker` | Desktop entries, installer UI, release notes |
| `BUNDLE_ID` | `com.alphaai.tracker` | macOS `Info.plist` (`CFBundleIdentifier`) |
| `EXECUTABLE_NAME` | `client` | Binary name, launch commands |
| `PUBLISHER` | `Alpha AI` | `.deb` Maintainer, Windows installer publisher |
| `APP_MUTEX` | `AlphaAITracker` | Windows single-instance mutex; ProgramData folder stem |
| `WINDOWS_SERVICE_NAME` | `AlphaAITracker.Service` | Windows DLP SCM service + published exe base name |
| `WINDOWS_SERVICE_DISPLAY_NAME` | `Alpha AI Tracker DLP` | Services.msc display name |
| `WINDOWS_DLP_PIPE_NAME` | `AlphaAITracker.Dlp` | Named pipe tracker ↔ DLP service |
| `WINDOWS_SERVICE_MAINT_TASK` | `AlphaAITrackerSvcMaint` | Installer schtasks name for locked-service stop/delete |
| `WINDOWS_INSTALLER_NAME` | `AlphaAITracker` | Windows `.exe` installer filename |
| `DEB_PACKAGE_NAME` | `alpha-ai-tracker` | Linux `.deb` package filename |
| `MACOS_BUNDLE_NAME` | `Alpha AI Tracker` | macOS `.app` bundle and `.dmg` volume name |
| `APP_URL` | `https://alpha-ai-tracker.example.com` | Installer support/update URLs |
| `DESKTOP_CATEGORIES` | `Office;Productivity;` | Linux `.desktop` file `Categories=` |
| `WM_CLASS` | `AlphaAITracker` | Linux window manager class |
| `TAGLINE` *(optional)* | `ENTERPRISE SECURITY SUITE` | GUI only — the line under the nav-rail wordmark. Absent from the shipped file; add it to override the default. No build script reads it. |

## How It Works

### Linux (`build-deb.sh`)

```bash
source "$PROJECT_DIR/APP_IDENTIFIERS"
```

Then `$PACKAGE_NAME`, `$DISPLAY_NAME`, etc. are used everywhere:

```bash
cat > "$PKG_ROOT/usr/share/applications/$PACKAGE_NAME.desktop" << EOF
[Desktop Entry]
Name=$DISPLAY_NAME
Exec=/usr/share/$PACKAGE_NAME/$EXECUTABLE_NAME
EOF
```

### macOS (`build-dmg.sh`)

```bash
source "$PROJECT_DIR/APP_IDENTIFIERS"
```

Used in `Info.plist`:

```xml
<key>CFBundleIdentifier</key>
<string>$BUNDLE_ID</string>
<key>CFBundleName</string>
<string>$DISPLAY_NAME</string>
```

### Windows (`installer-windows.iss`)

Because Inno Setup `.iss` scripts cannot `source` shell files, the release/build pipeline generates a temporary include file:

```bash
bash publish/generate-windows-vars.sh
# produces publish/windows/windows_vars.iss
```

Which the `.iss` file includes:

```iss
#include "windows\windows_vars.iss"
```

The `.iss` then uses `{#MyAppName}`, `{#MyAppExeName}`, etc. throughout.

### GitHub Releases (`release.sh`)

```bash
source "$PROJECT_DIR/APP_IDENTIFIERS"
```

Used in release notes and `gh` title:

```bash
gh release create "$VERSION" --title "$DISPLAY_NAME $VERSION"
```

## Adding a New Identifier

1. Add it to `client/APP_IDENTIFIERS` in `KEY="value"` format (uppercase, digits and underscores only — that is what the runtime regex accepts).
2. Use it in the relevant build script(s).
3. If the **GUI** needs it, add an accessor to `Core/AppInfo.cs` (`Get("YOUR_KEY", "fallback")`) and expose it on `MainViewModel` for XAML to bind.
4. Document it in this README.
5. If it's Windows-specific, also add it to `generate-windows-vars.sh`.

## Example: Rebranding for a Customer

```bash
cat > client/APP_IDENTIFIERS <<EOF
PACKAGE_NAME="mycompany-tracker"
DISPLAY_NAME="MyCompany Tracker"
BUNDLE_ID="com.mycompany.tracker"
EXECUTABLE_NAME="tracker"
PUBLISHER="MyCompany Inc"
TAGLINE="WORKFORCE INTELLIGENCE"
...
EOF
```

Then run builds as normal — every installer, desktop entry, bundle metadata **and the GUI itself** updates automatically.

**Smoke test the re-brand:**

```bash
cd client
dotnet clean && bash publish/build-installer.sh -b linux
sudo dpkg -i installers/mycompany-tracker_0.2.0_amd64.deb
```

Confirm the nav rail, window title, splash, footer, tray tooltip and installer filename all changed with **no other source edit**. If a third file needed touching, the single-source guarantee has regressed — fix that, not the symptom.

## Related Files

- `client/VERSION` — version number
- `client/APP_IDENTIFIERS` — app identity
- `client/VERSION_README.md` — version management docs
- `client/UI_ARCHITECTURE.md` §6 — how the GUI consumes these strings
- `AGENTS.md` §6 → *Branding-Single-Source Rule* — the mandatory rule this file implements
