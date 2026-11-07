#!/usr/bin/env bash
# Phase 0: Authenticode-sign the strong-named plug-in on macOS/Linux with osslsigncode
# (Microsoft documents signtool, which is Windows-only). Signing material stays outside the repo.
set -euo pipefail

KEYS="${BROKERFORGE_SIGNING_DIR:-$HOME/.brokerforge/spike-signing}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
DLL="$ROOT/src/BrokerForge.Plugin/bin/Release/net462/BrokerForge.Plugin.dll"
OUT="$ROOT/out/BrokerForge.Plugin.dll"

dotnet build "$ROOT/src/BrokerForge.Plugin" -c Release --nologo -v q
mkdir -p "$ROOT/out"

# PEM certificate and key, not the PFX: reading the PFX failed intermittently and then fell back to a
# passphrase prompt, which has no terminal under the dev scripts.
osslsigncode sign \
  -certs "$KEYS/cert.pem" -key "$KEYS/cert.key" \
  -h sha256 -ts http://timestamp.digicert.com \
  -in "$DLL" -out "$OUT.tmp"
mv "$OUT.tmp" "$OUT"

osslsigncode verify -CAfile "$KEYS/cert.pem" -in "$OUT" | grep -E "Signature verification|Message digest"
echo "signed: $OUT"
