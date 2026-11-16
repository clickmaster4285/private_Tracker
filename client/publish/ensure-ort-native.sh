#!/usr/bin/env bash
# Fail the Windows installer bake if CPU ORT natives are missing from publish/windows.
# Soft-lock needs onnxruntime.dll + onnxruntime_providers_shared.dll beside client.exe.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT_DIR="${1:?usage: ensure-ort-native.sh <publish-windows-dir>}"

if [ ! -d "$OUT_DIR" ]; then
  echo "ERROR: ensure-ort-native: publish dir missing: $OUT_DIR" >&2
  exit 1
fi

# Prefer PowerShell (copies from runtimes/NuGet, strips DirectML).
if command -v powershell.exe >/dev/null 2>&1; then
  powershell.exe -NoProfile -ExecutionPolicy Bypass \
    -File "$(cygpath -w "$SCRIPT_DIR/ensure-ort-native.ps1" 2>/dev/null || echo "$SCRIPT_DIR/ensure-ort-native.ps1")" \
    -OutDir "$(cygpath -w "$OUT_DIR" 2>/dev/null || echo "$OUT_DIR")"
  exit $?
fi

if command -v pwsh >/dev/null 2>&1; then
  pwsh -NoProfile -ExecutionPolicy Bypass -File "$SCRIPT_DIR/ensure-ort-native.ps1" -OutDir "$OUT_DIR"
  exit $?
fi

# Bash-only fallback (CI without PowerShell).
ORT="$OUT_DIR/onnxruntime.dll"
SHARED="$OUT_DIR/onnxruntime_providers_shared.dll"

# Flatten from RID subfolders if publish left them nested.
if [ ! -f "$ORT" ] && [ -f "$OUT_DIR/runtimes/win-x64/native/onnxruntime.dll" ]; then
  cp "$OUT_DIR/runtimes/win-x64/native/onnxruntime.dll" "$ORT"
fi
if [ ! -f "$SHARED" ] && [ -f "$OUT_DIR/runtimes/win-x64/native/onnxruntime_providers_shared.dll" ]; then
  cp "$OUT_DIR/runtimes/win-x64/native/onnxruntime_providers_shared.dll" "$SHARED"
fi

if [ -f "$OUT_DIR/DirectML.dll" ]; then
  rm -f "$OUT_DIR/DirectML.dll"
  echo "  removed DirectML.dll (CPU ORT only)"
fi

if [ ! -f "$ORT" ]; then
  echo "ERROR: ensure-ort-native: onnxruntime.dll missing at $ORT" >&2
  echo "  Windows face soft-lock needs Microsoft.ML.OnnxRuntime natives in the publish output." >&2
  exit 1
fi
if [ ! -f "$SHARED" ]; then
  echo "ERROR: ensure-ort-native: onnxruntime_providers_shared.dll missing at $SHARED" >&2
  echo "  Both ORT natives must sit next to client.exe." >&2
  exit 1
fi

SIZE=$(wc -c < "$ORT" | tr -d ' ')
SSIZE=$(wc -c < "$SHARED" | tr -d ' ')
echo "  ✓ onnxruntime.dll present ($(numfmt --to=iec "$SIZE" 2>/dev/null || echo "${SIZE} bytes"))"
echo "  ✓ onnxruntime_providers_shared.dll present ($(numfmt --to=iec "$SSIZE" 2>/dev/null || echo "${SSIZE} bytes"))"
exit 0
