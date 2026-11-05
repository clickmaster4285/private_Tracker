#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# firefox-a11y-apparmor.sh — let the Alpha AI Tracker read snap Firefox windows
#
# PROBLEM
#   Ubuntu ships Firefox as a snap. The snap's AppArmor profile blocks EVERY
#   inbound D-Bus call from outside the sandbox ("AppArmor policy prevents this
#   sender..."), which includes the AT-SPI accessibility bus. Result: the
#   tracker's accessibility reader can never see snap Firefox windows, so
#   private/incognito Firefox browsing is completely invisible (sessionstore
#   and history never record private windows).
#
# FIX
#   Load a *surgical* copy of the snap profile that additionally allows D-Bus
#   traffic to be RECEIVED from unconfined peers (the tracker / AT-SPI bridge —
#   the same traffic screen readers use). This is NOT full complain mode: the
#   sandbox stays enforcing for files, network, and everything else; only D-Bus
#   receive access is widened.
#
#   A systemd oneshot unit re-applies the override at every boot (and after
#   `snap refresh firefox`, because snapd regenerates the base profile).
#
# USAGE
#   sudo bash firefox-a11y-apparmor.sh          # apply + install boot unit
#   sudo bash firefox-a11y-apparmor.sh --undo   # restore stock profile + remove unit
#
# NOTE
#   Firefox must be RESTARTED after applying (AppArmor mode is fixed at process
#   exec). The tracker captures private-window presence/title/flag once Firefox
#   restarts; the URL is still not exposed by Firefox itself on Linux.
# ─────────────────────────────────────────────────────────────────────────────

set -euo pipefail

PROFILE=/var/lib/snapd/apparmor/profiles/snap.firefox.firefox
OVERRIDE_DIR=/var/lib/alpha-ai-tracker/firefox-a11y
OVERRIDE_PROFILE="$OVERRIDE_DIR/snap.firefox.firefox"
UNIT=/etc/systemd/system/alpha-ai-firefox-a11y.service
MARKER="alpha-ai-tracker: allow AT-SPI"

log() { echo "[firefox-a11y] $*"; }

undo() {
    log "restoring stock enforce profile..."
    if [ -f "$PROFILE" ]; then
        apparmor_parser -r "$PROFILE" 2>/dev/null || true
    fi
    rm -f "$UNIT" "$OVERRIDE_DIR/apply.sh" "$OVERRIDE_PROFILE"
    rmdir "$OVERRIDE_DIR" 2>/dev/null || true
    systemctl daemon-reload 2>/dev/null || true
    systemctl disable --now alpha-ai-firefox-a11y.service 2>/dev/null || true
    log "done. (restart Firefox to run under the stock profile)"
}

if [ "${1:-}" = "--undo" ]; then
    undo
    exit 0
fi

if [ "$(id -u)" -ne 0 ]; then
    echo "run with sudo: sudo bash $0" >&2
    exit 1
fi

if [ ! -f "$PROFILE" ]; then
    log "snap firefox profile not found at $PROFILE — nothing to do (deb Firefox is already AT-SPI reachable)."
    exit 0
fi

# ── 1. Build the surgical override profile (always from the CURRENT base, so a
#    `snap refresh firefox` that changes revision-pinned paths can never leave a
#    stale override behind) ──────────────────────────────────────────────────
mkdir -p "$OVERRIDE_DIR"

cp "$PROFILE" "$OVERRIDE_PROFILE.tmp"
python3 - "$OVERRIDE_PROFILE.tmp" "$MARKER" <<'PY'
import sys
path, marker = sys.argv[1], sys.argv[2]
with open(path) as f:
    text = f.read()
if marker in text:
    sys.exit(0)
rule = (
    "\n  # --- " + marker + " ---\n"
    "  # The tracker reads Firefox windows through the AT-SPI accessibility\n"
    "  # bridge (the same traffic screen readers use). The stock snap profile\n"
    "  # denies ALL inbound D-Bus, hiding Firefox windows (incl. private ones)\n"
    "  # from the tracker. This receive-only rule lets the a11y bridge in while\n"
    "  # keeping the rest of the sandbox enforcing. No peer restriction: the\n"
    "  # sender (the tracker / AT-SPI daemon) may be labeled e.g. \"unconfined\"\n"
    "  # or \"vscode (unconfined)\" depending on how it was spawned.\n"
    "  dbus (receive),\n"
)
idx = text.rstrip().rfind('}')
if idx == -1:
    sys.exit('no closing brace in profile')
text = text[:idx] + rule + text[idx:]
with open(path, 'w') as f:
    f.write(text)
PY
mv "$OVERRIDE_PROFILE.tmp" "$OVERRIDE_PROFILE"
log "built surgical override profile (from current base)"

# ── 2. Load it (replaces the loaded enforce profile) ────────────────────────
if apparmor_parser -r "$OVERRIDE_PROFILE" 2>/dev/null; then
    log "AppArmor override loaded"
else
    apparmor_parser -r -W "$OVERRIDE_PROFILE"
    log "AppArmor override loaded (with -W)"
fi

# ── 3. Install the boot/refresh unit ────────────────────────────────────────
cat > "$UNIT" <<'UNIT'
[Unit]
Description=Alpha AI Tracker — re-apply snap Firefox AT-SPI AppArmor override
After=snapd.apparmor.service
Wants=snapd.apparmor.service

[Service]
Type=oneshot
ExecStart=/usr/bin/env bash /var/lib/alpha-ai-tracker/firefox-a11y/apply.sh
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
UNIT

cat > "$OVERRIDE_DIR/apply.sh" <<'APPLY'
#!/usr/bin/env bash
# Re-apply at boot / after snap refresh. REGENERATES from the current base
# profile (snapd rewrites the base on every `snap refresh`, and its paths are
# revision-pinned — a frozen copy would deny the new revision's files).
set -uo pipefail
PROFILE=/var/lib/snapd/apparmor/profiles/snap.firefox.firefox
OVERRIDE_DIR=/var/lib/alpha-ai-tracker/firefox-a11y
OVERRIDE=/var/lib/alpha-ai-tracker/firefox-a11y/snap.firefox.firefox
MARKER="alpha-ai-tracker: allow AT-SPI"
[ -f "$PROFILE" ] || exit 0
mkdir -p "$OVERRIDE_DIR"
cp "$PROFILE" "$OVERRIDE.tmp"
python3 - "$OVERRIDE.tmp" "$MARKER" <<'PY'
import sys
path, marker = sys.argv[1], sys.argv[2]
with open(path) as f:
    text = f.read()
if marker in text:
    sys.exit(0)
rule = (
    "\n  # --- " + marker + " ---\n"
    "  dbus (receive),\n"
)
idx = text.rstrip().rfind('}')
if idx == -1:
    sys.exit('no closing brace in profile')
text = text[:idx] + rule + text[idx:]
with open(path, 'w') as f:
    f.write(text)
PY
mv "$OVERRIDE.tmp" "$OVERRIDE"
apparmor_parser -r "$OVERRIDE" 2>/dev/null || apparmor_parser -r -W "$OVERRIDE" 2>/dev/null || true
exit 0
APPLY
chmod +x "$OVERRIDE_DIR/apply.sh"

systemctl daemon-reload || true
systemctl enable alpha-ai-firefox-a11y.service >/dev/null 2>&1 || true
systemctl start alpha-ai-firefox-a11y.service || true

log ""
log "Applied. NOW RESTART FIREFOX for the override to take effect:"
log "  (quit Firefox fully, then reopen — private windows become visible to the tracker)"
log ""
log "Verify:  sudo aa-status | grep 'snap.firefox.firefox'"
log "Undo:    sudo bash $0 --undo"
