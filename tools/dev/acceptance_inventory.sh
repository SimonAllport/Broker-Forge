#!/usr/bin/env bash
# Inventory live acceptance against the dev environment (see acceptance_inventory.py). Exits non-zero on the first failure.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PY="${PYTHON:-$( [ -x /usr/bin/python3 ] && echo /usr/bin/python3 || echo python3 )}"
"$PY" "$ROOT/tools/dev/acceptance_inventory.py"
