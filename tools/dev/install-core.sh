#!/usr/bin/env bash
# Install the BrokerForge core (out/BrokerForgeCore_managed.zip from tools/dev/export-core.sh) into another environment,
# the one-off install step of spec 11. Usage: tools/dev/install-core.sh <org url> <environment id> [<plug-in app id>]
# The plug-in app is this environment's own (tools/dev/setup-plugin-app.sh); without it, dev's is used.
set -euo pipefail
ORG_URL="${1:?org url, e.g. https://contoso-test.crm4.dynamics.com}"
ENV_ID="${2:?environment id}"
PLUGIN_APP_ID="${3:-}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PY="${PYTHON:-$( [ -x /usr/bin/python3 ] && echo /usr/bin/python3 || echo python3 )}"
PACKAGE="$ROOT/out/BrokerForgeCore_managed.zip"
[ -f "$PACKAGE" ] || { echo "run tools/dev/export-core.sh first" >&2; exit 1; }
DV_ORG="$ORG_URL" PLUGIN_APP="$PLUGIN_APP_ID" "$PY" "$ROOT/tools/dev/install_core.py" "$PACKAGE"
cat <<NEXT
Installed in $ORG_URL. Still to do outside Dataverse:
 1. The plug-in app's federated credential for environment $ENV_ID (tools/dev/setup-plugin-app.sh does both).
 2. The host's Trust:AllowedCallers entry for $ENV_ID: SECOND_ENVID and SECOND_ORGURL in ~/.brokerforge/dev-ids.env, then tools/dev/deploy.sh.
 3. Assign the BrokerForge administrator role to whoever sets up connections there.
NEXT
