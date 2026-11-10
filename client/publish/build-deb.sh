#!/usr/bin/env bash
# Build a .deb package for Alpha AI Tracker (Linux)
# Output: ../installers/alpha-ai-tracker_1.0.0_amd64.deb
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"

# Load app identifiers from single source of truth
source "$PROJECT_DIR/APP_IDENTIFIERS"

VERSION="$(cat "$PROJECT_DIR/VERSION")"
ARCH="amd64"
INSTALLER_DIR="$PROJECT_DIR/installers"
PKG_ROOT="/tmp/${PACKAGE_NAME}_deb"

rm -rf "$PKG_ROOT"
mkdir -p "$PKG_ROOT/DEBIAN"
mkdir -p "$PKG_ROOT/usr/share/$PACKAGE_NAME"
mkdir -p "$PKG_ROOT/usr/share/applications"
mkdir -p "$PKG_ROOT/usr/bin"
mkdir -p "$PKG_ROOT/usr/share/icons/hicolor/256x256/apps"
# /etc/alpha-ai-tracker no longer needed — config.enc is self-contained in app dir

# Copy published binaries
cp -r "$SCRIPT_DIR/linux/"* "$PKG_ROOT/usr/share/$PACKAGE_NAME/"

# Copy config.enc (encrypted .env) if it exists
if [ -f "$SCRIPT_DIR/linux/config.enc" ]; then
  cp "$SCRIPT_DIR/linux/config.enc" "$PKG_ROOT/usr/share/$PACKAGE_NAME/"
  echo "  Bundled config.enc"
else
  echo "  WARNING: config.enc not found! The app will need other config sources."
fi

# Placeholder for platform-specific files (AT-SPI now inline in C#)

# Desktop entry
cat > "$PKG_ROOT/usr/share/applications/$PACKAGE_NAME.desktop" << EOF
[Desktop Entry]
Name=$DISPLAY_NAME
Comment=Employee Monitoring & Productivity Dashboard
Exec=/usr/share/$PACKAGE_NAME/$EXECUTABLE_NAME
Icon=$PACKAGE_NAME
Terminal=false
Type=Application
Categories=$DESKTOP_CATEGORIES
StartupWMClass=$WM_CLASS
EOF

# Symlink in PATH
ln -s "/usr/share/$PACKAGE_NAME/client" "$PKG_ROOT/usr/bin/$PACKAGE_NAME"

# Icon (use a placeholder if none exists)
if [ -f "$PROJECT_DIR/Assets/icon.png" ]; then
  cp "$PROJECT_DIR/Assets/icon.png" "$PKG_ROOT/usr/share/icons/hicolor/256x256/apps/$PACKAGE_NAME.png"
else
  # Generate a minimal placeholder icon
  convert -size 256x256 xc:transparent "$PKG_ROOT/usr/share/icons/hicolor/256x256/apps/$PACKAGE_NAME.png" 2>/dev/null || true
fi

# Control file
cat > "$PKG_ROOT/DEBIAN/control" << EOF
Package: $PACKAGE_NAME
Version: $VERSION
Section: utils
Priority: optional
Architecture: $ARCH
Maintainer: $PUBLISHER <support@example.com>
Depends: gstreamer1.0-plugins-good, gstreamer1.0-pipewire, python3-gi, python3-dbus, gir1.2-gstreamer-1.0
Recommends: nftables
Description: $DISPLAY_NAME
 Employee Monitoring & Productivity Dashboard.
 Installed to /usr/share/$PACKAGE_NAME with a desktop entry and PATH symlink.
 Includes privileged DLP daemon ($PACKAGE_NAME-dlp.service) for USB / web block.
 Live stream on Linux: ScreenCast portal + PipeWire capture, GStreamer vp8enc
 (SIPSorceryMedia.Encoders only ships Windows vpxmd.dll).
EOF

# postinst script
cat > "$PKG_ROOT/DEBIAN/postinst" << 'EOF'
#!/bin/sh
set -e

# Enable AT-SPI accessibility (required for Wayland window title tracking)
if command -v gsettings >/dev/null 2>&1; then
  for user_home in /home/*; do
    user=$(basename "$user_home")
    if [ -d "$user_home" ]; then
      su "$user" -c "gsettings set org.gnome.desktop.interface toolkit-accessibility true" 2>/dev/null || true
    fi
  done
fi

# Install xdotool (X11 fallback for window title detection)
if command -v apt-get >/dev/null 2>&1; then
  apt-get install -y xdotool 2>/dev/null || true
fi

# GeoClue2 for GPS location (Phase 3 — optional, only when ALPHA_LOCATION_ENABLED=true)
# After install: enable Location Services in Settings → Privacy → Location.
# Polkit may prompt when the tracker first queries GeoClue2; allow for your user session.
if command -v apt-get >/dev/null 2>&1; then
  apt-get install -y geoclue-2.0 2>/dev/null || true
fi
echo ""
echo "Location tracking (optional): set ALPHA_LOCATION_ENABLED=true in config."
echo "  Requires GeoClue2 + desktop location permission (Settings → Privacy → Location)."
echo "  Without GPS permission the client falls back to IP-based approximate location."
echo ""

# Update desktop database
if [ -x /usr/bin/update-desktop-database ]; then
  update-desktop-database 2>/dev/null || true
fi
if [ -x /usr/bin/gtk-update-icon-cache ]; then
  gtk-update-icon-cache -f /usr/share/icons/hicolor 2>/dev/null || true
fi

EOF
# Append package-name-expanded lines (postinst body uses quoted heredoc so $ stays literal).
cat >> "$PKG_ROOT/DEBIAN/postinst" << EOF

# Teardown gate dir for protected-uninstall (sticky so the logged-in user can write).
mkdir -p /var/lib/$PACKAGE_NAME/dlp
chmod 1777 /var/lib/$PACKAGE_NAME/dlp 2>/dev/null || true
mkdir -p /var/lib/$PACKAGE_NAME/control
chmod 755 /var/lib/$PACKAGE_NAME/control 2>/dev/null || true

# Seed DLP service.env defaults when missing (never overwrite admin edits).
if [ ! -f /var/lib/$PACKAGE_NAME/dlp/service.env ]; then
  cat > /var/lib/$PACKAGE_NAME/dlp/service.env << 'ENVEOF'
ALPHA_DLP_ENABLED=true
ALPHA_DLP_USB_BLOCK=true
ALPHA_DLP_WEB_BLOCK=true
ALPHA_DLP_NETWORK_LOCKDOWN=true
ALPHA_DLP_APP_BLOCK=true
ALPHA_DLP_PORT_BLOCK=true
ALPHA_DLP_POLICY_POLL_SEC=300
ALPHA_DLP_ALERT_SYNC_SEC=60
ENVEOF
  chmod 644 /var/lib/$PACKAGE_NAME/dlp/service.env 2>/dev/null || true
fi
# Ensure ALPHA_SERVER_URL is present (policy pull needs it even when token comes from SQLite).
if [ -f /usr/share/$PACKAGE_NAME/config.enc ] || [ -f /usr/share/$PACKAGE_NAME/.env ]; then
  :
fi
# Prefer baked plaintext next to Service if publish copied it; else leave for device.auth bridge.
if [ -f /usr/share/$PACKAGE_NAME/Service/.env ]; then
  srv=\$(grep -E '^ALPHA_SERVER_URL=' /usr/share/$PACKAGE_NAME/Service/.env | head -1 | cut -d= -f2- | tr -d '\\r' || true)
  if [ -n "\$srv" ] && ! grep -q '^ALPHA_SERVER_URL=' /var/lib/$PACKAGE_NAME/dlp/service.env 2>/dev/null; then
    echo "ALPHA_SERVER_URL=\$srv" >> /var/lib/$PACKAGE_NAME/dlp/service.env
  fi
fi

# Enable + start privileged DLP daemon when packaged.
if [ -f /lib/systemd/system/$PACKAGE_NAME-dlp.service ]; then
  systemctl daemon-reload 2>/dev/null || true
  systemctl enable --now $PACKAGE_NAME-dlp.service 2>/dev/null || true
fi
EOF
chmod +x "$PKG_ROOT/DEBIAN/postinst"

# Package payload: seed /var/lib gate dir (sticky world-writable).
mkdir -p "$PKG_ROOT/var/lib/$PACKAGE_NAME/dlp"
chmod 1777 "$PKG_ROOT/var/lib/$PACKAGE_NAME/dlp"
mkdir -p "$PKG_ROOT/var/lib/$PACKAGE_NAME/control"
chmod 755 "$PKG_ROOT/var/lib/$PACKAGE_NAME/control"

# Privileged DLP daemon (L5) — system unit + binary under share/Service.
SERVICE_SRC="$SCRIPT_DIR/linux/Service"
SERVICE_BIN_NAME="${WINDOWS_SERVICE_NAME:-${APP_MUTEX}.Service}"
if [ -d "$SERVICE_SRC" ]; then
  mkdir -p "$PKG_ROOT/usr/share/$PACKAGE_NAME/Service"
  cp -a "$SERVICE_SRC/." "$PKG_ROOT/usr/share/$PACKAGE_NAME/Service/"
  chmod +x "$PKG_ROOT/usr/share/$PACKAGE_NAME/Service/$SERVICE_BIN_NAME" 2>/dev/null || true
  # Prefer a short PATH name for journal/docs.
  if [ -x "$PKG_ROOT/usr/share/$PACKAGE_NAME/Service/$SERVICE_BIN_NAME" ]; then
    ln -sf "/usr/share/$PACKAGE_NAME/Service/$SERVICE_BIN_NAME" \
      "$PKG_ROOT/usr/bin/${PACKAGE_NAME}-dlp"
  fi

  mkdir -p "$PKG_ROOT/lib/systemd/system"
  cat > "$PKG_ROOT/lib/systemd/system/${PACKAGE_NAME}-dlp.service" << EOF
[Unit]
Description=$DISPLAY_NAME DLP
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=/usr/share/$PACKAGE_NAME/Service/$SERVICE_BIN_NAME
WorkingDirectory=/usr/share/$PACKAGE_NAME/Service
Restart=always
RestartSec=5
Environment=ALPHA_DLP_DATA_DIR=/var/lib/$PACKAGE_NAME/dlp
# Defaults also merged into service.env on first start (EnvBootstrap).
EnvironmentFile=-/var/lib/$PACKAGE_NAME/dlp/service.env

[Install]
WantedBy=multi-user.target
EOF
else
  echo "  WARNING: DLP Service publish missing at $SERVICE_SRC — package without system DLP daemon."
fi

# prerm — refuse uninstall without verified removal-code gate (Windows teardown.ok parity).
# Allow: remove|purge only when gate exists; always allow failed-upgrade / abort-*.
cat > "$PKG_ROOT/DEBIAN/prerm" << EOF
#!/bin/sh
set -e

ACTION="\${1:-}"
case "\$ACTION" in
  remove|purge)
    ;;
  *)
    # failed-upgrade / abort-* — never block package recovery
    exit 0
    ;;
esac

GATE_VAR="/var/lib/$PACKAGE_NAME/dlp/teardown.ok"
GATE_USER=""
if [ -n "\${SUDO_USER:-}" ] && [ -d "/home/\$SUDO_USER" ]; then
  GATE_USER="/home/\$SUDO_USER/.local/share/$APP_MUTEX/dlp/teardown.ok"
elif [ -n "\${USER:-}" ] && [ "\$USER" != "root" ]; then
  GATE_USER="\$HOME/.local/share/$APP_MUTEX/dlp/teardown.ok"
fi

if [ -f "\$GATE_VAR" ] || { [ -n "\$GATE_USER" ] && [ -f "\$GATE_USER" ]; }; then
  # Consume gate so a replayed dpkg -r without a new code fails.
  rm -f "\$GATE_VAR" 2>/dev/null || true
  [ -n "\$GATE_USER" ] && rm -f "\$GATE_USER" 2>/dev/null || true

  APP_PID=\$(pidof $PACKAGE_NAME 2>/dev/null || pidof $EXECUTABLE_NAME 2>/dev/null || echo "")
  if [ -n "\$APP_PID" ]; then
    echo "Stopping $DISPLAY_NAME (PID: \$APP_PID)..."
    kill -9 \$APP_PID 2>/dev/null || true
    sleep 1
  fi
  # Stop user units best-effort (may have no session bus under root).
  if [ -n "\${SUDO_USER:-}" ]; then
    su - "\$SUDO_USER" -c "systemctl --user disable --now $PACKAGE_NAME.service" 2>/dev/null || true
  fi
  systemctl disable --now $PACKAGE_NAME-dlp.service 2>/dev/null || true
  exit 0
fi

echo ""
echo "$DISPLAY_NAME cannot be removed without a server-generated removal code."
echo "  1) Ask an admin for a Removal Code (Employees → Issue Removal Code)."
echo "  2) Run:  $EXECUTABLE_NAME --protected-uninstall --code <code>"
echo "  3) Then: sudo apt remove $PACKAGE_NAME   (or dpkg -r $PACKAGE_NAME)"
echo ""
exit 1
EOF
chmod +x "$PKG_ROOT/DEBIAN/prerm"

mkdir -p "$INSTALLER_DIR"
dpkg-deb --build --root-owner-group "$PKG_ROOT" "$INSTALLER_DIR/${PACKAGE_NAME}_${VERSION}_${ARCH}.deb"

echo "Linux .deb installer created: $INSTALLER_DIR/${PACKAGE_NAME}_${VERSION}_${ARCH}.deb"
