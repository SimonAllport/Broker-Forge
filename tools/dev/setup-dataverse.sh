#!/usr/bin/env bash
# Sign the plug-in and register the BrokerForge core components (see setup_dataverse.py). Source tables
# and Custom APIs come from tools/dev/generate.sh.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
# The timestamp server is occasionally unreachable; retry before giving up.
for attempt in 1 2 3; do
  "$ROOT/tools/spike/sign-plugin.sh" && break
  [ "$attempt" = 3 ] && exit 1
  sleep 5
done
# macOS: the python.org build ships without CA certificates; the system Python uses the keychain.
PY="${PYTHON:-$( [ -x /usr/bin/python3 ] && echo /usr/bin/python3 || echo python3 )}"
"$PY" "$ROOT/tools/dev/setup_dataverse.py" "$ROOT/out/BrokerForge.Plugin.dll"
