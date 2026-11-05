#!/usr/bin/env bash
# Release script for Alpha AI Tracker
# Builds all installers and creates a GitHub Release with the artifacts.
# Run from the client/ directory.
# Requires: gh CLI (authenticated), dotnet SDK, platform build tools
#
# Usage: bash publish/release.sh [version]
#   version: optional version tag (default: v1.0.0)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
INSTALLER_DIR="$PROJECT_DIR/installers"
VERSION="${1:-v$(cat "$PROJECT_DIR/VERSION")}"

# Fresh output — remove any installers left over from a PREVIOUS release. release.sh
# uploads every file in installers/, so without this a stale
# alpha-ai-tracker_1.1.0_amd64.deb / AlphaAITracker-Setup-1.1.0.exe would be attached
# to the new release and the client's auto-updater (FirstOrDefault asset match)
# could download the OLD installer — "the update" then re-installs the same version
# (root cause of the 2026-08-12 "still shows old version" bug). build-installer.sh
# also cleans, but deleting here first means even a failed build can never upload
# stale artifacts (the upload glob below only sees files built by THIS run).
rm -rf "$INSTALLER_DIR"

# Load app identifiers from single source of truth
# shellcheck disable=SC1090
source "$PROJECT_DIR/APP_IDENTIFIERS"

# Load REPO and ALPHA_SERVER_URL from .env if not set in environment
if [ -z "${REPO:-}" ] || [ -z "${ALPHA_SERVER_URL:-}" ]; then
  if [ -f "$PROJECT_DIR/.env" ]; then
    if [ -z "${REPO:-}" ]; then
      REPO=$(grep -E '^REPO=' "$PROJECT_DIR/.env" | cut -d '=' -f 2-)
    fi
    if [ -z "${ALPHA_SERVER_URL:-}" ]; then
      ALPHA_SERVER_URL=$(grep -E '^ALPHA_SERVER_URL=' "$PROJECT_DIR/.env" | cut -d '=' -f 2-)
    fi
  fi
fi
REPO="${REPO:-clickmaster4285/Alpha-AI-Tracker}"
export ALPHA_SERVER_URL
echo "=========================================="
echo " $DISPLAY_NAME — Release $VERSION"
echo "=========================================="

# ── Check prerequisites ──
if ! command -v gh &>/dev/null; then
  echo "ERROR: gh CLI is not installed. Install it from https://cli.github.com/"
  exit 1
fi

if ! gh auth status &>/dev/null; then
  echo "ERROR: gh CLI is not authenticated. Run 'gh auth login' first."
  exit 1
fi

# ── Step 1: Encrypt .env for distribution ──
echo ""
echo "[1/5] Encrypting .env → config.enc..."
bash "$SCRIPT_DIR/encrypt-config.sh"

# ── Step 2: Build Installers ──
echo ""
echo "[2/5] Building installers..."
bash "$SCRIPT_DIR/build-installer.sh"

if [ ! -d "$INSTALLER_DIR" ] || [ -z "$(ls -A "$INSTALLER_DIR" 2>/dev/null)" ]; then
  echo "ERROR: No installers were built. Check build logs above."
  exit 1
fi

echo ""
echo "Installers found:"
ls -lh "$INSTALLER_DIR/"

# ── Step 3: Create Git Tag ──
echo ""
echo "[3/5] Creating git tag $VERSION..."
cd "$PROJECT_DIR"
git add .
echo "  Staged changes in $PROJECT_DIR"
git commit -m "release: $VERSION" 2>/dev/null || echo "  (nothing to commit)"
git push origin HEAD 2>&1 || echo "  WARNING: Failed to push commit — push manually"
git tag -f "$VERSION" 2>/dev/null || true
git push origin "$VERSION" 2>&1 || {
  echo "WARNING: Failed to push tag. You may need to push manually:"
  echo "  git push origin $VERSION"
}

# ── Step 4: Create GitHub Release ──
echo ""
echo "[4/5] Creating GitHub release..."
RELEASE_NOTES=$(mktemp)
cat > "$RELEASE_NOTES" << EOF
# $DISPLAY_NAME $VERSION

## Installers

Download the appropriate installer for your platform:

| Platform | File |
|----------|------|
| Windows  | \`$WINDOWS_INSTALLER_NAME-Setup-$VERSION.exe\` |
| Linux    | \`${PACKAGE_NAME}_${VERSION#v}_amd64.deb\` |
| macOS    | \`$MACOS_BUNDLE_NAME.dmg\` |

See [client/build.md](./client/build.md) for installation instructions.
EOF

# Check if release already exists and delete it
if gh release view "$VERSION" --repo "$REPO" &>/dev/null 2>&1; then
  echo "  Release $VERSION already exists, deleting..."
  gh release delete "$VERSION" --repo "$REPO" -y 2>/dev/null || true
fi

# Collect only existing installer files
INSTALLER_FILES=()
for f in "$INSTALLER_DIR"/*.exe "$INSTALLER_DIR"/*.deb "$INSTALLER_DIR"/*.dmg; do
  if [ -f "$f" ]; then
    INSTALLER_FILES+=("$f")
  fi
done

echo "  Uploading ${#INSTALLER_FILES[@]} installer(s)..."
gh release create "$VERSION" \
  --repo "$REPO" \
  --title "$DISPLAY_NAME $VERSION" \
  --notes-file "$RELEASE_NOTES" \
  "${INSTALLER_FILES[@]}" 2>&1 || {
  echo ""
  echo "ERROR: Failed to create release. Uploading artifacts manually..."
  echo "  Installers are in: $INSTALLER_DIR/"
  echo "  Run manually: gh release create $VERSION --repo $REPO --title \"Alpha AI Tracker $VERSION\""
  echo "    --notes-file <(echo 'Release notes')"
  for f in "$INSTALLER_DIR"/*; do
    echo "    \"$f\" \\"
  done
  rm -f "$RELEASE_NOTES"
  exit 1
}

rm -f "$RELEASE_NOTES"

# ── Step 5: Verify ──
echo ""
echo "[5/5] Verifying release..."
gh release view "$VERSION" --repo "$REPO" --json tagName,url | head -5
echo ""
echo "=========================================="
echo " Release $VERSION complete!"
echo " View at: https://github.com/$REPO/releases/tag/$VERSION"
echo "=========================================="

