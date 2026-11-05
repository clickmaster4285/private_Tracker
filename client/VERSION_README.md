# Version Management

## Overview

This project uses a **single source of truth** for version numbers: the `VERSION` file at the root of the `client/` directory.

## How It Works

All build and release commands automatically read the version from this file. You **never** need to specify the version on the command line.

### Current Version

```
0.2.0
```

> Read the file rather than trusting this snippet — `cat client/VERSION` is authoritative.

## Usage

### Development

```bash
cd client

# Build and run (version automatically embedded in assembly)
dotnet run

# Build for release (version embedded in assembly metadata)
dotnet build -c Release
```

The version is automatically read from the `VERSION` file and embedded in:
- Assembly version (`AssemblyVersion`)
- File version (`FileVersion`)
- Informational version (`InformationalVersion`)

### Building Installers

```bash
cd client

# Build all installers (Linux, Windows, macOS)
bash publish/build-installer.sh

# Build specific platform
bash publish/build-installer.sh -b linux
bash publish/build-installer.sh -b win
bash publish/build-installer.sh -b mac
```

Each installer will automatically use the version from the `VERSION` file (at `0.2.0`, that is):
- **Linux (.deb):** `alpha-ai-tracker_0.2.0_amd64.deb`
- **Windows (.exe):** `AlphaAITracker-Setup-0.2.0.exe`
- **macOS (.dmg):** `AlphaAITracker.dmg` (with version in app bundle)

### Creating Releases

```bash
cd client

# Create a release (uses version from VERSION file)
bash publish/release.sh

# Optionally override version on command line
bash publish/release.sh v1.2.3
```

The release script will:
1. Read version from `VERSION` file, prefixed with "v" (currently `v0.2.0`)
2. Build all installers with that version
3. Create a git tag with the version
4. Create a GitHub release with the installers

## Changing the Version

To release a new version:

1. Edit the `VERSION` file:
   ```bash
   echo "0.0.2" > client/VERSION
   ```

2. Build and test:
   ```bash
   cd client
   dotnet build -c Release
   ```

3. Create the release:
   ```bash
   bash publish/release.sh
   ```

## Version Format

The version should follow semantic versioning:
```
MAJOR.MINOR.PATCH
```

Examples:
- `0.0.1` - Initial release
- `0.1.0` - New feature
- `1.0.0` - First stable release
- `1.2.3` - Bug fix

## Files That Use the Version

**Build time** — these read `VERSION` directly:

1. **`client.csproj`** — .NET assembly versioning (`Version`, `FileVersion`, `InformationalVersion`)
2. **`publish/build-deb.sh`** — Linux .deb package version
3. **`publish/build-dmg.sh`** — macOS app bundle version (CFBundleVersion)
4. **`publish/generate-windows-vars.sh`** → **`publish/installer-windows.iss`** — Windows installer version
5. **`publish/release.sh`** — Git tag and GitHub release version

**Runtime** — the version is read back out of the assembly, not re-read from disk:

6. **`Core/AppInfo.cs`** — reads `AssemblyInformationalVersionAttribute`, strips any `+<sha>` source-link suffix, and exposes `AppInfo.Version` / `VersionDisplay` ("Version 0.2.0") / `TitleWithVersion`. `MainViewModel` re-exposes these as `AppVersionDisplay` / `AppTitleWithVersion`, which is what the window title, splash footer and nav-rail footer bind to. No XAML or C# file contains a literal version string.

## Verification

To verify the version is correctly embedded:

```bash
# Check the VERSION file
cat client/VERSION

# Check the built assembly
strings bin/Release/net10.0/client.dll | grep "^0\.2\.0"

# Check a built installer
dpkg-deb -f installers/alpha-ai-tracker_0.2.0_amd64.deb Version
```

## Notes

- The `VERSION` file should contain **only** the version number (no "v" prefix, no extra whitespace)
- The `release.sh` script adds a "v" prefix for git tags (e.g., `v0.2.0`)
- Installer filenames use the version without the "v" prefix (e.g., `0.2.0`)
- Changing the version requires a rebuild: `dotnet clean && dotnet build`. The clean is **not optional** — `client.csproj` reads `VERSION` at project-evaluation time (deliberately, so IDE design-time builds don't fall back to `1.0.0`), so an incremental build reuses the cached project graph and keeps stamping the old version.
