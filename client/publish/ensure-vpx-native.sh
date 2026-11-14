#!/usr/bin/env bash
# Wrapper so build-installer.sh (Git Bash) can bundle VCRUNTIME140 into publish/windows.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT_DIR="${1:?usage: ensure-vpx-native.sh <publish-windows-dir>}"

if command -v powershell.exe >/dev/null 2>&1; then
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$SCRIPT_DIR/ensure-vpx-native.ps1" 2>/dev/null || echo "$SCRIPT_DIR/ensure-vpx-native.ps1")" -OutDir "$(cygpath -w "$OUT_DIR" 2>/dev/null || echo "$OUT_DIR")"
  exit $?
fi

if command -v pwsh >/dev/null 2>&1; then
  pwsh -NoProfile -ExecutionPolicy Bypass -File "$SCRIPT_DIR/ensure-vpx-native.ps1" -OutDir "$OUT_DIR"
  exit $?
fi

echo "WARN: ensure-vpx-native: PowerShell not found; skipping VCRUNTIME140 bundle" >&2
exit 0
