#!/usr/bin/env bash
# Build installers for Alpha AI Tracker (run from client/)
# Usage: bash publish/build-installer.sh [-b win|linux|mac|all]
set -euo pipefail

# ─── Windows: make sure we run under a bash that can see dotnet ──────────────
# From cmd / VS Code, a plain `bash` may resolve to WSL (C:\Windows\System32\bash.exe),
# which has no dotnet. Re-run this script under Git Bash in that case, and add the
# standard Windows dotnet folder to PATH when it is missing.
if ! command -v dotnet &>/dev/null; then
  for d in "/c/Program Files/dotnet" "/mnt/c/Program Files/dotnet"; do
    if [ -x "$d/dotnet.exe" ] || [ -x "$d/dotnet" ]; then
      if grep -qi microsoft /proc/version 2>/dev/null \
         && [ -x "/mnt/c/Program Files/Git/bin/bash.exe" ]; then
        echo "[env] WSL bash has no dotnet — re-running under Git Bash..."
        WIN_SCRIPT="$(wslpath -w "$(realpath "$0")")"
        WIN_CWD="$(wslpath -w "$(pwd)")"
        exec "/mnt/c/Program Files/Git/bin/bash.exe" -lc \
          'export PATH="/c/Program Files/dotnet:$PATH"; cd "$(cygpath -u "$1")" && shift && exec bash "$(cygpath -u "$0")" "$@"' \
          "$WIN_SCRIPT" "$WIN_CWD" "$@"
      fi
      export PATH="$d:$PATH"
      break
    fi
  done
  if ! command -v dotnet &>/dev/null; then
    echo "ERROR: dotnet not found. Install the .NET 10 SDK, or run this script from Git Bash."
    exit 1
  fi
fi

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
INSTALLER_DIR="$PROJECT_DIR/installers"

BUILD_ALL=true
BUILD_WIN=false
BUILD_LIN=false
BUILD_MAC=false

usage() {
  cat << EOF
Usage: $(basename "$0") [-b <platform>]

Build installers for Alpha AI Tracker.

Options:
  -b <platform>   Build for specific platform: win, linux, mac, all
  -h              Show this help message

Examples:
  $(basename "$0")                 Build all available installers
  $(basename "$0") -b win          Build Windows .exe installer only
  $(basename "$0") -b linux        Build Linux .deb installer only
  $(basename "$0") -b mac          Build macOS .dmg installer only

Prerequisites:
  Windows:  wine + Inno Setup (ISCC.exe)
  Linux:    dpkg-deb
  macOS:    macOS + create-dmg or hdiutil

See build.md for detailed setup instructions.
EOF
  exit 0
}

while getopts "b:h" opt; do
  case $opt in
    b)
      BUILD_ALL=false
      case "$OPTARG" in
        win)   BUILD_WIN=true ;;
        linux) BUILD_LIN=true ;;
        mac)   BUILD_MAC=true ;;
        all)   BUILD_ALL=true ;;
        *)
          echo "Error: Unknown platform '$OPTARG'. Valid: win, linux, mac, all"
          exit 1
          ;;
      esac
      ;;
    h) usage ;;
    *) usage ;;
  esac
done

if [ "$BUILD_ALL" = true ]; then
  BUILD_WIN=true
  BUILD_LIN=true
  BUILD_MAC=true
fi

# Fresh output — NEVER build into a folder with leftover installers from a previous
# version. release.sh uploads every file it finds in installers/, so a stale
# alpha-ai-tracker_1.1.0_amd64.deb sitting next to the new 1.1.1 one gets published
# too, and the client's auto-updater (FirstOrDefault asset match) can then pick the
# OLD installer — "the update" re-installs the same version (root cause of the
# 2026-08-12 "still shows old version" bug). Deleting the whole folder makes stale
# artifacts unrepresentable.
rm -rf "$INSTALLER_DIR"
mkdir -p "$INSTALLER_DIR"

echo "=========================================="
echo " Alpha AI Tracker — Installer Builder"
echo "=========================================="

PUBLISH_WIN="$SCRIPT_DIR/windows"
PUBLISH_LIN="$SCRIPT_DIR/linux"
PUBLISH_MAC="$SCRIPT_DIR/macos"

echo ""
echo "[Build] Building latest Release binary (single source of truth)..."

# Detect the project's TargetFramework from the csproj so this script doesn't
# hard-code a specific TFM (the project recently moved net8.0 → net10.0).
# Multi-TFM: net10.0 (linux/mac) + net10.0-windows (WinForms soft-locker).
TFM_WIN="net10.0-windows"
TFM_UNIX="net10.0"
TFM="$TFM_WIN"
echo "  Windows TFM: $TFM_WIN | Unix TFM: $TFM_UNIX"

RELEASE_OUT="$PROJECT_DIR/bin/Release/$TFM"
mkdir -p "$RELEASE_OUT"

PUBLISH_ARGS=""
if [ -n "${ALPHA_SERVER_URL:-}" ]; then
  PUBLISH_ARGS="-p:DefaultServerUrl=$ALPHA_SERVER_URL"
  echo "  Baking in ALPHA_SERVER_URL=$ALPHA_SERVER_URL"
fi

# 1) Build the Release binary at the well-known location.
#    We hash the managed assembly (client.dll) — its bytes are RID-independent
#    and identical across publishes. The per-RID apphost shim (client.exe /
#    client) is platform-specific and embeds resolved paths, so it would
#    differ even when the underlying code is the same.
if ! dotnet build "$PROJECT_DIR" -c Release -f "$TFM_WIN" $PUBLISH_ARGS -o "$RELEASE_OUT" --nologo; then
  echo "ERROR: dotnet build -c Release failed. Aborting before publish so the installer is never built against a broken binary."
  exit 1
fi

RELEASE_DLL="$RELEASE_OUT/client.dll"
if [ ! -f "$RELEASE_DLL" ]; then
  echo "ERROR: Latest managed assembly not produced at $RELEASE_DLL — refusing to build an installer against stale code."
  exit 1
fi

EXPECTED_HASH="$(sha256sum "$RELEASE_DLL" | awk '{print $1}')"
echo "  Release client.dll hash: $EXPECTED_HASH"

echo ""
echo "[Publish] Publishing .NET app per-RID (using the Release binary just built)..."
HAS_ERRORS=false
declare -A PUBLISH_HASHES
for RID in win-x64 linux-x64 osx-x64; do
  case "$RID" in
    win-x64)  OUT="$PUBLISH_WIN"; BUILD_FLAG=$BUILD_WIN; RID_TFM="$TFM_WIN" ;;
    linux-x64) OUT="$PUBLISH_LIN"; BUILD_FLAG=$BUILD_LIN; RID_TFM="$TFM_UNIX" ;;
    osx-x64)  OUT="$PUBLISH_MAC"; BUILD_FLAG=$BUILD_MAC; RID_TFM="$TFM_UNIX" ;;
  esac
  if [ "$BUILD_FLAG" = true ]; then
    rm -rf "$OUT"
    if dotnet publish "$PROJECT_DIR" -c Release -f "$RID_TFM" -r "$RID" --self-contained -o "$OUT" $PUBLISH_ARGS; then
      echo "  $RID ($RID_TFM) -> $OUT"

      # Hash the freshly published client.dll.
      PUBLISHED_DLL="$OUT/client.dll"
      if [ ! -f "$PUBLISHED_DLL" ]; then
        echo "  FAILED: published client.dll missing at $PUBLISHED_DLL"
        HAS_ERRORS=true
        continue
      fi

      ACTUAL_HASH="$(sha256sum "$PUBLISHED_DLL" | awk '{print $1}')"
      PUBLISH_HASHES[$RID]="$ACTUAL_HASH"
      echo "  ✓ $RID client.dll hash: $ACTUAL_HASH"
    else
      echo "  FAILED: $RID publish error. Check SDK and dependencies."
      HAS_ERRORS=true
    fi
  fi
done

# ─── Verify every published client.dll is newer than every source file.
# This is the strongest freshness guarantee we can make without dumping the
# whole project into a hermetic build: if any .cs file in the project is newer
# than the published dll, the publish step did not pick up the latest source —
# refuse to build the installer.
SOURCE_NEWER=""
NEWEST_SOURCE_MTIME=0
while IFS= read -r src; do
  MTIME=$(stat -c %Y "$src" 2>/dev/null || stat -f %m "$src" 2>/dev/null || echo 0)
  if [ "$MTIME" -gt "$NEWEST_SOURCE_MTIME" ]; then
    NEWEST_SOURCE_MTIME="$MTIME"
    NEWEST_SOURCE_FILE="$src"
  fi
done < <(find "$PROJECT_DIR" -type f \( -name '*.cs' -o -name '*.csproj' -o -name '*.axaml' -o -name '*.xaml' \) ! -path '*/bin/*' ! -path '*/obj/*' ! -path '*/publish/*' 2>/dev/null)

for RID in win-x64 linux-x64 osx-x64; do
  CUR="${PUBLISH_HASHES[$RID]:-}"
  [ -z "$CUR" ] && continue
  case "$RID" in
    win-x64)   RID_DIR="$PUBLISH_WIN" ;;
    linux-x64) RID_DIR="$PUBLISH_LIN" ;;
    osx-x64)   RID_DIR="$PUBLISH_MAC" ;;
  esac
  PUBLISHED_DLL_MTIME=$(stat -c %Y "$RID_DIR/client.dll" 2>/dev/null || stat -f %m "$RID_DIR/client.dll" 2>/dev/null || echo 0)
  if [ "$NEWEST_SOURCE_MTIME" -gt "$PUBLISHED_DLL_MTIME" ]; then
    SOURCE_NEWER="$RID"
    echo ""
    echo "  FAILED: a source file is newer than the published client.dll for $RID."
    echo "    Newest source: $(date -d "@$NEWEST_SOURCE_MTIME" '+%Y-%m-%d %H:%M:%S' 2>/dev/null || date -r "$NEWEST_SOURCE_MTIME" '+%Y-%m-%d %H:%M:%S') — $NEWEST_SOURCE_FILE"
    echo "    Published dll: $(date -d "@$PUBLISHED_DLL_MTIME" '+%Y-%m-%d %H:%M:%S' 2>/dev/null || date -r "$PUBLISHED_DLL_MTIME" '+%Y-%m-%d %H:%M:%S') — $RID_DIR/client.dll"
    echo ""
    echo "  This means the publish step did not rebuild the latest source."
    echo "  Run: dotnet clean && bash publish/build-installer.sh"
    break
  fi
done

if [ "$HAS_ERRORS" = true ] || [ -n "$SOURCE_NEWER" ]; then
  echo ""
  echo "ERROR: One or more builds failed (or published binary is stale). Aborting."
  echo "       Refusing to build installers until every RID bundles the latest binary."
  exit 1
fi

# ── Bundle the publish scripts (runtime helpers) into each publish output.
#    Browser journey tracking is now accessibility-based (embedded in the binary —
#    no extensions/, no native host, no install-extensions.sh to ship).
bundle_into_publish() {
  local rid_dir="$1"
  if [ -d "$PROJECT_DIR/publish" ]; then
    mkdir -p "$rid_dir/publish"
    cp "$PROJECT_DIR/publish"/*.sh "$rid_dir/publish/" 2>/dev/null || true
    chmod +x "$rid_dir/publish"/*.sh 2>/dev/null || true
    cp "$PROJECT_DIR/publish"/*.iss "$rid_dir/publish/" 2>/dev/null || true
    cp "$PROJECT_DIR/publish/windows/windows_vars.iss" "$rid_dir/publish/windows/" 2>/dev/null || true
    echo "  ✓ Bundled publish scripts and ISS includes into $rid_dir"
  fi
}
[ "$BUILD_WIN" = true ] && bundle_into_publish "$PUBLISH_WIN"
[ "$BUILD_LIN" = true ] && bundle_into_publish "$PUBLISH_LIN"
[ "$BUILD_MAC" = true ] && bundle_into_publish "$PUBLISH_MAC"

# ─── DLP service — Windows LocalSystem + Linux root systemd ───
SERVICE_PROJ="$PROJECT_DIR/DlpService/AlphaAITracker.Service.csproj"
if [ "$BUILD_WIN" = true ]; then
  echo ""
  # Refresh AssemblyName / Inno defines from APP_IDENTIFIERS before publishing.
  bash "$SCRIPT_DIR/generate-windows-vars.sh"
  # shellcheck disable=SC1090
  source "$PROJECT_DIR/APP_IDENTIFIERS"
  : "${WINDOWS_SERVICE_NAME:=${APP_MUTEX}.Service}"
  echo "[Publish] Publishing ${WINDOWS_SERVICE_NAME} (win-x64) → $PUBLISH_WIN/Service ..."
  SERVICE_OUT="$PUBLISH_WIN/Service"
  rm -rf "$SERVICE_OUT"
  if dotnet publish "$SERVICE_PROJ" -c Release -r win-x64 --self-contained -o "$SERVICE_OUT" --nologo; then
    cp "$PROJECT_DIR/DlpService/install-service.ps1" "$SERVICE_OUT/" 2>/dev/null || true
    cp "$PROJECT_DIR/DlpService/uninstall-service.ps1" "$SERVICE_OUT/" 2>/dev/null || true
    echo "  ✓ DLP service → $SERVICE_OUT"
  else
    echo "ERROR: DlpService publish failed."
    exit 1
  fi
fi

if [ "$BUILD_LIN" = true ]; then
  echo ""
  # shellcheck disable=SC1090
  source "$PROJECT_DIR/APP_IDENTIFIERS"
  echo "[Publish] Publishing ${PACKAGE_NAME}-dlp (linux-x64) → $PUBLISH_LIN/Service ..."
  SERVICE_OUT_LIN="$PUBLISH_LIN/Service"
  rm -rf "$SERVICE_OUT_LIN"
  if dotnet publish "$SERVICE_PROJ" -c Release -r linux-x64 --self-contained -o "$SERVICE_OUT_LIN" --nologo; then
    echo "  ✓ Linux DLP service → $SERVICE_OUT_LIN"
  else
    echo "ERROR: Linux DlpService publish failed."
    exit 1
  fi
fi

# ─── Step: Encrypt .env to config.enc and distribute to publish outputs ───
echo ""
echo "[Config] Encrypting .env → config.enc..."
bash "$SCRIPT_DIR/encrypt-config.sh"
CONFIG_ENC="$PROJECT_DIR/config.enc"
if [ -f "$CONFIG_ENC" ]; then
  echo "  Distributing config.enc to publish outputs..."
  for OUT in "$PUBLISH_WIN" "$PUBLISH_LIN" "$PUBLISH_MAC"; do
    if [ -d "$OUT" ]; then
      cp "$CONFIG_ENC" "$OUT/"
      echo "    → $OUT/config.enc"
    fi
  done
fi

# Drop any leftover TLS CA bundle from older publishes (stack is plain HTTP only).
if [ "$BUILD_WIN" = true ] && [ -d "$PUBLISH_WIN/tls" ]; then
  rm -rf "$PUBLISH_WIN/tls"
fi

# Windows installer
if [ "$BUILD_WIN" = true ]; then
  echo ""
  echo "[Windows] Bundling VCRUNTIME140 for vpxmd (live stream)..."
  bash "$SCRIPT_DIR/ensure-vpx-native.sh" "$PUBLISH_WIN"
  echo ""
  echo "[Windows] Building Windows installer..."
  bash "$SCRIPT_DIR/generate-windows-vars.sh"
  if command -v iscc &>/dev/null; then
    iscc "$SCRIPT_DIR/installer-windows.iss"
  elif [ -f "/c/Program Files (x86)/Inno Setup 6/ISCC.exe" ]; then
    "/c/Program Files (x86)/Inno Setup 6/ISCC.exe" "$SCRIPT_DIR/installer-windows.iss"
  elif [ -f "/c/Program Files/Inno Setup 6/ISCC.exe" ]; then
    "/c/Program Files/Inno Setup 6/ISCC.exe" "$SCRIPT_DIR/installer-windows.iss"
  elif command -v wine &>/dev/null && [ -f "/usr/share/wine/ISCC.exe" ]; then
    ISCC_PATH="/usr/share/wine/ISCC.exe"
    ISS_PATH=$(winepath -w "$SCRIPT_DIR/installer-windows.iss" 2>/dev/null || echo "$SCRIPT_DIR/installer-windows.iss")
    wine "$ISCC_PATH" "$ISS_PATH"
  elif command -v wine &>/dev/null && [ -f "$HOME/.wine/drive_c/InnoSetup/ISCC.exe" ]; then
    ISCC_PATH="$HOME/.wine/drive_c/InnoSetup/ISCC.exe"
    ISS_PATH=$(winepath -w "$SCRIPT_DIR/installer-windows.iss" 2>/dev/null || echo "$SCRIPT_DIR/installer-windows.iss")
    wine "$ISCC_PATH" "$ISS_PATH"
  else
    echo "  SKIPPED — requires Inno Setup (iscc)"
    echo "  See build.md for installation instructions"
  fi
fi

# Linux installer
if [ "$BUILD_LIN" = true ]; then
  echo ""
  echo "[Linux] Building .deb installer..."
  if command -v dpkg-deb &>/dev/null; then
    bash "$SCRIPT_DIR/build-deb.sh"
  else
    echo "  SKIPPED — requires dpkg-deb (Debian/Ubuntu)"
  fi
fi

# macOS installer
if [ "$BUILD_MAC" = true ]; then
  echo ""
  echo "[macOS] Building .dmg installer..."
  if [[ "$(uname)" == "Darwin" ]] || command -v create-dmg &>/dev/null; then
    bash "$SCRIPT_DIR/build-dmg.sh"
  else
    echo "  SKIPPED — must be built on macOS (requires hdiutil or create-dmg)"
  fi
fi

echo ""
echo "=========================================="
echo " Installers are in: $INSTALLER_DIR"
echo "=========================================="
ls -lh "$INSTALLER_DIR/" 2>/dev/null || echo "  (no installers were built)"
