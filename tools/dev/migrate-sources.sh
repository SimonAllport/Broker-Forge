#!/usr/bin/env bash
# Move OrdersAPI, TicketsAPI and TicketsEntraAPI onto connection rows and generate them (see migrate_sources.py).
# Usage: tools/dev/migrate-sources.sh [--generate] <Instance>...   Exit 4: the change list needs a person (printed).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PY="${PYTHON:-$( [ -x /usr/bin/python3 ] && echo /usr/bin/python3 || echo python3 )}"
exec "$PY" "$ROOT/tools/dev/migrate_sources.py" "$@"
