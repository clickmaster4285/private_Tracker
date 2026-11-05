#!/usr/bin/env bash
# Encrypt .env to config.enc using AES-256-GCM with the hardcoded transport key.
# Usage: bash publish/encrypt-config.sh [input_path] [output_path]
#
# Run from the client/ directory.
# Default: encrypt client/.env → client/config.enc
#
# This is called automatically by build-installer.sh during the build process.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
CLIENT_DIR="$(dirname "$SCRIPT_DIR")"

INPUT="${1:-$CLIENT_DIR/.env}"
OUTPUT="${2:-$CLIENT_DIR/config.enc}"

if [ ! -f "$INPUT" ]; then
  echo "=============================================="
  echo " WARNING: .env not found at $INPUT"
  echo " Create a .env file with your configuration."
  echo " Skipping encrypted config generation."
  echo "=============================================="
  exit 0
fi

echo "[encrypt-config] Encrypting $(basename "$INPUT") → $(basename "$OUTPUT") (AES-256-GCM)..."

cd "$CLIENT_DIR"
dotnet run --project "$CLIENT_DIR" -- --encrypt-config "$INPUT" "$OUTPUT"

echo "[encrypt-config] Done: $OUTPUT ($(stat -c%s "$OUTPUT" 2>/dev/null || stat -f%z "$OUTPUT" 2>/dev/null || echo "?") bytes)"
