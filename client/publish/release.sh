#!/usr/bin/env bash
# Release script for Alpha AI Tracker
# Builds installers and publishes them to the API server's setup folder, where
# GET /api/v1/releases + /api/v1/downloads/:assetId serve them to the website and
# to every installed client (which checks once a minute and self-updates).
# No GitHub, no gh CLI, no git push.
# Run from the client/ directory.
#
# Usage: bash publish/release.sh [-b win|linux|mac] [--setup-dir DIR]
#   -b           build only one platform (default: everything this machine can build)
#   --setup-dir  destination folder (default: $RELEASES_DIR, else ../server/setup)
#
# Files are named by version from client/VERSION, e.g. setup/v1.0.0.exe,
# setup/v1.0.0_amd64.deb, setup/v1.0.0.dmg. Optional notes: setup/v1.0.0.md.
# For a remote API host, copy the resulting file(s) into that server's RELEASES_DIR.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
INSTALLER_DIR="$PROJECT_DIR/installers"
VERSION="$(tr -d '[:space:]' < "$PROJECT_DIR/VERSION")"
SETUP_DIR="${RELEASES_DIR:-$PROJECT_DIR/../server/setup}"
BUILD_ARGS=()

while [ $# -gt 0 ]; do
  case "$1" in
    -b) BUILD_ARGS=(-b "$2"); shift 2 ;;
    --setup-dir) SETUP_DIR="$2"; shift 2 ;;
    -h|--help) sed -n '2,15p' "$0"; exit 0 ;;
    *) echo "Unknown argument: $1"; exit 1 ;;
  esac
done

# shellcheck disable=SC1090
source "$PROJECT_DIR/APP_IDENTIFIERS"

echo "=========================================="
echo " $DISPLAY_NAME — Release v$VERSION"
echo " Destination: $SETUP_DIR"
echo "=========================================="

# ── Step 1: Build installers (build-installer.sh wipes installers/ first and bakes config.enc) ──
echo ""
echo "[1/2] Building installers..."
bash "$SCRIPT_DIR/build-installer.sh" "${BUILD_ARGS[@]}"

shopt -s nullglob
BUILT=("$INSTALLER_DIR"/*.exe "$INSTALLER_DIR"/*.deb "$INSTALLER_DIR"/*.dmg)
shopt -u nullglob
if [ ${#BUILT[@]} -eq 0 ]; then
  echo "ERROR: No installers were built. Check build logs above."
  exit 1
fi

# ── Step 2: Copy into the server setup folder, named by version ──
echo ""
echo "[2/2] Publishing to $SETUP_DIR ..."
mkdir -p "$SETUP_DIR"
for f in "${BUILT[@]}"; do
  case "$f" in
    *.exe) dest="v$VERSION.exe" ;;
    *.deb) dest="v${VERSION}_amd64.deb" ;;
    *.dmg) dest="v$VERSION.dmg" ;;
  esac
  # Write to a temp name then rename, so a client polling mid-copy never sees a half file.
  cp "$f" "$SETUP_DIR/$dest.tmp"
  mv -f "$SETUP_DIR/$dest.tmp" "$SETUP_DIR/$dest"
  echo "  ✓ $(basename "$f") → $SETUP_DIR/$dest"
done

echo ""
echo "=========================================="
echo " Release v$VERSION published."
echo " Installed clients pick it up on their next check (≤ 1 minute)."
echo "=========================================="
