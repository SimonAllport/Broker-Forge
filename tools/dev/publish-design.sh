#!/usr/bin/env bash
# Publish one service instance as a connection design in the dev environment (see publish_design.py).
# Stopgap until plan 3's generator does this.
# Usage: tools/dev/publish-design.sh <Instance> <Solution> [--root DIR] [--settings-from INSTANCE] [--secret-name NAME]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
# macOS: the python.org build ships without CA certificates; the system Python uses the keychain.
PY="${PYTHON:-$( [ -x /usr/bin/python3 ] && echo /usr/bin/python3 || echo python3 )}"
"$PY" "$ROOT/tools/dev/publish_design.py" "$@"
