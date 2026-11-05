#!/usr/bin/env bash
# Build a .dmg for Alpha AI Tracker (macOS)
# Output: ../installers/AlphaAITracker.dmg
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"

# Load app identifiers from single source of truth
source "$PROJECT_DIR/APP_IDENTIFIERS"

APP_BUNDLE="/tmp/${MACOS_BUNDLE_NAME}.app"
INSTALLER_DIR="$PROJECT_DIR/installers"
VERSION="$(cat "$PROJECT_DIR/VERSION")"

rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources"

# If we have macOS publish output, use it from publish/macos/
# Otherwise, build directly
PUBLISH_DIR="$SCRIPT_DIR/macos"
if [ -d "$PUBLISH_DIR" ]; then
  echo "Copying macOS publish output..."
  cp -r "$PUBLISH_DIR/"* "$APP_BUNDLE/Contents/MacOS/"
else
  echo "ERROR: No macOS publish output found at $PUBLISH_DIR"
  echo "Run first: dotnet publish -c Release -r osx-x64 --self-contained -o publish/macos"
  exit 1
fi

# Info.plist
cat > "$APP_BUNDLE/Contents/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
 "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key>
  <string>$EXECUTABLE_NAME</string>
  <key>CFBundleIdentifier</key>
  <string>$BUNDLE_ID</string>
  <key>CFBundleName</key>
  <string>$DISPLAY_NAME</string>
  <key>CFBundleVersion</key>
  <string>$VERSION</string>
  <key>CFBundleShortVersionString</key>
  <string>$VERSION</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>LSMinimumSystemVersion</key>
  <string>10.15</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
EOF

# Copy icon if available
if [ -f "$PROJECT_DIR/Assets/icon.icns" ]; then
  cp "$PROJECT_DIR/Assets/icon.icns" "$APP_BUNDLE/Contents/Resources/"
fi

# Make the main binary executable
chmod +x "$APP_BUNDLE/Contents/MacOS/client"

mkdir -p "$INSTALLER_DIR"

# Use create-dmg if available, otherwise use hdiutil
if command -v create-dmg &>/dev/null; then
  create-dmg \
    --volname "${MACOS_BUNDLE_NAME}" \
    --window-pos 200 120 \
    --window-size 600 400 \
    --icon-size 100 \
    --icon "${MACOS_BUNDLE_NAME}.app" 150 190 \
    --app-drop-link 450 190 \
    "${INSTALLER_DIR}/AlphaAITracker.dmg" \
    "$APP_BUNDLE"
elif command -v hdiutil &>/dev/null; then
  TEMP_DMG="/tmp/${MACOS_BUNDLE_NAME}-temp.dmg"
  hdiutil create -srcfolder "$APP_BUNDLE" -volname "${MACOS_BUNDLE_NAME}" \
    -fs HFS+ -format UDRW "$TEMP_DMG" -ov
  hdiutil convert "$TEMP_DMG" -format UDZO -o "${INSTALLER_DIR}/AlphaAITracker.dmg"
  echo "DMG created with hdiutil (basic layout)"
else
  echo "ERROR: Neither create-dmg nor hdiutil found on macOS."
  echo "Install create-dmg: brew install create-dmg"
  exit 1
fi

echo "macOS .dmg installer created: $INSTALLER_DIR/AlphaAITracker.dmg"
